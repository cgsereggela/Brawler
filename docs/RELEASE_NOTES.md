## New in 1.5.0

- **Swinging hanging platforms:** shoot one of a platform's two chains and it swings down on the other, the swing
  pulling that chain along with it, until it hangs still from one end. Cut the last chain mid-swing and it flies off
  with the swing's speed, turning level as it falls, so timing the cut aims the crush.
- **Survival:** once every bot in the lobby is in a wave, later waves give the bots respawns to share instead, so
  each wave still gets harder. The wave banner and the HUD show how many respawns are left.
- **Smarter bots:** they find a lobbed shot at someone on a ledge they can't reach instead of waiting underneath, shoot
  the chains of a platform hanging over their target (and wait for a swinging one to come over them), and keep
  their footing on drifting platforms.
- **Lobby:** a player card's team, skill and remove buttons now sit in one column, and turning teams on or stepping
  through the game modes no longer shifts the whole screen. **Friendly fire** always shows, greyed out in
  free-for-all.
- **Main menu:** the PayPal tip card moves to the bottom left, out of the way of your name, with a shorter ask.
- **Map editor:** on a controller, B now always deletes the block A just placed.
- **Install guide:** a new Steam Deck section.

## New in 1.4.0

- **Sky arenas:** a new layout style with no floor at all. A row of islands along the bottom stands in for it, and a
  fall from anywhere knocks you out. About one arena in ten.
- **Drifting platforms:** hovering slabs that sway under you. They dip when you land on them, slide when you walk into
  them, and get knocked about by shots and blasts, then ease back to their spot. Use them to ride someone into your
  line of fire or knock them off an edge. About a third of arenas get one or two.
- **Drift arenas:** a new layout style built only from drifting platforms over a bottomless drop, so every step you
  take is on something that moves.
- **Hanging platforms:** platforms on one or two chains from the ceiling or a ledge above. Shoot the chains and the
  platform drops, crushing anyone underneath (the kill is yours), then lands as a new step on the ground, or falls out
  through a pit.
- **Map editor:** new **Drift** and **Hanger** tools. The editor shows how far each drifting platform can be shoved,
  plus each hanging platform's chains and where it will land. **Empty** in the Sky or Drift style starts with no
  floor.
- **Map codes** now carry drifting and hanging platforms. Codes from earlier versions still open, but codes made in
  1.4.0 won't open in older versions.

## New in 1.3.0

- **Kill cam GIFs:** after a kill cam, **Save kill cam GIF** on the results screen (or in the Esc menu during a match)
  saves it as an animated GIF in the game's `clips` folder, ready to post. The button then opens that folder.
- **Accessibility** (Options → Video):
  - **Colorblind-safe colors** swaps the player and team colors for a set that stays distinct with red-green or
    blue-yellow color blindness. It only changes what you see.
  - **UI scale** (100%, 115% or 130%) makes the menus and HUD bigger.
  - **Aim assist** for controllers (default 40%, 0% is off) bends your right-stick aim toward the enemy nearest that
    line.
- **Report Bug:** a new button on the main menu opens the GitHub issues page in your browser.
- **Look fixes:** every hat now sits properly on every hair style, and mouths are bolder.

## New in 1.2.0

- **Gun Game mode:** every kill moves you up a fixed ladder of eight cards, from Minigun to One Shot, and a kill on the
  last one wins. Free-for-all only; everyone respawns. The HUD shows how far up the ladder each player is.
- **Survival mode:** the humans team up against waves of bots. Each wave brings more bots, and tougher ones (Easy, then
  Normal, then Hard, with cards from wave 3). Pick a card after each wave you clear. Clear 10 waves to win; the run ends
  when everyone falls. Needs at least one bot in the lobby.
- **Draft:** a lobby toggle. Before the first battle, players take turns drafting opening cards from one shared pool.
- **Bot skill:** each bot in the lobby has an **Easy**, **Normal** or **Hard** button; click it to change. Normal plays
  like the old bots.
- **Unlockable cosmetics:** four new hats (Wizard, Laurel, Horns, Pirate) and a new **Trail** that follows you around
  (Sparks, Bubbles, Rainbow, Flames, Stars). They unlock as you play matches, win, take players out, clear Survival
  waves and win a Gun Game. **Customize** lets you try locked ones on and says what earns them, and the match-over
  screen tells you when you unlock one.
- **Map thumbnails:** **My maps** and the lobby's **Arenas** list show a small picture of each arena.
- **Fixes:** the lobby's match settings no longer slide off the screen in team modes (the **Remove** button now sits
  under a player's skill button); **Done** no longer drops off the bottom of **Options → Controls** (**Reset to
  defaults** now sits beside it); the Customize screen lost its "move the mouse to look around" hint.

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
