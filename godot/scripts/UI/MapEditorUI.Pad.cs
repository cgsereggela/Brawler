using Brawler.Core;
using Brawler.Logic;
using Godot;

namespace Brawler.UI;

/// <summary>
/// The map editor on a controller. The left stick moves a cursor over the arena (the d-pad nudges it one grid step),
/// and the buttons stand in for the mouse: A is the left button (hold and move to drag), B deletes the piece under the
/// cursor, LB and RB change tools, X and Y undo and redo. View switches to the buttons around the arena (Save,
/// Playtest, Back...), moved through like any menu; B or View comes back to the cursor.
/// </summary>
public partial class MapEditorUI
{
    /// <summary>True while a controller drives the arena cursor; the mouse or a key hands it back.</summary>
    public bool PadCursor { get; private set; }

    /// <summary>True while the controller moves through the editor's buttons instead of the cursor (View toggles it).</summary>
    public bool PadMenus { get; private set; }

    /// <summary>The tool in hand.</summary>
    public EditTool Tool => _tool;

    /// <summary>The controller's cursor, in world pixels.</summary>
    public Vector2 PadAt => _padAt;

    private Vector2 _padAt = new(W / 2f, H / 2f);
    private bool _padHeld;
    private const float PadSpeed = 460; // world px a second with the stick all the way over

    private const string PadHint = "Controller: stick moves, A places (hold to drag), B deletes, LB/RB change tool, X/Y undo/redo, View for the buttons.";

    private void PollPads(double delta)
    {
        if (PadCursor && !Pads.LastUsed) SetPadCursor(false);
        foreach (int d in Pads.Unclaimed())
        {
            if (Pads.JustPressed(d, PadLayout.Back) || PadMenus && Pads.JustPressed(d, PadLayout.B))
            {
                if (PadMenus && _library.Visible && !Pads.JustPressed(d, PadLayout.Back)) _library.Visible = false;
                else SetPadMenus(!PadMenus);
                continue;
            }
            if (PadMenus) continue;
            var s = Pads.State(d);
            var stick = new Vector2(s.Move.X, s.Move.Y);
            var nudge = new Vector2((Pads.JustPressed(d, PadLayout.DpadRight) ? 1 : 0) - (Pads.JustPressed(d, PadLayout.DpadLeft) ? 1 : 0),
                (Pads.JustPressed(d, PadLayout.DpadDown) ? 1 : 0) - (Pads.JustPressed(d, PadLayout.DpadUp) ? 1 : 0));
            bool any = stick != Vector2.Zero || nudge != Vector2.Zero || s.Buttons != 0;
            if (!PadCursor && !any) continue;
            if (!PadCursor) SetPadCursor(true);
            GetViewport().GuiReleaseFocus(); // so the stick doesn't also move a button's focus, nor A press it

            if (stick != Vector2.Zero || nudge != Vector2.Zero)
            {
                var to = _padAt + stick * stick.Length() * PadSpeed * (float)delta;
                if (nudge.X != 0) to.X = Snap(_padAt.X) + nudge.X * Tile;
                if (nudge.Y != 0) to.Y = Snap(_padAt.Y) + nudge.Y * Tile;
                _padAt = new Vector2(Mathf.Clamp(to.X, 0, W - 1), Mathf.Clamp(to.Y, 0, H - 1));
                PadPointer(new InputEventMouseMotion());
            }
            if (Pads.JustPressed(d, PadLayout.A)) { _padHeld = true; PadPointer(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true }); }
            else if (_padHeld && !s.Held(PadLayout.A)) { _padHeld = false; PadPointer(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = false }); }
            if (Pads.JustPressed(d, PadLayout.B)) PadPointer(new InputEventMouseButton { ButtonIndex = MouseButton.Right, Pressed = true });
            if (Pads.JustPressed(d, PadLayout.LeftShoulder)) StepTool(-1);
            if (Pads.JustPressed(d, PadLayout.RightShoulder)) StepTool(1);
            if (Pads.JustPressed(d, PadLayout.X)) Undo();
            if (Pads.JustPressed(d, PadLayout.Y)) Redo();
        }
    }

    /// <summary>A mouse event at the controller's cursor, through the same handling as the real mouse.</summary>
    private void PadPointer(InputEventMouse e)
    {
        e.Position = _padAt * Zoom;
        CanvasInput(e);
    }

    private void StepTool(int dir)
    {
        int n = Tools.Length, i = System.Array.FindIndex(Tools, t => t.Tool == _tool);
        SetTool(Tools[((i + dir) % n + n) % n].Tool);
    }

    private void SetPadCursor(bool on)
    {
        PadCursor = on;
        if (!on && _padHeld) { _padHeld = false; PadPointer(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = false }); }
        if (on) _mouse = _padAt;
        SetTool(_tool); // the hint names the controller's buttons
    }

    private void SetPadMenus(bool on)
    {
        PadMenus = on;
        if (on)
        {
            if (_padHeld) { _padHeld = false; PadPointer(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = false }); }
            _save.GrabFocus();
            Flash("Buttons: move with the stick, A to press. B or View goes back to the arena.");
        }
        else GetViewport().GuiReleaseFocus();
        _canvas.QueueRedraw();
    }

    /// <summary>The controller's cursor: a ring with a cross, in the accent color (dim while the buttons have it).</summary>
    private void DrawPadCursor()
    {
        if (!PadCursor) return;
        var c = _canvas;
        c.DrawSetTransform(Vector2.Zero, 0, Vector2.One * Zoom);
        float px = 1 / Zoom;
        var col = PadMenus ? Palette.TextDim : Palette.Accent;
        c.DrawArc(_padAt, 7, 0, Mathf.Tau, 20, col, px * 1.5f);
        c.DrawLine(_padAt - new Vector2(11, 0), _padAt - new Vector2(3, 0), col, px * 1.5f);
        c.DrawLine(_padAt + new Vector2(3, 0), _padAt + new Vector2(11, 0), col, px * 1.5f);
        c.DrawLine(_padAt - new Vector2(0, 11), _padAt - new Vector2(0, 3), col, px * 1.5f);
        c.DrawLine(_padAt + new Vector2(0, 3), _padAt + new Vector2(0, 11), col, px * 1.5f);
    }
}
