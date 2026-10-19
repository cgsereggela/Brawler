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
        _preferredDist = _rng.RandfRange(110, 190);
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
        f.Shoot = los && dist < 460 && (self.Ammo > 1 || dist < 160 || _rng.Randf() < 0.05f);

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
        var map = _game.Map;
        if (map != _navMap) { _navMap = map; _surface = -1; _hop = -1; }
        var feet = me + new Vector2(0, self.HalfSize.Y);
        if (onFloor && SurfaceAt(map, feet, 4) is int here and >= 0 && here != _surface) { _surface = here; _navT = 0; }
        _navT -= dt;
        bool navigating = false;
        if ((!los || Mathf.Abs(to.Y) > 60) && _surface >= 0)
        {
            if (_navT <= 0)
            {
                _navT = 0.3f;
                int goal = SurfaceUnder(map, target.GlobalPosition + new Vector2(0, target.HalfSize.Y));
                _hop = goal >= 0 ? MapGenerator.NextHop(map, _surface, goal) : -1;
            }
            navigating = _hop >= 0 && _hop != _surface;
        }
        if (navigating)
        {
            var hop = map.Surfaces[_hop];
            var cur = map.Surfaces[_surface];
            float x = me.X, aimX;
            if (hop.Y < cur.Y - 4)
            {
                // Climbing: get clear of the platform's underside, then jump toward it and steer onto it.
                bool overlap = cur.X2 > hop.X1 && cur.X1 < hop.X2;
                float edge = x < hop.X1 ? hop.X1 - x : x > hop.X2 ? x - hop.X2 : 0;
                if (!onFloor) aimX = Inside(x, hop, 12);
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
                    if (edge < 34) wantJump = true;
                }
            }
            else if (onFloor && hop.Y > cur.Y + 4 && cur.X2 > hop.X1 && cur.X1 < hop.X2)
            {
                // Dropping onto something under this surface: walk off the edge it sticks out past (or the nearest).
                bool leftOk = hop.X1 < cur.X1 - 4, rightOk = hop.X2 > cur.X2 + 4;
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

        if (onFloor && !navigating && to.Y < -30 && Mathf.Abs(to.X) < 140) wantJump = true; // target above
        if (self.IsOnWall() && move * self.GetWallNormal().X < 0) wantJump = true;   // wall ahead / wall jump
        if (onFloor && !GroundAhead(self, move)) wantJump = true;                     // edge or pit ahead
        if (!navigating && !onFloor && self.Velocity.Y > 60 && !GroundBelow(self, 140)) move = -move; // falling into a pit: steer back
        if (onFloor && _rng.Randf() < 0.008f) wantJump = true;

        // Card abilities: spend air jumps or jetpack fuel to climb toward the target or out of a fall,
        // and glide over pits. Without this, movement cards would only measure how well bots ignore them.
        bool jet = self.S[Stat.Jetpack] > 0;
        if (!onFloor && (self.S[Stat.AirJumps] > 0 || jet) && self.Velocity.Y > 20)
        {
            bool climbing = navigating && map.Surfaces[_hop].Y < feet.Y - 4;
            if (to.Y < -40 || climbing || !GroundBelow(self, 140)) wantJump = true;
        }
        bool glide = self.S[Stat.Glide] > 0 && !onFloor && self.Velocity.Y > 0 && (to.Y < -20 || !GroundBelow(self, 140));

        if (wantJump && _jumpHold <= -0.15f)
        {
            _jumps++;
            _jumpHold = jet && !onFloor ? _rng.RandfRange(0.5f, 0.8f) : _rng.RandfRange(0.15f, 0.35f);
        }
        f.MoveX = Mathf.Clamp(move, -1, 1);
        f.JumpHeld = _jumpHold > 0 || glide;
        f.JumpPresses = _jumps;

        // --- block incoming bullets ---
        if (self.BlockReady && ThreatIncoming(self, out int bulletId) && _consideredBullets.Add(bulletId))
        {
            if (_rng.Randf() < 0.55f) _blocks++;
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

    private MapLayout _navMap;
    private int _surface = -1, _hop = -1;
    private float _navT, _stuckT, _wanderT, _wanderDir = 1;
    private Vector2 _stuckFrom;

    /// <summary><paramref name="x"/> clamped into a surface, at most <paramref name="inset"/> px in from its ends.</summary>
    private static float Inside(float x, Surface s, float inset)
    {
        inset = Mathf.Min(inset, s.Width * 0.5f);
        return Mathf.Clamp(x, s.X1 + inset, s.X2 - inset);
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
        var hit = Sweep.Cast(new NVec(from.X, from.Y), new NVec(d.X, d.Y), 1, _game.Map.Solids);
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
