using System.Collections.Generic;
using Brawler.Core;
using Brawler.Game;
using Brawler.Logic;
using Godot;

namespace Brawler.UI;

/// <summary>Everyone picks at once: you see your own offer, plus who is still choosing.</summary>
public partial class CardPickUI : Control
{
    private readonly GameManager _game;
    private Label _title;
    private HBoxContainer _cards;
    private Label _status;
    private readonly List<Button> _buttons = new();

    public CardPickUI(GameManager game) => _game = game;
    public CardPickUI() { }

    public override void _Ready()
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        var dim = new ColorRect { Color = new Color(0.03f, 0.03f, 0.06f, 0.85f) };
        AddChild(dim);
        dim.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);

        var v = UiKit.VBox(10);
        v.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        v.Alignment = BoxContainer.AlignmentMode.Center;
        AddChild(v);

        _title = UiKit.Label("CHOOSE A CARD", 20, Palette.Accent, HorizontalAlignment.Center);
        v.AddChild(_title);
        var center = new CenterContainer();
        _cards = UiKit.HBox(6);
        center.AddChild(_cards);
        v.AddChild(center);
        _status = UiKit.Label("", 10, Palette.TextDim, HorizontalAlignment.Center);
        v.AddChild(_status);

        _game.CardPickBegan += Build;
        _game.CardWasPicked += (_, _) => UpdateStatus();
    }

    private void Build()
    {
        UiKit.ClearChildren(_cards);
        _buttons.Clear();
        var me = _game.LocalMatchPlayer;
        bool canPick = me != null && _game.PickOptions.TryGetValue(me.Slot, out _);

        _title.Text = canPick ? "CHOOSE A CARD" : "OTHERS ARE CHOOSING";
        if (canPick)
            foreach (int id in _game.PickOptions[me.Slot])
                _cards.AddChild(MakeCard(CardRegistry.Get(id)));
        if (_game.AutopilotLocal)
            foreach (var b in _buttons) b.Disabled = true; // the bot picks for us
        UpdateStatus();
    }

    private Button MakeCard(CardDef c)
    {
        var btn = new Button { CustomMinimumSize = new Vector2(96, 128) };
        var col = UiKit.CategoryColor(c.Category);
        var inner = UiKit.VBox(4);
        inner.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        inner.OffsetLeft = 6;
        inner.OffsetRight = -6;
        inner.OffsetTop = 6;
        inner.OffsetBottom = -6;
        inner.MouseFilter = MouseFilterEnum.Ignore;

        var name = UiKit.Label(c.Name, 12, Colors.White, HorizontalAlignment.Center);
        name.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        var cat = UiKit.Label(c.Category.ToString().ToUpperInvariant(), 8, col, HorizontalAlignment.Center);
        var bar = new ColorRect { Color = col, CustomMinimumSize = new Vector2(0, 1) };
        var desc = UiKit.Label(c.Description, 8, Palette.Text, HorizontalAlignment.Center);
        desc.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        foreach (var n in new Control[] { name, cat, bar, desc })
        {
            n.MouseFilter = MouseFilterEnum.Ignore;
            inner.AddChild(n);
        }
        btn.AddChild(inner);

        int id = c.Id;
        btn.Pressed += () =>
        {
            foreach (var b in _buttons) b.Disabled = true;
            btn.Modulate = new Color(1.3f, 1.3f, 1.0f);
            _game.LocalPick(id);
        };
        _buttons.Add(btn);
        return btn;
    }

    private void UpdateStatus()
    {
        var parts = new List<string>();
        foreach (var (slot, _) in _game.PickOptions)
        {
            string n = _game.NameOf(slot);
            parts.Add(_game.Picked.Contains(slot) ? $"{n}: done" : $"{n}: choosing...");
        }
        _status.Text = string.Join("    ", parts);
    }
}
