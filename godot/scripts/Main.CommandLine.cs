using System.Linq;
using Brawler.Core;
using Brawler.Fx;
using Brawler.Game;
using Brawler.Logic;
using Brawler.Net;
using Brawler.UI;
using Godot;

namespace Brawler;

// Automated test modes (pass after "--" on the command line):
//   --autotest            offline match, local player + 3 bots (--players=N: N players) on autopilot; exits 0 when the match ends
//   --autohost[=port]     host a LAN game with 1 bot, autopilot, start once a client joins
//   --autojoin[=ip:port]  join a LAN game on autopilot
//                         (all three take some cards out of the pool and fail if a pick offers one; the joiner also
//                         checks it received the host's disabled list)
//   --simulate=N          play N bot-only matches back to back (2–8 players) to fill the card stats log; exits 0
//   --players=A[-B]       player count (or range) per simulated match, default 2-8; the screenshot modes and --autotest
//                         take the top of it as their player count (default 4)
//   --statsdir=PATH       where card_stats.jsonl goes (default user://stats)
//   --battlecap=SECS      (with --simulate) abandon a battle after SECS simulated seconds; logs STALL lines with bot state
//   --killcamskip         (with --autotest/--autohost/--autojoin) ask to skip each kill cam half a second in
//   --stats               also record stats during --autotest/--autohost (off by default so tests don't pollute the log)
//   --arenashots          (with --shots=DIR) screenshot battles on seeds rich in crates, pools, saws and movers
//   --terrainshots        (with --shots=DIR) screenshot battles on hilly seeds, one per map style and per arena theme
//   --hudshots            (with --shots=DIR, and --cards=) screenshot each player chip's hover card (cards and stats)
//   --cardshots           (with --shots=DIR) every card, 24 to a page, to check their icons side by side
//   --readmeshots         (with --shots=DIR, run with --fixed-fps 60) the README media: a match with generated names,
//                         a GIF clip + screenshot per battle and card pick, the first kill cams, hover cards and the match-over
//                         screen, then quit
//   --videomatch          a full match to record with --write-movie: generated names (6 players, or --players=N), the
//                         lobby, the match played by autopilot, the match-over screen with a hover card, then quit;
//                         takes --mode, --teams, --rounds and --couch (the lobby on screen already shows them); with --customize it
//                         first tries on looks in the Customize screen (on a profile of its own) and plays in the last one
//   --demo=NAME           a scripted walk through one feature (thumbs), paced for recording with --write-movie for a
//                         Short; logs "DEMO <frame> <label>" at the moments to cut to
//   --botskills=A,B,..    bots get these skills (easy, normal, hard) as they are added, in turn
//   --draft               draft the opening cards from a shared pool before the first battle
//   --focuslog=PATH       write one line per drawn frame (phase, kill cam, live bullets, where the action is across the
//                         screen), to crop a recording to portrait
//   --aimshots=DIR        screenshot avatars with the widest faces aiming every way (barrel vs aim line, Muzzle dot,
//                         some with full recoil) into DIR, then quit
//   --crosshairshots=DIR  screenshot the crosshair in a range of ammo/reload states over every arena theme, then quit
//   --walkshots=DIR       screenshot leg poses (walk cycle frames, idle, air, wall slide, landing, ramps) into DIR, then quit
//   --lookshots=DIR       screenshot every eye, mouth, hair and hat option (some firing, some blinking) into DIR, then quit;
//                         --lookscale=S draws them at size S (default 3.2; 1 is their size in a match)
//                         --lookcombos instead draws every hat over every hair style, then every mouth with every eyes
//   --upnpcheck           look for a UPnP router and print its public address (read-only, forwards nothing), then quit
//   --cards=ID,ID,...     (offline) every player starts each battle holding these cards; --cards=random:N deals N random
//                         cards to each player every battle, to exercise the whole card pool
//   --carddir=DIR         load custom cards from DIR instead of user://cards (so tests leave the real folder alone)
//   --overlaycheck        (with --autotest) presses Esc mid-battle, checks the game keeps running under the options
//                         overlay with local input muted until a card pick, tries Leave match then No, closes it with
//                         Esc, then finishes the match; also checks the crosshair replaces the pointer only while
//                         you fight (not under the overlay or the card pick)
//   --leavecheck=WHEN     (with --autotest) leaves the match at WHEN (fight, killcam, pick or over) and checks you land
//                         in the menu with the normal pointer back and no crosshair left on screen; with --lockmouse
//                         (mouse lock on for this run, not saved) also checks the pointer is held in the window till then
//   --lava=SECS           sudden-death lava rises after SECS seconds of fighting (0 = off) in the matches this run hosts
//   --lavacheck           (with --autotest, --autohost or --autojoin) lava after 3 s; checks it rises on every peer, that
//                         nobody survives long in it, and (with --shots=DIR) screenshots it once a battle
//   --rarechance=PCT      percent chance that each offered card is rare, in the matches this run hosts
//   --teams=N             team mode for the matches this run hosts: N players a side (2, 3 or 4); --friendlyfire turns friendly fire on
//   --mode=ID             game mode for the matches this run hosts: elimination, koth, ctf, headhunter, gungame or survival (ctf without --teams plays even teams)
//   --arena=CODE          every battle of the matches this run hosts is played on this map code (see the map editor)
//   --rounds=N            round wins to take the match, for the matches this run hosts (a shorter test match)
//   --padcheck            feeds made-up controller presses: menu focus, + Local Player in the lobby, guest vs keyboard
//                         player input, and a card pick chosen on each controller; exits 0 on success
//   --clipcheck           (windowed) an offline match on autopilot until a kill cam is kept, then saves it as a GIF from
//                         the Esc overlay and checks the file, then quits
//   --musiccheck          (windowed) waits for the menu music, checks an M typed into the name box doesn't skip it and a
//                         plain M does, then quits
//   --lobbycheck          fills the lobby (a couch guest and bots) and walks every game mode and team size, then every
//                         Options tab (menu and in-match overlay), failing if
//                         anything ends up off screen (with --shots=DIR, saves each layout), then quits
//   --editorcheck         drives the map editor with made-up mouse presses (draw a block, a gap and a hill; place a spawn, a
//                         crate and a pool; move, resize, undo, delete; save, share and reopen a map code), then playtests
//                         the arena against a bot and checks the battle is played on it; exits 0 on success (with
//                         --shots=DIR, screenshots the editor)
//   --hazardcheck         bots only, on an arena with pools, a sawblade and a platform only a moving platform reaches;
//                         logs hazard damage per bot-minute and mover rides, and fails if bots walk into hazards or never
//                         ride the mover
//   --vantagecheck        one bot against a player standing still on a ledge it can't reach; fails if the bot rarely has
//                         a clear shot (it used to wait under the ledge, out of sight) or hardly hits
//   --hangercheck         one bot against a player standing still under a hanging platform; fails if the bot doesn't shoot
//                         the chains and crush them within 30 s
//   --beltcheck           a player and a crate standing on conveyor belts (and a bot standing still); fails unless both
//                         are carried along at the belt's speed, the way it runs
//   --driftcheck          bots only, on Drift arenas; counts falls nobody caused per bot-minute and fails if there are many
//   --errorcheck          throws an exception in a frame callback and checks the checks would have seen it (every check
//                         fails if the game logged an error, naming the first)
//   --dailycheck[=DAY]    the autopilot plays a day's daily challenge (yyyymmdd, default today) on a scratch profile:
//                         checks the arena, mode, starting hand and Hard bots, and that the best result is kept
//   --trainingcheck       the autopilot on the training range: card browser, hand-set cards, dummies dying and coming
//                         back, a sparring bot that never fires, Reset keeping the cards
//   --cardcheck[=NAME]    every built-in card (or those whose name contains NAME) held by everyone on the training range,
//                         one at a time, until each effect it adds has happened (a bounce, a split, a heal...)
//   --artbench            the C# time and memory to draw one character (AvatarArt), over 32 looks in many poses; needs a
//                         window
//   --seed=N              seeds everything random that steers the run (arenas, card offers, bots, autopilot picks); every
//                         check prints its seed in the AUTOTEST line, so with --fixed-fps a failing run replays exactly
//   --couch=N             (with --autotest, --autohost or --autojoin) N local co-op guests (1-3) join from this machine on
//                         autopilot; checks at the end that they played and picked cards like everyone else
//   --streamer            streamer mode on for this run only (not saved); with --menushots, shows the hosted lobby hiding addresses
//   --nomusic             mute the music for this run only (not saved), so a --write-movie recording has just the sound
//                         effects, to lay music under it later; --noaudio mutes everything (the recording's track is silent)
public partial class Main
{
    private string _autoMode;
    private bool _matchFinished;
    private long _frames;
    private const long AutotestMaxFrames = 60L * 60 * 30; // 30 simulated minutes per match

    // ---------- automated test modes ----------

    private void ParseDebugCards(string spec)
    {
        if (spec.StartsWith("random:") && int.TryParse(spec["random:".Length..], out int n))
        {
            var rng = new Rng(RunSeed.Next()); // replayable with --seed
            _game.DebugCards = slot =>
            {
                var hand = new System.Collections.Generic.List<int>();
                for (int i = 0; i < n; i++) hand.Add(CardRegistry.All[rng.Range(0, CardRegistry.All.Count - 1)].Id);
                Log.Info($"P{slot} cards: {string.Join(", ", hand.Select(id => CardRegistry.Get(id).Name))}");
                return hand;
            };
            return;
        }
        var ids = spec.Split(',').Select(s => int.TryParse(s, out int id) ? id : -1).Where(id => CardRegistry.Get(id) != null).ToList();
        _game.DebugCards = _ => new System.Collections.Generic.List<int>(ids);
    }

    private void HandleCommandLine()
    {
        var args = OS.GetCmdlineUserArgs();
        bool forceStats = false;
        foreach (var a in args)
        {
            if (a.StartsWith("--shots=")) _shotDir = a["--shots=".Length..];
            else if (a == "--streamer") StreamerMode.SetForRun(true);
            else if (a == "--nomusic") { AudioSettings.MuteMusicForRun = true; AudioSettings.Apply(); }
            else if (a == "--noaudio") { AudioSettings.MuteAllForRun = true; AudioSettings.Apply(); }
            else if (a.StartsWith("--lava=") && int.TryParse(a["--lava=".Length..], out int lava)) _game.LavaOverride = lava;
            else if (a.StartsWith("--rarechance=") && int.TryParse(a["--rarechance=".Length..], out int rare)) _game.RareOverride = Mathf.Clamp(rare, 0, 100);
            else if (a.StartsWith("--teams=") && int.TryParse(a["--teams=".Length..], out int teams)) _net.TestTeamSize = Mathf.Clamp(teams, 0, 4);
            else if (a == "--friendlyfire") _net.TestFriendlyFire = true;
            else if (a.StartsWith("--mode=")) _net.TestMode = GameModes.Parse(a["--mode=".Length..]);
            else if (a.StartsWith("--rounds=") && int.TryParse(a["--rounds=".Length..], out int rounds)) _net.TestRounds = Mathf.Clamp(rounds, 1, 10);
            else if (a == "--draft") _net.TestDraft = true;
            else if (a.StartsWith("--arena="))
            {
                if (MapCode.Decode(a["--arena=".Length..]) is { } arena) _net.TestArena = MapCode.Pack(arena);
                else { Finish(1, "--arena: that isn't a map code"); return; }
            }
            else if (a == "--lavacheck") { _lavaCheck = true; if (_game.LavaOverride < 0) _game.LavaOverride = 3; }
            else if (a.StartsWith("--battlecap=") && float.TryParse(a["--battlecap=".Length..], System.Globalization.CultureInfo.InvariantCulture, out float cap)) _battleCap = cap;
            else if (a.StartsWith("--statsdir=")) _game.Stats.Dir = a["--statsdir=".Length..].Replace('\\', '/');
            else if (a == "--stats") forceStats = true;
            else if (a == "--overlaycheck") _overlayCheck = true;
            else if (a.StartsWith("--leavecheck=")) _leaveWhen = a["--leavecheck=".Length..];
            else if (a == "--killcamskip") _game.KillCamStarted += () => GetTree().CreateTimer(0.5, true, false, true).Timeout += _game.SkipKillCam;
            else if (a.StartsWith("--dropat=") && int.TryParse(a["--dropat=".Length..], out int drop)) _dropAt = drop;
            else if (a == "--clipcheck")
            {
                _autoMode = "clipcheck";
                ClipCheck();
            }
            else if (a == "--musiccheck")
            {
                _autoMode = "musiccheck";
                MusicCheck();
            }
            else if (a == "--lobbycheck")
            {
                _autoMode = "lobbycheck";
                LobbyCheck();
            }
            else if (a == "--editorcheck")
            {
                _autoMode = "editorcheck";
                _game.DisableHitStop = true;
                EditorCheck();
            }
            else if (a == "--padcheck")
            {
                _autoMode = "padcheck";
                _padCheck = true;
                _game.DisableHitStop = true;
                PadCheck();
            }
            else if (a == "--nobots") _noBots = true;
            else if (a.StartsWith("--botskills="))
                _net.TestBotSkills = a["--botskills=".Length..].Split(',').Select(s => System.Enum.TryParse<BotSkill>(s, true, out var k) ? k : BotSkill.Normal).ToList();
            else if (a.StartsWith("--couch=") && int.TryParse(a["--couch=".Length..], out int couch)) _couch = Mathf.Clamp(couch, 0, CouchRoster.MaxGuests);
            else if (a == "--nofxcull") Fx.FxLayer.Culling = false;
            else if (a == "--rematch") _rematchTest = true;
            else if (a.StartsWith("--cards=")) ParseDebugCards(a["--cards=".Length..]);
            else if (a.StartsWith("--players="))
            {
                var r = a["--players=".Length..].Split('-');
                _playersArg = true;
                if (int.TryParse(r[0], out int lo)) _simMin = _simMax = lo;
                if (r.Length > 1 && int.TryParse(r[1], out int hi)) _simMax = hi;
                _simMin = Mathf.Clamp(_simMin, 2, LobbyState.MaxPlayers);
                _simMax = Mathf.Clamp(_simMax, _simMin, LobbyState.MaxPlayers);
            }
        }
        if (args.Any(a => a is "--autotest" or "--readmeshots" or "--videomatch" or "--padcheck" || a.StartsWith("--autohost") || a.StartsWith("--autojoin")))
            _game.Stats.Enabled = forceStats;
        if (_overlayCheck) _game.CardPickBegan += () => _ocSawPick = true;
        if (_shotDir != null && !args.Contains("--readmeshots")) // readme names its own shots
        {
            _game.CardPickBegan += () => GetTree().CreateTimer(0.3).Timeout += SaveShot;
            _game.MatchEnded += _ =>
            {
                GetTree().CreateTimer(0.3, true, false, true).Timeout += () =>
                {
                    SaveShot();
                    // Then with a hover card open, which must draw over the match-over screen.
                    _hud.DebugHover = 0;
                    GetTree().CreateTimer(0.1, true, false, true).Timeout += () =>
                    {
                        SaveShot();
                        _hud.DebugHover = -1;
                        // And the Stats tab.
                        if (_over.ShowStats()) GetTree().CreateTimer(0.1, true, false, true).Timeout += SaveShot;
                    };
                };
            };
            // The kill cam's build-up, its slow-motion kill and the aftermath (real seconds: it slows game time).
            _game.KillCamStarted += () =>
            {
                foreach (double at in new[] { 0.7, 1.9, 2.8 })
                    GetTree().CreateTimer(at, true, false, true).Timeout += () => { if (_game.KillCam.Playing) SaveShot(); };
            };
        }

        foreach (var a in args)
        {
            if (a == "--autotest") { if (!StartAutotest()) return; }
            else if (a.StartsWith("--autohost")) { if (!StartAutoHost(a)) return; }
            else if (a.StartsWith("--autojoin")) StartAutoJoin(a);
            else if (a.StartsWith("--simulate")) StartSimulate(a);
            else if (a == "--hazardcheck") StartHazardCheck();
            else if (a == "--vantagecheck") StartVantageCheck();
            else if (a == "--hangercheck") StartHangerCheck();
            else if (a == "--beltcheck") StartBeltCheck();
            else if (a == "--driftcheck") StartDriftCheck();
            else if (a == "--errorcheck") ErrorCheck();
            else if (a.StartsWith("--dailycheck")) StartDailyCheck(a);
            else if (a == "--trainingcheck") StartTrainingCheck();
            else if (a == "--cardcheck" || a.StartsWith("--cardcheck=")) StartCardCheck();
            else if (a == "--themebench") ThemeBench();
            else if (a == "--fxbench") FxBench();
            else if (a == "--artbench") ArtBench();
            else if (a == "--fxshots" && _shotDir != null) FxShots();
            else if (a == "--cardshots" && _shotDir != null) CardShots();
            else if (a == "--arenashots" && _shotDir != null) ArenaShots();
            else if (a == "--layoutshots" && _shotDir != null) LayoutShots();
            else if (a == "--hudshots" && _shotDir != null) HudShots();
            else if (a == "--terrainshots" && _shotDir != null) TerrainShots();
            else if (a == "--readmeshots" && _shotDir != null) ReadmeShots();
            else if (a == "--videomatch") VideoMatch();
            else if (a.StartsWith("--demo=")) Demo(a["--demo=".Length..]);
            else if (a.StartsWith("--focuslog=")) FocusLog(a);
            else if (a.StartsWith("--aimshots=")) AimShots(a);
            else if (a.StartsWith("--crosshairshots=")) CrosshairShots(a);
            else if (a.StartsWith("--walkshots=")) WalkShots(a);
            else if (a.StartsWith("--lookshots=")) LookShots(a);
            else if (a == "--upnpcheck")
            {
                System.Threading.Tasks.Task.Run(() =>
                {
                    string result = PortOpener.Probe();
                    Callable.From(() => { Log.Info($"UPNPCHECK {result}"); GetTree().Quit(0); }).CallDeferred();
                });
            }
            else if (a == "--makeicon")
            {
                _autoMode = "icon";
                Fx.IconMaker.Make(this);
            }
            else if (a == "--menushots" && _shotDir != null) MenuShots();
        }
    }
}
