using Brawler.Core;
using Brawler.Game;
using Brawler.Logic;
using Godot;

namespace Brawler.UI;

/// <summary>Score chips, center banners and the double-Esc leave prompt, drawn directly.</summary>
public partial class HudUI : Control
{
    private readonly GameManager _game;
    private string _banner = "";
    private Color _bannerColor;
    private float _bannerT, _bannerMax;
    private ulong _escAt;

    public event System.Action LeaveRequested;

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
        QueueRedraw();
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (!Visible || e is not InputEventKey { Pressed: true, Keycode: Key.Escape }) return;
        ulong now = Time.GetTicksMsec();
        if (now - _escAt < 1500) LeaveRequested?.Invoke();
        else
        {
            _escAt = now;
            _banner = "Press Esc again to leave";
            _bannerColor = Palette.TextDim;
            _bannerT = _bannerMax = 1.5f;
        }
    }

    public override void _Draw()
    {
        var font = ThemeDB.FallbackFont;
        var vs = GetViewportRect().Size;
        var s = _game.Settings;

        // Player chips across the top.
        float x = 12;
        foreach (var p in _game.Slots)
        {
            if (p == null) continue;
            var col = p.Left ? Palette.TextDim : p.Color;
            DrawRect(new Rect2(x, 12, 8, 8), col);
            DrawString(font, new Vector2(x + 11, 20), p.Info.Name, HorizontalAlignment.Left, -1, 10, p.Left ? Palette.TextDim : Palette.Text);
            float nameW = font.GetStringSize(p.Info.Name, HorizontalAlignment.Left, -1, 10).X;

            // Round pips (big) and battle pips (small) for the current round.
            float px = x + 11;
            for (int i = 0; i < s.RoundsToWin; i++)
            {
                var r = new Rect2(px + i * 6, 24, 4, 4);
                DrawRect(r, i < p.RoundWins ? col : new Color(1, 1, 1, 0.15f));
            }
            for (int i = 0; i < s.BattlesPerRound; i++)
            {
                var r = new Rect2(px + i * 4, 30, 2, 2);
                DrawRect(r, i < p.BattleWins ? Colors.White : new Color(1, 1, 1, 0.15f));
            }
            if (p.Cards.Count > 0)
                DrawString(font, new Vector2(px, 42), $"{p.Cards.Count} card{(p.Cards.Count == 1 ? "" : "s")}", HorizontalAlignment.Left, -1, 8, Palette.TextDim);
            x += Mathf.Max(nameW, s.RoundsToWin * 6) + 30;
        }

        // Battle counter.
        if (_game.BattleNumber > 0)
        {
            string label = $"Battle {_game.BattleNumber}";
            float w = font.GetStringSize(label, HorizontalAlignment.Left, -1, 8).X;
            DrawString(font, new Vector2(vs.X - 12 - w, 20), label, HorizontalAlignment.Left, -1, 8, Palette.TextDim);
        }

        // Banner.
        if (_bannerT > 0 && _banner.Length > 0)
        {
            float k = Mathf.Clamp(_bannerT / Mathf.Max(0.01f, _bannerMax), 0, 1);
            float alpha = Mathf.Min(1, _bannerT * 4);
            float pop = 1f + 0.25f * Mathf.Pow(k, 8);
            int size = (int)(24 * pop);
            var pos = new Vector2(0, vs.Y * 0.36f);
            DrawString(font, pos + new Vector2(2, 2), _banner, HorizontalAlignment.Center, vs.X, size, new Color(0, 0, 0, 0.6f * alpha));
            DrawString(font, pos, _banner, HorizontalAlignment.Center, vs.X, size, _bannerColor with { A = alpha });
        }

        // Your loadout, bottom-left.
        var me = _game.LocalMatchPlayer;
        if (me != null && me.Cards.Count > 0 && _game.Phase != Phase.CardPick)
        {
            float bx = 12;
            foreach (var c in CardRegistry.Resolve(me.Cards))
            {
                float w = font.GetStringSize(c.Name, HorizontalAlignment.Left, -1, 8).X + 6;
                DrawRect(new Rect2(bx, vs.Y - 16, w, 11), new Color(0, 0, 0, 0.4f));
                DrawRect(new Rect2(bx, vs.Y - 16, 1, 11), UiKit.CategoryColor(c.Category));
                DrawString(font, new Vector2(bx + 3, vs.Y - 8), c.Name, HorizontalAlignment.Left, -1, 8, Palette.TextDim);
                bx += w + 3;
            }
        }
    }
}
