using System.Collections.Generic;
using Brawler.Core;
using Brawler.Logic;
using Godot;
using NVec = System.Numerics.Vector2;

namespace Brawler.Game;

public sealed class Bullet
{
    public int Id;
    public int Owner;
    public Vector2 Pos, Vel;
    public float Radius, Gravity, Damage, Homing, Explosion, Knockback, Life;
    public int Bounces, Pierce, Split;
    public float Age;
    public bool Bounced, Dead;
    public readonly HashSet<int> Hit = new();
    public readonly Vector2[] Trail = new Vector2[4];

    public byte[] Serialize()
    {
        var w = new Wire();
        w.I32(Id).U8(Owner).V2(Pos).V2(Vel)
         .F(Radius).F(Gravity).F(Damage).F(Homing).F(Explosion).F(Knockback).F(Life)
         .U8(Bounces).U8(Pierce).U8(Split);
        return w.Done();
    }

    public static Bullet Deserialize(byte[] data)
    {
        var r = new WireReader(data);
        return new Bullet
        {
            Id = r.I32(), Owner = r.U8(), Pos = r.V2(), Vel = r.V2(),
            Radius = r.F(), Gravity = r.F(), Damage = r.F(), Homing = r.F(), Explosion = r.F(), Knockback = r.F(), Life = r.F(),
            Bounces = r.U8(), Pierce = r.U8(), Split = r.U8(),
        };
    }
}

/// <summary>
/// All bullets, simulated on every peer from the same spawn events. Only the host resolves player hits,
/// splits and explosions, and tells clients which bullets died.
/// </summary>
public partial class BulletSystem : Node2D
{
    public GameManager Game;
    public readonly List<Bullet> Bullets = new();
    private readonly List<Bullet> _spawnQueue = new();

    public static Bullet FromStats(int owner, Vector2 pos, Vector2 vel, StatBlock s) => new()
    {
        Owner = owner, Pos = pos, Vel = vel,
        Radius = s[Stat.BulletRadius], Gravity = s[Stat.BulletGravity], Damage = s[Stat.Damage],
        Homing = s[Stat.Homing], Explosion = s[Stat.ExplosionRadius], Knockback = s[Stat.Knockback],
        Life = s[Stat.Lifetime], Bounces = s.Int(Stat.Bounces), Pierce = s.Int(Stat.Pierce), Split = s.Int(Stat.SplitCount),
    };

    public void Add(Bullet b)
    {
        for (int i = 0; i < b.Trail.Length; i++) b.Trail[i] = b.Pos;
        Bullets.Add(b);
    }

    public void Kill(int id)
    {
        foreach (var b in Bullets)
            if (b.Id == id) b.Dead = true;
    }

    public void Clear() => Bullets.Clear();

    public void Step(float dt, bool host, IReadOnlyList<RectI> solids)
    {
        foreach (var b in Bullets)
        {
            if (b.Dead) continue;
            b.Age += dt;
            if (b.Age > b.Life || b.Pos.Y > MapLayout.KillY + 40)
            {
                if (host) HostDestroy(b, false);
                else b.Dead = true;
                continue;
            }

            for (int i = b.Trail.Length - 1; i > 0; i--) b.Trail[i] = b.Trail[i - 1];
            b.Trail[0] = b.Pos;

            if (b.Homing > 0) Steer(b, dt);
            b.Vel += new Vector2(0, b.Gravity * dt);

            var start = b.Pos;
            MoveWithBounces(b, dt, host, solids);
            if (host && !b.Dead) HostCheckPlayers(b, start);
        }

        Bullets.AddRange(_spawnQueue);
        _spawnQueue.Clear();
        Bullets.RemoveAll(b => b.Dead);
        QueueRedraw();
    }

    private void Steer(Bullet b, float dt)
    {
        Avatar best = null;
        float bestD = 260f;
        foreach (var a in Game.LiveAvatars())
        {
            if (a.Player.Slot == b.Owner) continue;
            float d = a.GlobalPosition.DistanceTo(b.Pos);
            if (d < bestD) { bestD = d; best = a; }
        }
        if (best == null) return;
        float speed = b.Vel.Length();
        float want = (best.GlobalPosition - b.Pos).Angle();
        float cur = b.Vel.Angle();
        float diff = Mathf.Wrap(want - cur, -Mathf.Pi, Mathf.Pi);
        float turn = Mathf.Clamp(diff, -b.Homing * dt, b.Homing * dt);
        b.Vel = Vector2.FromAngle(cur + turn) * speed;
    }

    private void MoveWithBounces(Bullet b, float dt, bool host, IReadOnlyList<RectI> solids)
    {
        var delta = b.Vel * dt;
        for (int iter = 0; iter < 4 && delta.LengthSquared() > 1e-6f; iter++)
        {
            var hit = Sweep.Cast(ToN(b.Pos), ToN(delta), b.Radius, solids);
            if (hit == null)
            {
                b.Pos += delta;
                return;
            }
            var h = hit.Value;
            var n = new Vector2(h.Normal.X, h.Normal.Y);
            b.Pos += delta * h.T + n * 0.01f;
            if (b.Bounces > 0)
            {
                b.Bounces--;
                b.Bounced = true;
                b.Vel = ToG(Sweep.Reflect(ToN(b.Vel), h.Normal)) * 0.92f;
                var remaining = delta * (1 - h.T);
                delta = ToG(Sweep.Reflect(ToN(remaining), h.Normal));
                Game.Fx.Spark(b.Pos, Palette.Player(Game.ColorOf(b.Owner)), 3);
                Game.Sfx.Play("bounce", 0.5f);
            }
            else
            {
                if (host) HostImpact(b, b.Pos);
                else
                {
                    b.Dead = true;
                    Game.Fx.Spark(b.Pos, Palette.Player(Game.ColorOf(b.Owner)), 4);
                }
                return;
            }
        }
    }

    // ---------- host-only resolution ----------

    private void HostCheckPlayers(Bullet b, Vector2 start)
    {
        foreach (var a in Game.LiveAvatars())
        {
            if (b.Hit.Contains(a.Player.Slot)) continue;
            if (a.Player.Slot == b.Owner && !b.Bounced && b.Age < 0.5f) continue; // no instant self-hits
            var r = a.Bounds;
            if (!Sweep.SegmentTouchesRect(ToN(start), ToN(b.Pos), b.Radius, r.Position.X, r.Position.Y, r.Size.X, r.Size.Y))
                continue;

            b.Hit.Add(a.Player.Slot);
            var kb = b.Vel.Normalized() * b.Knockback;
            if (a.BlockActive > 0)
            {
                Game.HostBlocked(a, b.Pos);
                HostDestroy(b, false);
                return;
            }
            Game.HostDamage(a, b.Damage, kb, b.Owner, b.Pos);
            if (b.Pierce > 0)
            {
                b.Pierce--;
                continue;
            }
            HostImpact(b, b.Pos);
            return;
        }
    }

    private void HostImpact(Bullet b, Vector2 at)
    {
        if (b.Explosion > 0) Game.HostExplode(at, b.Explosion, b.Damage * 0.8f, b.Owner, b.Knockback * 1.5f);
        if (b.Split > 0)
        {
            float baseA = (-b.Vel).Angle();
            for (int i = 0; i < b.Split; i++)
            {
                float a = baseA + (i - (b.Split - 1) / 2f) * 0.6f + Game.HostRng.Range(-0.1f, 0.1f);
                var child = new Bullet
                {
                    Owner = b.Owner, Pos = at, Vel = Vector2.FromAngle(a) * Mathf.Max(160, b.Vel.Length() * 0.6f),
                    Radius = Mathf.Max(1, b.Radius * 0.7f), Gravity = b.Gravity, Damage = b.Damage * 0.4f,
                    Homing = b.Homing, Explosion = 0, Knockback = b.Knockback * 0.5f, Life = 1.2f,
                    Bounces = Mathf.Min(b.Bounces, 1), Pierce = 0, Split = 0,
                };
                child.Hit.UnionWith(b.Hit);
                Game.HostSpawnBullet(child, queued: true);
            }
        }
        HostDestroy(b, true);
    }

    private void HostDestroy(Bullet b, bool impact)
    {
        b.Dead = true;
        Game.HostBulletDestroyed(b, impact);
    }

    /// <summary>Bullets spawned during Step are queued so the list isn't modified mid-iteration.</summary>
    public void AddQueued(Bullet b)
    {
        for (int i = 0; i < b.Trail.Length; i++) b.Trail[i] = b.Pos;
        _spawnQueue.Add(b);
    }

    // ---------- drawing ----------

    public override void _Draw()
    {
        foreach (var b in Bullets)
        {
            if (b.Dead) continue;
            var c = Palette.Player(Game.ColorOf(b.Owner));
            for (int i = b.Trail.Length - 1; i >= 0; i--)
            {
                float k = 1f - (i + 1) / (float)(b.Trail.Length + 1);
                float r = Mathf.Max(1, b.Radius * k);
                DrawRect(new Rect2((b.Trail[i] - new Vector2(r, r) * 0.5f).Round(), new Vector2(r, r).Ceil()), c with { A = 0.35f * k });
            }
            float s = Mathf.Max(2, Mathf.Round(b.Radius * 2));
            var p = (b.Pos - new Vector2(s, s) * 0.5f).Round();
            DrawRect(new Rect2(p, new Vector2(s, s)), Colors.White);
            if (s >= 4) DrawRect(new Rect2(p + Vector2.One, new Vector2(s - 2, s - 2)), c.Lightened(0.3f));
        }
    }

    private static NVec ToN(Vector2 v) => new(v.X, v.Y);
    private static Vector2 ToG(NVec v) => new(v.X, v.Y);
}
