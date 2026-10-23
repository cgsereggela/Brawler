using System.Linq;
using Brawler.Core;
using Brawler.Fx;
using Brawler.Game;
using Brawler.Logic;
using Brawler.Net;
using Brawler.UI;
using Godot;

namespace Brawler;

/// <summary>The screenshot, clip and benchmark modes (--themebench, --readmeshots, --menushots, ...).</summary>
public partial class Main
{
    // --shots=<dir>: save a screenshot every 4 simulated seconds during an autotest (needs a window, not --headless).
    private string _shotDir;
    private int _shotCount;

    private Control _gallery;

    /// <summary>--menushots: every option of one look part side by side, for checking the art.</summary>
    private void ShowGallery(LookPart part)
    {
        Show();
        _gallery?.QueueFree();
        _gallery = new PanelContainer();
        _gallery.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        _menu.GetParent().AddChild(_gallery);
        var v = UiKit.VBox(4);
        _gallery.AddChild(v);
        v.AddChild(UiKit.Label(Appearance.PartLabel(part), 22, Palette.Accent, font: Brawler.Fx.Fonts.Heavy));
        var grid = new GridContainer { Columns = 6 };
        v.AddChild(grid);
        for (int o = 0; o < Appearance.Count(part); o++)
        {
            var look = Appearance.Default();
            look[LookPart.Hat] = 0;
            if (part is LookPart.HairColor) look[LookPart.Hair] = 7;
            if (part is LookPart.HatColor) look[LookPart.Hat] = 1;
            look[part] = o;
            var cell = UiKit.VBox(0);
            cell.AddChild(new AvatarPreview(look, o % Palette.Players.Length, 2.6f) { ShowGun = false });
            var tmp = new Appearance();
            tmp[part] = o;
            cell.AddChild(UiKit.Label(tmp.OptionName(part), UiKit.Small, Palette.Text, HorizontalAlignment.Center));
            grid.AddChild(cell);
        }
    }

    private void SaveShot()
    {
        var img = GetViewport().GetTexture().GetImage();
        string path = $"{_shotDir}/shot_{_shotCount++:D2}_{_game.Phase}.png";
        img.SavePng(path);
    }

    /// <summary>--focuslog: where the action is across the screen (0–1): the kill during a kill cam, else your character
    /// pulled toward the middle of everyone still standing, or the middle of the screen when nobody is.</summary>
    private float FocusX()
    {
        if (_game.Phase is not (Phase.Countdown or Phase.Fighting or Phase.Intermission)) return 0.5f;
        Vector2? at = null;
        if (_game.KillCam.Playing) at = _game.KillCam.Framing().Focus;
        else
        {
            var alive = _game.Active().Select(p => p.Avatar).Where(v => v != null && IsInstanceValid(v) && v.Alive).ToList();
            if (alive.Count > 0)
            {
                var mid = alive.Aggregate(Vector2.Zero, (s, v) => s + v.GlobalPosition) / alive.Count;
                at = _game.LocalMatchPlayer?.Avatar is { Alive: true } me && IsInstanceValid(me) ? me.GlobalPosition.Lerp(mid, 0.35f) : mid;
            }
        }
        if (at is not { } p) return 0.5f;
        var vp = GetViewport();
        return Mathf.Clamp((vp.GetCanvasTransform() * p).X / vp.GetVisibleRect().Size.X, 0, 1);
    }

    private void SaveShot(string name) =>GetViewport().GetTexture().GetImage().SavePng($"{_shotDir}/{name}.png");

    // --readmeshots clips: every 3rd rendered frame (20 fps under --fixed-fps 60), scaled to 640 wide.
    private const int ClipW = 640, ClipH = 360, ClipEvery = 3;

    /// <summary>Records <paramref name="seconds"/> of the screen into DIR/name.gif.</summary>
    private void RecordClip(string name, double seconds)
    {
        var frames = new System.Collections.Generic.List<byte[]>();
        int frame = 0, total = (int)(seconds * 60);
        void Grab()
        {
            if (frame++ % ClipEvery == 0)
            {
                var img = GetViewport().GetTexture().GetImage();
                img.Convert(Image.Format.Rgb8);
                img.Resize(ClipW, ClipH, Image.Interpolation.Lanczos);
                frames.Add(img.GetData());
            }
            if (frame < total) return;
            RenderingServer.FramePostDraw -= Grab;
            System.IO.File.WriteAllBytes($"{_shotDir}/{name}.gif", Gif.Encode(frames, ClipW, ClipH, ClipEvery * 100 / 60));
            Log.Info($"README clip {name}: {frames.Count} frames");
        }
        RenderingServer.FramePostDraw += Grab;
    }

    // --themebench: Average frame time for each arena theme (windowed, vsync off), with bots fighting. Prints and quits.
    // Each theme gets a match of its own whose only arena is the theme's (the first seed that rolls it), and only frames
    // in the middle of a fight count: swapping the arena's look under one long match timed kill cams, intermissions and
    // whatever arena the next battle rolled whenever a battle ended mid-measure.
    private void ThemeBench()
    {
        _autoMode = "bench";
        _game.AutopilotLocal = true;
        _game.DisableHitStop = true;
        _game.DisableKillCam = true;
        DisplayServer.WindowSetVsyncMode(DisplayServer.VSyncMode.Disabled);
        _net.StartOffline();
        AddTestBots();
        var tree = GetTree();
        int theme = 0;
        void Next()
        {
            if (_net.InMatch) _game.HostReturnToLobby();
            // Quit from the lobby, a moment after the match is torn down.
            if (theme >= ArenaTheme.All.Length) { tree.CreateTimer(0.5, true, false, true).Timeout += () => tree.Quit(0); return; }
            int seed = 1;
            while (ArenaTheme.ForSeed(seed) != ArenaTheme.All[theme]) seed++;
            var arena = MapCode.Pack(MapDesign.From(MapGenerator.Generate(seed, 4)));
            var name = ArenaTheme.All[theme].Name;
            theme++;
            tree.CreateTimer(0.3, true, false, true).Timeout += () =>
            {
                _net.SetSettings(new MatchSettings { BattlesPerRound = 9, RoundsToWin = 9, KillCam = false, Maps = { arena } });
                _game.HostStartMatch();
                BenchFights(name, BenchSeconds, Next);
            };
        }
        tree.CreateTimer(1.0, true, false, true).Timeout += Next;
    }

    private const double BenchSeconds = 3.0;

    /// <summary>
    /// Times <paramref name="seconds"/> of frames spent fighting (after half a second of each fight to settle in), logs the
    /// average as a BENCH line for <paramref name="name"/>, then calls <paramref name="done"/>.
    /// </summary>
    private void BenchFights(string name, double seconds, System.Action done)
    {
        var tree = GetTree();
        double settled = 0, total = 0;
        long frames = 0;
        ulong last = 0;
        bool wasFighting = false;
        void Frame()
        {
            ulong now = Time.GetTicksUsec();
            bool fighting = _game.Phase == Phase.Fighting;
            double ms = (now - last) / 1000.0;
            if (fighting && !wasFighting) settled = 0; // every fight settles in afresh
            if (last != 0 && fighting && wasFighting)
            {
                if (settled < 500) settled += ms;
                else { total += ms; frames++; }
            }
            last = now;
            wasFighting = fighting;
            if (total < seconds * 1000) return;
            tree.ProcessFrame -= Frame;
            double avg = total / frames;
            Log.Info($"BENCH {name,-13} {avg:0.00} ms/frame ({1000 / avg:0} fps)");
            done();
        }
        tree.ProcessFrame += Frame;
    }

    // --fxbench: Floods the arena with top-tier hit and kill effects for 8 s (windowed, vsync off), printing frame
    // time and live particles each second, then a FXBENCH summary. Compare with --nofxcull.
    private void FxBench()
    {
        _autoMode = "bench";
        _game.AutopilotLocal = true;
        _game.DisableHitStop = true;
        DisplayServer.WindowSetVsyncMode(DisplayServer.VSyncMode.Disabled);
        _net.StartOffline();
        _net.AddBot();
        _net.AddBot();
        _net.SetSettings(new MatchSettings { BattlesPerRound = 9, RoundsToWin = 9 });
        _game.HostStartMatch();
        var tree = GetTree();
        var all = Game.Flavor.Fire | Game.Flavor.Homing | Game.Flavor.Vampire | Game.Flavor.Glass | Game.Flavor.Feather | Game.Flavor.Bouncy | Game.Flavor.Split | Game.Flavor.Pierce;
        double total = 0, worst = 0;
        int seconds = 0, peak = 0;
        void Burst()
        {
            if (seconds >= 8) return;
            _game.DebugShowcase(12, all);
            tree.CreateTimer(0.04, true, false, true).Timeout += Burst;
        }
        void Second()
        {
            ulong t0 = Time.GetTicksUsec(), f0 = Engine.GetProcessFrames();
            tree.CreateTimer(1.0, true, false, true).Timeout += () =>
            {
                double ms = (Time.GetTicksUsec() - t0) / 1000.0 / Mathf.Max(1, Engine.GetProcessFrames() - f0);
                int live = _game.Fx.ParticleCount;
                peak = Mathf.Max(peak, live);
                total += ms;
                worst = Mathf.Max(worst, ms);
                seconds++;
                Log.Info($"fxbench {seconds}s: {ms:0.00} ms/frame, {live} particles, cap {_game.Fx.Budget.Cap} (baseline {_game.Fx.Budget.Baseline * 1000:0.0} ms)");
                if (seconds < 8) { Second(); return; }
                Log.Info($"FXBENCH culling {(Fx.FxLayer.Culling ? "on" : "off")}: average {total / seconds:0.00} ms/frame, worst second {worst:0.00} ms, peak {peak} particles");
                tree.Quit(0);
            };
        }
        tree.CreateTimer(2.6).Timeout += () => { Burst(); Second(); };
    }

    // --fxshots: Screenshot hit and kill effects at every power tier (0 to 12 cards, flavors piling up), then quit.
    private void FxShots()
    {
        _autoMode = "fx";
        _game.AutopilotLocal = true;
        _game.DisableHitStop = true;
        _net.StartOffline();
        _net.AddBot();
        _net.AddBot();
        _net.SetSettings(new MatchSettings { BattlesPerRound = 5, RoundsToWin = 5 });
        _game.HostStartMatch();
        var tree = GetTree();
        var tiers = new (int Power, Game.Flavor Flavors)[]
        {
            (0, Game.Flavor.None), (2, Game.Flavor.Bouncy), (4, Game.Flavor.Fire | Game.Flavor.Homing),
            (6, Game.Flavor.Fire | Game.Flavor.Vampire | Game.Flavor.Pierce), (9, Game.Flavor.Glass | Game.Flavor.Split | Game.Flavor.Homing),
            (12, Game.Flavor.Fire | Game.Flavor.Homing | Game.Flavor.Vampire | Game.Flavor.Glass | Game.Flavor.Feather | Game.Flavor.Bouncy),
        };
        int k = 0;
        void Step()
        {
            if (k >= tiers.Length) { tree.Quit(0); return; }
            _game.DebugShowcase(tiers[k].Power, tiers[k].Flavors);
            k++;
            tree.CreateTimer(0.14, true, false, true).Timeout += () =>
            {
                SaveShot();
                tree.CreateTimer(0.25, true, false, true).Timeout += SaveShot;
                tree.CreateTimer(1.4, true, false, true).Timeout += Step;
            };
        }
        tree.CreateTimer(2.6).Timeout += Step;
    }

    // --arenashots: Screenshot battles on seeds rich in arena features (pools, saws, movers, glass, barrels), then quit.
    private void ArenaShots()
    {
        _autoMode = "arena";
        _game.AutopilotLocal = true;
        _game.DisableHitStop = true;
        _net.StartOffline();
        AddTestBots();
        _net.SetSettings(new MatchSettings { BattlesPerRound = 9, RoundsToWin = 9 });
        _game.HostStartMatch();
        var seeds = Enumerable.Range(1, 400)
            .Select(s => (Seed: s, M: MapGenerator.Generate(s, 4)))
            .OrderByDescending(x => x.M.Hazards.Count * 2 + x.M.Saws.Count * 2 + x.M.Movers.Count * 2
                + (x.M.Props.Any(p => p.Kind == PropKind.Glass) ? 2 : 0) + x.M.Props.Count(p => p.Kind == PropKind.Barrel))
            .Take(5).Select(x => x.Seed).ToList();
        var tree = GetTree();
        int k = 0;
        void Next()
        {
            if (k >= seeds.Count) { tree.Quit(0); return; }
            _game.DebugStartBattle(seeds[k++]);
            tree.CreateTimer(3.5, true, false, true).Timeout += () => { SaveShot(); Next(); };
        }
        tree.CreateTimer(1.0, true, false, true).Timeout += Next;
    }

    // --layoutshots: Screenshot battles on the first two seeds of each map style (MAPSHOT lines name them), then quit.
    private void LayoutShots()
    {
        _autoMode = "arena";
        _game.AutopilotLocal = true;
        _game.DisableHitStop = true;
        _net.StartOffline();
        AddTestBots();
        _net.SetSettings(new MatchSettings { BattlesPerRound = 99, RoundsToWin = 9 });
        _game.HostStartMatch();
        var seeds = System.Enum.GetValues<MapStyle>()
            .SelectMany(st => Enumerable.Range(1, 400).Where(s => MapGenerator.StyleFor(s) == st).Take(2)).ToList();
        var tree = GetTree();
        int k = 0;
        void Next()
        {
            if (k >= seeds.Count) { tree.Quit(0); return; }
            string name = $"layout_{k:D2}_{MapGenerator.StyleFor(seeds[k])}";
            Log.Info($"MAPSHOT {name} seed {seeds[k]} {ArenaTheme.ForSeed(seeds[k]).Name}");
            _game.DebugStartBattle(seeds[k++]);
            tree.CreateTimer(3.5, true, false, true).Timeout += () => { SaveShot(name); Next(); };
        }
        tree.CreateTimer(1.0, true, false, true).Timeout += Next;
    }

    // --hudshots: A battle with 4 players (or --players=) holding cards (use --cards=), opening each player chip's hover card in turn.
    private void HudShots()
    {
        _autoMode = "arena";
        _game.AutopilotLocal = true;
        _game.DisableHitStop = true;
        _net.StartOffline();
        AddTestBots();
        _net.SetSettings(new MatchSettings { BattlesPerRound = 9, RoundsToWin = 9 });
        _game.HostStartMatch();
        var tree = GetTree();
        int k = 0;
        void Next()
        {
            if (k >= _net.Lobby.Players.Count) { tree.Quit(0); return; }
            _hud.DebugHover = k++;
            tree.CreateTimer(0.3, true, false, true).Timeout += () => { SaveShot(); Next(); };
        }
        tree.CreateTimer(3.0, true, false, true).Timeout += Next;
    }

    // --terrainshots: Screenshot a battle on the hilliest seed of each arena theme (one per map style first), then quit.
    private void TerrainShots()
    {
        _autoMode = "arena";
        _game.AutopilotLocal = true;
        _game.DisableHitStop = true;
        _net.StartOffline();
        AddTestBots();
        _net.SetSettings(new MatchSettings { BattlesPerRound = 9, RoundsToWin = 9 });
        _game.HostStartMatch();
        var maps = Enumerable.Range(1, 600).Select(s => (Seed: s, M: MapGenerator.Generate(s, 4))).Where(x => x.M.Slopes.Count > 0).ToList();
        var seeds = ArenaTheme.All.Select(th => maps.Where(x => ArenaTheme.ForSeed(x.Seed) == th)
                .OrderByDescending(x => x.M.Slopes.Count + x.M.Terrain.Count).Select(x => x.Seed).FirstOrDefault())
            .Where(s => s > 0).ToList();
        foreach (MapStyle st in System.Enum.GetValues<MapStyle>())
            if (maps.FirstOrDefault(x => x.M.Style == st).Seed is int s and > 0 && !seeds.Contains(s)) seeds.Insert(0, s);
        var tree = GetTree();
        int k = 0;
        void Next()
        {
            if (k >= seeds.Count) { tree.Quit(0); return; }
            var m = MapGenerator.Generate(seeds[k], 4);
            Log.Info($"TERRAINSHOT seed {seeds[k]} {m.Style} {ArenaTheme.ForSeed(seeds[k]).Name}: {m.Slopes.Count} slopes, {m.Terrain.Count} raised");
            _game.DebugStartBattle(seeds[k++]);
            tree.CreateTimer(3.5, true, false, true).Timeout += () => { SaveShot(); Next(); };
        }
        tree.CreateTimer(1.0, true, false, true).Timeout += Next;
    }

    // --readmeshots: README media: a 4-player match with generated names, played out naturally. Each battle gets a GIF clip
    // and a screenshot (plus a hover card once cards are held), each card pick a clip and a screenshot, and
    // the match-over screen a screenshot. Run with --fixed-fps 60 so clips play at game speed.
    private void ReadmeShots()
    {
        _autoMode = "readme";
        _game.AutopilotLocal = true;
        _pick.ShowAutopilotPick = true;
        var rng = new Rng((ulong)System.DateTime.Now.Ticks);
        var names = FunNames.Pick(rng, _playersArg ? _simMax : 4);
        _net.LocalName = names[0];
        _net.LocalLook = Appearance.Random(rng);
        _net.StartOffline();
        for (int i = 1; i < names.Count; i++) _net.AddBot();
        for (int i = 1; i < names.Count; i++) _net.Lobby.Players[i].Name = names[i];
        _net.SetSettings(new MatchSettings { BattlesPerRound = 1, RoundsToWin = 4, LoserChoices = 5, WinnerChoices = 3 });
        var tree = GetTree();
        int battle = 0, pick = 0;
        _game.BattleStarted += () =>
        {
            int n = ++battle;
            tree.CreateTimer(0.4, true, false, true).Timeout += () => RecordClip($"battle_{n:D2}", 6.0);
            tree.CreateTimer(4.0, true, false, true).Timeout += () => { if (_game.Phase == Phase.Fighting) SaveShot($"battle_{n:D2}"); };
            if (n < 3) return;
            tree.CreateTimer(6.6, true, false, true).Timeout += () =>
            {
                if (_game.Phase != Phase.Fighting) return;
                _hud.DebugHover = 0;
                tree.CreateTimer(0.2, true, false, true).Timeout += () => { SaveShot($"hover_{n:D2}"); _hud.DebugHover = -1; };
            };
        };
        _game.CardPickBegan += () =>
        {
            int n = ++pick;
            RecordClip($"pick_{n:D2}", 2.5);
            tree.CreateTimer(2.0, true, false, true).Timeout += () => SaveShot($"pick_{n:D2}");
        };
        int cam = 0;
        _game.KillCamStarted += () => { if (++cam <= 4) RecordClip($"killcam_{cam:D2}", 3.6); };
        _game.MatchEnded += _ => tree.CreateTimer(3.0, true, false, true).Timeout += () => { SaveShot("match_over"); tree.Quit(0); };
        _game.HostStartMatch();
    }

    // --videomatch: A full match for a video, recorded with Godot's --write-movie: generated names, the lobby for a few
    // seconds, the match played out by autopilot, then the match-over screen with a hover card, then quit.
    private void VideoMatch()
    {
        _autoMode = "video";
        _game.AutopilotLocal = true;
        _pick.ShowAutopilotPick = true;
        var rng = new Rng((ulong)System.DateTime.Now.Ticks);
        var names = FunNames.Pick(rng, _playersArg ? _simMax : 6);
        _net.LocalName = names[0];
        _net.LocalLook = Appearance.Random(rng);
        _net.StartOffline();
        AddCouch(); // --couch=N: guests on autopilot alongside us (a Survival run with a full team of humans)
        for (int i = 1 + _couch; i < names.Count; i++) _net.AddBot();
        for (int i = 1; i < names.Count && i < _net.Lobby.Players.Count; i++) _net.Lobby.Players[i].Name = names[i];
        // --mode / --teams up front, so the lobby on screen already shows the match that's about to be played.
        var mode = _net.TestMode ?? GameMode.Elimination;
        _net.SetSettings(new MatchSettings { BattlesPerRound = 1, RoundsToWin = 5, Mode = mode });
        int teams = _net.TestTeamSize > 0 ? _net.TestTeamSize : GameModes.Problem(mode, 0) != null ? Mathf.Clamp((names.Count + 1) / 2, 2, 4) : 0;
        if (teams > 0) _net.SetTeamSize(teams);
        Show(_lobby);
        var tree = GetTree();
        _game.MatchEnded += _ => Log.Info($"VIDEO match over after {Engine.GetProcessFrames() / 60.0:0}s at 60 fps");
        _game.MatchEnded += _ => tree.CreateTimer(4.0, true, false, true).Timeout += () =>
        {
            _hud.DebugHover = 0;
            tree.CreateTimer(3.0, true, false, true).Timeout += () =>
            {
                _hud.DebugHover = -1;
                tree.CreateTimer(2.5, true, false, true).Timeout += () => tree.Quit(0);
            };
        };
        tree.CreateTimer(3.0, true, false, true).Timeout += _game.HostStartMatch;
    }

    // --focuslog: For cropping a recording to portrait: one line per drawn frame with the phase, whether the kill cam
    // is on, live bullets and where the action is (0–1 across the screen).
    private void FocusLog(string arg)
    {
        var log = new System.IO.StreamWriter(arg["--focuslog=".Length..]) { AutoFlush = false };
        log.WriteLine("frame phase killcam bullets focus");
        RenderingServer.FramePostDraw += () =>
        {
            log.WriteLine($"{Engine.GetFramesDrawn()} {_game.Phase} {(_game.KillCam.Playing ? 1 : 0)} {_game.Bullets?.Bullets.Count ?? 0} {FocusX():0.000}");
            if (Engine.GetFramesDrawn() % 60 == 0) log.Flush();
        };
        TreeExiting += () => log.Dispose();
    }

    // --aimshots: Avatars aiming in 8 directions, then 4 near-vertical aims with the body still facing the other way
    // (the facing hysteresis). Red dot on the aim line, cyan dot at Muzzle(). Screenshot, then quit.
    private void AimShots(string arg)
    {
        _shotDir = arg["--aimshots=".Length..];
        Show();
        var canvas = new Control();
        canvas.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        _menu.GetParent().AddChild(canvas);
        var gun = GunLook.Default;
        // Faces that reach furthest (visor, big eyes, shades, mustache) to check the gun clears them.
        Appearance Face(int k)
        {
            var look = Appearance.Default();
            look[LookPart.Eyes] = new[] { 1, 5, 7, 4 }[k / 4 % 4];
            look[LookPart.Mouth] = new[] { 7, 3, 6 }[k % 3];
            return look;
        }
        canvas.Draw += () =>
        {
            const float s = 2.4f;
            for (int k = 0; k < 12; k++)
            {
                var aim = k < 8 ? Vector2.Right.Rotated(k * Mathf.Pi / 4) : Vector2.Up.Rotated(Mathf.DegToRad(new[] { -8, 8, 172, 188 }[k - 8]));
                int facing = k < 8 ? (aim.X < -0.01f ? -1 : 1) : (aim.X < 0 ? 1 : -1);
                var at = canvas.Size * new Vector2((k % 4 + 0.5f) / 4, 0.2f + 0.3f * (k / 4));
                canvas.DrawLine(at, at + aim * 70, new Color(1, 1, 1, 0.3f), 1);
                canvas.DrawCircle(at + aim * 70, 4, Colors.Red);
                AvatarArt.Draw(canvas, at, new AvatarPose
                {
                    Body = Palette.Player(k % 4), Look = Face(k), Facing = facing, Aim = aim,
                    Scale = s, Grounded = true, Gun = true, GunLook = gun, Flash = k % 2 == 0 ? 1 : 0, Kick = k % 3 == 0 ? 1 : 0,
                });
                canvas.DrawCircle(at + AvatarArt.Muzzle(aim, facing, s, gun), 2.5f, Colors.Cyan);
            }
        };
        GetTree().CreateTimer(0.5).Timeout += () => { SaveShot(); GetTree().Quit(0); };
    }

    // --crosshairshots: The crosshair in a range of ammo and reload states, at real size and zoomed 3x, over every arena
    // theme's scenery and platform color. One screenshot per theme, then quit.
    private void CrosshairShots(string arg)
    {
        _shotDir = arg["--crosshairshots=".Length..];
        Show();
        var canvas = new Control();
        canvas.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        canvas.MouseFilter = Control.MouseFilterEnum.Ignore;
        _menu.GetParent().AddChild(canvas);
        var states = new (int Ammo, int Mag, bool Reloading, float Frac, float Kick, float Ready, bool Armed)[]
        {
            (3, 3, false, 0, 0, 0, true), (2, 3, false, 0, 0.8f, 0, true), (1, 3, false, 0, 0, 0, true),
            (0, 3, true, 0.3f, 0, 0, true), (0, 3, true, 0.8f, 0, 0, true), (3, 3, false, 0, 0, 0.6f, true),
            (1, 1, false, 0, 0, 0, true), (7, 12, false, 0, 0, 0, true), (23, 40, false, 0, 0, 0, true), (0, 3, false, 0, 0, 0, false),
        };
        int theme = 0;
        canvas.Draw += () =>
        {
            var th = ArenaTheme.All[theme];
            var vs = canvas.Size;
            // A platform band so each state is seen over both the sky and a block.
            canvas.DrawRect(new Rect2(0, vs.Y * 0.62f, vs.X, vs.Y * 0.14f), th.Block);
            canvas.DrawRect(new Rect2(0, vs.Y * 0.62f, vs.X, 2), Shapes.Hdr(th.Edge, 1.3f));
            canvas.DrawString(Brawler.Fx.Fonts.Bold, new Vector2(12, 24), th.Name, HorizontalAlignment.Left, -1, 14, Colors.White);
            for (int k = 0; k < states.Length; k++)
            {
                var st = states[k];
                var cs = new CrosshairState
                {
                    Tint = Palette.Player(k % Palette.Players.Length), Armed = st.Armed, Ammo = st.Ammo, Mag = st.Mag,
                    Reloading = st.Reloading, ReloadFrac = st.Frac, Kick = st.Kick, Ready = st.Ready, Time = 0,
                };
                float x = vs.X * (k + 0.5f) / states.Length;
                Crosshair.Paint(canvas, new Vector2(x, vs.Y * 0.62f), cs);
                // Zoomed copy above, over the sky.
                canvas.DrawSetTransform(new Vector2(x, vs.Y * 0.3f), 0, new Vector2(2.2f, 2.2f));
                Crosshair.Paint(canvas, Vector2.Zero, cs);
                canvas.DrawSetTransform(Vector2.Zero, 0, Vector2.One);
                Crosshair.Paint(canvas, new Vector2(x, vs.Y * 0.88f), cs);
            }
        };
        var tree = GetTree();
        void Next()
        {
            _menuBackdrop.Set(ArenaTheme.All[theme], 3 + theme);
            canvas.QueueRedraw();
            tree.CreateTimer(0.4).Timeout += () =>
            {
                SaveShot();
                if (++theme >= ArenaTheme.All.Length) tree.Quit(0);
                else Next();
            };
        }
        tree.CreateTimer(0.3).Timeout += Next;
    }

    // --walkshots: Leg poses on a ground line with the hitbox outlined: 8 frames of the walk cycle, then idle, walking
    // backwards, rise / apex / fall, wall slides, a landing squash, and ramps. Screenshot, then quit.
    private void WalkShots(string arg)
    {
        _shotDir = arg["--walkshots=".Length..];
        Show();
        var canvas = new Control();
        canvas.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        _menu.GetParent().AddChild(canvas);
        var hat = Appearance.Default();
        hat[LookPart.Hat] = 2;
        var longHair = Appearance.Default();
        longHair[LookPart.Hair] = 4;
        longHair[LookPart.Mouth] = 6;
        var poses = new System.Collections.Generic.List<(string Name, AvatarPose Pose, float Slope, int Wall)>();
        AvatarPose P(int k) => new()
        {
            Body = Palette.Player(k % 4), Look = Appearance.Default(), Facing = 1, Aim = Vector2.Right, Scale = 5f,
            Time = 0.4f + k * 0.37f, Grounded = true, Gun = true, GunLook = GunLook.Default, MoveDir = 1,
        };
        for (int k = 0; k < 8; k++)
            poses.Add(($"walk {k * 45}", P(k) with { Walk = 1, Moving = true, WalkPhase = k * Mathf.Pi / 4 }, 0, 0));
        poses.Add(("idle", P(0), 0, 0));
        poses.Add(("backwards", P(1) with { Walk = 1, Moving = true, MoveDir = -1, WalkPhase = 0.6f }, 0, 0));
        poses.Add(("rise", P(2) with { Grounded = false, Air = 1, VelY = -280 }, 0, 0));
        poses.Add(("apex", P(3) with { Grounded = false, Air = 1, VelY = 0 }, 0, 0));
        poses.Add(("fall", P(0) with { Grounded = false, Air = 1, VelY = 300 }, 0, 0));
        poses.Add(("wall front", P(1) with { Grounded = false, Air = 1, VelY = 40, Wall = 1, WallSide = 1 }, 0, 1));
        poses.Add(("wall behind", P(2) with { Grounded = false, Air = 1, VelY = 40, Wall = 1, WallSide = -1 }, 0, -1));
        poses.Add(("landing", P(3) with { Land = 1 }, 0, 0));
        foreach (float k in new[] { -0.67f, 0.67f })
        {
            poses.Add(($"ramp {k:+0.00;-0.00} idle", P(0) with { Look = hat, GroundSlope = k, GroundDrop = 7 * Mathf.Abs(k) }, k, 0));
            poses.Add(($"ramp {k:+0.00;-0.00} walk", P(1) with { Look = longHair, GroundSlope = k, GroundDrop = 7 * Mathf.Abs(k), Walk = 1, Moving = true, WalkPhase = 1.2f }, k, 0));
        }
        poses.Add(("left facing walk", P(2) with { Facing = -1, Aim = Vector2.Left, MoveDir = -1, Walk = 1, Moving = true, WalkPhase = 2f }, 0, 0));
        poses.Add(("giant idle", P(3) with { Scale = 7.5f, Look = longHair }, 0, 0));
        int page = 0;
        canvas.Draw += () =>
        {
            var font = Brawler.Fx.Fonts.Bold;
            for (int k = page * 8; k < Mathf.Min(poses.Count, page * 8 + 8); k++)
            {
                var (name, pose, slope, wall) = poses[k];
                float s = pose.Scale;
                int cell = k % 8;
                var at = canvas.Size * new Vector2((cell % 4 + 0.5f) / 4, 0.3f + 0.48f * (cell / 4));
                var hs = new Vector2(Body.W, Body.H) * 0.5f * s;
                canvas.DrawRect(new Rect2(at - hs, hs * 2), new Color(1, 1, 1, 0.35f), false, 1);
                if (wall != 0)
                    canvas.DrawRect(new Rect2(at + new Vector2(wall > 0 ? hs.X : -hs.X - 30, -hs.Y - 30), new Vector2(30, hs.Y * 2 + 60)), new Color(0.4f, 0.45f, 0.6f));
                else
                {
                    // Ground line: flat at the hitbox bottom, or a ramp touching its uphill corner.
                    float drop = Mathf.Abs(slope) * hs.X;
                    var g0 = at + new Vector2(-60, hs.Y + drop + slope * -60);
                    var g1 = at + new Vector2(60, hs.Y + drop + slope * 60);
                    canvas.DrawColoredPolygon(new[] { g0, g1, g1 + new Vector2(0, 40), g0 + new Vector2(0, 40) }, new Color(0.3f, 0.32f, 0.45f));
                    canvas.DrawLine(g0, g1, new Color(0.8f, 0.85f, 1f), 1.5f);
                }
                AvatarArt.Draw(canvas, at, pose);
                canvas.DrawCircle(at + AvatarArt.BodyOffset(pose) + AvatarArt.TorsoAnchor * s, 2f, Colors.Cyan);
                canvas.DrawString(font, at + new Vector2(-70, -hs.Y - 60), name, HorizontalAlignment.Left, -1, 18);
            }
        };
        void NextPage()
        {
            SaveShot();
            if (++page * 8 >= poses.Count) { GetTree().Quit(0); return; }
            canvas.QueueRedraw();
            GetTree().CreateTimer(0.3).Timeout += NextPage;
        }
        GetTree().CreateTimer(0.5).Timeout += NextPage;
    }

    // --lookshots: Every eye, mouth, hair and hat option on a default avatar, a page of 21 at a time (some firing, some
    // mid-blink), to compare the art before and after a drawing change. Screenshot each page, then quit.
    private void LookShots(string arg)
    {
        _shotDir = arg["--lookshots=".Length..];
        var scaleArg = OS.GetCmdlineUserArgs().FirstOrDefault(x => x.StartsWith("--lookscale="));
        float lookScale = scaleArg != null && float.TryParse(scaleArg["--lookscale=".Length..], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float ls) ? ls : 3.2f;
        Show();
        var canvas = new Control();
        canvas.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        _menu.GetParent().AddChild(canvas);
        var looks = new System.Collections.Generic.List<(string Name, Appearance Look)>();
        void Add(LookPart part, string[] names)
        {
            for (int i = 0; i < names.Length; i++)
            {
                var look = Appearance.Default();
                look[part] = i;
                if (part == LookPart.Hat) look[LookPart.HatColor] = i;
                looks.Add(($"{part} {names[i]}", look));
            }
        }
        Add(LookPart.Eyes, Appearance.EyeNames);
        Add(LookPart.Mouth, Appearance.MouthNames);
        Add(LookPart.Hair, Appearance.HairNames);
        Add(LookPart.Hat, Appearance.HatNames);
        if (OS.GetCmdlineUserArgs().Contains("--lookcombos"))
        {
            // Every hat over every hair style (dyes vary so clashing colors show), then every mouth with every eyes.
            looks.Clear();
            for (int hat = 0; hat < Appearance.HatNames.Length; hat++)
                for (int hair = 0; hair < Appearance.HairNames.Length; hair++)
                {
                    var look = Appearance.Default();
                    look[LookPart.Hat] = hat;
                    look[LookPart.Hair] = hair;
                    look[LookPart.HairColor] = (hat + hair) % Appearance.Dyes.Length;
                    look[LookPart.HatColor] = (hat * 3 + 6) % Appearance.Dyes.Length;
                    looks.Add(($"{Appearance.HatNames[hat]} / {Appearance.HairNames[hair]}", look));
                }
            for (int mouth = 0; mouth < Appearance.MouthNames.Length; mouth++)
                for (int eyes = 0; eyes < Appearance.EyeNames.Length; eyes++)
                {
                    var look = Appearance.Default();
                    look[LookPart.Mouth] = mouth;
                    look[LookPart.Eyes] = eyes;
                    look[LookPart.Hair] = (mouth + eyes) % Appearance.HairNames.Length;
                    looks.Add(($"{Appearance.MouthNames[mouth]} / {Appearance.EyeNames[eyes]}", look));
                }
        }
        const int perPage = 21;
        int page = 0;
        canvas.Draw += () =>
        {
            var font = Brawler.Fx.Fonts.Bold;
            for (int k = page * perPage; k < Mathf.Min(looks.Count, (page + 1) * perPage); k++)
            {
                int cell = k % perPage;
                var at = canvas.Size * new Vector2((cell % 7 + 0.5f) / 7, 0.24f + 0.31f * (cell / 7));
                AvatarArt.Draw(canvas, at, new AvatarPose
                {
                    Body = Palette.Player(k % 4), Look = looks[k].Look, Facing = k % 5 == 4 ? -1 : 1,
                    Aim = (k % 5 == 4 ? Vector2.Left : Vector2.Right).Rotated(-0.3f), Scale = lookScale, Time = 0.3f + k * 0.41f,
                    Grounded = true, Gun = true, GunLook = GunLook.Default, Flash = k % 3 == 0 ? 1 : 0, Blink = k % 11 == 10,
                });
                canvas.DrawString(font, at + new Vector2(-60, -80), looks[k].Name, HorizontalAlignment.Left, -1, 12);
            }
        };
        void NextPage()
        {
            SaveShot();
            if (++page * perPage >= looks.Count) { GetTree().Quit(0); return; }
            canvas.QueueRedraw();
            GetTree().CreateTimer(0.3).Timeout += NextPage;
        }
        GetTree().CreateTimer(0.5).Timeout += NextPage;
    }

    // --menushots: Screenshot the menu, the character editor, an offline lobby with bots, and a gallery
    // of every option of every look part, then quit.
    private void MenuShots()
    {
        var tree = GetTree();
        var steps = new System.Collections.Generic.List<System.Action>
        {
            () => OpenCustomize(_menu),
            () => OpenOptions(_menu),
            () =>
            {
                _net.StartOffline();
                AddTestBots();
                Show(_lobby);
            },
            // The same lobby as 2v2 (a column per team, team tags, friendly fire), then the mode button once more for 3v3.
            () => _net.SetTeamSize(2),
            () => _net.SetTeamSize(3),
            () =>
            {
                _net.SetSettings(new MatchSettings { DisabledCards = new(AutoDisabled) });
                _lobby.ShowCardPool(true);
            },
            () =>
            {
                // Every category's None: the pool stops at its minimum and says why. Saves next to the shots.
                CardPoolSettings.Path = $"{_shotDir}/cards_settings.cfg";
                foreach (var b in _lobby.FindChildren("*", "Button", true, false).OfType<Button>().Where(b => b.Text == "None"))
                    b.EmitSignal(BaseButton.SignalName.Pressed);
                int left = CardPool.EnabledCount(_net.Lobby.Settings.DisabledCards);
                Log.Info($"MENUSHOTS card pool after None everywhere: {left} enabled, saved [{string.Join(",", CardPoolSettings.Load().OrderBy(id => id))}]");
            },
            () =>
            {
                _lobby.ShowCardPool(false);
                _net.UseUpnp = false; // a screenshot run must not touch the router
                _net.Host(24611);
                _net.AddBot();
                Show(_lobby);
            },
            () =>
            {
                // The Video tab: window, bloom, shake and flashes.
                OpenOptions(_lobby);
                foreach (var t in _options.FindChildren("*", "TabContainer", true, false).OfType<TabContainer>()) t.CurrentTab = 1;
            },
            () =>
            {
                // The Controls tab: every action, emotes and ping included, must fit on screen.
                OpenOptions(_lobby);
                foreach (var t in _options.FindChildren("*", "TabContainer", true, false).OfType<TabContainer>()) t.CurrentTab = 2;
            },
            () =>
            {
                // The Online tab, with the lobby still hosting behind it.
                OpenOptions(_lobby);
                foreach (var t in _options.FindChildren("*", "TabContainer", true, false).OfType<TabContainer>()) t.CurrentTab = 3;
            },
            () => { Show(_lobby); Log.Info($"MENUSHOTS copy button: {_lobby.FindChildren("*", "Button", true, false).OfType<Button>().FirstOrDefault(b => b.Text.StartsWith("Copy"))?.Text}"); },
            () => { _net.Leave(); Show(_menu); },
        };
        foreach (var part in Appearance.Editable)
        {
            var p = part;
            steps.Add(() => ShowGallery(p));
        }
        int i = 0;
        void Next()
        {
            SaveShot();
            if (i >= steps.Count) { tree.Quit(0); return; }
            steps[i++]();
            tree.CreateTimer(0.4).Timeout += Next;
        }
        tree.CreateTimer(0.5).Timeout += Next;
    }
}
