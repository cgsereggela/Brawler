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
    // Host: match flow
    // =====================================================================

    public void HostStartMatch()
    {
        if (!IsHost) return;
        if (Net.TestTeamSize > 0 && Net.Lobby.Settings.TeamSize != Net.TestTeamSize)
        {
            Net.Lobby.Settings.TeamSize = Net.TestTeamSize; // --teams=N: deal the test lobby onto teams
            Net.Lobby.DealTeams();
        }
        if (Net.TestFriendlyFire) Net.Lobby.Settings.FriendlyFire = true;
        if (Net.TestRounds > 0) Net.Lobby.Settings.RoundsToWin = Net.TestRounds; // --rounds=N: a shorter test match
        if (Net.TestArena != null) Net.Lobby.Settings.Maps = new() { Net.TestArena }; // --arena=CODE
        if (Net.TestMode is { } mode)
        {
            Net.Lobby.Settings.Mode = mode; // --mode=ID
            if (GameModes.Problem(mode, Net.Lobby.Settings.TeamSize) != null)
            {
                Net.Lobby.Settings.TeamSize = Math.Clamp((Net.Lobby.Players.Count + 1) / 2, 2, 4); // Capture the Flag without --teams: even teams
                Net.Lobby.DealTeams();
            }
        }
        if (Net.Lobby.StartProblem() is { } problem)
        {
            Log.Info($"can't start: {problem}");
            return;
        }
        Net.InMatch = true;
        if (Stats.Mode != "sim") Stats.Mode = Net.Mode == NetMode.Host ? "host" : "offline";
        if (LavaOverride >= 0) Net.Lobby.Settings.LavaAfter = LavaOverride;
        if (RareOverride >= 0) Net.Lobby.Settings.RareChance = RareOverride;
        Rpc(MethodName.BeginMatch, Net.Lobby.Serialize());
        _arenaTurn = 0;
        HostStartBattle();
    }

    private void HostStartBattle()
    {
        uint seed = HostRng.NextUInt();
        // Custom arenas take turns: the seed says whose turn it is (see LayoutFor), so the battle RPC stays the same.
        uint n = (uint)Settings.Maps.Count;
        if (n > 0)
        {
            seed = seed / n * n;
            if (seed > uint.MaxValue - n) seed -= n;
            seed += (uint)(_arenaTurn++ % n);
        }
        Rpc(MethodName.StartBattle, (int)seed, ScoresBytes());
    }

    private int _arenaTurn; // host: battles started this match, for the custom arenas' turns

    /// <summary>
    /// A battle's arena on every peer: with custom arenas in the lobby, the one whose turn the seed carries (a code that
    /// doesn't read falls back to a generated arena); otherwise the seed's generated arena.
    /// </summary>
    public MapLayout LayoutFor(int seed, int players, out string name)
    {
        var maps = Settings.Maps;
        name = null;
        if (maps.Count > 0 && MapCode.Unpack(maps[(int)((uint)seed % (uint)maps.Count)]) is { } design)
        {
            name = design.Name != "" ? design.Name : null;
            return design.Build(players);
        }
        return MapGenerator.Generate(seed, players);
    }

    /// <summary>What the HUD calls this battle's arena: a custom arena's name, else its theme.</summary>
    public string ArenaName { get; private set; } = "";

    /// <summary>
    /// The level editor's framing: the camera fixed on the whole arena drawn at <c>Zoom</c> with its top-left corner at
    /// <c>Corner</c> on screen (base viewport pixels), or null for the match camera.
    /// </summary>
    public (Vector2 Corner, float Zoom)? EditorView
    {
        get => _editorView;
        set
        {
            _editorView = value;
            Visible = value != null || Phase != Phase.Idle;
        }
    }
    private (Vector2 Corner, float Zoom)? _editorView;

    private void HostResolveBattle()
    {
        int winner = _pendingWinner;
        bool roundWon = false, matchWon = false;
        if (winner >= 0 && Slots[winner] != null)
        {
            var w = Slots[winner];
            var side = SideOf(winner).ToList(); // in team modes the whole team scores together
            foreach (var p in side) p.BattleWins++;
            if (w.BattleWins >= Settings.BattlesPerRound)
            {
                roundWon = true;
                foreach (var p in side) p.RoundWins++;
                foreach (var p in Slots) if (p != null) p.BattleWins = 0;
                if (w.RoundWins >= Settings.RoundsToWin) matchWon = true;
            }
        }
        Stats.BattleEnd(winner, roundWon, Active());
        Log.Info($"arena: {Arena.Summary}");
        if (matchWon) HostSendAwards(); // ahead of BattleOver, so the match-over screen has them
        Rpc(MethodName.BattleOver, winner, ScoresBytes(), roundWon, matchWon);
        if (matchWon)
        {
            Stats.MatchEnd(winner, true, Slots.Where(p => p != null));
            return;
        }
        if (roundWon) HostAfter(1.6f, () => HostBeginCardPick(winner));
        else HostAfter(1.4f, HostStartBattle);
    }

    private void HostBeginCardPick(int roundWinner)
    {
        var w = new Wire();
        var entries = new List<PickEntry>();
        foreach (var p in Active())
        {
            bool won = p.Slot == roundWinner || Allies(p.Slot, roundWinner);
            int count = won ? Settings.WinnerChoices : Settings.LoserChoices;
            var ids = count > 0 ? CardRegistry.RollOptions(HostRng, count, Settings.DisabledCards, Settings.RareChance / 100f) : null;
            if (ids is not { Count: > 0 }) continue;
            var deal = Settings.CursedDeals ? CardRegistry.RollDeal(HostRng, Settings.DisabledCards, ids, Settings.RareChance / 100f) : null;
            entries.Add(new PickEntry(p.Slot, ids, deal));
        }
        if (entries.Count == 0)
        {
            HostStartBattle();
            return;
        }
        WritePicks(w, entries);
        _hostPicked.Clear();
        _botPickT.Clear();
        _pickTimeoutT = PickTimeout;
        int completedRounds = Slots.Where(p => p != null).Sum(p => p.RoundWins);
        foreach (var (slot, ids, deal) in entries)
        {
            bool bot = IsBotPlayer(Slots[slot]);
            if (bot) _botPickT[slot] = HostRng.Range(0.5f, 1.5f);
            Stats.PickOffered(completedRounds, Slots[slot], bot, slot == roundWinner || Allies(slot, roundWinner), ids, deal);
        }
        Rpc(MethodName.BeginCardPick, w.Done());
    }

    /// <param name="deal">Take the slot's cursed deal: <paramref name="cardId"/> must be its rare card, and its curse comes too.</param>
    private void HostApplyPick(int slot, int cardId, bool deal = false, bool timedOut = false)
    {
        if (Phase != Phase.CardPick || _hostPicked.Contains(slot)) return;
        int curse = -1;
        if (deal)
        {
            if (!PickDeals.TryGetValue(slot, out var d) || d.Rare != cardId) return;
            curse = d.Curse;
        }
        else if (!PickOptions.TryGetValue(slot, out var opts) || !opts.Contains(cardId)) return;
        _hostPicked.Add(slot);
        Stats.Picked(slot, cardId, timedOut, curse);
        Rpc(MethodName.CardPicked, slot, cardId, curse);
        HostCheckPicksDone();
    }

    private bool _lastPickCombo;

    private void HostCheckPicksDone()
    {
        bool done = PickOptions.Keys.All(s => _hostPicked.Contains(s) || Slots[s] == null || Slots[s].Left);
        // A little longer when the last pick found a combo, so its popup can be read.
        if (done && _hostNext == null) HostAfter(_lastPickCombo ? 1.8f : 0.9f, HostStartBattle);
    }

    public void HostReturnToLobby()
    {
        if (!IsHost) return;
        Rpc(MethodName.EndMatch);
        Net.BroadcastLobby();
    }

    // ---------- rematch vote (match-over screen) ----------

    /// <summary>Slots that voted for a rematch. Bots don't vote; the rematch starts once every human still here has.</summary>
    public readonly HashSet<int> RematchVotes = new();

    /// <summary>The humans whose vote a rematch waits on.</summary>
    public IEnumerable<MatchPlayer> RematchVoters() => Active().Where(p => !p.Info.IsBot);

    public bool CanRematch => Phase == Phase.MatchOver && Active().Count() >= 2 && Net.Lobby.StartProblem() == null;

    public void LocalVoteRematch(bool yes)
    {
        if (LocalMatchPlayer == null || Phase != Phase.MatchOver) return;
        // Couch guests vote with the keyboard player: they share the screen and its buttons.
        foreach (var p in LocalHumans().ToList())
            if (IsHost) HostVoteRematch(p.Slot, yes);
            else RpcId(1, MethodName.RequestRematch, p.Slot, yes);
    }

    private void HostVoteRematch(int slot, bool yes)
    {
        if (Phase != Phase.MatchOver || _hostNext != null) return;
        if (yes) RematchVotes.Add(slot);
        else RematchVotes.Remove(slot);
        HostSyncRematch();
    }

    /// <summary>Host: share the votes, and start the rematch once everyone still here is in.</summary>
    private void HostSyncRematch()
    {
        RematchVotes.RemoveWhere(s => Slots[s] == null || Slots[s].Left);
        var w = new Wire().U8(RematchVotes.Count);
        foreach (var s in RematchVotes) w.U8(s);
        Rpc(MethodName.SyncRematchVotes, w.Done());
        if (CanRematch && RematchVoters().All(p => RematchVotes.Contains(p.Slot)))
        {
            Log.Info("rematch: everyone is in");
            HostAfter(1.0f, HostStartMatch); // a beat to see the last vote land
        }
    }

    private void HostAfter(float seconds, Action a)
    {
        _hostNext = a;
        _hostNextT = seconds;
    }

    private void OnPeerLeft(long peer)
    {
        if (Phase == Phase.Idle) return;
        // Their couch guests go with them.
        var gone = Slots.Where(p => p != null && !p.Info.IsBot && !p.Left && p.Info.PeerId == peer).ToList();
        if (gone.Count == 0) return;
        if (KillCam.Playing) Rpc(MethodName.EndKillCam); // back to the real state before anything changes
        foreach (var mp in gone)
        {
            if (mp.Avatar != null && mp.Avatar.Alive) HostKill(mp.Avatar, KillCause.Left);
            Rpc(MethodName.PlayerLeft, mp.Slot);
        }
        if (Phase == Phase.CardPick) HostCheckPicksDone();
        if (Phase == Phase.MatchOver) HostSyncRematch(); // their vote goes, and the rest may now all be in

        // Too few left to play on: hold the match (see HostRejoinWaitTick) so whoever dropped can rejoin.
        if (Active().Count() < 2 && Phase != Phase.MatchOver && _rejoinWaitT <= 0)
        {
            _rejoinWaitT = RejoinWait;
            _rejoinWaitShown = -1;
        }
    }

    /// <summary>
    /// Host: someone joined while the match is on. If they dropped out of it, they get their slot back with its cards and
    /// score: matched by their game's rejoin key, or by name when exactly one dropped player has it (they restarted the
    /// game). They sit out the rest of a battle already under way and play from the next one, or pick now if a card
    /// pick is on.
    /// </summary>
    private bool HostTryRejoin(long peer, string key, string name)
    {
        if (Phase is Phase.Idle or Phase.MatchOver) return false;
        var dropped = Slots.Where(p => p != null && p.Left && !p.Info.IsBot).ToList();
        var mp = dropped.FirstOrDefault(p => key != null && p.Info.Key == key && p.Info.Guest == 0);
        if (mp == null && dropped.Count(p => p.Info.Name == name && p.Info.Guest == 0) == 1) mp = dropped.First(p => p.Info.Name == name && p.Info.Guest == 0);
        if (mp == null) return false;

        // Their couch guests (who share the rejoin key) come back with them.
        var back = new List<MatchPlayer> { mp };
        if (key != null) back.AddRange(dropped.Where(p => p != mp && p.Info.Guest > 0 && p.Info.Key == key));
        foreach (var p in back)
        {
            p.Info.PeerId = peer;
            p.Info.Key = key;
            p.Left = false;
            p.Input = new RemoteInput();
        }
        Net.SendCards(peer);
        RpcId(peer, MethodName.Rejoined, RejoinBytes());
        foreach (var p in back)
        {
            Rpc(MethodName.PlayerBack, p.Slot, peer);
            // Back in the lobby list too, for after the match.
            if (Net.Lobby.BySlot(p.Slot) == null)
                Net.Lobby.Players.Add(new PlayerInfo { Slot = p.Slot, PeerId = peer, Guest = p.Info.Guest, Name = p.Info.Name, ColorIdx = p.Info.ColorIdx, Look = p.Info.Look.Clone(), Key = key });
        }
        Net.Lobby.Players.Sort((a, b) => a.Slot.CompareTo(b.Slot));
        Net.BroadcastLobby();
        Log.Info($"{mp.Info.Name} rejoined slot {mp.Slot} with {mp.Cards.Count} cards");
        return true;
    }

    /// <summary>Host, while a match is short-handed: count down, and end the match if nobody is back in time.</summary>
    private void HostRejoinWaitTick(float dt)
    {
        if (Active().Count() >= 2 || Phase == Phase.MatchOver)
        {
            _rejoinWaitT = 0;
            return;
        }
        _rejoinWaitT -= dt;
        if (_rejoinWaitT <= 0)
        {
            _rejoinWaitT = 0;
            _hostNext = null;
            var remaining = Active().ToList();
            int winner = remaining.Count == 1 ? remaining[0].Slot : -1;
            Stats.MatchEnd(winner, false, Slots.Where(p => p != null));
            HostSendAwards();
            Rpc(MethodName.BattleOver, winner, ScoresBytes(), true, true);
            return;
        }
        int secs = Mathf.CeilToInt(_rejoinWaitT);
        if (secs != _rejoinWaitShown)
        {
            _rejoinWaitShown = secs;
            Rpc(MethodName.WaitingForRejoin, secs);
        }
    }

    // =====================================================================
    // Local actions (from UI)
    // =====================================================================

    /// <param name="deal">Take the cursed deal (its rare card is <paramref name="cardId"/>) instead of an ordinary card.</param>
    /// <param name="who">Which player at this machine is picking (default: the keyboard player).</param>
    public void LocalPick(int cardId, bool deal = false, MatchPlayer who = null)
    {
        who ??= LocalMatchPlayer;
        if (!IsLocalHuman(who)) return;
        if (IsHost) HostApplyPick(who.Slot, cardId, deal);
        else RpcId(1, MethodName.RequestPick, who.Slot, cardId, deal);
    }

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

    // ---------- kill cam ----------

    private int _lastVictim = -1, _lastKiller = -1;
    private bool _killCamWait; // host: a kill cam is on; the battle resolves once it ends
    private readonly HashSet<int> _killCamSkips = new();
    /// <summary>This peer asked to skip the kill cam on screen.</summary>
    public bool SkipAsked { get; private set; }
    /// <summary>How many have asked to skip the kill cam on screen (it ends once every human has).</summary>
    public int KillCamSkipVotes { get; private set; }

    /// <summary>Host: replay the battle's final kill everywhere, then resolve the battle (straight away if there's none).</summary>
    private void HostKillCamOrResolve()
    {
        // Not in the respawn modes: the last kill there rarely decided anything.
        if (Settings.KillCam && !DisableKillCam && _lastVictim >= 0 && !GameModes.Respawns(Mode))
        {
            _killCamSkips.Clear();
            Rpc(MethodName.PlayKillCam, _lastVictim, _lastKiller);
            if (KillCam.Playing)
            {
                _killCamWait = true;
                return;
            }
        }
        HostResolveBattle();
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void PlayKillCam(int victim, int killer)
    {
        SkipAsked = false;
        KillCamSkipVotes = 0;
        if (!KillCam.Start(victim, killer)) return;
        Log.Info($"kill cam: {NameOf(victim)}{(killer >= 0 ? $" by {NameOf(killer)}" : "")}");
        KillCamStarted?.Invoke();
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void EndKillCam()
    {
        if (KillCam.Playing) Log.Info($"kill cam ended early at {KillCam.Progress:P0}");
        KillCam.Stop();
    }

    /// <summary>The local player wants to skip the kill cam.</summary>
    public void SkipKillCam()
    {
        if (!KillCam.Playing || SkipAsked || LocalMatchPlayer == null) return;
        SkipAsked = true;
        // One skip speaks for everyone at this machine.
        if (IsHost) foreach (var p in LocalHumans().ToList()) HostSkipKillCam(p.Slot);
        else RpcId(1, MethodName.RequestSkipKillCam);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestSkipKillCam()
    {
        if (!IsHost) return;
        long sender = Multiplayer.GetRemoteSenderId();
        foreach (var p in Slots.Where(s => s != null && !s.Info.IsBot && !s.Left && s.Info.PeerId == sender).ToList())
            HostSkipKillCam(p.Slot);
    }

    private void HostSkipKillCam(int slot)
    {
        if (!_killCamWait) return;
        _killCamSkips.Add(slot);
        Rpc(MethodName.KillCamSkipped, _killCamSkips.Count);
        HostCheckKillCamSkips();
    }

    private void HostCheckKillCamSkips()
    {
        var humans = Active().Where(p => !p.Info.IsBot).ToList();
        if (KillCam.Playing && humans.Count > 0 && humans.All(p => _killCamSkips.Contains(p.Slot))) Rpc(MethodName.EndKillCam);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void KillCamSkipped(int votes) => KillCamSkipVotes = votes;

    /// <summary>Humans who could skip the kill cam (the HUD shows votes out of this).</summary>
    public int KillCamVoters => Active().Count(p => !p.Info.IsBot);

    private float _camZoom = 1;
    private Vector2 _camFocus = new Vector2(MapLayout.Width, MapLayout.Height) / 2;

    /// <summary>The camera closes in on the kill while the kill cam plays and eases back out after.</summary>
    private void FrameKillCam(float dt)
    {
        var center = new Vector2(MapLayout.Width, MapLayout.Height) / 2;
        var focus = center;
        float zoom = 1;
        if (KillCam.Playing)
        {
            var (at, span) = KillCam.Framing();
            focus = at;
            zoom = Mathf.Clamp(Mathf.Min(MapLayout.Width / (span.X + 320), MapLayout.Height / (span.Y + 200)), 1.35f, 2f);
        }
        // Real time (the replay slows delta down), so the camera keeps up at any speed.
        float real = dt / Mathf.Max(0.05f, (float)Engine.TimeScale);
        float k = 1f - Mathf.Exp(-(KillCam.Playing ? 5f : 7f) * real);
        _camZoom = Mathf.Lerp(_camZoom, zoom, k);
        _camFocus = _camFocus.Lerp(focus, k);
        // Keep the view inside the arena.
        var half = new Vector2(MapLayout.Width, MapLayout.Height) / (2 * _camZoom);
        _cam.Position = new Vector2(Mathf.Clamp(_camFocus.X, half.X, MapLayout.Width - half.X), Mathf.Clamp(_camFocus.Y, half.Y, MapLayout.Height - half.Y));
    }

    private void HostPickTick(float dt)
    {
        _pickTimeoutT -= dt;
        foreach (var slot in _botPickT.Keys.ToList())
        {
            _botPickT[slot] -= dt;
            if (_botPickT[slot] <= 0)
            {
                _botPickT.Remove(slot);
                var (card, deal) = BotChooseCard(slot, PickOptions[slot]);
                HostApplyPick(slot, card, deal);
            }
        }
        if (_pickTimeoutT <= 0)
        {
            foreach (var (slot, opts) in PickOptions)
                if (!_hostPicked.Contains(slot)) HostApplyPick(slot, opts[HostRng.Range(0, opts.Count - 1)], timedOut: true);
            _pickTimeoutT = float.MaxValue;
        }
    }

    private float _autoPickT = -1;

    private void ClientAutopilotPick(float dt)
    {
        if (!AutopilotLocal) return;
        // Everyone at this machine in turn: the keyboard player, then the couch guests.
        var me = LocalHumans().FirstOrDefault(p => !Picked.Contains(p.Slot) && PickOptions.ContainsKey(p.Slot));
        if (me == null) return;
        var opts = PickOptions[me.Slot];
        if (_autoPickT < 0) _autoPickT = 0.8f;
        _autoPickT -= dt;
        if (_autoPickT > 0) return;
        _autoPickT = -1;
        // Like a bot: a combo card when there is one, mostly, so autotests see combos found.
        var combos = opts.Where(id => Combos.Unlocks(me.Cards, id).Count > 0).ToList();
        var from = combos.Count > 0 && GD.Randf() < 0.75f ? combos : opts;
        // Now and then the cursed deal, so autotests take curses too.
        if (combos.Count == 0 && PickDeals.TryGetValue(me.Slot, out var deal) && GD.Randf() < 0.3f) LocalPick(deal.Rare, deal: true, me);
        else LocalPick(from[(int)(GD.Randi() % (uint)from.Count)], who: me);
    }

    /// <summary>
    /// Bots usually take a card that completes a combo when one is offered; otherwise they sometimes gamble on the
    /// cursed deal, and take any card the rest of the time.
    /// </summary>
    private (int Card, bool Deal) BotChooseCard(int slot, List<int> opts)
    {
        var combos = opts.Where(id => Combos.Unlocks(Slots[slot].Cards, id).Count > 0).ToList();
        if (combos.Count > 0 && HostRng.Range(0f, 1f) < 0.75f) return (combos[HostRng.Range(0, combos.Count - 1)], false);
        if (PickDeals.TryGetValue(slot, out var deal) && HostRng.Range(0f, 1f) < BotDealChance) return (deal.Rare, true);
        return (opts[HostRng.Range(0, opts.Count - 1)], false);
    }

    /// <summary>How often a bot without a combo to chase takes the cursed deal.</summary>
    private const float BotDealChance = 0.3f;

    // =====================================================================
    // Host: combat resolution (called by Avatar / BulletSystem)
    // =====================================================================

    public void HostSpawnBullet(Bullet b, bool queued = false)
    {
        b.Id = ++_bulletIds;
        if (queued) Bullets.AddQueued(b);
        else Bullets.Add(b);
        if (Net.Mode == NetMode.Host) Rpc(MethodName.SpawnBullet, b.Serialize());
        if (!queued)
        {
            OnShotFx(b);
            Stats.Shot(b.Owner);
            Book.Add(b.Owner, AwardStat.Shots);
        }
    }

    public void HostBulletDestroyed(Bullet b, bool impact)
    {
        ImpactFx(b.Pos, b.Owner, impact);
        if (Net.Mode == NetMode.Host) Rpc(MethodName.BulletGone, b.Id, b.Pos, impact);
    }

    /// <param name="reflected">Thorns damage: never reflected again (so two Thorns players can't loop).</param>
    public void HostDamage(Avatar victim, float damage, Vector2 knockback, int attackerSlot, Vector2 at, bool reflected = false)
    {
        if (victim.Guarded) return; // just respawned
        if (Spared(attackerSlot, victim.Player.Slot)) return; // teammates, friendly fire off: blasts, bashes and arcs pass harmlessly
        float hpBefore = victim.Hp;
        damage *= 1f - victim.S[Stat.Armor];
        knockback *= victim.S[Stat.KnockbackTaken];
        if (!victim.TakeHit(damage, knockback))
        {
            if (victim.Alive) { Stats.HitBlocked(victim.Player.Slot); Book.Add(victim.Player.Slot, AwardStat.HitsBlocked); }
            HostBlocked(victim, at);
            HostBlockedHit(victim, damage, at, attackerSlot, reflected);
            return;
        }
        if (victim.Hp <= 0 && victim.TryRevive())
            HostFx(FxKind.Revive, victim.GlobalPosition, 0, victim.Player.Slot, victim.GlobalPosition);
        if (attackerSlot >= 0 && attackerSlot != victim.Player.Slot)
        {
            victim.LastHitBy = attackerSlot;
            victim.LastHitAt = _clock;
        }
        float dealt = Mathf.Clamp(damage, 0, Mathf.Max(0, hpBefore));
        Stats.Damage(attackerSlot, victim.Player.Slot, dealt);
        Book.Add(victim.Player.Slot, AwardStat.DamageTaken, dealt);
        if (attackerSlot >= 0 && attackerSlot != victim.Player.Slot)
        {
            Book.Add(attackerSlot, AwardStat.Hits);
            Book.Add(attackerSlot, AwardStat.DamageDealt, dealt);
        }
        var attacker =attackerSlot >= 0 ? Slots[attackerSlot]?.Avatar : null;
        if (attacker != null && attacker != victim && attacker.Alive)
        {
            float steal = attacker.S[Stat.Lifesteal];
            if (steal > 0)
            {
                float before = attacker.Hp;
                attacker.Heal(damage * steal);
                HostHealed(attacker.Player.Slot, attacker.Hp - before);
            }
            foreach (var e in attacker.Player.Effects) e.OnDealDamage(attacker, victim, damage, this);
        }
        Rpc(MethodName.Damaged, victim.Player.Slot, victim.Hp, knockback, at, damage, attackerSlot);
        if (victim.Hp <= 0) HostKill(victim, KillCause.Shot, attackerSlot);

        float thorns = victim.S[Stat.Thorns];
        if (!reflected && thorns > 0 && attacker != null && attacker != victim && attacker.Alive)
            HostDamage(attacker, damage * thorns, Vector2.Zero, victim.Player.Slot, attacker.GlobalPosition, reflected: true);
    }

    /// <summary>Chain Lightning: a hit arcs to the nearest other enemy.</summary>
    public void HostChain(Avatar victim, float damage, int ownerSlot)
    {
        var owner = ownerSlot >= 0 && ownerSlot < Slots.Length ? Slots[ownerSlot] : null;
        float chain = owner?.Stats[Stat.Chain] ?? 0;
        if (chain <= 0) return;
        Avatar next = null;
        float best = 150f;
        foreach (var a in LiveAvatars())
        {
            if (a == victim || a.Player.Slot == ownerSlot || Allies(a.Player.Slot, ownerSlot)) continue;
            float d = a.GlobalPosition.DistanceTo(victim.GlobalPosition);
            if (d < best) { best = d; next = a; }
        }
        if (next == null) return;
        HostFx(FxKind.Zap, victim.GlobalPosition, 0, ownerSlot, next.GlobalPosition);
        var kb = (next.GlobalPosition - victim.GlobalPosition).Normalized() * 60;
        HostDamage(next, damage * chain, kb, ownerSlot, next.GlobalPosition);
    }

    /// <summary>Warp Rounds: move the shooter to where the bullet landed, if there's room for them.</summary>
    public void HostWarp(int ownerSlot, Vector2 at, Vector2 bulletVel)
    {
        var a = AvatarOf(ownerSlot);
        if (a == null || a.S[Stat.Warp] <= 0) return;
        var back = -bulletVel.Normalized();
        if (back == Vector2.Zero) back = Vector2.Up;
        var hs = a.HalfSize + new Vector2(1, 1);
        for (float d = hs.Length(); d < 48; d += 4)
        {
            var p = at + back * d;
            if (p.X < hs.X || p.X > MapLayout.Width - hs.X || p.Y < hs.Y || p.Y > MapLayout.KillY - 20) continue;
            var box = new Rect2(p - hs, hs * 2);
            bool clear = true;
            foreach (var r in Map.Solids)
                if (box.Intersects(new Rect2(r.X, r.Y, r.W, r.H))) { clear = false; break; }
            foreach (var s in Map.Slopes)
                if (s.Overlaps(box.Position.X, box.Position.Y, box.End.X, box.End.Y)) clear = false;
            if (!clear) continue;
            var from = a.GlobalPosition;
            a.GlobalPosition = p;
            a.Velocity = Vector2.Zero;
            HostFx(FxKind.Blink, from, 0, ownerSlot, p);
            return;
        }
    }

    /// <summary>Leech Field: drain enemies near the avatar (called a few times a second).</summary>
    public void HostLeech(Avatar a, float amount)
    {
        float radius = 44 * a.S[Stat.Size] + 10;
        foreach (var e in LiveAvatars().ToList())
        {
            if (e == a || Allies(e.Player.Slot, a.Player.Slot) || e.GlobalPosition.DistanceTo(a.GlobalPosition) > radius) continue;
            float before = e.Hp;
            HostDamage(e, amount, Vector2.Zero, a.Player.Slot, e.GlobalPosition);
            float dealt = Mathf.Max(0, before - Mathf.Max(0, e.Hp));
            if (dealt > 0 && a.Alive)
            {
                float hp = a.Hp;
                a.Heal(dealt);
                HostHealed(a.Player.Slot, a.Hp - hp);
            }
        }
    }

    public enum KillCause { Shot, Fall, Left, Hazard }

    public enum HazardHit { Magma, Poison, Saw }

    /// <summary>Last hazard that hurt each slot (every peer), so a hazard kill gets the right call-out.</summary>
    private readonly HazardHit[] _lastHazard = new HazardHit[LobbyState.MaxPlayers];

    /// <summary>
    /// Host: damage from the arena itself (pools, saws). Armor and block still count. A kill is credited to whoever
    /// hit the victim in the last few seconds, as with falls.
    /// </summary>
    public void HostHazardDamage(Avatar victim, float damage, Vector2 knockback, Vector2 at, HazardHit kind)
    {
        if (!victim.Alive) return;
        if (victim.Guarded) return;
        float hpBefore = victim.Hp;
        damage *= 1f - victim.S[Stat.Armor];
        knockback *= victim.S[Stat.KnockbackTaken];
        if (!victim.TakeHit(damage, knockback))
        {
            if (kind == HazardHit.Saw)
            {
                HostBlocked(victim, at);
                HostBlockedHit(victim, damage, at, -1);
            }
            return;
        }
        if (victim.Hp <= 0 && victim.TryRevive())
            HostFx(FxKind.Revive, victim.GlobalPosition, 0, victim.Player.Slot, victim.GlobalPosition);
        Arena.HazardDamage += damage;
        float dealt = Mathf.Clamp(damage, 0, Mathf.Max(0, hpBefore));
        Stats.Damage(-1, victim.Player.Slot, dealt);
        Book.Add(victim.Player.Slot, AwardStat.DamageTaken, dealt);
        Rpc(MethodName.HazardHurt, victim.Player.Slot, victim.Hp, knockback, at, damage, (int)kind);
        if (victim.Hp > 0) return;
        int slot = victim.Player.Slot;
        bool recent = victim.LastHitBy >= 0 && victim.LastHitBy != slot && _clock - victim.LastHitAt < 4f;
        HostKill(victim, KillCause.Hazard, recent ? victim.LastHitBy : -1);
    }

    public void HostPropBroken(int index, Vector2 at, bool fell) => Rpc(MethodName.PropBroken, index, at, fell);

    /// <summary>Host: pick the end-of-match awards from everyone who played (even if they left) and share them.</summary>
    private void HostSendAwards()
    {
        var awards = Awards.Pick(Book, Slots.Where(p => p != null).Select(p => p.Slot), HostRng);
        var w = new Wire().U8(awards.Count);
        foreach (var a in awards) w.U8(a.Slot).U8(a.Id).F(a.Value);
        Rpc(MethodName.SyncAwards, w.Done());
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void SyncAwards(byte[] data)
    {
        var r = new WireReader(data);
        MatchAwards.Clear();
        int n = r.U8();
        for (int i = 0; i < n; i++) MatchAwards.Add(new Award(r.U8(), r.U8(), r.F()));
        Log.Info($"awards: {string.Join("; ", MatchAwards.Select(a => $"{a.Title} = {NameOf(a.Slot)} ({a.Blurb})"))}");
    }

    private void HostHealed(int slot, float amount)
    {
        Stats.Healed(slot, amount);
        Book.Add(slot, AwardStat.Healed, amount);
    }

    public void HostKill(Avatar a, KillCause cause = KillCause.Shot, int killer = -1)
    {
        if (!a.Alive) return;
        if (cause != KillCause.Left) Stats.Death(a.Player.Slot, fell: cause == KillCause.Fall);
        a.Alive = false;
        if (killer == a.Player.Slot) killer = -1;
        if (Allies(killer, a.Player.Slot)) Log.Info($"team kill: {NameOf(killer)} took out teammate {NameOf(a.Player.Slot)}");
        if (cause != KillCause.Left)
        {
            Book.Add(killer >= 0 ? killer : a.Player.Slot, killer >= 0 ? AwardStat.Kills : AwardStat.SelfKills);
            if (cause == KillCause.Fall) Book.Add(a.Player.Slot, AwardStat.Falls);
        }
        HostObjectiveKill(a, killer, cause);
        Rpc(MethodName.Died, a.Player.Slot, a.GlobalPosition, killer, (int)cause);
        _lastVictim = cause == KillCause.Left ? -1 : a.Player.Slot;
        _lastKiller = killer;
        if (cause != KillCause.Left)
        {
            HostBotChat(killer, 0.3f, 1, 3, 4);
            HostBotChat(a.Player.Slot, 0.3f, 2, 3, 4);
        }
        // Kamikaze: go out with a bang (not when leaving the game or falling into the void).
        float boom = a.S[Stat.Kamikaze];
        if (boom > 0 && cause is KillCause.Shot or KillCause.Hazard) HostExplode(a.GlobalPosition, boom, 70, a.Player.Slot, 480);
    }

    public void HostExplode(Vector2 at, float radius, float damage, int ownerSlot, float knockback)
    {
        HostFx(FxKind.Explosion, at, radius, ownerSlot, at);
        Arena.HostExplosion(at, radius, damage, ownerSlot, knockback);
        foreach (var a in LiveAvatars().ToList())
        {
            var r = a.Bounds;
            var closest = new Vector2(Mathf.Clamp(at.X, r.Position.X, r.End.X), Mathf.Clamp(at.Y, r.Position.Y, r.End.Y));
            float d = closest.DistanceTo(at);
            if (d > radius) continue;
            float falloff = 1f - 0.5f * d / radius;
            float dmg = damage * falloff * (a.Player.Slot == ownerSlot ? 0.5f : 1f);
            var dir = (a.GlobalPosition - at).Normalized();
            if (dir == Vector2.Zero) dir = Vector2.Up;
            HostDamage(a, dmg, dir * knockback * falloff, ownerSlot, closest);
        }
    }

    public void HostBlocked(Avatar a, Vector2 at) => HostFx(FxKind.Blocked, at, 0, a.Player.Slot, at);

    public void HostOnBlock(Avatar a)
    {
        Stats.BlockUsed(a.Player.Slot);
        Book.Add(a.Player.Slot, AwardStat.Blocks);
        HostFx(FxKind.Block, a.GlobalPosition, 0, a.Player.Slot, a.GlobalPosition);

        // Shockwave: starting a block erases enemy bullets around you.
        int slot = a.Player.Slot;
        var pos = a.GlobalPosition;
        float body = Mathf.Max(a.HalfSize.X, a.HalfSize.Y);
        float wave = a.S[Stat.Shockwave];
        if (wave > 0)
        {
            Bullets.HostErase(pos, body + wave, slot);
            HostFx(FxKind.Shockwave, pos, body + wave, slot, pos);
        }

        // Shield Bash: starting a block hits and shoves enemies touching your shield.
        float bash = a.S[Stat.ShieldBash];
        if (bash > 0)
        {
            float reach = body + BashReach;
            foreach (var e in LiveAvatars().ToList())
            {
                if (e == a) continue;
                var r = e.Bounds;
                var closest = new Vector2(Mathf.Clamp(pos.X, r.Position.X, r.End.X), Mathf.Clamp(pos.Y, r.Position.Y, r.End.Y));
                if (closest.DistanceTo(pos) > reach) continue;
                var dir = (e.GlobalPosition - pos).Normalized();
                if (dir == Vector2.Zero) dir = Vector2.Up;
                HostDamage(e, bash, dir * 330 + new Vector2(0, -90), slot, closest);
            }
            HostFx(FxKind.Bash, pos, reach, slot, pos);
        }
    }

    /// <summary>How far past your body Shield Bash reaches.</summary>
    public const float BashReach = 20f;
    /// <summary>How long Riposte's bonus damage lasts after a blocked hit.</summary>
    public const float CounterTime = 2f;

    /// <summary>Host: a hit (bullet, blast, saw) landed on an active block. Block cards react here.</summary>
    /// <param name="reflected">The hit was itself spike or thorns damage: no spikes back (two Spiked Shields can't loop).</param>
    public void HostBlockedHit(Avatar a, float damage, Vector2 at, int attackerSlot, bool reflected = false)
    {
        if (!a.Alive) return;
        var s = a.S;
        var attacker = attackerSlot >= 0 && attackerSlot < Slots.Length ? Slots[attackerSlot]?.Avatar : null;
        if (!reflected && s[Stat.BlockSpikes] > 0 && damage > 0 && attacker != null && attacker != a && attacker.Alive)
        {
            HostFx(FxKind.Zap, a.GlobalPosition, 0, a.Player.Slot, attacker.GlobalPosition);
            HostDamage(attacker, damage * s[Stat.BlockSpikes], Vector2.Zero, a.Player.Slot, attacker.GlobalPosition, reflected: true);
        }
        if (s[Stat.BlockHeal] > 0 && damage > 0)
        {
            float before = a.Hp;
            a.Heal(damage * s[Stat.BlockHeal]);
            HostHealed(a.Player.Slot, a.Hp - before);
        }
        if (s[Stat.BlockReload] > 0) a.RefillAmmo();
        if (s[Stat.BlockRefund] > 0) a.BlockCd = Mathf.Max(0, a.BlockCd - s[Stat.BlockCooldown] * s[Stat.BlockRefund]);
        if (s[Stat.Counter] > 0)
        {
            if (a.CounterT <= 0) HostFx(FxKind.Counter, a.GlobalPosition, 0, a.Player.Slot, a.GlobalPosition);
            a.CounterT = CounterTime;
        }
    }

    public void HostFx(FxKind kind, Vector2 pos, float value, int slot, Vector2 pos2)
        => Rpc(MethodName.FxEvent, (int)kind, pos, value, slot, pos2);

    public void OnJump(Avatar a, bool air = false)
    {
        var feet = a.GlobalPosition + new Vector2(0, a.HalfSize.Y);
        Fx.Emit(feet, PKind.Smoke, new Color(1, 1, 1, 0.5f), 4, 50, 0.35f, 2.2f, 0, Vector2.Up, 2.6f, 5);
        if (air) Fx.RingFx(feet, 2, 12 * a.S[Stat.Size], Palette.Player(a.Player.Info.ColorIdx).Lightened(0.4f), 0.25f, 1.5f);
        if (IsLocalHuman(a.Player)) Sfx.Play("jump", 0.6f);
    }

    private void OnShotFx(Bullet b)
    {
        var st = StyleOf(b.Owner);
        var col = Palette.Player(ColorOf(b.Owner));
        var dir = b.Vel.Normalized();
        Fx.Emit(b.Pos, PKind.Streak, col.Lightened(0.4f), 3 + st.Tier, 160, 0.16f, 0.8f, 0, dir, 0.9f);
        if (st.Tier >= 2) Fx.Flash(b.Pos, 8 + st.Tier * 2, col, 0.1f);
        if (st.Has(Flavor.Fire)) Fx.Emit(b.Pos, PKind.Ember, new Color("#ffa33b"), 2, 60, 0.4f, 1.2f, -40, dir, 1.2f);
        var gun = AvatarOf(b.Owner);
        Sfx.Play(gun != null ? Sfx.ShotFor(gun.S) : "shoot", 0.7f);
        if (IsLocalHuman(b.Owner)) Shake(0.12f);
        if (b.Child) return;
        var shooter = AvatarOf(b.Owner);
        if (shooter == null) return;
        shooter.OnFired();
        // Recoil Jet: pushed on the host and in the shooter's own client prediction.
        if (IsHost || IsLocalHuman(shooter.Player)) shooter.ApplyRecoil(dir);
    }

    // =====================================================================
    // Effects (every peer, cosmetic). They grow with the attacker's card count and take on card flavors.
    // =====================================================================

    public FxStyle StyleOf(int slot) => slot >= 0 && slot < Slots.Length && Slots[slot] != null ? Slots[slot].Fx : FxStyle.None;

    private Vector2 PosOf(int slot, Vector2 fallback) =>
        slot >= 0 && slot < Slots.Length && Slots[slot]?.Avatar is { } a ? a.GlobalPosition : fallback;

    /// <summary>A bullet hit a wall (or expired).</summary>
    private void ImpactFx(Vector2 at, int owner, bool impact)
    {
        var st = StyleOf(owner);
        var col = Palette.Player(ColorOf(owner));
        if (!impact)
        {
            Fx.Emit(at, PKind.Dot, col, 2, 40, 0.2f, 0.8f);
            return;
        }
        Fx.Spark(at, col.Lightened(0.3f), 4 + st.Tier * 2);
        Fx.Flash(at, 8 + st.Tier * 3, col, 0.12f);
        if (st.Tier >= 2) Fx.RingFx(at, 1, 8 + st.Tier * 3, col, 0.2f, 1.2f);
        if (st.Has(Flavor.Fire)) Fx.Emit(at, PKind.Ember, new Color("#ff8a2a"), 3 + st.Tier, 70, 0.6f, 1.2f, -60);
        if (st.Has(Flavor.Homing)) Fx.Emit(at, PKind.Star, col.Lightened(0.5f), 2, 50, 0.4f, 2.2f);
        if (st.Has(Flavor.Glass)) Fx.Emit(at, PKind.Shard, new Color("#c8f4ff"), 3, 90, 0.5f, 2f, 300);
    }

    private void HitFx(int victim, int attacker, Vector2 at, Vector2 knock, float damage)
    {
        var st = StyleOf(attacker);
        int tier = st.Tier;
        float I = st.Intensity;
        var vc = Palette.Player(ColorOf(victim));
        var ac = attacker >= 0 ? Palette.Player(ColorOf(attacker)) : Colors.White;
        var dir = knock.LengthSquared() > 1 ? knock.Normalized() : Vector2.Up;

        Fx.Emit(at, PKind.Streak, vc.Lightened(0.3f), Mathf.RoundToInt(7 * I), 200 * Mathf.Sqrt(I), 0.35f, 1.1f, 250, dir, 2.2f);
        Fx.Emit(at, PKind.Dot, Colors.White, 3 + tier * 2, 110, 0.3f, 0.9f);
        Fx.Emit(at, PKind.Chunk, vc, 3 + tier, 120, 0.6f, 2f, 500, dir, 2f);
        Fx.Flash(at, 12 + tier * 5, ac, 0.18f);
        Fx.RingFx(at, 2, 12 + tier * 5, ac, 0.25f, 1.6f);
        if (tier >= 2) Fx.RingFx(at, 2, 20 + tier * 6, Colors.White, 0.3f, 1f, 0.05f);
        if (tier >= 3) Fx.RaysFx(at, ac, 6, 22 + tier * 5, 0.25f);
        if (tier >= 4) Fx.ScreenFlash(ac with { A = 0.07f }, 0.08f);

        if (st.Has(Flavor.Fire))
        {
            Fx.Emit(at, PKind.Ember, new Color("#ff9a2e"), 5 + tier * 2, 120, 0.7f, 1.4f, -80);
            Fx.Emit(at, PKind.Smoke, new Color(0.15f, 0.1f, 0.1f, 0.8f), 2 + tier, 30, 0.8f, 3.5f, -20);
        }
        if (st.Has(Flavor.Homing)) Fx.Emit(at, PKind.Star, ac.Lightened(0.5f), 3 + tier, 90, 0.5f, 2.8f);
        if (st.Has(Flavor.Bouncy)) Fx.RingFx(at, 1, 10 + tier * 3, ac.Lightened(0.4f), 0.25f, 1f, 0.1f);
        if (st.Has(Flavor.Pierce)) Fx.Emit(at, PKind.Streak, Colors.White, 4, 520, 0.2f, 1.4f, 0, dir, 0.12f, 1f);
        if (st.Has(Flavor.Split)) Fx.Emit(at, PKind.Shard, ac, 4 + tier, 160, 0.6f, 2.4f, 350);
        if (st.Has(Flavor.Glass)) Fx.Emit(at, PKind.Shard, new Color("#d6f6ff"), 5 + tier, 180, 0.6f, 2.4f, 400);
        if (st.Has(Flavor.Feather)) Fx.Emit(at, PKind.Confetti, new Color(1, 1, 1, 0.9f), 3, 70, 1.2f, 3f, 40, null, Mathf.Tau, 3.5f);
        if (st.Has(Flavor.Vampire) && attacker >= 0)
            Fx.EmitHoming(at, PosOf(attacker, at), new Color("#ff2a4a"), 4 + tier * 2, 120, 0.9f, 1.1f);

        float size = 11 + Mathf.Clamp(damage / 5f, 0, 14) + tier;
        string txt = Mathf.RoundToInt(damage).ToString();
        if (st.Has(Flavor.Glass) && damage >= 40) txt += "!";
        Fx.Popup(at + new Vector2(0, -10), txt, Colors.White.Lerp(ac, 0.35f), size, 0.75f);
    }

    private static readonly string[][] KillWords =
    {
        new[] { "KO" },
        new[] { "KO!", "POP!" },
        new[] { "SMASHED!", "BONK!", "SPLAT!" },
        new[] { "WRECKED!", "DEMOLISHED!", "KABOOM!" },
        new[] { "OBLITERATED!", "ANNIHILATED!", "ERASED!" },
        new[] { "DELETED!!", "VAPORIZED!!", "SUPERNOVA!!" },
    };

    private readonly int[] _battleKills = new int[LobbyState.MaxPlayers];

    /// <summary>
    /// --fxshots: freezes everyone and plays a hit (left) and a kill (right) as if slot 0 held
    /// <paramref name="power"/> cards with <paramref name="flavors"/>, so effect scaling can be screenshotted.
    /// </summary>
    /// <summary>Test hook (offline only): replaces every player's cards at the start of each battle.</summary>
    public Func<int, List<int>> DebugCards;

    /// <summary>Test hook (host): starts a battle on a chosen map seed right away.</summary>
    public void DebugStartBattle(int seed)
    {
        if (!IsHost) return;
        _hostNext = null;
        Rpc(MethodName.StartBattle, seed, ScoresBytes());
    }

    public void DebugShowcase(int power, Flavor flavors)
    {
        if (Slots[0] == null || Slots[1] == null) return;
        foreach (var a in LiveAvatars()) a.Frozen = true;
        Bullets.Clear();
        Slots[0].Fx = new FxStyle(power, flavors);
        var mid = new Vector2(MapLayout.Width, MapLayout.Height) / 2;
        HitFx(1, 0, mid + new Vector2(-170, 0), new Vector2(140, -40), 25 + power * 4);
        Array.Clear(_battleKills);
        if (power >= 9) _battleKills[0] = 1; // show a streak call-out too
        KillFx(1, 0, mid + new Vector2(150, 10), KillCause.Shot);
    }

    private void KillFx(int victim, int killer, Vector2 at, KillCause cause)
    {
        var st = StyleOf(killer);
        int tier = st.Tier;
        float I = st.Intensity;
        var vc = Palette.Player(ColorOf(victim));
        var kc = killer >= 0 ? Palette.Player(ColorOf(killer)) : Colors.White;
        bool fell = cause == KillCause.Fall;
        if (fell) at = new Vector2(Mathf.Clamp(at.X, 20, MapLayout.Width - 20), MapLayout.Height - 4);
        Vector2? up = fell ? Vector2.Up : null;
        float spread = fell ? 1.4f : Mathf.Tau;

        Fx.Emit(at, PKind.Chunk, vc, Mathf.RoundToInt(16 * I), 260 * Mathf.Sqrt(I), 1.1f, 3f, 520, up, spread, 1.2f);
        Fx.Emit(at, PKind.Streak, vc.Lightened(0.4f), Mathf.RoundToInt(12 * I), 320, 0.45f, 1.4f, 200, up, spread);
        Fx.Emit(at, PKind.Dot, Colors.White, 8 + tier * 4, 200, 0.5f, 1.3f, 0, up, spread);
        Fx.Flash(at, 40 + tier * 12, vc, 0.35f);
        Fx.Flash(at, 18 + tier * 4, Colors.White, 0.15f);
        Fx.RingFx(at, 4, 50 + tier * 16, vc, 0.45f, 3f);
        Fx.RingFx(at, 2, 34 + tier * 10, Colors.White, 0.35f, 1.5f, 0.06f);
        if (tier >= 1) Fx.Emit(at, PKind.Smoke, new Color(0.12f, 0.08f, 0.16f, 0.9f), 3 + tier * 2, 70, 0.9f, 4f, -25);
        if (tier >= 2)
        {
            Fx.RaysFx(at, kc, 8 + tier * 2, 70 + tier * 22, 0.6f);
            Fx.Emit(at, PKind.Confetti, kc, 10 + tier * 6, 260, 1.6f, 3.4f, 260, up, spread, 2.2f);
            Fx.Emit(at, PKind.Confetti, vc.Lightened(0.3f), 6 + tier * 4, 240, 1.6f, 3.4f, 260, up, spread, 2.2f);
        }
        if (tier >= 3)
        {
            Fx.RingFx(at, 6, 90 + tier * 20, kc, 0.6f, 4f, 0.12f);
            Fx.ScreenFlash(kc with { A = 0.16f }, 0.16f);
            _zoomPunch = Mathf.Max(_zoomPunch, 0.5f + tier * 0.1f);
        }
        if (tier >= 4) Fx.Emit(at, PKind.Star, Colors.White, 6 + tier * 2, 260, 0.8f, 4f, 0, up, spread, 1.6f);

        if (st.Has(Flavor.Fire))
        {
            Fx.Emit(at, PKind.Ember, new Color("#ffae3b"), 18 + tier * 6, 260, 1.1f, 1.8f, -90);
            Fx.Emit(at, PKind.Smoke, new Color(0.18f, 0.1f, 0.08f, 0.9f), 6 + tier * 2, 90, 1.2f, 5f, -30);
            Fx.Flash(at, 70 + tier * 14, new Color("#ff7a22"), 0.4f);
        }
        if (st.Has(Flavor.Homing)) Fx.Emit(at, PKind.Star, kc.Lightened(0.5f), 10 + tier * 3, 200, 0.9f, 3.4f);
        if (st.Has(Flavor.Bouncy))
            for (int i = 0; i < 3; i++) Fx.RingFx(at, 4, 30 + i * 18, kc.Lightened(0.3f), 0.35f, 1.6f, 0.08f * (i + 1));
        if (st.Has(Flavor.Pierce))
        {
            var d = Vector2.FromAngle((float)GD.RandRange(0, Mathf.Pi));
            Fx.Emit(at, PKind.Streak, Colors.White, 6, 700, 0.25f, 2f, 0, d, 0.06f, 1f);
            Fx.Emit(at, PKind.Streak, Colors.White, 6, 700, 0.25f, 2f, 0, -d, 0.06f, 1f);
        }
        if (st.Has(Flavor.Split) || st.Has(Flavor.Glass))
            Fx.Emit(at, PKind.Shard, st.Has(Flavor.Glass) ? new Color("#d6f6ff") : kc, 14 + tier * 4, 300, 1f, 3.2f, 450, up, spread);
        if (st.Has(Flavor.Feather)) Fx.Emit(at, PKind.Confetti, Colors.White, 10, 160, 2f, 3.6f, 50, up, spread, 3f);
        if (st.Has(Flavor.Vampire) && killer >= 0)
            Fx.EmitHoming(at, PosOf(killer, at), new Color("#ff2a4a"), 14 + tier * 3, 200, 1.2f, 1.4f);

        // Words: how hard it was, plus a streak call-out.
        string word = fell && killer < 0 ? "OUT!" : KillWords[tier][(int)(GD.Randi() % (uint)KillWords[tier].Length)];
        // Blown up by your own shot (or nobody's): no one to credit.
        if (cause == KillCause.Shot && killer < 0) word = "UNLUCKY!";
        if (cause == KillCause.Hazard && victim >= 0)
            word = _lastHazard[victim] switch { HazardHit.Saw => "SHREDDED!", HazardHit.Magma => "MELTED!", _ => "POISONED!" };
        Fx.Popup(at + new Vector2(0, fell ? -40 : -18), word, kc.Lightened(0.2f), 16 + tier * 3, 1.1f);
        if (killer >= 0 && killer < _battleKills.Length)
        {
            int k = ++_battleKills[killer];
            string streak = k switch { 2 => "DOUBLE KILL", 3 => "TRIPLE KILL", >= 4 => "RAMPAGE", _ => null };
            if (streak != null)
            {
                Fx.Popup(PosOf(killer, at) + new Vector2(0, -38), streak, Palette.Accent, 18 + k * 2, 1.4f);
                Fx.RaysFx(PosOf(killer, at), Palette.Accent, 10, 60, 0.5f);
            }
        }

        Sfx.Play("death");
        if (tier >= 3 || st.Has(Flavor.Fire)) Sfx.Play("explode", 0.6f);
        Shake(0.7f + tier * 0.08f);
        HitStop(70 + Mathf.Min(tier, 4) * 20);
    }

    // =====================================================================
    // RPCs: host → everyone
    // =====================================================================

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void BeginMatch(byte[] lobbyData)
    {
        var lobby = LobbyState.Deserialize(lobbyData);
        Settings = lobby.Settings;
        Array.Clear(Slots);
        LocalMatchPlayer = null;
        LocalGuests.Clear();
        foreach (var info in lobby.Players)
        {
            var mp = new MatchPlayer { Info = info };
            if (IsHost) info.Key = Net.Lobby.BySlot(info.Slot)?.Key; // keys aren't sent, so take them from our own lobby
            if (AttachLocal(mp)) { }
            else if (IsHost)
            {
                mp.Input = info.IsBot ? new BotBrain(this) : new RemoteInput();
            }
            Slots[info.Slot] = mp;
        }
        BattleNumber = 0;
        MatchWinner = -1;
        RematchVotes.Clear();
        MatchAwards.Clear();
        Book.Reset();
        Net.InMatch = true;
        Visible = true;
        if (IsHost) Stats.MatchStart(Settings, Active(), IsBotPlayer);
        Log.Info($"match begins: {lobby.Players.Count} players, {GameModes.Name(Mode)}, {Teams.ModeName(Settings.TeamSize)}{(TeamMode && Settings.FriendlyFire ? " (friendly fire)" : "")}, battles/round {Settings.BattlesPerRound}, rounds to win {Settings.RoundsToWin}");
        MatchBegan?.Invoke();
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void StartBattle(int seed, byte[] scores)
    {
        ApplyScores(scores);
        BattleNumber++;
        foreach (var c in _avatars.GetChildren()) c.QueueFree();
        foreach (var p in Slots) if (p != null) p.Avatar = null;
        Bullets.Clear();
        Fx.Clear();
        if (DebugCards != null && !Networked)
            foreach (var p in Active())
            {
                p.Cards.Clear();
                p.Cards.AddRange(DebugCards(p.Slot));
                p.RecomputeStats();
            }

        var active = Active().OrderBy(p => p.Slot).ToList();
        var layout = LayoutFor(seed, active.Count, out var arenaName);
        MapNode.Build(layout);
        ArenaName = arenaName ?? MapNode.Theme.Name;
        Arena.Build(layout);

        // Seeded shuffle so spawn positions rotate fairly but identically on all peers.
        var order = Enumerable.Range(0, layout.Spawns.Count).ToList();
        var rng = new Rng((ulong)(uint)seed + 77);
        for (int i = order.Count - 1; i > 0; i--)
        {
            int j = rng.Range(0, i);
            (order[i], order[j]) = (order[j], order[i]);
        }
        // Teams start on opposite sides (swapping each battle): Red takes spawns from one end, Blue from the other.
        var byX = Enumerable.Range(0, layout.Spawns.Count).OrderBy(k => layout.Spawns[k].X).ToList();
        if (BattleNumber % 2 == 0) byX.Reverse();
        if (TeamMode && order.Count >= active.Count)
        {
            int lo = 0, hi = byX.Count - 1;
            for (int i = 0; i < active.Count; i++) order[i] = active[i].Info.Team == 0 ? byX[lo++] : byX[hi--];
        }
        for (int i = 0; i < active.Count; i++)
        {
            var p = active[i];
            var sp = layout.Spawns[order[i % order.Count]];
            SpawnAvatar(p, new Vector2(sp.X, sp.Y));
        }
        SetupObjectives(layout, seed, active, byX);
        if (IsHost)
        {
            foreach (var p in active)
                foreach (var e in p.Effects) e.OnBattleStart(p.Avatar, this);
            Stats.BattleStart(BattleNumber, Slots.Where(p => p != null).Sum(p => p.RoundWins) + 1, seed, active);
        }

        Phase = Phase.Countdown;
        CountdownLeft = CountdownTime;
        _lastCountdownTick = -1;
        System.Array.Clear(_battleKills);
        _endCheckT = -1;
        _lastVictim = _lastKiller = -1;
        _killCamWait = false;
        KillCam.Begin();
        PickOptions.Clear();
        PickDeals.Clear();
        Log.Info($"battle {BattleNumber} start (seed {seed}, {(Settings.Maps.Count > 0 ? $"custom arena \"{ArenaName}\", {layout.Solids.Count} solids, " : "")}map attempts {layout.Attempts}{(layout.UsedFallback ? ", fallback" : "")})");
        BattleStarted?.Invoke();
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void BattleOver(int winner, byte[] scores, bool roundWon, bool matchWon)
    {
        KillCam.End();
        ApplyScores(scores);
        string name = WinnerName(winner);
        Color col = WinnerColor(winner);
        Log.Info($"battle over: winner {winner} ({(winner >= 0 ? name : "draw")}) round={roundWon} match={matchWon} | {ScoreLine()}");

        if (matchWon)
        {
            Phase = Phase.MatchOver;
            MatchWinner = winner;
            Net.InMatch = false;
            Banner?.Invoke(winner >= 0 ? $"{name} WINS THE MATCH!" : "MATCH OVER", col, 3f);
            Sfx.Play("win");
            MatchEnded?.Invoke(winner);
            return;
        }
        Phase = Phase.Intermission;
        if (winner < 0) Banner?.Invoke("DRAW", col, 1.2f);
        else if (roundWon) { Banner?.Invoke($"{name} TAKES THE ROUND", col, 1.5f); Sfx.Play("win", 0.7f); }
        else Banner?.Invoke($"{name} wins the battle", col, 1.2f);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void BeginCardPick(byte[] data)
    {
        ReadPicks(data);
        Picked.Clear();
        Phase = Phase.CardPick;
        Bullets.Clear();
        CardPickBegan?.Invoke();
    }

    /// <summary>To a rejoining client only: the match as it stands (see <see cref="RejoinBytes"/>).</summary>
    [Rpc(MultiplayerApi.RpcMode.Authority, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Rejoined(byte[] data)
    {
        var r = new WireReader(data);
        var lobby = LobbyState.Deserialize(r.Blob());
        Settings = lobby.Settings;
        Array.Clear(Slots);
        LocalMatchPlayer = null;
        LocalGuests.Clear();
        foreach (var info in lobby.Players)
        {
            var mp = new MatchPlayer { Info = info, Left = r.Bool(), RoundWins = r.U8(), BattleWins = r.U8() };
            int cards = r.U8();
            for (int k = 0; k < cards; k++) mp.Cards.Add(r.I16());
            mp.RecomputeStats();
            AttachLocal(mp);
            Slots[info.Slot] = mp;
        }
        BattleNumber = r.I16();
        MatchWinner = -1;
        Phase = Phase.Idle; // until the next StartBattle, unless a card pick is on
        Net.InMatch = true;
        Visible = true;
        Log.Info($"rejoined the match at battle {BattleNumber} with {LocalMatchPlayer?.Cards.Count ?? 0} cards | {ScoreLine()}");
        MatchBegan?.Invoke();
        ScoresChanged?.Invoke();
        if (r.Bool())
        {
            ReadPicks(r.Blob());
            Picked.Clear();
            int n = r.U8();
            for (int i = 0; i < n; i++) Picked.Add(r.U8());
            Phase = Phase.CardPick;
            CardPickBegan?.Invoke();
        }
        else Banner?.Invoke("REJOINED! YOU'RE IN FROM THE NEXT BATTLE", Palette.Accent, 2.5f);
    }

    /// <summary>Gives a player at this machine their input: the keyboard player, or a couch guest's controller. False if they play elsewhere.</summary>
    private bool AttachLocal(MatchPlayer mp)
    {
        var info = mp.Info;
        if (info.IsBot || info.PeerId != Net.LocalPeer) return false;
        if (info.Guest == 0)
        {
            LocalMatchPlayer = mp;
            mp.Input = AutopilotLocal ? new BotBrain(this) : new LocalInput(this);
        }
        else
        {
            LocalGuests.Add(mp);
            int device = Pads.Couch.DeviceOf(info.Guest);
            // A guest whose controller is gone (a rejoin after restarting the game) stands still until the next lobby.
            mp.Input = AutopilotLocal ? new BotBrain(this) : device >= 0 ? new PadInput(device) : null;
        }
        return true;
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void PlayerBack(int slot, long peer)
    {
        var p = Slots[slot];
        if (p == null) return;
        p.Info.PeerId = peer;
        p.Left = false;
        if (p != LocalMatchPlayer) Banner?.Invoke($"{p.Info.Name} is back", Palette.Player(p.Info.ColorIdx), 1.5f);
        ScoresChanged?.Invoke();
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void WaitingForRejoin(int seconds)
    {
        Banner?.Invoke($"WAITING FOR PLAYERS TO REJOIN  {seconds}", Palette.TextDim, 1.2f);
    }

    /// <param name="curseId">The curse that came with a cursed deal, or -1 for an ordinary pick.</param>
    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void CardPicked(int slot, int cardId, int curseId)
    {
        var p = Slots[slot];
        if (p == null) return;
        Picked.Add(slot);
        var unlocked = Combos.Unlocks(p.Cards, cardId);
        _lastPickCombo = unlocked.Count > 0;
        p.Cards.Add(cardId);
        if (curseId >= 0) p.Cards.Add(curseId);
        p.RecomputeStats();
        Sfx.Play("pick", 0.6f);
        if (curseId >= 0) Log.Info($"{p.Info.Name} took a cursed deal: {CardRegistry.Get(cardId)?.Name} with {CardRegistry.Get(curseId)?.Name}");
        else Log.Info($"{p.Info.Name} picked {CardRegistry.Get(cardId)?.Name}");
        CardWasPicked?.Invoke(slot, cardId);
        if (curseId >= 0) CurseTaken?.Invoke(slot, curseId);
        foreach (var combo in unlocked)
        {
            Log.Info($"combo: {p.Info.Name} found {combo.Name}");
            if (IsLocalHuman(p)) Sfx.Play("combo", 0.7f);
            ComboFound?.Invoke(slot, combo);
        }
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void SyncRematchVotes(byte[] data)
    {
        var r = new WireReader(data);
        RematchVotes.Clear();
        int n = r.U8();
        for (int i = 0; i < n; i++) RematchVotes.Add(r.U8());
        RematchVotesChanged?.Invoke();
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void EndMatch()
    {
        ResetWorld();
        Net.InMatch = false;
        ReturnedToLobby?.Invoke();
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void PlayerLeft(int slot)
    {
        if (Slots[slot] == null) return;
        Slots[slot].Left = true;
        _respawnAt[slot] = -1; // if they rejoin, they sit out the rest of this battle
        if (Slots[slot].Avatar is { } a)
        {
            a.Alive = false;
            a.SyncGhost();
        }
        Banner?.Invoke($"{Slots[slot].Info.Name} left", Palette.TextDim, 1.2f);
        ScoresChanged?.Invoke();
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void SpawnBullet(byte[] data)
    {
        if (Map == null) return;
        var b = Bullet.Deserialize(data);
        Bullets.Add(b);
        OnShotFx(b);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void BulletGone(int id, Vector2 pos, bool impact)
    {
        var b = Bullets.Kill(id);
        ImpactFx(pos, b?.Owner ?? -1, impact);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Damaged(int slot, float newHp, Vector2 knockback, Vector2 at, float damage, int attacker)
    {
        var a = Slots[slot]?.Avatar;
        if (a == null) return;
        if (!IsHost)
        {
            a.Hp = newHp;
            if (IsLocalHuman(slot)) a.Velocity += knockback; // predicted avatar feels the hit
        }
        a.OnHurtFx();
        HitFx(slot, attacker, at, knockback, damage);
        Sfx.Play("hit");
        float tierShake = StyleOf(attacker).Tier * 0.04f;
        Shake((IsLocalHuman(slot) ? 0.45f : 0.2f) + tierShake);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void HazardHurt(int slot, float newHp, Vector2 knockback, Vector2 at, float damage, int kind)
    {
        var a = Slots[slot]?.Avatar;
        if (a == null) return;
        if (!IsHost)
        {
            a.Hp = newHp;
            if (IsLocalHuman(slot)) a.Velocity += knockback;
        }
        var k = (HazardHit)kind;
        _lastHazard[slot] = k;
        a.OnHurtFx();
        bool mine = IsLocalHuman(slot);
        switch (k)
        {
            case HazardHit.Saw:
                var dir = knockback.LengthSquared() > 1 ? knockback.Normalized() : Vector2.Up;
                Fx.Emit(at, PKind.Streak, new Color("#fff1c2"), 12, 260, 0.25f, 1f, 300, dir, 2.4f);
                Fx.Emit(at, PKind.Chunk, Palette.Player(ColorOf(slot)), 4, 140, 0.6f, 2f, 500, dir, 2f);
                Fx.Flash(at, 16, Colors.White, 0.12f);
                Fx.Popup(at + new Vector2(0, -10), Mathf.RoundToInt(damage).ToString(), new Color("#ffe1e1"), 13, 0.7f);
                Sfx.Play("saw");
                Shake(mine ? 0.45f : 0.2f);
                break;
            case HazardHit.Magma:
                Fx.Emit(at, PKind.Ember, new Color("#ffb13b"), 5, 90, 0.6f, 1.3f, -70, Vector2.Up, 1.6f);
                Fx.Emit(at, PKind.Smoke, new Color(0.25f, 0.15f, 0.12f, 0.6f), 1, 20, 0.6f, 3f, -30);
                Fx.Popup(at + new Vector2(0, -26), Mathf.RoundToInt(damage).ToString(), new Color("#ffb13b"), 9, 0.5f);
                Sfx.Play("sizzle", 0.55f);
                if (mine) Shake(0.08f);
                break;
            case HazardHit.Poison:
                Fx.Emit(at, PKind.Dot, new Color("#9dff4a"), 4, 40, 0.6f, 1.2f, -40);
                Fx.Popup(at + new Vector2(0, -20), Mathf.RoundToInt(damage).ToString(), new Color("#9dff4a"), 9, 0.5f);
                Sfx.Play("sizzle", 0.3f);
                break;
        }
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void PropBroken(int index, Vector2 at, bool fell)
    {
        if (Map == null) return;
        Arena.Break(index, at, fell);
        if (!fell) Shake(0.1f);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Died(int slot, Vector2 at, int killer, int cause)
    {
        var a = Slots[slot]?.Avatar;
        if (a == null) return;
        a.Alive = false;
        a.CollisionLayer = 0;
        a.QueueRedraw();
        KillCam.NoteDeath(slot, _clock);
        NoteRespawn(slot, (KillCause)cause);
        KillFx(slot, killer, at, (KillCause)cause);
        Log.Info($"{NameOf(slot)} died{(killer >= 0 ? $" (by {NameOf(killer)})" : "")}");
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void FxEvent(int kind, Vector2 pos, float value, int slot, Vector2 pos2)
    {
        var col = Palette.Player(ColorOf(slot));
        var st = StyleOf(slot);
        switch ((FxKind)kind)
        {
            case FxKind.Explosion:
            {
                var fire = new Color("#ffa33b");
                Fx.Flash(pos, value * 2.2f, fire, 0.3f);
                Fx.Flash(pos, value * 0.9f, Colors.White, 0.12f);
                Fx.RingFx(pos, 2, value * 1.1f, col.Lightened(0.3f), 0.3f, 3f);
                Fx.RingFx(pos, 2, value * 1.6f, fire, 0.4f, 1.5f, 0.05f);
                Fx.Emit(pos, PKind.Ember, fire, 14 + st.Tier * 4, value * 6, 0.8f, 1.6f, -60);
                Fx.Emit(pos, PKind.Streak, col.Lightened(0.4f), 10 + st.Tier * 2, value * 8, 0.35f, 1.4f, 100);
                Fx.Emit(pos, PKind.Smoke, new Color(0.16f, 0.1f, 0.1f, 0.9f), 6 + st.Tier, value * 2, 1f, 4.5f, -30);
                if (st.Tier >= 3) Fx.RaysFx(pos, fire, 8, value * 2.4f, 0.35f);
                Sfx.Play("explode");
                Shake(0.4f + st.Tier * 0.04f);
                break;
            }
            case FxKind.Block:
                Fx.RingFx(pos, 8, 22, Colors.White, 0.25f, 2f);
                Fx.Flash(pos, 22, col, 0.2f);
                Sfx.Play("block", 0.7f);
                break;
            case FxKind.Blocked:
                Fx.Emit(pos, PKind.Streak, Colors.White, 10, 180, 0.3f, 1f);
                Fx.Emit(pos, PKind.Star, col.Lightened(0.5f), 3, 80, 0.4f, 2.5f);
                Fx.RingFx(pos, 2, 14, Colors.White, 0.2f, 1.5f);
                Fx.Popup(pos + new Vector2(0, -10), "BLOCKED", Colors.White, 11, 0.7f);
                Sfx.Play("blocked");
                break;
            case FxKind.Shockwave:
                Fx.RingFx(pos, 6, value, col.Lightened(0.5f), 0.32f, 2.5f);
                Fx.RingFx(pos, 4, value * 0.7f, Colors.White, 0.22f, 1.2f, 0.04f);
                Fx.Flash(pos, value * 0.8f, col, 0.15f);
                Fx.Emit(pos, PKind.Dot, col.Lightened(0.4f), 12, value * 3, 0.35f, 1.2f);
                Sfx.Play("blink", 0.6f);
                break;
            case FxKind.Bash:
                Fx.RingFx(pos, 6, value + 4, Colors.White, 0.2f, 3f);
                Fx.Emit(pos, PKind.Streak, col.Lightened(0.5f), 14, 220, 0.25f, 1.2f);
                Fx.Flash(pos, value * 1.4f, col, 0.18f);
                Sfx.Play("blocked", 0.9f);
                Shake(0.22f);
                break;
            case FxKind.Counter:
                Fx.Popup(pos + new Vector2(0, -20), "COUNTER!", col.Lightened(0.45f), 11, 0.8f);
                Fx.RingFx(pos, 20, 4, col.Lightened(0.3f), 0.3f, 2f);
                break;
            case FxKind.Blink:
                foreach (var p in new[] { pos, pos2 })
                {
                    Fx.Emit(p, PKind.Dot, col.Lightened(0.3f), 14, 110, 0.45f, 1.2f);
                    Fx.Emit(p, PKind.Star, Colors.White, 4, 60, 0.4f, 2.4f);
                    Fx.RingFx(p, 18, 2, col, 0.25f, 2f);
                }
                Sfx.Play("blink");
                break;
            case FxKind.Zap:
            {
                var bolt = new Color("#8fe9ff");
                Fx.Bolt(pos, pos2, bolt);
                Fx.Bolt(pos, pos2, col, 0.18f);
                Fx.Flash(pos2, 18, bolt, 0.18f);
                Fx.Emit(pos2, PKind.Streak, bolt, 6, 150, 0.2f, 0.8f);
                Sfx.Play("blink", 0.5f);
                break;
            }
            case FxKind.Revive:
            {
                var gold = new Color("#ffe066");
                Fx.RingFx(pos, 4, 40, gold, 0.45f, 3f);
                Fx.RaysFx(pos, gold, 10, 50, 0.5f);
                Fx.Emit(pos, PKind.Star, gold, 12, 120, 0.6f, 2.4f, -40);
                Fx.Popup(pos + new Vector2(0, -18), "SECOND WIND!", gold, 14, 1f);
                Sfx.Play("pad");
                break;
            }
        }
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, TransferMode = MultiplayerPeer.TransferModeEnum.UnreliableOrdered)]
    private void Snapshot(byte[] data)
    {
        if (IsHost || Map == null) return;
        var r = new WireReader(data);
        int n = r.U8();
        for (int i = 0; i < n; i++)
        {
            int slot = r.U8();
            int flags = r.U8();
            var pos = r.V2();
            var vel = r.V2();
            float hp = r.I16();
            int ammo = r.U8();
            float reload = r.U8() / 255f;
            float blockCd = r.U8() / 20f;
            float blockActive = r.U8() / 100f;
            float aim = r.I16() / 1000f;

            var a = Slots[slot]?.Avatar;
            if (a == null || _clock - a.BornAt < 0.25f) continue; // just respawned: older snapshots still show the dead body
            bool mine = IsLocalHuman(slot);
            if (a.Alive && (flags & 1) == 0) a.Alive = false;
            a.Hp = hp;
            a.Ammo = ammo;
            a.Reloading = (flags & 2) != 0;
            a.ReloadFrac = reload;
            a.BlockCd = blockCd;
            a.BlockActive = blockActive;
            if (!mine) a.AimDir = Vector2.FromAngle(aim);
            a.SetNetTarget(pos, vel, predicted: mine);
            a.QueueRedraw();
        }
        if (r.More) Arena.Read(r);
        if (r.More) ReadObjectives(r);
    }

    // =====================================================================
    // Emotes, pings and ghosts
    // =====================================================================

    /// <summary>Every peer, after the host lets an emote or a ping through: (slot, emote id) and (slot, where).</summary>
    public event Action<int, int> EmoteShown;
    public event Action<int, Vector2> PingShown;

    private readonly EmoteLimiter[] _emoteGuard = NewGuards(), _pingGuard = NewGuards();
    private readonly EmoteLimiter[] _myEmotes = NewGuards(), _myPings = NewGuards(); // per slot, for the players at this machine
    private readonly Rng _chatRng = new((ulong)DateTime.Now.Ticks ^ 0x9e3779b97f4a7c15UL);

    private static EmoteLimiter[] NewGuards() => Enumerable.Range(0, LobbyState.MaxPlayers).Select(_ => new EmoteLimiter()).ToArray();

    /// <summary>Emotes and pings work while the arena is on screen, alive or as a ghost.</summary>
    public bool CanChat => Phase is Phase.Countdown or Phase.Fighting or Phase.Intermission && Map != null;

    /// <param name="who">Which player at this machine (default: the keyboard player).</param>
    public void LocalEmote(int id, MatchPlayer who = null)
    {
        who ??= LocalMatchPlayer;
        if (!CanChat || !IsLocalHuman(who) || !Emotes.Valid(id) || !_myEmotes[who.Slot].TryUse()) return;
        if (IsHost) HostEmote(who.Slot, id);
        else RpcId(1, MethodName.RequestEmote, who.Slot, id);
    }

    public void LocalPing(Vector2 at, MatchPlayer who = null)
    {
        who ??= LocalMatchPlayer;
        if (!CanChat || !IsLocalHuman(who) || !_myPings[who.Slot].TryUse()) return;
        if (IsHost) HostPing(who.Slot, at);
        else RpcId(1, MethodName.RequestPing, who.Slot, at);
    }

    private void HostEmote(int slot, int id)
    {
        if (!CanChat || !Emotes.Valid(id) || Slots[slot] is not { Left: false } || !_emoteGuard[slot].TryUse()) return;
        Rpc(MethodName.ShowEmote, slot, id);
    }

    private void HostPing(int slot, Vector2 at)
    {
        if (!CanChat || Slots[slot] is not { Left: false } || !_pingGuard[slot].TryUse()) return;
        at = new Vector2(Mathf.Clamp(at.X, 0, MapLayout.Width), Mathf.Clamp(at.Y, 0, MapLayout.Height));
        Rpc(MethodName.ShowPing, slot, at);
    }

    /// <summary>Host: a bot sometimes says something when it scores, dies or wins (rate limits apply as for anyone).</summary>
    private void HostBotChat(int slot, float chance, params int[] ids)
    {
        if (slot < 0 || Slots[slot] is not { } p || !p.Info.IsBot || !_chatRng.Chance(chance)) return;
        HostEmote(slot, ids[_chatRng.Range(0, ids.Length - 1)]);
    }

    private void TickChatGuards(float dt)
    {
        foreach (var g in _emoteGuard) g.Tick(dt);
        foreach (var g in _pingGuard) g.Tick(dt);
        foreach (var g in _myEmotes) g.Tick(dt);
        foreach (var g in _myPings) g.Tick(dt);
    }

    /// <summary>Reads the emote and ping keys (and buttons) for the people at this computer.</summary>
    private void ReadChatKeys()
    {
        if (!CanChat || AutopilotLocal) return;
        var me = LocalMatchPlayer;
        if (me != null && !LocalInput.Suppressed)
        {
            for (int i = 0; i < Emotes.All.Length; i++)
                if (Input.IsActionJustPressed(Emotes.Action(i))) LocalEmote(i);
            if (Input.IsActionJustPressed("ping")) LocalPing(GetGlobalMousePosition());
            foreach (int d in Pads.Unclaimed()) PadChat(me, d);
        }
        foreach (var g in LocalGuests)
            if (g.Input is PadInput pad) PadChat(g, pad.Device);
    }

    private void PadChat(MatchPlayer who, int device)
    {
        for (int i = 0; i < Emotes.All.Length && i < PadLayout.Emotes.Length; i++)
            if (Pads.JustPressed(device, PadLayout.Emotes[i])) LocalEmote(i, who);
        // A controller pings where its player aims.
        if (Pads.JustPressed(device, PadLayout.Ping) && who.Avatar is { } a && IsInstanceValid(a))
            LocalPing(who.Input switch
            {
                PadInput p => a.GlobalPosition + p.AimDir * PadReader.Reach,
                LocalInput l => a.GlobalPosition + l.PadAimDir * PadReader.Reach,
                _ => a.GlobalPosition,
            }, who);
    }

    /// <summary>Where a bot's ghost (or an autopiloted one) floats: circling over the nearest live player's head.</summary>
    private Vector2 BotGhostTarget(Avatar ghost)
    {
        Avatar near = null;
        float best = float.MaxValue;
        foreach (var a in LiveAvatars())
        {
            float d = a.GlobalPosition.DistanceSquaredTo(ghost.GlobalPosition);
            if (d < best) (best, near) = (d, a);
        }
        float t = _clock * 1.3f + ghost.Player.Slot * 1.7f;
        var center = near?.GlobalPosition ?? new Vector2(MapLayout.Width / 2f, MapLayout.Height / 3f);
        return center + new Vector2(Mathf.Cos(t) * 46, -46 + Mathf.Sin(t * 2) * 10);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestEmote(int slot, int id)
    {
        if (!IsHost) return;
        if (SenderOwns(slot) is { } p) HostEmote(p.Slot, id);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestPing(int slot, Vector2 at)
    {
        if (!IsHost) return;
        if (SenderOwns(slot) is { } p) HostPing(p.Slot, at);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ShowEmote(int slot, int id)
    {
        if (Slots[slot]?.Avatar is not { } a || !Emotes.Valid(id)) return;
        a.ShowEmote(id);
        Sfx.Play("emote", 0.8f);
        Log.Info($"emote: {NameOf(slot)} says {Emotes.Text(id, !a.Alive)}");
        EmoteShown?.Invoke(slot, id);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ShowPing(int slot, Vector2 at)
    {
        if (Slots[slot] == null || Map == null) return;
        Fx.Ping(at, Palette.Player(ColorOf(slot)), NameOf(slot), Emotes.PingTime);
        Sfx.Play("ping", 0.8f);
        Log.Info($"ping: {NameOf(slot)} at ({at.X:0}, {at.Y:0})");
        PingShown?.Invoke(slot, at);
    }

    // =====================================================================
    // RPCs: client → host
    // =====================================================================

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.UnreliableOrdered)]
    private void SubmitInput(int slot, byte[] data)
    {
        if (!IsHost) return;
        if (SenderOwns(slot, allowLeft: true) is { Input: RemoteInput ri }) ri.Latest = InputFrame.FromBytes(data);
    }

    /// <summary>Host: the player in <paramref name="slot"/> if the peer sending this RPC plays it (itself or a couch guest of its), else null.</summary>
    private MatchPlayer SenderOwns(int slot, bool allowLeft = false)
    {
        long sender = Multiplayer.GetRemoteSenderId();
        return slot >= 0 && slot < Slots.Length && Slots[slot] is { } p && !p.Info.IsBot && p.Info.PeerId == sender && (allowLeft || !p.Left) ? p : null;
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestPick(int slot, int cardId, bool deal)
    {
        if (!IsHost) return;
        if (SenderOwns(slot, allowLeft: true) is { } p) HostApplyPick(p.Slot, cardId, deal);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestRematch(int slot, bool yes)
    {
        if (!IsHost) return;
        if (SenderOwns(slot) is { } p) HostVoteRematch(p.Slot, yes);
    }

    // =====================================================================
    // Serialization helpers
    // =====================================================================

    private byte[] ScoresBytes()
    {
        var w = new Wire();
        foreach (var p in Slots)
        {
            w.Bool(p != null);
            if (p != null) w.U8(p.RoundWins).U8(p.BattleWins);
        }
        return w.Done();
    }

    private void ApplyScores(byte[] data)
    {
        var r = new WireReader(data);
        foreach (var p in Slots)
        {
            if (!r.Bool()) continue;
            int rw = r.U8(), bw = r.U8();
            if (p == null) continue;
            p.RoundWins = rw;
            p.BattleWins = bw;
        }
        ScoresChanged?.Invoke();
    }

    /// <summary>One slot's offer in a card pick: its cards, and the cursed deal if it has one.</summary>
    private readonly record struct PickEntry(int Slot, List<int> Ids, (int Rare, int Curse)? Deal);

    private static void WritePicks(Wire w, IReadOnlyCollection<PickEntry> entries)
    {
        w.U8(entries.Count);
        foreach (var (slot, ids, deal) in entries)
        {
            w.U8(slot).U8(ids.Count);
            foreach (int id in ids) w.I16(id); // 16 bits: custom card ids start at 1000
            w.Bool(deal != null);
            if (deal is { } d) w.I16(d.Rare).I16(d.Curse);
        }
    }

    private void ReadPicks(byte[] data)
    {
        var r = new WireReader(data);
        PickOptions = new Dictionary<int, List<int>>();
        PickDeals = new Dictionary<int, (int Rare, int Curse)>();
        int n = r.U8();
        for (int i = 0; i < n; i++)
        {
            int slot = r.U8();
            int count = r.U8();
            var ids = new List<int>();
            for (int k = 0; k < count; k++) ids.Add(r.I16());
            PickOptions[slot] = ids;
            if (r.Bool()) PickDeals[slot] = (r.I16(), r.I16());
        }
    }

    /// <summary>
    /// The match as it stands, for a rejoining client: every slot's player, whether they've left, their score and cards,
    /// the battle number, and the card pick if one is on (offers and who has picked).
    /// </summary>
    private byte[] RejoinBytes()
    {
        var players = Slots.Where(p => p != null).ToList();
        var lobby = new LobbyState { Settings = Settings };
        lobby.Players.AddRange(players.Select(p => p.Info));
        var w = new Wire();
        w.Blob(lobby.Serialize());
        foreach (var p in players)
        {
            w.Bool(p.Left).U8(p.RoundWins).U8(p.BattleWins).U8(p.Cards.Count);
            foreach (int id in p.Cards) w.I16(id);
        }
        w.I16(BattleNumber);
        bool picking = Phase == Phase.CardPick;
        w.Bool(picking);
        if (picking)
        {
            var picks = new Wire();
            WritePicks(picks, PickOptions.Select(kv => new PickEntry(kv.Key, kv.Value, PickDeals.TryGetValue(kv.Key, out var d) ? d : null)).ToList());
            w.Blob(picks.Done());
            w.U8(Picked.Count);
            foreach (int s in Picked) w.U8(s);
        }
        return w.Done();
    }

    private byte[] SnapshotBytes()
    {
        var w = new Wire();
        var list = Slots.Where(p => p?.Avatar != null).ToList();
        w.U8(list.Count);
        foreach (var p in list)
        {
            var a = p.Avatar;
            int flags = (a.Alive ? 1 : 0) | (a.Reloading ? 2 : 0);
            w.U8(p.Slot).U8(flags).V2(a.GlobalPosition).V2(a.Velocity)
             .I16(Mathf.CeilToInt(a.Hp)).U8(Mathf.Clamp(a.Ammo, 0, 255))
             .U8((int)(a.ReloadFrac * 255)).U8(Mathf.Clamp((int)(a.BlockCd * 20), 0, 255))
             .U8(Mathf.Clamp((int)(a.BlockActive * 100), 0, 255)).I16((int)(a.AimDir.Angle() * 1000));
        }
        Arena.Write(w);
        WriteObjectives(w);
        return w.Done();
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
