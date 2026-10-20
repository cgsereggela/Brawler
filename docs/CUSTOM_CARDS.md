# Making your own cards

You can add your own cards to Brawler without any programming. A card is a small text file that says what the card
is called and which numbers it changes, like "+3 bullet bounces" or "half gravity". The game reads these files when it
starts, and your cards show up in the card picks next to the built-in ones, with a blue **CUSTOM** tag.

## 1. Find the cards folder

The game makes the folder the first time it runs. It is in your Brawler settings folder:

| System  | Cards folder |
|---------|--------------|
| Windows | `%APPDATA%\Godot\app_userdata\Brawler\cards` (paste this into the File Explorer address bar) |
| macOS   | `~/Library/Application Support/Godot/app_userdata/Brawler/cards` |
| Linux   | `~/.local/share/godot/app_userdata/Brawler/cards` |

Put one or more files ending in `.json` in that folder, then start (or restart) the game. Files are read in
alphabetical order. Other files are ignored.

The release zip has a `custom-cards` folder with this guide and two example files. Copy the example `.json` files into
your cards folder to try them out.

## 2. Write a card

Open a text editor (Notepad is fine), paste this in and save it as `cards\my-cards.json`:

```json
{
  "id": 1000,
  "name": "Bounce House",
  "description": ["+3 bullet bounces", "Bigger bullets", "-15% damage"],
  "category": "Gun",
  "mods": [
    { "stat": "Bounces", "op": "add", "value": 3 },
    { "stat": "BulletRadius", "op": "mul", "value": 1.3 },
    { "stat": "Damage", "op": "mul", "value": 0.85 }
  ]
}
```

To put several cards in one file, wrap them in square brackets with commas between them:

```json
[
  { "id": 1001, "name": "Moon Boots", "category": "Movement",
    "description": ["Half gravity", "+1 mid-air jump", "-10% HP"],
    "mods": [
      { "stat": "Gravity", "op": "mul", "value": 0.5 },
      { "stat": "AirJumps", "op": "add", "value": 1 },
      { "stat": "MaxHp", "op": "mul", "value": 0.9 }
    ] },
  { "id": 1003, "name": "Phase Step", "category": "Chaos", "effect": "blink",
    "description": ["Blocking teleports you", "far toward your cursor", "-30% block cooldown"],
    "mods": [
      { "stat": "TeleportOnBlock", "op": "add", "value": 1.5 },
      { "stat": "BlockCooldown", "op": "mul", "value": 0.7 }
    ] }
]
```

### The fields

| Field | Needed? | What it is |
|-------|---------|------------|
| `id` | yes | A whole number from **1000 to 32767**, different for every card. Numbers below 1000 belong to the built-in cards. |
| `name` | yes | The card's title, up to 24 letters. |
| `description` | no | The text on the card. Either one piece of text (use `\n` for a new line) or a list of lines as in the example. Up to 160 letters; about four short lines fit. The game does not write this for you, so make it match your mods. |
| `category` | no | `Gun`, `Body`, `Movement` or `Chaos`. It sets the card's colour and icon. Default: `Chaos`. |
| `mods` | see below | The list of stat changes. Each one is `{ "stat": ..., "op": ..., "value": ... }`. At most 12. |
| `effect` | no | A special behaviour that numbers alone can't do (see [Effects](#effects)). |

A card needs at least one mod or an effect. Upper or lower case doesn't matter in field names, stat names, `op` or
`category`. You may write `// comments` and leave a comma after the last item.

### How mods work

- `"op": "add"` adds the value to the stat. `{ "stat": "MaxHp", "op": "add", "value": 40 }` is +40 HP. Use a minus
  number to take away: `"value": -10`.
- `"op": "mul"` multiplies the stat. `1.3` is +30%, `0.8` is -20%, `2` is double.

When a player holds several cards, the game first applies every `add` from all their cards, then every `mul`. So the
order you pick cards in never matters. Holding the same card twice applies it twice.

Many stats start at 0 and only do something once a card adds to them, for example `Bounces` or `Homing`. Multiplying a
stat that is 0 leaves it at 0, so use `add` for those.

## 3. Stats

The **Base** column is what every player starts with. Speeds are in pixels per second, times in seconds.

### Player

| Stat | Base | Meaning |
|------|------|---------|
| `MaxHp` | 100 | Health. Never goes below 10. |
| `MoveSpeed` | 144 | Running speed (50 to 400). |
| `JumpVelocity` | 326.5 | Jump strength (168 to 805). |
| `Gravity` | 650 | How fast you fall (at least 150). Lower means floatier jumps. |
| `Size` | 1 | Body size (0.5 to 2.5). Bigger is easier to hit. |
| `BlockCooldown` | 4 | Seconds between blocks (at least 0.6). 999 means you can't block. |
| `BlockDuration` | 0.3 | How long a block lasts. |
| `Lifesteal` | 0 | Fraction of the damage you deal that heals you. 0.35 = 35%. |
| `TeleportOnBlock` | 0 | Teleport distance for the `blink` effect: 112 pixels per point. |

### Weapon

| Stat | Base | Meaning |
|------|------|---------|
| `FireCooldown` | 0.3 | Seconds between shots (at least 0.05). Lower fires faster. |
| `Ammo` | 3 | Shots before you reload (1 to 40). |
| `ReloadTime` | 1.5 | Seconds to reload (at least 0.2). |
| `Projectiles` | 1 | Bullets per shot (1 to 24). |
| `Spread` | 0 | Angle the bullets fan out over, in radians. 6.28 is a full circle. |
| `BurstCount` | 1 | Shots fired in a quick burst per trigger pull (1 to 12). |
| `BurstInterval` | 0.07 | Seconds between the shots of a burst. |

### Bullet

| Stat | Base | Meaning |
|------|------|---------|
| `BulletSpeed` | 380 | Bullet speed (60 to 1200). |
| `BulletGravity` | 320 | How much bullets drop. Negative makes them fall upward. |
| `Damage` | 25 | Damage per bullet (at least 1). |
| `BulletRadius` | 2 | Bullet size (1 to 14). |
| `Bounces` | 0 | Times a bullet bounces off walls. |
| `Pierce` | 0 | Extra players a bullet passes through. |
| `Homing` | 0 | How hard bullets curve toward enemies. |
| `ExplosionRadius` | 0 | Bullets explode on impact with this radius. |
| `SplitCount` | 0 | Bullets split into this many on impact (up to 12). |
| `Knockback` | 140 | How hard a hit pushes the victim. |
| `Lifetime` | 4 | Seconds a bullet lives (0.5 to 8). |

### Movement

| Stat | Base | Meaning |
|------|------|---------|
| `AirJumps` | 0 | Extra jumps in mid-air (up to 5). |
| `Accel` | 1 | Multiplier on acceleration (0.2 to 4). Low is slippery. |
| `WallJump` | 1 | Multiplier on wall-jump height; higher also slows wall slides. |
| `Glide` | 0 | 1 = holding jump while falling lets you glide. |
| `Jetpack` | 0 | Seconds of jetpack thrust per landing (jump in mid-air and hold). |
| `Dash` | 0 | Dash strength for the `dash` effect. |

### Body

| Stat | Base | Meaning |
|------|------|---------|
| `Armor` | 0 | Fraction of incoming damage ignored (up to 0.75). |
| `Regen` | 0 | HP healed per second. |
| `KnockbackTaken` | 1 | Multiplier on knockback you receive (0.1 to 4). |
| `ExtraLives` | 0 | Times per battle you survive a lethal hit (at half HP). |
| `Thorns` | 0 | Fraction of damage taken that is dealt back to the attacker (up to 1.5). |
| `Adrenaline` | 0 | Bonus to move speed and fire rate while under 40% HP. 0.5 = +50%. |

### Chaos

| Stat | Base | Meaning |
|------|------|---------|
| `Recoil` | 0 | Firing pushes you backward this hard. |
| `Boomerang` | 0 | 1 = bullets fly back to you. |
| `Phase` | 0 | 1 = bullets pass through walls. |
| `Sticky` | 0 | Seconds a bullet sits as a mine after hitting a wall. |
| `Guided` | 0 | How hard bullets steer toward your crosshair. |
| `Chain` | 0 | Fraction of hit damage that arcs to the next enemy. |
| `Berserk` | 0 | Extra damage as your HP drops, up to this fraction. 1.5 = up to +150%. |
| `Wild` | 0 | 1 = every bullet gets a random size, speed and damage. |
| `Leech` | 0 | Damage per second to enemies near you, healing you. |
| `Kamikaze` | 0 | Explosion radius when you die. |
| `Pinball` | 0 | 1 = your body bounces off walls, floors and ceilings. |
| `Warp` | 0 | 1 = you teleport to where your bullets land. |

## Effects

An effect adds behaviour that needs code. Set `"effect"` to one of these, and add the stat it reads, or it does
nothing:

| Effect | What it does | Needs |
|--------|--------------|-------|
| `blink` | Blocking teleports you toward your cursor, stopping at walls. | `TeleportOnBlock` (add 1 = 112 pixels) |
| `dash` | Blocking dashes you toward your cursor. | `Dash` (the built-in Dash card adds 650) |

## 4. Rules and limits

- **Ids** must be whole numbers from 1000 to 32767. If two cards share an id, the first one read wins (files go in
  alphabetical order) and the other is skipped with a warning.
- At most **200** custom cards and **12 mods** per card.
- Values that are far too big are **clamped** (cut down to a limit) and the log says so. `mul` values are kept
  between -10 and 10. `add` values are kept within ±1000, with tighter limits for stats where big numbers would break
  the game: for example `Projectiles` ±24, `SplitCount` and `BurstCount` ±12, `Bounces` ±20, `Pierce` ±10,
  `AirJumps` ±5, `Ammo` ±40, `ExtraLives` ±3, `Size` ±2, `ExplosionRadius` ±200, `Kamikaze` ±300, `Armor` ±0.75.
  On top of that, the player's final stats stay within the ranges in the tables above, however many cards they hold.
- A card with a mistake (an unknown stat, a missing name, a broken file and so on) is skipped. The other cards still
  load, and the game never crashes over a card file.
- **Multiplayer: the host's cards are used.** When you join someone's game, your own custom cards are put away and
  you play with exactly the host's custom cards, so everyone's stats agree. You don't need to install their files.
  Your own cards come back when you return to the menu. When you host, everyone plays with your cards.

## 5. Checking for mistakes

Each time the game starts it writes what it loaded to its log, `godot.log` in the `logs` folder next to your `cards`
folder (for example `%APPDATA%\Godot\app_userdata\Brawler\logs\godot.log`). Look for lines starting with
`[Brawler] custom cards:`:

```
[Brawler] custom cards: my-cards.json: card #2 'Moon Boots' (id 1001): mod #1: unknown stat "Gravty", skipped
[Brawler] custom cards: my-cards.json: card #3 'Hail' (id 1004): Projectiles add 500 is out of range, clamped to 24
[Brawler] custom cards: loaded 2 from 1 file(s) in C:/Users/you/AppData/Roaming/Godot/app_userdata/Brawler/cards [set 1a2b3c4d]: Bounce House (1000), Hail (1004)
```

Each line names the file, the card's position in the file, its name and id, and the problem. When you join a game the
log also says `using the host's N custom card(s)`. The `[set ...]` code is a fingerprint of the whole set. If two
players' logs show the same code, they have the same cards.

If the file isn't valid JSON at all (a missing comma or quote is the usual cause), the log says `not valid JSON` with
the line number. A JSON checker website can point at the exact spot.

## 6. Building from source

If you build the game yourself you can also add cards in code, and add new effects.

**A built-in card.** Add a line to the static constructor in `godot/scripts/Logic/Cards.cs`, using the next free id
below 1000. Never renumber existing ids; saved stats and network messages refer to them.

```csharp
Add(70, "Pogo", "+2 mid-air jumps\n-10% HP", CardCategory.Movement,
    A(Stat.AirJumps, 2), M(Stat.MaxHp, 0.9f));
```

`A(...)` is an add and `M(...)` a multiply. Put an effect id before the mods, as Blink and Dash do. Optionally give it a
visual flavour in `Game/FxStyle.cs`. Run the unit tests with `dotnet run --project tests`.

**A new stat.** Add it to the end of the `Stat` enum in `Logic/Stats.cs`, give it a base value in `StatBlock.Base()`
if it isn't 0 and a clamp in `Clamp()` if it needs one, and read it where it matters (`Avatar.S[Stat.X]` in
`Game/Avatar.cs` or `Game/BulletSystem.cs`). Add an `add` limit for it in `CustomCards.AddLimits` if big values would
break things. Custom card files can then use it by name.

**A new effect.** In `godot/scripts/Game/CardEffects.cs`, write a class that implements `ICardEffect` (hooks:
`OnBlock`, `OnDealDamage`, `OnBattleStart`), register its id in `CardEffects.Create`, and add the same id to
`CustomCards.EffectIds` in `Logic/CustomCards.cs` so card files may use it. If you need a new hook, add it to
`ICardEffect` and call it from the right place in `Avatar` or `GameManager`. Effects run on the host only.
