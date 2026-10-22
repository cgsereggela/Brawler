using System.Linq;
using Brawler.Core;
using Brawler.Fx;
using Brawler.Logic;
using Godot;

namespace Brawler.Game;

/// <summary>
/// Draws the game modes' objectives in the arena, behind the players: the hill with each side's held time, the flags
/// and their bases, the crown over the head, and the bubble that guards someone who just respawned.
/// </summary>
public partial class ObjectiveNode : Node2D
{
    public GameManager Game;
    private float _t;

    public override void _Process(double delta)
    {
        _t += (float)delta;
        Visible = Game.Mode != GameMode.Elimination && Game.Map != null && Game.Phase is not (Phase.Idle or Phase.CardPick or Phase.MatchOver);
        if (Visible) QueueRedraw();
    }

    public override void _Draw()
    {
        if (Game.Hill is { } hill) DrawHill(hill);
        if (Game.Flags is { } flags) DrawFlags(flags);
        if (Game.AvatarOf(Game.HeadSlot) is { } head) DrawCrown(head);
        foreach (var a in Game.LiveAvatars())
            if (a.Guarded) DrawGuard(a);
    }

    private void DrawHill(Hill hill)
    {
        float w = hill.X2 - hill.X1;
        var zone = new Rect2(hill.X1, hill.Y - Hill.Tall, w, Hill.Tall);
        bool held = hill.Holder >= 0;
        float pulse = 0.5f + 0.5f * Mathf.Sin(_t * (hill.Contested ? 10 : 3));
        var c = held ? Game.SideColor(hill.Holder) : hill.Contested ? Colors.White : Palette.Accent;

        // A column of light standing on the surface, brighter while someone holds it.
        Shapes.Gradient(this, new[] { zone.Position, new Vector2(zone.End.X, zone.Position.Y), zone.End, new Vector2(zone.Position.X, zone.End.Y) },
            c with { A = 0 }, c with { A = (held ? 0.2f : 0.1f) + 0.06f * pulse });
        foreach (float x in new[] { hill.X1, hill.X2 - 2f })
            Shapes.Gradient(this, Shapes.RoundRect(new Rect2(x, zone.Position.Y + 8, 2, Hill.Tall - 8), 0.9f), c with { A = 0 }, c with { A = 0.6f });
        DrawRect(new Rect2(hill.X1, hill.Y - 2, w, 3), Shapes.Hdr(c, held ? 1.5f : 1.1f) with { A = 0.9f });
        GlowTex.Draw(this, new Vector2(hill.CenterX, hill.Y - 4), w * 0.45f, c with { A = 0.18f + 0.1f * pulse });

        // A crown over the zone, and above it each side's held time.
        var top = new Vector2(hill.CenterX, zone.Position.Y - 6 + Mathf.Sin(_t * 2) * 1.5f);
        DrawColoredPolygon(CrownShape(top, 1f), Shapes.Hdr(c, 1.2f));
        if (hill.Contested)
            DrawString(Fonts.Bold, top + new Vector2(-40, -12), "CONTESTED", HorizontalAlignment.Center, 80, 9, Colors.White with { A = 0.6f + 0.4f * pulse });

        var sides = Enumerable.Range(0, Hill.Sides).Where(s => hill.Held[s] > 0).OrderByDescending(s => hill.Held[s]).Take(4).ToList();
        const float barW = 56, barH = 4;
        for (int i = 0; i < sides.Count; i++)
        {
            int s = sides[i];
            var at = new Vector2(hill.CenterX - barW / 2, top.Y - 22 - i * (barH + 3) - (hill.Contested ? 10 : 0));
            var sc = Game.SideColor(s);
            DrawColoredPolygon(Shapes.RoundRect(new Rect2(at - Vector2.One, new Vector2(barW + 2, barH + 2)), 2f), new Color(0.05f, 0.04f, 0.1f, 0.75f));
            float frac = Mathf.Clamp(hill.Held[s] / GameModes.HoldTime, 0, 1);
            if (frac > 0)
                DrawColoredPolygon(Shapes.RoundRect(new Rect2(at, new Vector2(Mathf.Max(barH, barW * frac), barH)), 1.6f), s == hill.Holder ? Shapes.Hdr(sc, 1.4f) : sc);
        }
    }

    private void DrawFlags(Flags flags)
    {
        for (int t = 0; t < 2; t++)
        {
            var f = flags.Team[t];
            var col = Palette.Team(t);
            // The base: a pad where the flag lives, glowing while it's there.
            var pad = new Vector2(f.Home.X, f.Home.Y + GameManager.FlagLift);
            DrawColoredPolygon(Shapes.Ellipse(pad, 18, 4, 16), col with { A = f.AtHome ? 0.45f : 0.18f });
            DrawRect(new Rect2(pad.X - 18, pad.Y - 0.5f, 36, 1), Shapes.Hdr(col, 1.3f) with { A = f.AtHome ? 0.9f : 0.35f });
            if (f.AtHome) GlowTex.Draw(this, pad + new Vector2(0, -6), 26, col with { A = 0.25f });

            Vector2 pos = new(f.Pos.X, f.Pos.Y);
            float side = 1;
            if (f.Carrier >= 0 && Game.AvatarOf(f.Carrier) is { } carrier)
            {
                // Over the carrier's shoulder, streaming behind them.
                side = carrier.AimDir.X < 0 ? 1 : -1;
                pos = carrier.GlobalPosition + new Vector2(side * 7, -4);
            }
            DrawFlag(pos, col, f.Carrier >= 0 ? side : 1);
            if (f.Dropped)
            {
                // Time left before it goes home by itself.
                float left = 1f - Mathf.Clamp(f.DroppedT / GameModes.FlagReturnTime, 0, 1);
                DrawArc(pos + new Vector2(0, -6), 15, -Mathf.Pi / 2, -Mathf.Pi / 2 + Mathf.Tau * left, 24, col.Lightened(0.3f) with { A = 0.8f }, 1.6f);
            }
        }
    }

    /// <summary>A flag whose pole stands at <paramref name="at"/> (about body height), cloth flying toward <paramref name="dir"/>.</summary>
    private void DrawFlag(Vector2 at, Color col, float dir)
    {
        var foot = at + new Vector2(0, GameManager.FlagLift);
        var tip = at + new Vector2(0, -20);
        DrawRect(new Rect2(foot.X - 0.8f, tip.Y, 1.6f, foot.Y - tip.Y), new Color("#ece6d8"));
        float wave = Mathf.Sin(_t * 7 + at.X * 0.1f);
        var cloth = new[]
        {
            tip,
            tip + new Vector2(dir * 7, 1 + wave),
            tip + new Vector2(dir * 14, 3.5f + wave * 1.6f),
            tip + new Vector2(dir * 7, 7 + wave),
            tip + new Vector2(0, 9),
        };
        GlowTex.Draw(this, tip + new Vector2(dir * 6, 4), 14, col with { A = 0.35f });
        DrawColoredPolygon(cloth, Shapes.Hdr(col, 1.25f));
        DrawCircle(tip, 1.6f, Palette.Accent);
    }

    private void DrawCrown(Avatar head)
    {
        float size = head.S[Stat.Size];
        float top = AvatarArt.TopY(head.Player.Info.Look) * size;
        var at = head.GlobalPosition + new Vector2(0, top - 21 + Mathf.Sin(_t * 3) * 1.2f);
        var gold = new Color("#ffd23d");
        GlowTex.Draw(this, at, 16, gold with { A = 0.35f });
        DrawColoredPolygon(CrownShape(at + new Vector2(0, 1), 1.15f), new Color(0.08f, 0.05f, 0.1f, 0.8f));
        DrawColoredPolygon(CrownShape(at, 1f), Shapes.Hdr(gold, 1.3f));
    }

    /// <summary>A small three-pointed crown centered on <paramref name="c"/>.</summary>
    private static Vector2[] CrownShape(Vector2 c, float k) => new[]
    {
        c + new Vector2(-7, 4) * k, c + new Vector2(-7, -3) * k, c + new Vector2(-3.5f, 0) * k, c + new Vector2(0, -5) * k,
        c + new Vector2(3.5f, 0) * k, c + new Vector2(7, -3) * k, c + new Vector2(7, 4) * k,
    };

    private void DrawGuard(Avatar a)
    {
        float r = Mathf.Max(a.HalfSize.X, a.HalfSize.Y) + 7;
        float k = 1f - Mathf.Clamp((Game.Clock - a.BornAt) / GameModes.SpawnGuard, 0, 1);
        var col = Palette.Player(a.Player.Info.ColorIdx).Lightened(0.5f);
        DrawCircle(a.GlobalPosition, r, col with { A = 0.08f * k });
        DrawArc(a.GlobalPosition, r, 0, Mathf.Tau, 32, col with { A = (0.4f + 0.3f * Mathf.Sin(_t * 18)) * k }, 1.2f);
    }
}
