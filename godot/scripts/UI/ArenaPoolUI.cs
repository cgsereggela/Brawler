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
/// The lobby's arenas, opened from Match settings: with none ticked every battle gets a fresh generated arena; ticked
/// custom arenas (from the map editor, or pasted as a map code) take turns instead. The host picks (and the choice
/// is kept for the next lobby); everyone else sees the list and can save any of them to their own maps.
/// </summary>
public partial class ArenaPoolUI : Control
{
    private readonly NetworkManager _net;
    private VBoxContainer _list;
    private Label _summary, _message;
    private LineEdit _paste;
    private Control _pasteRow;

    public ArenaPoolUI(NetworkManager net) => _net = net;
    public ArenaPoolUI() { }

    public override void _Ready()
    {
        var dim = new ColorRect { Color = new Color(0.02f, 0.01f, 0.06f, 0.9f), MouseFilter = MouseFilterEnum.Stop };
        dim.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(dim);
        var (_, body) = UiKit.CenteredPanel(this, 460);
        body.AddChild(new TitleText("ARENAS", 24, 1.2f, Palette.Accent, Palette.Hot));
        _summary = UiKit.Label("", UiKit.Small, Palette.TextDim);
        _summary.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        body.AddChild(_summary);
        var scroll = new ScrollContainer { CustomMinimumSize = new Vector2(0, 230), HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        body.AddChild(scroll);
        _list = UiKit.VBox(2);
        _list.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        scroll.AddChild(_list);

        var paste = UiKit.HBox(6);
        _paste = new LineEdit { PlaceholderText = "Paste a friend's map code", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _paste.TextSubmitted += Add;
        paste.AddChild(_paste);
        paste.AddChild(UiKit.Button("Add", () => Add(_paste.Text), 0, "Small"));
        _pasteRow = paste;
        body.AddChild(paste);
        _message = UiKit.Label("", UiKit.Tiny, Palette.Accent);
        body.AddChild(_message);

        var footer = UiKit.HBox(8);
        var none = UiKit.Button("Random only", () => Push(new List<string>()), 0, "Small");
        none.TooltipText = "No custom arenas: a new generated arena every battle";
        footer.AddChild(none);
        var done = UiKit.Primary("DONE", () => Visible = false);
        done.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        footer.AddChild(done);
        body.AddChild(footer);
        _none = none;

        _net.LobbyChanged += Refresh;
    }

    private Button _none;

    public override void _ExitTree() => _net.LobbyChanged -= Refresh;

    public void Open()
    {
        _message.Text = "";
        _paste.Text = "";
        Visible = true;
        Refresh();
    }

    /// <summary>The lobby's arenas as codes, in turn order.</summary>
    private List<string> Chosen() =>
        _net.Lobby.Settings.Maps.Select(MapCode.Unpack).Where(d => d != null).Select(MapCode.Encode).ToList();

    private void Refresh()
    {
        if (!Visible || _list == null) return;
        bool host = _net.IsHost;
        var chosen = Chosen();
        _summary.Text = chosen.Count == 0
            ? "Random arenas: every battle gets a freshly generated arena." + (host ? " Tick custom arenas to play those instead; they take turns, one per battle." : "")
            : $"{chosen.Count} custom arena{(chosen.Count == 1 ? "" : "s")} {(chosen.Count == 1 ? "is played every battle" : "take turns, one per battle")}.";
        _pasteRow.Visible = host;
        _none.Visible = host;
        _none.Disabled = chosen.Count == 0;

        UiKit.ClearChildren(_list);
        var library = MapLibrary.All();
        if (host)
        {
            // Saved maps, then any chosen ones that aren't saved here (pasted, or kept from a deleted file).
            var rows = library.Select(e => (e.Name, e.Code, e.Design)).ToList();
            foreach (var code in chosen)
                if (!rows.Exists(r => r.Code == code) && MapCode.Decode(code) is { } d) rows.Add((d.Name != "" ? d.Name : "Untitled", code, d));
            if (rows.Count == 0)
                _list.AddChild(UiKit.Label("No custom arenas yet. Make one in the Map Editor (main menu), or paste a code below.", UiKit.Small, Palette.TextDim));
            foreach (var (name, code, design) in rows)
            {
                var box = new CheckBox { Text = name, ButtonPressed = chosen.Contains(code), FocusMode = FocusModeEnum.None, SizeFlagsHorizontal = SizeFlags.ExpandFill };
                box.AddThemeFontSizeOverride("font_size", UiKit.Small);
                box.Toggled += on => Toggle(rows.Select(r => r.Code).ToList(), code, on);
                _list.AddChild(Row(box, design, code));
            }
        }
        else
        {
            if (chosen.Count == 0) _list.AddChild(UiKit.Label("The host hasn't picked any custom arenas.", UiKit.Small, Palette.TextDim));
            foreach (var code in chosen)
            {
                var d = MapCode.Decode(code);
                var name = UiKit.Label(d.Name != "" ? d.Name : "Untitled", UiKit.Small, Palette.Text);
                name.SizeFlagsHorizontal = SizeFlags.ExpandFill;
                var row = Row(name, d, code);
                bool saved = MapLibrary.Holds(code);
                var save = UiKit.Button(saved ? "Saved" : "Save", () =>
                {
                    if (d.Name == "") d.Name = "Shared arena";
                    d.Name = MapLibrary.FreeName(d.Name);
                    _message.Text = MapLibrary.Save(d) ? $"Saved {d.Name} to your maps." : "Couldn't save it.";
                    Refresh();
                }, 0, "Small");
                save.Disabled = saved;
                row.AddChild(save);
                _list.AddChild(row);
            }
        }
    }

    /// <summary>An arena's row: its name (or tick box), theme, and Copy for its code.</summary>
    private HBoxContainer Row(Control first, MapDesign d, string code)
    {
        var row = UiKit.HBox(8);
        row.AddChild(first);
        row.AddChild(UiKit.Label(ThemeRules.Name(d.Theme), UiKit.Tiny, Palette.TextDim));
        var copy = UiKit.Button("Copy", () =>
        {
            DisplayServer.ClipboardSet(code);
            _message.Text = $"Copied the code for {(d.Name != "" ? d.Name : "this arena")}: paste it to friends, or into the map editor.";
        }, 0, "Small");
        copy.TooltipText = "Copy this arena's map code";
        row.AddChild(copy);
        return row;
    }

    /// <summary>Ticks or unticks an arena, keeping the list's order as the turn order.</summary>
    private void Toggle(List<string> order, string code, bool on)
    {
        var chosen = Chosen();
        if (on && !chosen.Contains(code))
        {
            if (chosen.Count >= MatchSettings.MaxMaps)
            {
                _message.Text = $"At most {MatchSettings.MaxMaps} arenas.";
                Refresh();
                return;
            }
            chosen.Add(code);
        }
        else if (!on) chosen.Remove(code);
        Push(order.Where(chosen.Contains).Concat(chosen.Where(c => !order.Contains(c))).ToList());
    }

    private void Add(string text)
    {
        if (!_net.IsHost) return;
        var d = MapCode.Decode(text, out var why);
        if (d == null)
        {
            _message.Text = MapCode.Problem(why);
            return;
        }
        var code = MapCode.Encode(d);
        // Keep it, so it's in My maps for next time too.
        if (!MapLibrary.All().Exists(e => e.Code == code))
        {
            if (d.Name == "") d.Name = "Shared arena";
            d.Name = MapLibrary.FreeName(d.Name);
            MapLibrary.Save(d);
            code = MapCode.Encode(d);
        }
        _paste.Text = "";
        _message.Text = $"Added {d.Name}.";
        var chosen = Chosen();
        if (!chosen.Contains(code) && chosen.Count < MatchSettings.MaxMaps) chosen.Add(code);
        Push(chosen);
    }

    private void Push(List<string> codes)
    {
        if (!_net.IsHost) return;
        var s = _net.Lobby.Settings.Copy();
        s.Maps = codes.Select(c => MapCode.Pack(MapCode.Decode(c))).ToList();
        _net.SetSettings(s);
        MapLibrary.LobbyMaps = codes;
        Refresh();
    }

    /// <summary>What the lobby's Arenas button says.</summary>
    public static string Summary(MatchSettings s) => s.Maps.Count switch
    {
        0 => "Random",
        1 => MapCode.Unpack(s.Maps[0]) is { Name: { Length: > 0 } n } ? (n.Length > 12 ? n[..11] + "…" : n) : "1 custom",
        int n => $"{n} custom",
    };
}
