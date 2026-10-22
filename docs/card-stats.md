# Card balance stats

Brawler records every match so that card balance can be tuned from data. This page covers where the data comes from, what is stored, how the report computes its numbers, and how to extend it.

## Pipeline

```
GameManager (host) ──events──▶ StatsRecorder ──1 JSON line per match──▶ card_stats.jsonl
                                                                             │
tools/CardStats (dotnet run) ◀── StatsJson.ParseLines + StatsReport.Build ◀──┘
        └──▶ markdown report, cards.csv, pairs.csv
```

| Piece | File | Role |
|---|---|---|
| Recorder | `godot/scripts/Game/StatsRecorder.cs` | Collects events during a match and appends the finished record to the log. Runs on the host only. |
| Hooks | `godot/scripts/Game/GameManager*.cs` (mostly `.Flow` and `.Combat`) | Calls `Stats.*` at match, battle and pick boundaries, and on shots, damage, blocks and deaths. |
| Record model | `godot/scripts/Logic/StatsModel.cs` | `MatchRecord` and its child records, JSON options, and the card-table hash. Has no Godot dependency. |
| Aggregation | `godot/scripts/Logic/StatsReport.cs` | Builds per-card and per-pair tallies, then writes markdown and CSV. Has no Godot dependency and is unit-tested in `tests/`. |
| CLI | `tools/CardStats/Program.cs` | Reads the log, applies filters, writes the report. |

## When and where data is written

- **Location:** `user://stats/card_stats.jsonl`, which on Windows is `%APPDATA%\Godot\app_userdata\Brawler\stats\`. Override it with `--statsdir=PATH`.
- **Format:** JSON Lines, one complete match per line, appended. Delete or rename the file to start over. The reader skips any line it can't parse, such as a line cut short by a crash.
- **Who writes:** only the host, since it is the authority for hits and picks. Clients write nothing.
- **When:**

| Situation | What is written |
|---|---|
| Match finishes normally | `complete: true`, `winner` = slot |
| Host leaves mid-match, a peer leaves and fewer than 2 players remain, or the window is closed | `complete: false`, including every battle that finished |
| A battle still in progress at that moment | Dropped |
| A match with no finished battles | Nothing is written |

- **Automated tests:** `--autotest`, `--autohost` and `--autojoin` don't record, so the log stays clean. Add `--stats` to record anyway.
- **`--simulate=N [--players=A-B]`:** plays N bot-only matches with the lobby's default rules and records them with `mode: "sim"`. A match takes about 5 seconds headless. A simulated match that runs past 30 simulated minutes is abandoned and saved as incomplete.

## Record schema (version 4)

```jsonc
{
  "v": 4,                        // MatchRecord.CurrentVersion (2 added rare cards and cursed deals, 3 the team size, 4 the game mode)
  "id": "c1792a05b491",
  "startedUtc": "2026-10-19T03:28:40Z",
  "mode": "sim",                 // "offline" | "host" | "sim"
  "cardsHash": "baf58c9a",       // rules version: every card's id/name/effect/mods, the base stats, the body size and the arena
  "complete": true,
  "winner": 2,                   // match winner slot, -1 if none
  "durationSec": 446.05,         // simulated time, including countdowns and picks
  "settings": { "battlesPerRound": 2, "roundsToWin": 5, "loserChoices": 5, "winnerChoices": 3,
                "rareChance": 10, "cursedDeals": true,
                "teamSize": 0,           // 0 = free-for-all; team matches (2-4 a side) are left out of the report
                "gameMode": "elimination" },  // or "koth", "ctf", "headhunter": only elimination goes into the report
  // in a team match, "winner" (match and battle) is a slot standing for its whole team
  "players": [
    { "slot": 0, "name": "Sim", "bot": true, "finalCards": [11, 3, 13], "roundWins": 0, "left": false }
  ],
  "battles": [
    {
      "n": 1, "round": 1, "seed": -1482189222,
      "fightSec": 15.88,         // from "FIGHT!" until the winner was decided
      "winner": 1,               // -1 = draw
      "wonRound": false,         // this battle win also took the round
      "players": [
        {
          "slot": 0, "cards": [],          // cards held this battle; duplicates are stacks
          "shots": 20,                     // bullets fired by the weapon (split fragments not counted)
          "hits": 4,                       // damaging hits on others (includes splits and explosions)
          "damageDealt": 100, "damageTaken": 100, "selfDamage": 0,   // capped at the victim's remaining HP
          "healed": 0,                     // lifesteal actually gained
          "blocksUsed": 2, "hitsBlocked": 0,
          "kills": 0,
          "diedAt": 15.55,                 // fight seconds, -1 = survived
          "hpLeft": 0
        }
      ],
      "kills": [ { "t": 8.1, "victim": 3, "killer": 1, "cause": "shot" } ]  // killer = last damager; cause "shot" | "fall"
    }
  ],
  "picks": [
    {
      "round": 1,                // rounds completed so far
      "slot": 0, "bot": true, "roundWinner": false,
      "timedOut": false,         // pick timer expired and a random card was taken
      "decisionSec": 0.9,
      "offered": [4, 9, 1, 13, 7], "picked": 13,
      "heldBefore": []
    }
  ]
}
```

Notes:
- `bot` is also true for a local player on autopilot. A remote client running `--autojoin` shows up as human, because the host can't tell.
- A player who disconnects is not counted as a death in `kills`.
- In King of the Hill and Capture the Flag players respawn, so one player can die several times in a battle: `kills` lists every death, and `diedAt` holds the last.
- In `selfDamage`, explosive bullets damage their owner at half strength. That damage is counted here and not as dealt damage.

## How the report computes its numbers

Every per-card figure covers **player-battles in which the card was held**. A player holding two copies counts once, and the copy count is reported separately under *Stacking*.

| Metric | Definition |
|---|---|
| **Win ratio** | wins ÷ Σ(1/k), where k is the number of players in each battle. 1.00 = a fair share. Draws are left out. |
| **±** | 95% interval: 1.96·√Σ p(1−p) ÷ Σp, with p = 1/k |
| **Verdict** | `strong`: the lower bound is above 1.10. `strong?`: the lower bound is above 1.0. The weak labels mirror these. `few samples`: fewer than `--min` battles (default 30). |
| **Match win** | Same as win ratio, but per complete match, using each player's final cards |
| **Win time** | Average `fightSec` of the battles the holder won |
| **Dmg dealt / taken, Kills, Survive** | Per-battle averages. Survive = `diedAt`, or the full fight length if the player lived. |
| **Acc** | hits ÷ shots. Splits and explosions can push this up, and Shotgun's extra pellets pull it down. |
| **Pick ratio** | picked ÷ Σ(1/options) over every offer of the card. 1.00 = chosen as often as chance. Timed-out picks are skipped. |
| **Human pick** | Pick ratio using only human picks. Bots pick at random, so this is the column that shows preference. |
| **Avg round** | Average round number at which the card was picked |
| **Synergy** | pair win ratio − (ratio A + ratio B − 1), for pairs held together in at least `--min` battles. Positive = the combo beats the sum of its parts. |

Caveats when reading the data:
- Bot data measures raw power when the AI plays the card. It's biased by how well the AI uses it. For example, Homing makes up for poor bot aim.
- Every player gets a pick each round, so card counts stay roughly even. Setting `winnerChoices: 0` breaks that, since winners then hold fewer cards.
- Cards picked late are held in fewer battles, so their numbers come with wider intervals.

## Report CLI

```bash
cd tools/CardStats
dotnet run                                   # default log, current card table, markdown to stdout
dotnet run -- path/to/log.jsonl              # a specific log
dotnet run -- --humans | --bots              # filter players
dotnet run -- --mode=sim|host|offline        # filter matches
dotnet run -- --cards=current|all|<hash>     # card-table version (default: current)
dotnet run -- --min=50                       # sample threshold for verdicts and pairs
dotnet run -- --out=out/report.md --csv=out  # write files (out/ is git-ignored)
```

## Changing or extending it

- **Card ids are permanent.** The log refers to cards by id, so never renumber or reuse one.
- **Balance changes** alter `cardsHash` automatically. Despite the name, it also covers the base stats in `StatBlock.Base()`, the body size in `Body` (`Appearance.cs`), and the arena size and jump pad strength (`MapLayout`, `MapGenerator`), since all of them shift every card's numbers. The report then shows only the new version by default. Use `--cards=all` or a specific hash to compare versions.
- **To add a field:**
  1. Add the property to the record class in `StatsModel.cs`.
  2. Fill it in `StatsRecorder` from a hook in `GameManager`.
  3. Aggregate it in `StatsReport.Build`.

  Old lines still parse, with the new field at its default value. If the meaning of an existing field changes, bump `MatchRecord.CurrentVersion`.
- Damage and kill events come only from host-side `GameManager` methods: `HostDamage`, `HostKill`, `HostSpawnBullet` and `HostOnBlock`. New damage sources should go through `HostDamage` so they are counted.
