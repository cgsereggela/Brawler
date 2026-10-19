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

public enum FxKind { Explosion, Block, Blocked, Blink }

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

    public NetworkManager Net;
    public Sfx Sfx;
    public FxLayer Fx { get; private set; }
    public BulletSystem Bullets { get; private set; }
    public MapNode MapNode { get; private set; }
    public MapLayout Map => MapNode.Layout;
    public Rng HostRng { get; private set; } = new((ulong)DateTime.Now.Ticks);

    public readonly MatchPlayer[] Slots = new MatchPlayer[LobbyState.MaxPlayers];
    public MatchSettings Settings = new();
    public Phase Phase { get; private set; } = Phase.Idle;
    public float CountdownLeft { get; private set; }
    public MatchPlayer LocalMatchPlayer { get; private set; }
    public int BattleNumber { get; private set; }
    public int MatchWinner { get; private set; } = -1;

    /// <summary>When true the local player is driven by a bot (used by the automated tests).</summary>
    public bool AutopilotLocal;
    public bool DisableHitStop;

    /// <summary>Host-only card balance log (see StatsRecorder).</summary>
    public readonly StatsRecorder Stats = new();

    public Dictionary<int, List<int>> PickOptions { get; private set; } = new();
    public readonly HashSet<int> Picked = new();

    public event Action MatchBegan, BattleStarted, ScoresChanged, CardPickBegan, ReturnedToLobby;
    public event Action<int> MatchEnded;
    public event Action<int, int> CardWasPicked;
    public event Action<string, Color, float> Banner;

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

    public override void _Ready()
    {
        MapNode = new MapNode { Name = "Map" };
        Bullets = new BulletSystem { Name = "Bullets", Game = this };
        _avatars = new Node2D { Name = "Avatars" };
        Fx = new FxLayer { Name = "Fx" };
        _cam = new Camera2D { Name = "Camera", Position = new Vector2(MapLayout.Width, MapLayout.Height) / 2 };
        AddChild(MapNode);
        AddChild(_avatars);
        AddChild(Bullets);
        AddChild(Fx);
        AddChild(_cam);
        _cam.MakeCurrent();
        Visible = false;
        Net.PeerLeft += OnPeerLeft;
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

    public bool IsBotPlayer(MatchPlayer p) => p.Info.IsBot || (p == LocalMatchPlayer && AutopilotLocal);

    public string NameOf(int slot) => slot >= 0 && slot < Slots.Length && Slots[slot] != null ? Slots[slot].Info.Name : "?";

    private bool IsHost => Net.IsHost;
    private bool Networked => Net.Mode is NetMode.Host or NetMode.Client;

    // =====================================================================
    // Host: match flow
    // =====================================================================

    public void HostStartMatch()
    {
        if (!IsHost || Net.Lobby.Players.Count < 2) return;
        Net.InMatch = true;
        if (Stats.Mode != "sim") Stats.Mode = Net.Mode == NetMode.Host ? "host" : "offline";
        Rpc(MethodName.BeginMatch, Net.Lobby.Serialize());
        HostStartBattle();
    }

    private void HostStartBattle()
    {
        int seed = (int)HostRng.NextUInt();
        Rpc(MethodName.StartBattle, seed, ScoresBytes());
    }

    private void HostResolveBattle()
    {
        int winner = _pendingWinner;
        bool roundWon = false, matchWon = false;
        if (winner >= 0 && Slots[winner] != null)
        {
            var w = Slots[winner];
            w.BattleWins++;
            if (w.BattleWins >= Settings.BattlesPerRound)
            {
                roundWon = true;
                w.RoundWins++;
                foreach (var p in Active()) p.BattleWins = 0;
                if (w.RoundWins >= Settings.RoundsToWin) matchWon = true;
            }
        }
        Stats.BattleEnd(winner, roundWon, Active());
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
        var entries = new List<(int slot, List<int> ids)>();
        foreach (var p in Active())
        {
            int count = p.Slot == roundWinner ? Settings.WinnerChoices : Settings.LoserChoices;
            if (count > 0) entries.Add((p.Slot, CardRegistry.RollOptions(HostRng, count)));
        }
        if (entries.Count == 0)
        {
            HostStartBattle();
            return;
        }
        w.U8(entries.Count);
        foreach (var (slot, ids) in entries)
        {
            w.U8(slot).U8(ids.Count);
            foreach (int id in ids) w.U8(id);
        }
        _hostPicked.Clear();
        _botPickT.Clear();
        _pickTimeoutT = PickTimeout;
        int completedRounds = Slots.Where(p => p != null).Sum(p => p.RoundWins);
        foreach (var (slot, ids) in entries)
        {
            bool bot = IsBotPlayer(Slots[slot]);
            if (bot) _botPickT[slot] = HostRng.Range(0.5f, 1.5f);
            Stats.PickOffered(completedRounds, Slots[slot], bot, slot == roundWinner, ids);
        }
        Rpc(MethodName.BeginCardPick, w.Done());
    }

    private void HostApplyPick(int slot, int cardId, bool timedOut = false)
    {
        if (Phase != Phase.CardPick || _hostPicked.Contains(slot)) return;
        if (!PickOptions.TryGetValue(slot, out var opts) || !opts.Contains(cardId)) return;
        _hostPicked.Add(slot);
        Stats.Picked(slot, cardId, timedOut);
        Rpc(MethodName.CardPicked, slot, cardId);
        HostCheckPicksDone();
    }

    private void HostCheckPicksDone()
    {
        bool done = PickOptions.Keys.All(s => _hostPicked.Contains(s) || Slots[s] == null || Slots[s].Left);
        if (done && _hostNext == null) HostAfter(0.9f, HostStartBattle);
    }

    public void HostReturnToLobby()
    {
        if (!IsHost) return;
        Rpc(MethodName.EndMatch);
        Net.BroadcastLobby();
    }

    private void HostAfter(float seconds, Action a)
    {
        _hostNext = a;
        _hostNextT = seconds;
    }

    private void OnPeerLeft(long peer)
    {
        if (Phase == Phase.Idle) return;
        var mp = Slots.FirstOrDefault(p => p != null && !p.Info.IsBot && p.Info.PeerId == peer);
        if (mp == null) return;
        if (mp.Avatar != null && mp.Avatar.Alive) HostKill(mp.Avatar, KillCause.Left);
        Rpc(MethodName.PlayerLeft, mp.Slot);
        if (Phase == Phase.CardPick) HostCheckPicksDone();

        var remaining = Active().ToList();
        if (remaining.Count < 2 && Phase != Phase.MatchOver)
        {
            _hostNext = null;
            Stats.MatchEnd(remaining.Count == 1 ? remaining[0].Slot : -1, false, Slots.Where(p => p != null));
            Rpc(MethodName.BattleOver, remaining.Count == 1 ? remaining[0].Slot : -1, ScoresBytes(), true, true);
        }
    }

    // =====================================================================
    // Local actions (from UI)
    // =====================================================================

    public void LocalPick(int cardId)
    {
        if (LocalMatchPlayer == null) return;
        if (IsHost) HostApplyPick(LocalMatchPlayer.Slot, cardId);
        else RpcId(1, MethodName.RequestPick, cardId);
    }

    // =====================================================================
    // Simulation
    // =====================================================================

    public override void _PhysicsProcess(double delta)
    {
        float dt = (float)delta;
        if (IsHost) Stats.Tick(dt, Phase);
        if (IsHost && _hostNext != null)
        {
            _hostNextT -= dt;
            if (_hostNextT <= 0)
            {
                var a = _hostNext;
                _hostNext = null;
                a();
            }
        }

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
                Banner?.Invoke("FIGHT!", Palette.Accent, 0.7f);
                Sfx.Play("go");
            }
        }

        if (IsHost) HostSimulate(dt);
        else ClientSimulate(dt);
        Bullets.Step(dt, IsHost, Map.Solids);

        if (IsHost)
        {
            if (Phase == Phase.Fighting) HostCheckBattleEnd(dt);
            if (Net.Mode == NetMode.Host) Rpc(MethodName.Snapshot, SnapshotBytes());
        }
    }

    private void HostSimulate(float dt)
    {
        foreach (var p in Active())
        {
            var a = p.Avatar;
            if (a == null || !a.Alive) continue;
            var f = p.Input?.Next(a, dt) ?? new InputFrame { Aim = a.GlobalPosition + Vector2.Right };
            a.Step(f, dt, authority: true);
            if (a.GlobalPosition.Y > MapLayout.KillY) HostKill(a, KillCause.Fall);
        }
    }

    private void ClientSimulate(float dt)
    {
        foreach (var p in Active())
        {
            var a = p.Avatar;
            if (a == null || !a.Alive) continue;
            if (p == LocalMatchPlayer && p.Input != null)
            {
                var f = p.Input.Next(a, dt);
                RpcId(1, MethodName.SubmitInput, f.ToBytes());
                a.Step(f, dt, authority: false);
            }
            else a.SmoothRemote(dt);
        }
    }

    private void HostCheckBattleEnd(float dt)
    {
        var alive = LiveAvatars().ToList();
        if (alive.Count > 1)
        {
            _endCheckT = -1;
            return;
        }
        // Short grace period so simultaneous deaths (e.g. explosions) resolve as a draw fairly.
        if (_endCheckT < 0) _endCheckT = 0.35f;
        _endCheckT -= dt;
        if (_endCheckT > 0) return;
        _endCheckT = -1;
        _pendingWinner = alive.Count == 1 ? alive[0].Player.Slot : -1;
        Phase = Phase.Intermission;
        HostAfter(0.8f, HostResolveBattle);
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
                HostApplyPick(slot, BotChooseCard(PickOptions[slot]));
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
        if (!AutopilotLocal || LocalMatchPlayer == null || Picked.Contains(LocalMatchPlayer.Slot)) return;
        if (!PickOptions.TryGetValue(LocalMatchPlayer.Slot, out var opts)) return;
        if (_autoPickT < 0) _autoPickT = 0.8f;
        _autoPickT -= dt;
        if (_autoPickT > 0) return;
        _autoPickT = -1;
        LocalPick(opts[(int)(GD.Randi() % (uint)opts.Count)]);
    }

    private int BotChooseCard(List<int> opts) => opts[HostRng.Range(0, opts.Count - 1)];

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
        }
    }

    public void HostBulletDestroyed(Bullet b, bool impact)
    {
        Fx.Spark(b.Pos, Palette.Player(ColorOf(b.Owner)), impact ? 5 : 2);
        if (Net.Mode == NetMode.Host) Rpc(MethodName.BulletGone, b.Id, b.Pos, impact);
    }

    public void HostDamage(Avatar victim, float damage, Vector2 knockback, int attackerSlot, Vector2 at)
    {
        float hpBefore = victim.Hp;
        if (!victim.TakeHit(damage, knockback))
        {
            if (victim.Alive) Stats.HitBlocked(victim.Player.Slot);
            HostBlocked(victim, at);
            return;
        }
        Stats.Damage(attackerSlot, victim.Player.Slot, Mathf.Clamp(damage, 0, Mathf.Max(0, hpBefore)));
        var attacker = attackerSlot >= 0 ? Slots[attackerSlot]?.Avatar : null;
        if (attacker != null && attacker != victim && attacker.Alive)
        {
            float steal = attacker.S[Stat.Lifesteal];
            if (steal > 0)
            {
                float before = attacker.Hp;
                attacker.Heal(damage * steal);
                Stats.Healed(attacker.Player.Slot, attacker.Hp - before);
            }
            foreach (var e in attacker.Player.Effects) e.OnDealDamage(attacker, victim, damage, this);
        }
        Rpc(MethodName.Damaged, victim.Player.Slot, victim.Hp, knockback, at, damage);
        if (victim.Hp <= 0) HostKill(victim);
    }

    public enum KillCause { Shot, Fall, Left }

    public void HostKill(Avatar a, KillCause cause = KillCause.Shot)
    {
        if (!a.Alive) return;
        if (cause != KillCause.Left) Stats.Death(a.Player.Slot, fell: cause == KillCause.Fall);
        a.Alive = false;
        Rpc(MethodName.Died, a.Player.Slot, a.GlobalPosition);
    }

    public void HostExplode(Vector2 at, float radius, float damage, int ownerSlot, float knockback)
    {
        HostFx(FxKind.Explosion, at, radius, ownerSlot, at);
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
        HostFx(FxKind.Block, a.GlobalPosition, 0, a.Player.Slot, a.GlobalPosition);
    }

    public void HostFx(FxKind kind, Vector2 pos, float value, int slot, Vector2 pos2)
        => Rpc(MethodName.FxEvent, (int)kind, pos, value, slot, pos2);

    public void OnJump(Avatar a)
    {
        Fx.Burst(a.GlobalPosition + new Vector2(0, a.HalfSize.Y), new Color(1, 1, 1, 0.5f), 4, 40, 0.25f, 1, 0);
        if (a.Player == LocalMatchPlayer) Sfx.Play("jump", 0.6f);
    }

    private void OnShotFx(Bullet b)
    {
        Fx.Spark(b.Pos, Palette.Player(ColorOf(b.Owner)).Lightened(0.4f), 3);
        Sfx.Play("shoot", 0.7f);
        if (LocalMatchPlayer != null && b.Owner == LocalMatchPlayer.Slot) Shake(0.12f);
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
        long me = Net.LocalPeer;
        foreach (var info in lobby.Players)
        {
            var mp = new MatchPlayer { Info = info };
            bool isMine = !info.IsBot && info.PeerId == me;
            if (isMine)
            {
                LocalMatchPlayer = mp;
                mp.Input = AutopilotLocal ? new BotBrain(this) : new LocalInput(this);
            }
            else if (IsHost)
            {
                mp.Input = info.IsBot ? new BotBrain(this) : new RemoteInput();
            }
            Slots[info.Slot] = mp;
        }
        BattleNumber = 0;
        MatchWinner = -1;
        Net.InMatch = true;
        Visible = true;
        if (IsHost) Stats.MatchStart(Settings, Active(), IsBotPlayer);
        Log.Info($"match begins: {lobby.Players.Count} players, battles/round {Settings.BattlesPerRound}, rounds to win {Settings.RoundsToWin}");
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

        var active = Active().OrderBy(p => p.Slot).ToList();
        var layout = MapGenerator.Generate(seed, active.Count);
        MapNode.Build(layout);

        // Seeded shuffle so spawn positions rotate fairly but identically on all peers.
        var order = Enumerable.Range(0, layout.Spawns.Count).ToList();
        var rng = new Rng((ulong)(uint)seed + 77);
        for (int i = order.Count - 1; i > 0; i--)
        {
            int j = rng.Range(0, i);
            (order[i], order[j]) = (order[j], order[i]);
        }
        for (int i = 0; i < active.Count; i++)
        {
            var p = active[i];
            var sp = layout.Spawns[order[i % order.Count]];
            var av = new Avatar();
            av.Setup(p, this, Vector2.Zero);
            av.Position = new Vector2(sp.X, sp.Y - av.HalfSize.Y - 1);
            _avatars.AddChild(av);
            p.Avatar = av;
        }
        if (IsHost)
        {
            foreach (var p in active)
                foreach (var e in p.Effects) e.OnBattleStart(p.Avatar, this);
            Stats.BattleStart(BattleNumber, Slots.Where(p => p != null).Sum(p => p.RoundWins) + 1, seed, active);
        }

        Phase = Phase.Countdown;
        CountdownLeft = CountdownTime;
        _lastCountdownTick = -1;
        _endCheckT = -1;
        PickOptions.Clear();
        Log.Info($"battle {BattleNumber} start (seed {seed}, map attempts {layout.Attempts}{(layout.UsedFallback ? ", fallback" : "")})");
        BattleStarted?.Invoke();
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void BattleOver(int winner, byte[] scores, bool roundWon, bool matchWon)
    {
        ApplyScores(scores);
        string name = NameOf(winner);
        Color col = winner >= 0 ? Palette.Player(ColorOf(winner)) : Palette.TextDim;
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
        var r = new WireReader(data);
        PickOptions = new Dictionary<int, List<int>>();
        int n = r.U8();
        for (int i = 0; i < n; i++)
        {
            int slot = r.U8();
            int count = r.U8();
            var ids = new List<int>();
            for (int k = 0; k < count; k++) ids.Add(r.U8());
            PickOptions[slot] = ids;
        }
        Picked.Clear();
        Phase = Phase.CardPick;
        Bullets.Clear();
        CardPickBegan?.Invoke();
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void CardPicked(int slot, int cardId)
    {
        var p = Slots[slot];
        if (p == null) return;
        Picked.Add(slot);
        p.Cards.Add(cardId);
        p.RecomputeStats();
        Sfx.Play("pick", 0.6f);
        Log.Info($"{p.Info.Name} picked {CardRegistry.Get(cardId)?.Name}");
        CardWasPicked?.Invoke(slot, cardId);
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
        if (Slots[slot].Avatar != null) Slots[slot].Avatar.Alive = false;
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
        Bullets.Kill(id);
        Fx.Spark(pos, Palette.Text, impact ? 5 : 2);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Damaged(int slot, float newHp, Vector2 knockback, Vector2 at, float damage)
    {
        var a = Slots[slot]?.Avatar;
        if (a == null) return;
        if (!IsHost)
        {
            a.Hp = newHp;
            if (Slots[slot] == LocalMatchPlayer) a.Velocity += knockback; // predicted avatar feels the hit
        }
        a.OnHurtFx();
        var col = Palette.Player(ColorOf(slot));
        Fx.Burst(at, col, 8, 120, 0.4f);
        Fx.Popup(at + new Vector2(0, -8), Mathf.RoundToInt(damage).ToString(), Colors.White);
        Sfx.Play("hit");
        Shake(Slots[slot] == LocalMatchPlayer ? 0.45f : 0.2f);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Died(int slot, Vector2 at)
    {
        var a = Slots[slot]?.Avatar;
        if (a == null) return;
        a.Alive = false;
        a.CollisionLayer = 0;
        a.QueueRedraw();
        var col = Palette.Player(ColorOf(slot));
        Fx.Burst(at, col, 28, 220, 0.8f, 3);
        Fx.Burst(at, Colors.White, 10, 160, 0.4f, 2);
        Fx.RingFx(at, 4, 40, col, 0.4f);
        Sfx.Play("death");
        Shake(0.7f);
        HitStop(70);
        Log.Info($"{NameOf(slot)} died");
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void FxEvent(int kind, Vector2 pos, float value, int slot, Vector2 pos2)
    {
        var col = Palette.Player(ColorOf(slot));
        switch ((FxKind)kind)
        {
            case FxKind.Explosion:
                Fx.RingFx(pos, 2, value, col.Lightened(0.3f), 0.3f);
                Fx.Burst(pos, col, 16, value * 5, 0.5f, 2, 100);
                Fx.Burst(pos, Palette.Accent, 8, value * 3, 0.3f, 2, 0);
                Sfx.Play("explode");
                Shake(0.4f);
                break;
            case FxKind.Block:
                Fx.RingFx(pos, 6, 18, Colors.White, 0.25f);
                Sfx.Play("block", 0.7f);
                break;
            case FxKind.Blocked:
                Fx.Burst(pos, Colors.White, 8, 100, 0.3f, 1, 0);
                Fx.Popup(pos + new Vector2(0, -8), "BLOCK", Colors.White);
                Sfx.Play("blocked");
                break;
            case FxKind.Blink:
                Fx.Burst(pos, col, 12, 80, 0.4f, 2, 0);
                Fx.Burst(pos2, col, 12, 80, 0.4f, 2, 0);
                Sfx.Play("blink");
                break;
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
            if (a == null) continue;
            bool mine = Slots[slot] == LocalMatchPlayer;
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
    }

    // =====================================================================
    // RPCs: client → host
    // =====================================================================

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.UnreliableOrdered)]
    private void SubmitInput(byte[] data)
    {
        if (!IsHost) return;
        long sender = Multiplayer.GetRemoteSenderId();
        foreach (var p in Slots)
            if (p != null && !p.Info.IsBot && p.Info.PeerId == sender && p.Input is RemoteInput ri)
                ri.Latest = InputFrame.FromBytes(data);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestPick(int cardId)
    {
        if (!IsHost) return;
        long sender = Multiplayer.GetRemoteSenderId();
        var p = Slots.FirstOrDefault(s => s != null && !s.Info.IsBot && s.Info.PeerId == sender);
        if (p != null) HostApplyPick(p.Slot, cardId);
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
        return w.Done();
    }

    public string ScoreLine() => string.Join(", ", Active().Select(p => $"{p.Info.Name} R{p.RoundWins}/B{p.BattleWins}"));

    // =====================================================================
    // World reset & juice
    // =====================================================================

    public void ResetWorld()
    {
        // Leaving mid-match: keep whatever finished battles there were, flagged as incomplete.
        Stats.MatchEnd(-1, false, Slots.Where(p => p != null));
        foreach (var c in _avatars.GetChildren()) c.QueueFree();
        Bullets.Clear();
        Fx.Clear();
        MapNode.Build(null);
        Array.Clear(Slots);
        LocalMatchPlayer = null;
        PickOptions.Clear();
        Picked.Clear();
        Phase = Phase.Idle;
        _hostNext = null;
        Visible = false;
        Engine.TimeScale = 1;
    }

    public override void _Notification(int what)
    {
        if (what == NotificationWMCloseRequest) Stats.MatchEnd(-1, false, Slots.Where(p => p != null));
    }

    public void Shake(float amount) => _trauma = Mathf.Min(1, _trauma + amount);

    private void HitStop(int ms)
    {
        if (DisableHitStop) return;
        Engine.TimeScale = 0.05;
        _hitStopUntil = Time.GetTicksMsec() + (ulong)ms;
    }

    public override void _Process(double delta)
    {
        if (_hitStopUntil != 0 && Time.GetTicksMsec() >= _hitStopUntil)
        {
            Engine.TimeScale = 1;
            _hitStopUntil = 0;
        }
        _trauma = Mathf.Max(0, _trauma - (float)delta * 2.2f);
        float s = _trauma * _trauma * 6f;
        _cam.Offset = new Vector2((float)GD.RandRange(-s, s), (float)GD.RandRange(-s, s)).Round();
    }
}
