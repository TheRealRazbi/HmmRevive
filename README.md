# HMM Revive

🇧🇷 **Português:** [README.pt-BR.md](README.pt-BR.md)

Play **Heavy Metal Machines** again. Hoplon shut the game's servers down in 2022 and the game is online-only.
HMM Revive brings it back for free, private matches among friends:

- a headless **match server** built from the game's own shipped server code (the server scene was never shipped,
  so the mod rebuilds it at runtime), with bots;
- **clients connect directly** to that server (the game's built-in developer direct-connect mode), for example over
  [Tailscale](https://tailscale.com).

Extras: pick your car and skin at launch, switch either mid-match (`/car` and `/skin` in chat), bot count,
difficulty and cars per team, out-of-combat repair.

> Unofficial fan project, not affiliated with or endorsed by Hoplon. **No Hoplon code or game files** are in this
> repository or in the kit: the patcher works on a copy of *your own* Steam install, and the Steam install itself is
> never modified.

## Play

You need Windows and Heavy Metal Machines in your Steam library (build `Release.15.00.250`).

1. Download `HMM-Revive-friend-kit-<version>.zip` from [Releases](../../releases).
2. Unzip it to a folder whose path has **no accented or special letters**, e.g. `C:\HMM-Revive`
   (the game crashes at start otherwise). The same drive as the game uses no extra disk space.
3. Run `setup.bat` once. It finds the game and builds a patched copy in `.\instance`.
4. One person hosts with `host.bat`; everyone else joins with `play.bat` and the host's address.

The step-by-step guide, including hosting over Tailscale, is in the kit and here:
[README.txt](tools/friend-kit/README.txt) (English), [LEIA-ME.txt](tools/friend-kit/LEIA-ME.txt) (Português).
The version (e.g. `HMM Revive 1.0`) is the first line of every kit window; mention it when you report a problem.
What changed in each version: [CHANGELOG.md](CHANGELOG.md) (also in the kit as `CHANGELOG.txt`).
In a match, type `/cars` in chat to list the cars and `/car <name>` to switch, or `/skins` and `/skin <number>` for
your skin (the change happens while you're dead or between rounds).

**Security:** the match server has no real authentication. Anyone who can reach its UDP port (9696) can take a free
player slot while it runs. Share it over Tailscale (the kit README shows how to limit shared users to that one port),
set the number of human players exactly, and stop the server after playing.

## Known limits

- No main menu or rematch: "Exit game" closes the game, and the host starts a new server for each match.
- The in-match shop is gone (Hoplon removed it from the game); cars keep their base stats.
- If your connection times out, the game can drop you into its old main menu, which says you're banned for an
  inappropriate name. Nobody is banned (there are no official servers anymore): close the game and join again.
- Out-of-combat repair (7% max HP per second after 5 s without damage) uses our own values, not Hoplon's, and may
  still be tuned.

Planned: a game browser, so players can find running matches without passing addresses around.

## Build from source

Requirements: Heavy Metal Machines from Steam, the .NET 8 SDK (builds the patcher; the mod targets .NET 3.5 against
the game's own DLLs, which are never copied or committed), Git Bash and PowerShell.
If the game isn't in the default Steam folder, set `HMM_GAME_DIR` (build scripts) and `GameManaged` (mod project).

```bash
tools/build.sh                     # build mod + patcher, create/refresh the patched copy in ./instance
tools/test_match.sh 1 150          # smoke test: headless server + 1 hidden, muted client, prints logs
tools/make_friend_kit.sh           # build/HMM-Revive-friend-kit-<version>.zip
```

Run a match from the repo:
```powershell
tools/run_server.ps1 -Players 2 -SameTeam -RedBots 4 -BluBots 2   # headless, UDP 9696
tools/run_client.ps1 -Name Player -Ip 100.x.y.z -Car stingray      # visible game window
```
- `-Players`: humans the server waits for. `-RedBots/-BluBots`: bots per team, up to 4 cars per team.
- `-RedDifficulty/-BluDifficulty easy|medium|hard|auto`: bot AI per team (`auto` is the game's own pick).
- `-SameTeam` puts all humans on Blue (otherwise they alternate). `-Car`: car by name, codename or number.
- `-Skin` (client): skin number from `launcher/skins.txt` (0 = original), name or `random`.
  `-RedBotCars/-BluBotCars` (server): bot cars in slot order, e.g. `"wildfire,photon,16"`, or `random`.
- `-Score N` (server and clients): points to win. Client: `-Width/-Height`, `-Windowed/-Fullscreen`.
- Or use the menus: `launcher/host.bat` and `launcher/join.bat` (answers are saved in `launcher/settings.json`).
- The host must allow inbound UDP 9696 for `instance\HMM.exe` in Windows Firewall.

| path | what |
|---|---|
| `mod/HmmRevive/` | the mod: server bootstrap, hooks, car/skin choice and swap, repair, watchdog logs |
| `tools/Patcher/` | Mono.Cecil patcher: hardlinked copy of the game + IL hooks into `Assembly-CSharp-firstpass.dll` |
| `tools/friend-kit/` | scripts and READMEs of the kit zip |
| `launcher/` | host/join menus for running from the repo |
| `docs/HOW-IT-WORKS.md` | how the game and the mod work |

Contributions are welcome. Never include decompiled game code, game files or patched game files.

## License

This project's code is licensed under the [Mozilla Public License 2.0](LICENSE). It covers only the code in this
repository, not Heavy Metal Machines, which belongs to Hoplon. Heavy Metal Machines is a trademark of Hoplon.
Free and non-commercial: please don't sell access to it or redistribute Hoplon's files, patched or not.
