# HMM Revive changelog

Newest first. The version is shown on the first line of every kit window (setup, play, host).

## 1.6 (2026-10-18)

Preview. Thanks to AndreLuisOvidio for the automatic updates, and to Lonny for his help with the HMM Private Club.

Preview 2 adds the September 2017 version to "Download an old build from Steam" in Settings. If you have the first 1.6
preview, the launcher offers this update by itself.

- **Automatic updates** (by AndreLuisOvidio): the launcher looks for a newer HMM Revive on GitHub each time it starts.
  When there is one, a banner shows what's new and "Update now" downloads it, checks it and updates the kit folder in
  place (your settings and game copies stay), then the launcher restarts and the page reloads by itself. Settings has an
  Updates section with "Check for updates" and "Check for preview versions" (on by default in a preview kit). This
  works from 1.6 on: to get 1.6 itself, download the kit once more as usual.
- **Recharge** in the lobby's car stats, next to HP, move speed and damage: how fast each car charges its ultimate, from
  10% to 300% (for example Stingray at 70%). Both ways the meter fills are scaled: over time and from hitting. It's in
  balance presets and `HMMR1:` codes too (codes made in 1.5 still work). Steam version.
- **Effects**: in Settings, pick the effect for when your car leaves the base, respawns, destroys a car and scores, from
  the game's own (battle passes, events and store: about 50 of each). Everyone in the match sees them. Steam version.
- **HMM Private Club**: the Play page has a card for Lonny's server (the game's online menus and matchmaking).
  "Play on HMM Private Club" starts your Steam copy of the game, once the club's HMM-Connect has connected it; otherwise
  the card links to the club's Discord, where HMM-Connect is. HMM Revive's own copy isn't affected, so you can play both:
  after HMM-Connect, "Update game copy" in HMM Revive keeps working.
- **Old versions from Steam**: Settings explains how to download an old version of the game with your own Steam account
  (a command for Steam's console, then "Add the download"). For now the September 2017 version.
- **No own goals on the 2017 versions** while your team carries the ball in your own goal, like the Steam version (a
  ball thrown or dropped into your own goal still counts, as on Steam). Not tested in a real match yet: tell us how it
  plays.
- Everyone in a lobby needs HMM Revive 1.6.
- **Cloud lobbies:** on the Host screen, choose "In the cloud" instead of "On my PC". The lobby then runs on a server in the
  cloud that is created for it: nobody needs Radmin, Tailscale or ZeroTier, the lobby keeps running if the host closes the
  launcher, and the server is deleted a few minutes after the last player leaves (or after 3 hours at most). The host picks
  who can find it ("Anyone (listed in Play)" or "Only people with the code") and an optional password. Players join from the
  Play list, by typing the lobby code (six letters and digits, for example `a1b2c3`) in the join box, or by pasting the
  lobby's `https://` address; a lock marks lobbies that ask for a password.
- **Invite key:** creating a cloud lobby needs an invite key from whoever runs the cloud (Host screen, "In the cloud";
  it is saved on your PC and never leaves it except to the cloud service). Each key may have two cloud lobbies open at
  once. Joining a cloud lobby needs no key.
- **Rejoin:** if you close the launcher while your cloud lobby is open, the Host screen offers "Rejoin your cloud lobby"
  the next time. The lobby keeps going without its host; its other players are not promoted.
- **Ping:** the Play list shows the network delay to each lobby's server (green under 60 ms, yellow under 120 ms, red above),
  and inside a lobby the launcher keeps measuring it, with average, best, worst and lost readings. It times the connection
  to the lobby's port on the server's address (the game's own ping can be a few milliseconds higher). Works for local and cloud lobbies.
- **Regions:** the host picks where the cloud server runs: Brazil (the default), United States or Europe (Madrid). Pick the one
  nearest to most players; the Play list and the lobby show each cloud lobby's region.
- **Cloud progress:** creating a cloud lobby shows a progress bar, the five steps (contacting the cloud, reserving a server,
  downloading the game, starting the server, connecting), what each one is and how long is left. A banner follows you to
  the other tabs. The lobby opens in about 70 seconds in Brazil and about two minutes in the United States or Madrid.
- **Server preparing the game:** a cloud lobby opens while its server is still getting the game ready (a few seconds to half a
  minute). Everyone in the lobby sees a notice with the progress, can already pick team and car and get ready, and the match
  starts as soon as the game is ready. If the server cannot get the game, the lobby says so and closes a minute later.
- Local lobbies work as before. The Steam build only: the cloud server runs the same game server as the local one.

## 1.5 (2026-10-13)

Thanks to AndreLuisOvidio and DantasB for the first two community contributions.

New since the 1.5 preview:

- **Balance presets:** the lobby's match settings now start with "Balance preset". A preset keeps the ball speed, the
  ability cooldowns and the car stats together, so a tournament's rules are one click instead of moving every slider
  again. "Save as preset…" keeps the current settings under a name, and picking a preset in the list puts them back
  ("Normal" = the game's own). "Copy code" copies a short code (it starts with `HMMR1:`) to share in Discord or the
  chat; "Paste code…" adds someone else's preset to your list and uses it. The 2017 versions use only the ball speed.
- **Each player picks how they drive on the 2017 versions.** The host's "Drive with WASD (everyone)" is gone: in
  Settings (and in the lobby, next to Ready) every player picks "Mouse" (the game's own: left button forward, right
  button reverse, the car turns towards the cursor) or "Keyboard: WASD" (like the Steam version; the mouse buttons
  still work for speed). Mouse and keyboard players play together in the same match. Going back to Mouse also puts
  the game's own mouse driving back.

The rest of 1.5 (from the preview):

- **Ability cooldowns** (by AndreLuisOvidio): the host can set every car's ability cooldown in the lobby's match
  settings ("Ability cooldowns": 0.1 to 5 seconds, or the game's own). It applies to both weapons, the special and the
  boost of every car, players and bots. Steam version.
- **Car stats** (by DantasB): the host can change HP, move speed and damage, from 10% to 300%, for every car at once
  (top row) or for single cars, in the lobby's match settings. Damage stays normal while a car holds the ball. Steam
  version.
- **Ball droppers you can see on Metal God Arena (2017 versions):** the red and blue droppers that make you drop the
  ball were only partly drawn, and some were invisible, so you could lose the ball on what looked like bare floor. Now
  every dropper glows in its team's colour exactly where it is.
- Everyone in a lobby needs HMM Revive 1.5.

## 1.4 (2026-10-05)

New since the 1.4 preview:

- **No Steam needed.** A copy of the game folder (from a friend, a Google Drive, a depot download...) is enough: Steam
  doesn't have to be installed or open, for the Steam version and the September/November 2017 versions alike. The 2017
  versions used to hang on their splash screen without Steam; HMM Revive's copy of them no longer talks to Steam at all.
- **Ball speed:** the host picks it in the lobby's match settings (0.5x to 3x, 1x = normal). Higher means the ball
  flies faster when thrown or hit, and keeps rolling longer. The host can change it during the match too: in the lobby
  page, or by typing `/ballspeed 1.5` in the match chat (`/ballspeed` alone shows the current speed to anyone).
  Everyone in the match sees the change in the chat.
- **The September 2017 version is no longer experimental**, and it has **two arenas** now: Temple of Sacrifice (the
  one played so far) and Metal God Arena. The host picks the arena in the lobby, like on the Steam version. The
  November 2017 version has the same two arenas.
- **The November 2017 version stays experimental** and is hidden from the Host page unless you tick "Show the November
  2017 version when hosting" in Settings > Game copies, so nobody picks it by accident.
- **Radmin VPN** is now named next to Tailscale and ZeroTier (in the launcher and the READMEs): so far it's the easiest
  and most reliable way to play together. The host's Radmin address (26.x.y.z) shows on the lobby screen.
- The window to choose the game folder ("Add a game copy", "Choose game folder") now opens in front of the launcher
  instead of behind it.

The rest of 1.4 (from the preview):

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
- **Heavy Metal Machines from September 2017:** a second old version the launcher can set up and run, same rules as
  the November 2017 one (game's own pick screen, no draft or spectators). Add it under Settings > Game copies.
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
