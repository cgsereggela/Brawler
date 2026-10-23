# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

Brawler: a ROUNDS-style 2–8 player platform shooter in **Godot 4.7.2 .NET (C#, net9.0)**. The Godot project lives in `godot/`; `README.md` is the authoritative, detailed reference (rules, cards, every test flag, release process) and is kept up to date with features.

## Commands

```bash
dotnet build godot/Brawler.csproj                 # build the game (needed before running Godot from a terminal)
cd tests && dotnet run                            # unit tests for godot/scripts/Logic (prints PASS/FAIL per check, exit 1 on any failure)
cd tools/CardStats && dotnet run                  # card balance report from the stats log
```

There is no way to run a single unit test: `tests/Program.cs` is one top-level-statements file of `Check(cond, name)` calls grouped in `// ---------- Section ----------` blocks. Add new checks to the relevant section.

End-to-end checks run the real game with a command-line mode after `--`. On this machine Godot is at `C:/Users/cgser/Downloads/Godot_v4.7.2-stable_mono_win64/Godot_v4.7.2-stable_mono_win64/`; use the `_console.exe` so log output reaches the terminal (the plain `.exe` is the editor/GUI build):

```bash
GODOT="C:/Users/cgser/Downloads/Godot_v4.7.2-stable_mono_win64/Godot_v4.7.2-stable_mono_win64/Godot_v4.7.2-stable_mono_win64_console.exe"
"$GODOT" --headless --path godot --fixed-fps 60 -- --autotest     # offline full match driven by bots
"$GODOT" --path godot -- --editorcheck                            # the check CI runs before a release
```

Each check ends by logging `AUTOTEST PASS: …` or `AUTOTEST FAIL: …` (`Main.Checks.cs` `Finish`) and exits with that code. The README's **Tests** section lists every mode (`--autohost`/`--autojoin`, `--padcheck`, `--lobbycheck`, `--hazardcheck`, screenshot modes like `--menushots`/`--shots=DIR`, benches, `--simulate=N`, etc.). Windowed-only checks (`--editorcheck`, `--lobbycheck`, `--clipcheck`, screenshot modes) need a display.

Releases: pushing a `v*` tag runs `.github/workflows/release.yml` (unit tests, `--editorcheck`, export + zip via `tools/package.ps1`). The tag becomes the game version, which peers must match to join. When upgrading Godot, change `GODOT_VERSION` in the workflow and the SDK line in `godot/Brawler.csproj` together.

## Architecture

- **One scene, everything in code.** `godot/scenes/Main.tscn` is the only scene. `Main.cs` wires screens; the `Main.*.cs` partials hold the command-line test/screenshot/benchmark/demo modes (`CommandLine` parses flags, `Checks`, `Shots`, `Demos`).
- **`scripts/Logic/` is engine-free** (no `using Godot`) so `tests/` can compile it directly (`tests/Brawler.Tests.csproj` links `../godot/scripts/Logic/**/*.cs`). Rules, RNG, stats, cards, map generation, map codes, teams/game modes, synth audio live here. Keep Godot types out of it; pure maths that only lives in `Game/` cannot be unit-tested.
- **`scripts/Game/`**: `GameManager` is split into partials (`.Flow` match flow/kill cam/picks, `.Combat` damage, `.Modes` respawns/objectives, `.Chat` emotes/pings, `.Sync` snapshots and wire formats). Plus `Avatar`/`AvatarArt` (all character art is vector shapes drawn in code), `BulletSystem`, `BotBrain`, `CardEffects`, `MapNode`/`Arena`, `KillCam`, `FxStyle`.
- **`scripts/Core/`**: settings, profile, saved maps; `Shared.cs` holds lobby/settings/input types and the binary wire format. **`scripts/Net/`**: ENet host/join, lobby sync, version check, UPnP. **`scripts/Fx/`**: batched particles (`TriBatch`, budgeted by `Logic/FxBudget`). **`scripts/UI/`**: all screens.

### Netcode (determinism matters)
Host-authoritative. Clients send one `InputFrame` per physics tick; the host simulates every avatar, resolves hits and broadcasts 60 Hz snapshots; clients predict their own movement. Bullets are sent as **spawn events** and simulated on every peer, but only the host decides hits, splits and explosions. Arenas are rebuilt from a **seed** on each peer, so `MapGen`/`Rng` must stay deterministic. Bullet behavior every peer must simulate goes on `Bullet` and its wire format in `BulletSystem.cs`; host-only rules go in `GameManager.HostDamage` and friends.

## Invariants that are easy to break

- **Card ids are permanent**: never reuse or renumber (the stats log references them). Built-in cards are added in `CardRegistry` in `Logic/Cards.cs`; custom JSON cards use ids ≥ 1000. Adds apply before multipliers. Changing card numbers changes the card-table hash (new balance version) on purpose.
- **Appearance option lists are append-only** (`EyeNames`, `HairNames`, … in `Logic/Appearance.cs`): profiles store indices. Each new entry needs a matching `case` in `AvatarArt.Draw…`.
- Wire/save formats must stay readable from older data (tests check e.g. lobby data from before a field existed).
- Step-by-step recipes for adding a card, a look part or an arena theme are in the README; follow them.

## Repo conventions

- Always commit a script's Godot `.uid` file alongside it.
- `docs/changelist.txt` is the to-do list (issues to fix, features, performance). When an item is done, delete its line in a separate commit titled `Changelist: <item> fixed` (or similar).
- Commit subjects are short plain-English sentences, often prefixed with the area (`Lobby: …`, `Bots: …`).
- When a feature or test flag changes, update the matching README section (and `docs/` player guides / `docs/CUSTOM_CARDS.md` where relevant).
