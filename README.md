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

## Project layout
```
godot/
  scenes/Main.tscn          the only scene; everything else is built in code
  scripts/Main.cs           wiring, screens, automated test modes
  scripts/Logic/            engine-independent rules (unit-tested): Rng, Stats, Cards, MapGen, Sweep
  scripts/Core/Shared.cs    lobby/settings/input types + binary wire format
  scripts/Net/              NetworkManager: ENet host/join, lobby sync
  scripts/Game/             GameManager (match flow + netcode), Avatar, BulletSystem, BotBrain, CardEffects, MapNode
  scripts/Fx/               particles/rings/popups, procedurally synthesized SFX
  scripts/UI/               menu, lobby, HUD, card pick, match over
tests/                      console test runner for scripts/Logic (dotnet run)
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

## Tests
```bash
cd tests && dotnet run
```
Covers RNG determinism, stat stacking order, card registry, map generation (determinism, spawn reachability, fallback rate across 9,000 seeds) and swept collision.

End-to-end autopilot runs (the local player is driven by a bot; the process exits with code 0 when a full match completes):
```bash
GODOT=".../Godot_v4.7.2-stable_mono_win64_console.exe"
"$GODOT" --headless --path godot --fixed-fps 60 -- --autotest          # offline, 4 players
"$GODOT" --headless --path godot -- --autohost=24600 &                 # networked: host + bot...
"$GODOT" --headless --path godot -- --autojoin=127.0.0.1:24600         # ...plus a joining client
"$GODOT" --path godot --fixed-fps 60 -- --autotest --shots=C:/tmp/shots # windowed, saves screenshots
```
