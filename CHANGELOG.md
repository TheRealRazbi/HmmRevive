# HMM Revive changelog

Newest first. The version is shown on the first line of every kit window (setup, play, host).

## 1.2 (preview, 2026-09-28)

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
