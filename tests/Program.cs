using System;
using System.Linq;
using Brawler.Tests;
using static Brawler.Tests.Harness;

// Unit tests for the engine-free game logic in godot/scripts/Logic, one file per area in Sections/.
//   dotnet run                  every check
//   dotnet run -- MapCode       only the areas whose name contains "MapCode" (spaces and case don't matter); if no
//                               area matches, every check whose own name contains it
//   dotnet run -- --wav=DIR     also write every sound and song as .wav files for listening
// A new area: add a Sections/<Name>Tests.cs with a static Run() and list it below.
var sections = new (string Name, string Title, Action Run)[]
{
    ("Rng", "RNG", RngTests.Run),
    ("Stats", "Stats", StatsTests.Run),
    ("JumpRules", "Jump rules", JumpRulesTests.Run),
    ("MapGeneration", "Map generation", MapGenerationTests.Run),
    ("ArenaFeatures", "Arena features", ArenaFeaturesTests.Run),
    ("Sweep", "Sweep", SweepTests.Run),
    ("CardStats", "Card stats", CardStatsTests.Run),
    ("Appearance", "Appearance", AppearanceTests.Run),
    ("AudioSynthesis", "Audio synthesis", AudioSynthesisTests.Run),
    ("JoinAddresses", "Join addresses", JoinAddressesTests.Run),
    ("StreamerModeInviteCodes", "Streamer-mode invite codes", StreamerModeInviteCodesTests.Run),
    ("Emotes", "Emotes", EmotesTests.Run),
    ("SuddenDeathLava", "Sudden-death lava", SuddenDeathLavaTests.Run),
    ("EffectsBudget", "Effects budget (dynamic particle culling)", EffectsBudgetTests.Run),
    ("CardCombos", "Card combos", CardCombosTests.Run),
    ("KeyBindings", "Key bindings", KeyBindingsTests.Run),
    ("Terrain", "Terrain: hills, valleys and ramps", TerrainTests.Run),
    ("ArenaThemesFitTheirFeatures", "Arena themes fit their features", ArenaThemesFitTheirFeaturesTests.Run),
    ("CardPool", "Card pool (host-disabled cards)", CardPoolTests.Run),
    ("RareCardsAndCursedDeals", "Rare cards and cursed deals", RareCardsAndCursedDealsTests.Run),
    ("CustomCards", "Custom cards (JSON files, host sync)", CustomCardsTests.Run),
    ("VerticalClearanceBetweenPlatforms", "Vertical clearance between platforms", VerticalClearanceBetweenPlatformsTests.Run),
    ("LegAnimationFooting", "Leg animation footing (cosmetic ground / ramp / wall probes)", LegAnimationFootingTests.Run),
    ("GunRig", "Gun rig: the arm holds the gun clear of the face; muzzle is shared by art and bullets", GunRigTests.Run),
    ("CrosshairAmmoPips", "Crosshair ammo pips", CrosshairAmmoPipsTests.Run),
    ("ThickFloorBand", "Thick floor band (room for the HUD card strip)", ThickFloorBandTests.Run),
    ("ArenaAndMovementScale", "Arena and movement scale (25% bigger arena, faster run, higher jump)", ArenaAndMovementScaleTests.Run),
    ("ReadmeCaptureHelpers", "README capture helpers", ReadmeCaptureHelpersTests.Run),
    ("EndOfMatchAwards", "End-of-match awards", EndOfMatchAwardsTests.Run),
    ("MatchStats", "Match stats (the match-over Stats tab)", MatchStatsTests.Run),
    ("KillCamTiming", "Kill cam timing", KillCamTimingTests.Run),
    ("TeamModes", "Team modes", TeamModesTests.Run),
    ("GameModes", "Game modes", GameModesTests.Run),
    ("ControllersAndCouchCoOp", "Controllers and couch co-op", ControllersAndCouchCoOpTests.Run),
    ("MapDesignsAndMapCodes", "Map designs and map codes", MapDesignsAndMapCodesTests.Run),
    ("BotSkill", "Bot skill", BotSkillTests.Run),
    ("Draft", "Draft", DraftTests.Run),
    ("CosmeticUnlocks", "Cosmetic unlocks", CosmeticUnlocksTests.Run),
    ("DailyChallenge", "Daily challenge", DailyChallengeTests.Run),
    ("Ballistics", "Ballistics (bot aim and jumps) and snapshot packing", BallisticsTests.Run),
};

Args = args;
var words = args.Where(a => !a.StartsWith("--")).ToArray();
static string Key(string s) => new string(s.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
var run = words.Length == 0 ? sections
    : sections.Where(s => words.Any(w => Key(s.Name).Contains(Key(w)) || Key(s.Title).Contains(Key(w)))).ToArray();
if (run.Length == 0)
{
    NameFilter = words;
    run = sections;
}
foreach (var s in run) s.Run();

if (Passed + Failed == 0)
{
    Console.WriteLine($"No area or check matches {string.Join(" ", words)}. Areas: {string.Join(", ", sections.Select(s => s.Name))}");
    return 1;
}
Console.WriteLine(Failed == 0 ? "\nALL TESTS PASSED" : $"\n{Failed} TEST(S) FAILED");
return Failed == 0 ? 0 : 1;
