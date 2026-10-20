using System;
using System.Collections.Generic;
using System.Linq;
using Brawler.Core;
using Brawler.Fx;
using Brawler.Logic;
using Brawler.Net;
using Godot;

namespace Brawler.UI;

/// <summary>
/// The card pool, opened from the lobby: every card by category with a toggle. The host edits it (saved to
/// settings.cfg and synced to everyone); clients see the same panel read-only. At least
/// <see cref="CardPool.MinEnabled"/> cards always stay in.
/// </summary>
public partial class CardPoolUI : Control
{
    public event Action Closed;

    private readonly NetworkManager _net;
    private readonly Dictionary<int, CheckBox> _boxes = new();
    private readonly Dictionary<CardCategory, Label> _counts = new();
    private readonly List<Button> _hostOnly = new(); // All/None per category, rebuilt on Open
    private Button _reset;
    private HBoxContainer _columns;
    private Label _summary, _detail;
    private bool _warned;

    private const string HoverHint = "Point at a card to see what it does.";

    public CardPoolUI(NetworkManager net) => _net = net;
    public CardPoolUI() { }

    public override void _Ready()
    {
        // Swallows clicks meant for the lobby behind.
        var dim = new ColorRect { Color = new Color(0.02f, 0.01f, 0.06f, 0.9f), MouseFilter = MouseFilterEnum.Stop };
        dim.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(dim);

        var (_, body) = UiKit.CenteredPanel(this, 740);
        body.AddThemeConstantOverride("separation", 6);
        var top = UiKit.HBox(10);
        top.AddChild(new TitleText("CARD POOL", 24, 1.2f, Palette.Accent, Palette.Hot));
        _summary = UiKit.Label("", UiKit.Small, Palette.TextDim);
        _summary.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _summary.VerticalAlignment = VerticalAlignment.Center;
        top.AddChild(_summary);
        body.AddChild(top);

        var scroll = new ScrollContainer { CustomMinimumSize = new Vector2(0, 268), HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        body.AddChild(scroll);
        _columns = UiKit.HBox(12);
        _columns.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        scroll.AddChild(_columns);

        // The hovered card's text, so both host and clients can read what a card does.
        _detail = UiKit.Label("", UiKit.Small, Palette.TextDim);
        _detail.ClipText = true;
        _detail.CustomMinimumSize = new Vector2(0, 18);
        body.AddChild(_detail);

        var footer = UiKit.HBox(8);
        _reset = UiKit.Button("Enable all", () => Push(new HashSet<int>()), 0, "Small");
        footer.AddChild(_reset);
        var done = UiKit.Primary("DONE", Close);
        done.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        footer.AddChild(done);
        body.AddChild(footer);

        _net.LobbyChanged += Refresh;
    }

    public override void _ExitTree() => _net.LobbyChanged -= Refresh;

    /// <summary>Rebuilds the list from the registry as it is now (custom cards may have been added since startup).</summary>
    public void Open()
    {
        _boxes.Clear();
        _counts.Clear();
        _hostOnly.Clear();
        UiKit.ClearChildren(_columns);
        _warned = false;
        _detail.Text = HoverHint;
        foreach (CardCategory cat in Enum.GetValues<CardCategory>())
        {
            var cards = CardRegistry.All.Where(c => c.Category == cat).OrderBy(c => c.Name).ToList();
            if (cards.Count > 0) _columns.AddChild(Column(cat, cards));
        }
        Visible = true;
        Refresh();
    }

    private Control Column(CardCategory cat, List<CardDef> cards)
    {
        var col = UiKit.VBox(0);
        col.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        var head = UiKit.HBox(4);
        var title = UiKit.Label(cat.ToString().ToUpperInvariant(), UiKit.Small, UiKit.CategoryColor(cat), font: Fonts.Bold);
        head.AddChild(title);
        _counts[cat] = UiKit.Label("", UiKit.Tiny, Palette.TextDim);
        _counts[cat].SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _counts[cat].VerticalAlignment = VerticalAlignment.Center;
        head.AddChild(_counts[cat]);
        col.AddChild(head);

        var ids = cards.Select(c => c.Id).ToList();
        var bulk = UiKit.HBox(4);
        foreach (var (text, on) in new[] { ("All", true), ("None", false) })
        {
            var b = UiKit.Button(text, () => SetMany(ids, on), 0, "Small");
            b.AddThemeFontSizeOverride("font_size", UiKit.Tiny);
            b.FocusMode = FocusModeEnum.None;
            _hostOnly.Add(b);
            bulk.AddChild(b);
        }
        col.AddChild(bulk);
        col.AddChild(UiKit.Spacer(3));

        foreach (var card in cards)
        {
            int id = card.Id;
            var box = new CheckBox { Text = card.Name, FocusMode = FocusModeEnum.None };
            box.AddThemeFontSizeOverride("font_size", UiKit.Small);
            box.AddThemeConstantOverride("h_separation", 4);
            Compact(box);
            box.Toggled += on => SetMany(new[] { id }, on);
            string text = $"{card.Name}: {card.Description.Replace("\n", ", ")}";
            box.MouseEntered += () => _detail.Text = text;
            col.AddChild(box);
            _boxes[id] = box;
        }
        return col;
    }

    /// <summary>A slim row instead of the theme's glass button, so a whole category fits in a column.</summary>
    private static void Compact(CheckBox box)
    {
        StyleBoxFlat Row(float alpha) => new()
        {
            BgColor = new Color(1, 1, 1, alpha),
            CornerRadiusTopLeft = 6, CornerRadiusTopRight = 6, CornerRadiusBottomLeft = 6, CornerRadiusBottomRight = 6,
            ContentMarginLeft = 4, ContentMarginRight = 4, ContentMarginTop = 1, ContentMarginBottom = 1,
        };
        foreach (var state in new[] { "normal", "pressed", "disabled", "focus" }) box.AddThemeStyleboxOverride(state, Row(0));
        box.AddThemeStyleboxOverride("hover", Row(0.08f));
        box.AddThemeStyleboxOverride("hover_pressed", Row(0.08f));
        // The default empty box barely shows on dark glass; read-only rows keep the normal look.
        _unchecked ??= EmptyBox(16);
        box.AddThemeIconOverride("unchecked", _unchecked);
        box.AddThemeIconOverride("unchecked_disabled", _unchecked);
        box.AddThemeIconOverride("checked_disabled", box.GetThemeIcon("checked"));
    }

    private static ImageTexture _unchecked;

    /// <summary>A rounded square outline, the size of the theme's check mark box.</summary>
    private static ImageTexture EmptyBox(int size)
    {
        var img = Image.CreateEmpty(size, size, false, Image.Format.Rgba8);
        float half = size / 2f - 1.5f, r = 3f;
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            // Distance to a rounded square's edge, then a 1.5 px ring around it.
            var q = new Vector2(Mathf.Abs(x + 0.5f - size / 2f), Mathf.Abs(y + 0.5f - size / 2f)) - new Vector2(half - r, half - r);
            float d = new Vector2(Mathf.Max(q.X, 0), Mathf.Max(q.Y, 0)).Length() + Mathf.Min(Mathf.Max(q.X, q.Y), 0) - r;
            img.SetPixel(x, y, new Color(1, 1, 1, 0.55f * Mathf.Clamp(1.25f - Mathf.Abs(d), 0, 1)));
        }
        return ImageTexture.CreateFromImage(img);
    }

    private void SetMany(IEnumerable<int> ids, bool on)
    {
        if (!_net.IsHost) { Refresh(); return; }
        var disabled = new HashSet<int>(_net.Lobby.Settings.DisabledCards);
        _warned = false;
        foreach (int id in ids)
        {
            if (on) disabled.Remove(id);
            else if (!disabled.Contains(id))
            {
                // Never let the pool fall below what the largest pick needs.
                if (CardPool.EnabledCount(disabled) <= CardPool.MinEnabled) { _warned = true; break; }
                disabled.Add(id);
            }
        }
        Push(disabled);
    }

    private void Push(HashSet<int> disabled)
    {
        if (!_net.IsHost) return;
        var s = _net.Lobby.Settings;
        _net.SetSettings(new MatchSettings
        {
            BattlesPerRound = s.BattlesPerRound, RoundsToWin = s.RoundsToWin,
            LoserChoices = s.LoserChoices, WinnerChoices = s.WinnerChoices, DisabledCards = disabled,
        });
        CardPoolSettings.Save(disabled);
        Refresh(); // offline play broadcasts too, but refresh here anyway in case nothing comes back
    }

    private void Refresh()
    {
        if (!Visible || _summary == null) return;
        var disabled = _net.Lobby.Settings.DisabledCards;
        bool host = _net.IsHost;
        foreach (var (id, box) in _boxes)
        {
            bool on = CardPool.IsEnabled(id, disabled);
            box.SetPressedNoSignal(on);
            box.Disabled = !host;
            var color = on ? Palette.Text : Palette.TextDim with { A = 0.55f };
            box.AddThemeColorOverride("font_color", color);
            box.AddThemeColorOverride("font_pressed_color", color);
            box.AddThemeColorOverride("font_disabled_color", color);
            box.AddThemeColorOverride("font_hover_color", host ? Colors.White : color);
            box.AddThemeColorOverride("font_hover_pressed_color", host ? Colors.White : color);
        }
        foreach (var (cat, label) in _counts)
        {
            var ids = CardRegistry.All.Where(c => c.Category == cat).ToList();
            label.Text = $"{ids.Count(c => CardPool.IsEnabled(c.Id, disabled))}/{ids.Count}";
        }
        foreach (var b in _hostOnly) b.Visible = host;
        _reset.Visible = host;

        int enabled = CardPool.EnabledCount(disabled), total = CardRegistry.All.Count;
        _summary.Text = !host ? $"{enabled} of {total} cards in play. The host chooses the pool."
            : _warned ? $"{enabled} of {total} cards in play. At least {CardPool.MinEnabled} must stay in."
            : $"{enabled} of {total} cards in play. Untick cards to keep them out of picks.";
        _summary.AddThemeColorOverride("font_color", host && _warned ? Palette.Hot.Lightened(0.35f) : Palette.TextDim);
    }

    private void Close()
    {
        Visible = false;
        Closed?.Invoke();
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (!Visible || e is not InputEventKey { Pressed: true, Echo: false, Keycode: Key.Escape }) return;
        Close();
        GetViewport().SetInputAsHandled();
    }
}
