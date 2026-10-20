using System.Collections.Generic;
using Brawler.Core;
using Brawler.Logic;
using Godot;
using NVec = System.Numerics.Vector2;

namespace Brawler.Game;

/// <summary>
/// Simple but scrappy AI: keeps a fighting distance from the nearest enemy, leads its shots
/// (compensating for bullet gravity), jumps over gaps and walls, and blocks incoming bullets.
/// It only reads world state, so it works the same on the host or driving a client.
/// </summary>
public sealed class BotBrain : IInputSource
{
    private readonly GameManager _game;
    private readonly RandomNumberGenerator _rng = new();
    private byte _jumps, _blocks;
    private float _jumpHold, _decisionT, _strafeDir = 1, _aimWobbleT;
    private Vector2 _aimWobble;
    private float _preferredDist;
    private readonly HashSet<int> _consideredBullets = new();

    public BotBrain(GameManager game)
    {
        _game = game;
        _preferredDist = _rng.RandfRange(140, 240);
    }

    public InputFrame Next(Avatar self, float dt)
    {
        var f = new InputFrame { JumpPresses = _jumps, BlockPresses = _blocks, Aim = self.GlobalPosition + Vector2.Right };
        var target = NearestEnemy(self);
        _jumpHold -= dt;
        _decisionT -= dt;
        _aimWobbleT -= dt;

        if (target == null)
        {
            f.JumpHeld = _jumpHold > 0;
            return f;
        }

        var me = self.GlobalPosition;
        var to = target.GlobalPosition - me;
        float dist = to.Length();

        // --- aim with lead and drop compensation ---
        float speed = self.S[Stat.BulletSpeed];
        float t = dist / speed;
        var predicted = target.GlobalPosition + target.Velocity * t * 0.8f;
        predicted.Y -= 0.5f * self.S[Stat.BulletGravity] * t * t;
        if (_aimWobbleT <= 0)
        {
            _aimWobbleT = 0.3f;
            _aimWobble = new Vector2(_rng.RandfRange(-1, 1), _rng.RandfRange(-1, 1)) * (6 + dist * 0.06f);
        }
        f.Aim = predicted + _aimWobble;

        bool los = HasLineOfSight(me, target.GlobalPosition);
        f.Shoot = los && dist < 520 && (self.Ammo > 1 || dist < 180 || _rng.Randf() < 0.05f);

        // --- movement ---
        if (_decisionT <= 0)
        {
            _decisionT = _rng.RandfRange(0.4f, 1.1f);
            _strafeDir = _rng.Randf() < 0.5f ? -1 : 1;
        }
        float move;
        // Leech Field only works up close, so its holder hugs the target instead of keeping range.
        bool hug = self.S[Stat.Leech] > 0;
        float pref = hug ? 20 : _preferredDist, band = hug ? 15 : 50;
        if (dist > pref + band) move = Mathf.Sign(to.X);
        else if (dist < pref - band) move = -Mathf.Sign(to.X);
        else move = _strafeDir * 0.6f;
        if (!los) move = Mathf.Sign(to.X == 0 ? 1 : to.X);

        bool wantJump = false;
        bool onFloor = self.IsOnFloor();

        // --- navigation: out of sight, or far above/below → follow the jump graph one surface at a time ---
        var map = NavMap(self);
        var feet = me + new Vector2(0, self.HalfSize.Y);
        int here = onFloor ? SurfaceAt(map, feet, 4) : -1;
        // On a ramp: plan from the surface at its nearer end.
        if (onFloor && here < 0) here = MapGenerator.SlopeSurface(map, feet.X, feet.Y);
        // On a crate, barrel or mover: plan from the surface it stands on. Otherwise a bot on a crate stack kept the
        // surface it jumped from and hopped on the stack forever, "climbing" to a ledge it was already above.
        if (onFloor && here < 0) here = SurfaceUnder(map, feet);
        if (here >= 0 && here != _surface) { _surface = here; _navT = 0; }
        // Long in the air (wall-jumping, gliding, jetting) means the surface we last stood on says little about
        // where we are; plan from where we'd land instead, or stale plans can loop forever without touching down.
        _airT = onFloor ? 0 : _airT + dt;
        if (_airT > 1f && SurfaceUnder(map, feet) is int below and >= 0 && below != _surface) { _surface = below; _navT = 0; }
        _navT -= dt;
        bool navigating = false, dropping = false;
        if ((!los || Mathf.Abs(to.Y) > 75) && _surface >= 0)
        {
            if (_navT <= 0)
            {
                _navT = 0.3f;
                int goal = SurfaceUnder(map, target.GlobalPosition + new Vector2(0, target.HalfSize.Y));
                _hop = goal >= 0 ? MapGenerator.NextHop(map, _surface, goal) : -1;
                // Can't get to the target's surface (it's on a ledge with no way up from here): head for the
                // reachable surface with the best shot at it instead of milling about underneath.
                if (_hop < 0) _hop = MapGenerator.NextHop(map, _surface, VantageSurface(map, self, target));
            }
            navigating = _hop >= 0 && _hop != _surface;
        }
        if (navigating)
        {
            var hop = map.Surfaces[_hop];
            var cur = map.Surfaces[_surface];
            float x = me.X, aimX;
            if (MapGenerator.SlopeLinks(map, cur, hop)) aimX = Inside(x, hop, 10); // a ramp leads there: just run
            else if (hop.Y < cur.Y - 4)
            {
                // Climbing: get clear of the platform's underside, then jump toward it and steer onto it.
                bool overlap = cur.X2 > hop.X1 && cur.X1 < hop.X2;
                float edge = x < hop.X1 ? hop.X1 - x : x > hop.X2 ? x - hop.X2 : 0;
                if (!onFloor)
                {
                    // Until our feet are above its top, stay out from under it: rising into the underside
                    // kills the jump, and bots used to bonk their heads on the same ledge forever.
                    aimX = Inside(x, hop, 12);
                    float hw = self.HalfSize.X + 2;
                    if (feet.Y > hop.Y - 1 && UnderPlatform(map, hop, me, self.HalfSize))
                    {
                        bool leftFree = SideClear(map, hop, false, self.HalfSize), rightFree = SideClear(map, hop, true, self.HalfSize);
                        bool left = leftFree && rightFree ? x - hop.X1 < hop.X2 - x : leftFree;
                        aimX = left ? hop.X1 - hw : hop.X2 + hw;
                    }
                    // Beside it but not yet up to its top: wait just off its end. Steering into the side made a bot
                    // that launched next to a ledge touch its end, wall-jump away from it and try again forever.
                    else if (feet.Y > hop.Y - 1 && (x < hop.X1 || x > hop.X2))
                        aimX = x < hop.X1 ? hop.X1 - hw : hop.X2 + hw;
                }
                else if (!MapGenerator.CanTraverse(cur, hop) && MapGenerator.PadTo(map, cur, hop) is JumpPad pad)
                    aimX = (pad.X1 + pad.X2) * 0.5f; // too high to jump: ride the pad (it launches us on contact)
                else if (overlap)
                {
                    // Walk to a spot on this surface that's clear of the platform above, then jump.
                    float launch = MapGenerator.LaunchX(map, cur, hop, x);
                    aimX = float.IsNaN(launch) ? x : launch;
                    if (Mathf.Abs(aimX - x) < 5) wantJump = true;
                }
                else
                {
                    aimX = Inside(x, hop, 10);
                    if (edge < 44) wantJump = true;
                }
            }
            else if (hop.Y > cur.Y + 4 && cur.X2 > hop.X1 && cur.X1 < hop.X2 && feet.Y <= cur.Y + 2 && (onFloor || (x >= cur.X1 && x <= cur.X2)))
            {
                // Dropping onto something under this surface: walk off an edge it sticks out past (or the nearest),
                // but never one with no room to fall past, like a ledge ending a few px short of the border wall.
                // Also while airborne above this surface: steering straight down would just land back on it.
                dropping = true;
                bool leftOpen = SideClear(map, cur, false, self.HalfSize), rightOpen = SideClear(map, cur, true, self.HalfSize);
                bool leftOk = hop.X1 < cur.X1 - 4 && leftOpen, rightOk = hop.X2 > cur.X2 + 4 && rightOpen;
                if (!leftOk && !rightOk) { leftOk = leftOpen; rightOk = rightOpen; }
                bool goLeft = leftOk && rightOk ? x - cur.X1 < cur.X2 - x : leftOk || (!rightOk && x - cur.X1 < cur.X2 - x);
                aimX = goLeft ? cur.X1 - 10 : cur.X2 + 10;
            }
            else aimX = Inside(x, hop, 10); // same level, or already falling: steer onto it
            move = Mathf.Abs(aimX - x) < 3 ? 0 : Mathf.Sign(aimX - x);
        }

        // Unstick: if we haven't really moved for a while and can't see anyone, wander and hop for a bit.
        _stuckT += dt;
        if (me.DistanceTo(_stuckFrom) > 24) { _stuckFrom = me; _stuckT = 0; }
        if (_stuckT > 2.5f && !los && _wanderT <= 0)
        {
            _wanderT = _rng.RandfRange(1f, 2f);
            _wanderDir = _rng.Randf() < 0.5f ? -1 : 1;
            _stuckT = 0;
        }
        if (_wanderT > 0)
        {
            _wanderT -= dt;
            move = _wanderDir;
            if (onFloor && _rng.Randf() < 0.05f) wantJump = true;
        }

        bool climbing = navigating && map.Surfaces[_hop].Y < feet.Y - 4, descending = navigating && map.Surfaces[_hop].Y > feet.Y + 4;
        if (onFloor && !navigating && to.Y < -38 && Mathf.Abs(to.X) < 175) wantJump = true; // target above
        // Wall ahead: jump it / wall-jump up it, but not on the way down, where kicking off a ledge's side just
        // bounces us back up next to it (bots wall-jumped against the same 8 px ledge end for minutes).
        if (self.IsOnWall() && move * self.GetWallNormal().X < 0 && !descending) wantJump = true;
        if (onFloor && !dropping && !GroundAhead(self, move)) wantJump = true;        // edge or pit ahead
        if (onFloor && _game.Arena.DangerAhead(self, move))
        {
            // A pool or sawblade in the way (or underfoot): hop it, and keep going rather than stopping in it.
            wantJump = true;
            if (Mathf.Abs(move) < 0.1f) move = _strafeDir;
        }
        if (!navigating && !onFloor && self.Velocity.Y > 60 && !GroundBelow(self, 175)) move = -move; // falling into a pit: steer back
        if (onFloor && _rng.Randf() < 0.008f) wantJump = true;

        // Card abilities: spend air jumps or jetpack fuel to climb toward the target or out of a fall,
        // and glide over pits. Without this, movement cards would only measure how well bots ignore them.
        bool jet = self.S[Stat.Jetpack] > 0;
        // Only a real pit (nothing below at all) counts as a fall to save: a jetpack bot that "saved" itself from
        // every long drop could never get down off a high ledge, and hovered over it all battle.
        bool pit = !onFloor && !GroundBelow(self, MapLayout.Height);
        // A bot that walked or was knocked off a ledge still has its ground jump (Avatar.MidAirJumpsLeft counts it).
        if (!onFloor && (self.MidAirJumpsLeft > 0 || jet) && self.Velocity.Y > 20 && ((!descending && (to.Y < -50 || climbing)) || pit))
            wantJump = true;
        bool glide = self.S[Stat.Glide] > 0 && !onFloor && self.Velocity.Y > 0 && ((!descending && to.Y < -25) || pit);

        if (wantJump && _jumpHold <= -0.15f)
        {
            _jumps++;
            _jumpHold = jet && !onFloor ? _rng.RandfRange(0.5f, 0.8f) : _rng.RandfRange(0.15f, 0.35f);
        }
        f.MoveX = Mathf.Clamp(move, -1, 1);
        // Climbing: hold jump all the way up. Letting go early cuts the rise (the short-hop rule), which made bots
        // miss ledges and turned a jump pad's ~215 px launch into ~100, so pocketed bots never got out.
        // Same for a jump across a gap to a surface at our level: a short hop falls short of the far side.
        bool rising = navigating && !descending && !onFloor && self.Velocity.Y < 0;
        f.JumpHeld = _jumpHold > 0 || glide || rising;
        f.JumpPresses = _jumps;

        // --- block incoming bullets ---
        if (self.BlockReady && ThreatIncoming(self, out int bulletId) && _consideredBullets.Add(bulletId))
        {
            if (_rng.Randf() < 0.55f) _blocks++;
        }
        _trailT -= dt;
        if (_trailT <= 0)
        {
            _trailT = 0.25f;
            _trail.Enqueue($"({me.X:0},{me.Y:0}){(onFloor ? "F" : "")}{(navigating ? "N" + _hop : "")}{(los ? "L" : "")}m{f.MoveX:0.#}{(wantJump ? "J" : "")}");
            if (_trail.Count > 16) _trail.Dequeue();
        }
        f.BlockPresses = _blocks;
        return f;
    }

    private Avatar NearestEnemy(Avatar self)
    {
        Avatar best = null;
        float bestD = float.MaxValue;
        foreach (var a in _game.LiveAvatars())
        {
            if (a == self) continue;
            float d = a.GlobalPosition.DistanceSquaredTo(self.GlobalPosition);
            if (d < bestD) { bestD = d; best = a; }
        }
        return best;
    }

    private MapLayout _navSource, _navMap;
    private Vector2 _navSize;

    /// <summary>The jump graph for our current body size (rebuilt when the arena or our size changes).</summary>
    private MapLayout NavMap(Avatar self)
    {
        var size = self.HalfSize * 2;
        if (_game.Map != _navSource || size != _navSize)
        {
            _navSource = _game.Map;
            _navSize = size;
            _navMap = MapGenerator.NavLayout(_game.Map, size.X, size.Y);
            _surface = -1;
            _hop = -1;
            _vantage = -1;
        }
        return _navMap;
    }

    private int _surface = -1, _hop = -1, _vantage = -1;
    private float _navT, _stuckT, _wanderT, _wanderDir = 1, _airT;
    private Vector2 _stuckFrom;

    public string DebugState => $"surface {_surface} hop {_hop} stuck {_stuckT:0.0}s wander {_wanderT:0.0}s trail {string.Join(" ", _trail)}";
    private readonly Queue<string> _trail = new();
    private float _trailT;

    /// <summary><paramref name="x"/> clamped into a surface, at most <paramref name="inset"/> px in from its ends.</summary>
    private static float Inside(float x, Surface s, float inset)
    {
        inset = Mathf.Min(inset, s.Width * 0.5f);
        return Mathf.Clamp(x, s.X1 + inset, s.X2 - inset);
    }

    /// <summary>
    /// True if a body of <paramref name="half"/> size fits right beside one end of surface <paramref name="s"/>,
    /// at its height: room to walk off that end, or to rise past it and step on.
    /// </summary>
    private static bool SideClear(MapLayout map, Surface s, bool right, Vector2 half)
    {
        float w = half.X * 2 + 2, x1 = right ? s.X2 : s.X1 - w, y1 = s.Y - half.Y * 2 - 2, y2 = s.Y + 6;
        foreach (var r in map.Solids)
            if (r.X < x1 + w && x1 < r.Right && r.Y < y2 && y1 < r.Bottom) return false;
        foreach (var sl in map.Slopes)
            if (sl.Overlaps(x1, y1, x1 + w, y2)) return false;
        return true;
    }

    /// <summary>
    /// True if the solid whose top is surface <paramref name="s"/> hangs over our head (its underside above us, and
    /// overlapping us sideways), so jumping straight up would hit it. Beside a tower, which reaches down to the
    /// floor, this is false: rising along its side and stepping on is fine.
    /// </summary>
    private static bool UnderPlatform(MapLayout map, Surface s, Vector2 me, Vector2 half)
    {
        foreach (var r in map.Solids)
            if (r.Y == s.Y && r.X < me.X + half.X + 2 && r.Right > me.X - half.X - 2 && r.Bottom <= me.Y - half.Y + 2
                && r.X < s.X2 && r.Right > s.X1)
                return true;
        return false;
    }

    /// <summary>
    /// The surface reachable from where we stand that's best for shooting at <paramref name="target"/>: in line
    /// of sight if possible, then closest. Used when the target's own surface can't be reached.
    /// </summary>
    private int VantageSurface(MapLayout map, Avatar self, Avatar target)
    {
        var reach = MapGenerator.ReachableFrom(map, _surface);
        var tp = target.GlobalPosition;
        int best = _surface;
        float bestScore = float.MaxValue;
        for (int i = 0; i < map.Surfaces.Count; i++)
        {
            if (!reach[i]) continue;
            var s = map.Surfaces[i];
            var stand = new Vector2(Inside(tp.X, s, self.HalfSize.X + 2), s.Y - self.HalfSize.Y);
            float score = stand.DistanceTo(tp) + (HasLineOfSight(stand, tp) ? 0 : 1000);
            if (i == _vantage) score -= 60; // stick with the last pick unless another is clearly better, or we flip-flop
            if (score < bestScore) { bestScore = score; best = i; }
        }
        return _vantage = best;
    }

    /// <summary>The surface whose top is at <paramref name="feet"/> (within <paramref name="tol"/> px), or -1.</summary>
    private static int SurfaceAt(MapLayout map, Vector2 feet, float tol)
    {
        for (int i = 0; i < map.Surfaces.Count; i++)
        {
            var s = map.Surfaces[i];
            if (Mathf.Abs(feet.Y - s.Y) < tol && feet.X >= s.X1 - 4 && feet.X <= s.X2 + 4) return i;
        }
        return -1;
    }

    /// <summary>The highest surface at or below a point (where someone in the air will land), or -1.</summary>
    private static int SurfaceUnder(MapLayout map, Vector2 p)
    {
        int best = -1;
        for (int i = 0; i < map.Surfaces.Count; i++)
        {
            var s = map.Surfaces[i];
            if (s.Y >= p.Y - 4 && p.X >= s.X1 - 4 && p.X <= s.X2 + 4 && (best < 0 || s.Y < map.Surfaces[best].Y)) best = i;
        }
        return best;
    }

    private bool HasLineOfSight(Vector2 from, Vector2 to)
    {
        var d = to - from;
        var hit = Sweep.Cast(new NVec(from.X, from.Y), new NVec(d.X, d.Y), 1, _game.Map.Solids, _game.Map.Slopes);
        return hit == null;
    }

    private bool GroundAhead(Avatar self, float move)
    {
        if (Mathf.Abs(move) < 0.1f) return true;
        var probe = self.GlobalPosition + new Vector2(Mathf.Sign(move) * (self.HalfSize.X + 6), 0);
        return SolidInColumn(probe.X, probe.Y + self.HalfSize.Y, 50);
    }

    private bool GroundBelow(Avatar self, float depth)
        => SolidInColumn(self.GlobalPosition.X, self.GlobalPosition.Y + self.HalfSize.Y, depth);

    private bool SolidInColumn(float x, float y, float depth)
    {
        foreach (var r in _game.Map.Solids)
            if (x >= r.X && x < r.Right && r.Bottom > y && r.Y < y + depth) return true;
        foreach (var s in _game.Map.Slopes)
            if (x >= s.X1 && x < s.X2 && s.Bottom > y && s.YAt(x) < y + depth) return true;
        return false;
    }

    private bool ThreatIncoming(Avatar self, out int bulletId)
    {
        bulletId = -1;
        var me = self.GlobalPosition;
        foreach (var b in _game.Bullets.Bullets)
        {
            if (b.Dead || (b.Owner == self.Player.Slot && !b.Bounced)) continue;
            var rel = me - b.Pos;
            if (rel.LengthSquared() > 45 * 45) continue;
            if (b.Vel.Dot(rel) <= 0) continue;
            bulletId = b.Id;
            return true;
        }
        return false;
    }
}
