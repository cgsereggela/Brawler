using System.Linq;
using Brawler.Core;
using Brawler.Fx;
using Brawler.Game;
using Brawler.Logic;
using Godot;

namespace Brawler.UI;

/// <summary>Player chips, center banners and the loadout strip, drawn directly. Leaving is in the Esc options overlay.</summary>
public partial class HudUI : Control
{
    private readonly GameManager _game;
    private string _banner = "";
    private Color _bannerColor;
    private float _bannerT, _bannerMax, _t;
    private readonly System.Collections.Generic.List<(Rect2 Chip, MatchPlayer P)> _chips = new();
    /// <summary>Test hook (--hudshots): open this chip's hover card as if the mouse were over it.</summary>
    public int DebugHover = -1;

    public HudUI(GameManager game) => _game = game;
    public HudUI() { }

    public override void _Ready()
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore;
        _game.Banner += (text, color, seconds) =>
        {
            _banner = text;
            _bannerColor = color;
            _bannerT = _bannerMax = seconds;
        };
    }

    public override void _Process(double delta)
    {
        _bannerT -= (float)delta;
        _t += (float)delta;
        QueueRedraw();
    }

    private void Pill(Rect2 r, Color bg, Color? border = null)
    {
        var poly = Shapes.RoundRect(r, r.Size.Y / 2, 6);
        DrawColoredPolygon(poly, bg);
        if (border is { } b) Shapes.DrawClosed(this, poly, b, 1f);
    }

    public override void _Draw()
    {
        var vs = GetViewportRect().Size;
        var s = _game.Settings;
        var bold = Fonts.Bold;

        // Player chips across the top. They're laid out full size first; if that would run into the battle counter
        // (five or more players), every chip goes compact: smaller head, names clipped, tighter pips.
        _chips.Clear();
        var widths = new System.Collections.Generic.List<(MatchPlayer P, float W)>();
        bool compact = false;
        for (int pass = 0; pass < 2; pass++)
        {
            widths.Clear();
            float total = 10;
            foreach (var p in _game.Slots)
            {
                if (p == null) continue;
                float w = ChipWidth(p, compact);
                widths.Add((p, w));
                total += w + (compact ? 5 : 8);
            }
            if (compact || total <= vs.X - 130) break;
            compact = true;
        }
        float x = 10;
        foreach (var (p, w) in widths)
        {
            DrawChip(p, new Rect2(x, 8, w, compact ? 34 : 38), compact);
            x += w + (compact ? 5 : 8);
        }

        // Battle counter.
        if (_game.BattleNumber > 0)
        {
            string label = $"BATTLE {_game.BattleNumber}";
            float w = bold.GetStringSize(label, HorizontalAlignment.Left, -1, 12).X + 22;
            var r = new Rect2(vs.X - 10 - w, 10, w, 22);
            Pill(r, new Color(0.06f, 0.03f, 0.13f, 0.72f), new Color(1, 1, 1, 0.15f));
            DrawString(bold, r.Position + new Vector2(0, 16), label, HorizontalAlignment.Center, w, 12, Palette.TextDim);
            string theme = _game.MapNode.Theme.Name.ToUpperInvariant();
            DrawString(Fonts.Regular, new Vector2(vs.X - 14 - 200, 46), theme, HorizontalAlignment.Right, 200, 10, Palette.TextDim with { A = 0.7f });
        }

        DrawBanner(vs);
        DrawHoverCard(vs);
        DrawGhostHint(vs);

        // Your loadout, bottom-left.
        var me = _game.LocalMatchPlayer;
        if (me != null && me.Cards.Count > 0 && _game.Phase != Phase.CardPick)
        {
            // Centered in the floor band (MapGenerator.FloorDepth), so the strip never covers the fight.
            float bx = 10, band = vs.Y * MapGenerator.FloorDepth / MapLayout.Height;
            foreach (var c in CardRegistry.Resolve(me.Cards))
            {
                float w = bold.GetStringSize(c.Name, HorizontalAlignment.Left, -1, 11).X + 24;
                var r = new Rect2(bx, vs.Y - band / 2 - 9, w, 18);
                Pill(r, new Color(0.06f, 0.03f, 0.13f, 0.7f), new Color(1, 1, 1, 0.1f));
                DrawCircle(r.Position + new Vector2(9, 9), 3.5f, UiKit.CategoryColor(c.Category), true, -1, true);
                DrawString(bold, r.Position + new Vector2(16, 13), c.Name, HorizontalAlignment.Left, -1, 11, Palette.Text);
                bx += w + 5;
            }
        }
    }

    private const float CompactName = 50; // compact chips clip names to this width

    private float ChipWidth(MatchPlayer p, bool compact)
    {
        var s = _game.Settings;
        float badge = p.Cards.Count > 0 ? (compact ? 10 : 12) : 0;
        if (!compact)
            return 44 + Mathf.Max(Fonts.Bold.GetStringSize(p.Info.Name, HorizontalAlignment.Left, -1, 13).X, s.RoundsToWin * 9) + 14 + badge;
        float nameW = Mathf.Min(CompactName, Fonts.Bold.GetStringSize(p.Info.Name, HorizontalAlignment.Left, -1, 11).X);
        return 35 + Mathf.Max(nameW, PipStep(true) * s.RoundsToWin) + 8 + badge;
    }

    /// <summary>The name cut down with an ellipsis until it fits in <paramref name="width"/>.</summary>
    private static string Fit(string name, float width, int size)
    {
        var font = Fonts.Bold;
        if (font.GetStringSize(name, HorizontalAlignment.Left, -1, size).X <= width) return name;
        for (int n = name.Length - 1; n > 0; n--)
        {
            string s = name[..n].TrimEnd() + "…";
            if (font.GetStringSize(s, HorizontalAlignment.Left, -1, size).X <= width) return s;
        }
        return "…";
    }

    /// <summary>Spacing of the round-win pips; compact chips squeeze long matches into the name's width.</summary>
    private float PipStep(bool compact) => compact ? Mathf.Min(6, CompactName / Mathf.Max(1, _game.Settings.RoundsToWin)) : 9;

    /// <summary>One player chip: a head, the name, round-win pips with battle-win dots under them, and a card count.</summary>
    private void DrawChip(MatchPlayer p, Rect2 chip, bool compact)
    {
        var s = _game.Settings;
        var bold = Fonts.Bold;
        _chips.Add((chip, p));
        var col = p.Left ? Palette.TextDim : p.Color;
        float head = chip.Size.Y, radius = compact ? 11 : 12;
        DrawColoredPolygon(Shapes.RoundRect(chip, radius, 6), new Color(0.06f, 0.03f, 0.13f, 0.72f));
        DrawColoredPolygon(Shapes.RoundRect(new Rect2(chip.Position, new Vector2(head, head)), radius, 6), col with { A = 0.3f });
        Shapes.DrawClosed(this, Shapes.RoundRect(chip, radius, 6), col with { A = 0.55f }, 1f);

        // Mini character head.
        AvatarArt.Draw(this, chip.Position + (compact ? new Vector2(17, 25) : new Vector2(19, 27)), new AvatarPose
        {
            Body = col, Look = p.Info.Look, Facing = 1, Aim = Vector2.Right, Scale = compact ? 1f : 1.15f, Time = _t, Grounded = true,
        });

        float tx = chip.Position.X + (compact ? 38 : 44), y0 = chip.Position.Y;
        var nameCol = p.Left ? Palette.TextDim : Colors.White;
        if (compact) DrawString(bold, new Vector2(tx, y0 + 13), Fit(p.Info.Name, CompactName, 11), HorizontalAlignment.Left, -1, 11, nameCol);
        else DrawString(bold, new Vector2(tx, y0 + 15), p.Info.Name, HorizontalAlignment.Left, -1, 13, nameCol);
        float step = PipStep(compact), pipR = compact ? Mathf.Min(2.7f, step * 0.45f) : 3.6f;
        float pipY = y0 + (compact ? 21 : 24);
        for (int i = 0; i < s.RoundsToWin; i++)
        {
            var c = new Vector2(tx + pipR + i * step, pipY);
            if (i < p.RoundWins)
            {
                DrawCircle(c, pipR, Shapes.Hdr(col.Lightened(0.15f), 1.15f), true, -1, true);
                DrawCircle(c + new Vector2(-1, -1) * pipR / 3.6f, 1.1f * pipR / 3.6f, new Color(1, 1, 1, 0.7f), true, -1, true);
            }
            else DrawCircle(c, pipR * 0.94f, new Color(1, 1, 1, 0.14f), true, -1, true);
        }
        float dotStep = compact ? 5 : 6;
        for (int i = 0; i < s.BattlesPerRound; i++)
            DrawCircle(new Vector2(tx + 2 + i * dotStep, y0 + (compact ? 28 : 32)), compact ? 1.5f : 1.7f,
                i < p.BattleWins ? Colors.White : new Color(1, 1, 1, 0.18f), true, -1, true);
        if (p.Cards.Count > 0)
        {
            string cards = p.Cards.Count.ToString();
            var badge = compact ? new Rect2(chip.End.X - 17, y0 - 4, 19, 13) : new Rect2(chip.End.X - 20, y0 - 4, 22, 15);
            Pill(badge, Palette.Accent);
            DrawString(Fonts.Heavy, badge.Position + new Vector2(0, compact ? 10.5f : 12), cards, HorizontalAlignment.Center, badge.Size.X,
                compact ? 10 : 11, new Color(0.15f, 0.08f, 0.02f));
        }
    }

    /// <summary>Hovering a player chip opens a card under it: the cards they've picked, then their resulting stats.</summary>
    private void DrawHoverCard(Vector2 vs)
    {
        var mouse = GetLocalMousePosition();
        var hit = DebugHover >= 0 ? Mathf.Min(DebugHover, _chips.Count - 1) : _chips.FindIndex(c => c.Chip.Grow(2).HasPoint(mouse));
        if (hit < 0) return;
        var (chip, p) = _chips[hit];
        var bold = Fonts.Bold;
        var col = p.Left ? Palette.TextDim : p.Color;

        var stacks = StatSummary.Stacks(p.Cards);
        var stats = StatSummary.Lines(StatBlock.Compute(CardRegistry.Resolve(p.Cards)));
        const float pad = 10, row = 15, colW = 118, gap = 12;
        int cardCols = stacks.Count > 6 ? 2 : 1;
        int cardRows = Mathf.Max(1, (stacks.Count + cardCols - 1) / cardCols);
        int statRows = (stats.Count + 1) / 2;
        float w = pad * 2 + colW * 2 + gap;
        float h = pad + 18 + cardRows * row + 10 + 16 + statRows * row + pad - 2;
        var r = new Rect2(Mathf.Clamp(chip.Position.X, 6, vs.X - w - 6), chip.End.Y + 6, w, h);

        var poly = Shapes.RoundRect(r, 10, 6);
        DrawColoredPolygon(poly, new Color(0.05f, 0.03f, 0.11f, 0.93f));
        Shapes.DrawClosed(this, poly, col with { A = 0.6f }, 1f);
        // A notch pointing up at the chip.
        float nx = Mathf.Clamp(chip.Position.X + chip.Size.X / 2, r.Position.X + 14, r.End.X - 14);
        DrawColoredPolygon(new[] { new Vector2(nx - 6, r.Position.Y + 0.5f), new Vector2(nx, r.Position.Y - 5), new Vector2(nx + 6, r.Position.Y + 0.5f) },
            new Color(0.05f, 0.03f, 0.11f, 0.93f));
        DrawPolyline(new[] { new Vector2(nx - 6, r.Position.Y), new Vector2(nx, r.Position.Y - 5), new Vector2(nx + 6, r.Position.Y) }, col with { A = 0.6f }, 1f, true);

        float x0 = r.Position.X + pad, y = r.Position.Y + pad + 10;
        DrawString(bold, new Vector2(x0, y), "CARDS", HorizontalAlignment.Left, -1, 10, Palette.TextDim);
        if (stacks.Count > 0)
        {
            string count = $"{p.Cards.Count}";
            DrawString(bold, new Vector2(r.End.X - pad - 60, y), count, HorizontalAlignment.Right, 60, 10, Palette.Accent);
        }
        y += 8;
        if (stacks.Count == 0)
            DrawString(Fonts.Regular, new Vector2(x0, y + 11), "No cards yet", HorizontalAlignment.Left, -1, 11, Palette.TextDim with { A = 0.7f });
        for (int i = 0; i < stacks.Count; i++)
        {
            var (card, n) = stacks[i];
            var at = new Vector2(x0 + i / cardRows * (colW + gap), y + i % cardRows * row);
            DrawCircle(at + new Vector2(3.5f, 7.5f), 3.2f, UiKit.CategoryColor(card.Category), true, -1, true);
            float maxW = (cardCols == 1 ? w - pad * 2 : colW) - 12 - (n > 1 ? 22 : 0);
            DrawString(bold, at + new Vector2(11, 11.5f), card.Name, HorizontalAlignment.Left, maxW, 11, Palette.Text);
            if (n > 1)
            {
                float nameW = Mathf.Min(maxW, bold.GetStringSize(card.Name, HorizontalAlignment.Left, -1, 11).X);
                DrawString(Fonts.Heavy, at + new Vector2(15 + nameW, 11.5f), $"x{n}", HorizontalAlignment.Left, -1, 10, Palette.Accent);
            }
        }
        y += cardRows * row + 6;
        DrawLine(new Vector2(x0, y), new Vector2(r.End.X - pad, y), new Color(1, 1, 1, 0.1f), 1);
        y += 14;
        DrawString(bold, new Vector2(x0, y), "STATS", HorizontalAlignment.Left, -1, 10, Palette.TextDim);
        y += 2;
        for (int i = 0; i < stats.Count; i++)
        {
            var line = stats[i];
            var at = new Vector2(x0 + i % 2 * (colW + gap), y + i / 2 * row + 11.5f);
            DrawString(Fonts.Regular, at, line.Label, HorizontalAlignment.Left, colW - 40, 11, Palette.TextDim);
            DrawString(bold, at, line.Value, HorizontalAlignment.Right, colW, 11, line.Changed ? Palette.Accent : Palette.Text);
        }
    }

    /// <summary>While you're a ghost: a reminder that you can still emote and ping until the next battle.</summary>
    private void DrawGhostHint(Vector2 vs)
    {
        if (_game.LocalMatchPlayer?.Avatar is not { GhostUp: true } || _game.Phase != Phase.Fighting) return;
        string emotes = string.Join("/", Enumerable.Range(0, Emotes.All.Length)
            .Select(i => Keybinds.Current.Row(Emotes.Action(i)).FirstOrDefault(b => !b.IsNone))
            .Where(b => !b.IsNone).Select(Keybinds.Label));
        string text = $"YOU'RE A GHOST  ·  {(emotes.Length > 0 ? emotes + " emote  ·  " : "")}{Keybinds.Hint("ping")} ping";
        var font = Fonts.Bold;
        float w = font.GetStringSize(text, HorizontalAlignment.Left, -1, 12).X + 28;
        var r = new Rect2((vs.X - w) / 2, 58, w, 24);
        float pulse = 0.75f + 0.25f * Mathf.Sin(_t * 3);
        Pill(r, new Color(0.06f, 0.03f, 0.13f, 0.7f), Palette.Cool with { A = 0.4f * pulse });
        DrawString(font, r.Position + new Vector2(0, 17), text, HorizontalAlignment.Center, w, 12, Palette.Text with { A = 0.9f });
    }

    private void DrawBanner(Vector2 vs)
    {
        if (_bannerT <= 0 || _banner.Length == 0) return;
        float age = _bannerMax - _bannerT;
        float alpha = Mathf.Min(1, _bannerT * 4) * Mathf.Min(1, age * 10);
        float pop = 1f + 0.5f * Mathf.Exp(-age * 12f);
        bool small = _banner.Length > 14;
        int size = (int)((small ? 30 : 46) * pop);
        var font = Fonts.Heavy;
        float y = vs.Y * 0.4f;

        // A slanted light band sweeps in behind the text.
        float sweep = Mathf.Min(1, age * 5);
        float bandH = size * 1.05f;
        var c0 = _bannerColor with { A = 0 };
        var c1 = _bannerColor with { A = 0.35f * alpha };
        float bw = vs.X * sweep;
        float left = (vs.X - bw) / 2;
        DrawPolygon(new[]
        {
            new Vector2(left + 20, y - bandH * 0.62f), new Vector2(left + bw + 20, y - bandH * 0.62f),
            new Vector2(left + bw - 20, y + bandH * 0.38f), new Vector2(left - 20, y + bandH * 0.38f),
        }, new[] { c0, c1, c1, c0 });

        float w = font.GetStringSize(_banner, HorizontalAlignment.Left, -1, size).X;
        var at = new Vector2((vs.X - w) / 2, y + size * 0.18f);
        int outline = Mathf.Max(5, size / 6);
        DrawStringOutline(font, at + new Vector2(0, size * 0.08f), _banner, HorizontalAlignment.Left, -1, size, outline, _bannerColor.Darkened(0.6f) with { A = alpha });
        DrawStringOutline(font, at, _banner, HorizontalAlignment.Left, -1, size, outline, new Color(0.07f, 0.03f, 0.13f, alpha));
        DrawString(font, at, _banner, HorizontalAlignment.Left, -1, size, _bannerColor.Lightened(0.3f) with { A = alpha });
    }
}
