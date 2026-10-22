## New in 1.1.0

- **Map editor:** **Map Editor** on the main menu builds your own arenas: blocks, gaps, hills, spawns, jump pads,
  crates, barrels, glass, magma and poison pools, sawblades and moving platforms. Start from a generated arena or an
  empty one, playtest it against a bot, and save it to **My maps**. It works on a controller too: the left stick moves
  a cursor, **A** places, **B** deletes, **LB/RB** change tools, and **View** reaches the buttons.
- **Map codes:** every arena packs into one line of text. **Copy** it to friends; they **Paste** it in their editor or
  in a lobby. If a code won't open, the game says why: it isn't a map code, it's damaged, or it was made in a newer version.
- **Custom arenas in matches:** in the lobby the host clicks **Arenas** to pick up to 12 saved or pasted arenas, which
  take turns, one per battle. Anyone can copy an arena's code from that list, other players can save the host's
  arenas, and mid-match the Esc menu has **Save arena** and **Copy code** for the arena being played.
- **Smarter bots:** bots now dodge sawblades, step around or jump over magma and poison, and ride moving platforms.
- **Fixes:** two map names that differ only in punctuation no longer overwrite each other's file; copies count up
  (Map 2, Map 3) instead of piling up "2 2 2"; leaving the editor warns once about changes that aren't saved; lobbies
  with many big arenas send them once, not on every lobby change.

## New in 1.0.0

- **Controllers:** play and move through every menu with a gamepad (Xbox, PlayStation, Switch Pro and others).
- **Couch co-op:** up to four people on one computer, each with a controller. In the lobby click **+ Local Player** and
  press **A** on the new player's controller. Works vs bots, hosting and joining.
- **Signed Windows build:** `Brawler.exe` carries Brawler's own signature (thumbprint
  `15E5691329FCBBDD1C1F8F2438471C47FC1E27D9`), so you can check it hasn't been tampered with. Windows still asks you
  to confirm the first run.

## Download

| Your computer | File |
|---|---|
| Windows 10/11 | `Brawler-…-windows.zip`. Extract it, then run `Brawler.exe` (on the SmartScreen warning, click *More info → Run anyway*) |
| macOS 11+ | `Brawler-…-macos.zip`. Unzip it, move it to Applications, then allow it under *System Settings → Privacy & Security → Open Anyway* |
| Linux | `Brawler-…-linux.zip`. Unzip it, run `chmod +x Brawler.x86_64`, then `./Brawler.x86_64` |

Each zip includes `README.txt` with the full install guide and a **step-by-step guide to playing with friends on
other networks** (Tailscale, automatic port opening, or manual port forwarding).

Everyone in a lobby needs **this same version**.
