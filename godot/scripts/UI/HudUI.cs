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

        // Player chips across the top.
        float x = 10;
        foreach (var p in _game.Slots)
        {
            if (p == null) continue;
            var col = p.Left ? Palette.TextDim : p.Color;
            float nameW = bold.GetStringSize(p.Info.Name, HorizontalAlignment.Left, -1, 13).X;
            float pipsW = s.RoundsToWin * 9;
            float w = 44 + Mathf.Max(nameW, pipsW) + 14 + (p.Cards.Count > 0 ? 12 : 0);
            var chip = new Rect2(x, 8, w, 38);
            DrawColoredPolygon(Shapes.RoundRect(chip, 12, 6), new Color(0.06f, 0.03f, 0.13f, 0.72f));
            DrawColoredPolygon(Shapes.RoundRect(new Rect2(chip.Position, new Vector2(38, 38)), 12, 6), col with { A = 0.3f });
            Shapes.DrawClosed(this, Shapes.RoundRect(chip, 12, 6), col with { A = 0.55f }, 1f);

            // Mini character head.
            AvatarArt.Draw(this, chip.Position + new Vector2(19, 27), new AvatarPose
            {
                Body = col, Look = p.Info.Look, Facing = 1, Aim = Vector2.Right, Scale = 1.15f, Time = _t, Grounded = true,
            });

            float tx = chip.Position.X + 44;
            DrawString(bold, new Vector2(tx, 23), p.Info.Name, HorizontalAlignment.Left, -1, 13, p.Left ? Palette.TextDim : Colors.White);
            for (int i = 0; i < s.RoundsToWin; i++)
            {
                var c = new Vector2(tx + 3.5f + i * 9, 32);
                if (i < p.RoundWins)
                {
                    DrawCircle(c, 3.6f, Shapes.Hdr(col.Lightened(0.15f), 1.15f), true, -1, true);
                    DrawCircle(c + new Vector2(-1, -1), 1.1f, new Color(1, 1, 1, 0.7f), true, -1, true);
                }
                else DrawCircle(c, 3.4f, new Color(1, 1, 1, 0.14f), true, -1, true);
            }
            for (int i = 0; i < s.BattlesPerRound; i++)
                DrawCircle(new Vector2(tx + 2 + i * 6, 40), 1.7f, i < p.BattleWins ? Colors.White : new Color(1, 1, 1, 0.18f), true, -1, true);
            if (p.Cards.Count > 0)
            {
                string cards = p.Cards.Count.ToString();
                var badge = new Rect2(chip.End.X - 20, chip.Position.Y - 4, 22, 15);
                Pill(badge, Palette.Accent);
                DrawString(Fonts.Heavy, badge.Position + new Vector2(0, 12), cards, HorizontalAlignment.Center, badge.Size.X, 11, new Color(0.15f, 0.08f, 0.02f));
            }
            x += w + 8;
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

        // Your loadout, bottom-left.
        var me = _game.LocalMatchPlayer;
        if (me != null && me.Cards.Count > 0 && _game.Phase != Phase.CardPick)
        {
            float bx = 10;
            foreach (var c in CardRegistry.Resolve(me.Cards))
            {
                float w = bold.GetStringSize(c.Name, HorizontalAlignment.Left, -1, 11).X + 24;
                var r = new Rect2(bx, vs.Y - 26, w, 18);
                Pill(r, new Color(0.06f, 0.03f, 0.13f, 0.7f), new Color(1, 1, 1, 0.1f));
                DrawCircle(r.Position + new Vector2(9, 9), 3.5f, UiKit.CategoryColor(c.Category), true, -1, true);
                DrawString(bold, r.Position + new Vector2(16, 13), c.Name, HorizontalAlignment.Left, -1, 11, Palette.Text);
                bx += w + 5;
            }
        }
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
