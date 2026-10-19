using System.Collections.Generic;
using Godot;

namespace Brawler.Fx;

/// <summary>Cheap square-pixel particles, rings and floating text, all drawn in one canvas item.</summary>
public partial class FxLayer : Node2D
{
    private struct Particle
    {
        public Vector2 Pos, Vel;
        public float Life, MaxLife, Size, Gravity;
        public Color Color;
    }

    private struct Ring
    {
        public Vector2 Pos;
        public float R0, R1, Life, MaxLife;
        public Color Color;
    }

    private struct Text
    {
        public Vector2 Pos;
        public string Value;
        public float Life;
        public Color Color;
    }

    private readonly List<Particle> _parts = new();
    private readonly List<Ring> _rings = new();
    private readonly List<Text> _texts = new();
    private readonly RandomNumberGenerator _rng = new();

    public void Burst(Vector2 pos, Color color, int count, float speed, float life = 0.5f, float size = 2, float gravity = 300)
    {
        for (int i = 0; i < count; i++)
        {
            var dir = Vector2.FromAngle(_rng.RandfRange(0, Mathf.Tau));
            _parts.Add(new Particle
            {
                Pos = pos, Vel = dir * _rng.RandfRange(speed * 0.3f, speed),
                Life = life * _rng.RandfRange(0.6f, 1f), MaxLife = life, Size = size, Gravity = gravity,
                Color = color,
            });
        }
    }

    public void Spark(Vector2 pos, Color color, int count) => Burst(pos, color, count, 80, 0.25f, 1, 0);

    public void RingFx(Vector2 pos, float r0, float r1, Color color, float life = 0.3f)
        => _rings.Add(new Ring { Pos = pos, R0 = r0, R1 = r1, Life = life, MaxLife = life, Color = color });

    public void Popup(Vector2 pos, string text, Color color)
        => _texts.Add(new Text { Pos = pos, Value = text, Life = 0.7f, Color = color });

    public void Clear()
    {
        _parts.Clear();
        _rings.Clear();
        _texts.Clear();
    }

    public override void _Process(double delta)
    {
        float dt = (float)delta;
        for (int i = _parts.Count - 1; i >= 0; i--)
        {
            var p = _parts[i];
            p.Life -= dt;
            if (p.Life <= 0) { _parts.RemoveAt(i); continue; }
            p.Vel = p.Vel * (1f - 2.5f * dt) + new Vector2(0, p.Gravity * dt);
            p.Pos += p.Vel * dt;
            _parts[i] = p;
        }
        for (int i = _rings.Count - 1; i >= 0; i--)
        {
            var r = _rings[i];
            r.Life -= dt;
            if (r.Life <= 0) _rings.RemoveAt(i); else _rings[i] = r;
        }
        for (int i = _texts.Count - 1; i >= 0; i--)
        {
            var t = _texts[i];
            t.Life -= dt;
            t.Pos += new Vector2(0, -20 * dt);
            if (t.Life <= 0) _texts.RemoveAt(i); else _texts[i] = t;
        }
        QueueRedraw();
    }

    public override void _Draw()
    {
        foreach (var p in _parts)
        {
            float k = p.Life / p.MaxLife;
            float s = Mathf.Max(1, Mathf.Round(p.Size * (0.5f + k * 0.5f)));
            DrawRect(new Rect2(p.Pos.Round(), new Vector2(s, s)), p.Color with { A = Mathf.Min(1, k * 1.5f) });
        }
        foreach (var r in _rings)
        {
            float k = 1f - r.Life / r.MaxLife;
            float radius = Mathf.Lerp(r.R0, r.R1, 1f - (1f - k) * (1f - k));
            DrawArc(r.Pos, radius, 0, Mathf.Tau, 32, r.Color with { A = 1f - k }, 2f);
        }
        var font = ThemeDB.FallbackFont;
        foreach (var t in _texts)
            DrawString(font, t.Pos.Round() + new Vector2(-20, 0), t.Value, HorizontalAlignment.Center, 40, 10, t.Color with { A = Mathf.Min(1, t.Life * 3) });
    }
}
