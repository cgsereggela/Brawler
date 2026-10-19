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
    private float _clock;        // simulated seconds, for "recently hit" checks
    private float _zoomPunch;    // brief camera zoom on big kills

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
        Fx.ScreenRect = new Rect2(-40, -40, MapLayout.Width + 80, MapLayout.Height + 80);
        MapNode.Avatars = LiveAvatars;
        MapNode.PadFired += pos =>
        {
            Fx.Emit(pos, PKind.Streak, MapNode.Theme.Edge, 10, 220, 0.35f, 1.1f, 0, Vector2.Up, 0.9f);
            Fx.RingFx(pos, 4, 26, MapNode.Theme.Edge, 0.3f, 2);
            Sfx.Play("pad", 0.7f);
        };
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
        _clock += dt;
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
        ImpactFx(b.Pos, b.Owner, impact);
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
        if (attackerSlot >= 0 && attackerSlot != victim.Player.Slot)
        {
            victim.LastHitBy = attackerSlot;
            victim.LastHitAt = _clock;
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
        Rpc(MethodName.Damaged, victim.Player.Slot, victim.Hp, knockback, at, damage, attackerSlot);
        if (victim.Hp <= 0) HostKill(victim, KillCause.Shot, attackerSlot);
    }

    public enum KillCause { Shot, Fall, Left }

    public void HostKill(Avatar a, KillCause cause = KillCause.Shot, int killer = -1)
    {
        if (!a.Alive) return;
        if (cause != KillCause.Left) Stats.Death(a.Player.Slot, fell: cause == KillCause.Fall);
        a.Alive = false;
        if (killer == a.Player.Slot) killer = -1;
        Rpc(MethodName.Died, a.Player.Slot, a.GlobalPosition, killer, (int)cause);
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
        var feet = a.GlobalPosition + new Vector2(0, a.HalfSize.Y);
        Fx.Emit(feet, PKind.Smoke, new Color(1, 1, 1, 0.5f), 4, 50, 0.35f, 2.2f, 0, Vector2.Up, 2.6f, 5);
        if (a.Player == LocalMatchPlayer) Sfx.Play("jump", 0.6f);
    }

    private void OnShotFx(Bullet b)
    {
        var st = StyleOf(b.Owner);
        var col = Palette.Player(ColorOf(b.Owner));
        var dir = b.Vel.Normalized();
        Fx.Emit(b.Pos, PKind.Streak, col.Lightened(0.4f), 3 + st.Tier, 160, 0.16f, 0.8f, 0, dir, 0.9f);
        if (st.Tier >= 2) Fx.Flash(b.Pos, 8 + st.Tier * 2, col, 0.1f);
        if (st.Has(Flavor.Fire)) Fx.Emit(b.Pos, PKind.Ember, new Color("#ffa33b"), 2, 60, 0.4f, 1.2f, -40, dir, 1.2f);
        Sfx.Play("shoot", 0.7f);
        if (LocalMatchPlayer != null && b.Owner == LocalMatchPlayer.Slot) Shake(0.12f);
        if (b.Owner >= 0 && b.Owner < Slots.Length) Slots[b.Owner]?.Avatar?.OnFired();
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
        System.Array.Clear(_battleKills);
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
            if (Slots[slot] == LocalMatchPlayer) a.Velocity += knockback; // predicted avatar feels the hit
        }
        a.OnHurtFx();
        HitFx(slot, attacker, at, knockback, damage);
        Sfx.Play("hit");
        float tierShake = StyleOf(attacker).Tier * 0.04f;
        Shake((Slots[slot] == LocalMatchPlayer ? 0.45f : 0.2f) + tierShake);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Died(int slot, Vector2 at, int killer, int cause)
    {
        var a = Slots[slot]?.Avatar;
        if (a == null) return;
        a.Alive = false;
        a.CollisionLayer = 0;
        a.QueueRedraw();
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
            case FxKind.Blink:
                foreach (var p in new[] { pos, pos2 })
                {
                    Fx.Emit(p, PKind.Dot, col.Lightened(0.3f), 14, 110, 0.45f, 1.2f);
                    Fx.Emit(p, PKind.Star, Colors.White, 4, 60, 0.4f, 2.4f);
                    Fx.RingFx(p, 18, 2, col, 0.25f, 2f);
                }
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
        _cam.Offset = new Vector2((float)GD.RandRange(-s, s), (float)GD.RandRange(-s, s));
        _zoomPunch = Mathf.Max(0, _zoomPunch - (float)delta * 3f);
        _cam.Zoom = Vector2.One * (1f + 0.06f * _zoomPunch * _zoomPunch);
    }
}
