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
    /// Every member, the host included, talks to it through <see cref="Session"/>.
    /// </summary>
    public class Lobby
    {
        public class Member
        {
            public string Id, Token, Name, Team, Car = "", Skin = "0", Version;
            public bool Ready, IsHost, InMatch;
            public DateTime Seen = DateTime.UtcNow;
        }

        public class TeamSetup
        {
            public int Bots;                // wanted bots; fewer when humans take the slots
            public string Difficulty = "auto";
            public List<string> Cars = new List<string>(); // per bot slot: car id, "random" or "default"
        }

        public const int TeamSize = 4;
        private readonly object _gate = new object();
        private readonly List<Member> _members = new List<Member>();
        private int _rev = 1;
        private HttpServer _http;
        private UdpClient _discovery;
        private Thread _ticker;
        private volatile bool _closed;

        public readonly int LobbyPort, GamePort;
        public string Phase = "lobby"; // lobby, starting, playing
        public string Message = "";
        public int Arena = 1, Score = 3;
        public bool AutoStart = true;
        public readonly Dictionary<string, TeamSetup> Teams = new Dictionary<string, TeamSetup> { ["blue"] = new TeamSetup(), ["red"] = new TeamSetup() };
        private DateTime? _countdownEnd;
        private readonly string _lobbyId = NewId(6); // tells the same lobby apart when found on several networks
        private int _matchNumber;
        private bool _matchStarted; // the server reached a match (vs. died while loading)

        public Lobby(int lobbyPort, int gamePort)
        {
            LobbyPort = lobbyPort;
            GamePort = gamePort;
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
            AutoStart = h.Bool("autoStart", AutoStart);
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
            var d = new Dictionary<string, object> { ["arena"] = Arena, ["score"] = Score, ["autoStart"] = AutoStart };
            foreach (var t in Teams)
                d[t.Key] = new Dictionary<string, object> { ["bots"] = t.Value.Bots, ["difficulty"] = t.Value.Difficulty, ["cars"] = t.Value.Cars.ToArray() };
            return d;
        }

        /// <summary>Host changes arena, score, bots or auto-start (from the host's own page).</summary>
        public void Configure(Dictionary<string, object> h)
        {
            lock (_gate)
            {
                LoadSetup(h);
                Settings.Set("host", SetupJson());
                _countdownEnd = null;
                Changed();
            }
        }

        // ---- members ----

        private int Humans(string team) => _members.Count(m => m.Team == team);
        private int BotsOn(string team) => Math.Max(0, Math.Min(Teams[team].Bots, TeamSize - Humans(team)));

        public Member Join(string name, string version, bool isHost, out string error)
        {
            error = null;
            name = Settings.CleanName(name);
            lock (_gate)
            {
                if (_closed) { error = "The lobby is closed."; return null; }
                if (name == null) { error = "Pick a player name first (letters and digits)."; return null; }
                if (version != Program.Version) { error = $"The host runs HMM Revive {Program.Version} and you have {version}. Everyone needs the same version."; return null; }
                Member old = _members.FirstOrDefault(m => m.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (old != null)
                {
                    // Same name again: a restarted launcher reclaims its slot once the old one stopped answering.
                    if ((DateTime.UtcNow - old.Seen).TotalSeconds < 8 && !isHost) { error = $"Someone called {name} is already in this lobby. Change your name."; return null; }
                    _members.Remove(old);
                }
                if (_members.Count >= 2 * TeamSize) { error = "The lobby is full (8 players)."; return null; }
                string team = Humans("blue") <= Humans("red") ? "blue" : "red";
                if (Humans(team) >= TeamSize) team = team == "blue" ? "red" : "blue";
                var m = new Member { Id = NewId(4), Token = NewId(16), Name = name, Team = team, Version = version, IsHost = isHost };
                _members.Add(m);
                Message = $"{name} joined.";
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
                if ((team == "red" || team == "blue") && team != m.Team)
                {
                    if (Phase != "lobby") return "Teams are locked during a match.";
                    if (Humans(team) >= TeamSize) return "That team is full.";
                    m.Team = team;
                    m.Ready = false;
                }
                if (d.ContainsKey("car")) m.Car = CleanCar(d.Str("car")) is string c && c != "default" && c != "random" ? c : "";
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
                Message = $"{m.Name} left.";
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
                Message = $"{m.Name} was removed by the host.";
                Changed();
            }
        }

        public void MoveMember(string id, string team)
        {
            lock (_gate)
            {
                Member m = _members.FirstOrDefault(x => x.Id == id);
                if (m == null || Phase != "lobby" || (team != "red" && team != "blue") || m.Team == team || Humans(team) >= TeamSize) return;
                m.Team = team;
                m.Ready = false;
                Changed();
            }
        }

        // ---- match ----

        /// <summary>Why the match can't start now, or null.</summary>
        private string CantStart()
        {
            if (Phase != "lobby") return "A match is already running.";
            if (_members.Count == 0) return "Nobody is in the lobby.";
            foreach (string t in new[] { "blue", "red" })
                if (Humans(t) + BotsOn(t) == 0) return $"The {(t == "blue" ? "Blue" : "Red")} team is empty: add a bot or a player.";
            if (!Paths.GameReady) return "The game isn't set up on the host's PC.";
            return null;
        }

        public string Start()
        {
            lock (_gate)
            {
                string why = CantStart();
                if (why != null) return why;
                BeginMatch();
                return null;
            }
        }

        public void Stop()
        {
            lock (_gate)
            {
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
            foreach (Member m in _members) m.InMatch = true;
            var o = new Game.ServerOptions
            {
                Port = GamePort, Players = _members.Count, Arena = Arena, Score = Score == 3 ? 0 : Score,
                BluBots = BotsOn("blue"), RedBots = BotsOn("red"),
                BluDifficulty = Teams["blue"].Difficulty, RedDifficulty = Teams["red"].Difficulty,
                BluBotCars = BotCars("blue"), RedBotCars = BotCars("red"),
            };
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
                    foreach (Member m in _members.Where(x => !x.IsHost && !x.InMatch && (now - x.Seen).TotalSeconds > 20).ToList())
                    {
                        _members.Remove(m);
                        Message = $"{m.Name} lost connection to the lobby.";
                        Changed();
                    }
                    if (Phase == "lobby")
                    {
                        bool allReady = _members.Count > 0 && _members.All(x => x.Ready) && CantStart() == null;
                        if (AutoStart && allReady && _countdownEnd == null) { _countdownEnd = now.AddSeconds(3); Changed(); }
                        else if ((!AutoStart || !allReady) && _countdownEnd != null) { _countdownEnd = null; Changed(); }
                        if (_countdownEnd != null && now >= _countdownEnd) BeginMatch();
                    }
                    else if (Phase == "starting" && Game.ServerListening(GamePort))
                    {
                        Phase = "playing";
                        _matchStarted = true;
                        Message = "Match running. Your game is starting.";
                        Changed();
                    }
                    else if (Phase != "lobby" && !Game.Running(Game.Server))
                    {
                        Phase = "lobby";
                        if (!Message.StartsWith("The host stopped"))
                            Message = _matchStarted ? "Match over. Get ready for the next one!" : "The match server stopped before the match began.";
                        foreach (Member m in _members) { m.InMatch = false; m.Ready = false; m.Seen = now; }
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
                    ["phase"] = Phase,
                    ["message"] = Message,
                    ["arena"] = Arena,
                    ["score"] = Score,
                    ["autoStart"] = AutoStart,
                    ["countdown"] = _countdownEnd == null ? -1 : Math.Max(0, (int)Math.Ceiling((_countdownEnd.Value - DateTime.UtcNow).TotalSeconds)),
                    ["cantStart"] = CantStart(),
                    ["gamePort"] = GamePort,
                    ["match"] = _matchNumber,
                    ["teams"] = teams,
                    ["me"] = me?.Id,
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
                    ["phase"] = Phase,
                    ["players"] = _members.Count,
                    ["arena"] = Arena,
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
                    Member m = Join(d.Str("name"), d.Str("version"), false, out string error);
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
