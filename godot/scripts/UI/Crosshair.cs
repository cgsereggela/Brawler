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
    public bool Armed;        // alive in a battle: show the ammo pips (otherwise only the dimmed cross)
    public int Ammo, Mag;
    public bool Reloading;
    public float ReloadFrac;  // 0..1
    public float Kick;        // 1 right after a shot, decays to 0: the ticks spread
    public float Ready;       // 1 right after a reload finishes, decays to 0: the full pips flash
    public float Time;
}

/// <summary>
/// Replaces the OS mouse pointer during a match: a crosshair in the local player's color at the mouse, with the
/// magazine as small pips at its lower right (one per bullet; a gauge plus a number for big magazines, see
/// <see cref="AmmoPips"/>) and reload as a small dial at its upper right. Lives in its own CanvasLayer above the HUD, so it renders at the window's resolution and is redrawn
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
        // Dead: the ghost floats after the mouse and shows where it is, so the crosshair steps aside (the pointer stays hidden).
        if (me?.Avatar is { Alive: false } dead && IsInstanceValid(dead)) return;
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

    private const float Under = 1.9f; // how much wider the dark under-stroke is
    private const float Top = -Mathf.Pi / 2;
    private static readonly Color Ink = new(0.04f, 0.02f, 0.09f, 0.72f);
    private static readonly Color Empty = new(1, 1, 1, 0.22f);
    private static readonly Color Low = new("#ff4d6a");
    /// <summary>The small reload dial: its radius, and its center relative to the crosshair (upper right, mirroring the pips).</summary>
    private const float ReloadDialR = 3.6f;
    private static readonly Vector2 ReloadDialAt = new(AmmoPips.X0 + ReloadDialR, -AmmoPips.Y0 - ReloadDialR);

    private static void Arc(CanvasItem ci, Vector2 c, float r, float from, float to, Color col, float w)
    {
        int pts = Mathf.Clamp((int)(Mathf.Abs(to - from) * r / 2.2f) + 2, 3, 64);
        ci.DrawArc(c, r, from, to, pts, col, w, true);
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

        int mag = Math.Max(1, s.Mag);
        if (s.Reloading)
        {
            // Reload: a small dial at the upper right, filling clockwise from the top, so the crosshair itself stays
            // calm (the pips at the lower right keep showing the empty magazine).
            const float dr = ReloadDialR;
            var dial = at + ReloadDialAt;
            float frac = Mathf.Clamp(s.ReloadFrac, 0, 1);
            ci.DrawCircle(dial, dr + 1.6f, Ink, true, -1, true);
            Arc(ci, dial, dr, 0, Mathf.Tau, Empty with { A = 0.16f }, 1.7f);
            if (frac > 0.002f) Arc(ci, dial, dr, Top, Top + Mathf.Tau * frac, Shapes.Hdr(Palette.Accent, 1.15f), 1.7f);
        }

        // Ammo: small upright pips at the lower right, one per bullet, or a short gauge plus the count for big
        // magazines. The last bullet (of 3+) turns red; just after a reload the full pips flash bright.
        int ammo = Math.Clamp(s.Ammo, 0, mag);
        var full = ammo <= 1 && mag >= 3 ? Low : tint;
        if (s.Ready > 0) full = Shapes.Hdr(full.Lerp(Colors.White, 0.5f * s.Ready), 1 + 0.6f * s.Ready);
        var corner = at + new Vector2(AmmoPips.X0, AmmoPips.Y0);
        float w = AmmoPips.Width(mag);
        // One dark plate behind the whole block, so it reads on any theme.
        float h = AmmoPips.Rows(mag) * (AmmoPips.PipH + AmmoPips.GapY) - AmmoPips.GapY;
        ci.DrawColoredPolygon(Shapes.RoundRect(new Rect2(corner - new Vector2(1.6f, 1.6f), new Vector2(w + 3.2f, h + 3.2f)), 1.8f), Ink);
        if (AmmoPips.Continuous(mag))
        {
            var bar = new Rect2(corner, new Vector2(w, AmmoPips.PipH));
            ci.DrawColoredPolygon(Shapes.RoundRect(bar, 0.8f), Empty);
            float f = AmmoPips.Gauge(ammo, mag);
            if (f > 0) ci.DrawColoredPolygon(Shapes.RoundRect(new Rect2(corner, new Vector2(Mathf.Max(1.6f, w * f), AmmoPips.PipH)), 0.8f), full);
            var font = Fonts.Heavy;
            var pos = corner + new Vector2(w + 3.5f, AmmoPips.PipH + 2.4f);
            ci.DrawStringOutline(font, pos, ammo.ToString(), HorizontalAlignment.Left, -1, 9, 4, Ink);
            ci.DrawString(font, pos, ammo.ToString(), HorizontalAlignment.Left, -1, 9, ammo <= 1 ? Low : Colors.White);
        }
        else
            for (int i = 0; i < mag; i++)
            {
                var (x, y) = AmmoPips.Pip(i);
                var pip = Shapes.RoundRect(new Rect2(at + new Vector2(x, y), new Vector2(AmmoPips.PipW, AmmoPips.PipH)), 0.7f);
                ci.DrawColoredPolygon(pip, i < ammo ? full : Empty);
            }
    }
}
