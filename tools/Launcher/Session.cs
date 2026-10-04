using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace HmmRevive.Launcher
{
    /// <summary>
    /// This player's seat in a lobby (someone else's, or our own when hosting): follows the lobby with long polls, sends
    /// our team/car/ready, and launches our game when the host's match server is up.
    /// </summary>
    public class Session
    {
        public readonly string Address; // host:port of the lobby as we reach it
        public readonly string GameIp;  // where our game connects (the host's address, 127.0.0.1 for our own lobby)
        private readonly string _token;
        public readonly string MemberId;
        private volatile bool _closed;
        public volatile Dictionary<string, object> Lobby;
        public volatile string Error;
        private int _launchedMatch = -1;
        private string _lastPhase;

        private Session(string address, string gameIp, string token, string id)
        {
            Address = address;
            GameIp = gameIp;
            _token = token;
            MemberId = id;
            new Thread(Follow) { IsBackground = true, Name = "session" }.Start();
        }

        public bool Closed => _closed;

        private static string Post(string url, object body, int timeoutMs = 5000)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = "POST";
            req.ContentType = "application/json";
            req.Timeout = req.ReadWriteTimeout = timeoutMs;
            req.Proxy = null;
            byte[] b = Encoding.UTF8.GetBytes(Js.Write(body));
            using (Stream s = req.GetRequestStream()) s.Write(b, 0, b.Length);
            return Read(req);
        }

        public static string Get(string url, int timeoutMs)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Timeout = req.ReadWriteTimeout = timeoutMs;
            req.Proxy = null;
            return Read(req);
        }

        // Error replies carry {"error": "..."}; surface that text instead of "(409) Conflict".
        private static string Read(HttpWebRequest req)
        {
            try
            {
                using (var res = (HttpWebResponse)req.GetResponse())
                using (var r = new StreamReader(res.GetResponseStream(), Encoding.UTF8)) return r.ReadToEnd();
            }
            catch (WebException e) when (e.Response != null)
            {
                string text;
                using (var r = new StreamReader(e.Response.GetResponseStream(), Encoding.UTF8)) text = r.ReadToEnd();
                string msg = null;
                try { msg = Js.Read(text).Str("error"); } catch { }
                throw new Exception(msg ?? e.Message);
            }
        }

        /// <summary>"100.x.y.z", "host:9697" or "[::1]:9697" → host and port.</summary>
        public static bool ParseAddress(string input, int defaultPort, out string host, out int port)
        {
            host = null;
            port = defaultPort;
            if (string.IsNullOrWhiteSpace(input)) return false;
            string a = input.Trim();
            if (a.StartsWith("http://")) a = a.Substring(7);
            a = a.TrimEnd('/');
            int colon = a.LastIndexOf(':');
            if (colon > 0 && a.IndexOf(':') == colon)
            {
                if (!int.TryParse(a.Substring(colon + 1), out port) || port <= 0 || port > 65535) return false;
                a = a.Substring(0, colon);
            }
            if (a.Length == 0 || a.Any(ch => !(char.IsLetterOrDigit(ch) || ch == '.' || ch == '-'))) return false;
            host = a;
            return true;
        }

        public static Session Join(string address, int defaultPort, out string error)
        {
            error = null;
            if (!ParseAddress(address, defaultPort, out string host, out int port)) { error = "That doesn't look like an address (e.g. 100.x.y.z)."; return null; }
            string baseUrl = $"http://{host}:{port}";
            try
            {
                var d = Js.Read(Post(baseUrl + "/lobby/join", new Dictionary<string, object>
                    { ["name"] = Settings.Data.Str("name"), ["version"] = Program.Version, ["builds"] = Builds.ReadyBuilds().ToArray() }));
                var s = new Session($"{host}:{port}", host, d.Str("token"), d.Str("id"));
                s.SendChoices(false);
                return s;
            }
            catch (Exception e)
            {
                error = e is WebException ? $"No lobby answered at {host}:{port}. Is the host's lobby open, and are you both on the same Tailscale/ZeroTier network?" : e.Message;
                return null;
            }
        }

        /// <summary>The host's own seat: joins the lobby object directly.</summary>
        public static Session JoinOwn(Lobby lobby, out string error)
        {
            var m = lobby.Join(Settings.Data.Str("name"), Program.Version, new[] { lobby.Build }, true, out error);
            if (m == null) return null;
            var s = new Session("127.0.0.1:" + lobby.LobbyPort, "127.0.0.1", m.Token, m.Id);
            s.SendChoices(false);
            return s;
        }

        /// <summary>Our entry in the last lobby snapshot, or null.</summary>
        private Dictionary<string, object> Me(Dictionary<string, object> d) =>
            d?.List("members").OfType<Dictionary<string, object>>().FirstOrDefault(m => m.Str("id") == d.Str("me"));

        /// <summary>After a draft our car is one of the team's picks, set in the lobby, not the one in our settings.</summary>
        private static bool Drafted(Dictionary<string, object> d) => d?.Obj("draft") != null;

        /// <summary>Sends our car and skin (from settings, or <paramref name="car"/>), and optionally a team or ready flag.</summary>
        public string SendChoices(bool? ready, string team = null, string car = null)
        {
            var lobby = Lobby;
            var body = new Dictionary<string, object> { ["token"] = _token };
            if (car == null && Drafted(lobby)) car = Me(lobby)?.Str("car"); // keep the drafted car; skins still follow it
            else body["car"] = car = car ?? Settings.Data.Str("car");
            body["skin"] = Settings.SkinFor(car ?? "");
            if (ready.HasValue) body["ready"] = ready.Value;
            if (team != null) body["team"] = team;
            try { Post($"http://{Address}/lobby/update", body); return null; }
            catch (Exception e) { return e.Message; }
        }

        /// <summary>Draft: select or unselect a car for our team's turn ("select"), or lock the turn in ("lock").</summary>
        public string DraftAct(string action, string car)
        {
            try { Post($"http://{Address}/lobby/draft", new Dictionary<string, object> { ["token"] = _token, ["action"] = action, ["car"] = car ?? "" }); return null; }
            catch (Exception e) { return e.Message; }
        }

        /// <summary>A lobby chat message.</summary>
        public string Say(string text)
        {
            try { Post($"http://{Address}/lobby/chat", new Dictionary<string, object> { ["token"] = _token, ["text"] = text ?? "" }); return null; }
            catch (Exception e) { return e.Message; }
        }

        /// <summary>Start our game again for the running match (it crashed or was closed); the server takes us back by name.</summary>
        public void Relaunch()
        {
            _launchedMatch = -1;
            if (Lobby != null) React(Lobby);
        }

        public void Leave()
        {
            _closed = true;
            // A spectator who leaves gives the seat back, and the game only has two: stop watching too. (A player who
            // leaves the lobby keeps playing the running match.)
            if (Me(Lobby)?.Str("team") == HmmRevive.Launcher.Lobby.Spectator) Game.StopClient();
            try { Post($"http://{Address}/lobby/leave", new Dictionary<string, object> { ["token"] = _token }, 2000); } catch { }
        }

        private void Follow()
        {
            int since = 0, failures = 0;
            while (!_closed)
            {
                try
                {
                    var d = Js.Read(Get($"http://{Address}/lobby/state?token={_token}&since={since}", 25000));
                    failures = 0;
                    since = d.Int("rev");
                    if (d.Str("me") == null)
                    {
                        if (Me(Lobby)?.Str("team") == HmmRevive.Launcher.Lobby.Spectator) Game.StopClient(); // the seat is someone else's now
                        Error = "You were removed from the lobby.";
                        _closed = true;
                        break;
                    }
                    Lobby = d;
                    React(d);
                }
                catch (Exception e)
                {
                    if (_closed) break;
                    if (++failures >= 4)
                    {
                        Error = "Lost connection to the lobby host (" + e.Message + ").";
                        _closed = true;
                        break;
                    }
                    Thread.Sleep(1500);
                }
            }
        }

        // The host's server is up and we're in this match: start our game once per match.
        private void React(Dictionary<string, object> d)
        {
            var me = Me(d);
            if (me == null) return;
            int match = d.Int("match");
            string phase = d.Str("phase");
            // Back in the lobby: after a finished match our game quits by itself; after the host stopped the match it
            // would sit on a dead connection, so close it (a few seconds later, to let a normal exit happen first).
            if (phase == "lobby" && _lastPhase != null && _lastPhase != "lobby" && Game.Running(Game.Client))
            {
                Process game = Game.Client;
                new Thread(() => { Thread.Sleep(4000); if (Game.Client == game) Game.StopClient(); }) { IsBackground = true }.Start();
            }
            _lastPhase = phase;
            if (d.Str("phase") == "playing" && me.Bool("inMatch") && _launchedMatch != match)
            {
                _launchedMatch = match;
                int score = d.Int("score");
                string car = Drafted(d) ? me.Str("car") : null; // null: the car in our settings
                try { Game.StartClient(d.Str("build") ?? Builds.Steam, GameIp, d.Int("gamePort", 9696), me.Str("team"), score == 3 ? 0 : score, car, HmmRevive.Launcher.Lobby.CleanCooldown(d.Str("cooldown"))); }
                catch (Exception e) { Error = "Couldn't start the game: " + e.Message; }
            }
        }
    }

    /// <summary>Finding lobbies: broadcast on ZeroTier/LAN, ask every online Tailscale peer, and check recent hosts.</summary>
    public static class Discovery
    {
        public const string Probe = "HMMREVIVE?";

        public static List<Dictionary<string, object>> Find(int lobbyPort)
        {
            var found = new Dictionary<string, Dictionary<string, object>>();
            var own = new HashSet<string>(Net.Addresses().Select(a => (string)a["ip"])) { "127.0.0.1" };
            void Add(string ip, Dictionary<string, object> info, string via)
            {
                if (info == null || info.Int("hmmrevive") != 1) return;
                int port = info.Int("lobbyPort", lobbyPort);
                info["address"] = port == lobbyPort ? ip : ip + ":" + port;
                info["via"] = via;
                info["own"] = own.Contains(ip);
                // A lobby answers on every network it shares with us; keep the best address (Tailscale, recent, then broadcast/LAN).
                string key = info.Str("id") ?? ip + ":" + port;
                int Rank(Dictionary<string, object> x) => (x["via"] as string) == "Tailscale" ? 0 : (x["via"] as string) == "recent" ? 1 : 2;
                lock (found) if (!found.TryGetValue(key, out var old) || Rank(info) < Rank(old)) found[key] = info;
            }

            var tasks = new List<Task>();
            tasks.Add(Task.Run(() =>
            {
                try
                {
                    using (var udp = new UdpClient(0) { EnableBroadcast = true })
                    {
                        byte[] probe = Encoding.ASCII.GetBytes(Probe);
                        foreach (IPAddress b in Net.Broadcasts())
                            try { udp.Send(probe, probe.Length, new IPEndPoint(b, lobbyPort)); } catch { }
                        DateTime end = DateTime.UtcNow.AddMilliseconds(1500);
                        while (DateTime.UtcNow < end)
                        {
                            udp.Client.ReceiveTimeout = Math.Max(1, (int)(end - DateTime.UtcNow).TotalMilliseconds);
                            var from = new IPEndPoint(IPAddress.Any, 0);
                            byte[] data;
                            try { data = udp.Receive(ref from); } catch (SocketException) { break; }
                            try { Add(from.Address.ToString(), Js.Read(Encoding.UTF8.GetString(data)), "broadcast"); } catch { }
                        }
                    }
                }
                catch (Exception e) { Program.Log("broadcast discovery failed: " + e.Message); }
            }));

            var direct = Net.TailscalePeers().Select(ip => (ip, "Tailscale")).ToList();
            direct.AddRange(Settings.Data.List("recent").Select(o => (o.ToString(), "recent")));
            foreach (var (addr, via) in direct)
            {
                if (!Session.ParseAddress(addr, lobbyPort, out string host, out int port)) continue;
                tasks.Add(Task.Run(() =>
                {
                    try { Add(host, Js.Read(Session.Get($"http://{host}:{port}/lobby/info", 1500)), via); }
                    catch { }
                }));
            }
            Task.WaitAll(tasks.ToArray(), 4000);
            lock (found) return found.Values.ToList();
        }
    }
}
