using System;
using System.Collections.Generic;
using System.Linq;
using Brawler.Core;
using Brawler.Fx;
using Brawler.Logic;
using Brawler.Net;
using Godot;

namespace Brawler.Game;

public enum Phase { Idle, Countdown, Fighting, Intermission, CardPick, MatchOver }

public enum FxKind { Explosion, Block, Blocked, Blink, Zap, Revive, Shockwave, Bash, Counter }

/// <summary>
/// Match flow and the battle simulation. The host is authoritative: it runs every avatar, resolves hits,
/// and drives phase changes through RPCs. Clients predict their own movement, replay bullets locally,
/// and apply snapshots for everything else.
///
/// Flow: BeginMatch → StartBattle → Countdown → Fighting → Intermission → (BeginCardPick after a round) → StartBattle … → MatchOver
///
/// This file holds the state, the queries and the per-frame simulation. The rest is split by topic:
/// GameManager.Flow.cs (match flow, kill cam, card picks, rejoins), GameManager.Combat.cs (damage, kills, effects),
/// GameManager.Modes.cs (game modes), GameManager.Chat.cs (emotes, pings, ghosts) and GameManager.Sync.cs
/// (snapshots, client requests, wire formats).
/// </summary>
public partial class GameManager : Node2D
{
    public const float CountdownTime = 1.8f;
    public const float PickTimeout = 30f;
    /// <summary>How long the host holds a match that drops below two players, for whoever dropped to rejoin.</summary>
    public const float RejoinWait = 45f;

    public NetworkManager Net;
    public Sfx Sfx;
    public FxLayer Fx { get; private set; }
    public BulletSystem Bullets { get; private set; }
    public MapNode MapNode { get; private set; }
    public Arena Arena { get; private set; }
    public MapLayout Map => MapNode.Layout;
    public Rng HostRng { get; private set; } = new((ulong)DateTime.Now.Ticks);

    public readonly MatchPlayer[] Slots = new MatchPlayer[LobbyState.MaxPlayers];
    public MatchSettings Settings = new();
    /// <summary>Test runs (--lava=SECS): when 0 or more, overrides the lobby's sudden-death lava setting for matches this peer hosts.</summary>
    public int LavaOverride = -1;
    /// <summary>--rarechance=PCT: the rare-card chance for matches this run hosts (-1 = the lobby setting).</summary>
    public int RareOverride = -1;
    public Phase Phase { get; private set; } = Phase.Idle;
    public float CountdownLeft { get; private set; }
    public MatchPlayer LocalMatchPlayer { get; private set; }
    /// <summary>Local co-op guests: other people at this machine, each on their own controller (see Pads.Couch).</summary>
    public readonly List<MatchPlayer> LocalGuests = new();

    /// <summary>Someone playing at this machine: the keyboard player or a couch guest.</summary>
    public bool IsLocalHuman(MatchPlayer p) => p != null && (p == LocalMatchPlayer || LocalGuests.Contains(p));
    public bool IsLocalHuman(int slot) => slot >= 0 && slot < Slots.Length && IsLocalHuman(Slots[slot]);

    /// <summary>Everyone playing at this machine, keyboard player first.</summary>
    public IEnumerable<MatchPlayer> LocalHumans()
    {
        if (LocalMatchPlayer != null) yield return LocalMatchPlayer;
        foreach (var g in LocalGuests) yield return g;
    }
    public int BattleNumber { get; private set; }
    public int MatchWinner { get; private set; } = -1;

    /// <summary>When true the local player is driven by a bot (used by the automated tests).</summary>
    public bool AutopilotLocal;
    public bool DisableHitStop;
    /// <summary>--simulate: no kill cams (nobody is watching).</summary>
    public bool DisableKillCam;
    /// <summary>Replays the final kill of each battle (see <see cref="Game.KillCam"/>).</summary>
    public KillCam KillCam { get; private set; }
    /// <summary>Every peer: a kill cam replay began.</summary>
    public event Action KillCamStarted;

    /// <summary>Host-only card balance log (see StatsRecorder).</summary>
    public readonly StatsRecorder Stats = new();
    /// <summary>Host: the tallies behind the end-of-match awards.</summary>
    public readonly AwardBook Book = new();
    /// <summary>This match's awards, once it's over (every peer).</summary>
    public readonly List<Award> MatchAwards = new();
    private readonly float[] _airT = new float[LobbyState.MaxPlayers];

    public Dictionary<int, List<int>> PickOptions { get; private set; } = new();
    /// <summary>The cursed deal each slot was offered this pick, if any: a rare card that comes with a curse.</summary>
    public Dictionary<int, (int Rare, int Curse)> PickDeals { get; private set; } = new();
    public readonly HashSet<int> Picked = new();

    public event Action MatchBegan, BattleStarted, ScoresChanged, CardPickBegan, ReturnedToLobby, RematchVotesChanged;
    public event Action<int> MatchEnded;
    public event Action<int, int> CardWasPicked;
    /// <summary>A cursed deal was taken: this slot also got this curse card (every peer, right after <see cref="CardWasPicked"/>).</summary>
    public event Action<int, int> CurseTaken;
    /// <summary>A pick unlocked a card combo for this slot (on every peer, right after <see cref="CardWasPicked"/>).</summary>
    public event Action<int, ComboDef> ComboFound;
    /// <summary>Host: a hazard hurt someone (slot, what, damage asked for), for the hazard check.</summary>
    public event Action<int, HazardHit, float> HazardDamaged;
    public event Action<string, Color, float> Banner;
    public void ShowBanner(string text, Color color, float seconds) => Banner?.Invoke(text, color, seconds);

    private Node2D _avatars;
    private Camera2D _cam;
    private int _bulletIds;
    private int _pendingWinner = -1;
    private float _endCheckT = -1;
    private Action _hostNext;
    private float _hostNextT;
    private float _pickTimeoutT;
    private readonly HashSet<int> _hostPicked = new();
    private readonly Dictionary<int, float> _botPickT = new();
    private float _trauma;
    private ulong _hitStopUntil;
    private int _lastCountdownTick;
    private float _clock;        // simulated seconds, for "recently hit" checks
    private float _zoomPunch;    // brief camera zoom on big kills
    private float _rejoinWaitT;  // host: seconds left holding a short-handed match (0 = not waiting)
    private int _rejoinWaitShown;

    public override void _Ready()
    {
        MapNode = new MapNode { Name = "Map" };
        Bullets = new BulletSystem { Name = "Bullets", Game = this };
        _avatars = new Node2D { Name = "Avatars" };
        Fx = new FxLayer { Name = "Fx" };
        KillCam = new KillCam(this);
        _cam = new Camera2D { Name = "Camera", Position = new Vector2(MapLayout.Width, MapLayout.Height) / 2 };
        AddChild(MapNode);
        Arena = new Arena { Name = "Arena", Game = this };
        Arena.Material = PaintShaders.Grain;
        AddChild(Arena);
        AddChild(new ObjectiveNode { Name = "Objectives", Game = this });
        AddChild(_avatars);
        AddChild(Bullets);
        AddChild(new LavaNode { Name = "Lava", Game = this });
        AddChild(Fx);
        AddChild(new SpawnMarker { Name = "SpawnMarker", Game = this });
        AddChild(_cam);
        _cam.MakeCurrent();
        Fx.ScreenRect = new Rect2(-40, -40, MapLayout.Width + 80, MapLayout.Height + 80);
        MapNode.Avatars = LiveAvatars;
        MapNode.PadFired += pos =>
        {
            if (KillCam.Playing) return; // its burst was recorded and replays with the rest
            Fx.Emit(pos, PKind.Streak, MapNode.Theme.Edge, 10, 220, 0.35f, 1.1f, 0, Vector2.Up, 0.9f);
            Fx.RingFx(pos, 4, 26, MapNode.Theme.Edge, 0.3f, 2);
            Sfx.Play("pad", 0.7f);
        };
        Visible = false;
        Net.PeerLeft += OnPeerLeft;
        Net.TryRejoin = HostTryRejoin;
    }

    // =====================================================================
    // Queries
    // =====================================================================

    public IEnumerable<MatchPlayer> Active() => Slots.Where(p => p != null && !p.Left);

    public IEnumerable<Avatar> LiveAvatars()
    {
        foreach (var p in Slots)
            if (p?.Avatar != null && p.Avatar.Alive && !p.Left) yield return p.Avatar;
    }

    public int ColorOf(int slot) => slot >= 0 && slot < Slots.Length && Slots[slot] != null ? Slots[slot].Info.ColorIdx : 0;

    public bool IsBotPlayer(MatchPlayer p) => p.Info.IsBot || (IsLocalHuman(p) && AutopilotLocal);

    /// <summary>The live avatar in a slot, or null.</summary>
    public Avatar AvatarOf(int slot) =>
        slot >= 0 && slot < Slots.Length && Slots[slot]?.Avatar is { Alive: true } a && !Slots[slot].Left ? a : null;

    public string NameOf(int slot) => slot >= 0 && slot < Slots.Length && Slots[slot] != null ? Slots[slot].Info.Name : "?";

    // ---------- teams (2v2, 3v3, 4v4) ----------

    public bool TeamMode => Settings.TeamSize > 0;

    /// <summary>The slot's team in a team mode, else -1.</summary>
    public int TeamOf(int slot) => TeamMode && slot >= 0 && slot < Slots.Length && Slots[slot] != null ? Slots[slot].Info.Team : -1;

    /// <summary>Two different players on the same team.</summary>
    public bool Allies(int a, int b) => a != b && TeamOf(a) >= 0 && TeamOf(a) == TeamOf(b);

    /// <summary>The attacker can't hurt the victim: teammates, with friendly fire off.</summary>
    public bool Spared(int attacker, int victim) => !Settings.FriendlyFire && Allies(attacker, victim);

    /// <summary>Who a win belongs to on screen: the slot's player, or their whole team.</summary>
    public string WinnerName(int slot) => TeamOf(slot) is >= 0 and var t ? $"{Teams.Names[t].ToUpperInvariant()} TEAM" : NameOf(slot);

    public Color WinnerColor(int slot) =>
        slot < 0 ? Palette.TextDim : TeamOf(slot) is >= 0 and var t ? Palette.Team(t) : Palette.Player(ColorOf(slot));

    /// <summary>The slot and, in team modes, every teammate (left players included, so their scores stay in step).</summary>
    public IEnumerable<MatchPlayer> SideOf(int slot) =>
        Slots.Where(p => p != null && (p.Slot == slot || Allies(p.Slot, slot)));

    public bool IsHost => Net.IsHost;
    private bool Networked => Net.Mode is NetMode.Host or NetMode.Client;

    // =====================================================================
    // Simulation
    // =====================================================================

    public override void _PhysicsProcess(double delta)
    {
        float dt = (float)delta;
        _clock += dt;
        KillCam.Stamp(_clock);
        TickChatGuards(dt);
        if (IsHost) Stats.Tick(dt, Phase);
        if (IsHost && _rejoinWaitT > 0) HostRejoinWaitTick(dt);
        if (IsHost && _hostNext != null && _rejoinWaitT <= 0) // the match flow holds while waiting for a rejoin
        {
            _hostNextT -= dt;
            if (_hostNextT <= 0)
            {
                var a = _hostNext;
                _hostNext = null;
                a();
            }
        }

        if (IsHost && _killCamWait && !KillCam.Playing)
        {
            _killCamWait = false;
            HostAfter(0.3f, HostResolveBattle);
        }
        if (KillCam.Playing) return; // nothing moves while the replay is on

        if (Phase == Phase.CardPick)
        {
            if (IsHost) HostPickTick(dt);
            else ClientAutopilotPick(dt);
            return;
        }
        if (Phase is not (Phase.Countdown or Phase.Fighting or Phase.Intermission)) return;
        if (Map == null) return;

        if (Phase == Phase.Countdown)
        {
            CountdownLeft -= dt;
            int tick = Mathf.CeilToInt(CountdownLeft / (CountdownTime / 3f));
            if (tick != _lastCountdownTick && tick > 0)
            {
                _lastCountdownTick = tick;
                Banner?.Invoke(tick.ToString(), Palette.Text, 0.5f);
                Sfx.Play("tick");
            }
            if (CountdownLeft <= 0)
            {
                Phase = Phase.Fighting;
                foreach (var a in LiveAvatars()) a.Frozen = false;
                if (IsHost) Stats.FightBegan();
                Banner?.Invoke(FightCall(), Palette.Accent, Mode == GameMode.Elimination ? 0.7f : 1.1f);
                Sfx.Play("go");
            }
        }

        TickRespawnTimers();
        Arena.Advance(dt); // movers first, so riders are carried this tick
        if (IsHost) HostSimulate(dt);
        else ClientSimulate(dt);
        if (IsHost) Arena.HostStep(dt, Phase == Phase.Fighting);
        else Arena.ClientSmooth();
        Arena.RebuildSolids();
        Bullets.Step(dt, IsHost, Arena.Solids);
        KillCam.Capture(_clock);

        if (IsHost)
        {
            if (Phase == Phase.Fighting)
            {
                HostObjectivesTick(dt);
                HostCheckBattleEnd(dt);
            }
            if (Net.Mode == NetMode.Host) Rpc(MethodName.Snapshot, SnapshotBytes());
        }
    }

    private void HostSimulate(float dt)
    {
        foreach (var p in Active())
        {
            var a = p.Avatar;
            if (a == null) continue;
            if (!a.Alive)
            {
                // A ghost follows its player's cursor; a bot's haunts whoever is nearest.
                var target = IsBotPlayer(p) ? BotGhostTarget(a) : p.Input?.Next(a, dt).Aim ?? a.GlobalPosition;
                a.GhostStep(target, dt);
                continue;
            }
            var f = p.Input?.Next(a, dt) ?? new InputFrame { Aim = a.GlobalPosition + Vector2.Right };
            a.Step(f, dt, authority: true);
            // Award tally: the longest stretch off the floor and walls.
            _airT[p.Slot] = a.IsOnFloor() || a.IsOnWallOnly() || Phase != Phase.Fighting ? 0 : _airT[p.Slot] + dt;
            Book.Max(p.Slot, AwardStat.Airtime, _airT[p.Slot]);
            if (a.GlobalPosition.Y > MapLayout.KillY)
            {
                // Knocked into a pit: the last hitter gets the (cosmetic) credit.
                bool recent = a.LastHitBy >= 0 && a.LastHitBy != p.Slot && _clock - a.LastHitAt < 4f;
                HostKill(a, KillCause.Fall, recent ? a.LastHitBy : -1);
            }
        }
    }

    private void ClientSimulate(float dt)
    {
        foreach (var p in Active())
        {
            var a = p.Avatar;
            if (a == null) continue;
            if (IsLocalHuman(p) && p.Input != null)
            {
                // Dead, the input still goes to the host: its Aim steers our ghost there too.
                var f = a.Alive || !AutopilotLocal ? p.Input.Next(a, dt) : new InputFrame { Aim = BotGhostTarget(a) };
                RpcId(1, MethodName.SubmitInput, p.Slot, f.ToBytes());
                if (a.Alive) a.Step(f, dt, authority: false);
                else a.GhostStep(f.Aim, dt);
            }
            else a.SmoothRemote(dt);
        }
    }

    private void HostCheckBattleEnd(float dt)
    {
        var alive = LiveAvatars().ToList();
        // An objective won it (the hill, a flag, the head). Otherwise, while the fallen still respawn, nothing else ends it.
        bool over = _objectiveWinner >= 0 || (!RespawnsNow && (TeamMode ? Teams.Settled(alive.Select(a => a.Player.Info.Team), out _) : alive.Count <= 1));
        if (!over)
        {
            _endCheckT = -1;
            return;
        }
        // Short grace period so simultaneous deaths (e.g. explosions) resolve as a draw fairly.
        if (_endCheckT < 0) _endCheckT = 0.35f;
        _endCheckT -= dt;
        if (_endCheckT > 0) return;
        _endCheckT = -1;
        // In team modes a survivor stands for their team (see SideOf).
        _pendingWinner = _objectiveWinner >= 0 ? _objectiveWinner : alive.Count == 1 || (TeamMode && alive.Count > 0) ? alive[0].Player.Slot : -1;
        HostBotChat(_pendingWinner, 0.45f, 0, 1);
        Phase = Phase.Intermission;
        HostAfter(0.8f, HostKillCamOrResolve);
    }

    public string ScoreLine() => string.Join(", ", Active().Select(p => $"{p.Info.Name}{(TeamMode ? $" [{Teams.Names[p.Info.Team]}]" : "")} R{p.RoundWins}/B{p.BattleWins}"));

    // =====================================================================
    // World reset & juice
    // =====================================================================

    public void ResetWorld()
    {
        // Leaving mid-match: keep whatever finished battles there were, flagged as incomplete.
        Stats.MatchEnd(-1, false, Slots.Where(p => p != null));
        foreach (var c in _avatars.GetChildren()) c.QueueFree();
        KillCam.End();
        _killCamWait = false;
        Bullets.Clear();
        Fx.Clear();
        MapNode.Build(null);
        Arena.Build(null);
        Array.Clear(Slots);
        LocalMatchPlayer = null;
        LocalGuests.Clear();
        PickOptions.Clear();
        PickDeals.Clear();
        Picked.Clear();
        RematchVotes.Clear();
        MatchAwards.Clear();
        Phase = Phase.Idle;
        _hostNext = null;
        _rejoinWaitT = 0;
        ClearObjectives();
        Visible = false;
        Engine.TimeScale = 1;
    }

    public override void _Notification(int what)
    {
        if (what == NotificationWMCloseRequest) Stats.MatchEnd(-1, false, Slots.Where(p => p != null));
    }

    /// <summary>A, LB or the right trigger on any controller at this machine.</summary>
    private static bool PadSkipPressed()
    {
        foreach (int d in Pads.Devices)
            if (Pads.JustPressedAny(d, PadLayout.Jump) || Pads.TriggerJustPressed(d, right: true)) return true;
        return false;
    }

    public void Shake(float amount)
    {
        _trauma = Mathf.Min(1, _trauma + amount);
        KillCam.NoteShake(_clock, amount);
    }

    private void HitStop(int ms)
    {
        if (DisableHitStop || KillCam.Playing) return;
        Engine.TimeScale = 0.05;
        _hitStopUntil = Time.GetTicksMsec() + (ulong)ms;
    }

    public override void _Process(double delta)
    {
        ReadChatKeys();
        if (KillCam.Playing && !AutopilotLocal && (Input.IsActionJustPressed("shoot") || Input.IsActionJustPressed("jump") || PadSkipPressed())) SkipKillCam();
        if (KillCam.Playing) KillCam.Advance((float)delta);
        if (_hitStopUntil != 0 && Time.GetTicksMsec() >= _hitStopUntil && !KillCam.Playing)
        {
            Engine.TimeScale = 1;
            _hitStopUntil = 0;
        }
        _trauma = Mathf.Max(0, _trauma - (float)delta * 2.2f);
        float s = _trauma * _trauma * 6f * Accessibility.Shake;
        _cam.Offset = new Vector2((float)GD.RandRange(-s, s), (float)GD.RandRange(-s, s));
        _zoomPunch = Mathf.Max(0, _zoomPunch - (float)delta * 3f);
        if (_editorView is var (corner, zoom))
        {
            var size = GetViewport().GetVisibleRect().Size;
            _cam.Offset = Vector2.Zero;
            _cam.Position = (size / 2 - corner) / zoom;
            _cam.Zoom = Vector2.One * zoom;
            return;
        }
        FrameKillCam((float)delta);
        _cam.Zoom = Vector2.One * (_camZoom + 0.06f * _zoomPunch * _zoomPunch * Accessibility.Shake);
    }
}
