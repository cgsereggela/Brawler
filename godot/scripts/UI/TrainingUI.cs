using System;
using System.Collections.Generic;
using System.Linq;
using Brawler.Core;
using Brawler.Fx;
using Brawler.Logic;
using Godot;

namespace Brawler.UI;

/// <summary>
/// The training range's card browser, over the left of the arena: every card (rares and curses too) to search, filter
/// by category and click onto yourself, the cards you hold (click one to drop it), and Clear and Reset. Tab shows and
/// hides it; while it is open the game keeps running but your character takes no input.
/// </summary>
public partial class TrainingUI : Control
{
    /// <summary>The cards you should now hold.</summary>
    public event Action<List<int>> CardsSet;
    public event Action ResetPressed;

    /// <summary>The cards held right now (read each refresh, so a reset or respawn never loses track).</summary>
    public Func<List<int>> Held = () => new();

    private LineEdit _search;
    private GridContainer _grid;
    private HFlowContainer _held;
    private Label _heldTitle;
    private string _filter = "All";
    private int _heldHash = -1;

    private static readonly string[] Filters = { "All", "Gun", "Body", "Movement", "Chaos", "Block", "Curse", "Rare" };

    public override void _Ready()
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.LeftWide);
        OffsetLeft = 12;
        // Clear of the player chips along the top and your card strip along the bottom.
        OffsetTop = 52;
        OffsetBottom = -36;
        OffsetRight = 12 + 330;
        MouseFilter = MouseFilterEnum.Ignore;
        var panel = new PanelContainer();
        panel.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(panel);
        var body = UiKit.VBox(6);
        panel.AddChild(body);

        body.AddChild(UiKit.Label("TRAINING RANGE", UiKit.Small, Palette.Accent, HorizontalAlignment.Center, Fonts.Bold));
        body.AddChild(UiKit.Label("click a card to hold it · Tab hides this · Esc to leave", UiKit.Tiny, Palette.TextDim, HorizontalAlignment.Center));

        _search = new LineEdit { PlaceholderText = "search cards", ClearButtonEnabled = true };
        _search.TextChanged += _ => Fill();
        body.AddChild(_search);

        var filters = new HFlowContainer();
        filters.AddThemeConstantOverride("h_separation", 3);
        filters.AddThemeConstantOverride("v_separation", 3);
        foreach (var f in Filters)
        {
            var b = UiKit.Button(f, () => { _filter = f; Fill(); }, variation: "Small");
            b.AddThemeFontSizeOverride("font_size", UiKit.Tiny);
            filters.AddChild(b);
        }
        body.AddChild(filters);

        var scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        _grid = new GridContainer { Columns = 2, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _grid.AddThemeConstantOverride("h_separation", 3);
        _grid.AddThemeConstantOverride("v_separation", 3);
        scroll.AddChild(_grid);
        body.AddChild(scroll);

        _heldTitle = UiKit.Label("YOUR CARDS", UiKit.Tiny, Palette.TextDim, font: Fonts.Bold);
        body.AddChild(_heldTitle);
        _held = new HFlowContainer();
        _held.AddThemeConstantOverride("h_separation", 3);
        _held.AddThemeConstantOverride("v_separation", 3);
        body.AddChild(_held);

        var row = UiKit.HBox(6);
        var clear = UiKit.Button("Clear Cards", () => CardsSet?.Invoke(new List<int>()));
        var reset = UiKit.Button("Reset Range", () => ResetPressed?.Invoke());
        reset.TooltipText = "Everyone back to a spawn at full health; you keep your cards";
        clear.SizeFlagsHorizontal = reset.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        row.AddChild(clear);
        row.AddChild(reset);
        body.AddChild(row);

        Fill();
    }

    public override void _Process(double delta)
    {
        if (!Visible) return;
        var held = Held();
        int hash = held.Aggregate(17, (h, id) => h * 31 + id);
        if (hash != _heldHash) ShowHeld(held, hash);
    }

    /// <summary>The cards that pass the search and the category filter, in registry order.</summary>
    private IEnumerable<CardDef> Shown()
    {
        string q = _search.Text.Trim();
        return CardRegistry.All.Where(c =>
            (_filter == "All" || (_filter == "Rare" ? c.IsRare : c.Category.ToString() == _filter))
            && (q.Length == 0 || c.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || c.Description.Contains(q, StringComparison.OrdinalIgnoreCase)));
    }

    private void Fill()
    {
        if (_grid == null) return;
        UiKit.ClearChildren(_grid);
        foreach (var c in Shown())
        {
            var card = c;
            var b = CardButton(card, () => CardsSet?.Invoke(Held().Append(card.Id).ToList()));
            b.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            b.ClipText = true; // a long name stays in its column
            _grid.AddChild(b);
        }
    }

    private void ShowHeld(List<int> held, int hash)
    {
        _heldHash = hash;
        _heldTitle.Text = held.Count == 0 ? "YOUR CARDS: none yet" : $"YOUR CARDS ({held.Count}): click one to drop it";
        UiKit.ClearChildren(_held);
        for (int i = 0; i < held.Count; i++)
        {
            if (CardRegistry.Get(held[i]) is not { } card) continue;
            int at = i;
            _held.AddChild(CardButton(card, () =>
            {
                var now = Held();
                if (at < now.Count) now.RemoveAt(at);
                CardsSet?.Invoke(now);
            }));
        }
    }

    /// <summary>A small button for a card: its name in its category's color (gold for a rare), its text as the tooltip.</summary>
    private static Button CardButton(CardDef card, Action onPress)
    {
        var b = UiKit.Button(card.Name, onPress, variation: "Small");
        b.AddThemeFontSizeOverride("font_size", UiKit.Tiny);
        var col = card.IsRare ? UiKit.RareGold : UiKit.CategoryColor(card.Category);
        b.AddThemeColorOverride("font_color", col);
        b.AddThemeColorOverride("font_hover_color", col.Lightened(0.3f));
        b.TooltipText = $"{card.Name} ({(card.IsRare ? "rare " : "")}{card.Category})\n{card.Description}";
        return b;
    }
}
