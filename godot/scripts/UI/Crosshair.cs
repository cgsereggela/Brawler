using System;
using Brawler.Core;
using Brawler.Fx;
using Brawler.Game;
using Brawler.Logic;
using Godot;

namespace Brawler.UI;

/// <summary>What the crosshair shows: the magazine and reload state, plus short-lived animation values.</summary>
public struct CrosshairState
{
    public Color Tint;        // the player's color
    public bool Armed;        // alive in a battle: show the ammo ring (otherwise only the dimmed cross)
    public int Ammo, Mag;
    public bool Reloading;
    public float ReloadFrac;  // 0..1
    public float Kick;        // 1 right after a shot, decays to 0: the ticks spread
    public float Ready;       // 1 right after a reload finishes, decays to 0: a ring pulses out
    public float Time;
}

/// <summary>
/// Replaces the OS mouse pointer during a match: a crosshair in the local player's color at the mouse, with the
/// magazine as a ring of pips (one per bullet; a gauge plus a number for big magazines) and reload as an arc sweeping
/// round the ring. Lives in its own CanvasLayer above the HUD, so it renders at the window's resolution and is redrawn
/// every frame. Every stroke sits on a dark under-stroke so it reads on any arena theme. Main decides when it is
/// <see cref="Active"/> (in the arena, with no card pick, match-over screen or options overlay up); the OS cursor is
/// hidden exactly while it is.
/// </summary>
public partial class Crosshair : Node2D
{
    private readonly GameManager _game;
    /// <summary>Set by Main: whether the crosshair should replace the pointer this frame.</summary>
    public Func<bool> ShouldShow = () => false;
    public bool Active { get; private set; }

    private Avatar _tracked;
    private int _lastAmmo;
    private bool _wasReloading;
    private float _kick, _ready, _t;

    public Crosshair(GameManager game) => _game = game;
    public Crosshair() { }

    public override void _Process(double delta)
    {
        float dt = (float)delta;
        _t += dt;
        bool active = ShouldShow();
        if (active != Active)
        {
            Active = active;
            Visible = active;
        }
        // Only touch the mouse mode on a change of need, so a mode set elsewhere (none today) isn't fought every frame.
        var want = Active ? Input.MouseModeEnum.Hidden : Input.MouseModeEnum.Visible;
        if (Input.MouseMode != want && Input.MouseMode is Input.MouseModeEnum.Visible or Input.MouseModeEnum.Hidden)
            Input.MouseMode = want;
        if (!Active) return;

        var me = _game.LocalMatchPlayer;
        var a = me?.Avatar is { Alive: true } av ? av : null;
        if (a != _tracked)
        {
            _tracked = a;
            _lastAmmo = a?.Ammo ?? 0;
            _wasReloading = a?.Reloading ?? false;
            _kick = _ready = 0;
        }
        if (a != null)
        {
            if (a.Ammo < _lastAmmo) _kick = 1;
            if (_wasReloading && !a.Reloading) _ready = 1;
            _lastAmmo = a.Ammo;
            _wasReloading = a.Reloading;
        }
        _kick = Mathf.Max(0, _kick - dt * 7);
        _ready = Mathf.Max(0, _ready - dt * 4);
        QueueRedraw();
    }

    public override void _ExitTree()
    {
        if (Input.MouseMode == Input.MouseModeEnum.Hidden) Input.MouseMode = Input.MouseModeEnum.Visible;
    }

    public override void _Draw()
    {
        var me = _game.LocalMatchPlayer;
        var a = _tracked;
        var at = GetLocalMousePosition();
        // On autopilot (test runs) the mouse means nothing: put the crosshair on the bot's aim line instead.
        if (a != null && _game.AutopilotLocal)
            at = GetGlobalTransformWithCanvas().AffineInverse() * a.GetGlobalTransformWithCanvas() * (a.AimDir * 70);
        Paint(this, at, new CrosshairState
        {
            Tint = me?.Color ?? Palette.Accent,
            Armed = a != null,
            Ammo = a?.Ammo ?? 0,
            Mag = a?.S.Int(Stat.Ammo) ?? 0,
            Reloading = a?.Reloading ?? false,
            ReloadFrac = a?.ReloadFrac ?? 0,
            Kick = _kick,
            Ready = _ready,
            Time = _t,
        });
    }

    // ---------- art ----------

    public const float Radius = 12f;
    private const float RingW = 2.1f, Under = 1.9f; // ring stroke, and how much wider the dark under-stroke is
    private static readonly Color Ink = new(0.04f, 0.02f, 0.09f, 0.72f);
    private static readonly Color Empty = new(1, 1, 1, 0.2f);
    private static readonly Color Low = new("#ff4d6a");

    private static void Arc(CanvasItem ci, Vector2 c, float r, float from, float to, Color col, float w)
    {
        int pts = Mathf.Clamp((int)(Mathf.Abs(to - from) * r / 2.2f) + 2, 3, 64);
        ci.DrawArc(c, r, from, to, pts, col, w, true);
    }

    /// <summary>Under-stroke, then the colored stroke.</summary>
    private static void InkedArc(CanvasItem ci, Vector2 c, float r, float from, float to, Color col, float w)
    {
        Arc(ci, c, r, from, to, Ink, w + Under);
        Arc(ci, c, r, from, to, col, w);
    }

    /// <summary>Draws a crosshair at <paramref name="at"/>. Static so test galleries can draw any state.</summary>
    public static void Paint(CanvasItem ci, Vector2 at, CrosshairState s)
    {
        var tint = s.Tint.Lightened(0.3f);
        float alpha = s.Armed ? 1f : 0.55f;

        // The cross: four ticks around a center dot, spreading for a moment after each shot.
        float inner = 3.4f + 2.6f * s.Kick, outer = inner + 4.2f;
        for (int i = 0; i < 4; i++)
        {
            var d = Vector2.Right.Rotated(i * Mathf.Pi / 2);
            ci.DrawLine(at + d * inner, at + d * outer, Ink with { A = Ink.A * alpha }, 1.5f + Under, true);
        }
        ci.DrawCircle(at, 1.2f + Under / 2, Ink with { A = Ink.A * alpha }, true, -1, true);
        for (int i = 0; i < 4; i++)
        {
            var d = Vector2.Right.Rotated(i * Mathf.Pi / 2);
            // Faded toward white while reloading: you can't shoot yet.
            var tick = s.Reloading ? tint.Lerp(Colors.White, 0.5f) with { A = 0.6f * alpha } : tint with { A = alpha };
            ci.DrawLine(at + d * inner, at + d * outer, tick, 1.5f, true);
        }
        ci.DrawCircle(at, 1.2f, Colors.White with { A = alpha }, true, -1, true);
        if (!s.Armed) return;

        const float r = Radius;
        int mag = Math.Max(1, s.Mag);
        if (s.Reloading)
        {
            // Reload: a dim track with a bright arc sweeping clockwise from the top, and a glowing head.
            float frac = Mathf.Clamp(s.ReloadFrac, 0, 1);
            InkedArc(ci, at, r, 0, Mathf.Tau, Empty with { A = 0.14f }, RingW);
            if (frac > 0.002f)
            {
                float to = AmmoRing.Top + Mathf.Tau * frac;
                Arc(ci, at, r, AmmoRing.Top, to, Shapes.Hdr(Palette.Accent, 1.15f), RingW);
                var head = at + Vector2.Right.Rotated(to) * r;
                ci.DrawCircle(head, RingW * 0.9f + Under / 2, Ink, true, -1, true);
                ci.DrawCircle(head, RingW * 0.9f, Shapes.Hdr(Palette.Accent.Lightened(0.4f), 1.4f), true, -1, true);
            }
            return;
        }

        // Ammo: one pip per bullet, or a gauge for big magazines. The last bullet (of 3+) turns red.
        int ammo = Math.Clamp(s.Ammo, 0, mag);
        var full = ammo <= 1 && mag >= 3 ? Low : tint;
        if (AmmoRing.Continuous(mag))
        {
            InkedArc(ci, at, r, 0, Mathf.Tau, Empty, RingW);
            if (ammo > 0)
            {
                var (from, to) = AmmoRing.Gauge(ammo, mag);
                Arc(ci, at, r, from, to, full, RingW);
            }
            for (int i = AmmoRing.TickEvery; i < mag; i += AmmoRing.TickEvery)
            {
                var d = Vector2.Right.Rotated(AmmoRing.Top + Mathf.Tau * i / mag);
                ci.DrawLine(at + d * (r - RingW * 0.7f), at + d * (r + RingW * 0.7f), Ink, 0.9f, true);
            }
        }
        else
        {
            for (int i = 0; i < mag; i++)
            {
                var (from, to) = AmmoRing.Pip(i, mag, r);
                Arc(ci, at, r, from, to, Ink, RingW + Under);
            }
            for (int i = 0; i < mag; i++)
            {
                var (from, to) = AmmoRing.Pip(i, mag, r);
                Arc(ci, at, r, from, to, i < ammo ? full : Empty, RingW);
            }
        }
        if (mag >= AmmoRing.NumberFrom)
        {
            // The count as a number, tucked at the lower right of the ring.
            var font = Fonts.Heavy;
            string txt = ammo.ToString();
            var pos = at + new Vector2(r * 0.72f + 2.5f, r * 0.72f + 8.5f);
            ci.DrawStringOutline(font, pos, txt, HorizontalAlignment.Left, -1, 9, 4, Ink);
            ci.DrawString(font, pos, txt, HorizontalAlignment.Left, -1, 9, ammo <= 1 ? Low : Colors.White);
        }
        if (s.Ready > 0)
        {
            // Reload done: a ring pulses out and fades.
            float k = 1 - s.Ready;
            Arc(ci, at, r + 1 + 6 * k, 0, Mathf.Tau, Shapes.Hdr(Palette.Accent, 1.3f) with { A = s.Ready * 0.8f }, 1.4f * s.Ready + 0.3f);
        }
    }
}
