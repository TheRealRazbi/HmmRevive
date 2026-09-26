HMM Revive kit @VERSION@
========================
Play Heavy Metal Machines again on a private server run by one of you. Free, private and unofficial.
This kit contains no game files; it patches a copy of YOUR OWN Steam install.
(Portugues: veja LEIA-ME.txt)
What's new in this version: CHANGELOG.txt

Everyone needs
  1. Heavy Metal Machines installed from Steam (it must be in your library).
  2. Tailscale (https://tailscale.com/download), installed and signed in with any account.

Once
  - Unzip this folder anywhere. The same drive as the game is best (no extra disk space used).
    The folder path must use plain letters only (no accents like g/c/a with marks): if your Windows user name has
    them, C:\Users\<name>\Downloads crashes the game, so use something like C:\HMM-Revive instead.
  - Double-click setup.bat. It finds the game and builds a patched copy in .\instance.
    Your Steam install is not changed.

PLAYERS
  - Accept the host's Tailscale invite link (once). Not needed if the host gives you a public relay address
    (a server name that forwards the game to the host): then you don't need Tailscale at all, just type that address in play.bat.
  - When the host says the server is up: double-click play.bat, then type the host's Tailscale IP
    (100.x.y.z) or relay address, your name and a car. Your answers are remembered; just press Enter the next time.
  - "Exit game" closes the game. For the next match, the host restarts the server and you run play.bat again.
  - In a match, type /cars in chat to list cars and /car <name> to switch (happens while you are dead or between rounds).

HOST (one person, on a PC that stays on during the match)
  Once:
  - Share your PC with each player: https://login.tailscale.com/admin/machines > the "..." menu of your PC >
    Share... > send each player their own invite link. Sharing shows them this PC only, none of your others.
  - Limit what they can reach to the game port: https://login.tailscale.com/admin/acls > replace the "grants"
    section with the one below and save.
        "grants": [
            { "src": ["autogroup:member"], "dst": ["*"], "ip": ["*"] },
            { "src": ["autogroup:shared"], "dst": ["*"], "ip": ["udp:9696"] },
        ],
    The first line keeps full access for you and your own devices; the second lets players reach only
    UDP 9696 (the game) on the PC you shared.
  - The first time you run host.bat it offers to add a Windows Firewall rule (UDP 9696, Tailscale addresses
    only). Say yes and accept the admin prompt, or players can't connect.
  Every match:
  - Double-click host.bat, pick arena, number of human players and bots. It shows your Tailscale IP:
    send it to the players. The match starts once all human players have joined.
  - host.bat offers to start your own game too. Points to win are always 3.
  - After the match, run host.bat again for a new one. stop.bat stops the server and the game.
  - Up to 8 cars per match (4 per team). Humans alternate Blue/Red in joining order, unless you pick
    "all humans on the same team" (then up to 4 humans against bots).

Something went wrong? Tell them your version (top line of the setup/play/host window) and send the newest instance\hmmrevive-client-*.log (players) or
instance\hmmrevive-server-9696.log and instance\server_unity_9696.log (host) to whoever gave you this kit.
