using System.Collections.Generic;
using Brawler.Core;
using Brawler.Fx;
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
    // Chaos behaviors (simulated on every peer): fly back to the owner, pass through walls, stick as a mine,
    // steer toward the owner's crosshair. Child = a split fragment (no recoil or muzzle flash).
    public bool Boomerang, Phase, Child, Stuck;
    // Bounce Bombs combo: an explosive bullet also blows up (smaller) each time it bounces.
    public bool BounceBlast;
    public float Sticky, Guided;
    public readonly HashSet<int> Hit = new();
    public readonly Vector2[] Trail = new Vector2[TrailMax];
    public const int TrailMax = 14;

    /// <summary>Copies what drawing a bullet reads (the kill cam keeps these copies of past ticks).</summary>
    public void CopyLook(Bullet to)
    {
        to.Id = Id;
        to.Owner = Owner;
        to.Pos = Pos;
        to.Vel = Vel;
        to.Radius = Radius;
        to.Life = Life;
        to.Age = Age;
        to.Dead = Dead;
        to.Phase = Phase;
        to.Stuck = Stuck;
        System.Array.Copy(Trail, to.Trail, TrailMax);
    }

    public byte[] Serialize()
    {
        var w = new Wire();
        w.I32(Id).U8(Owner).V2(Pos).V2(Vel)
         .F(Radius).F(Gravity).F(Damage).F(Homing).F(Explosion).F(Knockback).F(Life)
         .U8(Bounces).U8(Pierce).U8(Split)
         .U8((Boomerang ? 1 : 0) | (Phase ? 2 : 0) | (Child ? 4 : 0) | (BounceBlast ? 8 : 0)).F(Sticky).F(Guided);
        return w.Done();
    }

    public static Bullet Deserialize(byte[] data)
    {
        var r = new WireReader(data);
        var b = new Bullet
        {
            Id = r.I32(), Owner = r.U8(), Pos = r.V2(), Vel = r.V2(),
            Radius = r.F(), Gravity = r.F(), Damage = r.F(), Homing = r.F(), Explosion = r.F(), Knockback = r.F(), Life = r.F(),
            Bounces = r.U8(), Pierce = r.U8(), Split = r.U8(),
        };
        int flags = r.U8();
        b.Boomerang = (flags & 1) != 0;
        b.Phase = (flags & 2) != 0;
        b.Child = (flags & 4) != 0;
        b.BounceBlast = (flags & 8) != 0;
        b.Sticky = r.F();
        b.Guided = r.F();
        return b;
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
    /// <summary>Bounce Bombs explosions the host has set off (autotest COMBOCHECK).</summary>
    public int BounceBlasts;
    private readonly List<Bullet> _spawnQueue = new();

    public static Bullet FromStats(int owner, Vector2 pos, Vector2 vel, StatBlock s) => new()
    {
        Owner = owner, Pos = pos, Vel = vel,
        Radius = s[Stat.BulletRadius], Gravity = s[Stat.BulletGravity], Damage = s[Stat.Damage],
        Homing = s[Stat.Homing], Explosion = s[Stat.ExplosionRadius], Knockback = s[Stat.Knockback],
        Life = s[Stat.Lifetime], Bounces = s.Int(Stat.Bounces), Pierce = s.Int(Stat.Pierce), Split = s.Int(Stat.SplitCount),
        Boomerang = s[Stat.Boomerang] > 0, Phase = s[Stat.Phase] > 0, Sticky = s[Stat.Sticky], Guided = s[Stat.Guided],
        BounceBlast = s[Stat.BounceBlast] > 0,
    };

    private const float BoomerangTurn = 0.45f; // seconds out before a boomerang bullet heads home

    public void Add(Bullet b)
    {
        for (int i = 0; i < b.Trail.Length; i++) b.Trail[i] = b.Pos;
        Bullets.Add(b);
    }

    public Bullet Kill(int id)
    {
        foreach (var b in Bullets)
            if (b.Id == id)
            {
                b.Dead = true;
                return b;
            }
        return null;
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

            if (b.Stuck)
            {
                // A mine: sits still and waits for someone to touch it.
                if (host) HostCheckPlayers(b, b.Pos);
                continue;
            }

            bool returning = b.Boomerang && b.Age > BoomerangTurn;
            if (returning)
            {
                if (ReturnHome(b, dt, host)) continue;
            }
            else
            {
                if (b.Guided > 0) SteerGuided(b, dt);
                else if (b.Homing > 0) Steer(b, dt);
                b.Vel += new Vector2(0, b.Gravity * dt);
            }

            var start = b.Pos;
            if (b.Phase || returning) b.Pos += b.Vel * dt; // ghosts and returning boomerangs ignore walls
            else MoveWithBounces(b, dt, host, solids);
            if (host && !b.Dead) HostCheckPlayers(b, start);
            if (!b.Dead) Shed(b);
        }

        Bullets.AddRange(_spawnQueue);
        _spawnQueue.Clear();
        Bullets.RemoveAll(b => b.Dead);
    }

    private void Steer(Bullet b, float dt)
    {
        Avatar best = null;
        float bestD = 260f;
        foreach (var a in Game.LiveAvatars())
        {
            if (a.Player.Slot == b.Owner || Game.Allies(a.Player.Slot, b.Owner)) continue;
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

    /// <summary>Steer toward the point under the owner's crosshair, at the bullet's current range.</summary>
    private void SteerGuided(Bullet b, float dt)
    {
        var owner = Game.AvatarOf(b.Owner);
        if (owner == null) return;
        var target = owner.GlobalPosition + owner.AimDir * (owner.GlobalPosition.DistanceTo(b.Pos) + 50);
        float speed = b.Vel.Length();
        float cur = b.Vel.Angle();
        float diff = Mathf.Wrap((target - b.Pos).Angle() - cur, -Mathf.Pi, Mathf.Pi);
        float turn = Mathf.Clamp(diff, -b.Guided * dt, b.Guided * dt);
        b.Vel = Vector2.FromAngle(cur + turn) * speed;
        b.Vel += new Vector2(0, -b.Gravity * dt * 0.8f); // mostly cancel drop so it goes where you point
    }

    /// <summary>Fly back to the owner; returns true when caught (the bullet is gone).</summary>
    private bool ReturnHome(Bullet b, float dt, bool host)
    {
        var owner = Game.AvatarOf(b.Owner);
        if (owner == null)
        {
            b.Vel += new Vector2(0, b.Gravity * dt);
            return false;
        }
        var to = owner.GlobalPosition - b.Pos;
        float speed = Mathf.Max(220, b.Vel.Length());
        b.Vel = b.Vel.MoveToward(to.Normalized() * speed, speed * 5 * dt);
        if (to.Length() > owner.HalfSize.Length() + b.Radius + 4) return false;
        if (host) HostDestroy(b, false);
        else b.Dead = true;
        return true;
    }

    private void MoveWithBounces(Bullet b, float dt, bool host, IReadOnlyList<RectI> solids)
    {
        var delta = b.Vel * dt;
        for (int iter = 0; iter < 4 && delta.LengthSquared() > 1e-6f; iter++)
        {
            var hit = Sweep.Cast(ToN(b.Pos), ToN(delta), b.Radius, solids, Game.Map?.Slopes);
            if (hit == null)
            {
                b.Pos += delta;
                return;
            }
            var h = hit.Value;
            var n = new Vector2(h.Normal.X, h.Normal.Y);
            b.Pos += delta * h.T + n * 0.01f;
            // Crates, barrels, glass and drifting platforms take the hit; glass the bullet can break, it flies straight through.
            if (Game.Arena.BulletHitsSolid(b, h.Index, host))
            {
                delta *= 1 - h.T;
                continue;
            }
            if (b.Bounces > 0)
            {
                b.Bounces--;
                b.Bounced = true;
                b.Vel = ToG(Sweep.Reflect(ToN(b.Vel), h.Normal)) * 0.92f;
                var remaining = delta * (1 - h.T);
                delta = ToG(Sweep.Reflect(ToN(remaining), h.Normal));
                var bc = Palette.Player(Game.ColorOf(b.Owner));
                Game.Fx.Emit(b.Pos, PKind.Streak, bc.Lightened(0.3f), 4, 140, 0.2f, 0.9f, 0, n, 2.2f);
                Game.Fx.RingFx(b.Pos, 1, 7 + b.Radius * 2, bc, 0.18f, 1.2f);
                Game.Sfx.Play("bounce", 0.5f);
                if (host && b.BounceBlast && b.Explosion > 0)
                {
                    BounceBlasts++;
                    Game.HostExplode(b.Pos, b.Explosion * 0.7f, b.Damage * 0.5f, b.Owner, b.Knockback);
                }
            }
            else if (b.Sticky > 0)
            {
                // Sticky Mines: park on the surface and arm.
                b.Stuck = true;
                b.Vel = Vector2.Zero;
                b.Life = b.Age + b.Sticky;
                Game.Fx.RingFx(b.Pos, 1, 8 + b.Radius * 2, Palette.Player(Game.ColorOf(b.Owner)), 0.25f, 1.2f);
                return;
            }
            else
            {
                if (host) HostImpact(b, b.Pos);
                else
                {
                    b.Dead = true;
                    Game.Fx.Spark(b.Pos, Palette.Player(Game.ColorOf(b.Owner)), 2);
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
            if (b.Hit.Contains(a.Player.Slot) || Game.Spared(b.Owner, a.Player.Slot)) continue; // shots pass through teammates
            if (a.Player.Slot == b.Owner && ((!b.Bounced && b.Age < 0.5f) || b.Boomerang || b.Stuck)) continue; // no instant self-hits
            var r = a.Bounds;
            if (!Sweep.SegmentTouchesRect(ToN(start), ToN(b.Pos), b.Radius, r.Position.X, r.Position.Y, r.Size.X, r.Size.Y))
                continue;

            b.Hit.Add(a.Player.Slot);
            var kb = b.Vel.Normalized() * b.Knockback;
            if (a.BlockActive > 0)
            {
                Game.HostBlocked(a, b.Pos);
                Game.HostBlockedHit(a, b.Damage, b.Pos, b.Owner);
                if (a.S[Stat.Reflect] > 0) HostReflect(b, a);
                HostDestroy(b, false);
                return;
            }
            Game.HostDamage(a, b.Damage, kb, b.Owner, b.Pos);
            Game.HostChain(a, b.Damage, b.Owner);
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
                    Phase = b.Phase, Guided = 0, Child = true,
                };
                child.Hit.UnionWith(b.Hit);
                Game.HostSpawnBullet(child, queued: true);
            }
        }
        if (!b.Child) Game.HostWarp(b.Owner, at, b.Vel);
        HostDestroy(b, true);
    }

    private void HostDestroy(Bullet b, bool impact)
    {
        b.Dead = true;
        Game.HostBulletDestroyed(b, impact);
    }

    /// <summary>
    /// Reflector: a copy of a blocked bullet flies back at whoever fired it (straight back if they're gone),
    /// now owned by the blocker. It's a Child, so it never warps or kicks anyone.
    /// </summary>
    private void HostReflect(Bullet b, Avatar blocker)
    {
        var shooter = b.Owner >= 0 && b.Owner < Game.Slots.Length ? Game.Slots[b.Owner]?.Avatar : null;
        var dir = shooter is { Alive: true } && shooter != blocker ? (shooter.GlobalPosition - b.Pos).Normalized() : -b.Vel.Normalized();
        if (dir == Vector2.Zero) dir = Vector2.Up;
        var r = new Bullet
        {
            Owner = blocker.Player.Slot, Pos = b.Pos, Vel = dir * Mathf.Max(260, b.Vel.Length()),
            Radius = b.Radius, Gravity = b.Gravity, Damage = b.Damage, Homing = b.Homing, Explosion = b.Explosion,
            Knockback = b.Knockback, Life = Mathf.Max(1.5f, b.Life - b.Age), Bounces = b.Bounces, Pierce = b.Pierce,
            Split = b.Split, Phase = b.Phase, BounceBlast = b.BounceBlast, Child = true,
        };
        r.Hit.Add(blocker.Player.Slot);
        Game.HostSpawnBullet(r, queued: true);
    }

    /// <summary>Host: Shockwave. Destroys every bullet not owned by <paramref name="keepOwner"/> within the radius.</summary>
    public void HostErase(Vector2 at, float radius, int keepOwner)
    {
        foreach (var b in Bullets)
            if (!b.Dead && b.Owner != keepOwner && !Game.Allies(b.Owner, keepOwner) && b.Pos.DistanceTo(at) <= radius + b.Radius) HostDestroy(b, false);
    }

    /// <summary>Bullets spawned during Step are queued so the list isn't modified mid-iteration.</summary>
    public void AddQueued(Bullet b)
    {
        for (int i = 0; i < b.Trail.Length; i++) b.Trail[i] = b.Pos;
        _spawnQueue.Add(b);
    }

    // ---------- drawing ----------

    private DrawLayer _trails, _cores;
    private float _t;

    public override void _Ready()
    {
        _trails = new DrawLayer(DrawTrails, additive: true);
        _cores = new DrawLayer(DrawCores);
        AddChild(_trails);
        AddChild(_cores);
    }

    public override void _Process(double delta)
    {
        _t += (float)delta;
        _trails.QueueRedraw();
        _cores.QueueRedraw();
    }

    /// <summary>Flavored particles shed while a bullet flies (every peer, cosmetic).</summary>
    private void Shed(Bullet b)
    {
        var st = Game.StyleOf(b.Owner);
        if (st.Power == 0) return;
        float roll = GD.Randf();
        if (st.Has(Flavor.Fire) && roll < 0.35f)
            Game.Fx.Emit(b.Pos, PKind.Ember, new Color("#ff9a2e"), 1, 30, 0.45f, 1f + b.Radius * 0.3f, -40);
        else if (st.Has(Flavor.Homing) && roll < 0.2f)
            Game.Fx.Emit(b.Pos, PKind.Star, Palette.Player(Game.ColorOf(b.Owner)).Lightened(0.5f), 1, 20, 0.35f, 1.8f);
        else if (st.Has(Flavor.Electric) && roll < 0.25f)
            Game.Fx.Emit(b.Pos, PKind.Streak, new Color("#8fe9ff"), 1, 90, 0.12f, 0.7f);
        else if (st.Has(Flavor.Vampire) && roll < 0.15f)
            Game.Fx.Emit(b.Pos, PKind.Dot, new Color("#ff2a4a"), 1, 15, 0.3f, 0.8f);
        else if (st.Tier >= 3 && roll < 0.08f * st.Tier)
            Game.Fx.Emit(b.Pos, PKind.Dot, Palette.Player(Game.ColorOf(b.Owner)).Lightened(0.4f), 1, 25, 0.3f, 0.7f);
    }

    private static Color TrailColor(FxStyle st, Color owner) =>
        st.Has(Flavor.Fire) ? new Color("#ff7a22") : st.Has(Flavor.Electric) ? new Color("#8fe9ff") : st.Has(Flavor.Ghost) ? new Color("#c9b6ff") : st.Has(Flavor.Vampire) ? new Color("#ff2a4a") : st.Has(Flavor.Glass) ? new Color("#bff2ff") : owner;

    private void DrawTrails(DrawLayer l)
    {
        foreach (var b in Bullets)
        {
            if (b.Dead) continue;
            var st = Game.StyleOf(b.Owner);
            var owner = Palette.Player(Game.ColorOf(b.Owner));
            var c = TrailColor(st, owner);
            int len = Mathf.Min(Bullet.TrailMax, 5 + st.Tier * 2 + (st.Has(Flavor.Pierce) ? 3 : 0));
            float width = b.Radius * 2f * (1f + st.Tier * 0.08f);
            var prev = b.Pos;
            for (int i = 0; i < len; i++)
            {
                var p = b.Trail[i];
                float k = 1f - i / (float)len;
                if (prev.DistanceSquaredTo(p) > 0.01f)
                    l.DrawLine(prev, p, Shapes.Hdr(c, 1.2f + st.Tier * 0.15f) with { A = 0.75f * k }, Mathf.Max(0.6f, width * k), true);
                prev = p;
            }
            float glow = b.Radius * (4f + st.Tier * 0.9f) * (st.Has(Flavor.Big) ? 1.3f : 1f);
            float pulse = 1f + 0.12f * Mathf.Sin(_t * 18 + b.Id);
            GlowTex.Draw(l, b.Pos, glow * pulse, owner with { A = 0.5f + st.Tier * 0.06f });
            if (st.Tier >= 4)
                l.DrawArc(b.Pos, b.Radius + 2.5f + Mathf.Sin(_t * 14 + b.Id) * 0.8f, 0, Mathf.Tau, 16, Shapes.Hdr(owner, 1.6f) with { A = 0.6f }, 0.8f, true);
        }
    }

    private void DrawCores(DrawLayer l)
    {
        foreach (var b in Bullets)
        {
            if (b.Dead) continue;
            var st = Game.StyleOf(b.Owner);
            var owner = Palette.Player(Game.ColorOf(b.Owner));
            float r = Mathf.Max(1.4f, b.Radius);
            float alpha = b.Phase ? 0.55f + 0.2f * Mathf.Sin(_t * 20 + b.Id) : 1f; // ghosts shimmer
            if (b.Stuck)
            {
                // Armed mine: a blinking warning ring that speeds up as it is about to expire.
                float left = b.Life - b.Age;
                float blink = 0.5f + 0.5f * Mathf.Sin(_t * (left < 1.5f ? 22 : 8) + b.Id);
                l.DrawArc(b.Pos, r + 3.5f, 0, Mathf.Tau, 20, Shapes.Hdr(owner, 1.4f) with { A = 0.35f + 0.55f * blink }, 1f, true);
            }
            l.DrawCircle(b.Pos, r + 0.7f, owner.Darkened(0.55f) with { A = alpha }, true, -1, true);
            l.DrawCircle(b.Pos, r, TrailColor(st, owner).Lightened(0.35f) with { A = alpha }, true, -1, true);
            l.DrawCircle(b.Pos - b.Vel.Normalized() * r * 0.15f, r * 0.55f, Shapes.Hdr(Colors.White, 1.6f + st.Tier * 0.2f) with { A = alpha }, true, -1, true);
        }
    }

    private static NVec ToN(Vector2 v) => new(v.X, v.Y);
    private static Vector2 ToG(NVec v) => new(v.X, v.Y);
}
