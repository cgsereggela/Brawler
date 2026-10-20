using System;
using System.Collections.Generic;
using System.Linq;
using Brawler.Core;
using Brawler.Fx;
using Brawler.Game;
using Brawler.Logic;
using Godot;

namespace Brawler.UI;

/// <summary>Everyone picks at once: you see your own offer, plus who is still choosing.</summary>
public partial class CardPickUI : Control
{
    private readonly GameManager _game;
    private TitleText _title;
    private HBoxContainer _cards;
    private Label _status;
    private readonly List<CardView> _views = new();

    public CardPickUI(GameManager game) => _game = game;
    public CardPickUI() { }

    public override void _Ready()
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        var dim = new ColorRect { Color = new Color(0.04f, 0.02f, 0.1f, 0.78f) };
        AddChild(dim);
        dim.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);

        var v = UiKit.VBox(16);
        v.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        v.Alignment = BoxContainer.AlignmentMode.Center;
        AddChild(v);

        _title = new TitleText("CHOOSE A CARD", 34, 2f, Palette.Accent, Palette.Hot) { SizeFlagsHorizontal = SizeFlags.ShrinkCenter };
        v.AddChild(_title);
        _cards = UiKit.HBox(12);
        _cards.Alignment = BoxContainer.AlignmentMode.Center;
        v.AddChild(_cards);
        _status = UiKit.Label("", UiKit.Small, Palette.TextDim, HorizontalAlignment.Center);
        v.AddChild(_status);

        _game.CardPickBegan += Build;
        _game.CardWasPicked += (_, _) => UpdateStatus();
    }

    private void Build()
    {
        UiKit.ClearChildren(_cards);
        _views.Clear();
        var me = _game.LocalMatchPlayer;
        bool canPick = me != null && _game.PickOptions.TryGetValue(me.Slot, out _);

        _title.SetText(canPick ? "CHOOSE A CARD" : "OTHERS ARE CHOOSING");
        if (canPick)
        {
            var opts = _game.PickOptions[me.Slot];
            for (int i = 0; i < opts.Count; i++)
            {
                var card = CardRegistry.Get(opts[i]);
                int owned = me.Cards.Count(id => id == card.Id);
                float width = Mathf.Min(132, (760 - 12 * (opts.Count - 1)) / (float)opts.Count);
                var view = new CardView(card, owned, i * 0.07f, width) { Locked = _game.AutopilotLocal }; // the bot picks for us
                int id = card.Id;
                view.Picked += () =>
                {
                    foreach (var other in _views) other.Locked = true;
                    view.Chosen = true;
                    _game.LocalPick(id);
                };
                _views.Add(view);
                _cards.AddChild(view);
            }
        }
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        var parts = new List<string>();
        foreach (var (slot, _) in _game.PickOptions)
        {
            string n = _game.NameOf(slot);
            parts.Add(_game.Picked.Contains(slot) ? $"{n}  ✓" : $"{n}  …");
        }
        _status.Text = string.Join("      ", parts);
    }
}

/// <summary>One card: a dark glass face with a colored header, an icon, the name and its effects. Lifts on hover.</summary>
public partial class CardView : Control
{
    public event Action Picked;
    public bool Locked;
    public bool Chosen;

    private readonly CardDef _card;
    private readonly int _owned;
    private float _delay, _appear, _hover, _t;
    private bool _mouseIn;

    private readonly float W = 132;
    private const float H = 188;

    public CardView() { }

    public CardView(CardDef card, int owned, float delay, float width = 132)
    {
        _card = card;
        _owned = owned;
        W = width;
        _delay = delay;
        CustomMinimumSize = new Vector2(W, H);
        MouseFilter = MouseFilterEnum.Stop;
        MouseDefaultCursorShape = CursorShape.PointingHand;
        MouseEntered += () => _mouseIn = true;
        MouseExited += () => _mouseIn = false;

        var col = UiKit.CategoryColor(card.Category);
        // Name and effects flow top-down below the header, so a two-line name pushes the text down.
        var text = new VBoxContainer { Position = new Vector2(7, 82), Size = new Vector2(W - 14, H - 88), MouseFilter = MouseFilterEnum.Ignore };
        text.AddThemeConstantOverride("separation", 3);
        AddChild(text);
        var name = UiKit.Label(card.Name.ToUpperInvariant(), W < 120 ? 15 : 17, Colors.White, HorizontalAlignment.Center, Fonts.Heavy);
        name.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        name.CustomMinimumSize = new Vector2(W - 14, 0);
        name.MouseFilter = MouseFilterEnum.Ignore;
        name.AddThemeConstantOverride("line_spacing", -4);
        text.AddChild(name);
        var desc = UiKit.Label(card.Description, UiKit.Small, Palette.Text, HorizontalAlignment.Center);
        desc.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        desc.CustomMinimumSize = new Vector2(W - 14, 0);
        desc.MouseFilter = MouseFilterEnum.Ignore;
        desc.AddThemeConstantOverride("line_spacing", -1);
        text.AddChild(desc);
        var cat = UiKit.Label(card.Category.ToString().ToUpperInvariant(), UiKit.Tiny, col.Darkened(0.65f), HorizontalAlignment.Center, Fonts.Heavy);
        cat.Position = new Vector2(0, 6);
        cat.Size = new Vector2(W, 16);
        cat.MouseFilter = MouseFilterEnum.Ignore;
        AddChild(cat);
        Modulate = new Color(1, 1, 1, 0);
    }

    public override void _GuiInput(InputEvent e)
    {
        if (Locked || e is not InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left }) return;
        Picked?.Invoke();
        AcceptEvent();
    }

    public override void _Process(double delta)
    {
        float dt = (float)delta;
        _t += dt;
        if (_delay > 0) { _delay -= dt; return; }
        _appear = Mathf.Min(1, _appear + dt * 4f);
        bool lift = Chosen || (_mouseIn && !Locked);
        _hover = Mathf.MoveToward(_hover, lift ? 1 : 0, dt * 7f);
        float ease = 1f - Mathf.Pow(1f - _appear, 3);
        float dim = Locked && !Chosen && _appear >= 1 ? 0.45f : 1f;
        Modulate = new Color(dim, dim, dim, ease);
        PivotOffset = Size / 2;
        Scale = Vector2.One * (0.85f + 0.15f * ease + 0.06f * _hover);
        Position = Position with { Y = (1f - ease) * 40f - _hover * 8f };
        QueueRedraw();
    }

    public override void _Draw()
    {
        var col = UiKit.CategoryColor(_card.Category);
        var rect = new Rect2(Vector2.Zero, new Vector2(W, H));
        // Glow behind the card when hovered or chosen.
        if (_hover > 0) GlowTex.Draw(this, rect.GetCenter(), H * 0.75f, col with { A = 0.45f * _hover });
        DrawColoredPolygon(Shapes.RoundRect(new Rect2(rect.Position + new Vector2(0, 6), rect.Size), 14), new Color(0, 0, 0, 0.4f));
        var face = Shapes.RoundRect(rect, 14, 6);
        Shapes.Gradient(this, face, new Color(0.16f, 0.11f, 0.3f), new Color(0.07f, 0.05f, 0.14f));

        // Header band in the category color, clipped to the card's rounded top.
        var header = Shapes.RoundRect(new Rect2(0, 0, W, 76), 14, 6);
        for (int i = 0; i < header.Length; i++) if (header[i].Y > 30) header[i].Y = 76;
        Shapes.Gradient(this, header, col.Lightened(0.25f), col.Darkened(0.2f));
        DrawIcon(new Vector2(W / 2, 47), col);

        float pulse = 0.5f + 0.5f * Mathf.Sin(_t * 4);
        var border = _hover > 0 ? Shapes.Hdr(col.Lightened(0.3f), 1f + 0.4f * _hover) : new Color(1, 1, 1, 0.16f);
        Shapes.DrawClosed(this, face, border, 1.5f + _hover * 1.2f);
        if (Chosen) Shapes.DrawClosed(this, Shapes.RoundRect(rect.Grow(4), 17, 6), Shapes.Hdr(Colors.White, 1.2f) with { A = 0.6f + 0.4f * pulse }, 2f);

        // Player-made cards (custom card files) get a tag hanging off the bottom edge, clear of the category and badge.
        if (CardRegistry.IsCustom(_card.Id))
        {
            var tag = new Rect2(W / 2 - 29, H - 10, 58, 20);
            DrawColoredPolygon(Shapes.RoundRect(tag, 10), Palette.Cool);
            DrawString(Fonts.Heavy, tag.Position + new Vector2(0, 14.5f), "CUSTOM", HorizontalAlignment.Center, tag.Size.X, 11, new Color(0.02f, 0.1f, 0.14f));
        }

        if (_owned > 0)
        {
            var badge = new Rect2(W - 44, -8, 50, 20);
            DrawColoredPolygon(Shapes.RoundRect(badge, 10), Palette.Accent);
            DrawString(Fonts.Heavy, badge.Position + new Vector2(0, 15), $"x{_owned + 1}", HorizontalAlignment.Center, badge.Size.X, 13, new Color(0.15f, 0.08f, 0.02f));
        }
    }

    /// <summary>A simple glyph per category, drawn in white on the header.</summary>
    private void DrawIcon(Vector2 c, Color col)
    {
        var ink = new Color(1, 1, 1, 0.95f);
        var shadow = col.Darkened(0.55f) with { A = 0.6f };
        float spin = _t * 0.8f;
        switch (_card.Category)
        {
            case CardCategory.Gun:
                for (int i = 3; i >= 1; i--)
                    DrawCircle(c + new Vector2(-i * 9, 0), 6 - i * 1.2f, ink with { A = 0.25f * (4 - i) }, true, -1, true);
                DrawCircle(c + new Vector2(2, 2), 10, shadow, true, -1, true);
                DrawCircle(c, 10, ink, true, -1, true);
                DrawCircle(c + new Vector2(-3, -3), 3.5f, col.Lightened(0.6f), true, -1, true);
                break;
            case CardCategory.Body:
            {
                var heart = new List<Vector2>();
                for (int i = 0; i < 40; i++)
                {
                    float t = i / 40f * Mathf.Tau;
                    heart.Add(c + new Vector2(16 * Mathf.Pow(Mathf.Sin(t), 3), -(13 * Mathf.Cos(t) - 5 * Mathf.Cos(2 * t) - 2 * Mathf.Cos(3 * t) - Mathf.Cos(4 * t))) * 0.75f);
                }
                var pts = heart.ToArray();
                DrawColoredPolygon(Shapes.Offset(pts, new Vector2(2, 2)), shadow);
                DrawColoredPolygon(pts, ink);
                break;
            }
            case CardCategory.Movement:
            {
                // A bouncing double chevron with speed lines.
                float hop = Mathf.Abs(Mathf.Sin(_t * 3)) * -4;
                for (int k = 0; k < 2; k++)
                {
                    var o = c + new Vector2(-4 + k * 11, hop);
                    var chev = new[] { o + new Vector2(-6, -12), o + new Vector2(6, 0), o + new Vector2(-6, 12) };
                    DrawPolyline(Shapes.Offset(chev, new Vector2(2, 2)), shadow, 4.5f, true);
                    DrawPolyline(chev, ink with { A = k == 0 ? 0.6f : 0.95f }, 4.5f, true);
                }
                for (int i = 0; i < 3; i++)
                    DrawLine(c + new Vector2(-24, -7 + i * 7 + hop), c + new Vector2(-14 + (i % 2) * 3, -7 + i * 7 + hop), ink with { A = 0.5f }, 2f, true);
                break;
            }
            default:
                DrawColoredPolygon(Shapes.Star(c + new Vector2(2, 2), 17, 7, 5, spin - Mathf.Pi / 2), shadow);
                DrawColoredPolygon(Shapes.Star(c, 17, 7, 5, spin - Mathf.Pi / 2), ink);
                break;
        }
    }
}
