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
        if (dist > _preferredDist + 50) move = Mathf.Sign(to.X);
        else if (dist < _preferredDist - 50) move = -Mathf.Sign(to.X);
        else move = _strafeDir * 0.6f;
        if (!los) move = Mathf.Sign(to.X == 0 ? 1 : to.X);

        bool wantJump = false;
        bool onFloor = self.IsOnFloor();
        if (onFloor && to.Y < -30 && Mathf.Abs(to.X) < 140) wantJump = true;        // target above
        if (self.IsOnWall() && move * self.GetWallNormal().X < 0) wantJump = true;   // wall ahead / wall jump
        if (onFloor && !GroundAhead(self, move)) wantJump = true;                     // edge or pit ahead
        if (!onFloor && self.Velocity.Y > 60 && !GroundBelow(self, 140)) move = -move; // falling into a pit: steer back
        if (onFloor && _rng.Randf() < 0.008f) wantJump = true;

        if (wantJump && _jumpHold <= -0.15f)
        {
            _jumps++;
            _jumpHold = _rng.RandfRange(0.15f, 0.35f);
        }
        f.MoveX = Mathf.Clamp(move, -1, 1);
        f.JumpHeld = _jumpHold > 0;
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
