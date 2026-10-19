using System.Collections.Generic;
using Godot;

namespace Brawler.Fx;

public enum PKind
{
    Dot,      // glowing dot
    Streak,   // glowing line along the velocity
    Ember,    // flickering hot spark that floats up
    Star,     // spinning 4-point sparkle
    Chunk,    // solid tumbling debris
    Confetti, // fluttering paper
    Shard,    // tumbling triangle
    Smoke,    // soft puff that grows and fades
}

/// <summary>
/// All transient effects: particles, shockwave rings, light rays, glow flashes, screen flashes and popup text.
/// Glowing things go to an additive layer (and push HDR colors into the bloom); solid debris and text go to normal
/// layers. Everything is cosmetic and local to each peer.
/// </summary>
public partial class FxLayer : Node2D
{
    private const int MaxParticles = 2200;

    private struct Particle
    {
        public Vector2 Pos, Vel, Home;
        public float Life, MaxLife, Size, Gravity, Drag, Rot, Spin, HomeK;
        public Color Color;
        public PKind Kind;
    }

    private struct Ring
    {
        public Vector2 Pos;
        public float R0, R1, Life, MaxLife, Width, Delay;
        public Color Color;
    }

    private struct Rays
    {
        public Vector2 Pos;
        public int Count;
        public float Length, Life, MaxLife, Rot;
        public Color Color;
    }

    private struct Glow
    {
        public Vector2 Pos;
        public float Radius, Life, MaxLife;
        public Color Color;
    }

    private struct Text
    {
        public Vector2 Pos;
        public string Value;
        public float Life, MaxLife, Size, Age;
        public Color Color;
    }

    private readonly List<Particle> _parts = new();
    private readonly List<Ring> _rings = new();
    private readonly List<Rays> _rays = new();
    private readonly List<Glow> _glows = new();
    private readonly List<Text> _texts = new();
    private readonly List<(Vector2[] Pts, Color Color, float Life, float MaxLife)> _bolts = new();
    private readonly RandomNumberGenerator _rng = new();
    private Color _screenColor;
    private float _screenLife, _screenMax;
    private DrawLayer _solid, _add, _top;

    /// <summary>Area covered by screen flashes (the arena).</summary>
    public Rect2 ScreenRect = new(-40, -40, 880, 530);

    public override void _Ready()
    {
        _solid = new DrawLayer(DrawSolid);
        _add = new DrawLayer(DrawAdditive, additive: true);
        _top = new DrawLayer(DrawTop);
        AddChild(_solid);
        AddChild(_add);
        AddChild(_top);
    }

    // ---------- emitters ----------

    /// <summary>
    /// Emits <paramref name="count"/> particles. With a <paramref name="dir"/>, they fan out within
    /// <paramref name="spread"/> radians of it; otherwise in every direction.
    /// </summary>
    public void Emit(Vector2 pos, PKind kind, Color color, int count, float speed, float life, float size,
        float gravity = 0, Vector2? dir = null, float spread = Mathf.Tau, float drag = 2.5f)
    {
        for (int i = 0; i < count && _parts.Count < MaxParticles; i++)
        {
            float a = dir is { } d ? d.Angle() + _rng.RandfRange(-spread * 0.5f, spread * 0.5f) : _rng.RandfRange(0, Mathf.Tau);
            float l = life * _rng.RandfRange(0.6f, 1f);
            _parts.Add(new Particle
            {
                Pos = pos, Vel = Vector2.FromAngle(a) * _rng.RandfRange(speed * 0.3f, speed),
                Life = l, MaxLife = l, Size = size * _rng.RandfRange(0.7f, 1.2f), Gravity = gravity, Drag = drag,
                Rot = _rng.RandfRange(0, Mathf.Tau), Spin = _rng.RandfRange(-12f, 12f), Color = color, Kind = kind,
            });
        }
    }

    /// <summary>Particles that burst out, then get pulled toward <paramref name="home"/> (life-steal motes).</summary>
    public void EmitHoming(Vector2 pos, Vector2 home, Color color, int count, float speed, float life, float size)
    {
        for (int i = 0; i < count && _parts.Count < MaxParticles; i++)
        {
            float l = life * _rng.RandfRange(0.75f, 1f);
            _parts.Add(new Particle
            {
                Pos = pos, Vel = Vector2.FromAngle(_rng.RandfRange(0, Mathf.Tau)) * _rng.RandfRange(speed * 0.4f, speed),
                Life = l, MaxLife = l, Size = size, Drag = 3f, Color = color, Kind = PKind.Dot, Home = home, HomeK = 1,
            });
        }
    }

    public void Spark(Vector2 pos, Color color, int count) => Emit(pos, PKind.Streak, color, count, 140, 0.22f, 0.9f);

    public void RingFx(Vector2 pos, float r0, float r1, Color color, float life = 0.3f, float width = 2f, float delay = 0)
        => _rings.Add(new Ring { Pos = pos, R0 = r0, R1 = r1, Life = life, MaxLife = life, Color = color, Width = width, Delay = delay });

    public void RaysFx(Vector2 pos, Color color, int count, float length, float life = 0.4f)
        => _rays.Add(new Rays { Pos = pos, Color = color, Count = count, Length = length, Life = life, MaxLife = life, Rot = _rng.RandfRange(0, Mathf.Tau) });

    public void Flash(Vector2 pos, float radius, Color color, float life = 0.2f)
        => _glows.Add(new Glow { Pos = pos, Radius = radius, Color = color, Life = life, MaxLife = life });

    public void ScreenFlash(Color color, float life = 0.12f)
    {
        _screenColor = color;
        _screenLife = _screenMax = life;
    }

    public void Popup(Vector2 pos, string text, Color color, float size = 13, float life = 0.8f)
        => _texts.Add(new Text { Pos = pos, Value = text, Life = life, MaxLife = life, Color = color, Size = size });

    public void Clear()
    {
        _parts.Clear();
        _rings.Clear();
        _rays.Clear();
        _glows.Clear();
        _texts.Clear();
        _bolts.Clear();
        _screenLife = 0;
    }

    // ---------- update ----------

    public override void _Process(double delta)
    {
        float dt = (float)delta;
        for (int i = _parts.Count - 1; i >= 0; i--)
        {
            var p = _parts[i];
            p.Life -= dt;
            if (p.Life <= 0) { _parts.RemoveAt(i); continue; }
            if (p.HomeK > 0)
            {
                // Seek home harder as the particle ages.
                float age = 1f - p.Life / p.MaxLife;
                var to = p.Home - p.Pos;
                p.Vel = p.Vel.Lerp(to.Normalized() * Mathf.Max(220, to.Length() * 5), Mathf.Clamp(age * 6 * dt * 10, 0, 1));
                if (to.LengthSquared() < 16) p.Life = Mathf.Min(p.Life, 0.05f);
            }
            else
            {
                p.Vel = p.Vel * (1f - p.Drag * dt) + new Vector2(0, p.Gravity * dt);
                if (p.Kind == PKind.Confetti) p.Vel.X += Mathf.Sin(p.Life * 9 + p.Rot) * 60 * dt;
            }
            p.Pos += p.Vel * dt;
            p.Rot += p.Spin * dt;
            _parts[i] = p;
        }
        Tick(_rings, dt, (ref Ring r, float d) => { if (r.Delay > 0) r.Delay -= d; else r.Life -= d; return r.Life > 0; });
        Tick(_rays, dt, (ref Rays r, float d) => { r.Life -= d; r.Rot += d * 0.6f; return r.Life > 0; });
        Tick(_glows, dt, (ref Glow g, float d) => { g.Life -= d; return g.Life > 0; });
        for (int i = _bolts.Count - 1; i >= 0; i--)
        {
            var b = _bolts[i];
            b.Life -= dt;
            if (b.Life <= 0) _bolts.RemoveAt(i);
            else _bolts[i] = b;
        }
        Tick(_texts, dt, (ref Text t, float d) =>
        {
            t.Life -= d;
            t.Age += d;
            t.Pos += new Vector2(0, -26 * d * Mathf.Clamp(t.Life / t.MaxLife * 2, 0, 1));
            return t.Life > 0;
        });
        _screenLife = Mathf.Max(0, _screenLife - dt);
        _solid.QueueRedraw();
        _add.QueueRedraw();
        _top.QueueRedraw();
    }

    private delegate bool Updater<T>(ref T item, float dt);

    private static void Tick<T>(List<T> list, float dt, Updater<T> update) where T : struct
    {
        for (int i = list.Count - 1; i >= 0; i--)
        {
            var item = list[i];
            if (update(ref item, dt)) list[i] = item;
            else list.RemoveAt(i);
        }
    }

    // ---------- drawing ----------

    private void DrawSolid(DrawLayer l)
    {
        foreach (var p in _parts)
        {
            float k = p.Life / p.MaxLife;
            switch (p.Kind)
            {
                case PKind.Smoke:
                {
                    float r = p.Size * (1f + (1f - k) * 1.8f);
                    l.DrawCircle(p.Pos, r, p.Color with { A = p.Color.A * 0.45f * k }, true, -1, true);
                    break;
                }
                case PKind.Chunk:
                {
                    float s = p.Size * (0.5f + 0.5f * Mathf.Min(1, k * 2));
                    var poly = Quad(p.Pos, s, s, p.Rot);
                    l.DrawColoredPolygon(poly, p.Color with { A = Mathf.Min(1, k * 3) });
                    l.DrawColoredPolygon(Quad(p.Pos - new Vector2(s * 0.15f, s * 0.15f), s * 0.45f, s * 0.45f, p.Rot), p.Color.Lightened(0.35f) with { A = Mathf.Min(1, k * 3) });
                    break;
                }
                case PKind.Confetti:
                {
                    float flip = Mathf.Abs(Mathf.Cos(p.Rot * 1.3f));
                    l.DrawColoredPolygon(Quad(p.Pos, p.Size * (0.25f + 0.75f * flip), p.Size * 0.55f, p.Rot), p.Color with { A = Mathf.Min(1, k * 2.5f) });
                    break;
                }
                case PKind.Shard:
                {
                    var a = Vector2.FromAngle(p.Rot) * p.Size;
                    var b = Vector2.FromAngle(p.Rot + 2.4f) * p.Size * 0.6f;
                    var c = Vector2.FromAngle(p.Rot + 3.9f) * p.Size * 0.5f;
                    l.DrawColoredPolygon(new[] { p.Pos + a, p.Pos + b, p.Pos + c }, p.Color with { A = Mathf.Min(1, k * 2.5f) });
                    break;
                }
            }
        }
    }

    /// <summary>A jagged lightning bolt between two points.</summary>
    public void Bolt(Vector2 from, Vector2 to, Color color, float life = 0.25f)
    {
        int n = Mathf.Clamp((int)(from.DistanceTo(to) / 10), 3, 24);
        var normal = (to - from).Normalized().Orthogonal();
        var pts = new Vector2[n + 1];
        for (int i = 0; i <= n; i++)
        {
            float t = i / (float)n;
            float jag = i == 0 || i == n ? 0 : _rng.RandfRange(-7f, 7f);
            pts[i] = from.Lerp(to, t) + normal * jag;
        }
        _bolts.Add((pts, color, life, life));
    }

    private void DrawAdditive(DrawLayer l)
    {
        foreach (var g in _glows)
        {
            float k = g.Life / g.MaxLife;
            GlowTex.Draw(l, g.Pos, g.Radius * (1.2f - 0.4f * k), g.Color with { A = k * k });
        }
        foreach (var b in _bolts)
        {
            float k = b.Life / b.MaxLife;
            l.DrawPolyline(b.Pts, b.Color with { A = 0.5f * k }, 4f, true);
            l.DrawPolyline(b.Pts, Shapes.Hdr(Colors.White, 1.8f) with { A = k }, 1.3f, true);
        }
        foreach (var r in _rays)
        {
            float k = r.Life / r.MaxLife;
            float len = r.Length * (0.6f + 0.4f * (1 - k * k));
            for (int i = 0; i < r.Count; i++)
            {
                float a = r.Rot + i * Mathf.Tau / r.Count;
                float w = 0.09f + 0.05f * (i % 2);
                var tip = r.Pos + Vector2.FromAngle(a) * len * (i % 2 == 0 ? 1f : 0.7f);
                l.DrawPolygon(new[] { r.Pos, r.Pos + Vector2.FromAngle(a - w) * len * 0.3f, tip, r.Pos + Vector2.FromAngle(a + w) * len * 0.3f },
                    new[] { r.Color with { A = 0.7f * k }, r.Color with { A = 0.4f * k }, r.Color with { A = 0 }, r.Color with { A = 0.4f * k } });
            }
        }
        foreach (var r in _rings)
        {
            if (r.Delay > 0) continue;
            float k = 1f - r.Life / r.MaxLife;
            float radius = Mathf.Lerp(r.R0, r.R1, 1f - (1f - k) * (1f - k));
            l.DrawArc(r.Pos, radius, 0, Mathf.Tau, 48, Shapes.Hdr(r.Color, 1.5f) with { A = 1f - k }, r.Width * (1.3f - k), true);
        }
        foreach (var p in _parts)
        {
            float k = p.Life / p.MaxLife;
            switch (p.Kind)
            {
                case PKind.Dot:
                    GlowTex.Draw(l, p.Pos, p.Size * 4, p.Color with { A = 0.35f * k });
                    l.DrawCircle(p.Pos, p.Size * (0.4f + 0.6f * k), Shapes.Hdr(p.Color, 1.6f) with { A = Mathf.Min(1, k * 2) }, true, -1, true);
                    break;
                case PKind.Streak:
                {
                    var tail = p.Vel * 0.035f;
                    if (tail.LengthSquared() < 1) tail = tail.Normalized();
                    l.DrawLine(p.Pos, p.Pos - tail, Shapes.Hdr(p.Color, 1.8f) with { A = Mathf.Min(1, k * 2) }, p.Size * (0.5f + 0.5f * k), true);
                    break;
                }
                case PKind.Ember:
                {
                    float flick = 0.6f + 0.4f * Mathf.Sin(p.Life * 40 + p.Rot * 5);
                    GlowTex.Draw(l, p.Pos, p.Size * 5, p.Color with { A = 0.3f * k * flick });
                    l.DrawCircle(p.Pos, p.Size * 0.7f * (0.5f + 0.5f * k), Shapes.Hdr(p.Color.Lightened(0.3f), 2f) with { A = k * flick }, true, -1, true);
                    break;
                }
                case PKind.Star:
                    l.DrawColoredPolygon(Shapes.Star(p.Pos, p.Size * (0.6f + 0.6f * k), p.Size * 0.25f, 4, p.Rot), Shapes.Hdr(p.Color, 1.8f) with { A = Mathf.Min(1, k * 2) });
                    break;
            }
        }
        if (_screenLife > 0)
            l.DrawRect(ScreenRect, _screenColor with { A = _screenColor.A * (_screenLife / _screenMax) });
    }

    private void DrawTop(DrawLayer l)
    {
        foreach (var t in _texts)
        {
            float k = t.Life / t.MaxLife;
            float pop = 1f + 0.7f * Mathf.Exp(-t.Age * 16f);
            float alpha = Mathf.Min(1, k * 4);
            int size = Mathf.Max(6, Mathf.RoundToInt(t.Size * pop));
            var font = Fonts.Heavy;
            float w = font.GetStringSize(t.Value, HorizontalAlignment.Left, -1, size).X;
            var at = t.Pos + new Vector2(-w / 2, size * 0.35f);
            l.DrawStringOutline(font, at + new Vector2(0, 1.2f), t.Value, HorizontalAlignment.Left, -1, size, Mathf.Max(3, size / 4), new Color(0.05f, 0.02f, 0.1f, 0.55f * alpha));
            l.DrawStringOutline(font, at, t.Value, HorizontalAlignment.Left, -1, size, Mathf.Max(2, size / 5), new Color(0.08f, 0.04f, 0.14f, alpha));
            l.DrawString(font, at, t.Value, HorizontalAlignment.Left, -1, size, t.Color with { A = alpha });
        }
    }

    private static Vector2[] Quad(Vector2 c, float w, float h, float rot)
    {
        var x = Vector2.FromAngle(rot) * w * 0.5f;
        var y = Vector2.FromAngle(rot + Mathf.Pi / 2) * h * 0.5f;
        return new[] { c - x - y, c + x - y, c + x + y, c - x + y };
    }
}
