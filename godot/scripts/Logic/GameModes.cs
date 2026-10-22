using System;
using System.Collections.Generic;
using System.Linq;
using NVec = System.Numerics.Vector2;

namespace Brawler.Logic;

/// <summary>How a battle is won. Ids are on the wire and in the stats log, so never renumber them.</summary>
public enum GameMode { Elimination = 0, KingOfTheHill = 1, CaptureTheFlag = 2, Headhunter = 3, GunGame = 4, Survival = 5 }

/// <summary>
/// The game modes' rules that don't need the engine. A battle is still one point toward the round in every mode;
/// only what wins it changes:
/// <list type="bullet">
/// <item>Elimination: last player (or team) standing.</item>
/// <item>King of the Hill: hold the marked zone alone for <see cref="HoldTime"/> seconds in total. Players respawn.</item>
/// <item>Capture the Flag (team modes only): bring the other team's flag home while yours is there. Players respawn.</item>
/// <item>Headhunter: elimination, but one player, the match leader, is marked, and whoever takes them out wins the battle on the spot.</item>
/// <item>Gun Game (free-for-all only): everyone holds just one card, the first of <see cref="GunLadder"/>; each kill swaps yours for the next. A kill with the last card wins the whole match. Players respawn, and there is no lava.</item>
/// <item>Survival (free-for-all lobbies with bots): the humans team up against the bots, one wave per battle. Each wave brings one more bot (up to the lobby's), tougher and with more cards (see <see cref="WaveSize"/>, <see cref="WaveSkill"/>, <see cref="WaveCards"/>). Clear <see cref="SurvivalWaves"/> waves to win; if every human falls, the run is over. No respawns.</item>
/// </list>
/// In the respawn modes (but Gun Game, which has none), sudden death starts twice as late as the lobby's lava setting says, and from then on nobody
/// respawns, so a stalled battle still ends by elimination.
/// </summary>
public static class GameModes
{
    public static readonly GameMode[] All = { GameMode.Elimination, GameMode.KingOfTheHill, GameMode.CaptureTheFlag, GameMode.Headhunter, GameMode.GunGame, GameMode.Survival };

    /// <summary>Gun Game: the cards in order (Minigun, Shotgun, Explosive, Bouncy, Homing, Sniper, Boomerang, One Shot). Card ids, so never reorder lightly.</summary>
    public static readonly int[] GunLadder = { 31, 10, 6, 1, 5, 30, 57, 65 };

    /// <summary>Seconds a side must hold the hill, in total, to win the battle.</summary>
    public const float HoldTime = 10f;
    /// <summary>Seconds between dying and coming back in the respawn modes.</summary>
    public const float RespawnTime = 3f;
    /// <summary>Seconds a dropped flag lies before it goes home by itself.</summary>
    public const float FlagReturnTime = 12f;
    /// <summary>Seconds a respawned player can't be hurt, so nobody is shot as they appear.</summary>
    public const float SpawnGuard = 1.2f;

    public static string Name(GameMode m) => m switch
    {
        GameMode.KingOfTheHill => "King of the Hill",
        GameMode.CaptureTheFlag => "Capture the Flag",
        GameMode.Headhunter => "Headhunter",
        GameMode.GunGame => "Gun Game",
        GameMode.Survival => "Survival",
        _ => "Elimination",
    };

    /// <summary>Short id for command lines and logs.</summary>
    public static string Id(GameMode m) => m switch
    {
        GameMode.KingOfTheHill => "koth",
        GameMode.CaptureTheFlag => "ctf",
        GameMode.Headhunter => "headhunter",
        GameMode.GunGame => "gungame",
        GameMode.Survival => "survival",
        _ => "elimination",
    };

    public static GameMode? Parse(string s) => All.Cast<GameMode?>().FirstOrDefault(m => Id(m.Value) == s?.ToLowerInvariant());

    /// <summary>A received or stored value that isn't a mode falls back to elimination.</summary>
    public static GameMode From(int v) => Enum.IsDefined(typeof(GameMode), v) ? (GameMode)v : GameMode.Elimination;

    public static GameMode Next(GameMode m) => All[(Array.IndexOf(All, m) + 1) % All.Length];

    public static bool Respawns(GameMode m) => m is GameMode.KingOfTheHill or GameMode.CaptureTheFlag or GameMode.GunGame;

    /// <summary>Why a lobby can't start this mode with this team size, or null.</summary>
    public static string Problem(GameMode m, int teamSize) =>
        m == GameMode.CaptureTheFlag && teamSize <= 0 ? "Capture the Flag needs teams: pick 2v2, 3v3 or 4v4."
        : m == GameMode.GunGame && teamSize > 0 ? "Gun Game is every player for themselves: pick Free-for-all."
        : m == GameMode.Survival && teamSize > 0 ? "Survival teams the humans up against the bots by itself: pick Free-for-all." : null;

    /// <summary>Survival: waves to clear to win.</summary>
    public const int SurvivalWaves = 10;

    /// <summary>Survival: bots in <paramref name="wave"/> (from 1): as many as there are humans, then one more every second wave, up to the lobby's <paramref name="bots"/>.</summary>
    public static int WaveSize(int wave, int humans, int bots) => Math.Clamp(Math.Max(1, humans) + (wave - 1) / 2, Math.Min(1, bots), bots);

    /// <summary>Survival: how well the bots in <paramref name="wave"/> play. Easy for four waves, then Normal for three, then Hard.</summary>
    public static BotSkill WaveSkill(int wave) => wave <= 4 ? BotSkill.Easy : wave <= 7 ? BotSkill.Normal : BotSkill.Hard;

    /// <summary>Survival: random cards each bot in <paramref name="wave"/> brings: none in the first two, then one more every second wave (the humans pick one every wave).</summary>
    public static int WaveCards(int wave) => Math.Clamp((wave - 1) / 2, 0, SurvivalWaves);

    /// <summary>Gun Game: the card a player holds at <paramref name="level"/> kills (the last one stays once the ladder is done).</summary>
    public static int GunCard(int level) => GunLadder[Math.Clamp(level, 0, GunLadder.Length - 1)];

    /// <summary>When sudden-death lava starts in this mode, from the lobby setting (0 = never).</summary>
    public static int LavaAfter(GameMode m, int lavaAfter) => m == GameMode.GunGame ? 0 : Respawns(m) && lavaAfter > 0 ? lavaAfter * 2 : lavaAfter;

    /// <summary>Whether someone who dies now comes back: a respawn mode, before sudden death.</summary>
    public static bool RespawnsNow(GameMode m, float fightTime, int lavaAfter) =>
        Respawns(m) && RisingLava.Rising(fightTime, LavaAfter(m, lavaAfter)) <= 0;

    /// <summary>
    /// The hill for King of the Hill: the widest-enough surface nearest the middle of the arena that the first spawn
    /// can reach and get back from, so nobody spawns somewhere the hill can't be fought over from.
    /// </summary>
    public static Hill PickHill(MapLayout map)
    {
        int home = map.Spawns.Count > 0 ? SurfaceAt(map, map.Spawns[0].X, map.Spawns[0].Y) : -1;
        var reach = home >= 0 ? MapGenerator.ReachableFrom(map, home) : null;
        int best = -1;
        float bestScore = float.MaxValue;
        for (int i = 0; i < map.Surfaces.Count; i++)
        {
            var s = map.Surfaces[i];
            if (s.Width < Hill.MinWidth) continue;
            if (reach != null && (!reach[i] || !MapGenerator.ReachableFrom(map, i)[home])) continue;
            float score = Math.Abs(s.CenterX - MapLayout.Width / 2f) + Math.Abs(s.Y - MapLayout.Height * 0.5f) * 0.7f;
            if (score < bestScore) (bestScore, best) = (score, i);
        }
        if (best < 0) return new Hill(MapLayout.Width / 2 - Hill.MaxWidth / 2, MapLayout.Width / 2 + Hill.MaxWidth / 2, MapGenerator.FloorY);
        var h = map.Surfaces[best];
        int half = Math.Min(h.Width, Hill.MaxWidth) / 2;
        return new Hill(h.CenterX - half, h.CenterX + half, h.Y);
    }

    private static int SurfaceAt(MapLayout map, int x, int y)
    {
        int best = -1;
        for (int i = 0; i < map.Surfaces.Count; i++)
        {
            var s = map.Surfaces[i];
            if (x < s.X1 - 4 || x > s.X2 + 4 || s.Y < y - 4) continue;
            if (best < 0 || s.Y < map.Surfaces[best].Y) best = i;
        }
        return best;
    }

    /// <summary>
    /// Headhunter: who is marked this battle. The leader: most round wins, then battle wins. In team modes the leading
    /// team is found the same way (teammates share a score) and one of its players is marked. Ties, like the first
    /// battle, are broken by the battle seed, so every peer picks the same head without syncing it.
    /// </summary>
    /// <param name="players">Everyone playing this battle: slot, team (-1 outside team modes), round and battle wins.</param>
    public static int PickHead(IReadOnlyList<(int Slot, int Team, int Rounds, int Battles)> players, int seed)
    {
        if (players.Count == 0) return -1;
        var rng = new Rng((ulong)(uint)seed + 991);
        int top = players.Max(p => p.Rounds * 100 + p.Battles);
        var leaders = players.Where(p => p.Rounds * 100 + p.Battles == top).OrderBy(p => p.Slot).ToList();
        if (players[0].Team >= 0)
        {
            var teams = leaders.Select(p => p.Team).Distinct().OrderBy(t => t).ToList();
            int team = teams[rng.Range(0, teams.Count - 1)];
            leaders = players.Where(p => p.Team == team).OrderBy(p => p.Slot).ToList();
        }
        return leaders[rng.Range(0, leaders.Count - 1)].Slot;
    }
}

/// <summary>
/// King of the Hill's zone: a stretch of one surface and the air just above it. A side (a player, or a team in team
/// modes) holds it while it has someone inside and nobody else does; held time adds up and never drains.
/// </summary>
public sealed class Hill
{
    public const int MinWidth = 40, MaxWidth = 110, Tall = 56;
    public readonly int X1, X2, Y;
    /// <summary>Seconds held so far, by side.</summary>
    public readonly float[] Held = new float[Sides];
    /// <summary>The side holding it right now, or -1 (empty or contested).</summary>
    public int Holder { get; private set; } = -1;
    public bool Contested { get; private set; }

    /// <summary>Room for every side: a slot each in free-for-all (up to 8), or a team.</summary>
    public const int Sides = 8;

    public Hill(int x1, int x2, int y) => (X1, X2, Y) = (x1, x2, y);

    public float CenterX => (X1 + X2) / 2f;

    /// <summary>Whether a body (center and half size) stands in the zone: overlapping it, with its feet not below the surface.</summary>
    public bool Inside(NVec c, NVec half) =>
        c.X + half.X > X1 && c.X - half.X < X2 && c.Y + half.Y > Y - Tall && c.Y + half.Y <= Y + 3;

    /// <summary>One tick: credits the side alone in the zone. Returns the side that has now held it long enough, or -1.</summary>
    /// <param name="sidesInside">The side of each live player standing in the zone (repeats are fine).</param>
    public int Step(float dt, IEnumerable<int> sidesInside)
    {
        var sides = sidesInside.Distinct().ToList();
        Contested = sides.Count > 1;
        Holder = sides.Count == 1 ? sides[0] : -1;
        if (Holder < 0 || Holder >= Held.Length) return -1;
        Held[Holder] = MathF.Min(GameModes.HoldTime, Held[Holder] + dt);
        return Held[Holder] >= GameModes.HoldTime ? Holder : -1;
    }

    /// <summary>Client: take the host's view of the zone.</summary>
    public void Set(int holder, bool contested, float[] held)
    {
        Holder = holder;
        Contested = contested;
        Array.Copy(held, Held, Math.Min(held.Length, Held.Length));
    }
}

public enum FlagEvent { Taken, Dropped, Returned, Captured }

/// <summary>A live player as the flags see them.</summary>
public readonly record struct Runner(int Slot, int Team, NVec Pos, float Reach);

/// <summary>
/// Capture the Flag's two flags. Each team's sits at its base: an enemy who touches it takes it, and dropping it
/// (the carrier dies) leaves it where it fell, until a defender touches it or <see cref="GameModes.FlagReturnTime"/>
/// passes, and it goes home. Carrying the enemy flag to your own base while your flag is there scores.
/// </summary>
public sealed class Flags
{
    public const float Grab = 12f, BaseReach = 22f;

    public sealed class Flag
    {
        public NVec Home, Pos;
        public int Carrier = -1;
        public bool Dropped;
        public float DroppedT;
        public bool AtHome => Carrier < 0 && !Dropped;
    }

    public readonly Flag[] Team = { new(), new() };

    public Flags(NVec redHome, NVec blueHome)
    {
        Team[0].Home = Team[0].Pos = redHome;
        Team[1].Home = Team[1].Pos = blueHome;
    }

    /// <summary>The flag this slot carries (0 or 1), or -1.</summary>
    public int CarriedBy(int slot) => Team[0].Carrier == slot ? 0 : Team[1].Carrier == slot ? 1 : -1;

    /// <summary>One tick of pickups, returns and captures. Returns what happened, as (event, flag's team, slot).</summary>
    public List<(FlagEvent Kind, int Team, int Slot)> Step(float dt, IReadOnlyList<Runner> live)
    {
        var events = new List<(FlagEvent, int, int)>();
        for (int t = 0; t < 2; t++)
        {
            var f = Team[t];
            if (f.Carrier >= 0)
            {
                int i = IndexOf(live, f.Carrier);
                if (i < 0) continue; // the carrier's death drops it (see Drop)
                var r = live[i];
                f.Pos = r.Pos;
                var home = Team[r.Team];
                if (home.AtHome && NVec.Distance(r.Pos, home.Home) < BaseReach + r.Reach)
                {
                    events.Add((FlagEvent.Captured, t, r.Slot));
                    Reset(f);
                }
                continue;
            }
            if (f.Dropped)
            {
                f.DroppedT += dt;
                if (f.DroppedT >= GameModes.FlagReturnTime)
                {
                    Reset(f);
                    events.Add((FlagEvent.Returned, t, -1));
                    continue;
                }
            }
            foreach (var r in live)
            {
                if (NVec.Distance(r.Pos, f.Pos) > Grab + r.Reach) continue;
                if (r.Team == t)
                {
                    if (!f.Dropped) continue;
                    Reset(f);
                    events.Add((FlagEvent.Returned, t, r.Slot));
                }
                else
                {
                    if (CarriedBy(r.Slot) >= 0) continue;
                    f.Carrier = r.Slot;
                    f.Dropped = false;
                    events.Add((FlagEvent.Taken, t, r.Slot));
                }
                break;
            }
        }
        return events;
    }

    /// <summary>
    /// The carrier is down: their flag drops at <paramref name="at"/>, or goes straight home when
    /// <paramref name="at"/> is null (lost in a pit). Returns the flag's team, or -1 if they carried none.
    /// </summary>
    public int Drop(int slot, NVec? at)
    {
        int t = CarriedBy(slot);
        if (t < 0) return -1;
        var f = Team[t];
        f.Carrier = -1;
        if (at is { } p)
        {
            f.Pos = p;
            f.Dropped = true;
            f.DroppedT = 0;
        }
        else Reset(f);
        return t;
    }

    /// <summary>Puts a team's flag back at its base (it fell somewhere it can't be reached).</summary>
    public void SendHome(int team) => Reset(Team[team]);

    private static void Reset(Flag f)
    {
        f.Carrier = -1;
        f.Dropped = false;
        f.DroppedT = 0;
        f.Pos = f.Home;
    }

    private static int IndexOf(IReadOnlyList<Runner> live, int slot)
    {
        for (int i = 0; i < live.Count; i++)
            if (live[i].Slot == slot) return i;
        return -1;
    }
}
