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
3. If Windows shows "Windows protected your PC", click **More info**, then **Run anyway**. `Brawler.exe` is signed,
   but with Brawler's own certificate rather than one bought from a certificate authority (that costs money each
   year), so Windows doesn't recognise the publisher yet.
   To check your copy is the real one: right-click `Brawler.exe` → **Properties** → **Digital Signatures**. The signer
   is **Brawler**; select it, click **Details** → **View Certificate** → **Details**, and the **Thumbprint** is
   `15e5691329fcbbdd1c1f8f2438471c47fc1e27d9`.
4. The first time you host a game, Windows Firewall asks whether Brawler may use the network. Tick
   **both Private and Public networks**, then click **Allow**. (Missed it? See *Firewall* in the online guide.)

Keep `Brawler.exe` and the `data_Brawler_windows_x86_64` folder together. The game needs both. It draws with Vulkan and
switches to DirectX 12 by itself if Vulkan isn't available.

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

### Steam Deck
The Deck runs the Linux build natively, so you don't need Proton.
1. Press the **Steam** button, then **Power → Switch to Desktop**. (**Steam + X** brings up the on-screen keyboard.)
2. In a browser, download `Brawler-<version>-linux.zip` (see **Download** above).
3. In **Dolphin**, right-click the zip in **Downloads** and choose **Extract → Extract archive here, autodetect
   subfolder**. Move the folder somewhere permanent, for example `~/Games/Brawler`.
4. Right-click `Brawler.x86_64`, choose **Properties → Permissions**, tick **Is executable**, then click **OK**.
5. In Steam, click **Games → Add a Non-Steam Game to My Library…**, then **Browse…**, pick `Brawler.x86_64`, and click
   **Add Selected Programs**. Leave **Compatibility** (Proton) off.
6. Double-click **Return to Gaming Mode**. Brawler is under **Library → Non-Steam**. The game reads the controller
   itself, so set the controls to the **Gamepad** template: **Controller settings → Current Layout → Templates →
   Gamepad**.

To type a host's address in **Join a Friend**, press **Steam + X**. The Deck's firewall is off by default, so hosting
needs no extra setup. When you update, replace the folder in the same place, or change the shortcut's target in
Steam under **Properties → Shortcut**. Settings are kept in the Linux folder listed under **Updating**.

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

**Controllers** work too: left stick moves, right stick aims, **A**/**LB** jump, **RT**/**RB** shoot, **LT**/**B** block,
**X** reloads, the d-pad and **Y** are emotes, and **Start** opens the options. In menus, use the d-pad or left stick,
**A** to choose and **B** to go back.

**Couch co-op:** friends on the same computer can join with their own controllers. In the lobby click
**+ Local Player**, then the new player presses **A** on their controller. Up to four people per computer, in any
kind of game (vs bots, hosting or joining). After each round you take turns picking cards on the shared screen.

- **Play vs Bots:** you against bots. Add up to three in the lobby.
- **Host Game:** opens a lobby that friends can join. You can fill empty slots with bots.
- **Join a Friend:** type the host's address (for example `192.168.1.20`, or `203.0.113.7:24565` with a port) and click **Join**.
  The game remembers the last address you used.

Win battles to take a round, and win rounds to take the match. After every round everyone picks a card,
and players who lost get more cards to choose from.

The host decides which cards can come up: click **Cards** in the lobby and untick the ones you don't want
(at least 7 must stay in). Your choice is remembered next time you host. Friends can open it to see the pool.

### Your own arenas
Click **Map Editor** on the main menu.
1. **Start** from an arena: pick a layout style and click **Generate** (again for another), or click **Empty**. Use the
   arrows by **Theme** to choose how it looks.
2. **Build** with the tools on the left: blocks, floor gaps, hills, spawns, jump pads, crates, barrels, glass, magma
   and poison pools, sawblades and moving platforms.
   - **Select** moves and resizes pieces.
   - Right-click deletes a piece. **Ctrl+Z** undoes.
   - The line under the arena warns about anything that would play badly, such as a spawn standing on nothing.
3. **Play** it right away with **Playtest**.
4. **Keep** it: name it and click **Save**.
5. **Share** it: **Copy** puts its map code on your clipboard. A friend pastes it with **Paste** in their editor.

To play your arenas with friends, click **Arenas** in the lobby when you host. Tick saved arenas, or paste a friend's
code; ticked arenas take turns, one per battle. Leave everything unticked for a new random arena every battle.
Saved arenas are `.map` files in the `maps` folder inside the settings folder listed under **Updating** below.

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
