using System.Linq;
using Brawler.Core;
using Brawler.Fx;
using Brawler.Game;
using Brawler.Logic;
using Brawler.Net;
using Brawler.UI;
using Godot;

namespace Brawler;

/// <summary>The automated test matches (--autotest, --autohost, --autojoin, --simulate and friends) and the checks they run.</summary>
public partial class Main
{
    // ---------- starting the automated matches ----------

    // --autotest: an offline match, the local player and bots on autopilot, with the pool and chat checks.
    private bool StartAutotest()
    {
        _autoMode = "offline";
        _game.AutopilotLocal = true;
        _game.DisableHitStop = true;
        _net.LocalName = "Auto";
        _net.StartOffline();
        AddCouch();
        AddTestBots();
        _net.SetSettings(new MatchSettings { BattlesPerRound = 2, RoundsToWin = 3, LoserChoices = 5, WinnerChoices = 3, DisabledCards = new(AutoDisabled) });
        var roundTrip = LobbyState.Deserialize(_net.Lobby.Serialize()).Settings.DisabledCards;
        if (!roundTrip.SetEquals(AutoDisabled)) { Finish(1, $"poolcheck: lobby round trip gave [{string.Join(",", roundTrip)}]"); return false; }
        // A full lobby with 12 arenas: the arenas go once, and the small syncs without them stay one datagram.
        var big = LobbyState.Deserialize(_net.Lobby.Serialize());
        big.Settings.Maps = Enumerable.Range(1, MatchSettings.MaxMaps).Select(i => MapCode.Pack(MapDesign.Generate(MapStyle.Tiers, ThemeId.SunsetPeaks, i * 7919))).ToList();
        while (big.Players.Count < LobbyState.MaxPlayers) big.Players.Add(new PlayerInfo { Slot = big.Players.Count, Name = new string('W', 16), Look = big.Players[0].Look, IsBot = true });
        byte[] fullSync = big.Serialize(), lightSync = big.Serialize(withMaps: false);
        var kept = LobbyState.Deserialize(lightSync, big.Settings.Maps).Settings.Maps;
        if (lightSync.Length > 1000 || !kept.Zip(big.Settings.Maps).All(m => m.First.SequenceEqual(m.Second)) || LobbyState.Deserialize(fullSync).Settings.Maps.Count != MatchSettings.MaxMaps)
        { Finish(1, $"lobby sync: {lightSync.Length} bytes without arenas (max 1000), {fullSync.Length} with, or the arenas didn't survive"); return false; }
        Log.Info($"AUTOTEST lobby sync: {fullSync.Length} bytes with 12 arenas, {lightSync.Length} without, for {big.Players.Count} players");
        StartPoolCheck();
        StartChatCheck();
        Log.Info("AUTOTEST offline starting");
        _game.HostStartMatch();
        return true;
    }

    // --autohost[=port]: host a LAN game with a bot; the match starts once a client joins. False if hosting failed.
    private bool StartAutoHost(string arg)
    {
        _autoMode = "host";
        int port = arg.Contains('=') && int.TryParse(arg.Split('=')[1], out int p) ? p : NetworkManager.DefaultPort;
        _game.AutopilotLocal = true;
        _game.DisableHitStop = true;
        _net.LocalName = "AutoHost";
        _net.UseUpnp = false;
        if (!_net.Host(port)) { Finish(1, "could not host"); return false; }
        AddCouch();
        if (!_noBots) _net.AddBot();
        _net.SetSettings(new MatchSettings { BattlesPerRound = 1, RoundsToWin = 2, LoserChoices = 3, WinnerChoices = 2, DisabledCards = new(AutoDisabled) });
        StartPoolCheck();
        StartChatCheck();
        _net.LobbyChanged += AutoHostMaybeStart;
        return true;
    }

    // --autojoin[=ip:port]: join a LAN game on autopilot.
    private void StartAutoJoin(string arg)
    {
        _autoMode = "join";
        string target = arg.Contains('=') ? arg.Split('=')[1] : $"127.0.0.1:{NetworkManager.DefaultPort}";
        var parts = target.Split(':');
        int port = parts.Length > 1 && int.TryParse(parts[1], out int p) ? p : NetworkManager.DefaultPort;
        _game.AutopilotLocal = true;
        _game.DisableHitStop = true;
        _net.LocalName = "AutoJoin";
        StartPoolCheck();
        StartChatCheck();
        _net.LobbyChanged += AutoJoinCheckPool;
        (_joinHost, _joinPort) = (parts[0], port);
        if (_dropAt > 0) _game.BattleStarted += AutoDropMaybe;
        _net.Join(parts[0], port);
    }

    // --simulate=N: N bot-only matches back to back, for the card stats log.
    private void StartSimulate(string arg)
    {
        _autoMode = "sim";
        _simTotal = arg.Contains('=') && int.TryParse(arg.Split('=')[1], out int n) ? Mathf.Max(1, n) : 20;
        _game.AutopilotLocal = true;
        _game.DisableHitStop = true;
        _game.Stats.Mode = "sim";
        Log.Info($"SIMULATE {_simTotal} matches, {_simMin}-{_simMax} players → {ProjectSettings.GlobalizePath(_game.Stats.FilePath)}");
        SimNextMatch();
    }

    /// <summary>The match of an automated run ended: start the next one, or check what happened and quit.</summary>
    private void TestMatchEnded(int winner)
    {
    if (_autoMode == "sim") SimMatchEnded(winner);
    else if (_rematchTest && _rematches == 0 && _autoMode is "offline" or "host" or "join") AutoVoteRematch();
    else if (_autoMode is not (null or "readme" or "video" or "demo")) // readme, video and demos quit after their match-over screen
    {
        var t = GetTree().CreateTimer(_autoMode == "host" ? 2.0 : _shotDir != null ? 1.0 : 0.5, true, false, true); // --shots: time for the match-over shots
        t.Timeout += () =>
        {
            if (_overlayCheck && _ocStage != OcDone) Finish(1, $"overlaycheck: match ended at check stage {_ocStage}");
            else if (_poolCheck && _poolPicks == 0 && _net.TestRounds != 1 && _game.Mode is not (GameMode.GunGame or GameMode.Survival)) Finish(1, "poolcheck: the match ended without a card pick"); // --rounds=1 and Gun Game have no pick, nor a Survival run lost in its first wave
            else if (_dropAt > 0 && !_rejoinChecked) Finish(1, $"droptest: the match ended without a rejoin");
            else if (ChatCheckProblem() is { } chat) Finish(1, chat);
            else if (ComboCheckProblem() is { } combo) Finish(1, combo);
            else if (LavaCheckProblem() is { } lava) Finish(1, lava);
            else if (CouchCheckProblem() is { } couch) Finish(1, couch);
            else if (DailyCheckProblem() is { } daily) Finish(1, daily);
            else Finish(0, $"match finished, winner slot {winner} | {_game.ScoreLine()}");
        };
    }
    }

    // ---------- --simulate: bot-only matches for the card stats log ----------

    private int _simTotal, _simDone, _simMin = 2, _simMax = LobbyState.MaxPlayers;
    private bool _playersArg; // --players= given: also sets how many play in --autotest, --hudshots and the --menushots lobby

    // ---------- --padcheck: controllers, driven by synthetic joypad events ----------

    private bool _padCheck;

    private async void PadCheck()
    {
        Pads.TestDevices.AddRange(new[] { 0, 1 });
        var tree = GetTree();
        async System.Threading.Tasks.Task Wait(double s) => await ToSignal(tree.CreateTimer(s), SceneTreeTimer.SignalName.Timeout);
        void Press(int dev, int button, bool down) => Input.ParseInputEvent(new InputEventJoypadButton { Device = dev, ButtonIndex = (JoyButton)button, Pressed = down, Pressure = down ? 1 : 0 });
        void Stick(int dev, JoyAxis axis, float v) => Input.ParseInputEvent(new InputEventJoypadMotion { Device = dev, Axis = axis, AxisValue = v });
        async System.Threading.Tasks.Task Tap(int dev, int button)
        {
            Press(dev, button, true);
            await Wait(0.1);
            Press(dev, button, false);
            await Wait(0.1);
        }
        string Name(Control c) => c is Button b ? $"button \"{b.Text}\"" : c?.GetType().Name ?? "nothing";

        await Wait(0.5);
        // The menu: a d-pad press with nothing focused lands on the first button, then moves on from there.
        await Tap(0, PadLayout.DpadDown);
        var first = GetViewport().GuiGetFocusOwner();
        if (first == null || !_menu.IsAncestorOf(first)) { Finish(1, $"padcheck: the d-pad focused {Name(first)}, not a menu button"); return; }
        await Tap(0, PadLayout.DpadDown);
        var second = GetViewport().GuiGetFocusOwner();
        if (second == first) { Finish(1, $"padcheck: the d-pad did not move the focus off {Name(first)}"); return; }
        Log.Info($"PADCHECK menu: focus landed on {Name(first)}, moved to {Name(second)}");

        // The lobby: + Local Player, then A on the second controller.
        FindButton(_menu, "PLAY VS BOTS").EmitSignal(BaseButton.SignalName.Pressed);
        await Wait(0.3);
        if (!_lobby.Visible) { Finish(1, "padcheck: no lobby"); return; }
        FindButton(_lobby, "+ Local Player").EmitSignal(BaseButton.SignalName.Pressed);
        await Wait(0.4);
        await Tap(1, PadLayout.A);
        if (Pads.Couch.DeviceOf(1) != 1 || _net.Lobby.Guest(_net.LocalPeer, 1) is not { } guest) { Finish(1, "padcheck: A on the second controller did not add a local player"); return; }
        Log.Info($"PADCHECK lobby: {guest.Name} joined on controller 1, slot {guest.Slot}");
        if (_shotDir != null) { await Wait(0.3); SaveShot(); }

        // Input: the guest's controller drives the guest, and no longer the keyboard player.
        var pad = new PadInput(1);
        var local = new LocalInput(_game);
        pad.Next(null, 1f / 60);
        local.Next(null, 1f / 60);
        Press(1, PadLayout.RightShoulder, true);
        Press(1, PadLayout.A, true);
        Stick(1, JoyAxis.LeftX, 1);
        await Wait(0.1);
        var f = pad.Next(null, 1f / 60);
        var mine = local.Next(null, 1f / 60);
        Press(1, PadLayout.RightShoulder, false);
        Press(1, PadLayout.A, false);
        Stick(1, JoyAxis.LeftX, 0);
        if (!f.Shoot || !f.JumpHeld || f.JumpPresses != 1 || f.MoveX < 0.9f) { Finish(1, $"padcheck: guest frame shoot={f.Shoot} jump={f.JumpHeld}/{f.JumpPresses} move={f.MoveX:0.00}"); return; }
        if (mine.Shoot || mine.JumpHeld || mine.MoveX != 0) { Finish(1, "padcheck: the guest's controller also drove the keyboard player"); return; }
        // An unclaimed controller drives the keyboard player.
        Press(0, PadLayout.RightShoulder, true);
        Stick(0, JoyAxis.LeftX, -1);
        await Wait(0.1);
        mine = local.Next(null, 1f / 60);
        Press(0, PadLayout.RightShoulder, false);
        Stick(0, JoyAxis.LeftX, 0);
        if (!mine.Shoot || mine.MoveX > -0.9f) { Finish(1, $"padcheck: the spare controller did not drive the keyboard player (shoot={mine.Shoot} move={mine.MoveX:0.00})"); return; }
        Log.Info("PADCHECK input: the guest's controller drives only the guest; the spare one drives the keyboard player");

        // A card pick, chosen on the controllers: the keyboard player first, then the guest.
        _net.AddBot();
        _net.SetSettings(new MatchSettings { BattlesPerRound = 1, RoundsToWin = 2, LoserChoices = 3, WinnerChoices = 2, KillCam = false });
        _game.LavaOverride = 2;
        _game.HostStartMatch();
        if (_shotDir != null)
        {
            // Mid-fight: both crosshairs (the guest's rides their aim line).
            for (int i = 0; i < 100 && _game.Phase != Phase.Fighting; i++) await Wait(0.1);
            Stick(1, JoyAxis.RightX, 0.7f);
            Stick(1, JoyAxis.RightY, -0.7f);
            await Wait(0.6);
            SaveShot();
            Stick(1, JoyAxis.RightX, 0);
            Stick(1, JoyAxis.RightY, 0);
        }
        for (int i = 0; i < 1800 && _game.Phase != Phase.CardPick; i++) await Wait(0.1);
        if (_game.Phase != Phase.CardPick) { Finish(1, "padcheck: no card pick within 3 minutes"); return; }
        var me = _game.LocalMatchPlayer;
        var g = _game.LocalGuests.FirstOrDefault();
        int mineBefore = me.Cards.Count, guestBefore = g?.Cards.Count ?? 0;
        await Wait(1.0);
        if (GetViewport().GuiGetFocusOwner() is not CardView) await Tap(0, PadLayout.DpadRight); // nothing focused yet: the d-pad grabs the first card
        if (GetViewport().GuiGetFocusOwner() is not CardView) { Finish(1, $"padcheck: the card pick has {Name(GetViewport().GuiGetFocusOwner())} focused, not a card"); return; }
        await Tap(0, PadLayout.DpadRight);
        if (_shotDir != null) { await Wait(0.3); SaveShot(); }
        await Tap(0, PadLayout.A);
        await Wait(1.5);
        if (_shotDir != null) SaveShot();
        if (g != null && _game.PickOptions.ContainsKey(g.Slot))
        {
            if (GetViewport().GuiGetFocusOwner() is not CardView) await Tap(1, PadLayout.DpadRight);
            await Tap(1, PadLayout.A);
        }
        await Wait(0.5);
        bool mePicked = !_game.PickOptions.ContainsKey(me.Slot) || me.Cards.Count > mineBefore;
        bool guestPicked = g == null || !_game.PickOptions.ContainsKey(g.Slot) || g.Cards.Count > guestBefore || _game.Phase != Phase.CardPick && g.Cards.Count > guestBefore;
        if (!mePicked || !guestPicked) { Finish(1, $"padcheck: picks by controller: keyboard player {mePicked}, guest {guestPicked}"); return; }
        Log.Info($"PADCHECK pick: {me.Info.Name} took {CardRegistry.Get(me.Cards.Last())?.Name}, {g?.Info.Name} took {(g?.Cards.Count > 0 ? CardRegistry.Get(g.Cards.Last())?.Name : "-")}");
        Finish(0, "padcheck: menus, local co-op join, controller input and card picks all work");
    }

    // ---------- --editorcheck: the map editor, driven by made-up mouse presses ----------

    private async void EditorCheck()
    {
        // Saved maps and settings go somewhere of their own, so the test leaves the player's alone.
        MapLibrary.Dir = "user://editorcheck/maps";
        MapLibrary.SettingsPath = "user://editorcheck/settings.cfg";
        DirAccess.MakeDirRecursiveAbsolute(MapLibrary.Dir);
        foreach (var f in DirAccess.GetFilesAt(MapLibrary.Dir)) DirAccess.RemoveAbsolute($"{MapLibrary.Dir}/{f}");
        MapLibrary.Draft = "";
        MapLibrary.LobbyMaps = new();
        var tree = GetTree();
        async System.Threading.Tasks.Task Wait(double s) => await ToSignal(tree.CreateTimer(s), SceneTreeTimer.SignalName.Timeout);
        var ed = _editor;
        Vector2 At(float x, float y) => new Vector2(x, y) * MapEditorUI.Zoom;
        void Move(float x, float y) => ed.Feed(new InputEventMouseMotion { Position = At(x, y) });
        void Button(float x, float y, bool down, MouseButton b = MouseButton.Left) => ed.Feed(new InputEventMouseButton { Position = At(x, y), ButtonIndex = b, Pressed = down });
        void Click(float x, float y, MouseButton b = MouseButton.Left)
        {
            Move(x, y);
            Button(x, y, true, b);
            Button(x, y, false, b);
        }
        void Drag(float x1, float y1, float x2, float y2)
        {
            Move(x1, y1);
            Button(x1, y1, true);
            for (int i = 1; i <= 4; i++) Move(x1 + (x2 - x1) * i / 4, y1 + (y2 - y1) * i / 4);
            Button(x2, y2, false);
        }
        bool Fail(string why)
        {
            Finish(1, $"editorcheck: {why}");
            return true;
        }

        await Wait(0.5);
        FindButton(_menu, "Map Editor").EmitSignal(BaseButton.SignalName.Pressed);
        await Wait(0.3);
        if (!ed.Visible || _game.EditorView == null || _game.MapNode.Layout == null) { Fail("the Map Editor button didn't open the editor with an arena showing"); return; }
        if (_shotDir != null) { await Wait(2.5); SaveShot("editor_generated"); }

        ed.Load(MapDesign.Empty(ThemeId.ElderForest, 1));
        ed.SetTool(EditTool.Block);
        Drag(300, 470, 420, 478);
        if (ed.Design.Blocks.Count != 1 || ed.Design.Blocks[0].W < 100) { Fail($"dragging with Block made {ed.Design.Blocks.Count} blocks"); return; }
        var block = ed.Design.Blocks[0];
        ed.SetTool(EditTool.Spawn);
        Click(350, 300);
        Click(120, 300);
        if (ed.Design.Spawns.Count != 2 || ed.Design.Spawns[0].Y != block.Y || ed.Design.Spawns[1].Y != MapGenerator.FloorY)
        { Fail($"spawns didn't land on the block and the floor: {string.Join(" ", ed.Design.Spawns)}"); return; }
        ed.SetTool(EditTool.Gap);
        Drag(600, 545, 680, 545);
        ed.SetTool(EditTool.Hill);
        Click(820, 500);
        ed.SetTool(EditTool.Crate);
        Click(250, 200);
        ed.SetTool(EditTool.Poison);
        Drag(150, 500, 200, 500);
        var lay = ed.Layout;
        if (ed.Design.Pits.Count != 1 || ed.Design.Hills.Count != 1 || lay.Slopes.Count != 2 || ed.Design.Props.Count != 1
            || ed.Design.Props[0].Rect.Bottom != MapGenerator.FloorY || ed.Design.Hazards.Count != 1)
        { Fail($"gap/hill/crate/pool: {ed.Design.Pits.Count} pits, {ed.Design.Hills.Count} hills ({lay.Slopes.Count} slopes), {ed.Design.Props.Count} props, {ed.Design.Hazards.Count} pools"); return; }
        if (ed.Design.Problems().Count != 0) { Fail($"problems on a fine arena: {string.Join(" | ", ed.Design.Problems())}"); return; }
        Log.Info($"EDITORCHECK tools: block {block}, 2 spawns, a gap, a hill, a crate and a pool");

        // Select, move, resize, undo, redo, delete.
        ed.SetTool(EditTool.Select);
        Drag(360, 476, 440, 436);
        var moved = ed.Design.Blocks[0];
        if (moved.X != block.X + 80 || moved.Y != block.Y - 40) { Fail($"dragging the block moved it to {moved}, from {block}"); return; }
        if (ed.Design.Spawns[0].Y != moved.Y) { Fail($"the spawn on the block didn't move with it: {ed.Design.Spawns[0]}"); return; }
        Drag(moved.Right, moved.Y + 4, moved.Right + 40, moved.Y + 4);
        if (ed.Design.Blocks[0].W != moved.W + 40) { Fail($"dragging the block's right edge left it {ed.Design.Blocks[0].W} wide, from {moved.W}"); return; }
        ed.Feed(new InputEventKey { Keycode = Key.Z, CtrlPressed = true, Pressed = true });
        ed.Feed(new InputEventKey { Keycode = Key.Z, CtrlPressed = true, Pressed = true });
        if (ed.Design.Blocks[0] != block) { Fail($"two undos left the block at {ed.Design.Blocks[0]}, not {block}"); return; }
        ed.Feed(new InputEventKey { Keycode = Key.Y, CtrlPressed = true, Pressed = true });
        if (ed.Design.Blocks[0] != moved) { Fail("redo didn't put the move back"); return; }
        var crate = ed.Design.Props[0].Rect;
        Click(crate.X + crate.W / 2f, crate.Y + crate.H / 2f, MouseButton.Right);
        if (ed.Design.Props.Count != 0) { Fail("right-click didn't delete the crate"); return; }
        Log.Info("EDITORCHECK editing: move, resize, undo, redo and right-click delete work");

        // A drifting platform: click for a short one, drag it somewhere else, and it survives a map code.
        ed.SetTool(EditTool.Drift);
        Click(700, 300);
        if (ed.Design.Drifts.Count != 1 || ed.Design.Drifts[0].W != 48) { Fail($"clicking with Drift made {ed.Design.Drifts.Count} drifting platforms"); return; }
        var drift = ed.Design.Drifts[0];
        if (_shotDir != null) { await Wait(0.3); SaveShot("editor_drift"); }
        ed.SetTool(EditTool.Select);
        Drag(drift.X + 20, drift.Y + 4, drift.X + 60, drift.Y - 36);
        if (ed.Design.Drifts[0] != drift with { X = drift.X + 40, Y = drift.Y - 40 }) { Fail($"dragging the drifting platform left it at {ed.Design.Drifts[0]}, from {drift}"); return; }
        if (MapCode.Decode(MapCode.Encode(ed.Design)) is not { } back || !back.Drifts.SequenceEqual(ed.Design.Drifts)) { Fail("the drifting platform didn't survive the map code"); return; }
        Click(drift.X + 60, drift.Y - 36, MouseButton.Right);
        if (ed.Design.Drifts.Count != 0) { Fail("right-click didn't delete the drifting platform"); return; }
        Log.Info("EDITORCHECK drift: place, move, share and delete a drifting platform");

        // A conveyor belt: drag right to left along the floor, click it to turn it round, slide it along, share it.
        ed.SetTool(EditTool.Belt);
        Drag(520, 500, 440, 500);
        if (ed.Design.Conveyors.Count != 1 || ed.Design.Conveyors[0] is not { W: 80, Dir: -1 } belt) { Fail($"dragging with Belt made {string.Join(", ", ed.Design.Conveyors)}"); return; }
        if (!Conveyor.OnBlock(ed.Design.Build(2), belt)) { Fail($"the belt {belt} isn't on the ground"); return; }
        if (_shotDir != null) { await Wait(0.3); SaveShot("editor_belt"); }
        Click(480, belt.Y - 1);
        if (ed.Design.Conveyors.Count != 1 || ed.Design.Conveyors[0].Dir != 1) { Fail("clicking the belt didn't turn it around"); return; }
        ed.SetTool(EditTool.Select);
        Drag(belt.X + 20, belt.Y - 1, belt.X + 60, belt.Y - 1);
        if (ed.Design.Conveyors[0] != belt with { X = belt.X + 40, Dir = 1 }) { Fail($"dragging the belt left it at {ed.Design.Conveyors[0]}, from {belt}"); return; }
        if (MapCode.Decode(MapCode.Encode(ed.Design)) is not { } beltBack || !beltBack.Conveyors.SequenceEqual(ed.Design.Conveyors)) { Fail("the belt didn't survive the map code"); return; }
        Click(belt.X + 60, belt.Y - 1, MouseButton.Right);
        if (ed.Design.Conveyors.Count != 0) { Fail("right-click didn't delete the belt"); return; }
        Log.Info("EDITORCHECK belt: lay, turn around, move, share and delete a conveyor belt");

        // A hanging platform: drag one out under the ceiling; it hangs on two chains and lands on the floor.
        ed.SetTool(EditTool.Hang);
        Drag(640, 200, 720, 200);
        if (ed.Design.Hangers.Count != 1 || ed.Design.Hangers[0].W != 80) { Fail($"dragging with Hanger made {ed.Design.Hangers.Count} hanging platforms"); return; }
        var hanger = ed.Design.Hangers[0];
        if (HangerMath.Anchors(ed.Layout, hanger).Length != 2 || HangerMath.LandY(ed.Layout, hanger) != MapGenerator.FloorY - hanger.H)
        { Fail($"the hanging platform {hanger} doesn't hang on two chains over the floor"); return; }
        if (_shotDir != null) { await Wait(0.3); SaveShot("editor_hanger"); }
        if (MapCode.Decode(MapCode.Encode(ed.Design)) is not { } hb || !hb.Hangers.SequenceEqual(ed.Design.Hangers)) { Fail("the hanging platform didn't survive the map code"); return; }
        Click(hanger.X + 10, hanger.Y + 4, MouseButton.Right);
        if (ed.Design.Hangers.Count != 0) { Fail("right-click didn't delete the hanging platform"); return; }
        Log.Info("EDITORCHECK hanger: place, share and delete a hanging platform");

        // A controller: the stick moves a cursor, A places, B deletes, X/Y undo/redo, the bumpers change tools, View the buttons.
        const int padDev = 5;
        Pads.TestDevices.Add(padDev);
        async System.Threading.Tasks.Task PadTap(int button)
        {
            Input.ParseInputEvent(new InputEventJoypadButton { Device = padDev, ButtonIndex = (JoyButton)button, Pressed = true, Pressure = 1 });
            await Wait(0.05);
            Input.ParseInputEvent(new InputEventJoypadButton { Device = padDev, ButtonIndex = (JoyButton)button, Pressed = false });
            await Wait(0.05);
        }
        ed.SetTool(EditTool.Select);
        await PadTap(PadLayout.RightShoulder);
        if (ed.Tool != EditTool.Block || !ed.PadCursor) { Fail($"RB on a controller left the {ed.Tool} tool (cursor {ed.PadCursor})"); return; }
        var padFrom = ed.PadAt;
        Input.ParseInputEvent(new InputEventJoypadMotion { Device = padDev, Axis = JoyAxis.LeftX, AxisValue = -1 });
        Input.ParseInputEvent(new InputEventJoypadMotion { Device = padDev, Axis = JoyAxis.LeftY, AxisValue = -1 });
        await Wait(0.25);
        Input.ParseInputEvent(new InputEventJoypadMotion { Device = padDev, Axis = JoyAxis.LeftX, AxisValue = 0 });
        Input.ParseInputEvent(new InputEventJoypadMotion { Device = padDev, Axis = JoyAxis.LeftY, AxisValue = 0 });
        await Wait(0.05);
        if (ed.PadAt.X > padFrom.X - 40 || ed.PadAt.Y > padFrom.Y - 40) { Fail($"the stick moved the cursor from {padFrom} to {ed.PadAt}"); return; }
        int padBlocks = ed.Design.Blocks.Count;
        await PadTap(PadLayout.A);
        if (ed.Design.Blocks.Count != padBlocks + 1) { Fail($"A with the block tool left {ed.Design.Blocks.Count} blocks, not {padBlocks + 1}"); return; }
        await PadTap(PadLayout.B);
        if (ed.Design.Blocks.Count != padBlocks) { Fail($"B on a controller didn't delete the block under the cursor at {ed.PadAt} (blocks {string.Join(" ", ed.Design.Blocks.Skip(padBlocks))})"); return; }
        await PadTap(PadLayout.X);
        await PadTap(PadLayout.Y);
        if (ed.Design.Blocks.Count != padBlocks) { Fail("X then Y (undo, redo) on a controller didn't end where they started"); return; }
        await PadTap(PadLayout.Back);
        if (!ed.PadMenus || GetViewport().GuiGetFocusOwner()?.IsVisibleInTree() != true) { Fail("View on a controller didn't move it to the editor's buttons"); return; }
        await PadTap(PadLayout.B);
        if (ed.PadMenus || GetViewport().GuiGetFocusOwner() != null) { Fail("B didn't bring the controller back to the arena cursor"); return; }
        Pads.TestDevices.Remove(padDev);
        Pads.KeyboardUsed();
        await Wait(0.05);
        ed.SetTool(EditTool.Select);
        Log.Info("EDITORCHECK controller: the stick moves the cursor, A places, B deletes, bumpers change tools, View reaches the buttons");

        // Name it, save it, share it, open the code again.
        var name = FindChild<LineEdit>(ed, l => l.PlaceholderText.StartsWith("Name"));
        name.Text = "Check Arena";
        name.EmitSignal(LineEdit.SignalName.TextChanged, name.Text);
        FindButton(ed, "Save").EmitSignal(BaseButton.SignalName.Pressed);
        var saved = MapLibrary.All();
        if (saved.Count != 1 || saved[0].Name != "Check Arena") { Fail($"Save left {saved.Count} maps in the library"); return; }
        var code = MapCode.Encode(ed.Design);
        ed.Load(MapDesign.Empty(ThemeId.NeonNight, 1));
        if (!ed.Import(" " + code + "\n") || MapCode.Encode(ed.Design) != code) { Fail("pasting the code back didn't reopen the same arena"); return; }
        if (ed.Import(code[..^4])) { Fail("a cut-off code was accepted"); return; }
        Log.Info($"EDITORCHECK sharing: saved, and the {code.Length}-character code reopens it: {code}");

        // Names that make the same file name ("check_arena") each keep a file of their own.
        var twins = new[] { "Check Arena!", "Check Arena?" }.Select(n => { var t = MapCode.Decode(code); t.Name = n; return t; }).ToList();
        foreach (var t in twins) MapLibrary.Save(t);
        twins[0].Seed++;
        MapLibrary.Save(twins[0]); // a second save replaces its own file only
        saved = MapLibrary.All();
        if (saved.Count != 3 || saved.Select(e => e.Name).Distinct().Count() != 3 || saved.First(e => e.Name == "Check Arena!").Design.Seed != twins[0].Seed)
        { Fail($"maps whose names make the same file name overwrote each other: {string.Join(", ", saved.Select(e => e.Name))}"); return; }
        var copy = MapCode.Decode(code);
        copy.Name = MapLibrary.FreeName("Check Arena");
        MapLibrary.Save(copy);
        if (copy.Name != "Check Arena 2" || MapLibrary.FreeName("Check Arena 2") != "Check Arena 3") { Fail($"a clashing name became \"{copy.Name}\" / \"{MapLibrary.FreeName("Check Arena 2")}\", not Check Arena 2 / 3"); return; }
        MapLibrary.Delete(copy.Name);
        foreach (var t in twins) MapLibrary.Delete(t.Name);
        if (MapLibrary.All() is not [{ Name: "Check Arena" }]) { Fail("deleting the look-alike maps took the wrong file"); return; }
        Log.Info("EDITORCHECK library: look-alike names get files of their own, and clashing names count up");
        FindButton(ed, "My maps").EmitSignal(BaseButton.SignalName.Pressed);
        await Wait(0.2);
        if (FindChild<TextureRect>(ed, t => t.Texture != null && t.IsVisibleInTree() && t.CustomMinimumSize == MapThumb.Size) is not { } thumb || thumb.Texture.GetWidth() < 64)
        { Fail("My maps shows no thumbnail for the saved arena"); return; }
        if (_shotDir != null) { await Wait(0.3); SaveShot("editor_library"); }
        FindButton(ed, "DONE").EmitSignal(BaseButton.SignalName.Pressed);
        Log.Info("EDITORCHECK thumbnails: My maps shows a picture of each arena");
        if (_shotDir != null)
            foreach (var style in System.Enum.GetValues<MapStyle>())
                MapThumb.Texture(MapDesign.Generate(style, (ThemeId)((int)style % ArenaTheme.All.Length), 77)).GetImage().SavePng($"{_shotDir}/thumb_{style}.png");

        if (_shotDir != null)
        {
            ed.SetTool(EditTool.Select);
            Click(moved.X + 20, moved.Y + 4);
            await Wait(2.5);
            SaveShot("editor_custom");
        }

        // Playtest: a match against a bot on exactly this arena.
        var design = ed.Design.Copy();
        _game.AutopilotLocal = true;
        FindButton(ed, "PLAYTEST").EmitSignal(BaseButton.SignalName.Pressed);
        await Wait(0.3);
        if (!_lobby.Visible || _net.Lobby.Settings.Maps.Count != 1) { Fail("Playtest didn't open a lobby with the arena chosen"); return; }
        _net.Lobby.Settings.BattlesPerRound = 1;
        _net.Lobby.Settings.RoundsToWin = 1;
        _game.HostStartMatch();
        for (int i = 0; i < 100 && _game.BattleNumber == 0; i++) await Wait(0.05);
        var played = _game.MapNode.Layout;
        var expected = design.Build(2);
        if (played == null || !played.Solids.SequenceEqual(expected.Solids) || !played.Slopes.SequenceEqual(expected.Slopes) || _game.ArenaName != "Check Arena")
        { Fail($"the playtest battle wasn't on the arena (name {_game.ArenaName}, {played?.Solids.Count} solids vs {expected.Solids.Count})"); return; }
        var spawned = _game.Slots.Where(p => p?.Avatar != null).Select(p => p.Avatar.Position).ToList();
        if (spawned.Count != 2 || !spawned.All(at => design.Spawns.Any(sp => at.DistanceTo(new Vector2(sp.X, sp.Y)) < 30)))
        { Fail($"fighters didn't start on the arena's spawns: {string.Join(" ", spawned)}"); return; }
        // The overlay offers the arena: here already saved, so its Save says so.
        if (MapCode.Decode(_game.ArenaCode ?? "")?.Name != "Check Arena") { Fail($"the battle's arena code is {_game.ArenaCode ?? "missing"}"); return; }
        OpenOverlay();
        var savedArena = FindButton(_options, "SAVED");
        bool arenaRowShown = savedArena is { Disabled: true } && savedArena.IsVisibleInTree() && FindButton(_options, "COPY CODE") != null;
        CloseOverlay();
        if (!arenaRowShown) { Fail("the match overlay doesn't offer the custom arena (Saved / Copy code)"); return; }
        if (_shotDir != null) { await Wait(1.5); SaveShot("editor_playtest"); }
        Log.Info("EDITORCHECK playtest: the battle is played on the arena, from its spawns, and the overlay offers it");

        // Leaving the playtest goes back to the editor, with the arena still there.
        _autoMode = null; // leaving is the point here, not a failure
        _net.Leave();
        await Wait(0.3);
        if (!ed.Visible || MapCode.Encode(ed.Design) != MapCode.Encode(design)) { Fail("leaving the playtest didn't come back to the editor and its arena"); return; }
        // Back warns once about changes that aren't in My maps, then leaves.
        var unsaved = MapDesign.Generate(MapStyle.Tiers, ThemeId.NeonNight, 4242);
        unsaved.Name = "Unsaved Arena";
        ed.Import(MapCode.Encode(unsaved));
        FindButton(ed, "Back").EmitSignal(BaseButton.SignalName.Pressed);
        await Wait(0.2);
        if (!ed.Visible || !ed.Unsaved) { Fail("Back left the editor without warning about an unsaved arena"); return; }
        Log.Info("EDITORCHECK leaving: Back warns once about an arena that isn't in My maps");
        // The lobby's arenas: tick the saved arena, then back to random.
        FindButton(ed, "Back").EmitSignal(BaseButton.SignalName.Pressed);
        await Wait(0.2);
        FindButton(_menu, "PLAY VS BOTS").EmitSignal(BaseButton.SignalName.Pressed);
        await Wait(0.3);
        var arenas = FindButton(_lobby, "Random");
        if (arenas == null || _net.Lobby.Settings.Maps.Count != 0) { Fail("a new lobby didn't start on random arenas"); return; }
        arenas.EmitSignal(BaseButton.SignalName.Pressed);
        await Wait(0.2);
        var tick = FindChild<CheckBox>(_lobby, c => c.Text == "Check Arena" && c.IsVisibleInTree());
        if (tick == null) { Fail("the saved arena isn't listed in the lobby's arenas"); return; }
        if (FindChild<Button>(_lobby, b => b.Text == "Copy" && b.IsVisibleInTree() && b.GetParent() == tick.GetParent()) == null) { Fail("the lobby's arenas have no Copy button"); return; }
        if (FindChild<TextureRect>(_lobby, t => t.Texture != null && t.GetParent() == tick.GetParent()) == null) { Fail("the lobby's arenas have no thumbnail"); return; }
        tick.ButtonPressed = true;
        await Wait(0.2);
        if (_net.Lobby.Settings.Maps.Count != 1 || arenas.Text != "Check Arena" || MapLibrary.LobbyMaps.Count != 1)
        { Fail($"ticking the arena left {_net.Lobby.Settings.Maps.Count} arenas chosen, the button saying {arenas.Text}"); return; }
        if (_shotDir != null) SaveShot("editor_lobby_arenas");
        FindButton(_lobby, "Random only").EmitSignal(BaseButton.SignalName.Pressed);
        if (_net.Lobby.Settings.Maps.Count != 0) { Fail("Random only didn't clear the arenas"); return; }
        Log.Info("EDITORCHECK lobby: the saved arena can be ticked for a match, and Random only clears it");
        Finish(0, "editorcheck: drawing, editing, saving, sharing, playtesting and picking an arena all work");
    }

    private static T FindChild<T>(Node root, System.Func<T, bool> match) where T : Node
    {
        foreach (var c in root.GetChildren())
        {
            if (c is T t && match(t)) return t;
            if (FindChild(c, match) is { } found) return found;
        }
        return null;
    }

    // ---------- --couch=N: local co-op guests in the automated matches ----------

    private int _couch;

    /// <summary>Adds the --couch guests, on made-up controller ids (they play on autopilot, so no controller is read).</summary>
    private void AddCouch()
    {
        for (int i = 0; i < _couch; i++)
            if (!_net.AddGuest(100 + i)) Log.Info($"COUCHCHECK could not add guest {i + 1}");
    }

    private string CouchCheckProblem()
    {
        if (_couch == 0) return null;
        var guests = _game.LocalGuests;
        if (guests.Count != _couch) return $"couchcheck: {guests.Count} couch guests in the match, expected {_couch}";
        var me = _game.LocalMatchPlayer;
        foreach (var g in guests)
        {
            if (g.Info.PeerId != _net.LocalPeer || g.Info.Guest < 1) return $"couchcheck: {g.Info.Name} is not a guest of this machine";
            // When there were card picks, everyone at this machine took cards, not just the keyboard player.
            if (me != null && me.Cards.Count > 0 && g.Cards.Count == 0) return $"couchcheck: guest {g.Info.Name} never picked a card";
        }
        var remote = _net.IsHost ? _net.Lobby.Players.Count(p => p.Guest > 0 && p.PeerId != _net.LocalPeer) : 0;
        Log.Info($"COUCHCHECK {guests.Count} guests played: {string.Join(", ", guests.Select(g => $"{g.Info.Name} {g.Cards.Count} cards"))}" +
                 (_net.IsHost ? $"; guests from other machines: {remote}" : ""));
        return null;
    }

    /// <summary>Bots to add beside the local player in the test modes that take --players= (default four players).</summary>
    private void AddTestBots() { for (int i = 1; i < (_playersArg ? _simMax : 4); i++) _net.AddBot(); }

    private readonly Rng _simRng = new((ulong)System.DateTime.Now.Ticks);
    private readonly System.Diagnostics.Stopwatch _simClock = new();
    private long _battleFrames;
    private float _battleCap; // --battlecap=SECS (diagnostics): abandon a single battle that runs longer than this

    /// <summary>One log line per live fighter, for working out why a simulated battle drags on.</summary>
    private void LogStall(string why)
    {
        Log.Info($"STALL {why}: battle {_game.BattleNumber}, {_battleFrames / 60}s, map seed {_game.Map?.Seed}");
        foreach (var a in _game.LiveAvatars())
        {
            var cards = string.Join(",", a.Player.Cards.Select(id => CardRegistry.Get(id)?.Name ?? id.ToString()));
            var bot = a.Player.Input is BotBrain b ? b.DebugState : "";
            Log.Info($"  slot {a.Player.Slot} pos ({a.GlobalPosition.X:0},{a.GlobalPosition.Y:0}) vel ({a.Velocity.X:0},{a.Velocity.Y:0}) hp {a.Hp:0}/{a.S[Stat.MaxHp]:0} floor {a.IsOnFloor()} {bot} cards [{cards}]");
        }
    }

    private void SimNextMatch()
    {
        if (!_simClock.IsRunning) _simClock.Start();
        int players = _simRng.Range(_simMin, _simMax);
        _net.LocalName = "Sim";
        _net.StartOffline();
        for (int i = 1; i < players; i++) _net.AddBot();
        _net.SetSettings(new MatchSettings()); // the lobby defaults: what people actually play
        _game.DisableKillCam = true;
        _frames = 0;
        _matchFinished = false;
        _game.HostStartMatch();
    }

    private void SimMatchEnded(int winner)
    {
        _simDone++;
        double secs = _simClock.Elapsed.TotalSeconds;
        Log.Info($"SIMULATE {_simDone}/{_simTotal} done (winner slot {winner}, {_game.BattleNumber} battles, {secs:0}s elapsed) | {_game.ScoreLine()}");
        // Defer: we're inside the BattleOver RPC; tearing the match down right here would pull state from under it.
        Callable.From(() =>
        {
            _game.HostReturnToLobby();
            if (_simDone >= _simTotal) Finish(0, $"simulated {_simDone} matches in {secs:0}s");
            else SimNextMatch();
        }).CallDeferred();
    }

    private void AutoHostMaybeStart()
    {
        // Another machine has joined (couch guests on this one don't count).
        if (_net.InMatch || _net.Lobby.Players.Where(p => !p.IsBot).Select(p => p.PeerId).Distinct().Count() < 2) return;
        _net.LobbyChanged -= AutoHostMaybeStart;
        Log.Info("AUTOTEST host: client joined, starting match");
        GetTree().CreateTimer(1.0).Timeout += () => _game.HostStartMatch();
    }

    public override void _PhysicsProcess(double delta)
    {
        if (_autoMode == null) return;
        if (_overlayCheck && _ocStage != OcDone) OverlayCheckStep();
        if (_leaveWhen != null && _leaveStage < 2) LeaveCheckStep();
        if (_chatCheck) PollGhosts();
        if (_lavaCheck) PollLava();
        if (_autoMode == "hazardcheck") PollHazards();
        if (_autoMode == "vantagecheck") PollVantage();
        if (_autoMode == "hangercheck") PollHanger();
        if (_autoMode == "beltcheck") PollBelts();
        if (_autoMode == "driftcheck") PollDrift();
        if (_autoMode == "trainingcheck") PollTraining();
        if (_autoMode == "cardcheck") PollCards();
        if (_autoMode == "sim" && _game.Phase == Phase.Fighting)
        {
            _battleFrames++;
            if (_battleFrames % (60 * 60) == 0) LogStall("long battle");
            if (_battleCap > 0 && _battleFrames > _battleCap * 60)
            {
                LogStall("battle cap");
                Log.Info("SIMULATE match timed out, abandoning it (saved as incomplete)");
                _frames = 0;
                _battleFrames = 0;
                SimMatchEnded(-1);
                return;
            }
        }
        if (++_frames > AutotestMaxFrames)
        {
            if (_autoMode != "sim") { Finish(1, "timed out before the match ended"); return; }
            LogStall("match timeout");
            Log.Info("SIMULATE match timed out, abandoning it (saved as incomplete)");
            _frames = 0;
            SimMatchEnded(-1);
        }
        if (_shotDir != null && _autoMode != "readme" && _frames % 240 == 0 && _shotCount < 40) SaveShot();
        // And one mid-countdown each battle, where the YOU marker shows.
        bool counting = _game.Phase == Phase.Countdown && _game.CountdownLeft < GameManager.CountdownTime / 2;
        if (_shotDir != null && _autoMode != "readme" && counting && !_countdownShot && _shotCount < 40) SaveShot();
        _countdownShot = counting;
    }

    // ---------- --dropat=N (with --autojoin): leave in battle N, join again, and check the slot came back ----------

    private int _dropAt, _dropCards, _joinPort;
    private bool _noBots, _dropping, _rejoinChecked;
    private bool _countdownShot;
    private string _joinHost;

    private void AutoDropMaybe()
    {
        if (_dropping || _rejoinChecked || _game.BattleNumber != _dropAt) return;
        _dropping = true;
        _dropCards = _game.LocalMatchPlayer?.Cards.Count ?? -1;
        GetTree().CreateTimer(1.5).Timeout += () =>
        {
            Log.Info($"DROPTEST leaving in battle {_dropAt} with {_dropCards} cards");
            _net.Leave();
            _game.MatchBegan += AutoRejoinCheck;
            GetTree().CreateTimer(1.0).Timeout += () => _net.Join(_joinHost, _joinPort);
        };
    }

    private void AutoRejoinCheck()
    {
        _game.MatchBegan -= AutoRejoinCheck;
        _dropping = false;
        var me = _game.LocalMatchPlayer;
        if (me == null || me.Cards.Count != _dropCards)
        {
            Finish(1, $"droptest: rejoined with {me?.Cards.Count ?? -1} cards, had {_dropCards}");
            return;
        }
        _rejoinChecked = true;
        Log.Info($"DROPTEST rejoined slot {me.Slot} with all {_dropCards} cards");
    }

    // ---------- --rematch (with --autotest, --autohost or --autojoin): vote for a rematch, check it starts fresh ----------

    private bool _rematchTest;
    private int _rematches;

    private void AutoVoteRematch()
    {
        // The host votes first and the client a second later, so the host has to wait for the second vote.
        GetTree().CreateTimer(_autoMode == "join" ? 1.5 : 0.5, true, false, true).Timeout += () =>
        {
            Log.Info($"REMATCHTEST voting for a rematch ({_game.RematchVotes.Count} votes in so far)");
            _game.MatchBegan += AutoRematchCheck;
            _game.LocalVoteRematch(true);
        };
    }

    private void AutoRematchCheck()
    {
        _game.MatchBegan -= AutoRematchCheck;
        var stale = _game.Slots.Where(p => p != null && (p.Cards.Count > 0 || p.RoundWins > 0 || p.BattleWins > 0)).ToList();
        if (stale.Count > 0 || _game.RematchVotes.Count > 0)
        {
            Finish(1, $"rematchtest: rematch didn't start fresh: {string.Join(", ", stale.Select(p => p.Info.Name))}, {_game.RematchVotes.Count} votes left");
            return;
        }
        _rematches++;
        Log.Info($"REMATCHTEST rematch started with {_game.Active().Count()} players");
    }

    // ---------- emote, ping and ghost check (--autotest, --autohost, --autojoin) ----------

    private bool _chatCheck;
    private int _myEmotes, _myPings, _otherChat;
    // Each ghost seen (avatars are rebuilt every battle, so keyed by instance): who, how long it was up, how far it went.
    private readonly System.Collections.Generic.Dictionary<ulong, (string Name, float Age, float Travel)> _ghosts = new();

    /// <summary>
    /// Two seconds into every fight, the autopilot emotes (a different one each battle) and pings above its head. Our
    /// own emotes and pings must come back from the host, and every ghost that was up for a second must have moved.
    /// </summary>
    private void StartChatCheck()
    {
        _chatCheck = true;
        StartComboCheck();
        _game.EmoteShown += (slot, _) => { if (slot == _game.LocalMatchPlayer?.Slot) _myEmotes++; else _otherChat++; };
        _game.PingShown += (slot, _) => { if (slot == _game.LocalMatchPlayer?.Slot) _myPings++; else _otherChat++; };
        _game.BattleStarted += () => GetTree().CreateTimer(GameManager.CountdownTime + 2.0, true, false, true).Timeout += () =>
        {
            var me = _game.LocalMatchPlayer?.Avatar;
            if (me == null || _game.Phase != Phase.Fighting) return;
            _game.LocalEmote(_game.BattleNumber % Emotes.All.Length);
            _game.LocalPing(me.GlobalPosition + new Vector2(0, -40));
        };
        // With --shots: a few pictures of a ghost's bubble, once the ghost has risen.
        if (_shotDir != null)
            _game.EmoteShown += (slot, _) =>
            {
                if (_chatShots >= 3 || _game.Slots[slot]?.Avatar is not { Alive: false }) return;
                int n = ++_chatShots;
                GetTree().CreateTimer(Avatar.GhostDelay + 0.3, true, false, true).Timeout += () => SaveShot($"chat_{n}");
            };
    }

    private int _chatShots;

    private void PollGhosts()
    {
        foreach (var p in _game.Slots)
            if (p?.Avatar is { GhostUp: true } a)
                _ghosts[a.GetInstanceId()] = (p.Info.Name, a.GhostAge, a.GhostTravel);
    }

    /// <summary>Null when the check passed (or isn't running); otherwise what went wrong.</summary>
    private string ChatCheckProblem()
    {
        if (!_chatCheck) return null;
        var stuck = _ghosts.Values.Where(g => g.Age > 1f && g.Travel < 10f).ToList();
        Log.Info($"CHATCHECK own emotes {_myEmotes}, own pings {_myPings}, others' {_otherChat}; ghosts {_ghosts.Count}, "
            + $"floated {(_ghosts.Count > 0 ? _ghosts.Values.Average(g => g.Travel) : 0):0} px on average");
        if (_myEmotes == 0) return "chatcheck: our emotes never came back from the host";
        if (_myPings == 0) return "chatcheck: our pings never came back from the host";
        if (_ghosts.Count == 0) return "chatcheck: no ghost ever rose";
        if (stuck.Count > 0) return $"chatcheck: ghosts that never moved: {string.Join(", ", stuck.Select(g => g.Name))}";
        return null;
    }

    // ---------- combo check (with the chat check) ----------

    private readonly System.Collections.Generic.List<(int Slot, ComboDef Combo)> _combosFound = new();

    private void StartComboCheck()
    {
        _game.ComboFound += (slot, combo) => _combosFound.Add((slot, combo));
        _game.MatchBegan += _combosFound.Clear; // a rematch starts everyone with no cards again
        if (_shotDir == null) return;
        // With --shots: an offer with a COMBO tag on it, and the "combo found" panel after our pick.
        _game.CardPickBegan += () =>
        {
            var me = _game.LocalMatchPlayer;
            if (_comboShots >= 4 || me == null || !_game.PickOptions.TryGetValue(me.Slot, out var opts)) return;
            if (!opts.Any(id => Combos.Unlocks(me.Cards, id).Count > 0)) return;
            int n = ++_comboShots;
            GetTree().CreateTimer(0.6, true, false, true).Timeout += () => SaveShot($"combo_offer_{n}");
        };
        _game.ComboFound += (slot, _) =>
        {
            if (_comboShots >= 4 || slot != _game.LocalMatchPlayer?.Slot) return;
            int n = ++_comboShots;
            GetTree().CreateTimer(0.4, true, false, true).Timeout += () => SaveShot($"combo_found_{n}");
        };
    }

    private int _comboShots;

    /// <summary>
    /// Every combo announced must really be active for that player at the end, and every combo a player ends with must
    /// have been announced. Skipped with --cards, which replaces everyone's cards each battle, and after a rejoin.
    /// </summary>
    private string ComboCheckProblem()
    {
        if (!_chatCheck) return null;
        bool dealt = _game.DebugCards != null || _dropAt > 0;
        var players = _game.Slots.Where(p => p != null).ToList();
        int held = players.Sum(p => Combos.Active(p.Cards).Count);
        Log.Info($"COMBOCHECK combos found {_combosFound.Count}, held at the end {held}, bounce blasts {_game.Bullets.BounceBlasts}");
        if (dealt) return null;
        foreach (var (slot, combo) in _combosFound)
            if (_game.Slots[slot] is { } p && !combo.ActiveFor(p.Cards))
                return $"combocheck: {p.Info.Name} was told they found {combo.Name} but their cards don't make it";
        foreach (var p in players)
            foreach (var combo in Combos.Active(p.Cards))
                if (!_combosFound.Contains((p.Slot, combo)))
                    return $"combocheck: {p.Info.Name} holds {combo.Name} but it was never announced";
        return null;
    }

    // ---------- lava check (--lavacheck with --autotest, --autohost, --autojoin) ----------

    private bool _lavaCheck;
    private readonly System.Collections.Generic.HashSet<int> _lavaBattles = new(), _lavaShots = new();
    private readonly System.Collections.Generic.Dictionary<int, float> _inLava = new();
    private float _lavaWorst, _lavaOver; // longest anyone stood in it; and longest past what their max HP should last

    private void PollLava()
    {
        if (_game.Phase != Phase.Fighting || _game.Map == null) return;
        float y = _game.Arena.LavaY;
        int battle = _game.BattleNumber;
        if (y < MapGenerator.FloorY - 40) _lavaBattles.Add(battle);
        if (_shotDir != null && y < MapGenerator.FloorY - 120 && _lavaShots.Add(battle)) SaveShot($"lava_{battle:D2}");
        // How long each fighter has stood in it without dying: lava that doesn't hurt (or a death that never synced) shows up here.
        foreach (var a in _game.LiveAvatars())
        {
            int slot = a.Player.Slot;
            bool inside = a.GlobalPosition.Y + a.HalfSize.Y - 2 > y;
            float t = inside ? (_inLava.TryGetValue(slot, out float was) ? was : 0) + 1f / 60f : 0;
            _inLava[slot] = t;
            _lavaWorst = Mathf.Max(_lavaWorst, t);
            // Cards that add HP make the lava take longer: measure past the time it needs to burn through a full health bar.
            _lavaOver = Mathf.Max(_lavaOver, t - a.S[Stat.MaxHp] / RisingLava.Dps);
        }
    }

    /// <summary>Null when the check passed (or isn't running); otherwise what went wrong.</summary>
    private string LavaCheckProblem()
    {
        if (!_lavaCheck) return null;
        Log.Info($"LAVACHECK setting {_game.Settings.LavaAfter}s, lava over the floor in {_lavaBattles.Count}/{_game.BattleNumber} battles, longest anyone lived in it {_lavaWorst:0.00}s ({_lavaOver:0.00}s past what their max HP should last)");
        if (_game.Settings.LavaAfter != 3) return $"lavacheck: this peer got lava after {_game.Settings.LavaAfter}s, not 3";
        if (_lavaBattles.Count == 0) return "lavacheck: the lava never rose over the floor";
        if (_lavaOver > 1f) return $"lavacheck: someone lived {_lavaOver:0.0}s longer in the lava than their max HP should last";
        return null;
    }

    // ---------- card pool check (--autotest, --autohost, --autojoin) ----------

    // Out of the pool in test matches: a few cards from every category, plus ids no card has (custom cards
    // another machine may have) to show unknown ids pass through.
    private static readonly int[] AutoDisabled = { 1, 2, 3, 17, 30, 43, 55, 56, 57, 58, 59, 60, 82, 92, 1000, 4242 };
    private bool _poolCheck;
    private int _poolPicks;

    private void StartPoolCheck()
    {
        _poolCheck = true;
        _game.CardPickBegan += () =>
        {
            if (!_poolCheck) return;
            _poolPicks++;
            var offered = _game.PickOptions.Values.SelectMany(ids => ids)
                .Concat(_game.PickDeals.Values.SelectMany(d => new[] { d.Rare, d.Curse })).ToList();
            var bad = offered.Where(AutoDisabled.Contains).ToList();
            if (bad.Count > 0) { Finish(1, $"poolcheck: disabled cards offered: {string.Join(",", bad)}"); return; }
            if (_game.PickOptions.Values.Any(ids => ids.Distinct().Count() != ids.Count)) { Finish(1, "poolcheck: an offer repeats a card"); return; }
            Log.Info($"POOLCHECK pick {_poolPicks}: {offered.Count} cards offered, none disabled");
        };
    }

    /// <summary>The client's first lobby must carry the host's disabled list exactly.</summary>
    private void AutoJoinCheckPool()
    {
        if (_net.Lobby.Players.Count == 0) return;
        _net.LobbyChanged -= AutoJoinCheckPool;
        AddCouch();
        var got = _net.Lobby.Settings.DisabledCards;
        if (!got.SetEquals(AutoDisabled)) { Finish(1, $"poolcheck: client got disabled [{string.Join(",", got.OrderBy(id => id))}]"); return; }
        Log.Info($"POOLCHECK client has the host's disabled list: {string.Join(",", got.OrderBy(id => id))}");
    }

    // ---------- --leavecheck=WHEN: leaving a match puts the pointer back ----------

    private string _leaveWhen;
    private int _leaveStage;
    private long _leaveFrames;

    private void LeaveCheckStep()
    {
        _leaveFrames++;
        if (_leaveStage == 0)
        {
            bool now = _leaveWhen switch
            {
                "killcam" => _game.KillCam.Playing && _leaveFrames > 30,
                "pick" => _pick.Visible && _leaveFrames > 30,
                "over" => _over.Visible && _leaveFrames > 5, // test runs quit half a second into this screen
                _ => _game.Phase == Phase.Fighting && _crosshair.Active && _leaveFrames > 90,
            };
            if (!_game.KillCam.Playing && !_pick.Visible && !_over.Visible && _game.Phase != Phase.Fighting) _leaveFrames = 0;
            if (!now) return;
            Log.Info($"LEAVECHECK leaving at {_leaveWhen} ({_game.Phase}, crosshair {_crosshair.Active})");
            _dropping = true;
            _net.Leave();
            (_leaveStage, _leaveFrames) = (1, 0);
            return;
        }
        if (_leaveFrames < 20) return;
        if (_shotDir != null) SaveShot();
        string state = $"menu {_menu.Visible} hud {_hud.Visible} crosshair active {_crosshair.Active} visible {_crosshair.Visible} pointer {Input.MouseMode}";
        if (!_menu.Visible || _crosshair.Visible || Input.MouseMode != Input.MouseModeEnum.Visible) Finish(1, $"leavecheck {_leaveWhen}: {state}");
        else Finish(0, $"leavecheck {_leaveWhen}: back in the menu with the pointer ({state})");
    }

    // ---------- --overlaycheck: the Esc options overlay over a running match ----------

    private bool _overlayCheck, _ocSawPick;
    private int _ocStage;
    private long _ocFrames;
    private float _ocMoved;
    private readonly System.Collections.Generic.Dictionary<int, Vector2> _ocLast = new();
    private const int OcDone = 7;

    private static void PressEsc()
    {
        Input.ParseInputEvent(new InputEventKey { Keycode = Key.Escape, PhysicalKeycode = Key.Escape, Pressed = true });
        Input.ParseInputEvent(new InputEventKey { Keycode = Key.Escape, PhysicalKeycode = Key.Escape, Pressed = false });
    }

    /// <summary>What the local player would send right now with move-right and shoot held down.</summary>
    private InputFrame ProbeHeldInput()
    {
        Input.ActionPress("move_right");
        Input.ActionPress("shoot");
        var f = new LocalInput(_game).Next(null, 1f / 60);
        Input.ActionRelease("move_right");
        Input.ActionRelease("shoot");
        return f;
    }

    private static Button FindButton(Node n, string text)
    {
        foreach (var c in n.GetChildren())
            if (c is Button b && b.Text == text) return b;
            else if (FindButton(c, text) is { } found) return found;
        return null;
    }

    private static CardView FirstCard(Node n)
    {
        foreach (var c in n.GetChildren())
            if (c is CardView cv && cv.IsVisibleInTree()) return cv;
            else if (FirstCard(c) is { } found) return found;
        return null;
    }

    private void OverlayCheckStep()
    {
        void Fail(string why) { _overlayCheck = false; Finish(1, $"overlaycheck: {why}"); }
        bool overlayUp = _overlay && _options.Visible && LocalInput.Suppressed;
        _ocFrames++;
        switch (_ocStage)
        {
            case 0: // a battle well under way: press Esc
                if (_game.Phase != Phase.Fighting) { _ocFrames = 0; return; }
                if (_ocFrames < 60) return;
                var free = ProbeHeldInput();
                if (free.MoveX <= 0 || !free.Shoot) { Fail("held keys don't register even without the overlay"); return; }
                if (!_crosshair.Active) { Fail("no crosshair in a battle"); return; }
                PressEsc();
                (_ocStage, _ocFrames) = (1, 0);
                return;
            case 1: // the overlay is up and your held keys do nothing
                if (!_overlay) { if (_ocFrames > 10) Fail("Esc did not open the overlay"); return; }
                if (!overlayUp) { Fail("overlay flagged open but not shown, or input not suppressed"); return; }
                var muted = ProbeHeldInput();
                if (muted.MoveX != 0 || muted.Shoot) { Fail("held keys still move or shoot under the overlay"); return; }
                Log.Info("OVERLAYCHECK Esc opened the overlay mid-battle; held move+shoot give no input");
                (_ocStage, _ocFrames, _ocMoved) = (2, 0, 0);
                _ocLast.Clear();
                return;
            case 2: // three seconds under the overlay: nothing paused, the bots keep fighting
                if (GetTree().Paused || !Mathf.IsEqualApprox(Engine.TimeScale, 1)) { Fail("the game paused"); return; }
                if (!overlayUp) { Fail("overlay closed by itself during the battle"); return; }
                if (_ocFrames > 2 && _crosshair.Active) { Fail("the crosshair still replaces the pointer under the overlay"); return; }
                foreach (var a in _game.LiveAvatars())
                {
                    if (a.Player == _game.LocalMatchPlayer) continue;
                    if (_ocLast.TryGetValue(a.Player.Slot, out var p)) _ocMoved += a.GlobalPosition.DistanceTo(p);
                    _ocLast[a.Player.Slot] = a.GlobalPosition;
                }
                if (_ocFrames == 60 && _shotDir != null) SaveShot();
                if (_ocFrames < 180) return;
                if (_ocMoved < 100) { Fail($"bots moved only {_ocMoved:0}px in 3s under the overlay"); return; }
                Log.Info($"OVERLAYCHECK game kept running: bots moved {_ocMoved:0}px in 3s, now battle {_game.BattleNumber} {_game.Phase}");
                (_ocStage, _ocFrames, _ocSawPick) = (3, 0, false);
                return;
            case 3: // stays open through battle ends and new battles until a card pick comes up under it
                if (!overlayUp) { Fail($"overlay closed by itself ({_game.Phase})"); return; }
                if (_ocFrames > 60 * 180) { Fail("no card pick within 3 minutes"); return; }
                if (!_ocSawPick || _game.Phase != Phase.CardPick || FirstCard(_pick) is not { } card) return;
                // Hover a card: the overlay, not the card, must be what the mouse is over.
                var at = GetViewport().GetFinalTransform() * card.GetGlobalRect().GetCenter();
                Input.ParseInputEvent(new InputEventMouseMotion { Position = at, GlobalPosition = at });
                (_ocStage, _ocFrames) = (4, 0);
                return;
            case 4: // card pick under the overlay: both on screen, overlay on top and catching the mouse
                if (_ocFrames < 6) return;
                if (_game.Phase != Phase.CardPick) { (_ocStage, _ocFrames, _ocSawPick) = (3, 0, false); return; }
                if (!overlayUp || !_pick.Visible || !_hud.Visible) { Fail($"card pick screens wrong: options {_options.Visible} pick {_pick.Visible} hud {_hud.Visible}"); return; }
                var hovered = GetViewport().GuiGetHoveredControl();
                if (hovered == null || !(hovered == _options || _options.IsAncestorOf(hovered))) { Fail($"mouse over a card reaches {hovered?.GetPath().ToString() ?? "nothing"}, not the overlay"); return; }
                Log.Info($"OVERLAYCHECK card pick came up under the overlay; the mouse hits {hovered.Name}, not the cards");
                if (_shotDir != null) SaveShot();
                FindButton(_options, "LEAVE MATCH")?.EmitSignal(BaseButton.SignalName.Pressed);
                (_ocStage, _ocFrames) = (5, 0);
                return;
            case 5: // Leave asks first; No backs out without leaving
                if (_ocFrames < 6) return;
                if (FindButton(_options, "YES") is not { } yes || !yes.IsVisibleInTree() || FindButton(_options, "LEAVE MATCH").IsVisibleInTree())
                { Fail("Leave match did not ask for confirmation"); return; }
                if (_shotDir != null) SaveShot();
                FindButton(_options, "NO").EmitSignal(BaseButton.SignalName.Pressed);
                if (!FindButton(_options, "LEAVE MATCH").IsVisibleInTree() || _net.Mode == NetMode.None) { Fail("No did not back out of leaving"); return; }
                Log.Info("OVERLAYCHECK Leave match asks first; No backs out");
                PressEsc();
                (_ocStage, _ocFrames) = (6, 0);
                return;
            case 6: // Esc closed it; the card pick is still there and input is back
                if (_ocFrames < 6) return;
                if (_overlay || _options.Visible || LocalInput.Suppressed) { Fail("Esc did not close the overlay"); return; }
                if (!_hud.Visible || _pick.Visible != (_game.Phase == Phase.CardPick)) { Fail($"after closing: hud {_hud.Visible} pick {_pick.Visible} in {_game.Phase}"); return; }
                if (_crosshair.Active == _pick.Visible) { Fail($"crosshair {(_crosshair.Active ? "shown" : "hidden")} with the card pick {(_pick.Visible ? "up" : "down")}"); return; }
                Log.Info($"OVERLAYCHECK Esc closed it; {_game.Phase} screens intact, input restored");
                _ocStage = OcDone;
                return;
        }
    }

    // ---------- --hazardcheck: bots on an arena full of hazards ----------

    private const float HazardCheckSeconds = 300;
    // Limits set against the worst of seeds 1-8 (tools/check.sh runs seeds 1-3), with room to spare: pools 41-67 damage per
    // bot-minute, saws 8-19, 5-11 pit crossings. The pool limit was 40, inside that spread, so the check was a coin flip.
    private const float HzMaxPools = 75, HzMaxSaws = 30;
    private const int HzMinCrossings = 3;
    private float _hzBotSeconds, _hzMoverSeconds, _hzFight;
    private readonly float[] _hzDamage = new float[3];
    private readonly System.Collections.Generic.Dictionary<int, int> _hzSide = new();
    private int _hzCrossings, _hzFalls;

    // The pit in the middle: a moving platform ferries across it, under a wall hanging from the ceiling.
    private const int HzPit1 = 400, HzPit2 = 600;

    /// <summary>
    /// Bots only, on a hand-made arena split in two by a pit with a wall over it, which only a moving platform
    /// crosses (under the wall). Each half has a pool on its floor (magma on one, poison on the other), and a sawblade
    /// runs along one. Logs hazard damage per bot-minute, time spent on the mover and how often bots crossed the pit,
    /// and fails if bots walk into hazards or never cross.
    /// </summary>
    private void StartHazardCheck()
    {
        _autoMode = "hazardcheck";
        _game.AutopilotLocal = true;
        _game.DisableHitStop = true;
        _game.DisableKillCam = true;
        _game.LavaOverride = 0;
        int floor = MapGenerator.FloorY;
        var d = MapDesign.Empty(ThemeId.MagmaCore, 1);
        d.Pits.Add((HzPit1, HzPit2));
        d.Blocks.Add(new RectI(490, MapGenerator.Wall, 20, 470 - MapGenerator.Wall));
        d.Blocks.Add(new RectI(120, 475, 140, 12));
        d.Blocks.Add(new RectI(740, 475, 140, 12));
        d.Movers.Add(new Mover(HzPit1, HzPit2 - ArenaFeatures.MoverW, 500, ArenaFeatures.MoverW, ArenaFeatures.MoverH, 6, 0));
        d.Hazards.Add(new Hazard(HazardKind.Poison, 250, 340, floor));
        d.Hazards.Add(new Hazard(HazardKind.Magma, 660, 740, floor));
        d.Saws.Add(new SawTrack(780, floor - 10, 920, floor - 10, ArenaFeatures.SawRadius, MapDesign.SawPeriod(780, floor - 10, 920, floor - 10), 0));
        d.Spawns.Add(new SpawnPoint(40, floor));
        d.Spawns.Add(new SpawnPoint(960, floor));
        d.Spawns.Add(new SpawnPoint(180, 475));
        d.Spawns.Add(new SpawnPoint(820, 475));
        _game.HazardDamaged += (_, kind, dmg) => _hzDamage[(int)kind] += dmg;
        _game.BattleStarted += _hzSide.Clear; // being put on the other side by a new battle is no crossing
        _net.LocalName = "Hazard";
        _net.StartOffline();
        for (int i = 1; i < 4; i++) _net.AddBot();
        _net.SetSettings(new MatchSettings { BattlesPerRound = 50, RoundsToWin = 50, LoserChoices = 3, WinnerChoices = 0, KillCam = false, Maps = { MapCode.Pack(d) } });
        _game.HostStartMatch();
    }

    private void PollHazards()
    {
        if (_game.Phase != Phase.Fighting || _game.Map == null) return;
        const float dt = 1f / 60;
        _hzFight += dt;
        float clock = _game.Arena.Clock;
        foreach (var a in _game.LiveAvatars())
        {
            _hzBotSeconds += dt;
            var feet = a.GlobalPosition + new Vector2(0, a.HalfSize.Y);
            bool onFloor = a.IsOnFloor();
            foreach (var m in _game.Map.Movers)
            {
                float x = m.XAt(clock);
                if (onFloor && Mathf.Abs(feet.Y - m.Y) < 3 && feet.X > x - 2 && feet.X < x + m.W + 2) _hzMoverSeconds += dt;
            }
            int slot = a.Player.Slot;
            if (feet.Y > MapGenerator.FloorY + 40) { if (!_hzSide.TryGetValue(slot, out int fell) || fell != 2) _hzFalls++; _hzSide[slot] = 2; continue; }
            if (!onFloor || feet.X > HzPit1 && feet.X < HzPit2) continue;
            int side = feet.X < HzPit1 ? -1 : 1;
            if (_hzSide.TryGetValue(slot, out int was) && was == -side) _hzCrossings++;
            _hzSide[slot] = side;
        }
        if (_hzFight < HazardCheckSeconds) return;
        float minutes = _hzBotSeconds / 60;
        float pools = (_hzDamage[0] + _hzDamage[1]) / minutes, saws = _hzDamage[2] / minutes;
        string line = $"magma {_hzDamage[0] / minutes:0.0}, poison {_hzDamage[1] / minutes:0.0}, saw {saws:0.0} damage per bot-minute; "
            + $"{_hzMoverSeconds:0}s on the mover, crossed the pit {_hzCrossings} times, fell in {_hzFalls} times, in {_hzFight:0}s of fighting ({_game.BattleNumber} battles)";
        Log.Info($"HAZARDCHECK {line}");
        if (pools > HzMaxPools) Finish(1, $"hazardcheck: bots stand in pools ({line})");
        else if (saws > HzMaxSaws) Finish(1, $"hazardcheck: bots walk into sawblades ({line})");
        else if (_hzCrossings < HzMinCrossings) Finish(1, $"hazardcheck: bots don't ride the mover across ({line})");
        else Finish(0, line);
    }

    // ---------- --vantagecheck: a bot against someone on a ledge it can't reach ----------

    private const float VantageCheckSeconds = 60;
    private float _vcFight, _vcUnder, _vcDamage, _vcLastHp = -1;
    private bool _vcPlaced;

    // The ledge: far too high to jump to from the floor, with nothing else in the arena to climb.
    private static readonly RectI VcLedge = new(440, 304, 120, 16);

    /// <summary>
    /// One Normal bot on a bare floor against a player standing still on a ledge it has no way up to (kept alive, lava
    /// off). The bot can't reach the ledge, so it has to find a spot on the floor with a shot, straight or lobbed over
    /// the ledge's edge. Fails if it spends most of the time right under the ledge, out of sight (where bots used to
    /// wait for the lava), or hardly lands a hit.
    /// </summary>
    private void StartVantageCheck()
    {
        _autoMode = "vantagecheck";
        _game.DisableHitStop = true;
        _game.DisableKillCam = true;
        _game.LavaOverride = 0;
        var d = MapDesign.Empty(ThemeId.DungeonDepths, 1);
        d.Blocks.Add(VcLedge);
        d.Spawns.Add(new SpawnPoint(VcLedge.X + VcLedge.W / 2, VcLedge.Y));
        d.Spawns.Add(new SpawnPoint(VcLedge.X + VcLedge.W / 2 + 20, MapGenerator.FloorY));
        _net.LocalName = "Ledge";
        _net.StartOffline();
        _net.AddBot();
        _net.SetSettings(new MatchSettings { KillCam = false, Maps = { MapCode.Pack(d) } });
        _game.HostStartMatch();
    }

    private void PollVantage()
    {
        if (_game.LocalMatchPlayer is { } me && me.Input is not StillInput) me.Input = new StillInput();
        if (_game.Phase != Phase.Fighting) { _vcPlaced = false; return; }
        if (_game.LocalMatchPlayer?.Avatar is not { Alive: true } target) return;
        var bot = _game.LiveAvatars().FirstOrDefault(a => a != target);
        if (bot == null) return;
        if (!_vcPlaced)
        {
            target.GlobalPosition = new Vector2(VcLedge.X + VcLedge.W / 2f, VcLedge.Y - target.HalfSize.Y);
            target.Velocity = Vector2.Zero;
            _vcPlaced = true;
            _vcLastHp = target.Hp;
        }
        const float dt = 1f / 60;
        _vcFight += dt;
        var from = bot.GlobalPosition;
        if (from.X > VcLedge.X - 8 && from.X < VcLedge.Right + 8) _vcUnder += dt;
        if (target.Hp < _vcLastHp) _vcDamage += _vcLastHp - target.Hp;
        target.Hp = target.S[Stat.MaxHp]; // never let the battle end
        _vcLastHp = target.Hp;
        if (_vcFight < VantageCheckSeconds) return;
        float under = _vcUnder / _vcFight, dps = _vcDamage / _vcFight;
        string line = $"bot spent {under:P0} of {_vcFight:0}s under the ledge and dealt {dps:0.0} damage/s";
        Log.Info($"VANTAGECHECK {line}");
        // Seeds 1-8: 1-9% of the time under the ledge, 4.2-7.9 damage/s.
        if (under > 0.5f) Finish(1, $"vantagecheck: the bot hides under the ledge ({line})");
        else if (dps < 2) Finish(1, $"vantagecheck: the bot hardly hits ({line})");
        else Finish(0, line);
    }

    // ---------- --hangercheck: a bot, and someone standing under a hanging platform ----------

    // Seeds 1-8 drop the platform in 8.6-15.0 s, so 30 s leaves twice the slowest.
    private const float HangerCheckSeconds = 30;
    private float _hcFight;
    private bool _hcPlaced, _hcCrushed;

    private static readonly Hanger HcHanger = new(440, 280, 120, 8);

    /// <summary>
    /// One Normal bot on a floor against a player standing still under a hanging platform (kept alive, lava off). The
    /// quickest kill is to shoot the chains and drop the platform on them. Fails if the bot hasn't done so within
    /// <see cref="HangerCheckSeconds"/>.
    /// </summary>
    private void StartHangerCheck()
    {
        _autoMode = "hangercheck";
        _game.DisableHitStop = true;
        _game.DisableKillCam = true;
        _game.LavaOverride = 0;
        var d = MapDesign.Empty(ThemeId.DungeonDepths, 1);
        d.Blocks.Add(new RectI(400, 152, 200, 16)); // the chains hang from this
        d.Hangers.Add(HcHanger);
        d.Spawns.Add(new SpawnPoint(500, MapGenerator.FloorY));
        d.Spawns.Add(new SpawnPoint(120, MapGenerator.FloorY));
        _game.HazardDamaged += (_, kind, _) => _hcCrushed |= kind == GameManager.HazardHit.Crush;
        _net.LocalName = "Under";
        _net.StartOffline();
        _net.AddBot();
        _net.SetSettings(new MatchSettings { KillCam = false, Maps = { MapCode.Pack(d) } });
        _game.HostStartMatch();
    }

    private void PollHanger()
    {
        if (_game.LocalMatchPlayer is { } me && me.Input is not StillInput) me.Input = new StillInput();
        if (_game.Phase != Phase.Fighting) { _hcPlaced = false; return; }
        if (_game.LocalMatchPlayer?.Avatar is not { Alive: true } target) return;
        if (!_hcPlaced)
        {
            target.GlobalPosition = new Vector2(HcHanger.X + HcHanger.W / 2f, MapGenerator.FloorY - target.HalfSize.Y);
            target.Velocity = Vector2.Zero;
            _hcPlaced = true;
        }
        _hcFight += 1f / 60;
        target.Hp = target.S[Stat.MaxHp]; // never let the battle end
        bool dropped = _game.Arena.HangersDropped > 0;
        if (!dropped && _hcFight < HangerCheckSeconds) return;
        if (dropped && !_hcCrushed && _game.Arena.Hangers.Exists(h => h.FellAt >= 0 && !h.Landed && !h.Gone)) return; // still on its way down
        string line = $"platform {(dropped ? $"dropped after {_hcFight:0.0}s" : "never dropped")}, {(_hcCrushed ? "crushing" : "missing")} the player under it";
        Log.Info($"HANGERCHECK {line}");
        if (!dropped) Finish(1, $"hangercheck: the bot never shoots the chains ({line})");
        else if (!_hcCrushed) Finish(1, $"hangercheck: the bot dropped the platform but it missed ({line})");
        else Finish(0, line);
    }

    // ---------- --beltcheck: a player and a crate standing on conveyor belts ----------

    private const float BeltCheckSeconds = 2;
    private float _bcFight;
    private bool _bcPlaced, _bcMoved;
    private Vector2 _bcPlayerFrom, _bcCrateFrom;

    // One belt runs right under the player, the other left under a crate; the bot stands off both.
    private static readonly Conveyor BcPlayerBelt = new(200, MapGenerator.FloorY, 240, 1), BcCrateBelt = new(560, MapGenerator.FloorY, 240, -1);

    /// <summary>
    /// A player and a bot both standing still (lava off), the player on a belt running right and a crate on one running
    /// left. Fails unless after <see cref="BeltCheckSeconds"/> both have been carried about the belt's speed times that,
    /// the way their belt runs.
    /// </summary>
    private void StartBeltCheck()
    {
        _autoMode = "beltcheck";
        _game.DisableHitStop = true;
        _game.DisableKillCam = true;
        _game.LavaOverride = 0;
        var d = MapDesign.Empty(ThemeId.DungeonDepths, 1);
        d.Conveyors.Add(BcPlayerBelt);
        d.Conveyors.Add(BcCrateBelt);
        d.Props.Add(new PropSpawn(PropKind.Crate, BcCrateBelt.X + 180, MapGenerator.FloorY - 16, 16, 16));
        d.Spawns.Add(new SpawnPoint(BcPlayerBelt.X + 20, MapGenerator.FloorY));
        d.Spawns.Add(new SpawnPoint(940, MapGenerator.FloorY));
        _net.LocalName = "Rider";
        _net.StartOffline();
        _net.AddBot();
        _net.SetSettings(new MatchSettings { KillCam = false, Maps = { MapCode.Pack(d) } });
        _game.HostStartMatch();
    }

    private void PollBelts()
    {
        foreach (var p in _game.LiveAvatars().Select(a => a.Player))
            if (p.Input is not StillInput) p.Input = new StillInput();
        if (_game.Phase != Phase.Fighting) { _bcPlaced = _bcMoved = false; return; }
        if (_game.LocalMatchPlayer?.Avatar is not { Alive: true } rider || _game.Arena.Props.FirstOrDefault() is not { } crate) return;
        if (!_bcPlaced)
        {
            if (!_bcMoved)
            {
                // Either spawn can be the player's, so put the player on their belt and the bot off both.
                foreach (var a in _game.LiveAvatars())
                {
                    a.GlobalPosition = new Vector2(a == rider ? BcPlayerBelt.X + 20 : 940, MapGenerator.FloorY - a.HalfSize.Y);
                    a.Velocity = Vector2.Zero;
                }
                _bcMoved = true;
                return;
            }
            if (!rider.IsOnFloor() || !crate.IsOnFloor()) return; // let both settle first
            _bcPlayerFrom = rider.GlobalPosition;
            _bcCrateFrom = crate.GlobalPosition;
            _bcPlaced = true;
        }
        _bcFight += 1f / 60;
        if (_bcFight < BeltCheckSeconds) return;
        float want = Conveyor.Speed * BeltCheckSeconds, rode = rider.GlobalPosition.X - _bcPlayerFrom.X, pushed = crate.GlobalPosition.X - _bcCrateFrom.X;
        string line = $"in {_bcFight:0.0}s the player rode {rode:0} px and the crate {pushed:0} px (belts run {want:0} px)";
        Log.Info($"BELTCHECK {line}");
        if (_shotDir != null) SaveShot("beltcheck");
        if (Mathf.Abs(rode - want) > want * 0.15f) Finish(1, $"beltcheck: the belt didn't carry the player ({line})");
        else if (Mathf.Abs(pushed + want) > want * 0.15f) Finish(1, $"beltcheck: the belt didn't carry the crate ({line})");
        else Finish(0, line);
    }

    // ---------- --driftcheck: bots on Drift arenas ----------

    private const float DriftCheckSeconds = 300;
    private float _dcFight, _dcBotSeconds;
    private int _dcFalls, _dcKnocked;
    private readonly System.Collections.Generic.HashSet<int> _dcOut = new();

    /// <summary>
    /// Four bots on generated Drift arenas (every platform drifts, over a bottomless drop), lava off. Counts the falls
    /// nobody caused (no hit in the last 2 s) per bot-minute, beside the knock-offs, and fails if bots walk or jump off
    /// on their own too often.
    /// </summary>
    private void StartDriftCheck()
    {
        _autoMode = "driftcheck";
        _game.AutopilotLocal = true;
        _game.DisableHitStop = true;
        _game.DisableKillCam = true;
        _game.LavaOverride = 0;
        var settings = new MatchSettings { BattlesPerRound = 50, RoundsToWin = 50, LoserChoices = 3, WinnerChoices = 0, KillCam = false };
        var themes = new[] { ThemeId.CandyClouds, ThemeId.SunsetPeaks, ThemeId.NeonNight, ThemeId.FrostBite };
        for (int i = 0; i < themes.Length; i++) settings.Maps.Add(MapCode.Pack(MapDesign.Generate(MapStyle.Drift, themes[i], 1 + i)));
        _game.BattleStarted += _dcOut.Clear;
        _net.LocalName = "Drift";
        _net.StartOffline();
        for (int i = 1; i < 4; i++) _net.AddBot();
        _net.SetSettings(settings);
        _game.HostStartMatch();
    }

    private void PollDrift()
    {
        if (_game.Phase != Phase.Fighting || _game.Map == null) return;
        const float dt = 1f / 60;
        _dcFight += dt;
        foreach (var a in _game.LiveAvatars())
        {
            int slot = a.Player.Slot;
            _dcBotSeconds += dt;
            if (a.GlobalPosition.Y < MapGenerator.FloorY + 40 || !_dcOut.Add(slot)) continue;
            if (a.LastHitBy >= 0 && _game.Clock - a.LastHitAt < 2) _dcKnocked++;
            else _dcFalls++;
        }
        if (_dcFight < DriftCheckSeconds) return;
        float perMin = _dcFalls / (_dcBotSeconds / 60);
        string line = $"{_dcFalls} falls on their own ({perMin:0.00} per bot-minute) and {_dcKnocked} knocked off, in {_dcFight:0}s of fighting ({_game.BattleNumber} battles)";
        Log.Info($"DRIFTCHECK {line}");
        if (perMin > DriftCheckMaxFalls) Finish(1, $"driftcheck: bots fall off drifting platforms on their own ({line})");
        else Finish(0, line);
    }

    // Before the fixes bots fell on their own about 13 times a bot-minute; after, 3.5-4.5. Jumps between drifting
    // platforms still miss now and then (one is shoved off mid-flight): this catches a slide back, not that.
    // Seeds 1-8: 2.9-4.7 falls on their own per bot-minute.
    private const float DriftCheckMaxFalls = 6f;

    /// <summary>Test input: stand still, aiming nowhere in particular.</summary>
    private sealed class StillInput : IInputSource
    {
        public InputFrame Next(Avatar self, float dt) => new() { Aim = self.GlobalPosition + Vector2.Right * 40 };
    }

    private void Finish(int code, string why)
    {
        _autoMode = null;
        ErrorWatch.Finish(GetTree(), code, why);
    }

    // ---------- --errorcheck: the error count the checks fail on sees an exception Godot caught ----------

    private void ErrorCheck()
    {
        _autoMode = "errorcheck";
        // Thrown from a frame callback, as a bug in a card effect would be: Godot logs it and carries on.
        Callable.From(() => throw new System.InvalidOperationException("errorcheck: a deliberate exception")).CallDeferred();
        GetTree().CreateTimer(0.5).Timeout += () =>
        {
            if (ErrorWatch.Count == 0) { Finish(1, "errorcheck: an exception in a callback went uncounted"); return; }
            string seen = ErrorWatch.First;
            ErrorWatch.Reset();
            Finish(0, $"errorcheck: counted it ({seen})");
        };
    }

    // ---------- --lobbycheck: every lobby layout fits on screen ----------

    /// <summary>
    /// The main menu, then a full lobby (host, a couch guest and six bots) in every game mode and team size, then the
    /// options on every tab, all at every UI scale: fails if any button or label ends up off screen. With --shots=DIR,
    /// saves each layout.
    /// </summary>
    // ---------- --musiccheck: M skips to the next song, but not while typing ----------

    private async void MusicCheck()
    {
        var tree = GetTree();
        async System.Threading.Tasks.Task Wait(double s) => await ToSignal(tree.CreateTimer(s), SceneTreeTimer.SignalName.Timeout);
        void PressM()
        {
            foreach (bool down in new[] { true, false })
                Input.ParseInputEvent(new InputEventKey { Keycode = Key.M, PhysicalKeycode = Key.M, Unicode = 'm', Pressed = down });
        }
        for (int i = 0; i < 300 && _music.Track == null; i++) await Wait(0.1);
        string first = _music.Track;
        if (first == null) { Finish(1, "musiccheck: no music started"); return; }
        // Typing an M into the name box goes into the box.
        _menu.NameEdit.GrabFocus();
        PressM();
        await Wait(1.5);
        if (_music.Track != first) { Finish(1, $"musiccheck: typing M in a text box skipped the song ({first} -> {_music.Track})"); return; }
        _menu.NameEdit.ReleaseFocus();
        await Wait(8); // the second song of the kind has rendered by now
        PressM();
        await Wait(1.5);
        if (_music.Track == first) { Finish(1, $"musiccheck: M did not skip {first}"); return; }
        Finish(0, $"musiccheck: M skipped {first} -> {_music.Track}; typing M in the name box didn't");
    }

    private async void LobbyCheck()
    {
        var tree = GetTree();
        async System.Threading.Tasks.Task Wait(double s) => await ToSignal(tree.CreateTimer(s), SceneTreeTimer.SignalName.Timeout);
        float userScale = Accessibility.UiScale;
        string tag = "";
        void Scale(float s)
        {
            Accessibility.PreviewUiScale(s);
            tag = s == 1 ? "" : $"_{s * 100:0}";
        }
        // Anything visible under root that ends up off screen, by its text; saves the layout as lobby_NAME with --shots.
        bool Fits(Control root, string name)
        {
            name += tag;
            var screen = GetViewport().GetVisibleRect().Grow(0.5f);
            // A row scrolled out of view inside a scroll box is fine as long as the box itself is on screen.
            static bool Scrolled(Control c)
            {
                for (var p = c.GetParent(); p != null; p = p.GetParent()) if (p is ScrollContainer) return true;
                return false;
            }
            var off = FindAll<Control>(root, c => c.IsVisibleInTree() && (c is ScrollContainer || c is BaseButton or Label && !Scrolled(c))
                    && !screen.Encloses(c.GetGlobalTransformWithCanvas() * new Rect2(Vector2.Zero, c.Size)))
                .Select(c => c switch { Button b => b.Text, Label l => l.Text, _ => $"{c.Name} tab" }).ToList();
            if (_shotDir != null) SaveShot($"lobby_{name}");
            if (off.Count > 0)
            {
                Accessibility.PreviewUiScale(userScale);
                Finish(1, $"lobbycheck: {name}: off screen: {string.Join(", ", off.Take(6))}");
            }
            return off.Count == 0;
        }
        int layouts = 0;
        foreach (float s in Accessibility.UiScales)
        {
            Scale(s);
            await Wait(0.4);
            if (!Fits(_menu, "menu")) return;
            layouts++;
        }
        Press(_menu, "PLAY VS BOTS");
        await Wait(0.5);
        _net.AddGuest(100);
        while (_net.Lobby.Players.Count < LobbyState.MaxPlayers) _net.AddBot();
        foreach (float s in Accessibility.UiScales)
        {
            Scale(s);
            foreach (var mode in GameModes.All)
                foreach (int size in new[] { 0, 2, 3, 4 })
                {
                    _net.Lobby.Settings.Mode = mode;
                    _net.SetTeamSize(size);
                    await Wait(0.15);
                    if (!Fits(_lobby, $"{GameModes.Id(mode)}_{(size == 0 ? "ffa" : $"{size}v{size}")}")) return;
                    layouts++;
                }
        }
        // The lobby's options, then the in-match overlay version (with Leave match), on every tab.
        OpenOptions(_lobby);
        var tabs = FindAll<TabContainer>(_options, _ => true).First();
        KillCamClips.KeepBlankForCheck(10); // the overlay then shows the kill cam row, and the arena row too:
        string arena = MapCode.Encode(MapDesign.Generate(MapStyle.Tiers, ThemeId.SunsetPeaks, 7));
        foreach (float s in Accessibility.UiScales)
        {
            Scale(s);
            foreach (bool overlay in new[] { false, true })
            {
                _options.SetOverlay(overlay);
                if (overlay) _options.SetArena(arena);
                for (int t = 0; t < tabs.GetTabCount(); t++)
                {
                    tabs.CurrentTab = t;
                    await Wait(0.15);
                    if (!Fits(_options, $"options_{(overlay ? "overlay_" : "")}{tabs.GetTabTitle(t).ToLowerInvariant()}")) return;
                    layouts++;
                }
            }
        }
        Accessibility.PreviewUiScale(userScale);
        Finish(0, $"lobbycheck: all {layouts} menu, lobby and options layouts fit on screen at every UI scale");
    }

    // ---------- --clipcheck: a kill cam saves as a GIF ----------

    /// <summary>Image descriptors in a GIF, walking its blocks (a byte search also matches inside the image data).</summary>
    private static int CountGifFrames(byte[] b)
    {
        int i = 13, frames = 0;
        if ((b[10] & 0x80) != 0) i += 3 << ((b[10] & 7) + 1); // global colour table
        void SkipSubBlocks() { while (i < b.Length && b[i] != 0) i += b[i] + 1; i++; }
        while (i < b.Length && b[i] != 0x3B)
        {
            if (b[i] == 0x21) { i += 2; SkipSubBlocks(); }
            else if (b[i] == 0x2C)
            {
                frames++;
                byte flags = b[i + 9];
                i += 10;
                if ((flags & 0x80) != 0) i += 3 << ((flags & 7) + 1); // local colour table
                i++; // LZW minimum code size
                SkipSubBlocks();
            }
            else return -1;
        }
        return frames;
    }

    /// <summary>
    /// An offline match on autopilot until a kill cam has been kept, then saves it from the Esc overlay's button and
    /// checks the GIF on disk (header, size, frame count). Needs a window (headless draws nothing to grab).
    /// </summary>
    private async void ClipCheck()
    {
        var tree = GetTree();
        async System.Threading.Tasks.Task Wait(double s) => await ToSignal(tree.CreateTimer(s, true, false, true), SceneTreeTimer.SignalName.Timeout);
        _game.AutopilotLocal = true;
        _net.StartOffline();
        AddTestBots();
        _net.SetSettings(new MatchSettings { BattlesPerRound = 3, RoundsToWin = 9 });
        _game.HostStartMatch();
        for (int i = 0; i < 240 && !KillCamClips.HasClip; i++) await Wait(0.5);
        if (!KillCamClips.HasClip) { Finish(1, "clipcheck: no kill cam was kept in 2 minutes"); return; }
        OpenOverlay();
        await Wait(0.2);
        var button = FindAll<Button>(_options, b => b.IsVisibleInTree() && b.Text == "SAVE KILL CAM GIF").FirstOrDefault();
        if (button == null) { Finish(1, "clipcheck: the Esc overlay has no SAVE KILL CAM GIF button"); return; }
        button.EmitSignal(BaseButton.SignalName.Pressed);
        for (int i = 0; i < 120 && (KillCamClips.Saving || !KillCamClips.Saved); i++) await Wait(0.25);
        string path = KillCamClips.LastSaved;
        if (!KillCamClips.Saved || path == null || !System.IO.File.Exists(path)) { Finish(1, "clipcheck: the GIF wasn't written"); return; }
        var bytes = System.IO.File.ReadAllBytes(path);
        int w = bytes[6] | bytes[7] << 8, h = bytes[8] | bytes[9] << 8;
        int frames = CountGifFrames(bytes);
        string line = $"{System.IO.Path.GetFileName(path)}: {w}x{h}, {frames} frames, {bytes.Length / 1024} KB; button now reads {button.Text}";
        if (_shotDir != null) System.IO.File.Copy(path, $"{_shotDir}/killcam.gif", true);
        System.IO.File.Delete(path);
        if (System.Text.Encoding.ASCII.GetString(bytes, 0, 6) != "GIF89a" || w != KillCamClips.W || h != KillCamClips.H || frames != KillCamClips.FrameCount)
            Finish(1, $"clipcheck: bad GIF ({line})");
        else if (button.Text != "OPEN CLIPS FOLDER") Finish(1, $"clipcheck: the button didn't change after saving ({line})");
        else Finish(0, $"clipcheck: {line}");
    }
}
