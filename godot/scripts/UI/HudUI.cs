using System.Collections.Generic;
using System.Linq;
using Brawler.Core;
using Brawler.Fx;
using Brawler.Game;
using Brawler.Logic;
using Godot;

namespace Brawler.UI;

/// <summary>
/// Player chips, center banners and the loadout strip, drawn directly. Leaving is in the Esc options overlay.
/// The parts that only change with the score (chips, battle counter, loadout) go on a layer behind this control that
/// redraws only when they change; every frame this control draws the moving parts over it (chip heads, banner, ghost
/// hint, hover card). Each draws all its shapes into one triangle batch and then its text, grouped by font and size,
/// so the HUD costs a handful of draw calls instead of hundreds.
/// </summary>
public partial class HudUI : Control
{
    private readonly GameManager _game;
    private string _banner = "";
    private Color _bannerColor;
    private float _bannerT, _bannerMax, _t;
    private readonly List<(Rect2 Chip, MatchPlayer P)> _chips = new();
    private bool _compact;
    private Control _base;
    private int _baseKey;
    private readonly TriBatch _batch = new();
    /// <summary>Test hook (--hudshots): open this chip's hover card as if the mouse were over it.</summary>
    public int DebugHover = -1;

    /// <summary>Soft edge on round shapes (UI units), like Godot's antialiased circles at the window's usual stretch.</summary>
    private const float Feather = 0.5f;

    public HudUI(GameManager game) => _game = game;
    public HudUI() { }

    public override void _Ready()
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore;
        _base = new Control { ShowBehindParent = true, MouseFilter = MouseFilterEnum.Ignore };
        _base.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _base.Draw += DrawBase;
        AddChild(_base);
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
        var vs = GetViewportRect().Size;
        int key = BaseKey(vs);
        if (key != _baseKey)
        {
            _baseKey = key;
            Layout(vs);
            _base.QueueRedraw();
        }
        QueueRedraw();
    }

    /// <summary>A hash of everything the cached layer shows; when it changes, the chips are laid out and the layer redrawn.</summary>
    private int BaseKey(Vector2 vs)
    {
        var h = new System.HashCode();
        h.Add(vs);
        h.Add(_game.Settings.RoundsToWin);
        h.Add(_game.Settings.BattlesPerRound);
        h.Add(_game.Settings.TeamSize);
        h.Add(_game.BattleNumber);
        if (_game.BattleNumber > 0) h.Add(_game.MapNode.Theme.Name);
        h.Add(_game.Phase == Phase.CardPick);
        foreach (var p in _game.Slots)
        {
            if (p == null) continue;
            h.Add(p.Info.Name);
            h.Add(p.Color);
            h.Add(p.Left);
            h.Add(p.RoundWins);
            h.Add(p.BattleWins);
            h.Add(p.Cards.Count);
        }
        var me = _game.LocalMatchPlayer;
        h.Add(me);
        if (me != null) foreach (int c in me.Cards) h.Add(c);
        return h.ToHashCode();
    }

    /// <summary>
    /// Player chips across the top. They're laid out full size first; if that would run into the battle counter
    /// (five or more players), every chip goes compact: smaller head, names clipped, tighter pips.
    /// </summary>
    private void Layout(Vector2 vs)
    {
        _chips.Clear();
        var widths = new List<(MatchPlayer P, float W)>();
        bool compact = false;
        // Team modes: Red's chips, then a wider gap, then Blue's.
        var order = _game.Slots.Where(p => p != null).OrderBy(p => _game.TeamOf(p.Slot)).ThenBy(p => p.Slot).ToList();
        const float teamGap = 10;
        for (int pass = 0; pass < 2; pass++)
        {
            widths.Clear();
            float total = 10 + (_game.TeamMode ? teamGap : 0);
            foreach (var p in order)
            {
                float w = ChipWidth(p, compact);
                widths.Add((p, w));
                total += w + (compact ? 5 : 8);
            }
            if (compact || total <= vs.X - 130) break;
            compact = true;
        }
        _compact = compact;
        float x = 10;
        int lastTeam = -1;
        foreach (var (p, w) in widths)
        {
            int team = _game.TeamOf(p.Slot);
            if (lastTeam >= 0 && team != lastTeam) x += teamGap;
            lastTeam = team;
            _chips.Add((new Rect2(x, 8, w, compact ? 34 : 38), p));
            x += w + (compact ? 5 : 8);
        }
    }

    private void BeginBatch(CanvasItem ci)
    {
        _batch.Begin(ci);
        _batch.Feather = Feather;
        _batch.SoftLines = true;
    }

    private void Pill(Rect2 r, Color bg, Color? border = null)
    {
        var poly = Shapes.RoundRect(r, r.Size.Y / 2, 6);
        _batch.Polygon(poly, bg);
        if (border is { } b) _batch.Stroke(poly, 1f, b, closed: true);
    }

    // Grown by half the feather: Godot feathered antialiased circles outside their radius, which shows on small dots.
    private void Dot(Vector2 c, float r, Color col) => _batch.Circle(c, r + Feather * 0.5f, col, Mathf.Clamp(Mathf.CeilToInt(r * 5), 8, 28));

    /// <summary>The cached layer: the chips (all but their heads), the battle counter and your loadout.</summary>
    private void DrawBase()
    {
        var ci = _base;
        var vs = GetViewportRect().Size;
        var bold = Fonts.Bold;

        // Your loadout, bottom-left, centered in the floor band (MapGenerator.FloorDepth) so the strip never covers the
        // fight. Combos the loadout unlocked come first, in gold, so a long hand never pushes them off screen.
        var loadout = new List<(Rect2 R, string Name, Color Dot, bool Combo)>();
        var me = _game.LocalMatchPlayer;
        if (me != null && me.Cards.Count > 0 && _game.Phase != Phase.CardPick)
        {
            float bx = 10, band = vs.Y * MapGenerator.FloorDepth / MapLayout.Height;
            void Add(string name, Color dot, bool combo)
            {
                float w = bold.GetStringSize(name, HorizontalAlignment.Left, -1, 11).X + 24;
                loadout.Add((new Rect2(bx, vs.Y - band / 2 - 9, w, 18), name, dot, combo));
                bx += w + 5;
            }
            foreach (var combo in Combos.Active(me.Cards)) Add(combo.Name, Palette.Accent, true);
            foreach (var c in CardRegistry.Resolve(me.Cards)) Add(c.Name, UiKit.CategoryColor(c.Category), false);
        }

        string label = $"BATTLE {_game.BattleNumber}";
        float lw = bold.GetStringSize(label, HorizontalAlignment.Left, -1, 12).X + 22;
        var counter = new Rect2(vs.X - 10 - lw, 10, lw, 22);

        BeginBatch(ci);
        foreach (var (chip, p) in _chips) ChipShapes(chip, p);
        if (_game.BattleNumber > 0) Pill(counter, new Color(0.06f, 0.03f, 0.13f, 0.72f), new Color(1, 1, 1, 0.15f));
        foreach (var (r, _, dot, combo) in loadout)
        {
            if (combo)
            {
                Pill(r, new Color(0.16f, 0.09f, 0.02f, 0.8f), Palette.Accent with { A = 0.55f });
                _batch.Star(r.Position + new Vector2(9, 9), 5, 2.2f, 5, -Mathf.Pi / 2, Palette.Accent);
            }
            else
            {
                Pill(r, new Color(0.06f, 0.03f, 0.13f, 0.7f), new Color(1, 1, 1, 0.1f));
                Dot(r.Position + new Vector2(9, 9), 3.5f, dot);
            }
        }
        _batch.Flush();

        foreach (var (chip, p) in _chips) ChipName(ci, chip, p);
        foreach (var (chip, p) in _chips) ChipBadge(ci, chip, p);
        if (_game.BattleNumber > 0)
        {
            ci.DrawString(bold, counter.Position + new Vector2(0, 16), label, HorizontalAlignment.Center, lw, 12, Palette.TextDim);
            string theme = _game.MapNode.Theme.Name.ToUpperInvariant();
            ci.DrawString(Fonts.Regular, new Vector2(vs.X - 14 - 200, 46), theme, HorizontalAlignment.Right, 200, 10, Palette.TextDim with { A = 0.7f });
        }
        foreach (var (r, name, _, combo) in loadout)
            ci.DrawString(bold, r.Position + new Vector2(16, 13), name, HorizontalAlignment.Left, -1, 11, combo ? Palette.Accent : Palette.Text);
    }

    public override void _Draw()
    {
        var vs = GetViewportRect().Size;

        // The chip heads breathe and blink, so they're drawn every frame, over the cached chips. The chips fade out
        // under the kill cam's letterbox bars.
        _base.Modulate = Colors.White with { A = 1 - _camShow };
        if (_camShow < 0.5f)
            foreach (var (chip, p) in _chips)
                AvatarArt.Draw(this, chip.Position + (_compact ? new Vector2(17, 25) : new Vector2(19, 27)), new AvatarPose
                {
                    Body = ChipColor(p), Look = p.Info.Look, Facing = 1, Aim = Vector2.Right, Scale = _compact ? 1f : 1.15f, Time = _t,
                    Grounded = true,
                });

        DrawBanner(vs);
        DrawGhostHint(vs);
        DrawHoverCard(vs);
        DrawKillCam(vs);
    }

    private float _camShow, _camRealT; // kill cam bars: 0..1 slid in; real seconds since it began

    private void Box(Rect2 r, Color c) =>
        _batch.Polygon(new[] { r.Position, new Vector2(r.End.X, r.Position.Y), r.End, new Vector2(r.Position.X, r.End.Y) }, c);

    /// <summary>Kill cam: letterbox bars with who killed whom, the slow-motion speed and how to skip.</summary>
    private void DrawKillCam(Vector2 vs)
    {
        var cam = _game.KillCam;
        float real = (float)GetProcessDeltaTime() / Mathf.Max(0.05f, (float)Engine.TimeScale);
        _camShow = Mathf.MoveToward(_camShow, cam.Playing ? 1 : 0, real * 5);
        _camRealT = cam.Playing ? _camRealT + real : 0;
        if (_camShow <= 0) return;
        float e = _camShow * _camShow * (3 - 2 * _camShow);
        float barH = Mathf.Round(vs.Y * 0.09f);
        float top = -barH + barH * e, bottom = vs.Y - barH * e;
        var ink = new Color(0.03f, 0.015f, 0.06f, 0.97f);
        var red = new Color("#ff3355");
        BeginBatch(this);
        Box(new Rect2(0, top, vs.X, barH), ink);
        Box(new Rect2(0, bottom, vs.X, barH), ink);
        // Progress along the top bar's lower edge.
        Box(new Rect2(0, top + barH - 2, vs.X, 2), new Color(1, 1, 1, 0.1f));
        Box(new Rect2(0, top + barH - 2, vs.X * cam.Progress, 2), red with { A = 0.85f });
        bool blink = cam.Playing && _camRealT % 1f < 0.6f;
        if (blink) Dot(new Vector2(26, top + barH / 2), 5, Shapes.Hdr(red, 1.4f));
        _batch.Flush();

        var heavy = Fonts.Heavy;
        var bold = Fonts.Bold;
        float mid = top + barH / 2;
        DrawString(heavy, new Vector2(40, mid + 8), "KILL CAM", HorizontalAlignment.Left, -1, 22, Palette.Text);
        float speed = Logic.KillCamTiming.Speed(cam.FromKill);
        DrawString(bold, new Vector2(vs.X - 220, mid + 6), $"{speed:0.0}x", HorizontalAlignment.Right, 200, 16, speed < 0.9f ? red.Lightened(0.3f) : Palette.TextDim);

        // Who got whom, in their colors.
        float y = bottom + barH / 2 + 8;
        string victim = _game.NameOf(cam.Victim);
        var vCol = Palette.Player(_game.ColorOf(cam.Victim)).Lightened(0.2f);
        if (cam.Killer >= 0 && cam.Killer != cam.Victim)
        {
            string killer = _game.NameOf(cam.Killer);
            const string arrow = "  ELIMINATED  ";
            float wk = heavy.GetStringSize(killer, HorizontalAlignment.Left, -1, 22).X;
            float wa = bold.GetStringSize(arrow, HorizontalAlignment.Left, -1, 13).X;
            float wv = heavy.GetStringSize(victim, HorizontalAlignment.Left, -1, 22).X;
            float x = (vs.X - wk - wa - wv) / 2;
            DrawString(heavy, new Vector2(x, y), killer, HorizontalAlignment.Left, -1, 22, Palette.Player(_game.ColorOf(cam.Killer)).Lightened(0.2f));
            DrawString(bold, new Vector2(x + wk, y - 2), arrow, HorizontalAlignment.Left, -1, 13, Palette.TextDim);
            DrawString(heavy, new Vector2(x + wk + wa, y), victim, HorizontalAlignment.Left, -1, 22, vCol);
        }
        else
        {
            string text = $"{victim} IS OUT";
            float w = heavy.GetStringSize(text, HorizontalAlignment.Left, -1, 22).X;
            DrawString(heavy, new Vector2((vs.X - w) / 2, y), text, HorizontalAlignment.Left, -1, 22, vCol);
        }

        // Skipping needs every human; show how many are in.
        if (_game.LocalMatchPlayer != null && cam.Playing)
        {
            int voters = _game.KillCamVoters, votes = _game.KillCamSkipVotes;
            string skip = _game.SkipAsked
                ? (voters > 1 ? $"Skipping... {votes}/{voters}" : "Skipping...")
                : $"Skip: {Keybinds.Hint("shoot")} / {Keybinds.Hint("jump")}{(voters > 1 && votes > 0 ? $"  {votes}/{voters}" : "")}";
            DrawString(bold, new Vector2(vs.X - 320, bottom + barH / 2 + 5), skip, HorizontalAlignment.Right, 300, 12, Palette.TextDim);
        }
    }

    private const float CompactName = 50; // compact chips clip names to this width

    private static Color ChipColor(MatchPlayer p) => p.Left ? Palette.TextDim : p.Color;

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

    /// <summary>
    /// One player chip's shapes: the backing with a tinted head square, round-win pips with battle-win dots under them,
    /// and the card-count badge. The head itself is drawn every frame by <see cref="_Draw"/>.
    /// </summary>
    private void ChipShapes(Rect2 chip, MatchPlayer p)
    {
        var s = _game.Settings;
        bool compact = _compact;
        var col = ChipColor(p);
        float head = chip.Size.Y, radius = compact ? 11 : 12;
        var outline = Shapes.RoundRect(chip, radius, 6);
        _batch.Polygon(outline, new Color(0.06f, 0.03f, 0.13f, 0.72f));
        _batch.Polygon(Shapes.RoundRect(new Rect2(chip.Position, new Vector2(head, head)), radius, 6), col with { A = 0.3f });
        _batch.Stroke(outline, 1f, col with { A = 0.55f }, closed: true);
        if (_game.TeamOf(p.Slot) is >= 0 and var team)
            _batch.Polygon(Shapes.RoundRect(new Rect2(chip.Position.X + head + 2, chip.End.Y + 2, chip.Size.X - head - 6, 2.5f), 1.2f),
                p.Left ? Palette.TextDim : Palette.Team(team));

        float tx = chip.Position.X + (compact ? 38 : 44), y0 = chip.Position.Y;
        float step = PipStep(compact), pipR = compact ? Mathf.Min(2.7f, step * 0.45f) : 3.6f;
        float pipY = y0 + (compact ? 21 : 24);
        for (int i = 0; i < s.RoundsToWin; i++)
        {
            var c = new Vector2(tx + pipR + i * step, pipY);
            if (i < p.RoundWins)
            {
                Dot(c, pipR, Shapes.Hdr(col.Lightened(0.15f), 1.15f));
                Dot(c + new Vector2(-1, -1) * pipR / 3.6f, 1.1f * pipR / 3.6f, new Color(1, 1, 1, 0.7f));
            }
            else Dot(c, pipR * 0.94f, new Color(1, 1, 1, 0.14f));
        }
        float dotStep = compact ? 5 : 6;
        for (int i = 0; i < s.BattlesPerRound; i++)
            Dot(new Vector2(tx + 2 + i * dotStep, y0 + (compact ? 28 : 32)), compact ? 1.5f : 1.7f,
                i < p.BattleWins ? Colors.White : new Color(1, 1, 1, 0.18f));
        if (p.Cards.Count > 0) Pill(BadgeRect(chip), Palette.Accent);
    }

    private Rect2 BadgeRect(Rect2 chip) =>
        _compact ? new Rect2(chip.End.X - 17, chip.Position.Y - 4, 19, 13) : new Rect2(chip.End.X - 20, chip.Position.Y - 4, 22, 15);

    private void ChipName(CanvasItem ci, Rect2 chip, MatchPlayer p)
    {
        float tx = chip.Position.X + (_compact ? 38 : 44), y0 = chip.Position.Y;
        var nameCol = p.Left ? Palette.TextDim : Colors.White;
        if (_compact) ci.DrawString(Fonts.Bold, new Vector2(tx, y0 + 13), Fit(p.Info.Name, CompactName, 11), HorizontalAlignment.Left, -1, 11, nameCol);
        else ci.DrawString(Fonts.Bold, new Vector2(tx, y0 + 15), p.Info.Name, HorizontalAlignment.Left, -1, 13, nameCol);
    }

    private void ChipBadge(CanvasItem ci, Rect2 chip, MatchPlayer p)
    {
        if (p.Cards.Count == 0) return;
        var badge = BadgeRect(chip);
        ci.DrawString(Fonts.Heavy, badge.Position + new Vector2(0, _compact ? 10.5f : 12), p.Cards.Count.ToString(), HorizontalAlignment.Center,
            badge.Size.X, _compact ? 10 : 11, new Color(0.15f, 0.08f, 0.02f));
    }

    /// <summary>Hovering a player chip opens a card under it: the cards they've picked, then their resulting stats.</summary>
    private void DrawHoverCard(Vector2 vs)
    {
        var mouse = GetLocalMousePosition();
        var hit = DebugHover >= 0 ? Mathf.Min(DebugHover, _chips.Count - 1) : _chips.FindIndex(c => c.Chip.Grow(2).HasPoint(mouse));
        if (hit < 0) return;
        var (chip, p) = _chips[hit];
        var bold = Fonts.Bold;
        var col = ChipColor(p);

        var stacks = StatSummary.Stacks(p.Cards);
        var combos = Combos.Active(p.Cards);
        var stats = StatSummary.Lines(StatBlock.Compute(CardRegistry.Resolve(p.Cards)));
        const float pad = 10, row = 15, colW = 118, gap = 12;
        int cardCols = stacks.Count > 6 ? 2 : 1;
        int cardRows = Mathf.Max(1, (stacks.Count + cardCols - 1) / cardCols);
        int statRows = (stats.Count + 1) / 2;
        float w = pad * 2 + colW * 2 + gap;
        float comboH = combos.Count > 0 ? row : 0;
        float h = pad + 18 + cardRows * row + comboH + 10 + 16 + statRows * row + pad - 2;
        var r = new Rect2(Mathf.Clamp(chip.Position.X, 6, vs.X - w - 6), chip.End.Y + 6, w, h);
        float x0 = r.Position.X + pad, top = r.Position.Y + pad + 10;
        float cardsY = top + 8, ruleY = cardsY + cardRows * row + comboH + 6, statsY = ruleY + 14;
        Vector2 CardAt(int i) => new(x0 + i / cardRows * (colW + gap), cardsY + i % cardRows * row);
        Vector2 StatAt(int i) => new(x0 + i % 2 * (colW + gap), statsY + 2 + i / 2 * row + 11.5f);

        BeginBatch(this);
        var poly = Shapes.RoundRect(r, 10, 6);
        var back = new Color(0.05f, 0.03f, 0.11f, 0.93f);
        _batch.Polygon(poly, back);
        _batch.Stroke(poly, 1f, col with { A = 0.6f }, closed: true);
        // A notch pointing up at the chip.
        float nx = Mathf.Clamp(chip.Position.X + chip.Size.X / 2, r.Position.X + 14, r.End.X - 14);
        _batch.Polygon(new[] { new Vector2(nx - 6, r.Position.Y + 0.5f), new Vector2(nx, r.Position.Y - 5), new Vector2(nx + 6, r.Position.Y + 0.5f) }, back);
        _batch.Stroke(new[] { new Vector2(nx - 6, r.Position.Y), new Vector2(nx, r.Position.Y - 5), new Vector2(nx + 6, r.Position.Y) }, 1f, col with { A = 0.6f });
        for (int i = 0; i < stacks.Count; i++)
            Dot(CardAt(i) + new Vector2(3.5f, 7.5f), 3.2f, UiKit.CategoryColor(stacks[i].Card.Category));
        if (combos.Count > 0)
            _batch.Star(new Vector2(x0, cardsY + cardRows * row) + new Vector2(3.5f, 7.5f), 4.5f, 2f, 5, -Mathf.Pi / 2, Palette.Accent);
        _batch.SoftLines = false; // a hard 1px rule, as before
        _batch.Line(new Vector2(x0, ruleY), new Vector2(r.End.X - pad, ruleY), 1, new Color(1, 1, 1, 0.1f));
        _batch.Flush();

        DrawString(bold, new Vector2(x0, top), "CARDS", HorizontalAlignment.Left, -1, 10, Palette.TextDim);
        if (stacks.Count > 0)
            DrawString(bold, new Vector2(r.End.X - pad - 60, top), $"{p.Cards.Count}", HorizontalAlignment.Right, 60, 10, Palette.Accent);
        DrawString(bold, new Vector2(x0, statsY), "STATS", HorizontalAlignment.Left, -1, 10, Palette.TextDim);
        float MaxW(int n) => (cardCols == 1 ? w - pad * 2 : colW) - 12 - (n > 1 ? 22 : 0);
        for (int i = 0; i < stacks.Count; i++)
            DrawString(bold, CardAt(i) + new Vector2(11, 11.5f), stacks[i].Card.Name, HorizontalAlignment.Left, MaxW(stacks[i].Count), 11, Palette.Text);
        if (combos.Count > 0)
            DrawString(bold, new Vector2(x0, cardsY + cardRows * row) + new Vector2(11, 11.5f), "Combo: " + string.Join(", ", combos.Select(c => c.Name)),
                HorizontalAlignment.Left, w - pad * 2 - 12, 11, Palette.Accent);
        for (int i = 0; i < stats.Count; i++)
            DrawString(bold, StatAt(i), stats[i].Value, HorizontalAlignment.Right, colW, 11, stats[i].Changed ? Palette.Accent : Palette.Text);
        for (int i = 0; i < stacks.Count; i++)
        {
            var (card, n) = stacks[i];
            if (n <= 1) continue;
            float nameW = Mathf.Min(MaxW(n), bold.GetStringSize(card.Name, HorizontalAlignment.Left, -1, 11).X);
            DrawString(Fonts.Heavy, CardAt(i) + new Vector2(15 + nameW, 11.5f), $"x{n}", HorizontalAlignment.Left, -1, 10, Palette.Accent);
        }
        if (stacks.Count == 0)
            DrawString(Fonts.Regular, new Vector2(x0, cardsY + 11), "No cards yet", HorizontalAlignment.Left, -1, 11, Palette.TextDim with { A = 0.7f });
        for (int i = 0; i < stats.Count; i++)
            DrawString(Fonts.Regular, StatAt(i), stats[i].Label, HorizontalAlignment.Left, colW - 40, 11, Palette.TextDim);
    }

    /// <summary>While you're a ghost: when you respawn (respawn modes), or a reminder that you can still emote and ping until the next battle.</summary>
    private void DrawGhostHint(Vector2 vs)
    {
        if (_game.LocalMatchPlayer?.Avatar is not { GhostUp: true } || _game.Phase != Phase.Fighting) return;
        string emotes = string.Join("/", Enumerable.Range(0, Emotes.All.Length)
            .Select(i => Keybinds.Current.Row(Emotes.Action(i)).FirstOrDefault(b => !b.IsNone))
            .Where(b => !b.IsNone).Select(Keybinds.Label));
        float respawn = _game.RespawnIn(_game.LocalMatchPlayer.Slot);
        string text = respawn >= 0
            ? $"RESPAWNING IN {Mathf.Max(1, Mathf.CeilToInt(respawn))}"
            : $"YOU'RE A GHOST  ·  {(emotes.Length > 0 ? emotes + " emote  ·  " : "")}{Keybinds.Hint("ping")} ping";
        var font = Fonts.Bold;
        float w = font.GetStringSize(text, HorizontalAlignment.Left, -1, 12).X + 28;
        var r = new Rect2((vs.X - w) / 2, 58, w, 24);
        float pulse = 0.75f + 0.25f * Mathf.Sin(_t * 3);
        BeginBatch(this);
        Pill(r, new Color(0.06f, 0.03f, 0.13f, 0.7f), Palette.Cool with { A = 0.4f * pulse });
        _batch.Flush();
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
