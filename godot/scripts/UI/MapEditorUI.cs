using System;
using System.Collections.Generic;
using System.Linq;
using Brawler.Core;
using Brawler.Fx;
using Brawler.Game;
using Brawler.Logic;
using Godot;

namespace Brawler.UI;

public enum EditTool { Select, Block, Gap, Hill, Spawn, Pad, Crate, Barrel, Glass, Magma, Poison, Saw, Mover, Drift }

/// <summary>
/// The level editor: the arena drawn by the game itself (themed ground, backdrop, extras) at a smaller scale, with
/// tools down the side to draw blocks, cut gaps in the floor, raise hills and place spawns, jump pads, crates,
/// barrels, glass, pools, sawblades, moving platforms and drifting platforms. Everything snaps to the 8 px tile grid; spawns, pads, crates
/// and pools drop onto whatever is below them. Arenas start from a generated one in any style and theme (or an empty
/// one), save to the player's map list and share as a map code (<see cref="MapCode"/>). Unsaved work is kept between
/// visits (<see cref="MapLibrary.Draft"/>).
/// </summary>
public partial class MapEditorUI : Control
{
    public event Action Closed;
    /// <summary>Play the arena now: an offline match against bots on it.</summary>
    public event Action<MapDesign> Playtest;

    public MapDesign Design { get; private set; }
    public MapLayout Layout => _layout;

    /// <summary>Where the arena sits on screen (base viewport pixels) and at what scale.</summary>
    public static readonly Vector2 Corner = new(202, 6);
    public const float Zoom = 0.79f;
    private const int Tile = MapGenerator.Tile, FloorY = MapGenerator.FloorY, Wall = MapGenerator.Wall;
    private const int W = MapLayout.Width, H = MapLayout.Height;

    private readonly GameManager _game;
    private MapLayout _layout;
    private EditTool _tool = EditTool.Select;
    private Control _canvas;
    private Label _hint, _status, _themeName, _styleName;
    private LineEdit _name, _code;
    private Button _save, _undoB, _redoB;
    private readonly Dictionary<EditTool, Button> _toolButtons = new();
    private Control _library;
    private VBoxContainer _libraryList;
    private MapStyle _style = MapStyle.Tiers;
    private readonly List<MapDesign> _undo = new(), _redo = new();
    private string _flash;
    private ulong _flashUntil;
    private bool _armedSave, _armedLeave;
    private bool _touched; // edited since it was opened or saved
    private string _armedDelete;

    // Pointer state, in world pixels.
    private enum Kind { None, Block, Pit, Hill, Spawn, Pad, Prop, Hazard, Saw, Mover, Drift }
    private (Kind K, int I) _sel = (Kind.None, -1), _hover = (Kind.None, -1);
    private Vector2 _mouse, _down;
    private bool _pressed, _dragged, _shift;
    private int _edges; // resizing a block: 1 left, 2 right, 4 top, 8 bottom
    private MapDesign _dragBase;

    public MapEditorUI(GameManager game) => _game = game;
    public MapEditorUI() { }

    private static readonly (EditTool Tool, string Label, string Key, string Hint)[] Tools =
    {
        (EditTool.Select, "Select", "V", "Click a piece to select it, drag to move it. Drag a block's edge to resize it. Wheel over a selected hill: height (Shift: top width). Delete removes."),
        (EditTool.Block, "Block", "B", "Drag to draw a solid block: a platform, wall or tower. Click for a short platform."),
        (EditTool.Gap, "Gap", "G", "Drag along the floor to open a pit; falling in is a knock-out. Select a gap and delete it to fill it in."),
        (EditTool.Hill, "Hill", "H", "Click the floor to raise a hill with ramps you can walk up. Hills can't overlap or cross a gap."),
        (EditTool.Spawn, "Spawn", "S", "Click to place a spawn point on the ground below. Up to 8; any missing are picked for you."),
        (EditTool.Pad, "Pad", "J", "Click to put a jump pad on the ground below. Leave open air above it."),
        (EditTool.Crate, "Crate", "C", "Click to drop a crate: it can be pushed and shot apart. Shift: place it exactly here."),
        (EditTool.Barrel, "Barrel", "R", "Click to drop an explosive barrel. Shift: place it exactly here."),
        (EditTool.Glass, "Glass", "L", "Drag sideways for a row of glass panes: they hold you up until they're shot out."),
        (EditTool.Magma, "Magma", "M", "Drag along the ground for a pool of magma. It burns fast."),
        (EditTool.Poison, "Poison", "P", "Drag along the ground for a pool of poison. It keeps hurting for a moment after you step out."),
        (EditTool.Saw, "Saw", "W", "Drag from one end of a sawblade's track to the other. Click for a track along the ground."),
        (EditTool.Mover, "Mover", "O", "Drag sideways for a moving platform's lane (it starts at the left end)."),
        (EditTool.Drift, "Drift", "F", "Drag to draw a drifting platform. Landing on it, walking into it, shooting or blasting it shoves it, and it eases back. Click for a short one."),
    };

    public override void _Ready()
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore;

        // ----- left: the tools -----
        var side = new PanelContainer { Position = new Vector2(4, 4), Size = new Vector2(Corner.X - 8, H - 8) };
        AddChild(side);
        var col = UiKit.VBox(5);
        side.AddChild(col);
        col.AddChild(new TitleText("MAP EDITOR", 18, 1f, Palette.Accent, Palette.Hot));
        var grid = new GridContainer { Columns = 2 };
        grid.AddThemeConstantOverride("h_separation", 4);
        grid.AddThemeConstantOverride("v_separation", 4);
        var group = new ButtonGroup();
        foreach (var (tool, label, key, _) in Tools)
        {
            var t = tool;
            var b = UiKit.Button(label, () => SetTool(t), 0, "Small");
            b.AddThemeFontSizeOverride("font_size", UiKit.Small);
            b.ToggleMode = true;
            b.ButtonGroup = group;
            b.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            b.TooltipText = $"{(tool == EditTool.Gap ? "Floor gap" : tool == EditTool.Pad ? "Jump pad" : label)} ({key})";
            b.FocusMode = FocusModeEnum.None;
            grid.AddChild(b);
            _toolButtons[tool] = b;
        }
        col.AddChild(grid);
        _hint = UiKit.Label("", UiKit.Tiny, Palette.TextDim);
        _hint.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _hint.CustomMinimumSize = new Vector2(0, 74);
        col.AddChild(_hint);

        col.AddChild(UiKit.Label("THEME", UiKit.Tiny, Palette.TextDim, font: Fonts.Bold));
        col.AddChild(Stepper(out _themeName, () => StepTheme(-1), () => StepTheme(1)));
        col.AddChild(UiKit.Label("START OVER FROM", UiKit.Tiny, Palette.TextDim, font: Fonts.Bold));
        col.AddChild(Stepper(out _styleName, () => StepStyle(-1), () => StepStyle(1)));
        var fresh = UiKit.HBox(4);
        var gen = UiKit.Button("Generate", Generate, 0, "Small");
        gen.TooltipText = "A new random arena in this style (press again for another)";
        var empty = UiKit.Button("Empty", () => Replace(MapDesign.Empty(Design.Theme, Design.Seed, floor: _style != MapStyle.Sky)), 0, "Small");
        empty.TooltipText = "Just the walls and a flat floor (in the Sky style, not even a floor)";
        foreach (var b in new[] { gen, empty })
        {
            b.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            b.FocusMode = FocusModeEnum.None;
            fresh.AddChild(b);
        }
        col.AddChild(fresh);
        col.AddChild(new Control { SizeFlagsVertical = SizeFlags.ExpandFill });
        var undoRow = UiKit.HBox(4);
        _undoB = UiKit.Button("Undo", Undo, 0, "Small");
        _undoB.TooltipText = "Ctrl+Z";
        _redoB = UiKit.Button("Redo", Redo, 0, "Small");
        _redoB.TooltipText = "Ctrl+Y";
        foreach (var b in new[] { _undoB, _redoB })
        {
            b.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            b.FocusMode = FocusModeEnum.None;
            undoRow.AddChild(b);
        }
        col.AddChild(undoRow);

        // ----- the arena: the game draws it underneath; this takes the mouse and draws the editing marks -----
        _canvas = new Control
        {
            Position = Corner, Size = new Vector2(W, H) * Zoom, MouseFilter = MouseFilterEnum.Stop,
            FocusMode = FocusModeEnum.Click, ClipContents = true, MouseDefaultCursorShape = CursorShape.Cross,
        };
        _canvas.GuiInput += CanvasInput;
        _canvas.Draw += DrawCanvas;
        _canvas.Draw += DrawPadCursor;
        _canvas.MouseExited += () => { _hover = (Kind.None, -1); _mouse = new Vector2(-100, -100); _canvas.QueueRedraw(); };
        AddChild(_canvas);

        // ----- bottom: name, saving, the code, playing -----
        float top = Corner.Y + H * Zoom + 4;
        var bottom = new PanelContainer { Position = new Vector2(Corner.X, top), Size = new Vector2(W * Zoom, H - top - 4) };
        AddChild(bottom);
        var rows = UiKit.VBox(4);
        bottom.AddChild(rows);
        var row1 = UiKit.HBox(6);
        row1.AddChild(UiKit.Label("NAME", UiKit.Tiny, Palette.TextDim, font: Fonts.Bold));
        _name = new LineEdit { PlaceholderText = "Name your arena", MaxLength = MapDesign.MaxName, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _name.TextChanged += t =>
        {
            Design.Name = MapCode.Trim(t);
            _armedSave = false;
            RefreshCode();
        };
        row1.AddChild(_name);
        _save = UiKit.Button("Save", Save, 0, "Small");
        _save.TooltipText = "Save to My maps (under this name)";
        row1.AddChild(_save);
        row1.AddChild(UiKit.Button("My maps", OpenLibrary, 0, "Small"));
        var play = UiKit.Primary("PLAYTEST", () => Playtest?.Invoke(Design.Copy()));
        play.TooltipText = "Play this arena against bots right now";
        row1.AddChild(play);
        row1.AddChild(UiKit.Button("Back", Close, 0, "Small"));
        rows.AddChild(row1);

        var row2 = UiKit.HBox(6);
        row2.AddChild(UiKit.Label("CODE", UiKit.Tiny, Palette.TextDim, font: Fonts.Bold));
        _code = new LineEdit { SizeFlagsHorizontal = SizeFlags.ExpandFill, SelectAllOnFocus = true, PlaceholderText = "Paste a map code and press Enter" };
        _code.AddThemeFontSizeOverride("font_size", UiKit.Tiny);
        _code.TextSubmitted += t => Import(t);
        row2.AddChild(_code);
        var copy = UiKit.Button("Copy", CopyCode, 0, "Small");
        copy.TooltipText = "Copy this arena's code, to send to friends";
        row2.AddChild(copy);
        var paste = UiKit.Button("Paste", () => Import(DisplayServer.ClipboardGet()), 0, "Small");
        paste.TooltipText = "Open the map code on your clipboard";
        row2.AddChild(paste);
        rows.AddChild(row2);
        _status = UiKit.Label("", UiKit.Tiny, Palette.TextDim);
        _status.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _status.ClipText = true;
        _status.CustomMinimumSize = new Vector2(0, 26);
        rows.AddChild(_status);

        BuildLibrary();
        VisibilityChanged += () =>
        {
            if (Visible) Open();
            else Leave();
        };
    }

    private static HBoxContainer Stepper(out Label name, Action prev, Action next)
    {
        var row = UiKit.HBox(2);
        var l = UiKit.Button("<", prev, 22, "Small");
        var r = UiKit.Button(">", next, 22, "Small");
        l.FocusMode = r.FocusMode = FocusModeEnum.None;
        name = UiKit.Label("", UiKit.Small, Palette.Text, HorizontalAlignment.Center);
        name.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        name.VerticalAlignment = VerticalAlignment.Center;
        row.AddChild(l);
        row.AddChild(name);
        row.AddChild(r);
        return row;
    }

    // ---------- opening and closing ----------

    private void Open()
    {
        _paintedAt = 0;
        if (Design == null)
        {
            Design = MapCode.Decode(MapLibrary.Draft) ?? MapDesign.Generate(MapStyle.Tiers, ThemeId.SunsetPeaks, (int)(GD.Randi() % 100000));
            _style = Design.Style;
        }
        _game.EditorView = (Corner, Zoom);
        SetTool(_tool);
        _name.Text = Design.Name;
        Rebuild();
    }

    private void Leave()
    {
        _library.Visible = false;
        _game.EditorView = null;
        _game.MapNode.Build(null);
        _game.Arena.Build(null);
    }

    /// <summary>Back: warns once if the arena was changed and isn't in My maps (the draft is kept either way).</summary>
    private void Close()
    {
        if (Unsaved && !_armedLeave)
        {
            _armedLeave = true;
            Flash("This arena isn't saved in My maps. Save it, or press Back again to leave (it stays here as your draft).");
            return;
        }
        _armedLeave = false;
        Closed?.Invoke();
    }

    /// <summary>Changed since it was opened or saved, and no saved map matches it.</summary>
    public bool Unsaved => _touched && !MapLibrary.All().Exists(e => MapCode.Encode(e.Design) == MapCode.Encode(Design));

    /// <summary>Opens a design (from the lobby's arena list or a test), replacing what is being edited.</summary>
    public void Load(MapDesign d)
    {
        Design = d.Copy();
        _touched = false;
        _style = Design.Style;
        _sel = _hover = (Kind.None, -1);
        if (_name != null) _name.Text = Design.Name;
        if (IsInsideTree() && Visible) Rebuild();
    }

    // ---------- tools and history ----------

    public void SetTool(EditTool tool)
    {
        _tool = tool;
        _toolButtons[tool].ButtonPressed = true;
        _hint.Text = Tools.First(t => t.Tool == tool).Hint + (PadCursor ? " " + PadHint : tool == EditTool.Select ? "" : " Right-click any piece to delete it.");
        if (tool != EditTool.Select) _sel = (Kind.None, -1);
        _canvas.QueueRedraw();
    }

    private void Remember()
    {
        _touched = true;
        _armedLeave = false;
        _undo.Add(Design.Copy());
        if (_undo.Count > 100) _undo.RemoveAt(0);
        _redo.Clear();
    }

    private void Undo()
    {
        if (_undo.Count == 0) return;
        _redo.Add(Design);
        Design = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        AfterHistory();
    }

    private void Redo()
    {
        if (_redo.Count == 0) return;
        _undo.Add(Design);
        Design = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        AfterHistory();
    }

    private void AfterHistory()
    {
        _touched = true;
        _armedLeave = false;
        _sel = _hover = (Kind.None, -1);
        _name.Text = Design.Name;
        _style = Design.Style;
        Rebuild();
    }

    /// <summary>Starts over from another design (undoable), keeping the name.</summary>
    private void Replace(MapDesign d)
    {
        Remember();
        d.Name = Design.Name;
        Design = d;
        _sel = _hover = (Kind.None, -1);
        Rebuild();
    }

    private void Generate() => Replace(MapDesign.Generate(_style, Design.Theme, (int)(GD.Randi() % 1000000) + 1));

    private void StepTheme(int dir)
    {
        Remember();
        Design.SetTheme((ThemeId)(((int)Design.Theme + dir + ThemeRules.Count) % ThemeRules.Count));
        Rebuild();
    }

    private void StepStyle(int dir)
    {
        int n = Enum.GetValues<MapStyle>().Length;
        _style = (MapStyle)(((int)_style + dir + n) % n);
        RefreshLabels();
    }

    /// <summary>Rebuilds the arena from the design and redraws it, and keeps the draft.</summary>
    private void Rebuild()
    {
        _layout = Design.Build(4);
        _paintDirty = true;
        Paint();
        MapLibrary.Draft = MapCode.Encode(Design);
        RefreshLabels();
        _canvas.QueueRedraw();
    }

    private bool _paintDirty;
    private ulong _paintedAt;

    /// <summary>
    /// Hands the arena to the game to draw: at most four times a second, since each time repaints the ground on a
    /// worker thread (a burst of edits would queue up paintings and leave the ground plain for a while).
    /// </summary>
    private void Paint()
    {
        if (!_paintDirty || Time.GetTicksMsec() - _paintedAt < 250) return;
        _paintDirty = false;
        _paintedAt = Time.GetTicksMsec();
        _game.MapNode.Build(_layout);
        _game.Arena.Build(_layout);
    }

    private void RefreshLabels()
    {
        _themeName.Text = ThemeRules.Name(Design.Theme);
        _styleName.Text = _style.ToString();
        _undoB.Disabled = _undo.Count == 0;
        _redoB.Disabled = _redo.Count == 0;
        RefreshCode();
    }

    private void RefreshCode()
    {
        if (!_code.HasFocus()) _code.Text = MapCode.Encode(Design);
        RefreshStatus();
    }

    private void RefreshStatus()
    {
        if (_flash != null && Time.GetTicksMsec() < _flashUntil)
        {
            _status.Text = _flash;
            _status.AddThemeColorOverride("font_color", Palette.Accent);
            return;
        }
        var problems = Design.Problems();
        _status.Text = problems.Count > 0 ? string.Join("  ", problems) : $"Ready to play. {_code.Text.Length}-character code.";
        _status.AddThemeColorOverride("font_color", problems.Count > 0 ? Palette.Hot.Lightened(0.2f) : Palette.TextDim);
    }

    private void Flash(string text)
    {
        _flash = text;
        _flashUntil = Time.GetTicksMsec() + 3000;
        RefreshStatus();
        GetTree().CreateTimer(3.05).Timeout += () => { if (IsInstanceValid(this)) RefreshStatus(); };
    }

    // ---------- saving and sharing ----------

    private void Save()
    {
        if (Design.Name == "")
        {
            Flash("Give your arena a name first.");
            _name.GrabFocus();
            return;
        }
        if (MapLibrary.Exists(Design.Name) && !_armedSave && MapLibrary.All().FirstOrDefault(e => e.Name == Design.Name).Design is { } old && MapCode.Encode(old) != MapCode.Encode(Design))
        {
            _armedSave = true;
            Flash($"You already have a map called {Design.Name}. Press Save again to replace it.");
            return;
        }
        _armedSave = false;
        bool ok = MapLibrary.Save(Design);
        if (ok) _touched = false;
        Flash(ok ? $"Saved {Design.Name} to My maps." : "Couldn't save the map.");
    }

    private void CopyCode()
    {
        DisplayServer.ClipboardSet(MapCode.Encode(Design));
        Flash("Code copied. Paste it to friends: they open it with Paste in their map editor, or add it to a lobby's arenas.");
    }

    /// <summary>Opens a pasted map code; true if it was one.</summary>
    public bool Import(string text)
    {
        var d = MapCode.Decode(text, out var why);
        if (d == null)
        {
            Flash(MapCode.Problem(why));
            return false;
        }
        Remember();
        Load(d);
        _touched = true; // a pasted arena isn't in My maps until saved
        _code.ReleaseFocus();
        Rebuild();
        Flash(d.Name != "" ? $"Opened {d.Name}." : "Opened the map.");
        return true;
    }

    private void BuildLibrary()
    {
        _library = new Control { Visible = false };
        AddChild(_library);
        var dim = new ColorRect { Color = new Color(0.02f, 0.01f, 0.06f, 0.85f), MouseFilter = MouseFilterEnum.Stop };
        dim.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _library.AddChild(dim);
        var (_, body) = UiKit.CenteredPanel(_library, 420);
        body.AddChild(new TitleText("MY MAPS", 22, 1.2f, Palette.Accent, Palette.Hot));
        var scroll = new ScrollContainer { CustomMinimumSize = new Vector2(0, 300), HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        body.AddChild(scroll);
        _libraryList = UiKit.VBox(4);
        _libraryList.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        scroll.AddChild(_libraryList);
        body.AddChild(UiKit.Label($"Saved in {ProjectSettings.GlobalizePath(MapLibrary.Dir)}", UiKit.Tiny, Palette.TextDim));
        body.AddChild(UiKit.Primary("DONE", () => _library.Visible = false));
    }

    private void OpenLibrary()
    {
        _armedDelete = null;
        FillLibrary();
        _library.Visible = true;
    }

    private void FillLibrary()
    {
        UiKit.ClearChildren(_libraryList);
        var all = MapLibrary.All();
        if (all.Count == 0)
            _libraryList.AddChild(UiKit.Label("No saved maps yet. Name your arena and press Save.", UiKit.Small, Palette.TextDim));
        foreach (var e in all)
        {
            var row = UiKit.HBox(6);
            row.AddChild(MapThumb.For(e.Design, e.Code));
            var name = UiKit.Label(e.Name, UiKit.Small, Palette.Text);
            name.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            row.AddChild(name);
            row.AddChild(UiKit.Label(ThemeRules.Name(e.Design.Theme), UiKit.Tiny, Palette.TextDim));
            var entry = e;
            row.AddChild(UiKit.Button("Open", () =>
            {
                Remember();
                Load(entry.Design);
                Rebuild();
                _library.Visible = false;
                Flash($"Opened {entry.Name}.");
            }, 0, "Small"));
            var del = UiKit.Button(_armedDelete == e.Name ? "Sure?" : "Delete", () =>
            {
                if (_armedDelete != entry.Name)
                {
                    _armedDelete = entry.Name;
                    FillLibrary();
                    return;
                }
                MapLibrary.Delete(entry.Name);
                _armedDelete = null;
                FillLibrary();
            }, 0, "Small");
            row.AddChild(del);
            _libraryList.AddChild(row);
        }
    }

    // ---------- pointer ----------

    private static Vector2 ToWorld(Vector2 local) => local / Zoom;
    private static int Snap(float v) => (int)MathF.Round(v / Tile) * Tile;
    private static int Snap4(float v) => (int)MathF.Round(v / 4) * 4;

    private void CanvasInput(InputEvent e)
    {
        switch (e)
        {
            case InputEventMouseMotion m:
                _mouse = ToWorld(m.Position);
                _shift = m.ShiftPressed;
                if (_pressed && (_mouse - _down).Length() > 3) _dragged = true;
                if (!_pressed) _hover = Hit(_mouse);
                if (_pressed && _tool == EditTool.Select && _sel.K != Kind.None && _dragged) DragSelected();
                _canvas.QueueRedraw();
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.Left } b:
                _mouse = ToWorld(b.Position);
                _shift = b.ShiftPressed;
                if (b.Pressed) PointerDown();
                else if (_pressed) PointerUp();
                _canvas.QueueRedraw();
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.Right, Pressed: true } b:
                _mouse = ToWorld(b.Position);
                if (!_pressed && Hit(_mouse) is var hit && hit.K != Kind.None)
                {
                    Remember();
                    Delete(hit);
                }
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.WheelUp or MouseButton.WheelDown, Pressed: true } b when _sel.K == Kind.Hill:
                Remember();
                ReshapeHill(_sel.I, b.ButtonIndex == MouseButton.WheelUp ? Tile : -Tile, b.ShiftPressed);
                _canvas.AcceptEvent();
                break;
            case InputEventKey { Pressed: true } k:
                if (HandleKey(k)) _canvas.AcceptEvent();
                break;
        }
    }

    private bool HandleKey(InputEventKey k)
    {
        bool ctrl = k.CtrlPressed || k.MetaPressed;
        switch (k.Keycode)
        {
            case Godot.Key.Z when ctrl && k.ShiftPressed: Redo(); return true;
            case Godot.Key.Z when ctrl: Undo(); return true;
            case Godot.Key.Y when ctrl: Redo(); return true;
            case Godot.Key.D when ctrl: Duplicate(); return true;
            case Godot.Key.Delete or Godot.Key.Backspace when _sel.K != Kind.None:
                Remember();
                Delete(_sel);
                return true;
            case Godot.Key.Escape when _sel.K != Kind.None || _pressed:
                _pressed = false;
                if (_dragBase != null) Design = _dragBase;
                _dragBase = null;
                _sel = (Kind.None, -1);
                _canvas.QueueRedraw();
                return true;
        }
        if (ctrl || k.AltPressed) return false;
        foreach (var (tool, _, key, _) in Tools)
            if (k.Keycode.ToString() == key)
            {
                SetTool(tool);
                return true;
            }
        return false;
    }

    private void PointerDown()
    {
        _pressed = true;
        _dragged = false;
        _down = _mouse;
        _dragBase = null;
        if (_tool != EditTool.Select) return;
        _sel = Hit(_mouse);
        _edges = _sel.K == Kind.Block ? Edges(Design.Blocks[_sel.I], _mouse) : 0;
        if (_sel.K != Kind.None) _dragBase = Design.Copy();
    }

    private void PointerUp()
    {
        _pressed = false;
        if (_tool == EditTool.Select)
        {
            if (_dragBase != null && _dragged)
            {
                // The design already holds the move; remember where it came from.
                var moved = Design;
                Design = _dragBase;
                if (ValidMove(moved))
                {
                    Remember();
                    Design = moved;
                }
                else Flash("Hills can't overlap each other.");
                Rebuild();
            }
            _dragBase = null;
            return;
        }
        Create();
    }

    /// <summary>
    /// Which block edges are under the pointer, for resizing: within 5 px, or a quarter of the block across for a thin
    /// one, so the middle of a platform still moves it.
    /// </summary>
    private static int Edges(RectI r, Vector2 p)
    {
        float nearX = Math.Min(5, r.W / 4f), nearY = Math.Min(5, r.H / 4f);
        int e = 0;
        if (Math.Abs(p.X - r.X) < nearX) e |= 1;
        else if (Math.Abs(p.X - r.Right) < nearX) e |= 2;
        if (Math.Abs(p.Y - r.Y) < nearY) e |= 4;
        else if (Math.Abs(p.Y - r.Bottom) < nearY) e |= 8;
        return e;
    }

    // ---------- hit testing ----------

    private (Kind K, int I) Hit(Vector2 p)
    {
        var d = Design;
        for (int i = d.Spawns.Count - 1; i >= 0; i--)
            if ((new Vector2(d.Spawns[i].X, d.Spawns[i].Y - 9) - p).Length() < 9) return (Kind.Spawn, i);
        for (int i = d.Props.Count - 1; i >= 0; i--)
            if (Grow(d.Props[i].Rect, 2).Contains((int)p.X, (int)p.Y)) return (Kind.Prop, i);
        for (int i = d.Pads.Count - 1; i >= 0; i--)
            if (new RectI(d.Pads[i].X1 - 2, d.Pads[i].Y - 6, d.Pads[i].X2 - d.Pads[i].X1 + 4, 9).Contains((int)p.X, (int)p.Y)) return (Kind.Pad, i);
        for (int i = d.Saws.Count - 1; i >= 0; i--)
        {
            var s = d.Saws[i];
            if (Geometry2D.GetClosestPointToSegment(p, new Vector2(s.X1, s.Y1), new Vector2(s.X2, s.Y2)).DistanceTo(p) < s.Radius + 3) return (Kind.Saw, i);
        }
        for (int i = d.Hazards.Count - 1; i >= 0; i--)
            if (Grow(d.Hazards[i].Rect, 2).Contains((int)p.X, (int)p.Y)) return (Kind.Hazard, i);
        for (int i = d.Movers.Count - 1; i >= 0; i--)
            if (Grow(d.Movers[i].Lane, 2).Contains((int)p.X, (int)p.Y)) return (Kind.Mover, i);
        for (int i = d.Drifts.Count - 1; i >= 0; i--)
            if (Grow(d.Drifts[i].Rect, 2).Contains((int)p.X, (int)p.Y)) return (Kind.Drift, i);
        // The smallest block under the pointer, so a platform inside a big wall can still be picked.
        int best = -1;
        for (int i = 0; i < d.Blocks.Count; i++)
            if (Grow(d.Blocks[i], 3).Contains((int)p.X, (int)p.Y) && (best < 0 || Area(d.Blocks[i]) <= Area(d.Blocks[best]))) best = i;
        if (best >= 0) return (Kind.Block, best);
        for (int i = 0; i < d.Hills.Count; i++)
            if (Geometry2D.IsPointInPolygon(p, HillPolygon(d.Hills[i]))) return (Kind.Hill, i);
        for (int i = 0; i < d.Pits.Count; i++)
            if (p.X >= d.Pits[i].x1 && p.X < d.Pits[i].x2 && p.Y > FloorY - 12) return (Kind.Pit, i);
        return (Kind.None, -1);
    }

    private static RectI Grow(RectI r, int by) => r.Grow(by, by);
    private static int Area(RectI r) => r.W * r.H;

    private static Vector2[] HillPolygon(List<(int x, int h)> hill)
    {
        var pts = hill.Select(c => new Vector2(c.x, FloorY - c.h)).ToList();
        if (hill[^1].h > 0) pts.Add(new Vector2(hill[^1].x, FloorY));
        if (hill[0].h > 0) pts.Insert(0, new Vector2(hill[0].x, FloorY));
        return pts.ToArray();
    }

    // ---------- moving ----------

    /// <summary>The design with the selection moved (or a block resized) to follow the pointer.</summary>
    private void DragSelected()
    {
        var d = _dragBase.Copy();
        int dx = Snap(_mouse.X - _down.X), dy = Snap(_mouse.Y - _down.Y);
        int i = _sel.I;
        switch (_sel.K)
        {
            case Kind.Block when _edges != 0:
            {
                var r = d.Blocks[i];
                int x1 = r.X, y1 = r.Y, x2 = r.Right, y2 = r.Bottom;
                if ((_edges & 1) != 0) x1 = Math.Min(x2 - Tile, Snap(_mouse.X));
                if ((_edges & 2) != 0) x2 = Math.Max(x1 + Tile, Snap(_mouse.X));
                if ((_edges & 4) != 0) y1 = Math.Min(y2 - Tile, Snap(_mouse.Y));
                if ((_edges & 8) != 0) y2 = Math.Max(y1 + Tile, Snap(_mouse.Y));
                d.Blocks[i] = Inside(new RectI(x1, y1, x2 - x1, y2 - y1));
                break;
            }
            case Kind.Block:
            {
                var from = d.Blocks[i];
                var to = Inside(new RectI(from.X + dx, from.Y + dy, from.W, from.H));
                d.Blocks[i] = to;
                Carry(_dragBase, d, from, to.X - from.X, to.Y - from.Y);
                break;
            }
            case Kind.Pit:
            {
                var (x1, x2) = d.Pits[i];
                int w = x2 - x1, nx = Math.Clamp(x1 + dx, Wall, W - Wall - w);
                d.Pits[i] = (nx, nx + w);
                break;
            }
            case Kind.Hill:
            {
                var hill = d.Hills[i];
                int shift = Math.Clamp(dx, Wall - hill[0].x, W - Wall - hill[^1].x);
                d.Hills[i] = hill.Select(c => (c.x + shift, c.h)).ToList();
                break;
            }
            case Kind.Spawn:
                d.Spawns[i] = DropSpawn(_dragBase.Spawns[i].X + (_mouse.X - _down.X), _dragBase.Spawns[i].Y + (_mouse.Y - _down.Y));
                break;
            case Kind.Pad:
            {
                var p = _dragBase.Pads[i];
                d.Pads[i] = DropPad(p.CenterX + (_mouse.X - _down.X), p.Y - 10 + (_mouse.Y - _down.Y));
                break;
            }
            case Kind.Prop:
            {
                var group = GlassRow(_dragBase, i);
                foreach (int k in group)
                {
                    var p = _dragBase.Props[k];
                    d.Props[k] = p.Kind == PropKind.Glass || _shift
                        ? p with { X = p.X + dx, Y = p.Y + dy }
                        : DropProp(p.Kind, p.X + p.W / 2f + (_mouse.X - _down.X), p.Y + (_mouse.Y - _down.Y));
                }
                break;
            }
            case Kind.Hazard:
            {
                var h = _dragBase.Hazards[i];
                int w = h.X2 - h.X1, x = Snap4(h.X1 + (_mouse.X - _down.X));
                var s = MapDesign.SurfaceBelow(_layout, x + w / 2, (int)(h.Y - 10 + (_mouse.Y - _down.Y)));
                d.Hazards[i] = s is { } surf ? h with { X1 = x, X2 = x + w, Y = surf.Y } : h with { X1 = x, X2 = x + w };
                break;
            }
            case Kind.Saw:
            {
                var s = d.Saws[i];
                d.Saws[i] = s with { X1 = s.X1 + dx, Y1 = s.Y1 + dy, X2 = s.X2 + dx, Y2 = s.Y2 + dy };
                break;
            }
            case Kind.Drift:
            {
                var from = d.Drifts[i].Rect;
                var to = Inside(new RectI(from.X + dx, from.Y + dy, from.W, from.H));
                d.Drifts[i] = new Drift(to.X, to.Y, to.W, to.H);
                Carry(_dragBase, d, from, to.X - from.X, to.Y - from.Y);
                break;
            }
            case Kind.Mover:
            {
                var m = d.Movers[i];
                int lane = m.X2 - m.X1, x = Math.Clamp(m.X1 + dx, Wall, W - Wall - lane - m.W);
                d.Movers[i] = m with { X1 = x, X2 = x + lane, Y = Math.Clamp(m.Y + dy, Wall + Tile, FloorY - m.H) };
                break;
            }
        }
        Design = d;
    }

    /// <summary>Moves whatever stands on top of a block (spawns, pads, crates, barrels, pools, saws) along with it.</summary>
    private static void Carry(MapDesign from, MapDesign to, RectI block, int dx, int dy)
    {
        bool On(float x1, float x2, int y) => y == block.Y && x2 > block.X && x1 < block.Right;
        for (int k = 0; k < from.Spawns.Count; k++)
            if (On(from.Spawns[k].X, from.Spawns[k].X, from.Spawns[k].Y)) to.Spawns[k] = new SpawnPoint(from.Spawns[k].X + dx, from.Spawns[k].Y + dy);
        for (int k = 0; k < from.Pads.Count; k++)
            if (On(from.Pads[k].X1, from.Pads[k].X2, from.Pads[k].Y)) to.Pads[k] = new JumpPad(from.Pads[k].X1 + dx, from.Pads[k].X2 + dx, from.Pads[k].Y + dy);
        for (int k = 0; k < from.Props.Count; k++)
            if (from.Props[k].Kind != PropKind.Glass && On(from.Props[k].X, from.Props[k].X + from.Props[k].W, from.Props[k].Y + from.Props[k].H))
                to.Props[k] = from.Props[k] with { X = from.Props[k].X + dx, Y = from.Props[k].Y + dy };
        for (int k = 0; k < from.Hazards.Count; k++)
            if (On(from.Hazards[k].X1, from.Hazards[k].X2, from.Hazards[k].Y))
                to.Hazards[k] = from.Hazards[k] with { X1 = from.Hazards[k].X1 + dx, X2 = from.Hazards[k].X2 + dx, Y = from.Hazards[k].Y + dy };
        for (int k = 0; k < from.Saws.Count; k++)
        {
            var s = from.Saws[k];
            if (s.Y1 == s.Y2 && On(s.X1, s.X2, (int)s.Y1 + 2))
                to.Saws[k] = s with { X1 = s.X1 + dx, X2 = s.X2 + dx, Y1 = s.Y1 + dy, Y2 = s.Y2 + dy };
        }
    }

    /// <summary>Hills may not overlap one another.</summary>
    private static bool ValidMove(MapDesign d)
    {
        var spans = d.Hills.Select(h => (h[0].x, h[^1].x)).OrderBy(s => s.Item1).ToList();
        for (int i = 0; i + 1 < spans.Count; i++)
            if (spans[i + 1].Item1 < spans[i].Item2) return false;
        return true;
    }

    private static RectI Inside(RectI r)
    {
        int x = Math.Clamp(r.X, Wall, W - Wall - Math.Min(r.W, W - 2 * Wall));
        int y = Math.Clamp(r.Y, Wall, H - Math.Min(r.H, H - Wall));
        return new RectI(x, y, Math.Min(r.W, W - 2 * Wall), Math.Min(r.H, H - Wall));
    }

    private SpawnPoint DropSpawn(float x, float y)
    {
        int sx = Math.Clamp(Snap4(x), Wall + 8, W - Wall - 8);
        return MapDesign.SurfaceBelow(_layout, sx, (int)y) is { } s ? new SpawnPoint(sx, s.Y) : new SpawnPoint(sx, Snap(y));
    }

    private JumpPad DropPad(float cx, float y)
    {
        int x = Math.Clamp(Snap4(cx - MapGenerator.PadWidth / 2f), Wall, W - Wall - MapGenerator.PadWidth);
        int py = MapDesign.SurfaceBelow(_layout, x + MapGenerator.PadWidth / 2, (int)y) is { } s ? s.Y : Snap(y);
        return new JumpPad(x, x + MapGenerator.PadWidth, py);
    }

    private PropSpawn DropProp(PropKind kind, float cx, float y)
    {
        int w = kind == PropKind.Crate ? ArenaFeatures.CrateSize : ArenaFeatures.BarrelW;
        int h = kind == PropKind.Crate ? ArenaFeatures.CrateSize : ArenaFeatures.BarrelH;
        int x = Math.Clamp((int)MathF.Round(cx - w / 2f), Wall, W - Wall - w);
        if (_shift) return new PropSpawn(kind, x, Math.Clamp((int)MathF.Round(y - h / 2f), Wall, FloorY - h), w, h);
        int top = MapDesign.SurfaceBelow(_layout, x + w / 2, (int)y) is { } s ? s.Y : FloorY;
        return new PropSpawn(kind, x, top - h, w, h);
    }

    /// <summary>A glass pane's whole row (touching panes at the same height), or just the prop for anything else.</summary>
    private static List<int> GlassRow(MapDesign d, int i)
    {
        var p = d.Props[i];
        var row = new List<int> { i };
        if (p.Kind != PropKind.Glass) return row;
        var at = new Dictionary<int, int>();
        for (int k = 0; k < d.Props.Count; k++)
            if (d.Props[k].Kind == PropKind.Glass && d.Props[k].Y == p.Y) at.TryAdd(d.Props[k].X, k);
        for (int x = p.X - ArenaFeatures.GlassTile; at.TryGetValue(x, out int k); x -= ArenaFeatures.GlassTile) row.Add(k);
        for (int x = p.X + ArenaFeatures.GlassTile; at.TryGetValue(x, out int k); x += ArenaFeatures.GlassTile) row.Add(k);
        return row;
    }

    private void ReshapeHill(int i, int by, bool top)
    {
        var hill = Design.Hills[i];
        int height = hill.Max(c => c.h), x1 = hill[0].x;
        var flat = hill.Where(c => c.h == height).Select(c => c.x).ToList();
        int width = flat.Count >= 2 ? flat[^1] - flat[0] : 3 * Tile;
        if (top) width = Math.Clamp(width + by, Tile, 40 * Tile);
        else height = Math.Clamp(height + by, 2 * Tile, 12 * Tile);
        var next = TerrainGen.Hill(x1, height, width);
        // Grow about the middle, and stay inside the walls.
        int shift = (hill[0].x + hill[^1].x - next[0].x - next[^1].x) / 2 / Tile * Tile;
        shift = Math.Clamp(shift, Wall - next[0].x, W - Wall - next[^1].x);
        Design.Hills[i] = next.Select(c => (c.x + shift, c.h)).ToList();
        if (!ValidMove(Design))
        {
            Design.Hills[i] = hill;
            _undo.RemoveAt(_undo.Count - 1);
            Flash("Hills can't overlap each other.");
            return;
        }
        Rebuild();
    }

    private void Delete((Kind K, int I) item)
    {
        var d = Design;
        switch (item.K)
        {
            case Kind.Block: d.Blocks.RemoveAt(item.I); break;
            case Kind.Pit: d.Pits.RemoveAt(item.I); break;
            case Kind.Hill: d.Hills.RemoveAt(item.I); break;
            case Kind.Spawn: d.Spawns.RemoveAt(item.I); break;
            case Kind.Pad: d.Pads.RemoveAt(item.I); break;
            case Kind.Prop:
                foreach (int k in GlassRow(d, item.I).OrderByDescending(k => k)) d.Props.RemoveAt(k);
                break;
            case Kind.Hazard: d.Hazards.RemoveAt(item.I); break;
            case Kind.Saw: d.Saws.RemoveAt(item.I); break;
            case Kind.Mover: d.Movers.RemoveAt(item.I); break;
            case Kind.Drift: d.Drifts.RemoveAt(item.I); break;
            default: return;
        }
        _sel = _hover = (Kind.None, -1);
        Rebuild();
    }

    /// <summary>Ctrl+D: a copy of the selected block, saw, mover or drifting platform a little up and to the right.</summary>
    private void Duplicate()
    {
        var (k, i) = _sel;
        if (k is not (Kind.Block or Kind.Saw or Kind.Mover or Kind.Drift)) return;
        Remember();
        switch (k)
        {
            case Kind.Block:
                Design.Blocks.Add(Inside(new RectI(Design.Blocks[i].X + 2 * Tile, Design.Blocks[i].Y - 2 * Tile, Design.Blocks[i].W, Design.Blocks[i].H)));
                _sel = (k, Design.Blocks.Count - 1);
                break;
            case Kind.Saw:
                var s = Design.Saws[i];
                Design.Saws.Add(s with { X1 = s.X1 + 16, X2 = s.X2 + 16, Y1 = s.Y1 - 16, Y2 = s.Y2 - 16 });
                _sel = (k, Design.Saws.Count - 1);
                break;
            case Kind.Mover:
                var m = Design.Movers[i];
                Design.Movers.Add(m with { Y = Math.Max(Wall + Tile, m.Y - 2 * Tile) });
                _sel = (k, Design.Movers.Count - 1);
                break;
            case Kind.Drift:
                if (Full(Design.Drifts.Count, MapDesign.MaxDrifts, "drifting platforms")) break;
                var dr = Inside(new RectI(Design.Drifts[i].X + 2 * Tile, Design.Drifts[i].Y - 2 * Tile, Design.Drifts[i].W, Design.Drifts[i].H));
                Design.Drifts.Add(new Drift(dr.X, dr.Y, dr.W, dr.H));
                _sel = (k, Design.Drifts.Count - 1);
                break;
        }
        Rebuild();
    }

    // ---------- making new pieces ----------

    private bool Full(int count, int max, string what)
    {
        if (count < max) return false;
        Flash($"That's as many {what} as an arena can hold ({max}).");
        return true;
    }

    private void Create()
    {
        var d = Design;
        float x1 = Math.Min(_down.X, _mouse.X), x2 = Math.Max(_down.X, _mouse.X);
        bool drag = _dragged && x2 - x1 >= Tile;
        var before = d.Copy();
        switch (_tool)
        {
            case EditTool.Block:
            {
                if (Full(d.Blocks.Count, MapDesign.MaxBlocks, "blocks")) break;
                RectI r;
                if (_dragged)
                {
                    int ax = Snap(Math.Min(_down.X, _mouse.X)), bx = Snap(Math.Max(_down.X, _mouse.X));
                    int ay = Snap(Math.Min(_down.Y, _mouse.Y)), by = Snap(Math.Max(_down.Y, _mouse.Y));
                    r = new RectI(ax, ay, Math.Max(Tile, bx - ax), Math.Max(Tile, by - ay));
                }
                else r = new RectI(Snap(_mouse.X) - 4 * Tile, Snap(_mouse.Y), 8 * Tile, Tile);
                d.Blocks.Add(Inside(r));
                break;
            }
            case EditTool.Gap:
            {
                if (Full(d.Pits.Count, MapDesign.MaxPits, "floor gaps")) break;
                int a = drag ? Snap(x1) : Snap(_mouse.X) - 4 * Tile, b = drag ? Snap(x2) : a + 8 * Tile;
                a = Math.Max(a, Wall);
                b = Math.Min(b, W - Wall);
                if (b - a < 2 * Tile) break;
                d.Pits.Add((a, b));
                MergePits(d);
                break;
            }
            case EditTool.Hill:
            {
                if (Full(d.Hills.Count, MapDesign.MaxHills, "hills")) break;
                var hill = TerrainGen.Hill(0, 4 * Tile, 6 * Tile);
                int shift = Math.Clamp(Snap(_mouse.X - hill[^1].x / 2f), Wall, W - Wall - hill[^1].x);
                d.Hills.Add(hill.Select(c => (c.x + shift, c.h)).ToList());
                if (!ValidMove(d))
                {
                    d.Hills.RemoveAt(d.Hills.Count - 1);
                    Flash("Hills can't overlap each other.");
                }
                break;
            }
            case EditTool.Spawn:
                if (!Full(d.Spawns.Count, MapDesign.MaxSpawns, "spawns")) d.Spawns.Add(DropSpawn(_mouse.X, _mouse.Y));
                break;
            case EditTool.Pad:
                if (!Full(d.Pads.Count, MapDesign.MaxPads, "jump pads")) d.Pads.Add(DropPad(_mouse.X, _mouse.Y));
                break;
            case EditTool.Crate or EditTool.Barrel:
                if (!Full(d.Props.Count, MapDesign.MaxProps, "crates, barrels and panes"))
                    d.Props.Add(DropProp(_tool == EditTool.Crate ? PropKind.Crate : PropKind.Barrel, _mouse.X, _mouse.Y));
                break;
            case EditTool.Glass:
            {
                int g = ArenaFeatures.GlassTile;
                int a = drag ? Snap(x1) : Snap(_mouse.X) - 2 * g, n = drag ? Math.Max(1, (Snap(x2) - Snap(x1)) / g) : 4;
                int y = Snap(_down.Y);
                for (int k = 0; k < n && d.Props.Count < MapDesign.MaxProps; k++)
                    if (a + k * g >= Wall && a + (k + 1) * g <= W - Wall) d.Props.Add(new PropSpawn(PropKind.Glass, a + k * g, y, g, g));
                break;
            }
            case EditTool.Magma or EditTool.Poison:
            {
                if (Full(d.Hazards.Count, MapDesign.MaxHazards, "pools")) break;
                if (MapDesign.SurfaceBelow(_layout, (int)_down.X, (int)_down.Y) is not { } s)
                {
                    Flash("A pool needs ground under it.");
                    break;
                }
                int a = drag ? Snap4(x1) : Snap4(_down.X) - 16, b = drag ? Snap4(x2) : a + 32;
                a = Math.Max(a, s.X1);
                b = Math.Min(b, s.X2);
                if (b - a >= 8) d.Hazards.Add(new Hazard(_tool == EditTool.Magma ? HazardKind.Magma : HazardKind.Poison, a, b, s.Y));
                break;
            }
            case EditTool.Saw:
            {
                if (Full(d.Saws.Count, MapDesign.MaxSaws, "sawblades")) break;
                float r = ArenaFeatures.SawRadius;
                if (_dragged && (_mouse - _down).Length() >= 2 * Tile)
                {
                    float ax = Snap(_down.X), ay = Snap(_down.Y), bx = Snap(_mouse.X), by = Snap(_mouse.Y);
                    d.Saws.Add(new SawTrack(ax, ay, bx, by, r, MapDesign.SawPeriod(ax, ay, bx, by), 0));
                }
                else if (MapDesign.SurfaceBelow(_layout, (int)_mouse.X, (int)_mouse.Y) is { } s)
                {
                    float a = Math.Max(s.X1 + 10, Snap(_mouse.X) - 48), b = Math.Min(s.X2 - 10, Snap(_mouse.X) + 48);
                    if (b > a) d.Saws.Add(new SawTrack(a, s.Y - 2, b, s.Y - 2, r, MapDesign.SawPeriod(a, 0, b, 0), 0));
                }
                break;
            }
            case EditTool.Drift:
            {
                if (Full(d.Drifts.Count, MapDesign.MaxDrifts, "drifting platforms")) break;
                var r = Inside(DriftRect());
                d.Drifts.Add(new Drift(r.X, r.Y, r.W, r.H));
                break;
            }
            case EditTool.Mover:
            {
                if (Full(d.Movers.Count, MapDesign.MaxMovers, "moving platforms")) break;
                int mw = ArenaFeatures.MoverW;
                int a = drag ? Snap(x1) : Snap(_mouse.X) - 80, b = drag ? Snap(x2) - mw : a + 160 - mw;
                a = Math.Max(a, Wall);
                b = Math.Min(b, W - Wall - mw);
                int y = Math.Clamp(Snap(_down.Y), Wall + Tile, FloorY - ArenaFeatures.MoverH);
                if (b - a >= 2 * Tile) d.Movers.Add(new Mover(a, b, y, mw, ArenaFeatures.MoverH, MapDesign.MoverPeriod(a, b), 0));
                break;
            }
        }
        // Only a click that made something can be undone.
        if (MapCode.Encode(before) != MapCode.Encode(d))
        {
            _undo.Add(before);
            if (_undo.Count > 100) _undo.RemoveAt(0);
            _redo.Clear();
        }
        Rebuild();
    }

    /// <summary>
    /// The drifting platform the Drift tool would make: the box dragged out (at most four tiles tall), or a six-tile
    /// platform one tile thick centred on a click.
    /// </summary>
    private RectI DriftRect()
    {
        bool drag = _pressed ? _dragged : _dragged && (_mouse - _down).Length() >= Tile;
        if (!drag) return new RectI(Snap(_mouse.X) - 3 * Tile, Snap(_mouse.Y), 6 * Tile, Tile);
        int ax = Snap(Math.Min(_down.X, _mouse.X)), bx = Snap(Math.Max(_down.X, _mouse.X));
        int ay = Snap(Math.Min(_down.Y, _mouse.Y)), by = Snap(Math.Max(_down.Y, _mouse.Y));
        return new RectI(ax, ay, Math.Max(2 * Tile, bx - ax), Math.Clamp(by - ay, Tile, 4 * Tile));
    }

    /// <summary>Joins floor gaps that overlap or touch.</summary>
    private static void MergePits(MapDesign d)
    {
        var pits = d.Pits.OrderBy(p => p.x1).ToList();
        d.Pits.Clear();
        foreach (var p in pits)
        {
            if (d.Pits.Count > 0 && p.x1 <= d.Pits[^1].x2) d.Pits[^1] = (d.Pits[^1].x1, Math.Max(d.Pits[^1].x2, p.x2));
            else d.Pits.Add(p);
        }
    }

    // ---------- drawing ----------

    private static readonly Color Ink = new(0.04f, 0.03f, 0.08f, 0.85f);

    private void DrawCanvas()
    {
        var c = _canvas;
        c.DrawSetTransform(Vector2.Zero, 0, Vector2.One * Zoom);
        var d = Design;
        if (d == null) return;
        float px = 1 / Zoom; // one screen pixel, in world pixels

        // A faint 40 px grid while drawing shapes.
        if (_tool is EditTool.Block or EditTool.Gap or EditTool.Glass or EditTool.Mover or EditTool.Saw or EditTool.Drift)
        {
            var g = new Color(1, 1, 1, 0.05f);
            for (int x = 40; x < W; x += 40) c.DrawLine(new Vector2(x, 0), new Vector2(x, H), g, px);
            for (int y = 2; y < H; y += 40) c.DrawLine(new Vector2(0, y), new Vector2(W, y), g, px);
        }

        // Gaps in the floor (so they can be found and selected), tracks and lanes.
        foreach (var (x1, x2) in d.Pits)
            c.DrawRect(new Rect2(x1, FloorY, x2 - x1, H - FloorY), new Color(1, 0.4f, 0.35f, _tool == EditTool.Gap ? 0.22f : 0.08f));
        foreach (var s in d.Saws)
            Dashed(c, new Vector2(s.X1, s.Y1), new Vector2(s.X2, s.Y2), new Color(1, 0.85f, 0.6f, 0.6f), px * 1.5f);
        foreach (var m in d.Movers)
            c.DrawRect(ToRect(m.Lane), new Color(0.65f, 0.85f, 1f, 0.45f), false, px);
        // How far each drifting platform can be shoved, faintly (brighter with its tool).
        foreach (var dr in d.Drifts)
            c.DrawRect(ToRect(dr.Range), new Color(0.75f, 1f, 0.8f, _tool == EditTool.Drift ? 0.4f : 0.18f), false, px);

        // Spawns: numbered markers (red when they stand on nothing).
        for (int i = 0; i < d.Spawns.Count; i++)
        {
            var sp = d.Spawns[i];
            bool ok = _layout != null && MapGenerator.SurfaceIndexOf(_layout, sp) >= 0;
            var at = new Vector2(sp.X, sp.Y - 9);
            var col = ok ? Palette.Player(i) : new Color("#ff5050");
            c.DrawCircle(at, 8, Ink);
            c.DrawArc(at, 8, 0, Mathf.Tau, 20, col, 2 * px);
            c.DrawString(Fonts.Bold, at + new Vector2(-8, 4.5f), ok ? $"{i + 1}" : "!", HorizontalAlignment.Center, 16, 12, col);
        }

        if (_hover.K != Kind.None && _hover != _sel && !_pressed) Outline(c, _hover, new Color(1, 1, 1, 0.55f), px);
        if (_sel.K != Kind.None)
        {
            Outline(c, _sel, Palette.Cool, 2 * px, fill: _pressed && _dragged);
            if (_sel.K == Kind.Block && _sel.I < d.Blocks.Count)
            {
                var r = ToRect(d.Blocks[_sel.I]);
                foreach (var h in new[] { r.Position, r.End, new Vector2(r.End.X, r.Position.Y), new Vector2(r.Position.X, r.End.Y) })
                    c.DrawRect(new Rect2(h - Vector2.One * 3, Vector2.One * 6), Palette.Cool);
            }
        }
        if (_tool != EditTool.Select && _mouse.X >= 0) Ghost(c, px);
    }

    private static Rect2 ToRect(RectI r) => new(r.X, r.Y, r.W, r.H);

    private static void Dashed(Control c, Vector2 a, Vector2 b, Color col, float width)
    {
        float len = a.DistanceTo(b);
        if (len < 1) return;
        var dir = (b - a) / len;
        for (float t = 0; t < len; t += 8) c.DrawLine(a + dir * t, a + dir * Math.Min(len, t + 5), col, width);
    }

    /// <summary>A piece's outline (and, while it is dragged, a fill so the new spot shows over the old drawing).</summary>
    private void Outline(Control c, (Kind K, int I) item, Color col, float width, bool fill = false)
    {
        var d = Design;
        var f = col with { A = 0.25f };
        void Box(Rect2 r)
        {
            if (fill) c.DrawRect(r, f);
            c.DrawRect(r, col, false, width);
        }
        int i = item.I;
        switch (item.K)
        {
            case Kind.Block when i < d.Blocks.Count: Box(ToRect(d.Blocks[i])); break;
            case Kind.Pit when i < d.Pits.Count: Box(new Rect2(d.Pits[i].x1, FloorY, d.Pits[i].x2 - d.Pits[i].x1, H - FloorY)); break;
            case Kind.Hill when i < d.Hills.Count:
                var poly = HillPolygon(d.Hills[i]);
                if (fill) c.DrawColoredPolygon(poly, f);
                c.DrawPolyline(poly.Append(poly[0]).ToArray(), col, width);
                break;
            case Kind.Spawn when i < d.Spawns.Count: c.DrawArc(new Vector2(d.Spawns[i].X, d.Spawns[i].Y - 9), 11, 0, Mathf.Tau, 24, col, width); break;
            case Kind.Pad when i < d.Pads.Count: Box(new Rect2(d.Pads[i].X1, d.Pads[i].Y - 5, MapGenerator.PadWidth, 6)); break;
            case Kind.Prop when i < d.Props.Count:
                foreach (int k in GlassRow(d, i)) Box(ToRect(d.Props[k].Rect));
                break;
            case Kind.Hazard when i < d.Hazards.Count: Box(ToRect(d.Hazards[i].Rect)); break;
            case Kind.Saw when i < d.Saws.Count:
                var s = d.Saws[i];
                foreach (var p in new[] { new Vector2(s.X1, s.Y1), new Vector2(s.X2, s.Y2) }) c.DrawArc(p, s.Radius + 2, 0, Mathf.Tau, 20, col, width);
                c.DrawLine(new Vector2(s.X1, s.Y1), new Vector2(s.X2, s.Y2), col, width);
                break;
            case Kind.Drift when i < d.Drifts.Count:
                Box(ToRect(d.Drifts[i].Rect));
                c.DrawRect(ToRect(d.Drifts[i].Range), col with { A = 0.5f }, false, width * 0.5f);
                break;
            case Kind.Mover when i < d.Movers.Count:
                Box(ToRect(d.Movers[i].Lane));
                Box(new Rect2(d.Movers[i].X1, d.Movers[i].Y, d.Movers[i].W, d.Movers[i].H));
                break;
        }
    }

    /// <summary>What the current tool would make: the shape being dragged out, or where a click would drop a piece.</summary>
    private void Ghost(Control c, float px)
    {
        var col = Palette.Accent;
        var fill = col with { A = 0.25f };
        float x1 = Math.Min(_down.X, _mouse.X), x2 = Math.Max(_down.X, _mouse.X);
        bool drag = _pressed && _dragged;
        void Box(Rect2 r)
        {
            c.DrawRect(r, fill);
            c.DrawRect(r, col, false, px);
        }
        switch (_tool)
        {
            case EditTool.Block:
                if (drag)
                {
                    int ax = Snap(x1), bx = Snap(x2), ay = Snap(Math.Min(_down.Y, _mouse.Y)), by = Snap(Math.Max(_down.Y, _mouse.Y));
                    Box(new Rect2(ax, ay, Math.Max(Tile, bx - ax), Math.Max(Tile, by - ay)));
                }
                else Box(new Rect2(Snap(_mouse.X) - 4 * Tile, Snap(_mouse.Y), 8 * Tile, Tile));
                break;
            case EditTool.Gap:
                if (drag) Box(new Rect2(Snap(x1), FloorY, Snap(x2) - Snap(x1), H - FloorY));
                else Box(new Rect2(Snap(_mouse.X) - 4 * Tile, FloorY, 8 * Tile, H - FloorY));
                break;
            case EditTool.Hill:
            {
                var hill = TerrainGen.Hill(0, 4 * Tile, 6 * Tile);
                int shift = Math.Clamp(Snap(_mouse.X - hill[^1].x / 2f), Wall, W - Wall - hill[^1].x);
                var poly = HillPolygon(hill.Select(p => (p.x + shift, p.h)).ToList());
                c.DrawColoredPolygon(poly, fill);
                break;
            }
            case EditTool.Spawn:
            {
                var sp = DropSpawn(_mouse.X, _mouse.Y);
                c.DrawArc(new Vector2(sp.X, sp.Y - 9), 8, 0, Mathf.Tau, 20, col, 2 * px);
                break;
            }
            case EditTool.Pad:
            {
                var p = DropPad(_mouse.X, _mouse.Y);
                Box(new Rect2(p.X1, p.Y - 4, MapGenerator.PadWidth, 4));
                break;
            }
            case EditTool.Crate or EditTool.Barrel:
                Box(ToRect(DropProp(_tool == EditTool.Crate ? PropKind.Crate : PropKind.Barrel, _mouse.X, _mouse.Y).Rect));
                break;
            case EditTool.Glass:
            {
                int g = ArenaFeatures.GlassTile;
                int a = drag ? Snap(x1) : Snap(_mouse.X) - 2 * g, n = drag ? Math.Max(1, (Snap(x2) - Snap(x1)) / g) : 4;
                int y = Snap(drag ? _down.Y : _mouse.Y);
                for (int k = 0; k < n; k++) Box(new Rect2(a + k * g, y, g, g));
                break;
            }
            case EditTool.Magma or EditTool.Poison:
            {
                var from = drag ? _down : _mouse;
                if (MapDesign.SurfaceBelow(_layout, (int)from.X, (int)from.Y) is { } s)
                {
                    float a = drag ? Snap4(x1) : Snap4(from.X) - 16, b = drag ? Snap4(x2) : a + 32;
                    a = Math.Max(a, s.X1);
                    b = Math.Min(b, s.X2);
                    var pool = _tool == EditTool.Magma ? new Color("#ff7a33") : new Color("#8de04a");
                    c.DrawRect(new Rect2(a, s.Y - Hazard.Depth, b - a, Hazard.Depth), pool with { A = 0.5f });
                }
                break;
            }
            case EditTool.Saw:
                if (drag)
                {
                    var a = new Vector2(Snap(_down.X), Snap(_down.Y));
                    var b = new Vector2(Snap(_mouse.X), Snap(_mouse.Y));
                    Dashed(c, a, b, col, px * 1.5f);
                    c.DrawArc(a, ArenaFeatures.SawRadius, 0, Mathf.Tau, 20, col, px * 1.5f);
                    c.DrawArc(b, ArenaFeatures.SawRadius, 0, Mathf.Tau, 20, col with { A = 0.5f }, px);
                }
                else c.DrawArc(new Vector2(Snap(_mouse.X), Snap(_mouse.Y)), ArenaFeatures.SawRadius, 0, Mathf.Tau, 20, col, px * 1.5f);
                break;
            case EditTool.Drift:
            {
                var r = DriftRect();
                Box(ToRect(r));
                c.DrawRect(ToRect(new Drift(r.X, r.Y, r.W, r.H).Range), col with { A = 0.5f }, false, px);
                break;
            }
            case EditTool.Mover:
            {
                int mw = ArenaFeatures.MoverW, y = Snap(drag ? _down.Y : _mouse.Y);
                int a = drag ? Snap(x1) : Snap(_mouse.X) - 80, b = drag ? Snap(x2) - mw : a + 160 - mw;
                c.DrawRect(new Rect2(a, y, Math.Max(0, b - a) + mw, ArenaFeatures.MoverH), col, false, px);
                Box(new Rect2(a, y, mw, ArenaFeatures.MoverH));
                break;
            }
        }
    }

    /// <summary>Test hook: an input event on the arena, with its position in canvas pixels (world pixels times <see cref="Zoom"/>).</summary>
    public void Feed(InputEvent e) => CanvasInput(e);

    public override void _Process(double delta)
    {
        if (Visible) Paint();
        if (Visible) PollPads(delta);
        // The undo buttons follow keyboard undo too.
        if (_undoB != null && Visible)
        {
            _undoB.Disabled = _undo.Count == 0;
            _redoB.Disabled = _redo.Count == 0;
        }
    }
}
