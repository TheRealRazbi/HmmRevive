HMM Revive kit @VERSION@
========================
Play Heavy Metal Machines again on a private server run by one of you. Free, private and unofficial.
This kit contains no game files; it makes a patched copy of YOUR OWN game.
(Portugues: veja LEIA-ME.txt)
What's new in this version: CHANGELOG.txt

Everyone needs
  1. Heavy Metal Machines: installed from Steam (free), or a copy of the game folder anywhere on your PC.
  2. A way to reach the host's PC: Tailscale (https://tailscale.com/download) or ZeroTier (https://www.zerotier.com),
     installed and joined to the same network as the host. On the same home network you need neither.

Start
  - Unzip this folder anywhere. The same drive as the game is best (no extra disk space used).
    The folder path must use plain letters only (no accents like g/c/a with marks): if your Windows user name has
    them, C:\Users\<name>\Downloads crashes the game, so use something like C:\HMM-Revive instead.
  - Double-click HMM-Revive.exe. The first time it offers to set up: it finds the game in Steam and builds a patched
    copy in .\instance (the original is not changed). Game somewhere else? Click "Choose game folder" and pick HMM.exe
    in it. A window opens with everything else:
      Play      lobbies hosted on your network; click Join, or type the host's address.
      Host      open your own lobby.
      Settings  your name, car, skin, screen resolution.
  - Keep the black HMM-Revive window open while you play; close it to quit the launcher.

In a lobby
  - Pick your team, car and skin, then click Ready. The host picks the arena, points to win and the bots (how many,
    how hard, which cars). When everyone is ready the match starts (or when the host clicks Start), and every
    player's game opens by itself.
  - After the match everyone is back in the lobby: click Ready again for a rematch.
  - In a match, type /cars in chat to list cars and /car <name> to switch (happens while you are dead or between rounds).
    /skins lists your car's skins and /skin <number> changes it the same way.

HOST (one person, on a PC that stays on during the match)
  - In HMM-Revive.exe, go to Host and click "Set up firewall" once (Windows asks for admin). It lets players in on
    the game port (UDP 9696) and the lobby port (TCP and UDP 9697), from Tailscale and local networks only.
  - Tailscale: share your PC with each player: https://login.tailscale.com/admin/machines > the "..." menu of your PC >
    Share... > send each player their own invite link. Sharing shows them this PC only, none of your others.
    Then limit what they can reach to the game and lobby ports: https://login.tailscale.com/admin/acls > replace the
    "grants" section with the one below and save.
        "grants": [
            { "src": ["autogroup:member"], "dst": ["*"], "ip": ["*"] },
            { "src": ["autogroup:shared"], "dst": ["*"], "ip": ["udp:9696", "tcp:9697", "udp:9697"] },
        ],
    The first line keeps full access for you and your own devices; the second lets players reach only the game
    (UDP 9696) and the lobby (9697) on the PC you shared. If you set this up for an older kit with only "udp:9696",
    add the two 9697 entries, or players can't join your lobby.
  - ZeroTier: players join your network and you authorize them in its admin page.
  - Click "Open lobby". Players on your network see it under Play; others can type the address shown in the lobby.

Tournaments (in the lobby's match settings, host only)
  - "Draft before each match": starting the match first starts a draft. A random team goes first, then the teams
    take turns banning cars and then picking them (by default bans A1 B1, picks A1 B2 A2 B1 A1 B1; A = the team that
    goes first; the host can change both). Each turn has 60 seconds by default; when time runs out the turn is
    completed at random. After the draft each player chooses one of the team's cars and clicks Ready again.
  - "2 spectator seats": up to two people watch the match with the game's spectator camera, without a car
    (click "Watch" in the lobby). They can join a match that is already running.

Older version of the game (experimental, not ready yet)
  - HMM-Revive.exe can also run an older version of Heavy Metal Machines. It's experimental: expect problems. If you have a copy of it, go to Settings >
    Game copies > "Add a game copy" and pick its HMM.exe: the launcher tells you whether that copy is supported and
    sets it up in .\instance-<version>. When you host with more than one copy set up, you choose the game before
    opening the lobby; everyone in the lobby needs the same copy set up. In that version you pick your car in the
    game's own pick screen when the match starts, and the game keeps its original shop and numbers.

The old way (still works): host.bat starts a server from menus, play.bat joins one (also for relay addresses),
stop.bat stops both. In HMM-Revive.exe, Play > "Direct connect" joins such servers too.

Something went wrong? Tell them your version (top of the HMM-Revive window) and send launcher-settings.log plus the newest
instance\hmmrevive-client-*.log (players) or instance\hmmrevive-server-9696.log and instance\server_unity_9696.log (host)
(for the older version of the game, the same files in its instance-<version> folder)
to whoever gave you this kit.
