# Brawler

A ROUNDS-style 2–4 player platform shooter: lose a round, pick a card, come back stronger.
Godot 4.7 (.NET / C#), LAN or direct-IP multiplayer, bots, procedurally generated arenas.

## Run it

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

### Playing with friends (LAN / direct IP)
- **Host:** Menu → *Host LAN Game*. The lobby shows your local IP(s) and the port (default `24565`).
- **Join:** Enter the host's IP and port and click *Join*.
- Over the internet, the host must forward UDP port 24565, or you can use a virtual LAN such as Tailscale, ZeroTier or Radmin VPN.
- **Test alone:** in Godot use *Debug → Customize Run Instances → 2*, host in one window and join `127.0.0.1` in the other.

## Rules
- A **battle** ends when one player is left standing. Win *N battles* to take a **round**, and win *M rounds* to take the **match**. Both numbers are set in the lobby.
- After every round, **everyone picks a card**. Losers choose from more options (default 5) than the winner (default 3, or 0 to skip).
- Each battle gets a newly generated arena.

## Characters
- **Customize Character** (in the menu or lobby) opens the editor: body color, eyes, nose, mouth, hair, hair color, hat and hat color, plus *Randomize*. The preview follows your mouse.
- Your name, color and look are saved to `%APPDATA%Godotpp_userdataBrawlerprofile.cfg` and sent to everyone in the lobby. Bots get a random look.
- Looks are cosmetic only. A hat is drawn above the hitbox and never takes a hit.

## Project layout
```
godot/
  scenes/Main.tscn          the only scene; everything else is built in code
  scripts/Main.cs           wiring, screens, automated test modes
  scripts/Logic/            engine-independent rules (unit-tested): Rng, Stats, Cards, MapGen, Sweep, Appearance, StatsModel/StatsReport
  scripts/Core/Shared.cs    lobby/settings/input types + binary wire format; Profile.cs saves the local look
  scripts/Net/              NetworkManager: ENet host/join, lobby sync
  scripts/Game/             GameManager (match flow + netcode), Avatar, AvatarArt (character + gun pixel art), BulletSystem, BotBrain, CardEffects, MapNode
  scripts/Fx/               particles/rings/popups, procedurally synthesized SFX
  scripts/UI/               menu, lobby, character editor, HUD, card pick, match over
tests/                      console test runner for scripts/Logic (dotnet run)
tools/CardStats/            card balance report from the stats log (dotnet run)
```

### Netcode in one paragraph
The host is authoritative. Clients send one `InputFrame` per physics tick, and the host simulates every avatar, resolves hits and broadcasts a 60 Hz snapshot. Each client predicts its own movement and snaps back only if it drifts more than 24 px. Bullets are sent as **spawn events**: every peer simulates them locally, but only the host decides hits, splits and explosions. Each arena is rebuilt from a **seed**, so a map costs 4 bytes on the wire.

## Adding a card
1. Add a line to `CardRegistry` in `godot/scripts/Logic/Cards.cs`:
   ```csharp
   Add(17, "Sniper", "+80% bullet speed\n+30% damage\n+50% fire cooldown", CardCategory.Weapon,
       M(Stat.BulletSpeed, 1.8f), M(Stat.Damage, 1.3f), M(Stat.FireCooldown, 1.5f));
   ```
   `A(...)` adds and `M(...)` multiplies. Adds always apply before multipliers, so pick order doesn't matter.
2. If the card needs behavior beyond stats, give it an effect id (as `"blink"` does), implement `ICardEffect` in `godot/scripts/Game/CardEffects.cs`, and register it in `CardEffects.Create`.
3. If it needs a new number, add it to the `Stat` enum and `StatBlock.Base()` in `Stats.cs`, then read it where it applies.
4. Never reuse or renumber an id: the stats log refers to cards by id.

## Adding a face part, hairstyle or hat
Character art is drawn in code from small pixel maps, so there are no image files to manage.
1. Add the option name to the end of its list in `godot/scripts/Logic/Appearance.cs` (`EyeNames`, `NoseNames`, `MouthNames`, `HairNames`, `HatNames` or `Dyes`). Saved profiles store indices, so append; don't reorder.
2. Add the art at the same index in `godot/scripts/Game/AvatarArt.cs`. Noses, mouths, hair and hats are `PixelArt` strings: one string per row, one char per pixel, `.` empty. `#`, `l` and `d` are the part's color (hair or hat dye) with light and dark shades; the other keys are listed in `Pal`. With `true` as the third argument, a dark outline is added automatically. Eyes are drawn in `DrawEyes` because their pupils follow the aim.
3. Check the result with `--menushots` (see Tests). It saves a gallery page for every part.

Sprite space faces right with the body centered on (0,0): x −7..6, y −9..8 (14×18, feet in the last two rows). The gun is built from `GunLook` parameters (receiver, barrel, magazine, scope, stock). Card-specific guns can be added later by giving each avatar a different `GunLook`.

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
Covers RNG determinism, stat stacking order, card registry, map generation (determinism, spawn reachability, fallback rate across 9,000 seeds), swept collision, card stats aggregation, and character looks (wire and profile round-trips, option wrapping).

End-to-end autopilot runs (the local player is driven by a bot; the process exits with code 0 when a full match completes):
```bash
GODOT=".../Godot_v4.7.2-stable_mono_win64_console.exe"
"$GODOT" --headless --path godot --fixed-fps 60 -- --autotest          # offline, 4 players
"$GODOT" --headless --path godot -- --autohost=24600 &                 # networked: host + bot...
"$GODOT" --headless --path godot -- --autojoin=127.0.0.1:24600         # ...plus a joining client
"$GODOT" --path godot --fixed-fps 60 -- --autotest --shots=C:/tmp/shots # windowed, saves screenshots
"$GODOT" --path godot -- --shots=C:/tmp/shots --menushots              # menu, editor, lobby + a gallery page per look part
```
