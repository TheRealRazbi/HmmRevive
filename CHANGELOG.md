# HMM Revive changelog

Newest first. The version is shown on the first line of every kit window (setup, play, host).

## 1.4 (preview, 2026-10-04)

- **The host can swap players between teams**, also when both teams are full, to balance the teams by skill (like in
  the original game's custom matches). In the lobby, the host clicks "Swap…" on a player, then "⇄ Swap" on a player
  of the other team, or "Move here" on a free seat of the other team. Everyone swapped clicks Ready again.
- **Lobby chat:** a chat box in the lobby page, for everyone in the lobby (also during the draft and the match). It
  also shows who joined and left.
- **Spectators can chat in the match.** Their messages go to everyone, tagged [Spectator] (the game only allowed this
  in its custom matches).
- **Emotes are back.** In a match, hold the emote key to open the emote wheel and show an emote above your car, like
  before the servers closed. Pick your four emotes in Settings > Emotes (119 to choose from).
- **Skin pictures:** a picture of the skin next to the car and skin choice (lobby and Settings), so you can tell the
  skins apart. The first time the launcher starts after setup, it makes the pictures from your own game files (a hidden
  game window for a few seconds), so they always match your game.
- **Heavy Metal Machines from September 2017 (experimental):** a second old version the launcher can set up and run,
  same rules as the November 2017 one (game's own pick screen, one arena, no draft or spectators). Add it under
  Settings > Game copies. Not ready for real matches yet.
- **Ability cooldowns:** the host can set every car's ability cooldown in the lobby's match settings ("Ability
  cooldowns": 0.1 to 5 seconds, or the game's own). It applies to both weapons, the special and the boost of every
  car, players and bots, from the next match.
- Everyone in a lobby needs HMM Revive 1.4.

## 1.3 (2026-10-03)

New since the 1.3 preview:

- **Draft order fixed.** Bans and picks now have their own order. By default team A bans 1 car, then team B bans 1;
  then the picks go `A1 B2 A2 B1 A1 B1` (A picks 1, B picks 2, A picks 2, then one car at a time). A is the team that
  goes first. The host can still change both orders.
- **Time limit per draft turn:** 60 seconds by default, like the game's own pick screen. When the time runs out, the
  turn is completed at random (cars the team already selected are kept). The host can pick 30-120 seconds or no limit.
- **Spectators can come back.** A spectator who closed their game, or left the lobby, couldn't watch the running
  match again, even with a free seat. Now they can: click "Start my game again", or join the lobby again.
  Leaving the lobby as a spectator also closes your game, and a few seconds later your seat is free for someone else.
  A player who joins while a match is running takes a free spectator seat and watches it.

The rest of 1.3 (from the preview):

For tournaments:

- **Draft:** the host can turn on "Draft before each match" in the lobby's match settings. Starting the match then
  starts the draft first. A random team goes first, and the teams take turns banning cars, then picking them (order
  above). Each team picks as many cars as it has players and bots.
  - Any player of the team whose turn it is clicks cars on the draft board, then "Lock in". Teammates see the
    selection as it happens; the other team only sees it once it's locked in.
  - Banned cars and the other team's picks can't be picked.
  - After the draft, each player chooses which of the team's cars they drive (choosing a teammate's car swaps
    with them), and bots get the cars left over. Everyone clicks Ready again, and the match starts.
  - Teams are locked from the draft until the match ends. The host can reset the draft. Every match gets a new draft.
- **2 spectator seats:** the host can turn on "2 spectator seats". Spectators watch the match with the game's own
  spectator camera and don't drive a car. Click "Watch" in the lobby's Spectators box. Spectators can also join a
  match that is already running. When the teams are full or drafted, new players join as spectators if a seat is free.
- The draft and spectators need the Steam version of the game.

Also in 1.3:

- **Heavy Metal Machines from November 2017 (experimental, not ready yet).** Besides the Steam version, the
  launcher can run that older version. It's marked experimental: expect problems, and don't use it for real
  matches yet. If you have a copy, add it under Settings > Game copies > "Add a game copy" and pick its HMM.exe.
  The launcher tells you whether the copy is supported and sets it up in its own folder (`instance-2017`). The
  folder you picked isn't changed.
  - In that version you pick your car and skin in the game's own pick screen, and it keeps the game's original
    shop and numbers. It has one arena, and no draft or spectators.
  - With more than one copy set up, the host picks the game before opening the lobby. The match list shows which
    game a lobby plays.
- Everyone in a lobby needs HMM Revive 1.3.

## 1.2 (2026-09-29)

New since the 1.2 preview:

- **Host without playing:** untick "I play in the match too" in the lobby's match settings. Your PC runs the lobby
  and the match, but no game opens on it, and the match starts when the players are ready. Good for a server PC or
  a VPS. On a server with a public address, the launcher shows that address to share, and "Set up firewall" lets
  players in from anywhere.
- **Out-of-combat repair is much faster:** 100 HP per second (was 7% of max HP per second), exactly what the
  arenas' repair areas give (the healing spots in Metal God Arena, for example). That's 22% per second for the
  lightest cars and 10% for the heaviest. The 5 second wait after the last hit stays the same.
- **Game outside Steam:** "Choose game folder" when the launcher doesn't find the game in Steam (details below).

Everything in 1.2:

- **New launcher: `HMM-Revive.exe`.** A window with everything in one place instead of the .bat menus, in English
  or Brazilian Portuguese (picked from your system language; switch at the top right):
  - **Play:** lists lobbies hosted on your Tailscale or ZeroTier network (or home network); join with a click or by
    typing the host's address.
  - **Host:** open a lobby. You pick the arena, points to win and the bots (how many per team, how hard, and each
    bot's car); players pick their team, car and skin and click Ready. The match starts when everyone is ready
    (or when you click Start), and every player's game opens by itself.
  - **Rematch:** after a match everyone is back in the lobby; click Ready again for the next one.
  - **Settings:** your name, car, skin (remembered per car) and screen resolution. First-time setup and updating
    your game copy are one click, and "Set up firewall" allows the game and lobby ports for hosting.
- **Hosts on Tailscale:** the lobby uses port 9697 (TCP and UDP) next to the game's UDP 9696. If your Tailscale
  grants only allow `udp:9696` for shared users, add `tcp:9697` and `udp:9697` (see README.txt).
- **Match servers now stop by themselves** 30 seconds after the match ends (they used to run until closed), and
  players' games close when that happens.
- **Game outside Steam:** if the launcher doesn't find the game in Steam, "Choose game folder" lets you pick HMM.exe
  in any copy of the game (another Steam library, or a plain game folder). The launcher remembers it for updates.
- host.bat and play.bat still work as before.

## 1.1 (2026-09-27)

- **Skins:** pick a skin when you join (play.bat lists your car's skins and remembers your pick per car; `0` =
  original, the default). In a match,
  `/skins` lists them and `/skin <number>` changes it when you respawn or between rounds.
- **Bot cars:** host.bat lets you pick each team's bot cars, e.g. `wildfire, photon, peacemaker`, or `random`.
- **Fixed:** after switching cars mid-match, you now spawn in your new car's role position (transporters first), and
  the weapon details window shows your new car.
- **Out-of-combat repair** is a bit faster (7% of max HP per second instead of 5%) and now works for Full Metal Judge
  in defensive mode (the shield's own decay used to stop it). His aggressive mode still doesn't repair.

## 1.0 (2026-09-26)

First public release. Changes since the test kits shared earlier:

- **Firewall prompt only once:** host.bat asked to add the Windows Firewall rule, with an admin prompt, on every
  run. Now it asks only the first time (and again only if you move the kit folder).
- **Out-of-combat repair:** a car that takes no damage for 5 seconds repairs 5% of its max HP per second while the
  bomb is being delivered. The values are a first guess and may change.
- **Version shown:** every kit window starts with `HMM Revive <version>`, and play.bat/host.bat warn you when your
  game copy is older than the kit (run setup.bat again).
- **Folders with special letters:** the game can't start from a folder whose path has accented or Turkish letters
  (e.g. in your Windows user name). The kit now says so and tells you to move it, instead of the game crashing.
- **Fixed:** after switching away from Zephyr while dead, the game spammed errors until the round ended.
