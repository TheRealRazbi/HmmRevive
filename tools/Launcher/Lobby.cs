using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace HmmRevive.Launcher
{
    /// <summary>
    /// The lobby a host runs: players join over HTTP on the lobby port (TCP 9697), pick team, car and ready, and the host
    /// starts the match (or it starts by itself once everyone is ready). Starting launches the match server here; every
    /// member's launcher sees the phase change and launches its own game with the team it picked. When the server exits
    /// (the mod quits it after the match), everyone is back in the lobby for a rematch.
    /// Every member, the host included, talks to it through <see cref="Session"/>. The host may stay out of the match
    /// (team <see cref="NoTeam"/>, e.g. a server PC or VPS): then it only runs the lobby and the match server.
    /// A lobby plays one game build (<see cref="Build"/>); only players who have that copy set up can join. On an old build
    /// players pick their car in the game's own pick screen, so the lobby has no car or skin choice, and the arena is one of the
    /// build's two (<see cref="Catalog.LegacyArenas"/>).
    /// Tournaments (Steam build only): up to two spectators (team <see cref="Spectator"/>, the game's narrators) and a
    /// <see cref="Draft"/> of bans and picks before each match.
    /// </summary>
    public class Lobby
    {
        public class Member
        {
            public string Id, Token, Name, Team, Car = "", Skin = "0", Version;
            public bool Ready, IsHost, InMatch;
            public DateTime Seen = DateTime.UtcNow;
            public readonly List<DateTime> Said = new List<DateTime>(); // recent chat messages (flood limit)
        }

        public class TeamSetup
        {
            public int Bots;                // wanted bots; fewer when humans take the slots
            public string Difficulty = "auto";
            public List<string> Cars = new List<string>(); // per bot slot: car id, "random" or "default"
        }

        public const int TeamSize = 4;
        public const string NoTeam = "none"; // the host when it doesn't play
        public const string Spectator = "spec";
        public const int SpectatorSlots = 2; // the game's own limit (AuthenticationManager.FakeNarrator)
        private readonly object _gate = new object();
        private readonly List<Member> _members = new List<Member>();
        private int _rev = 1;
        private HttpServer _http;
        private UdpClient _discovery;
        private Thread _ticker;
        private volatile bool _closed;

        public readonly int LobbyPort, GamePort;
        public readonly string Build;
        public string Phase = "lobby"; // lobby, starting, playing
        public string Message = "";
        public int Arena = 1, Score = 3;
        public int BallSpeed = 100; // percent, 50-300; the host may change it during the match too
        public bool AutoStart = true;
        public bool Spectators, DraftOn;
        public string DraftBans = Draft.DefaultBans, DraftPicks = Draft.DefaultPicks;
        public int DraftTime = Draft.DefaultTurnSeconds; // seconds per draft turn, 0 = no limit
        private Draft _draft; // this match's draft: running (phase "draft") or done (cars limited to each team's picks)
        private readonly Random _rng = new Random();
        public readonly Dictionary<string, TeamSetup> Teams = new Dictionary<string, TeamSetup> { ["blue"] = new TeamSetup(), ["red"] = new TeamSetup() };
        private DateTime? _countdownEnd;
        private int _draftSecondsShown = -1; // the turn clock members last saw
        private readonly string _lobbyId = NewId(6); // tells the same lobby apart when found on several networks
        private int _matchNumber;
        private bool _matchStarted; // the server reached a match (vs. died while loading)

        public Lobby(int lobbyPort, int gamePort, string build)
        {
            LobbyPort = lobbyPort;
            GamePort = gamePort;
            Build = build;
            LoadSetup(Settings.Data.Obj("host"));
        }

        public bool Open()
        {
            _http = new HttpServer(IPAddress.Any, LobbyPort, Handle);
            if (!_http.Start()) return false;
            try
            {
                _discovery = new UdpClient(new IPEndPoint(IPAddress.Any, LobbyPort)) { EnableBroadcast = true };
                new Thread(DiscoveryLoop) { IsBackground = true, Name = "discovery" }.Start();
            }
            catch (SocketException e) { Program.Log("discovery port busy: " + e.Message); }
            _ticker = new Thread(Tick) { IsBackground = true, Name = "lobby" };
            _ticker.Start();
            Program.Log($"lobby open on TCP/UDP {LobbyPort}");
            return true;
        }

        public void Close()
        {
            _closed = true;
            _http?.Stop();
            try { _discovery?.Close(); } catch { }
            Game.StopServer();
            lock (_gate) { _rev++; Monitor.PulseAll(_gate); }
        }

        private static string NewId(int bytes)
        {
            var b = new byte[bytes];
            using (var r = RandomNumberGenerator.Create()) r.GetBytes(b);
            return BitConverter.ToString(b).Replace("-", "").ToLowerInvariant();
        }

        private void Changed() { _rev++; Monitor.PulseAll(_gate); }

        // ---- settings ----

        private void LoadSetup(Dictionary<string, object> h)
        {
            if (h == null) return;
            Arena = h.Int("arena", Arena);
            Score = Math.Max(1, h.Int("score", Score));
            BallSpeed = Math.Max(50, Math.Min(300, h.Int("ballSpeed", BallSpeed)));
            AutoStart = h.Bool("autoStart", AutoStart);
            Spectators = h.Bool("spectators", Spectators);
            DraftOn = h.Bool("draft", DraftOn);
            // (1.3 preview had one "draftOrder" for bans and picks; its default was wrong, so it isn't read.)
            DraftBans = Draft.Clean(h.Str("draftBans")) ?? DraftBans;
            DraftPicks = Draft.Clean(h.Str("draftPicks")) ?? DraftPicks;
            DraftTime = Math.Max(0, Math.Min(600, h.Int("draftTime", DraftTime)));
            foreach (string t in new[] { "blue", "red" })
            {
                var d = h.Obj(t);
                if (d == null) continue;
                Teams[t].Bots = Math.Max(0, Math.Min(TeamSize, d.Int("bots", Teams[t].Bots)));
                string diff = d.Str("difficulty", "auto");
                Teams[t].Difficulty = new[] { "easy", "medium", "hard", "auto" }.Contains(diff) ? diff : "auto";
                Teams[t].Cars = d.List("cars").Select(o => CleanCar(o?.ToString())).ToList();
            }
        }

        private static string CleanCar(string c)
        {
            if (c == "random" || c == "default") return c;
            return Catalog.CarId(c)?.ToString() ?? "default";
        }

        public Dictionary<string, object> SetupJson()
        {
            var d = new Dictionary<string, object> { ["arena"] = Arena, ["score"] = Score, ["ballSpeed"] = BallSpeed, ["autoStart"] = AutoStart, ["spectators"] = Spectators, ["draft"] = DraftOn,
                ["draftBans"] = DraftBans, ["draftPicks"] = DraftPicks, ["draftTime"] = DraftTime };
            foreach (var t in Teams)
                d[t.Key] = new Dictionary<string, object> { ["bots"] = t.Value.Bots, ["difficulty"] = t.Value.Difficulty, ["cars"] = t.Value.Cars.ToArray() };
            return d;
        }

        /// <summary>Host changes arena, score, bots or auto-start (from the host's own page).</summary>
        public void Configure(Dictionary<string, object> h)
        {
            lock (_gate)
            {
                string before = DraftShape();
                int ball = BallSpeed;
                LoadSetup(h);
                if (BallSpeed != ball && (Phase == "starting" || Phase == "playing")) Game.SetLiveBallSpeed(Build, GamePort, BallSpeed);
                Settings.Set("host", SetupJson());
                _countdownEnd = null;
                if (_draft != null && DraftShape() != before) ResetDraft("The draft was reset: the match settings changed.");
                if (!SpectatorsOn)
                    foreach (Member m in _members.Where(x => x.Team == Spectator && !x.InMatch))
                        m.Team = m.IsHost ? NoTeam : Humans("blue") <= Humans("red") ? "blue" : "red";
                Changed();
            }
        }

        // What a running or finished draft depends on: changing it means a new draft.
        private string DraftShape() => $"{DraftEnabled} {DraftBans} {DraftPicks} {BotsOn("blue")} {BotsOn("red")}";

        // ---- members ----

        private int Humans(string team) => _members.Count(m => m.Team == team);
        private IEnumerable<Member> Players => _members.Where(m => m.Team == "blue" || m.Team == "red");
        private bool SpectatorsOn => Spectators && !Builds.IsLegacy(Build);
        private bool DraftEnabled => DraftOn && !Builds.IsLegacy(Build);
        private bool SpectatorFree => SpectatorsOn && Humans(Spectator) + Freeing < SpectatorSlots;

        // A spectator who stops watching a running match keeps the game's narrator slot until the server drops the
        // connection (SfNetwork ConnectionTimeout, 10 s), so that lobby seat stays taken a little longer.
        private readonly List<DateTime> _seatsFreeing = new List<DateTime>();
        private int Freeing => _seatsFreeing.Count(t => t > DateTime.UtcNow);
        private void SeatLeft(Member m)
        {
            if (m.Team == Spectator && m.InMatch && (Phase == "starting" || Phase == "playing")) _seatsFreeing.Add(DateTime.UtcNow.AddSeconds(15));
        }
        private bool Drafting => _draft != null; // running or done: the teams are locked
        private int BotsOn(string team) => Math.Max(0, Math.Min(Teams[team].Bots, TeamSize - Humans(team)));

        public Member Join(string name, string version, IEnumerable<string> builds, bool isHost, out string error)
        {
            error = null;
            name = Settings.CleanName(name);
            lock (_gate)
            {
                if (_closed) { error = "The lobby is closed."; return null; }
                if (name == null) { error = "Pick a player name first (letters and digits)."; return null; }
                if (version != Program.Version) { error = $"The host runs HMM Revive {Program.Version} and you have {version}. Everyone needs the same version."; return null; }
                if (!(builds ?? new[] { Builds.Steam }).Contains(Build)) { error = $"This lobby plays {Builds.Label(Build)}, and that game copy isn't set up on your PC."; return null; }
                Member old = _members.FirstOrDefault(m => m.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (old != null)
                {
                    // Same name again: a restarted launcher reclaims its slot once the old one stopped answering.
                    if ((DateTime.UtcNow - old.Seen).TotalSeconds < 8 && !isHost) { error = $"Someone called {name} is already in this lobby. Change your name."; return null; }
                    _members.Remove(old);
                }
                bool plays = !isHost || Settings.Data.Bool("hostPlays", true);
                string team = Humans("blue") <= Humans("red") ? "blue" : "red";
                if (Humans(team) >= TeamSize) team = team == "blue" ? "red" : "blue";
                if (old != null && old.Team != NoTeam && (old.Team == Spectator || Drafting || Phase != "lobby")) team = old.Team; // back in the same seat
                else if (!plays) team = NoTeam;
                else if (Players.Count() >= 2 * TeamSize || Drafting)
                {
                    // Full, or the teams are drafted: watch instead, if a spectator seat is free.
                    if (!SpectatorFree) { error = Drafting ? "This lobby is drafting and the spectator seats are taken. Join after the match." : "The lobby is full (8 players)."; return null; }
                    team = Spectator;
                }
                else if (Phase != "lobby" && SpectatorFree) team = Spectator; // a match is running: watch it (a team seat is free again after it)
                var m = new Member { Id = NewId(4), Token = NewId(16), Name = name, Team = team, Version = version, IsHost = isHost };
                if (old != null && old.Team == team) { m.Car = old.Car; m.Skin = old.Skin; m.InMatch = old.InMatch; }
                if (team == Spectator && Phase == "playing") m.InMatch = true; // spectators can join a running match
                _members.Add(m);
                Message = $"{name} joined.";
                ChatAdd(name, team, "joined", true);
                Changed();
                return m;
            }
        }

        public string Update(string token, Dictionary<string, object> d)
        {
            lock (_gate)
            {
                Member m = _members.FirstOrDefault(x => x.Token == token);
                if (m == null) return "You're not in this lobby anymore.";
                m.Seen = DateTime.UtcNow;
                string team = d.Str("team");
                if ((team == "red" || team == "blue" || (team == NoTeam && m.IsHost) || team == Spectator) && team != m.Team)
                {
                    // Players' seats are locked from the draft to the end of the match; watchers may come and go.
                    bool player = m.Team == "red" || m.Team == "blue";
                    if (player && (m.InMatch || Drafting)) return Drafting && Phase == "lobby" ? "The teams are locked after the draft. The host can reset the draft." : "Teams are locked during a match.";
                    if (team == Spectator && !SpectatorFree)
                        return !SpectatorsOn ? "This lobby has no spectator seats." : Freeing > 0 ? "A spectator just left: that seat is free again in a few seconds." : "The spectator seats are taken.";
                    if (team == "red" || team == "blue")
                    {
                        if (Phase != "lobby") return "Teams are locked during a match.";
                        if (Drafting) return "The teams are locked after the draft. The host can reset the draft.";
                        if (Humans(team) >= TeamSize) return "That team is full.";
                    }
                    SeatLeft(m);
                    m.Team = team;
                    m.Ready = false;
                    m.InMatch = team == Spectator && Phase == "playing";
                    if (m.IsHost) Settings.Set("hostPlays", team == "red" || team == "blue"); // remembered for the next lobby
                }
                if (d.ContainsKey("car"))
                {
                    string c = CleanCar(d.Str("car"));
                    c = c != "default" && c != "random" ? c : "";
                    if (!Drafting) m.Car = c;
                    else if (_draft.Done && Phase == "lobby" && (m.Team == "blue" || m.Team == "red") && c != m.Car && _draft.Pool(m.Team).Contains(c))
                    {
                        // Drafted: any of the team's cars; a teammate who drives it gets ours.
                        Member mate = Players.FirstOrDefault(x => x != m && x.Team == m.Team && x.Car == c);
                        if (mate != null) { mate.Car = m.Car; mate.Ready = false; }
                        m.Car = c;
                    }
                }
                if (d.ContainsKey("skin")) m.Skin = System.Text.RegularExpressions.Regex.Replace(d.Str("skin") ?? "0", @"[^A-Za-z0-9 ()'\-]", "");
                if (d.ContainsKey("ready")) m.Ready = d.Bool("ready");
                Changed();
                return null;
            }
        }

        public void Leave(string token)
        {
            lock (_gate)
            {
                Member m = _members.FirstOrDefault(x => x.Token == token);
                if (m == null) return;
                _members.Remove(m);
                SeatLeft(m);
                Message = $"{m.Name} left.";
                ChatAdd(m.Name, m.Team, "left", true);
                Changed();
            }
        }

        public void Kick(string id)
        {
            lock (_gate)
            {
                Member m = _members.FirstOrDefault(x => x.Id == id && !x.IsHost);
                if (m == null) return;
                _members.Remove(m);
                SeatLeft(m);
                Message = $"{m.Name} was removed by the host.";
                ChatAdd(m.Name, m.Team, "removed", true);
                Changed();
            }
        }

        /// <summary>Host moves a player to the other team's free seat.</summary>
        public string MoveMember(string id, string team)
        {
            lock (_gate)
            {
                Member m = _members.FirstOrDefault(x => x.Id == id);
                if (m == null) return "That player isn't in the lobby anymore.";
                string why = TeamsLockedWhy();
                if (why != null) return why;
                if ((team != "red" && team != "blue") || (m.Team != "red" && m.Team != "blue") || m.Team == team) return null;
                if (Humans(team) >= TeamSize) return "That team is full.";
                m.Team = team;
                m.Ready = false;
                Message = $"The host moved {m.Name} to the {(team == "blue" ? "Blue" : "Red")} team.";
                Changed();
                return null;
            }
        }

        /// <summary>Host swaps two players of different teams, even when both teams are full (to balance them by skill).</summary>
        public string SwapMembers(string idA, string idB)
        {
            lock (_gate)
            {
                Member a = _members.FirstOrDefault(x => x.Id == idA), b = _members.FirstOrDefault(x => x.Id == idB);
                if (a == null || b == null) return "That player isn't in the lobby anymore.";
                string why = TeamsLockedWhy();
                if (why != null) return why;
                bool player(Member x) => x.Team == "red" || x.Team == "blue";
                if (!player(a) || !player(b) || a.Team == b.Team) return "Pick two players of different teams.";
                (a.Team, b.Team) = (b.Team, a.Team);
                a.Ready = b.Ready = false;
                Message = $"The host swapped {a.Name} and {b.Name}.";
                Changed();
                return null;
            }
        }

        private string TeamsLockedWhy()
        {
            if (Drafting) return "The teams are locked after the draft. The host can reset the draft.";
            return Phase != "lobby" ? "Teams are locked during a match." : null;
        }

        // ---- chat ----

        private class ChatLine { public int Id; public string Name, Team, Text; public bool System; }
        private readonly List<ChatLine> _chat = new List<ChatLine>();
        private int _chatId;
        private const int ChatKept = 60, ChatMaxLength = 200;

        private void ChatAdd(string name, string team, string text, bool system)
        {
            _chat.Add(new ChatLine { Id = ++_chatId, Name = name, Team = team, Text = text, System = system });
            if (_chat.Count > ChatKept) _chat.RemoveAt(0);
        }

        /// <summary>A member's chat message, seen by everyone in the lobby (also during the draft and the match).</summary>
        public string Say(string token, string text)
        {
            lock (_gate)
            {
                Member m = _members.FirstOrDefault(x => x.Token == token);
                if (m == null) return "You're not in this lobby anymore.";
                m.Seen = DateTime.UtcNow;
                text = new string((text ?? "").Where(ch => !char.IsControl(ch)).ToArray()).Trim();
                if (text.Length == 0) return null;
                if (text.Length > ChatMaxLength) text = text.Substring(0, ChatMaxLength);
                DateTime now = DateTime.UtcNow;
                m.Said.RemoveAll(t => (now - t).TotalSeconds > 10);
                if (m.Said.Count >= 6) return "Slow down: wait a few seconds before the next message.";
                m.Said.Add(now);
                ChatAdd(m.Name, m.Team, text, false);
                Changed();
                return null;
            }
        }

        // ---- match ----

        /// <summary>Why the match can't start now, or null.</summary>
        private string CantStart()
        {
            if (Phase == "draft") return "The draft isn't over yet.";
            if (Phase != "lobby") return "A match is already running.";
            if (!Players.Any()) return _members.Count == 0 ? "Nobody is in the lobby." : "No players yet: someone has to join a team.";
            foreach (string t in new[] { "blue", "red" })
                if (Humans(t) + BotsOn(t) == 0) return $"The {(t == "blue" ? "Blue" : "Red")} team is empty: add a bot or a player.";
            if (!Builds.Ready(Build)) return "The game isn't set up on the host's PC.";
            return null;
        }

        public string Start()
        {
            lock (_gate)
            {
                string why = CantStart();
                if (why != null) return why;
                Go();
                return null;
            }
        }

        // Start: the draft first when the lobby drafts and this match has none yet, else the match.
        private bool NeedsDraft => DraftEnabled && _draft == null;
        private void Go() { if (NeedsDraft) BeginDraft(); else BeginMatch(); }

        // ---- draft ----

        private void BeginDraft()
        {
            _countdownEnd = null;
            var slots = new Dictionary<string, int> { ["blue"] = Humans("blue") + BotsOn("blue"), ["red"] = Humans("red") + BotsOn("red") };
            string first = _rng.Next(2) == 0 ? "blue" : "red";
            _draft = new Draft(DraftBans, DraftPicks, first, slots, Catalog.Cars.Select(c => c.Id.ToString()), DraftTime);
            Phase = "draft";
            foreach (Member m in Players) m.Ready = false;
            Message = $"Draft started: the {(first == "blue" ? "Blue" : "Red")} team goes first.";
            DraftAdvance();
            Changed();
        }

        // Teams without players draft at random; after the last pick every player gets one of the team's cars.
        private void DraftAdvance()
        {
            while (!_draft.Done && Humans(_draft.Current.Team) == 0) _draft.Auto(_rng);
            if (!_draft.Done || Phase != "draft") return;
            Phase = "lobby";
            foreach (string team in new[] { "blue", "red" })
            {
                List<string> pool = _draft.Pool(team);
                var taken = new HashSet<string>();
                var humans = Players.Where(x => x.Team == team).ToList();
                foreach (Member m in humans) // keep a car a player already had, when the team picked it
                    if (pool.Contains(m.Car) && taken.Add(m.Car)) continue;
                    else m.Car = "";
                foreach (Member m in humans.Where(x => x.Car == ""))
                {
                    m.Car = pool.FirstOrDefault(c => !taken.Contains(c)) ?? "";
                    taken.Add(m.Car);
                }
            }
            foreach (Member m in Players) m.Ready = false;
            Message = "Draft done. Choose which of your team's cars you drive, then get ready.";
        }

        public string DraftAct(string token, string action, string car)
        {
            lock (_gate)
            {
                Member m = _members.FirstOrDefault(x => x.Token == token);
                if (m == null) return "You're not in this lobby anymore.";
                m.Seen = DateTime.UtcNow;
                if (_draft == null || Phase != "draft") return "No draft is running.";
                if (m.Team != "blue" && m.Team != "red") return "Only players draft.";
                string error = action == "lock" ? _draft.Lock(m.Team) : _draft.Select(m.Team, CleanCar(car));
                if (error != null) return error;
                if (action == "lock")
                {
                    Message = $"{m.Name} locked in.";
                    DraftAdvance();
                }
                Changed();
                return null;
            }
        }

        public void ResetDraft(string message = "The host reset the draft.")
        {
            lock (_gate)
            {
                if (_draft == null || (Phase != "lobby" && Phase != "draft")) return;
                _draft = null;
                Phase = "lobby";
                _countdownEnd = null;
                foreach (Member m in Players) m.Ready = false;
                Message = message;
                Changed();
            }
        }

        public void Stop()
        {
            lock (_gate)
            {
                if (Phase == "draft") { ResetDraft("The host stopped the draft."); return; }
                if (Phase == "lobby") return;
                Message = "The host stopped the match.";
                Game.StopServer(); // the ticker sees the exit and resets the lobby
                Changed();
            }
        }

        private void BeginMatch()
        {
            _countdownEnd = null;
            _matchNumber++;
            _matchStarted = false;
            foreach (Member m in _members) m.InMatch = m.Team != NoTeam;
            bool legacy = Builds.IsLegacy(Build);
            var o = new Game.ServerOptions
            {
                Build = Build, Port = GamePort, Players = Players.Count(), Arena = Arena, Score = Score == 3 ? 0 : Score,
                BluBots = BotsOn("blue"), RedBots = BotsOn("red"),
                BluDifficulty = Teams["blue"].Difficulty, RedDifficulty = Teams["red"].Difficulty,
                BluBotCars = BotCars("blue"), RedBotCars = BotCars("red"),
                BallSpeed = BallSpeed, HostName = _members.FirstOrDefault(m => m.IsHost && m.InMatch)?.Name,
            };
            if (legacy) // the game's own rules: its two arenas, cars picked in the game, original points to win
            {
                o.Arena = Catalog.LegacyArena(Arena);
                o.Score = Program.TestScore;
                o.BluBotCars = o.RedBotCars = "";
            }
            Phase = "starting";
            Message = "Starting the match server...";
            Changed();
            try { Game.StartServer(o); }
            catch (Exception e)
            {
                Phase = "lobby";
                Message = "Couldn't start the match server: " + e.Message;
                foreach (Member m in _members) m.InMatch = false;
                Changed();
            }
        }

        private string BotCars(string team)
        {
            if (_draft != null)
            {
                List<string> left = _draft.Pool(team).Where(c => !Players.Any(m => m.Team == team && m.Car == c)).ToList();
                var drafted = Enumerable.Range(0, BotsOn(team)).Select(i => i < left.Count ? left[i] : "random").ToList();
                return drafted.Count == 0 ? "" : string.Join(",", drafted);
            }
            // One entry per bot: the mod reads a lone "random" as "every bot random".
            var cars = Teams[team].Cars.Take(BotsOn(team)).ToList();
            while (cars.Count < BotsOn(team)) cars.Add("default");
            return cars.All(c => c == "default") ? "" : string.Join(",", cars);
        }

        // Once a second: drop members whose launcher went quiet, run the auto-start countdown, follow the server.
        private void Tick()
        {
            while (!_closed)
            {
                Thread.Sleep(500);
                lock (_gate)
                {
                    DateTime now = DateTime.UtcNow;
                    // Players in a match keep their seat for a reconnect; a watching spectator whose launcher is gone for a
                    // minute gives the seat to whoever wants it (that launcher closes its game when it sees it's out).
                    foreach (Member m in _members.Where(x => !x.IsHost && (now - x.Seen).TotalSeconds > (!x.InMatch ? 20 : x.Team == Spectator ? 60 : double.MaxValue)).ToList())
                    {
                        _members.Remove(m);
                        SeatLeft(m);
                        Message = $"{m.Name} lost connection to the lobby.";
                        ChatAdd(m.Name, m.Team, "lost", true);
                        Changed();
                    }
                    if (_seatsFreeing.RemoveAll(t => t <= now) > 0) Changed(); // a spectator seat is free again
                    if (Phase == "draft" && _draft.TurnEnds != null)
                    {
                        int left = (int)Math.Ceiling((_draft.TurnEnds.Value - now).TotalSeconds);
                        if (left != _draftSecondsShown) { _draftSecondsShown = left; Changed(); } // members' turn clocks tick
                    }
                    if (Phase == "draft" && _draft.TimedOut)
                    {
                        string team = _draft.Current.Team == "blue" ? "Blue" : "Red";
                        _draft.Fill(_rng);
                        Message = $"Time's up: the {team} team's turn was completed at random.";
                        DraftAdvance();
                        Changed();
                    }
                    if (Phase == "lobby")
                    {
                        bool allReady = Players.Any() && Players.All(x => x.Ready) && CantStart() == null;
                        if (AutoStart && allReady && _countdownEnd == null) { _countdownEnd = now.AddSeconds(3); Changed(); }
                        else if ((!AutoStart || !allReady) && _countdownEnd != null) { _countdownEnd = null; Changed(); }
                        if (_countdownEnd != null && now >= _countdownEnd) Go();
                    }
                    else if (Phase == "starting" && Game.ServerListening(GamePort))
                    {
                        Phase = "playing";
                        _matchStarted = true;
                        Message = "Match running. Your game is starting.";
                        Changed();
                    }
                    else if ((Phase == "starting" || Phase == "playing") && !Game.Running(Game.Server))
                    {
                        Phase = "lobby";
                        if (!Message.StartsWith("The host stopped"))
                            Message = _matchStarted ? "Match over. Get ready for the next one!" : "The match server stopped before the match began.";
                        foreach (Member m in _members) { m.InMatch = false; m.Ready = false; m.Seen = now; }
                        _draft = null; // every match gets its own draft
                        _seatsFreeing.Clear();
                        Changed();
                    }
                }
            }
        }

        // ---- views ----

        public int Rev { get { lock (_gate) return _rev; } }

        /// <summary>Blocks until the lobby changes after <paramref name="since"/> or the timeout passes.</summary>
        public void WaitChange(int since, int timeoutMs)
        {
            DateTime end = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            lock (_gate)
                while (_rev <= since && !_closed)
                {
                    int left = (int)(end - DateTime.UtcNow).TotalMilliseconds;
                    if (left <= 0 || !Monitor.Wait(_gate, left)) break;
                }
        }

        public Dictionary<string, object> Snapshot(string token)
        {
            lock (_gate)
            {
                Member me = _members.FirstOrDefault(x => x.Token == token);
                if (me != null) me.Seen = DateTime.UtcNow;
                var teams = new Dictionary<string, object>();
                foreach (string t in new[] { "blue", "red" })
                    teams[t] = new Dictionary<string, object>
                    {
                        ["bots"] = BotsOn(t), ["wantedBots"] = Teams[t].Bots, ["difficulty"] = Teams[t].Difficulty,
                        ["cars"] = Teams[t].Cars.Take(BotsOn(t)).ToArray(),
                    };
                return new Dictionary<string, object>
                {
                    ["rev"] = _rev,
                    ["host"] = _members.FirstOrDefault(x => x.IsHost)?.Name ?? "",
                    ["version"] = Program.Version,
                    ["build"] = Build,
                    ["phase"] = Phase,
                    ["message"] = Message,
                    ["arena"] = Arena,
                    ["ballSpeed"] = BallSpeed,
                    ["score"] = Score,
                    ["autoStart"] = AutoStart,
                    ["spectators"] = SpectatorsOn,
                    ["spectatorSeatsFreeing"] = Freeing,
                    ["draftOn"] = DraftEnabled,
                    ["draftBans"] = DraftBans,
                    ["draftPicks"] = DraftPicks,
                    ["draftTime"] = DraftTime,
                    ["draft"] = _draft?.Json(me?.Team),
                    ["nextIsDraft"] = NeedsDraft,
                    ["countdown"] = _countdownEnd == null ? -1 : Math.Max(0, (int)Math.Ceiling((_countdownEnd.Value - DateTime.UtcNow).TotalSeconds)),
                    ["cantStart"] = CantStart(),
                    ["gamePort"] = GamePort,
                    ["match"] = _matchNumber,
                    ["teams"] = teams,
                    ["me"] = me?.Id,
                    // System lines carry a key (joined, left, removed, lost) that each page translates.
                    ["chat"] = _chat.Select(c => new Dictionary<string, object>
                        { ["id"] = c.Id, ["name"] = c.Name, ["team"] = c.Team, ["text"] = c.Text, ["sys"] = c.System }).ToArray(),
                    ["members"] = _members.Select(m => new Dictionary<string, object>
                    {
                        ["id"] = m.Id, ["name"] = m.Name, ["team"] = m.Team, ["car"] = m.Car, ["skin"] = m.Skin,
                        ["ready"] = m.Ready, ["host"] = m.IsHost, ["inMatch"] = m.InMatch,
                    }).ToArray(),
                };
            }
        }

        /// <summary>Short description for the match list (HTTP /lobby/info and the discovery reply).</summary>
        public Dictionary<string, object> Info()
        {
            lock (_gate)
                return new Dictionary<string, object>
                {
                    ["hmmrevive"] = 1,
                    ["id"] = _lobbyId,
                    ["host"] = _members.FirstOrDefault(x => x.IsHost)?.Name ?? "",
                    ["version"] = Program.Version,
                    ["build"] = Build,
                    ["phase"] = Phase,
                    ["players"] = Players.Count(),
                    ["spectatorSeats"] = SpectatorsOn ? Math.Max(0, SpectatorSlots - Humans(Spectator) - Freeing) : 0,
                    ["draft"] = DraftEnabled,
                    ["arena"] = Arena,
                    ["ballSpeed"] = BallSpeed,
                    ["bots"] = BotsOn("blue") + BotsOn("red"),
                    ["lobbyPort"] = LobbyPort,
                };
        }

        // ---- network ----

        private Response Handle(Request r)
        {
            switch (r.Path)
            {
                case "/lobby/info":
                    return Response.Json(Info());
                case "/lobby/join":
                {
                    if (r.Method != "POST") return null;
                    var d = Js.Read(r.Body);
                    Member m = Join(d.Str("name"), d.Str("version"), d.ContainsKey("builds") ? d.List("builds").Select(o => o?.ToString()) : null, false, out string error);
                    Program.Log($"join from {r.Remote} as {d.Str("name")}: {error ?? "ok"}");
                    return m == null ? Response.Error(error, 409) : Response.Json(new Dictionary<string, object> { ["token"] = m.Token, ["id"] = m.Id });
                }
                case "/lobby/state":
                {
                    int since = int.TryParse(r.Arg("since"), out int s) ? s : 0;
                    WaitChange(since, 15000);
                    return Response.Json(Snapshot(r.Arg("token")));
                }
                case "/lobby/update":
                {
                    if (r.Method != "POST") return null;
                    var d = Js.Read(r.Body);
                    string error = Update(d.Str("token"), d);
                    return error == null ? Response.Json(new Dictionary<string, object> { ["ok"] = true }) : Response.Error(error);
                }
                case "/lobby/draft":
                {
                    if (r.Method != "POST") return null;
                    var d = Js.Read(r.Body);
                    string error = DraftAct(d.Str("token"), d.Str("action"), d.Str("car"));
                    return error == null ? Response.Json(new Dictionary<string, object> { ["ok"] = true }) : Response.Error(error);
                }
                case "/lobby/chat":
                {
                    if (r.Method != "POST") return null;
                    var d = Js.Read(r.Body);
                    string error = Say(d.Str("token"), d.Str("text"));
                    return error == null ? Response.Json(new Dictionary<string, object> { ["ok"] = true }) : Response.Error(error);
                }
                case "/lobby/leave":
                    if (r.Method != "POST") return null;
                    Leave(Js.Read(r.Body).Str("token"));
                    return Response.Json(new Dictionary<string, object> { ["ok"] = true });
            }
            return null;
        }

        // Discovery: a launcher looking for matches broadcasts "HMMREVIVE?"; the reply is the lobby info.
        private void DiscoveryLoop()
        {
            while (!_closed)
            {
                try
                {
                    var from = new IPEndPoint(IPAddress.Any, 0);
                    byte[] data = _discovery.Receive(ref from);
                    if (Encoding.ASCII.GetString(data) != Discovery.Probe) continue;
                    byte[] reply = Encoding.UTF8.GetBytes(Js.Write(Info()));
                    _discovery.Send(reply, reply.Length, from);
                }
                catch (ObjectDisposedException) { return; }
                catch (SocketException) { if (_closed) return; }
            }
        }
    }
}
