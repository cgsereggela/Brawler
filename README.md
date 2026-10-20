# Brawler

A ROUNDS-style 2–4 player platform shooter: lose a round, pick a card, come back stronger.
Godot 4.7 (.NET / C#), LAN or direct-IP multiplayer, bots, procedurally generated themed arenas, and effects that get flashier the more cards you hold.

## Download and play

**Just want to play?** Grab the zip for your system from the **[Releases](../../releases/latest)** page, extract it and run it.
You don't need anything else installed. Step-by-step guides:

- **[Install and run](docs/INSTALL.md)**: Windows, macOS and Linux, plus the "unknown publisher" warnings.
- **[Playing online](docs/PLAYING_ONLINE.md)**: friends on different home networks, via Tailscale, automatic port opening or manual port forwarding.

## Run from source

1. Open `godot/project.godot` in **Godot 4.7 .NET** (you already have `Godot_v4.7.2-stable_mono_win64`).
2. Press **Build** (hammer icon) once, then **Run** (F5).

Or from a terminal: `dotnet build godot/Brawler.csproj`, then run the Godot exe with `--path godot`.

### Controls
| Action | Keys |
|---|---|
| Move | A / D (or arrows) |
| Jump / wall jump | W / Space |
| Shoot | Left mouse (hold) |
| Block | Right mouse / Shift |
| Leave match | Esc twice |

### Playing with friends
- **Host:** Menu → *Host Game*. The lobby shows your LAN/VPN address(es) and the port (default `24565`). It also asks
  the router to forward the port via UPnP and, if that works, shows the public `ip:port` with a **Copy** button.
- **Join:** type the host's address (`ip`, `ip:port` or `host:port`) and click *Join*. The last address is remembered.
- Players must run the same version (`application/config/version`, shown in the menu); the host rejects mismatches.
- Different home networks: see **[docs/PLAYING_ONLINE.md](docs/PLAYING_ONLINE.md)**.
- **Test alone:** in Godot use *Debug → Customize Run Instances → 2*, host in one window and join `127.0.0.1` in the other.

## Making a release
Players download zips from GitHub Releases. Builds come from Godot's export presets in `godot/export_presets.cfg`
(Windows, Linux, macOS universal, ad-hoc signed), zipped by `tools/package.ps1` together with a `README.txt` made from
`docs/INSTALL.md` + `docs/PLAYING_ONLINE.md`.

**Automatic (GitHub Actions):** push a version tag and `.github/workflows/release.yml` runs the tests, exports all three
platforms on Linux, and publishes a release with the zips (notes from `docs/RELEASE_NOTES.md` plus the commit list).
The tag becomes the game version.
```bash
git tag v0.2.0
git push origin v0.2.0
```
Running the workflow by hand (*Actions → Release → Run workflow*) builds the zips as a downloadable artifact without
publishing a release.

**Local:** needs the Godot .NET editor and its export templates (`-InstallTemplates` downloads them, about 1 GB, once).
```bash
powershell -ExecutionPolicy Bypass -File tools/package.ps1 -Godot "C:/path/to/Godot_v4.7.2-stable_mono_win64_console.exe" -InstallTemplates
```
Output goes to `dist/Brawler-<version>-<platform>.zip`. Pass `-Platforms windows` to build just one and `-Version 0.2.0` to bump the version.
When upgrading Godot, change `GODOT_VERSION` in the workflow and the SDK line in `Brawler.csproj` together.

## Rules
- A **battle** ends when one player is left standing. Win *N battles* to take a **round**, and win *M rounds* to take the **match**. Both numbers are set in the lobby.
- After every round, **everyone picks a card**. Losers choose from more options (default 5) than the winner (default 3, or 0 to skip).
- Each battle gets a newly generated arena.

## Cards
There are 69 cards in four categories. Strong cards come with a tradeoff (less damage, HP, ammo, speed, ...). The full list with exact numbers is in `godot/scripts/Logic/Cards.cs`.
- **Gun** (orange): more, bigger, faster or slower bullets. For example Sniper, Minigun, Twin Shot, Hail Storm, Cannonball, Needles, Ricochet, plus the originals (Bouncy, Big Bullet, Pierce, Splitter, Homing, Explosive, Shotgun, Burst, ...).
- **Body** (green): size and toughness. Giant (huge, tanky, very slow), Tiny (small, fast, fragile), Armor Plating, Regeneration, Anchor, Rubber Body, Juggernaut, Second Wind (one revive per battle), Thorns, Adrenaline, ...
- **Movement** (blue): Double Jump, Cloud Walker, Spring Legs, Rocket Boots, Sprinter, Afterburner, Traction, Ice Skates, Glider (hold jump), Wall Runner, Parkour, Dash (on block), Jetpack (jump in mid-air and hold), Feather, Speedy.
- **Chaos** (purple): cards that change how you play. Bullet Hell (a ring of 12 bullets), Upside Down (bullets fall up), Boomerang, Ghost Rounds (through walls), Kamikaze, Recoil Jet (shooting propels you), Warp Rounds (teleport to where your bullets land), Sticky Mines, Chain Lightning, Berserk, One Shot, Wild Rounds, Pinball (you bounce off surfaces), Mind Control (bullets follow your crosshair), Leech Field, plus Vampire, Blink and Glass Cannon.

## Arenas
- The arena is 800×450. Each battle's seed picks a **layout style**:
  - *Tiers*: scattered platforms with the odd pillar.
  - *Mirror*: a symmetric layout.
  - *Islands*: floating islands over deadly pits.
  - *Towers*: solid towers rising from the floor.
- **Jump pads** (glowing plates with rising chevrons) launch you about 170 px up. Maps never rely on them to connect spawns.
- **Arena features**, also seeded (`godot/scripts/Logic/ArenaFeatures.cs`). Like pads, maps never rely on them to connect spawns, and they always stay clear of spawns:
  - *Crates* (60 HP): walk into one to push it; you can stand on them and knock them into pits. Some are stacked two high.
  - *Explosive barrels* (30 HP): pushable, and they blow up when destroyed. The blast sets off other barrels nearby.
  - *Glass*: bridges across pits and floating panes. A bullet that breaks a tile flies on through, so you can shoot the floor out from under someone.
  - *Moving platforms* glide back and forth over pits or through open air and carry whoever is standing on them.
  - *Magma pools* burn (45 damage/s) while you stand in them. *Poison pools* hurt less (16/s) but keep working for 2 s after you step out.
  - *Sawblades* slide along platform tops. Each hit deals 22 damage and throws you clear. They also chew up crates and barrels.
  - Blocking stops hazard damage too. A hazard or pit kill is credited to whoever hit the victim in the last 4 seconds.
- **Movement:** gravity is gentle and you can still jump for 0.2 s after walking off a ledge, so a missed step can be saved mid-air.
- The seed also picks one of seven **themes** (Neon Night, Sunset Peaks, Deep Reef, Toxic Grove, Magma Core, Frost Bite, Candy Clouds). Each theme sets the sky, the silhouettes, drifting motes (bokeh, embers, bubbles, snow, fireflies), platform colors and decorations (string lights, grass, seaweed, mushrooms, lava drips, icicles, sprinkles). Themes are cosmetic and identical on every peer.

## Effects that grow with your cards
Every card you hold makes your shots, hits and kills flashier. Your **power** is your card count, and it maps to tiers 0–5:
- *More cards:* longer, brighter bullet trails; more sparks and debris; bigger shockwave rings; light rays and confetti on kills; screen flashes and a camera punch at high tiers; louder kill words ("KO!" up to "VAPORIZED!!"); longer hit-stop.
- *Flavors from specific cards:*
  - Explosive: embers and smoke.
  - Homing: sparkles.
  - Vampire: red motes that fly back to you.
  - Glass Cannon: glass shards.
  - Splitter: colored shards.
  - Bouncy: ring cascades.
  - Pierce: slash streaks.
  - Feather: drifting feathers.
  - Chain Lightning: crackling sparks and lightning bolts.
  - Ghost Rounds: shimmering, see-through bullets.
- *Streaks:* kills in the same battle call out DOUBLE KILL, TRIPLE KILL and RAMPAGE.
- *Pit kills:* knocking someone into a pit credits the last player who hit them (for the effects only, not the stats).

Everything is computed from the card list every peer already has, so nothing extra goes over the network. The mapping lives in `godot/scripts/Game/FxStyle.cs`.

## Characters
- **Customize Character** (in the menu or lobby) opens the editor: body color, eyes, nose, mouth, hair, hair color, hat and hat color, plus *Randomize*. The preview follows your mouse.
- Your name, color and look are saved to `%APPDATA%\Godot\app_userdata\Brawler\profile.cfg` and sent to everyone in the lobby. Bots get a random look.
- Looks are cosmetic only. A hat is drawn above the hitbox and never takes a hit.

## Look and feel
- **Rendering:** the game renders at the window's resolution (base 800×450, `canvas_items` stretch) with 4× MSAA. All art is smooth vector shapes drawn in code, and there are no image files.
- **Glow:** HDR 2D plus a `WorldEnvironment` glow make anything drawn brighter than 1.0 bloom: bullets, neon edges, muzzle flashes and effects. Use `Shapes.Hdr(color, boost)` to make something glow.
- **Text:** a system sans font (Bahnschrift on Windows, with fallbacks) through `Fonts.Regular/Bold/Heavy`.
- **UI:** a glass-panel theme in `UiKit`, with hover bounce on buttons. `TitleText` draws the big animated headings.

## Project layout
```
godot/
  scenes/Main.tscn          the only scene; everything else is built in code
  scripts/Main.cs           wiring, screens, automated test modes
  scripts/Logic/            engine-independent rules (unit-tested): Rng, Stats, Cards, MapGen, ArenaFeatures, Sweep, Appearance,
                            StatsModel/StatsReport, Synth (sound and music)
  scripts/Core/Shared.cs    lobby/settings/input types + binary wire format; Profile.cs saves the local look
  scripts/Net/              NetworkManager (ENet host/join, lobby sync, version check), PortOpener (UPnP)
  scripts/Game/             GameManager (match flow + netcode, hit/kill effects), Avatar, AvatarArt (character + gun vector art),
                            BulletSystem, BotBrain, CardEffects, MapNode (platforms, pads, decorations), Arena (crates, glass,
                            movers, pools, saws), Backdrop (scenery),
                            ArenaTheme, FxStyle (card-driven effect power and flavors)
  scripts/Fx/               FxLayer (particles, rings, rays, flashes, popups), Visuals (shapes, glow texture, fonts), synthesized SFX
  scripts/UI/               menu, lobby, character editor, HUD, card pick, match over
tests/                      console test runner for scripts/Logic (dotnet run)
tools/CardStats/            card balance report from the stats log (dotnet run)
tools/package.ps1           export + zip release builds (see Making a release)
docs/                       player guides (INSTALL, PLAYING_ONLINE), release notes, card stats
```

### Netcode in one paragraph
The host is authoritative. Clients send one `InputFrame` per physics tick, and the host simulates every avatar, resolves hits and broadcasts a 60 Hz snapshot. Each client predicts its own movement and snaps back only if it drifts more than 24 px. Bullets are sent as **spawn events**: every peer simulates them locally, but only the host decides hits, splits and explosions. Each arena is rebuilt from a **seed**, so a map costs 4 bytes on the wire.

## Adding a card
1. Add a line to `CardRegistry` in `godot/scripts/Logic/Cards.cs`:
   ```csharp
   Add(70, "Longshot", "+80% bullet speed\n+30% damage\n+50% fire cooldown", CardCategory.Gun,
       M(Stat.BulletSpeed, 1.8f), M(Stat.Damage, 1.3f), M(Stat.FireCooldown, 1.5f));
   ```
   `A(...)` adds and `M(...)` multiplies. Adds always apply before multipliers, so pick order doesn't matter.
2. If the card needs behavior beyond stats, give it an effect id (as `"blink"` does), implement `ICardEffect` in `godot/scripts/Game/CardEffects.cs`, and register it in `CardEffects.Create`.
3. If it needs a new number, add it to the `Stat` enum (and `StatBlock.Base()` if it is not 0) in `Stats.cs`, then read it where it applies. Bullet behaviors that every peer must simulate the same way go on `Bullet` and its wire format in `BulletSystem.cs`; host-only rules (damage, revives, chain hits) go in `GameManager.HostDamage` and friends.
4. Never reuse or renumber an id: the stats log refers to cards by id.
5. Optional: give it an effect flavor in `CardFlavors` in `godot/scripts/Game/FxStyle.cs`. Every card adds power on its own, flavored or not.

## Adding a face part, hairstyle or hat
Character art is vector shapes drawn in code, so there are no image files to manage.
1. Add the option name to the end of its list in `godot/scripts/Logic/Appearance.cs` (`EyeNames`, `NoseNames`, `MouthNames`, `HairNames`, `HatNames` or `Dyes`). Saved profiles store indices, so append; don't reorder.
2. Add a `case` with the same index to the matching `Draw…` method in `godot/scripts/Game/AvatarArt.cs` (`DrawEyes`, `DrawNose`, `DrawMouth`, `DrawHair`, `DrawHat`).
   - Shapes come from `Shapes` (`Ellipse`, `RoundRect`, `Capsule`, `Star`, or a point list). Draw them with `Shapes.Fill(ci, points, fill, outline, width)`.
   - Use `c.Hair` / `c.Hat` for dye colors, and wrap colors in `c.H(...)` so they flash white when hit.
   - For hair and hats, also add the shape's top y to `HairTop` / `HatTop`, so the HP bar clears it.
3. Check the result with `--menushots` (see Tests). It saves a gallery page for every part.

Sprite space faces right with the body centered on (0,0): x −7..7, y −9..9 (14×18, feet at the bottom). The gun is built from `GunLook` parameters (receiver, barrel, magazine, scope, stock). Card-specific guns can be added later by giving each avatar a different `GunLook`.

## Adding an arena theme
Add an `ArenaTheme` to `ArenaTheme.All` in `godot/scripts/Game/ArenaTheme.cs`. It sets three sky colors, the platform and edge colors, a skyline (`City`, `Mountains`, `Hills`, `Spires`), motes and a decoration. A new `Deco` or `Motes` kind needs a case in `MapNode.DrawDeco` or `Backdrop.DrawMotes`. Adding a theme changes which theme existing seeds get, which is fine because themes are cosmetic.

## Card balance stats
The host records every match to a log so card balance can be tuned from data instead of feel. Full details, including the log schema, metric formulas and how to extend it, are in [docs/card-stats.md](docs/card-stats.md).
- **Where:** `%APPDATA%\Godot\app_userdata\Brawler\stats\card_stats.jsonl`. Each line is one match in JSON. Change the folder with `--statsdir=PATH`.
- **What:** for every battle it stores each player's cards (stacks included), the fight length, the winner, shots, hits, damage dealt, taken, self-inflicted and healed, blocks, kills, and when and how each player died. For every card pick it stores what was offered, what was picked, who picked it (human or bot, round winner or not), how long the decision took, and what that player already held. Each record is tagged with its mode (`offline`, `host` or `sim`) and a hash of the card table, so changing a card's numbers starts a fresh balance version.
- Only the host records. A match you leave early is still saved, flagged `complete: false`. Automated tests don't record unless you pass `--stats`.

**Generate data with bots.** A 4-player match takes about 5 seconds headless:
```bash
GODOT=".../Godot_v4.7.2-stable_mono_win64_console.exe"
"$GODOT" --headless --path godot --fixed-fps 60 -- --simulate=200            # 2–4 players per match
"$GODOT" --headless --path godot --fixed-fps 60 -- --simulate=200 --players=4
```

**Read the report.** It gives win ratio, match win ratio, win time, damage, kills, survival, accuracy, pick ratio, stacking and pair synergies:
```bash
cd tools/CardStats && dotnet run                      # markdown report to the console
dotnet run -- --out=report.md --csv=out               # save the report plus cards.csv and pairs.csv for a spreadsheet
dotnet run -- --humans                                # only human players (what real play looks like)
dotnet run -- --mode=sim --cards=all --min=50         # filter by mode, include old card versions, raise the sample threshold
```
How to read it:
- **Win ratio** is wins divided by a fair share, where the fair share is 1/players per battle. 1.00 means balanced. The ± is a 95% interval, and the verdict flags a card only when that interval is clearly off 1.0.
- **Pick ratio** is picked divided by random choice. Bots pick at random, so use the *Human pick* column to see what players actually like.
- **Synergy** is a pair's win ratio minus what the two cards would predict on their own. Positive means the combo is stronger than its parts.
- Bot data shows raw power. Human data shows power once players aim and pick on purpose. Compare the two.

## Tests
```bash
cd tests && dotnet run
```
Covers RNG determinism, stat stacking order, card registry (12+ cards per category, every card keeps core stats sane, clamps on stacked cards), map generation (determinism, spawn reachability, fallback rate across 9,000 seeds, every layout style appearing, jump pads placed on surfaces away from spawns), arena features (determinism, clear of solids, spawns, pads and each other across 2,000 seeds), swept collision, card stats aggregation, join-address parsing, and character looks (wire and profile round-trips, option wrapping).

End-to-end autopilot runs (the local player is driven by a bot; the process exits with code 0 when a full match completes):
```bash
GODOT=".../Godot_v4.7.2-stable_mono_win64_console.exe"
"$GODOT" --headless --path godot --fixed-fps 60 -- --autotest          # offline, 4 players
"$GODOT" --headless --path godot -- --autohost=24600 &                 # networked: host + bot...
"$GODOT" --headless --path godot -- --autojoin=127.0.0.1:24600         # ...plus a joining client
"$GODOT" --path godot --fixed-fps 60 -- --autotest --shots=C:/tmp/shots # windowed, saves screenshots
"$GODOT" --path godot -- --shots=C:/tmp/shots --menushots              # menu, editor, lobby + a gallery page per look part
"$GODOT" --path godot -- --shots=C:/tmp/shots --fxshots                # hit + kill effects at each power tier (0 to 12 cards)
"$GODOT" --headless --path godot --fixed-fps 60 -- --autotest --cards=random:5  # every battle deals 5 random cards to each player
"$GODOT" --path godot --fixed-fps 60 -- --autotest --cards=57,62,69    # everyone holds these card ids (offline only)
"$GODOT" --path godot -- --themebench                                  # frame time per arena theme (vsync off)
```
