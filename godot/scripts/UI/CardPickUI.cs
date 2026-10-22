using System;
using System.Collections.Generic;
using System.Linq;
using Brawler.Core;
using Brawler.Fx;
using Brawler.Game;
using Brawler.Logic;
using Godot;

namespace Brawler.UI;

/// <summary>
/// Everyone picks at once: you see your own offer, plus who is still choosing. Couch co-op players at one machine
/// take turns on this screen, keyboard player first.
/// </summary>
public partial class CardPickUI : Control
{
    private readonly GameManager _game;
    private TitleText _title;
    private VBoxContainer _column, _rows;
    private HBoxContainer _cards; // the row cards are being added to
    private Label _status;
    private VBoxContainer _combo;
    private readonly List<CardView> _views = new();
    /// <summary>Who at this machine is choosing now, and who here already chose this pick.</summary>
    private MatchPlayer _picker;
    private readonly HashSet<int> _localDone = new();

    /// <summary>--readmeshots: under autopilot, show the cards undimmed and highlight the autopilot's pick as if clicked.</summary>
    public bool ShowAutopilotPick;

    public CardPickUI(GameManager game) => _game = game;
    public CardPickUI() { }

    public override void _Ready()
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        var dim = new ColorRect { Color = new Color(0.04f, 0.02f, 0.1f, 0.78f) };
        AddChild(dim);
        dim.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);

        var v = _column = UiKit.VBox(16);
        v.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        v.Alignment = BoxContainer.AlignmentMode.Center;
        AddChild(v);

        _title = new TitleText("CHOOSE A CARD", 34, 2f, Palette.Accent, Palette.Hot) { SizeFlagsHorizontal = SizeFlags.ShrinkCenter };
        v.AddChild(_title);
        _rows = UiKit.VBox(10);
        v.AddChild(_rows);
        _status = UiKit.Label("", UiKit.Small, Palette.TextDim, HorizontalAlignment.Center);
        v.AddChild(_status);
        // Always in the layout with room for the panel, so the cards don't jump when a combo shows up.
        _combo = UiKit.VBox(2);
        _combo.CustomMinimumSize = new Vector2(0, 52);
        v.AddChild(_combo);

        _game.CardPickBegan += () =>
        {
            _localDone.Clear();
            _found.Clear();
            _cursed.Clear();
            Build();
        };
        _game.CardWasPicked += (slot, cardId) =>
        {
            UpdateStatus();
            // A draft turn: everyone sees which card left the pool.
            if (_game.Drafting)
            {
                foreach (var v in _views) (v.Locked, v.Chosen) = (true, v.Card.Id == cardId);
                return;
            }
            if (!ShowAutopilotPick || slot != _picker?.Slot) return;
            foreach (var v in _views) (v.Locked, v.Chosen) = (true, v.Card.Id == cardId);
        };
        _game.CurseTaken += (slot, _) =>
        {
            _cursed.Add(slot);
            UpdateStatus();
        };
        _game.ComboFound += (slot, combo) =>
        {
            _found.Add(slot);
            if (_game.IsLocalHuman(slot)) ShowCombo(combo);
            UpdateStatus();
        };
    }

    private void Build()
    {
        UiKit.ClearChildren(_rows);
        _cards = null;
        _views.Clear();
        UiKit.ClearChildren(_combo);
        var me = _picker = _game.LocalHumans().FirstOrDefault(p => _game.PickOptions.ContainsKey(p.Slot) && !_game.Picked.Contains(p.Slot) && !_localDone.Contains(p.Slot));
        bool canPick = me != null;
        bool draft = _game.Drafting;
        // Draft: the one drafting this turn, whose cards everyone else watches.
        int drafter = draft ? _game.PickOptions.Keys.FirstOrDefault(-1) : -1;

        // With couch guests, say whose turn it is.
        string verb = draft ? "DRAFT" : "CHOOSE";
        _title.SetText(!canPick ? draft && drafter >= 0 ? $"{_game.NameOf(drafter).ToUpperInvariant()} IS DRAFTING" : "OTHERS ARE CHOOSING"
            : _game.LocalGuests.Count > 0 || draft && _game.LocalHumans().Count() > 1 ? $"{me.Info.Name.ToUpperInvariant()}, {verb} A CARD" : $"{verb} A CARD");
        if (canPick || drafter >= 0)
        {
            var opts = _game.PickOptions[canPick ? me.Slot : drafter];
            (int Rare, int Curse) deal = default;
            bool hasDeal = canPick && _game.PickDeals.TryGetValue(me.Slot, out deal) && CardRegistry.Get(deal.Rare) != null && CardRegistry.Get(deal.Curse) != null;
            int n = opts.Count + (hasDeal ? 1 : 0);
            // A draft pool too big for one line wraps onto two.
            int perRow = n > 6 ? (n + 1) / 2 : n;
            // Two rows only just fit under the player chips: close up the gaps.
            _column.AddThemeConstantOverride("separation", perRow < n ? 6 : 16);
            _combo.CustomMinimumSize = new Vector2(0, perRow < n ? 30 : 52);
            float width = Mathf.Min(132, (760 - 12 * (perRow - 1) - (hasDeal ? DealGap : 0)) / (float)perRow);
            var who = canPick ? me : _game.LocalHumans().FirstOrDefault() ?? _game.Slots[drafter];
            for (int i = 0; i < opts.Count; i++)
            {
                if (i % perRow == 0) NewRow();
                AddView(CardRegistry.Get(opts[i]), i * 0.07f, width, null, who, locked: !canPick);
            }
            if (hasDeal)
            {
                _cards.AddChild(new Control { CustomMinimumSize = new Vector2(DealGap - 12, 0), MouseFilter = MouseFilterEnum.Ignore });
                AddView(CardRegistry.Get(deal.Rare), opts.Count * 0.07f + 0.1f, width, CardRegistry.Get(deal.Curse), me, locked: false);
            }
        }
        UpdateStatus();
        // On a controller, start on the first card so the d-pad and A work right away.
        if (canPick && Pads.LastUsed && _views.Count > 0 && !_views[0].Locked) _views[0].GrabFocus();
    }

    private void NewRow()
    {
        _cards = UiKit.HBox(12);
        _cards.Alignment = BoxContainer.AlignmentMode.Center;
        _rows.AddChild(_cards);
    }

    /// <summary>Extra room before the cursed deal, so it reads as a separate choice.</summary>
    private const float DealGap = 28;

    /// <summary>One card to choose; with <paramref name="curse"/> it is the cursed deal and picking it takes both.</summary>
    /// <param name="who">Whose cards count for "owned" and combos (the picker, or for a watched draft turn, you).</param>
    /// <param name="locked">Only to look at: someone else's draft turn.</param>
    private void AddView(CardDef card, float delay, float width, CardDef curse, MatchPlayer who, bool locked)
    {
        var me = _picker;
        int owned = who.Cards.Count(id => id == card.Id);
        var view = new CardView(card, owned, delay, width, Combos.Unlocks(who.Cards, card.Id), curse) { Locked = locked || _game.AutopilotLocal && !ShowAutopilotPick }; // the bot picks for us
        int id = card.Id;
        view.Picked += () =>
        {
            foreach (var other in _views) other.Locked = true;
            view.Chosen = true;
            _localDone.Add(me.Slot);
            _game.LocalPick(id, deal: curse != null, me);
            // Next player at this machine, after a beat to see the choice land.
            if (_game.LocalHumans().Any(p => _game.PickOptions.ContainsKey(p.Slot) && !_game.Picked.Contains(p.Slot) && !_localDone.Contains(p.Slot)))
                GetTree().CreateTimer(0.7).Timeout += () => { if (Visible) Build(); };
        };
        _views.Add(view);
        _cards.AddChild(view);
    }

    private readonly HashSet<int> _found = new();
    private readonly HashSet<int> _cursed = new();

    /// <summary>Your pick unlocked a combo: its name and what it does, under the cards.</summary>
    private void ShowCombo(ComboDef combo)
    {
        var title = new TitleText($"COMBO FOUND!  {combo.Name.ToUpperInvariant()}", 22, 2f, Palette.Accent, Palette.Hot) { SizeFlagsHorizontal = SizeFlags.ShrinkCenter };
        _combo.AddChild(title);
        _combo.AddChild(UiKit.Label(combo.Description.Replace("\n", ", "), UiKit.Small, Palette.Text, HorizontalAlignment.Center));
    }

    private void UpdateStatus()
    {
        var parts = new List<string>();
        foreach (var (slot, _) in _game.PickOptions)
        {
            string n = _game.NameOf(slot);
            string combo = (_found.Contains(slot) ? "  COMBO!" : "") + (_cursed.Contains(slot) ? "  CURSED" : "");
            parts.Add(_game.Picked.Contains(slot) ? $"{n}  ✓{combo}" : $"{n}  …");
        }
        _status.Text = string.Join("      ", parts);
        if (_game.Drafting) _status.Text = $"Draft: {_views.Count} cards left in the shared pool      " + _status.Text;
    }
}

/// <summary>One card: a dark glass face with a colored header, an icon, the name and its effects. Lifts on hover.</summary>
public partial class CardView : Control
{
    public event Action Picked;
    public bool Locked;
    public bool Chosen;

    private readonly CardDef _card;
    public CardDef Card => _card;
    private readonly int _owned;
    private readonly List<ComboDef> _combos;
    private float _delay, _appear, _hover, _t;
    private bool _mouseIn, _focused;

    private readonly float W = 132;
    private const float H = 188;
    /// <summary>The curse plate hanging under a cursed deal's card.</summary>
    private const float PlateGap = 10, PlateH = 58;
    private readonly CardDef _curse;
    /// <summary>Card plus curse plate, if any.</summary>
    private float FullH => _curse == null ? H : H + PlateGap + PlateH;

    public CardView() { }

    /// <param name="curse">Makes this the cursed deal: the curse hangs under the card, and both are taken together.</param>
    public CardView(CardDef card, int owned, float delay, float width = 132, List<ComboDef> combos = null, CardDef curse = null)
    {
        _card = card;
        _curse = curse;
        _owned = owned;
        _combos = combos ?? new List<ComboDef>();
        if (_combos.Count > 0)
            TooltipText = string.Join("\n\n", _combos.Select(c => $"Completes the {c.Name} combo:\n{c.Description}"));
        W = width;
        _delay = delay;
        CustomMinimumSize = new Vector2(W, FullH);
        MouseFilter = MouseFilterEnum.Stop;
        MouseDefaultCursorShape = CursorShape.PointingHand;
        MouseEntered += () => _mouseIn = true;
        MouseExited += () => _mouseIn = false;
        FocusMode = FocusModeEnum.All; // the d-pad moves between cards, A picks
        FocusEntered += () => _focused = true;
        FocusExited += () => _focused = false;

        var col = UiKit.CategoryColor(card.Category);
        // Name and effects flow top-down below the header, so a two-line name pushes the text down.
        var text = new VBoxContainer { Position = new Vector2(7, 82), Size = new Vector2(W - 14, H - 88), MouseFilter = MouseFilterEnum.Ignore };
        text.AddThemeConstantOverride("separation", 3);
        AddChild(text);
        var name = UiKit.Label(card.Name.ToUpperInvariant(), W < 120 || card.Name.Split(' ').Max(w => w.Length) > 10 ? 15 : 17, Colors.White, HorizontalAlignment.Center, Fonts.Heavy);
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
        string catText = card.IsRare ? "RARE" : card.Category.ToString().ToUpperInvariant(); // the header color still says the category
        var cat = UiKit.Label(catText, UiKit.Tiny, col.Darkened(0.65f), HorizontalAlignment.Center, Fonts.Heavy);
        cat.Position = new Vector2(0, 6);
        cat.Size = new Vector2(W, 16);
        cat.MouseFilter = MouseFilterEnum.Ignore;
        AddChild(cat);
        if (curse != null)
        {
            var plate = new VBoxContainer { Position = new Vector2(6, H + PlateGap + 5), Size = new Vector2(W - 12, PlateH - 8), MouseFilter = MouseFilterEnum.Ignore };
            plate.AddThemeConstantOverride("separation", 0);
            AddChild(plate);
            var cname = UiKit.Label($"+ CURSE: {curse.Name.ToUpperInvariant()}", UiKit.Tiny, UiKit.CategoryColor(CardCategory.Curse).Lightened(0.45f), HorizontalAlignment.Center, Fonts.Heavy);
            cname.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            cname.CustomMinimumSize = new Vector2(W - 12, 0);
            cname.MouseFilter = MouseFilterEnum.Ignore;
            plate.AddChild(cname);
            var cdesc = UiKit.Label(curse.Description.Replace("\n", ", "), UiKit.Tiny, Palette.Text, HorizontalAlignment.Center);
            cdesc.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            cdesc.CustomMinimumSize = new Vector2(W - 12, 0);
            cdesc.MouseFilter = MouseFilterEnum.Ignore;
            cdesc.AddThemeConstantOverride("line_spacing", -2);
            plate.AddChild(cdesc);
            TooltipText = (TooltipText.Length > 0 ? TooltipText + "\n\n" : "") + $"Cursed deal: take {card.Name}, but {curse.Name} comes with it.";
        }
        Modulate = new Color(1, 1, 1, 0);
    }

    public override void _GuiInput(InputEvent e)
    {
        if (Locked || !(e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } || e is InputEventJoypadButton && e.IsActionPressed("ui_accept"))) return;
        Picked?.Invoke();
        AcceptEvent();
    }

    public override void _Process(double delta)
    {
        float dt = (float)delta;
        _t += dt;
        if (_delay > 0) { _delay -= dt; return; }
        _appear = Mathf.Min(1, _appear + dt * 4f);
        bool lift = Chosen || ((_mouseIn || _focused) && !Locked);
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
        var gold = UiKit.RareGold;
        var curseCol = UiKit.CategoryColor(CardCategory.Curse);
        var rect = new Rect2(Vector2.Zero, new Vector2(W, H));
        var full = new Rect2(Vector2.Zero, new Vector2(W, FullH));
        // Glow behind the card when hovered or chosen: gold for rares, blood red for a cursed deal.
        var glow = _curse != null ? curseCol : _card.IsRare ? gold : col;
        if (_hover > 0) GlowTex.Draw(this, full.GetCenter(), FullH * 0.75f, glow with { A = 0.45f * _hover });
        if (_curse != null) DrawCursePlate(curseCol);
        DrawColoredPolygon(Shapes.RoundRect(new Rect2(rect.Position + new Vector2(0, 6), rect.Size), 14), new Color(0, 0, 0, 0.4f));
        var face = Shapes.RoundRect(rect, 14, 6);
        if (_card.IsRare) Shapes.Gradient(this, face, new Color(0.24f, 0.16f, 0.24f), new Color(0.1f, 0.06f, 0.1f));
        else Shapes.Gradient(this, face, new Color(0.16f, 0.11f, 0.3f), new Color(0.07f, 0.05f, 0.14f));

        // Header band in the category color, clipped to the card's rounded top.
        var header = Shapes.RoundRect(new Rect2(0, 0, W, 76), 14, 6);
        for (int i = 0; i < header.Length; i++) if (header[i].Y > 30) header[i].Y = 76;
        Shapes.Gradient(this, header, col.Lightened(0.25f), col.Darkened(0.2f));
        DrawIcon(new Vector2(W / 2, 47), col);
        if (_card.IsRare) DrawSparkles(gold);

        float pulse = 0.5f + 0.5f * Mathf.Sin(_t * 4);
        var border = _card.IsRare
            ? Shapes.Hdr(gold.Lightened(0.15f * _hover), 1f + 0.25f * pulse + 0.4f * _hover)
            : _hover > 0 ? Shapes.Hdr(col.Lightened(0.3f), 1f + 0.4f * _hover) : new Color(1, 1, 1, 0.16f);
        Shapes.DrawClosed(this, face, border, (_card.IsRare ? 2.2f : 1.5f) + _hover * 1.2f);
        if (Chosen) Shapes.DrawClosed(this, Shapes.RoundRect(full.Grow(4), 17, 6), Shapes.Hdr(Colors.White, 1.2f) with { A = 0.6f + 0.4f * pulse }, 2f);

        // Player-made cards (custom card files) get a tag hanging off the bottom edge, clear of the category and badge.
        if (CardRegistry.IsCustom(_card.Id))
        {
            var tag = new Rect2(W / 2 - 29, H - 10, 58, 20);
            DrawColoredPolygon(Shapes.RoundRect(tag, 10), Palette.Cool);
            DrawString(Fonts.Heavy, tag.Position + new Vector2(0, 14.5f), "CUSTOM", HorizontalAlignment.Center, tag.Size.X, 11, new Color(0.02f, 0.1f, 0.14f));
        }

        // Taking this card completes a combo you're one card away from: a pulsing tag on the top-left corner.
        if (_combos.Count > 0)
        {
            var tag = new Rect2(-6, -12, 62, 18);
            GlowTex.Draw(this, tag.GetCenter(), 30, Palette.Accent with { A = 0.35f + 0.25f * pulse });
            DrawColoredPolygon(Shapes.RoundRect(tag, 10), Palette.Accent.Lerp(Palette.Hot, 0.35f * pulse));
            DrawString(Fonts.Heavy, tag.Position + new Vector2(0, 13.5f), "COMBO", HorizontalAlignment.Center, tag.Size.X, 11, new Color(0.15f, 0.08f, 0.02f));
        }

        if (_owned > 0)
        {
            var badge = new Rect2(W - 44, -8, 50, 20);
            DrawColoredPolygon(Shapes.RoundRect(badge, 10), Palette.Accent);
            DrawString(Fonts.Heavy, badge.Position + new Vector2(0, 15), $"x{_owned + 1}", HorizontalAlignment.Center, badge.Size.X, 13, new Color(0.15f, 0.08f, 0.02f));
        }
    }

    /// <summary>Four-point glints that twinkle in turn around a rare card's header.</summary>
    private void DrawSparkles(Color gold)
    {
        ReadOnlySpan<Vector2> spots = [new(0.16f, 18), new(0.84f, 26), new(0.26f, 62), new(0.76f, 66), new(0.5f, 14)];
        for (int i = 0; i < spots.Length; i++)
        {
            float a = Mathf.Max(0, Mathf.Sin(_t * 2.2f + i * 1.9f));
            if (a <= 0.05f) continue;
            var c = new Vector2(spots[i].X * W, spots[i].Y);
            float r = 2.5f + 3.5f * a;
            DrawColoredPolygon(Shapes.Star(c, r, r * 0.28f, 4, _t * 0.6f + i), Shapes.Hdr(gold.Lightened(0.5f), 1.3f) with { A = a });
        }
    }

    /// <summary>The curse that comes with a cursed deal: a dark red plate chained under the card.</summary>
    private void DrawCursePlate(Color curseCol)
    {
        var plate = new Rect2(3, H + PlateGap, W - 6, PlateH);
        // Two short chains from the card's bottom edge down to the plate.
        foreach (float x in new[] { W * 0.17f, W * 0.83f }) // clear of the CUSTOM tag in the middle
            for (int k = 0; k < 3; k++)
            {
                var c = new Vector2(x, H - 1 + k * 5);
                DrawArc(c, 2.6f, 0, Mathf.Tau, 10, curseCol.Darkened(0.25f), 1.6f);
            }
        DrawColoredPolygon(Shapes.RoundRect(new Rect2(plate.Position + new Vector2(0, 4), plate.Size), 10), new Color(0, 0, 0, 0.4f));
        var pts = Shapes.RoundRect(plate, 10, 5);
        Shapes.Gradient(this, pts, curseCol.Darkened(0.55f), new Color(0.12f, 0.02f, 0.05f));
        float flicker = 0.75f + 0.25f * Mathf.Sin(_t * 5.3f) * Mathf.Sin(_t * 2.1f);
        Shapes.DrawClosed(this, pts, Shapes.Hdr(curseCol, 0.9f + 0.3f * _hover) with { A = flicker }, 1.5f + _hover);
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
            case CardCategory.Block:
            {
                // A shield with a gentle sway and a glint across it.
                float sway = Mathf.Sin(_t * 2) * 0.08f;
                var shield = new List<Vector2>();
                for (int i = 0; i <= 12; i++)
                {
                    float x = -13 + 26 * i / 12f;
                    shield.Add(new Vector2(x, -13 + 2.5f * (1 - Mathf.Pow(x / 13, 2))));
                }
                for (int i = 1; i < 12; i++)
                {
                    float k = i / 12f;
                    shield.Add(new Vector2(13 * (1 - k) * Mathf.Cos(k * 0.9f), -2 + 17 * Mathf.Sin(k * Mathf.Pi / 2)));
                }
                shield.Add(new Vector2(0, 15));
                for (int i = 11; i >= 1; i--)
                {
                    float k = i / 12f;
                    shield.Add(new Vector2(-13 * (1 - k) * Mathf.Cos(k * 0.9f), -2 + 17 * Mathf.Sin(k * Mathf.Pi / 2)));
                }
                var pts = shield.Select(p => c + p.Rotated(sway)).ToArray();
                DrawColoredPolygon(Shapes.Offset(pts, new Vector2(2, 2)), shadow);
                DrawColoredPolygon(pts, ink);
                var boss = c + new Vector2(0, 0).Rotated(sway);
                DrawLine(c + new Vector2(0, -11).Rotated(sway), c + new Vector2(0, 12).Rotated(sway), col with { A = 0.7f }, 2.5f, true);
                DrawCircle(boss, 4, col, true, -1, true);
                DrawCircle(boss + new Vector2(-1.2f, -1.2f), 1.4f, col.Lightened(0.7f), true, -1, true);
                break;
            }
            case CardCategory.Curse:
            {
                // A skull that leans slowly side to side.
                float lean = Mathf.Sin(_t * 1.6f) * 0.12f;
                Vector2 P(float x, float y) => c + new Vector2(x, y).Rotated(lean);
                var skull = new List<Vector2>();
                for (int i = 0; i <= 20; i++)
                {
                    float a = Mathf.Pi * (0.85f + 1.3f * i / 20f); // the dome, left to right over the top
                    skull.Add(P(14 * Mathf.Cos(a), -3 + 13 * Mathf.Sin(a)));
                }
                skull.AddRange(new[] { P(8, 9), P(7, 14), P(-7, 14), P(-8, 9) });
                var pts = skull.ToArray();
                DrawColoredPolygon(Shapes.Offset(pts, new Vector2(2, 2)), shadow);
                DrawColoredPolygon(pts, ink);
                DrawCircle(P(-5.5f, -2), 3.6f, col.Darkened(0.5f), true, -1, true);
                DrawCircle(P(5.5f, -2), 3.6f, col.Darkened(0.5f), true, -1, true);
                DrawColoredPolygon(new[] { P(0, 3), P(2, 7), P(-2, 7) }, col.Darkened(0.5f));
                for (int i = -1; i <= 1; i++) DrawLine(P(i * 3.2f, 10), P(i * 3.2f, 14), col.Darkened(0.5f), 1.4f, true);
                break;
            }
            default:
                DrawColoredPolygon(Shapes.Star(c + new Vector2(2, 2), 17, 7, 5, spin - Mathf.Pi / 2), shadow);
                DrawColoredPolygon(Shapes.Star(c, 17, 7, 5, spin - Mathf.Pi / 2), ink);
                break;
        }
    }
}
