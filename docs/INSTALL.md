# Brawler {VERSION}: install and play

Brawler is a 2–4 player platform shooter. Lose a battle, pick a card, come back stronger.
Play against bots on your own, with friends on the same Wi-Fi, or with friends across the internet
(see **Playing online** below).

## 1. Download

1. Go to <https://github.com/cgsereggela/Brawler/releases/latest>.
2. Under **Assets**, download the zip for your computer:

   | Your computer | Download |
   |---|---|
   | Windows 10/11 (64-bit) | `Brawler-<version>-windows.zip` |
   | macOS 11 or newer (Intel or Apple Silicon) | `Brawler-<version>-macos.zip` |
   | Linux (64-bit) | `Brawler-<version>-linux.zip` |

You don't need to install .NET, Godot or anything else. Everything the game needs is in the zip.

> **Everyone you play with needs the same version.** The version is shown in the bottom-right corner of the
> main menu. If it doesn't match, joining fails with "Version mismatch". Download the same release as your friends.

## 2. Install and run

### Windows
1. Right-click the zip, choose **Extract All...**, then click **Extract**. Don't run the game from inside the zip.
2. Open the extracted folder and double-click **Brawler.exe**.
3. If Windows shows "Windows protected your PC", click **More info**, then **Run anyway**. The game isn't
   code-signed (that costs money each year), so Windows doesn't recognise it yet.
4. The first time you host a game, Windows Firewall asks whether Brawler may use the network. Tick
   **both Private and Public networks**, then click **Allow**. (Missed it? See *Firewall* in the online guide.)

Keep `Brawler.exe` and the `data_Brawler_windows_x86_64` folder together. The game needs both.

### macOS
1. Double-click the zip to unpack it, and drag **Brawler.app** into **Applications**.
2. The first time, macOS blocks it because it isn't notarised by Apple. Open it anyway:
   - Double-click Brawler and dismiss the warning.
   - Open **System Settings → Privacy & Security**, scroll down, and click **Open Anyway** next to the Brawler message.
   - Confirm with your password.

   Or, in Terminal: `xattr -cr /Applications/Brawler.app` and then open it normally.
3. When you host, allow "incoming network connections" if macOS asks.

### Linux
```bash
unzip Brawler-*-linux.zip -d Brawler && cd Brawler
chmod +x Brawler.x86_64
./Brawler.x86_64
```
Keep the `data_Brawler_linuxbsd_x86_64` folder next to the binary. A GPU with Vulkan drivers is best. The game
switches to OpenGL by itself if Vulkan isn't available.

## 3. Play

| Action | Keys (change them in Options → Controls) |
|---|---|
| Move | A / D (or arrows) |
| Jump / wall jump | W / Space / Up. If you walk or get knocked off a ledge without jumping, you keep that jump and can use it any time while falling. |
| Shoot | Left mouse (hold) |
| Block | Right mouse / Shift |
| Options during a match | Esc. They open over the game, which keeps running; your character stands still while they're open. |
| Leave match | Esc, then **Leave match**, then **Yes** |
| Windowed ⇄ full screen | Alt+Enter |

- **Play vs Bots:** you against bots. Add up to three in the lobby.
- **Host Game:** opens a lobby that friends can join. You can fill empty slots with bots.
- **Join a Friend:** type the host's address (for example `192.168.1.20`, or `203.0.113.7:24565` with a port) and click **Join**.
  The game remembers the last address you used.

Win battles to take a round, and win rounds to take the match. After every round everyone picks a card,
and players who lost get more cards to choose from.

The host decides which cards can come up: click **Cards** in the lobby and untick the ones you don't want
(at least 7 must stay in). Your choice is remembered next time you host. Friends can open it to see the pool.

### Your own cards
You can make your own cards with a small text file, no programming needed. The `custom-cards` folder in the zip
has a guide (`CUSTOM_CARDS.md`) and examples. Put card files in the `cards` folder inside the settings folder
listed under **Updating** below. When you host, your friends play with your cards automatically.

## Options

Open **Options** from the main menu or the lobby, or press **Esc** during a match. Every change applies at once and is saved.

- **Audio:** master, music and sound-effect volume.
- **Video → Window:** *Windowed* (a normal window), *Borderless* (fills the screen without a frame, alt-tabs instantly) or
  *Fullscreen* (exclusive full screen, can be a little smoother but is slower to alt-tab). Alt+Enter switches between
  windowed and your last full-screen choice.
- **Video → Bloom:** how strongly bright things glow (shots, explosions, lava, neon edges). Default 50%. 100% is the full
  glow and 0% turns it off, which also saves some GPU time.
- **Video → Shake:** how much the camera shakes and punches in on hits and kills. 0% keeps the camera still.
- **Video → Reduce flashes:** turns off full-screen flashes and the white flash on players who get hit, and dims the
  glow bursts from hits, explosions and lightning.
- **Controls:** each action has three slots. Click a slot, then press the key or mouse button you want (Esc cancels).
  Right-click a slot to clear it. If that key already belongs to another action it moves over, and the menu tells you.
  **Reset to defaults** puts everything back. The menu shows the letters printed on your own keyboard layout.

## Updating

Download the new zip and replace the old folder (or app). Your name, character and volume settings are kept
separately, so they carry over:

| System | Settings and logs |
|---|---|
| Windows | `%APPDATA%\Godot\app_userdata\Brawler` |
| macOS | `~/Library/Application Support/Godot/app_userdata/Brawler` |
| Linux | `~/.local/share/godot/app_userdata/Brawler` |

If something goes wrong, the `logs` folder in that location has `godot.log`. Attach it when you report a bug.
