using System.Collections.Generic;
using Brawler.Core;
using Brawler.Fx;
using Brawler.Logic;
using Godot;

namespace Brawler.Game;

/// <summary>A crate, barrel or glass tile: solid to players, damaged by bullets and explosions.</summary>
public partial class Prop : CharacterBody2D
{
    public int Index;
    public PropKind Kind;
    public Vector2 Size;
    public float Hp, MaxHp;
    public bool Broken;
    public int LastHitBy = -1;
    /// <summary>Host: someone walked into it this tick and wants it moving at this speed.</summary>
    public float PushV;
    public bool Pushed;
    public Vector2 NetTarget;
    public float Wobble; // cosmetic: shakes briefly when hit

    public bool Pushable => Kind != PropKind.Glass;
    public Rect2 Bounds => new(GlobalPosition - Size * 0.5f, Size);
    public RectI Rect => new(Mathf.RoundToInt(GlobalPosition.X - Size.X * 0.5f), Mathf.RoundToInt(GlobalPosition.Y - Size.Y * 0.5f), (int)Size.X, (int)Size.Y);

    public void Push(float speed)
    {
        PushV = Mathf.Abs(speed) > Mathf.Abs(PushV) || !Pushed ? speed : PushV;
        Pushed = true;
    }
}

/// <summary>
/// The arena's moving and breakable parts (from <see cref="ArenaFeatures"/>): crates and barrels to push and shoot,
/// glass to shatter, moving platforms, magma and poison pools, and sawblades. The host runs prop physics and all
/// damage; movers and saws follow the shared battle clock, so clients only need prop positions from snapshots.
/// </summary>
public partial class Arena : Node2D
{
    private const float PropGravity = 900f;
    private const float PoolTick = 0.25f;

    public GameManager Game;
    public readonly List<Prop> Props = new();
    /// <summary>Seconds since the battle started; movers and saws are functions of it.</summary>
    public float Clock;
    /// <summary>Every solid a bullet can hit this tick: the map, then live props, then movers.</summary>
    public readonly List<RectI> Solids = new();

    /// <summary>Host diagnostics for the battle log: seconds crates spent being pushed, damage dealt by hazards.</summary>
    public float PushedTime, HazardDamage;

    public string Summary
    {
        get
        {
            int broken = 0, glass = 0;
            foreach (var p in Props)
            {
                if (p.Kind == PropKind.Glass) glass++;
                if (p.Broken) broken++;
            }
            return $"{broken}/{Props.Count} props broken ({glass} glass), pushed {PushedTime:0.0}s, hazard damage {HazardDamage:0}, " +
                   $"{_map?.Hazards.Count ?? 0} pools, {_map?.Saws.Count ?? 0} saws, {_movers.Count} movers";
        }
    }

    private MapLayout _map;
    private readonly List<AnimatableBody2D> _movers = new();
    private readonly List<int> _solidProp = new(); // per Solids entry: prop index, or -1
    private readonly Dictionary<int, float> _poison = new(), _poolT = new(), _sawCd = new(), _lavaT = new();
    private DrawLayer _glow;
    private float _t;

    public override void _Ready()
    {
        _glow = new DrawLayer(DrawGlow, additive: true);
        AddChild(_glow);
    }

    public void Build(MapLayout map)
    {
        foreach (var p in Props) p.QueueFree();
        foreach (var m in _movers) m.QueueFree();
        Props.Clear();
        _movers.Clear();
        _poison.Clear();
        _poolT.Clear();
        _sawCd.Clear();
        _lavaT.Clear();
        Clock = 0;
        PushedTime = HazardDamage = 0;
        _map = map;
        if (map != null)
        {
            for (int i = 0; i < map.Props.Count; i++)
            {
                var s = map.Props[i];
                var size = new Vector2(s.W, s.H);
                var p = new Prop
                {
                    Index = i, Kind = s.Kind, Size = size, Hp = s.MaxHp, MaxHp = s.MaxHp,
                    Position = new Vector2(s.X, s.Y) + size * 0.5f, CollisionLayer = 1,
                    // Glass never moves; crates and barrels land on the map, each other and players.
                    CollisionMask = s.Pushable ? 1u | 2u : 0, FloorSnapLength = 2, NetTarget = new Vector2(s.X, s.Y) + size * 0.5f,
                };
                p.AddChild(new CollisionShape2D { Shape = new RectangleShape2D { Size = size } });
                AddChild(p);
                Props.Add(p);
            }
            foreach (var m in map.Movers)
            {
                var body = new AnimatableBody2D { SyncToPhysics = true, CollisionLayer = 1, CollisionMask = 0, Position = new Vector2(m.XAt(0) + m.W * 0.5f, m.Y + m.H * 0.5f) };
                body.AddChild(new CollisionShape2D { Shape = new RectangleShape2D { Size = new Vector2(m.W, m.H) } });
                AddChild(body);
                _movers.Add(body);
            }
        }
        RebuildSolids();
        QueueRedraw();
    }

    // ---------- simulation (every peer) ----------

    /// <summary>Advances the clock and moves platforms; runs before avatars move so riders are carried.</summary>
    public void Advance(float dt)
    {
        if (_map == null) return;
        Clock += dt;
        for (int i = 0; i < _movers.Count; i++)
        {
            var m = _map.Movers[i];
            _movers[i].Position = new Vector2(m.XAt(Clock) + m.W * 0.5f, m.Y + m.H * 0.5f);
        }
    }

    public void RebuildSolids()
    {
        Solids.Clear();
        _solidProp.Clear();
        if (_map == null) return;
        Solids.AddRange(_map.Solids);
        for (int i = 0; i < _map.Solids.Count; i++) _solidProp.Add(-1);
        foreach (var p in Props)
            if (!p.Broken) { Solids.Add(p.Rect); _solidProp.Add(p.Index); }
        for (int i = 0; i < _movers.Count; i++)
        {
            var m = _map.Movers[i];
            Solids.Add(new RectI(Mathf.RoundToInt(m.XAt(Clock)), m.Y, m.W, m.H));
            _solidProp.Add(-1);
        }
    }

    /// <summary>The prop behind entry <paramref name="solid"/> of <see cref="Solids"/>, or null.</summary>
    public Prop PropOfSolid(int solid) => solid >= 0 && solid < _solidProp.Count && _solidProp[solid] >= 0 ? Props[_solidProp[solid]] : null;

    /// <summary>
    /// A bullet touched a prop. Glass it can break is shattered and flown through (returns true); anything else
    /// takes the hit (host) and stops or deflects the bullet as a wall would.
    /// </summary>
    public bool BulletHitsProp(Bullet b, Prop p, int solid, bool host)
    {
        p.Wobble = 1;
        bool through = p.Kind == PropKind.Glass && b.Damage >= p.Hp;
        if (through)
        {
            // Out of the solids list right away, so the bullet carries on (clients predict the break the same way).
            Solids[solid] = new RectI(-1000, -1000, 0, 0);
        }
        if (host)
        {
            if (p.Pushable) p.Velocity += b.Vel.Normalized() * Mathf.Min(220, b.Knockback * 0.8f);
            HostDamageProp(p, b.Damage, b.Owner);
        }
        return through;
    }

    public void HostDamageProp(Prop p, float damage, int by)
    {
        if (p.Broken) return;
        p.Hp -= damage;
        p.Wobble = 1;
        if (by >= 0) p.LastHitBy = by;
        if (p.Hp <= 0) HostBreak(p, fell: false);
    }

    private void HostBreak(Prop p, bool fell)
    {
        if (p.Broken) return;
        p.Broken = true; // first, so a barrel's blast can't break it again
        Game.HostPropBroken(p.Index, p.GlobalPosition, fell);
        if (p.Kind == PropKind.Barrel && !fell)
            Game.HostExplode(p.GlobalPosition, 50, 40, p.LastHitBy, 430);
    }

    /// <summary>Every peer: remove a broken prop (with debris unless it just fell out of the arena).</summary>
    public void Break(int index, Vector2 at, bool fell)
    {
        if (index < 0 || index >= Props.Count) return;
        var p = Props[index];
        p.Broken = true;
        p.CollisionLayer = 0;
        p.CollisionMask = 0;
        p.Visible = false;
        RebuildSolids();
        if (fell) return;
        var fx = Game.Fx;
        switch (p.Kind)
        {
            case PropKind.Crate:
                fx.Emit(at, PKind.Chunk, new Color("#9a6a3c"), 10, 170, 0.9f, 2.4f, 520, Vector2.Up, 2.4f);
                fx.Emit(at, PKind.Shard, new Color("#c48a52"), 6, 140, 0.7f, 2f, 420);
                fx.Emit(at, PKind.Smoke, new Color(0.5f, 0.4f, 0.3f, 0.5f), 4, 30, 0.7f, 3.5f, -10);
                Game.Sfx.Play("crate");
                break;
            case PropKind.Barrel:
                fx.Emit(at, PKind.Chunk, new Color("#c8323a"), 8, 220, 0.9f, 2.2f, 520);
                break;
            case PropKind.Glass:
                fx.Emit(at, PKind.Shard, new Color("#bff4ff"), 7, 120, 0.8f, 2f, 480);
                fx.Emit(at, PKind.Dot, Shapes.Hdr(new Color("#9ff0ff"), 1.4f), 4, 60, 0.35f, 0.8f);
                Game.Sfx.Play("glass", 0.8f);
                break;
        }
    }

    /// <summary>Host: blasts hurt and fling props.</summary>
    public void HostExplosion(Vector2 at, float radius, float damage, int owner, float knockback)
    {
        foreach (var p in Props)
        {
            if (p.Broken) continue;
            var r = p.Bounds;
            var closest = new Vector2(Mathf.Clamp(at.X, r.Position.X, r.End.X), Mathf.Clamp(at.Y, r.Position.Y, r.End.Y));
            float d = closest.DistanceTo(at);
            if (d > radius) continue;
            float falloff = 1f - 0.5f * d / radius;
            if (p.Pushable)
            {
                var dir = (p.GlobalPosition - at).Normalized();
                if (dir == Vector2.Zero) dir = Vector2.Up;
                p.Velocity += (dir + Vector2.Up * 0.4f).Normalized() * knockback * 0.7f * falloff;
            }
            HostDamageProp(p, damage * falloff * 1.5f, owner);
        }
    }

    /// <summary>The sudden-death lava's surface (y) right now; +∞ before it starts or when the lobby turned it off.</summary>
    public float LavaY => _map == null ? float.PositiveInfinity : RisingLava.Level(FightTime, Game.Settings.LavaAfter);

    /// <summary>Seconds since the countdown ended (negative during it): the battle clock lava runs off.</summary>
    public float FightTime => Clock - GameManager.CountdownTime;

    /// <summary>Host: prop physics, then pools and saws (only while fighting).</summary>
    public void HostStep(float dt, bool fighting)
    {
        if (_map == null) return;
        foreach (var p in Props)
        {
            if (p.Broken || !p.Pushable) continue;
            var v = p.Velocity;
            v.Y = Mathf.Min(v.Y + PropGravity * dt, 480);
            if (p.Pushed) v.X = Mathf.MoveToward(v.X, p.PushV, 900 * dt);
            else v.X = Mathf.MoveToward(v.X, 0, (p.IsOnFloor() ? 520 : 60) * dt);
            if (p.Pushed) PushedTime += dt;
            p.Pushed = false;
            p.PushV = 0;
            p.Velocity = v;
            p.MoveAndSlide();
            if (p.GlobalPosition.Y > MapLayout.KillY) HostBreak(p, fell: true);
            else if (p.Bounds.Position.Y > LavaY) HostBreak(p, fell: true); // swallowed by the lava
        }
        if (!fighting) return;

        foreach (var a in Game.LiveAvatars())
        {
            int slot = a.Player.Slot;
            var hs = a.HalfSize;
            var feet = new Rect2(a.GlobalPosition.X - hs.X + 1, a.GlobalPosition.Y + hs.Y - 2, hs.X * 2 - 2, 4);

            // Pools: magma burns while you stand in it; poison keeps ticking for a while after you leave.
            bool magma = false, poison = false;
            foreach (var h in _map.Hazards)
            {
                var r = h.Rect;
                if (!feet.Intersects(new Rect2(r.X + 2, r.Y, r.W - 4, r.H))) continue;
                if (h.Kind == HazardKind.Magma) magma = true; else poison = true;
            }
            if (poison) _poison[slot] = Hazard.PoisonLinger;
            float left = _poison.GetValueOrDefault(slot) - dt;
            _poison[slot] = left;
            float t = _poolT.GetValueOrDefault(slot) - dt;
            if ((magma || left > 0) && t <= 0)
            {
                t = PoolTick;
                var at = new Vector2(a.GlobalPosition.X, a.GlobalPosition.Y + hs.Y);
                if (magma) Game.HostHazardDamage(a, Hazard.DpsOf(HazardKind.Magma) * PoolTick, new Vector2(0, -60), at, GameManager.HazardHit.Magma);
                else Game.HostHazardDamage(a, Hazard.DpsOf(HazardKind.Poison) * PoolTick, Vector2.Zero, a.GlobalPosition, GameManager.HazardHit.Poison);
            }
            _poolT[slot] = t;

            // Sudden-death lava: burns while your feet are in it, and pops you up each tick so you can climb out.
            float lavaT = _lavaT.GetValueOrDefault(slot) - dt;
            if (a.GlobalPosition.Y + hs.Y - 2 > LavaY && lavaT <= 0)
            {
                lavaT = PoolTick;
                var at = new Vector2(a.GlobalPosition.X, LavaY);
                var kb = new Vector2(0, -RisingLava.Pop - Mathf.Max(0, a.Velocity.Y));
                Game.HostHazardDamage(a, RisingLava.Dps * PoolTick, kb, at, GameManager.HazardHit.Magma);
            }
            _lavaT[slot] = lavaT;
            if (!a.Alive) continue;

            // Saws: a hard hit and a shove away, then a moment's grace.
            float cd = _sawCd.GetValueOrDefault(slot) - dt;
            _sawCd[slot] = cd;
            if (cd > 0 || !a.Alive) continue;
            foreach (var s in _map.Saws)
            {
                var c = ToG(s.At(Clock));
                var b = a.Bounds;
                var closest = new Vector2(Mathf.Clamp(c.X, b.Position.X, b.End.X), Mathf.Clamp(c.Y, b.Position.Y, b.End.Y));
                if (closest.DistanceTo(c) > s.Radius - 1) continue;
                var dir = (a.GlobalPosition - c).Normalized();
                if (dir == Vector2.Zero) dir = Vector2.Up;
                var kb = dir * SawTrack.Knockback;
                kb.Y = Mathf.Min(kb.Y, -170);
                _sawCd[slot] = SawTrack.Cooldown;
                Game.HostHazardDamage(a, SawTrack.Damage, kb, closest, GameManager.HazardHit.Saw);
                break;
            }
        }

        // Saws chew through crates and barrels that drift into them.
        foreach (var s in _map.Saws)
        {
            var c = ToG(s.At(Clock));
            foreach (var p in Props)
            {
                if (p.Broken || !p.Pushable) continue;
                var b = p.Bounds;
                var closest = new Vector2(Mathf.Clamp(c.X, b.Position.X, b.End.X), Mathf.Clamp(c.Y, b.Position.Y, b.End.Y));
                if (closest.DistanceTo(c) <= s.Radius) HostDamageProp(p, 90 * dt, -1);
            }
        }
    }

    /// <summary>Clients: ease props toward their last snapshot position.</summary>
    public void ClientSmooth()
    {
        foreach (var p in Props)
            if (!p.Broken && p.Pushable) p.GlobalPosition = p.GlobalPosition.Lerp(p.NetTarget, 0.5f);
    }

    /// <summary>True if a pool or saw is right ahead of (or under) a body walking in direction <paramref name="move"/>.</summary>
    public bool DangerAhead(Avatar a, float move)
    {
        if (_map == null) return false;
        var hs = a.HalfSize;
        float feetY = a.GlobalPosition.Y + hs.Y;
        float dir = Mathf.Abs(move) < 0.1f ? 0 : Mathf.Sign(move);
        float x0 = a.GlobalPosition.X - hs.X, x1 = a.GlobalPosition.X + hs.X;
        if (dir > 0) x1 += 26;
        if (dir < 0) x0 -= 26;
        var probe = new Rect2(x0, feetY - hs.Y * 2 - 4, x1 - x0, hs.Y * 2 + 6);
        foreach (var h in _map.Hazards)
        {
            var r = h.Rect;
            if (probe.Intersects(new Rect2(r.X, r.Y, r.W, r.H))) return true;
        }
        foreach (var s in _map.Saws)
        {
            var c = ToG(s.At(Clock));
            var closest = new Vector2(Mathf.Clamp(c.X, probe.Position.X, probe.End.X), Mathf.Clamp(c.Y, probe.Position.Y, probe.End.Y));
            if (closest.DistanceTo(c) < s.Radius + 6) return true;
        }
        return false;
    }

    // ---------- replication ----------

    public void Write(Wire w)
    {
        w.F(Clock).U8(Props.Count);
        foreach (var p in Props)
        {
            // Flags: 1 = still there, 2 = a position follows (glass never moves, so it skips it).
            w.U8((p.Broken ? 0 : 1) | (p.Pushable ? 2 : 0)).U8(Mathf.Clamp(Mathf.RoundToInt(p.Hp / p.MaxHp * 255), 0, 255));
            if (p.Pushable) w.V2(p.GlobalPosition);
        }
    }

    public void Read(WireReader r)
    {
        float clock = r.F();
        // Movers and saws run off the local clock; only pull it in when it has drifted noticeably.
        if (Mathf.Abs(clock - Clock) > 0.08f) Clock = clock;
        int n = r.U8();
        for (int i = 0; i < n; i++)
        {
            int flags = r.U8();
            bool alive = (flags & 1) != 0, pushable = (flags & 2) != 0;
            float hp = r.U8() / 255f;
            var pos = pushable ? r.V2() : default;
            if (i >= Props.Count) continue;
            var p = Props[i];
            if (p.Broken) continue;
            if (hp * p.MaxHp < p.Hp - 0.5f) p.Wobble = 1;
            p.Hp = hp * p.MaxHp;
            if (pushable) p.NetTarget = pos;
            if (!alive) Break(i, p.GlobalPosition, fell: true); // missed the break event: just remove it
        }
    }

    // ---------- drawing ----------

    public override void _Process(double delta)
    {
        if (_map == null) return;
        float dt = (float)delta;
        _t += dt;
        foreach (var p in Props) p.Wobble = Mathf.Max(0, p.Wobble - dt * 6);
        // Ambient: embers off magma, bubbles off poison.
        foreach (var h in _map.Hazards)
        {
            if (GD.Randf() > (h.X2 - h.X1) * dt * 0.08f) continue;
            var at = new Vector2((float)GD.RandRange(h.X1 + 2, h.X2 - 2), h.Y - 2);
            if (h.Kind == HazardKind.Magma) Game.Fx.Emit(at, PKind.Ember, new Color("#ffb13b"), 1, 25, 0.8f, 1f, -50, Vector2.Up, 0.8f);
            else Game.Fx.Emit(at, PKind.Dot, new Color("#9dff4a"), 1, 14, 0.9f, 1.1f, -30, Vector2.Up, 0.6f);
        }
        QueueRedraw();
        _glow.QueueRedraw();
    }

    private static Vector2 ToG(System.Numerics.Vector2 v) => new(v.X, v.Y);

    public override void _Draw()
    {
        if (_map == null) return;
        var edge = Game.MapNode.Theme.Edge;

        // Saw slots, under the blades.
        foreach (var s in _map.Saws)
        {
            var a = new Vector2(s.X1, s.Y1 + 2.5f);
            var b = new Vector2(s.X2, s.Y2 + 2.5f);
            DrawLine(a - new Vector2(4, 0), b + new Vector2(4, 0), new Color(0.03f, 0.02f, 0.05f, 0.9f), 3f, true);
        }

        // Pools.
        foreach (var h in _map.Hazards) DrawPool(h);

        // Movers: dark metal slabs with a neon top and chevrons pointing the way they're heading.
        for (int i = 0; i < _movers.Count; i++)
        {
            var m = _map.Movers[i];
            float x = m.XAt(Clock);
            var r = new Rect2(x, m.Y, m.W, m.H);
            DrawColoredPolygon(Shapes.RoundRect(new Rect2(r.Position + new Vector2(2, 4), r.Size), 3), new Color(0, 0, 0, 0.28f));
            var poly = Shapes.RoundRect(r, 3);
            Shapes.Gradient(this, poly, new Color("#4a4f66"), new Color("#1d2030"));
            Shapes.DrawClosed(this, poly, new Color("#0c0d14"), 1f);
            float vel = m.XAt(Clock + 0.05f) - x;
            float dir = Mathf.Abs(vel) < 0.05f ? 0 : Mathf.Sign(vel);
            for (int k = 0; k < 3 && dir != 0; k++)
            {
                float cx = x + m.W * 0.5f + (k - 1) * 7;
                var c = new Vector2(cx, m.Y + m.H * 0.55f);
                DrawPolyline(new[] { c + new Vector2(-dir * 1.5f, -2), c + new Vector2(dir * 1.5f, 0), c + new Vector2(-dir * 1.5f, 2) },
                    Shapes.Hdr(edge, 1.1f) with { A = 0.55f }, 1.1f, true);
            }
        }

        foreach (var p in Props)
        {
            if (p.Broken) continue;
            var c = p.GlobalPosition + new Vector2(Mathf.Sin(_t * 70 + p.Index) * p.Wobble * 1.2f, 0);
            var r = new Rect2(c - p.Size * 0.5f, p.Size);
            float dmg = 1f - Mathf.Clamp(p.Hp / p.MaxHp, 0, 1);
            switch (p.Kind)
            {
                case PropKind.Crate: DrawCrate(r, dmg, p.Index); break;
                case PropKind.Barrel: DrawBarrel(r, dmg); break;
                case PropKind.Glass: DrawGlass(r, dmg, p.Index); break;
            }
        }

        foreach (var s in _map.Saws) DrawSaw(ToG(s.At(Clock)), s.Radius);
    }

    private void DrawCrate(Rect2 r, float dmg, int seed)
    {
        var wood = new Color("#a26d3d");
        DrawColoredPolygon(Shapes.RoundRect(new Rect2(r.Position + new Vector2(1.5f, 2.5f), r.Size), 2), new Color(0, 0, 0, 0.3f));
        var poly = Shapes.RoundRect(r, 1.5f);
        Shapes.Gradient(this, poly, wood.Lightened(0.15f), wood.Darkened(0.3f));
        // Frame and a diagonal brace.
        var inner = r.Grow(-2.2f);
        Shapes.DrawClosed(this, Shapes.RoundRect(inner, 0.8f), wood.Darkened(0.45f), 1f);
        DrawLine(inner.Position, inner.End, wood.Darkened(0.4f), 1.4f, true);
        DrawLine(new Vector2(r.Position.X + 1, r.Position.Y + 0.6f), new Vector2(r.End.X - 1, r.Position.Y + 0.6f), wood.Lightened(0.45f), 1f, true);
        Shapes.DrawClosed(this, poly, new Color("#2a1a0e"), 1f);
        DrawCracks(r, dmg, seed, new Color("#24160b"));
    }

    private void DrawBarrel(Rect2 r, float dmg)
    {
        var red = new Color("#c8323a");
        DrawColoredPolygon(Shapes.RoundRect(new Rect2(r.Position + new Vector2(1.5f, 2.5f), r.Size), 3), new Color(0, 0, 0, 0.3f));
        var poly = Shapes.RoundRect(r, 3f);
        Shapes.Gradient(this, poly, red.Lightened(0.2f), red.Darkened(0.35f));
        // Rims, a warning stripe and a little flame.
        foreach (float fy in new[] { 0.2f, 0.8f })
            DrawLine(new Vector2(r.Position.X + 0.5f, r.Position.Y + r.Size.Y * fy), new Vector2(r.End.X - 0.5f, r.Position.Y + r.Size.Y * fy), red.Darkened(0.55f), 1.3f, true);
        var mid = r.GetCenter();
        DrawColoredPolygon(Shapes.RoundRect(new Rect2(r.Position.X + 1.5f, mid.Y - 2.6f, r.Size.X - 3, 5.2f), 1), new Color("#ffd23b"));
        DrawColoredPolygon(new[] { mid + new Vector2(0, -2.2f), mid + new Vector2(1.6f, 1.8f), mid + new Vector2(-1.6f, 1.8f) }, new Color("#1a0d0d"));
        DrawLine(new Vector2(r.Position.X + 2.2f, r.Position.Y + 2), new Vector2(r.Position.X + 2.2f, r.End.Y - 2), new Color(1, 1, 1, 0.25f), 1f, true);
        Shapes.DrawClosed(this, poly, new Color("#2a0a0c"), 1f);
        DrawCracks(r, dmg, 7, new Color("#2a0a0c"));
    }

    private void DrawGlass(Rect2 r, float dmg, int seed)
    {
        var tint = new Color("#9ff0ff");
        DrawRect(r, tint with { A = 0.22f });
        DrawLine(r.Position + new Vector2(0, 0.5f), new Vector2(r.End.X, r.Position.Y + 0.5f), Shapes.Hdr(tint, 1.3f) with { A = 0.85f }, 1f);
        if (seed % 2 == 0) DrawLine(r.Position + new Vector2(2, r.Size.Y - 1.5f), r.Position + new Vector2(5.5f, 1.5f), new Color(1, 1, 1, 0.35f), 0.8f, true);
        DrawRect(r, tint with { A = 0.5f }, false, 0.8f);
        DrawCracks(r, dmg, seed, new Color(1, 1, 1, 0.75f));
    }

    private void DrawCracks(Rect2 r, float dmg, int seed, Color col)
    {
        if (dmg < 0.3f) return;
        var c = r.GetCenter();
        int lines = dmg < 0.65f ? 2 : 4;
        for (int i = 0; i < lines; i++)
        {
            float a = seed * 1.7f + i * Mathf.Tau / lines;
            var d = Vector2.FromAngle(a);
            float len = Mathf.Min(r.Size.X, r.Size.Y) * 0.5f;
            var mid = c + d * len * 0.5f + d.Orthogonal() * 1.2f;
            DrawPolyline(new[] { c, mid, c + d * len * 0.95f }, col, 0.8f, true);
        }
    }

    private void DrawSaw(Vector2 c, float radius)
    {
        const int teeth = 12;
        float spin = _t * 16;
        var pts = new Vector2[teeth * 2];
        for (int i = 0; i < teeth * 2; i++)
        {
            float a = spin + i * Mathf.Pi / teeth;
            pts[i] = c + Vector2.FromAngle(a) * (i % 2 == 0 ? radius : radius * 0.74f);
        }
        DrawColoredPolygon(pts, new Color("#cfd5e2"));
        Shapes.DrawClosed(this, pts, new Color("#3a3f52"), 0.9f);
        DrawCircle(c, radius * 0.62f, new Color("#9aa2b6"), true, -1, true);
        DrawArc(c, radius * 0.45f, spin, spin + Mathf.Pi * 1.3f, 10, new Color(1, 1, 1, 0.55f), 0.9f, true);
        DrawCircle(c, radius * 0.24f, new Color("#2b2f40"), true, -1, true);
        DrawCircle(c, radius * 0.1f, new Color("#e6eaf2"), true, -1, true);
    }

    private void DrawPool(Hazard h)
    {
        bool magma = h.Kind == HazardKind.Magma;
        var top = magma ? new Color("#ffcf4a") : new Color("#c6ff5a");
        var bottom = magma ? new Color("#e2401f") : new Color("#3b9a2a");
        float w = h.X2 - h.X1;
        // A shallow lens of liquid lying on the surface, its top rippling.
        int n = Mathf.Max(6, (int)(w / 3));
        var pts = new List<Vector2>();
        for (int i = 0; i <= n; i++)
        {
            float u = i / (float)n, x = h.X1 + w * u;
            float edgeK = Mathf.Max(0, Mathf.Sin(u * Mathf.Pi)); // sin(pi) is a hair below 0, and its sqrt NaN
            float wave = Mathf.Sin(_t * (magma ? 2.2f : 3f) + x * 0.35f) * 0.7f * edgeK;
            pts.Add(new Vector2(x, h.Y - 3.2f * Mathf.Sqrt(edgeK) + wave));
        }
        pts.Add(new Vector2(h.X2, h.Y + 2));
        pts.Add(new Vector2(h.X1, h.Y + 2));
        Shapes.Gradient(this, pts.ToArray(), top, bottom);
        var surface = pts.GetRange(0, n + 1).ToArray();
        DrawPolyline(surface, Shapes.Hdr(top, 1.5f), 1.1f, true);
        // Bubbles that swell and pop.
        for (int i = 0; i < (int)(w / 10); i++)
        {
            float cycle = Mathf.PosMod(_t * (0.6f + (i % 3) * 0.25f) + i * 0.37f, 1f);
            float bx = h.X1 + 4 + Mathf.PosMod(i * 17.3f + Mathf.Floor(_t * 0.6f + i * 0.37f) * 7.1f, w - 8);
            DrawArc(new Vector2(bx, h.Y - 2.2f - cycle * 1.5f), 0.6f + cycle * 1.4f, 0, Mathf.Tau, 10, top with { A = 1 - cycle }, 0.8f, true);
        }
    }

    private void DrawGlow(DrawLayer l)
    {
        if (_map == null) return;
        var edge = Game.MapNode.Theme.Edge;
        foreach (var h in _map.Hazards)
        {
            var col = h.Kind == HazardKind.Magma ? new Color("#ff7a22") : new Color("#8dff3a");
            float pulse = 0.8f + 0.2f * Mathf.Sin(_t * 2 + h.X1);
            l.DrawTextureRect(GlowTex.Tex, new Rect2(h.X1 - 10, h.Y - 18, h.X2 - h.X1 + 20, 26), false, col with { A = 0.45f * pulse });
        }
        for (int i = 0; i < _movers.Count; i++)
        {
            var m = _map.Movers[i];
            float x = m.XAt(Clock);
            l.DrawColoredPolygon(Shapes.RoundRect(new Rect2(x + 1, m.Y, m.W - 2, 2f), 1f), Shapes.Hdr(edge, 1.3f));
            l.DrawTextureRect(GlowTex.Tex, new Rect2(x - 6, m.Y - 9, m.W + 12, 18), false, edge with { A = 0.25f });
        }
        foreach (var p in Props)
        {
            if (p.Broken) continue;
            if (p.Kind == PropKind.Barrel)
            {
                float k = 0.5f + 0.5f * Mathf.Sin(_t * 4 + p.Index);
                GlowTex.Draw(l, p.GlobalPosition, 14, new Color("#ff4a2a") with { A = 0.18f + 0.12f * k });
            }
            else if (p.Kind == PropKind.Glass)
                l.DrawTextureRect(GlowTex.Tex, new Rect2(p.GlobalPosition - new Vector2(7, 7), new Vector2(14, 14)), false, new Color("#7fe8ff") with { A = 0.12f });
        }
        foreach (var s in _map.Saws)
        {
            var c = ToG(s.At(Clock));
            GlowTex.Draw(l, c, s.Radius * 2.2f, new Color("#ff3a5a") with { A = 0.22f });
            l.DrawArc(c, s.Radius + 0.6f, 0, Mathf.Tau, 24, new Color(1, 1, 1, 0.18f), 1.2f, true);
        }
    }
}
