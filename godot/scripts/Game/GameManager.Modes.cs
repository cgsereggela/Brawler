using System;
using System.Collections.Generic;
using System.Linq;
using Brawler.Core;
using Brawler.Fx;
using Brawler.Logic;
using Godot;
using NVec = System.Numerics.Vector2;

namespace Brawler.Game;

/// <summary>
/// Game modes (see <see cref="GameModes"/>): respawns, the hill, the flags and the head. The host runs the rules and
/// sends what changes as <see cref="ObjectiveEvent"/>s (banners, logs) and in each snapshot (the hill's and the flags'
/// state). Where the hill is, where the flags sit and who the head is all follow from the battle seed, so every peer
/// works them out for itself in <see cref="SetupObjectives"/>.
/// </summary>
public partial class GameManager
{
    public GameMode Mode => Settings.Mode;

    /// <summary>Seconds of fighting before sudden-death lava rises in this battle's mode (0 = never).</summary>
    public int LavaAfter => Training ? 0 : GameModes.LavaAfter(Mode, Settings.LavaAfter);

    /// <summary>Whether the fallen come back: a respawn mode, before sudden death.</summary>
    public bool RespawnsNow => Training || GameModes.RespawnsNow(Mode, Arena.FightTime, Settings.LavaAfter);

    /// <summary>
    /// The training range (offline): one battle that never ends. The fallen come back, there is no lava, and the cards are
    /// set by hand (<see cref="SetTrainingCards"/>) rather than picked.
    /// </summary>
    public bool Training;

    /// <summary>Simulated seconds since the game started, on this peer.</summary>
    public float Clock => _clock;

    /// <summary>King of the Hill: this battle's zone, else null.</summary>
    public Hill Hill { get; private set; }
    /// <summary>Capture the Flag: this battle's flags, else null.</summary>
    public Flags Flags { get; private set; }
    /// <summary>Headhunter: the marked player this battle, else -1.</summary>
    public int HeadSlot { get; private set; } = -1;

    /// <summary>A flag's center sits this far above the ground it stands on (about a body's middle).</summary>
    public const float FlagLift = 11;

    private int _objectiveWinner = -1;           // host: the slot whose side has won the objective this battle
    private readonly float[] _respawnAt = Enumerable.Repeat(-1f, LobbyState.MaxPlayers).ToArray(); // Clock time, every peer
    private List<int>[] _sideSpawns;              // team modes: each team's half of the spawns
    private readonly int[] _hillSlot = new int[Hill.Sides]; // host: the last player of each side seen on the hill
    private int _hillAnnounced = -1;
    private float _hillBannerAt = -99;

    public enum ObjEvent { HillTaken, HillHeld, FlagTaken, FlagDropped, FlagReturned, FlagCaptured, HeadTaken }

    // =====================================================================
    // Queries
    // =====================================================================

    /// <summary>Who scores on the hill for this slot: their team, or themselves in free-for-all.</summary>
    public int HillSide(int slot) => TeamMode ? TeamOf(slot) : slot;

    public string SideName(int side) => TeamMode ? $"{Teams.Names[Mathf.Clamp(side, 0, 1)].ToUpperInvariant()} TEAM" : NameOf(side);

    public Color SideColor(int side) => TeamMode ? Palette.Team(side) : Palette.Player(ColorOf(side));

    /// <summary>Seconds until this slot respawns, or -1 if it won't.</summary>
    public float RespawnIn(int slot) =>
        slot < 0 || slot >= _respawnAt.Length || _respawnAt[slot] < 0 ? -1 : Mathf.Max(0, _respawnAt[slot] - _clock);

    /// <summary>The call at "FIGHT!": what this mode wants of you.</summary>
    private string FightCall()
    {
        int me = LocalMatchPlayer?.Slot ?? -1;
        return Mode switch
        {
            GameMode.KingOfTheHill => "TAKE THE HILL!",
            GameMode.CaptureTheFlag => "CAPTURE THE FLAG!",
            GameMode.Headhunter when HeadSlot >= 0 && HeadSlot == me => "YOU'RE THE HEAD: SURVIVE!",
            GameMode.Headhunter when HeadSlot >= 0 && Allies(HeadSlot, me) => $"PROTECT {NameOf(HeadSlot).ToUpperInvariant()}!",
            GameMode.Headhunter when HeadSlot >= 0 => $"HUNT {NameOf(HeadSlot).ToUpperInvariant()}!",
            GameMode.GunGame => $"GUN GAME: {GameModes.GunLadder.Length} KILLS TO WIN!",
            GameMode.Survival => $"WAVE {BattleNumber}: {WaveBots} BOT{(WaveBots == 1 ? "" : "S")}{(WaveRespawns > 0 ? $" + {WaveRespawns} RESPAWN{(WaveRespawns == 1 ? "" : "S")}" : "")}!",
            _ => "FIGHT!",
        };
    }

    /// <summary>
    /// Where a bot should be heading for the objective (a point at body height), or null to just fight. Bots go for
    /// the hill; in Capture the Flag a carrier runs home, and the rest chase down whoever has their flag, pick it
    /// up where it fell, or go for the enemy's.
    /// </summary>
    public Vector2? BotGoal(Avatar self)
    {
        if (Phase is not (Phase.Countdown or Phase.Fighting)) return null;
        int slot = self.Player.Slot;
        if (Hill != null)
            return new Vector2(Hill.CenterX + (slot % 3 - 1) * 12, Hill.Y - self.HalfSize.Y);
        if (Flags != null && TeamOf(slot) is >= 0 and var team)
        {
            var mine = Flags.Team[team];
            var theirs = Flags.Team[1 - team];
            if (theirs.Carrier == slot) return V(mine.Home);
            if (mine.Carrier >= 0) return AvatarOf(mine.Carrier)?.GlobalPosition;
            if (mine.Dropped) return V(mine.Pos);
            if (theirs.Carrier >= 0) return null; // a teammate has it: fight around them
            return V(theirs.Pos);
        }
        return null;
    }

    private static Vector2 V(NVec v) => new(v.X, v.Y);
    private static NVec N(Vector2 v) => new(v.X, v.Y);

    // =====================================================================
    // Every peer: battle setup, respawn timers
    // =====================================================================

    /// <summary>A body for <paramref name="p"/> standing at <paramref name="feet"/>, replacing any old one.</summary>
    private Avatar SpawnAvatar(MatchPlayer p, Vector2 feet, bool respawn = false)
    {
        if (p.Avatar != null && IsInstanceValid(p.Avatar)) p.Avatar.QueueFree();
        var av = new Avatar();
        av.Material = PaintShaders.Grain;
        av.Setup(p, this, Vector2.Zero);
        av.Position = new Vector2(feet.X, feet.Y - av.HalfSize.Y - 1);
        av.BornAt = _clock;
        av.Respawned = respawn;
        if (respawn) av.Frozen = Phase != Phase.Fighting;
        _avatars.AddChild(av);
        p.Avatar = av;
        return av;
    }

    /// <param name="byX">Spawn indexes ordered from Red's end of the arena to Blue's.</param>
    private void SetupObjectives(MapLayout layout, int seed, List<MatchPlayer> active, List<int> byX)
    {
        ClearObjectives();
        int n = byX.Count;
        _sideSpawns = TeamMode && n > 0 ? new[] { byX.Take((n + 1) / 2).ToList(), byX.Skip(n / 2).ToList() } : null;
        switch (Mode)
        {
            case GameMode.KingOfTheHill:
                Hill = GameModes.PickHill(layout);
                break;
            case GameMode.CaptureTheFlag when TeamMode && n > 0:
                var red = layout.Spawns[byX[0]];
                var blue = layout.Spawns[byX[n - 1]];
                Flags = new Flags(new NVec(red.X, red.Y - FlagLift), new NVec(blue.X, blue.Y - FlagLift));
                break;
            case GameMode.Headhunter:
                HeadSlot = GameModes.PickHead(active.Select(p => (p.Slot, TeamOf(p.Slot), p.RoundWins, p.BattleWins)).ToList(), seed);
                Log.Info($"headhunter: {NameOf(HeadSlot)} is the head");
                break;
        }
    }

    private void ClearObjectives()
    {
        Hill = null;
        Flags = null;
        HeadSlot = -1;
        _objectiveWinner = -1;
        _hillAnnounced = -1;
        Array.Fill(_respawnAt, -1f);
    }

    /// <summary>Survival: a bot of this wave comes back while the wave has respawns left, until sudden death.</summary>
    private bool WaveRespawnNow(int slot) => SurvivalRespawns && WaveRespawnsLeft > 0 && Slots[slot]?.Info.IsBot == true;

    private bool SurvivalRespawns => Mode == GameMode.Survival && RisingLava.Rising(Arena.FightTime, LavaAfter) <= 0;

    /// <summary>Died (every peer): start the respawn countdown, if this mode and moment allow one.</summary>
    private void NoteRespawn(int slot, KillCause cause)
    {
        // Not for someone who left: if they rejoin, they sit out the rest of the battle like everywhere else.
        bool back = (RespawnsNow || WaveRespawnNow(slot)) && Phase == Phase.Fighting && cause != KillCause.Left;
        if (back && Mode == GameMode.Survival) WaveRespawnsLeft--;
        _respawnAt[slot] = back ? _clock + GameModes.RespawnTime : -1;
    }

    /// <summary>Every peer: sudden death (or the battle ending) cancels respawns still counting down.</summary>
    private void TickRespawnTimers()
    {
        if ((RespawnsNow || SurvivalRespawns) && Phase == Phase.Fighting) return;
        for (int i = 0; i < _respawnAt.Length; i++) _respawnAt[i] = -1;
    }

    // =====================================================================
    // Host: the rules
    // =====================================================================

    private void HostObjectivesTick(float dt)
    {
        if (_objectiveWinner >= 0) return;
        foreach (var p in Active().ToList())
            if (p.Avatar is { Alive: false } && _respawnAt[p.Slot] >= 0 && _clock >= _respawnAt[p.Slot])
                Rpc(MethodName.Respawned, p.Slot, RespawnSpot(p));

        if (Hill != null)
        {
            var inside = new List<int>();
            foreach (var a in LiveAvatars())
            {
                if (!Hill.Inside(N(a.GlobalPosition), N(a.HalfSize))) continue;
                int side = HillSide(a.Player.Slot);
                if (side < 0 || side >= Hill.Sides) continue;
                inside.Add(side);
                _hillSlot[side] = a.Player.Slot;
            }
            int won = Hill.Step(dt, inside);
            if (Hill.Holder >= 0 && Hill.Holder != _hillAnnounced)
            {
                _hillAnnounced = Hill.Holder;
                Rpc(MethodName.ObjectiveEvent, (int)ObjEvent.HillTaken, Hill.Holder, _hillSlot[Hill.Holder]);
            }
            if (won >= 0)
            {
                _objectiveWinner = _hillSlot[won];
                Rpc(MethodName.ObjectiveEvent, (int)ObjEvent.HillHeld, won, _objectiveWinner);
            }
        }

        if (Flags != null)
        {
            // A dropped flag the lava reaches goes home.
            for (int t = 0; t < 2; t++)
                if (Flags.Team[t].Dropped && Flags.Team[t].Pos.Y > Arena.LavaY)
                {
                    Flags.SendHome(t);
                    Rpc(MethodName.ObjectiveEvent, (int)ObjEvent.FlagReturned, t, -1);
                }
            var live = LiveAvatars().Select(a => new Runner(a.Player.Slot, a.Player.Info.Team, N(a.GlobalPosition), Mathf.Max(a.HalfSize.X, a.HalfSize.Y))).ToList();
            foreach (var (kind, team, slot) in Flags.Step(dt, live))
            {
                var ev = kind switch
                {
                    FlagEvent.Taken => ObjEvent.FlagTaken,
                    FlagEvent.Dropped => ObjEvent.FlagDropped,
                    FlagEvent.Returned => ObjEvent.FlagReturned,
                    _ => ObjEvent.FlagCaptured,
                };
                Rpc(MethodName.ObjectiveEvent, (int)ev, team, slot);
                if (kind == FlagEvent.Captured && _objectiveWinner < 0) _objectiveWinner = slot;
            }
        }
    }

    /// <summary>Host, as someone goes down: a carried flag drops, and in Headhunter the head's killer wins the battle.</summary>
    private void HostObjectiveKill(Avatar a, int killer, KillCause cause)
    {
        int slot = a.Player.Slot;
        if (Flags != null && Flags.CarriedBy(slot) >= 0)
        {
            // Lost in a pit, it goes straight home; anywhere else it lies on the ground under where they fell.
            NVec? at = cause == KillCause.Fall ? null : DropSpot(a.GlobalPosition);
            int t = Flags.Drop(slot, at);
            Rpc(MethodName.ObjectiveEvent, (int)(at == null ? ObjEvent.FlagReturned : ObjEvent.FlagDropped), t, at == null ? -1 : slot);
        }
        if (Mode == GameMode.Headhunter && slot == HeadSlot && killer >= 0 && !Allies(killer, slot) && Phase == Phase.Fighting && _objectiveWinner < 0)
        {
            _objectiveWinner = killer;
            Rpc(MethodName.ObjectiveEvent, (int)ObjEvent.HeadTaken, slot, killer);
        }
        // Gun Game: a kill moves the killer up the ladder, and one with its last card wins.
        if (Mode == GameMode.GunGame && killer >= 0 && killer != slot && Slots[killer] is { } k && Phase == Phase.Fighting && _objectiveWinner < 0)
        {
            int level = GunLevel(k) + 1;
            if (level >= GameModes.GunLadder.Length) _objectiveWinner = killer;
            else Rpc(MethodName.GunLevelUp, killer, level);
        }
    }

    /// <summary>Where a dropped flag comes to rest: on the ground under <paramref name="at"/>, or null if there's none (a pit, the lava).</summary>
    private NVec? DropSpot(Vector2 at)
    {
        float x = Mathf.Clamp(at.X, MapGenerator.Wall + 6, MapLayout.Width - MapGenerator.Wall - 6);
        float best = float.MaxValue;
        foreach (var s in Map.Surfaces)
            if (x >= s.X1 && x <= s.X2 && s.Y >= at.Y - 6 && s.Y < best) best = s.Y;
        foreach (var sl in Map.Slopes)
            if (x >= sl.X1 && x < sl.X2 && sl.YAt(x) is var y && y >= at.Y - 6 && y < best) best = y;
        if (best == float.MaxValue || best > Arena.LavaY) return null;
        return new NVec(x, best - FlagLift);
    }

    /// <summary>
    /// Host: where someone comes back. Their team's half of the spawns in team modes, any spawn in free-for-all: the
    /// one furthest from the nearest enemy (with a little randomness), and never one the lava has reached.
    /// </summary>
    private Vector2 RespawnSpot(MatchPlayer p)
    {
        var spawns = Map.Spawns;
        var pool = _sideSpawns != null ? _sideSpawns[Mathf.Clamp(p.Info.Team, 0, 1)] : Enumerable.Range(0, spawns.Count).ToList();
        float lava = Arena.LavaY;
        int best = pool[0];
        float bestD = float.MinValue;
        foreach (int i in pool)
        {
            var sp = new Vector2(spawns[i].X, spawns[i].Y);
            if (sp.Y > lava - 30) continue;
            float d = 2000;
            foreach (var a in LiveAvatars())
                if (a.Player != p && !Allies(a.Player.Slot, p.Slot)) d = Mathf.Min(d, a.GlobalPosition.DistanceTo(sp));
            d += HostRng.Range(0f, 60f);
            if (d > bestD) (bestD, best) = (d, i);
        }
        return new Vector2(spawns[best].X, spawns[best].Y);
    }

    // =====================================================================
    // RPCs: host → everyone
    // =====================================================================

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Respawned(int slot, Vector2 feet)
    {
        var p = Slots[slot];
        if (p == null || Map == null || Phase != Phase.Fighting) return;
        _respawnAt[slot] = -1;
        var av = SpawnAvatar(p, feet, respawn: true);
        if (IsHost)
            foreach (var e in p.Effects) e.OnBattleStart(av, this);
        var col = Palette.Player(p.Info.ColorIdx);
        Fx.RingFx(av.GlobalPosition, 26, 4, col.Lightened(0.3f), 0.35f, 2f);
        Fx.Emit(av.GlobalPosition, PKind.Star, col.Lightened(0.5f), 8, 90, 0.5f, 2.2f, -30);
        if (IsLocalHuman(p)) Sfx.Play("blink", 0.7f);
        Log.Info($"{p.Info.Name} respawned");
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ObjectiveEvent(int kind, int team, int slot)
    {
        string who = NameOf(slot).ToUpperInvariant();
        string flag = $"{Teams.Names[Mathf.Clamp(team, 0, 1)].ToUpperInvariant()} FLAG";
        Color flagCol = Palette.Team(team);
        switch ((ObjEvent)kind)
        {
            case ObjEvent.HillTaken:
                Log.Info($"hill: {SideName(team)} took it");
                // The hill can change hands every second in a scrum: only a banner now and then.
                if (_clock - _hillBannerAt < 3f) break;
                _hillBannerAt = _clock;
                Banner?.Invoke($"{SideName(team).ToUpperInvariant()} HAS THE HILL", SideColor(team), 0.9f);
                Sfx.Play("tick", 0.6f);
                break;
            case ObjEvent.HillHeld:
                Log.Info($"hill: {SideName(team)} held it for {GameModes.HoldTime:0}s");
                break;
            case ObjEvent.FlagTaken:
                Log.Info($"flag: {NameOf(slot)} took the {Teams.Names[team]} flag");
                Banner?.Invoke($"{who} HAS THE {flag}!", Palette.Team(1 - team), 1.1f);
                Sfx.Play("ping", 0.8f);
                break;
            case ObjEvent.FlagDropped:
                Log.Info($"flag: {NameOf(slot)} dropped the {Teams.Names[team]} flag");
                Banner?.Invoke($"{flag} DROPPED", flagCol, 0.9f);
                break;
            case ObjEvent.FlagReturned:
                Log.Info($"flag: the {Teams.Names[team]} flag went home{(slot >= 0 ? $" ({NameOf(slot)} returned it)" : "")}");
                Banner?.Invoke($"{flag} RETURNED", flagCol, 0.9f);
                Sfx.Play("pick", 0.6f);
                break;
            case ObjEvent.FlagCaptured:
                Log.Info($"flag: {NameOf(slot)} captured the {Teams.Names[team]} flag");
                Banner?.Invoke("CAPTURED!", Palette.Team(1 - team), 1f);
                Fx.ScreenFlash(Palette.Team(1 - team) with { A = 0.12f }, 0.2f);
                Shake(0.4f);
                break;
            case ObjEvent.HeadTaken:
                Log.Info($"headhunter: {NameOf(slot)} took {NameOf(team)}'s head");
                Banner?.Invoke($"{who} TOOK THE HEAD!", Palette.Player(ColorOf(slot)), 1.1f);
                break;
        }
    }

    /// <summary>Gun Game: how far up the ladder a player is (0 = its first card).</summary>
    public static int GunLevel(MatchPlayer p) => Math.Max(0, Array.IndexOf(GameModes.GunLadder, p.Cards.Count > 0 ? p.Cards[0] : -1));

    /// <summary>Gun Game: gives a player the ladder's card for <paramref name="level"/>, and only that.</summary>
    private static void SetGunLevel(MatchPlayer p, int level)
    {
        p.Cards.Clear();
        p.Cards.Add(GameModes.GunCard(level));
        p.RecomputeStats();
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void GunLevelUp(int slot, int level)
    {
        var p = Slots[slot];
        if (p == null) return;
        SetGunLevel(p, level);
        var card = CardRegistry.Get(GameModes.GunCard(level));
        if (p.Avatar is { Alive: true } av)
        {
            if (IsHost)
            {
                av.Ammo = av.S.Int(Stat.Ammo); // a fresh gun comes loaded
                foreach (var e in p.Effects) e.OnBattleStart(av, this);
            }
            Fx.RingFx(av.GlobalPosition, 22, 3, Brawler.UI.UiKit.CategoryColor(card.Category).Lightened(0.3f), 0.3f, 2f);
        }
        Log.Info($"gun game: {p.Info.Name} is on {card.Name} ({level + 1}/{GameModes.GunLadder.Length})");
        if (IsLocalHuman(p))
        {
            Banner?.Invoke(level == GameModes.GunLadder.Length - 1 ? $"LAST ONE: {card.Name.ToUpperInvariant()}!" : $"LEVEL {level + 1}: {card.Name.ToUpperInvariant()}", Palette.Accent, 1.1f);
            Sfx.Play("pick", 0.7f);
        }
        ScoresChanged?.Invoke();
    }

    // =====================================================================
    // Snapshot
    // =====================================================================

    private void WriteObjectives(Wire w)
    {
        if (Hill != null)
        {
            w.I8(Hill.Holder).Bool(Hill.Contested);
            foreach (float h in Hill.Held) w.U8(Mathf.Clamp(Mathf.RoundToInt(h / GameModes.HoldTime * 255), 0, 255));
        }
        if (Flags != null)
            foreach (var f in Flags.Team)
                w.I8(f.Carrier).Bool(f.Dropped).F(f.Pos.X).F(f.Pos.Y).U8(Mathf.Clamp((int)(f.DroppedT * 10), 0, 255));
    }

    private void ReadObjectives(WireReader r)
    {
        if (Hill != null)
        {
            int holder = r.I8();
            bool contested = r.Bool();
            var held = new float[Hill.Sides];
            for (int i = 0; i < held.Length; i++) held[i] = r.U8() / 255f * GameModes.HoldTime;
            Hill.Set(holder, contested, held);
        }
        if (Flags != null)
            foreach (var f in Flags.Team)
            {
                f.Carrier = r.I8();
                f.Dropped = r.Bool();
                f.Pos = new NVec(r.F(), r.F());
                f.DroppedT = r.U8() / 10f;
            }
    }

    // ---------- Survival ----------

    /// <summary>Survival: bots fighting in this wave.</summary>
    public int WaveBots { get; private set; }
    /// <summary>Survival: respawns this wave's bots have between them, and how many are left (every peer counts the deaths).</summary>
    public int WaveRespawns { get; private set; }
    public int WaveRespawnsLeft { get; private set; }

    /// <summary>
    /// Survival, on every peer at the start of a wave: which bots fight it (the rest sit it out, with no avatar), how well
    /// they play, and the random cards they bring. Seeded by the battle, so every peer deals the same.
    /// </summary>
    private void SetupWave(int seed)
    {
        var bots = Active().Where(p => p.Info.IsBot).OrderBy(p => p.Slot).ToList();
        int wave = BattleNumber;
        int humans = Active().Count(p => !p.Info.IsBot);
        WaveBots = GameModes.WaveSize(wave, humans, bots.Count);
        WaveRespawnsLeft = WaveRespawns = GameModes.WaveRespawns(wave, humans, bots.Count);
        var skill = GameModes.WaveSkill(wave);
        var rng = new Rng((ulong)(uint)seed + 991);
        for (int i = 0; i < bots.Count; i++)
        {
            var b = bots[i];
            b.Benched = i >= WaveBots;
            b.Cards.Clear();
            if (!b.Benched && GameModes.WaveCards(wave) > 0)
                b.Cards.AddRange(CardRegistry.RollOptions(rng, GameModes.WaveCards(wave), Settings.DisabledCards, Settings.RareChance / 100f) ?? new());
            b.RecomputeStats();
            b.Info.Skill = skill;
            if (IsHost) b.Input = new BotBrain(this, skill);
        }
    }
}
