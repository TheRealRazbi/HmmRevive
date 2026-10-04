using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading;

namespace HmmRevive.Launcher
{
    /// <summary>
    /// HMM Revive launcher. Serves the launcher page on 127.0.0.1 (only this PC can open it) and opens it in an app window.
    /// Hosting also opens the lobby on the lobby port (TCP+UDP 9697) for the other players.
    ///   --root DIR         kit/repo folder (default: found from the exe's folder)
    ///   --instance NAME    game copy folder under root (default instance)
    ///   --ui-port N        page port (default 9690, next free one if taken)
    ///   --lobby-port N     lobby port (default 9697)   --game-port N   match server port (default 9696)
    ///   --settings FILE    settings file (default launcher-settings.json in root; a second launcher on one PC for tests)
    ///   --no-browser       don't open a window        --test-background   hidden, muted games (automated tests)
    ///   --test-score N     points to win on an old build's match (tests of the match end; players keep the game's rule)
    /// </summary>
    public static class Program
    {
        public static string Version
        {
            get { Version v = typeof(Program).Assembly.GetName().Version; return v.Major + "." + v.Minor; }
        }

        private static readonly object LogGate = new object();
        private static string _logFile;

        public static void Log(string msg)
        {
            string line = $"[{DateTime.Now:HH:mm:ss}] {msg}";
            lock (LogGate)
            {
                Console.WriteLine(line);
                try { if (_logFile != null) File.AppendAllText(_logFile, line + Environment.NewLine); } catch { }
            }
        }

        private static int _uiPort, _lobbyPort = 9697, _gamePort = 9696;
        public static int TestScore;
        private static Lobby _lobby;
        private static Session _session;
        private static readonly object Gate = new object();
        private static string _notice = "";

        public static int Main(string[] args)
        {
            string root = null, instance = "instance", settings = null;
            int wantUi = 9690;
            bool browser = true;
            for (int i = 0; i < args.Length; i++)
            {
                string Next() => i + 1 < args.Length ? args[++i] : "";
                switch (args[i])
                {
                    case "--root": root = Path.GetFullPath(Next()); break;
                    case "--instance": instance = Next(); break;
                    case "--settings": settings = Path.GetFullPath(Next()); break;
                    case "--ui-port": wantUi = int.Parse(Next()); break;
                    case "--lobby-port": _lobbyPort = int.Parse(Next()); break;
                    case "--game-port": _gamePort = int.Parse(Next()); break;
                    case "--no-browser": browser = false; break;
                    case "--test-background": Game.TestBackground = true; break;
                    case "--test-score": TestScore = int.Parse(Next()); break;
                }
            }
            Console.Title = "HMM Revive " + Version;
            Paths.Init(root, instance);
            if (settings != null) Paths.Settings = settings;
            Catalog.Load();
            Settings.Load();

            // Take the first free port. A taken one may be this launcher already running (opened twice): then just show it.
            HttpServer ui = null;
            foreach (int p in Candidates(wantUi))
            {
                ui = new HttpServer(IPAddress.Loopback, p, HandleLocal);
                if (ui.Start()) break;
                ui = null;
                try
                {
                    var hello = Js.Read(Session.Get($"http://127.0.0.1:{p}/api/hello", 1500));
                    if (hello.Str("app") == "hmmrevive-launcher" && string.Equals(hello.Str("settings"), Paths.Settings, StringComparison.OrdinalIgnoreCase))
                    {
                        Console.WriteLine("HMM Revive is already running; opening its window.");
                        if (browser) Browser.Open($"http://127.0.0.1:{p}/");
                        return 0;
                    }
                }
                catch { }
            }
            if (ui == null)
            {
                ui = new HttpServer(IPAddress.Loopback, 0, HandleLocal);
                ui.Start();
            }
            _uiPort = ui.Port;
            _logFile = Path.ChangeExtension(Paths.Settings, ".log"); // launcher-settings.log, fresh each start
            try { File.Delete(_logFile); } catch { }
            string url = $"http://127.0.0.1:{_uiPort}/";
            Console.WriteLine($"HMM Revive {Version}");
            Console.WriteLine($"Launcher page: {url}");
            Console.WriteLine("Keep this window open while you play. Close it to quit the launcher (a running match keeps going).");
            Console.WriteLine();
            Log($"root={Paths.Root} instance={Paths.Instance} ui={_uiPort} lobby={_lobbyPort} game={_gamePort}");
            if (browser) Browser.Open(url);
            while (true)
            {
                Firewall.Refresh(); // netsh is slow; the page reads the cached answer
                Thread.Sleep(30000);
            }
            return 0;
        }

        private static IEnumerable<int> Candidates(int first) => Enumerable.Range(0, 6).Select(i => first - i);

        // ---- local API (the page) ----

        private static Response Ok(object extra = null) => Response.Json(extra ?? new Dictionary<string, object> { ["ok"] = true });

        private static Response HandleLocal(Request r)
        {
            // Only our own page may call this: the Host header stops DNS rebinding, and the custom header on POST forces a
            // CORS preflight that we never answer, so other websites open in the browser can't press our buttons.
            string host = r.Header("Host") ?? "";
            if (host != $"127.0.0.1:{_uiPort}" && host != $"localhost:{_uiPort}") return Response.Error("forbidden", 403);
            if (r.Method == "POST" && r.Header("X-HMM-Revive") != "1") return Response.Error("forbidden", 403);
            if (r.Method == "GET" && r.Path.StartsWith("/img/")) return Picture(r.Path);
            if (r.Method == "GET" && !r.Path.StartsWith("/api/")) return Static(r.Path);
            var d = r.Method == "POST" ? Js.Read(r.Body) : null;
            switch (r.Path)
            {
                case "/api/hello": return Response.Json(new Dictionary<string, object> { ["app"] = "hmmrevive-launcher", ["settings"] = Paths.Settings });
                case "/api/catalog": return Response.Json(Catalog.ToJson());
                case "/api/state": return Response.Json(State());
                case "/api/settings": return SaveSettings(d);
                case "/api/discover": return Response.Json(new Dictionary<string, object> { ["lobbies"] = Discovery.Find(_lobbyPort).ToArray() });
                case "/api/host/open": return HostOpen(d?.Str("build") ?? Settings.Data.Str("hostBuild") ?? Builds.Steam);
                case "/api/host/close": return HostClose();
                case "/api/host/configure":
                    if (_lobby == null) return Response.Error("You're not hosting.");
                    _lobby.Configure(d);
                    return Ok();
                case "/api/host/start":
                    if (_lobby == null) return Response.Error("You're not hosting.");
                    string why = _lobby.Start();
                    return why == null ? Ok() : Response.Error(why);
                case "/api/host/stop":
                    _lobby?.Stop();
                    return Ok();
                case "/api/host/kick":
                    _lobby?.Kick(d.Str("id"));
                    return Ok();
                case "/api/host/draft-reset":
                    _lobby?.ResetDraft();
                    return Ok();
                case "/api/host/move":
                {
                    if (_lobby == null) return Response.Error("You're not hosting.");
                    string err = _lobby.MoveMember(d.Str("id"), d.Str("team"));
                    return err == null ? Ok() : Response.Error(err);
                }
                case "/api/host/swap":
                {
                    if (_lobby == null) return Response.Error("You're not hosting.");
                    string err = _lobby.SwapMembers(d.Str("a"), d.Str("b"));
                    return err == null ? Ok() : Response.Error(err);
                }
                case "/api/join": return JoinLobby(d.Str("address"));
                case "/api/leave":
                    LeaveLobby();
                    return Ok();
                case "/api/lobby/update":
                {
                    Session s = _session;
                    if (s == null) return Response.Error("You're not in a lobby.");
                    string err = s.SendChoices(d.ContainsKey("ready") ? d.Bool("ready") : (bool?)null, d.Str("team"), d.Str("car"));
                    return err == null ? Ok() : Response.Error(err);
                }
                case "/api/lobby/draft":
                {
                    Session s = _session;
                    if (s == null) return Response.Error("You're not in a lobby.");
                    string err = s.DraftAct(d.Str("action"), d.Str("car"));
                    return err == null ? Ok() : Response.Error(err);
                }
                case "/api/lobby/chat":
                {
                    Session s = _session;
                    if (s == null) return Response.Error("You're not in a lobby.");
                    string err = s.Say(d.Str("text"));
                    return err == null ? Ok() : Response.Error(err);
                }
                case "/api/lobby/relaunch":
                    _session?.Relaunch();
                    return Ok();
                case "/api/quickjoin": return QuickJoin(d.Str("address"));
                case "/api/game/stop":
                    Game.StopClient();
                    return Ok();
                case "/api/setup":
                {
                    // {} = the Steam copy, {build} = set up/update that copy, {gameDir} = a folder the player chose.
                    string build = d.Str("build") ?? Builds.Steam;
                    string dir = Builds.CopyDir(build);
                    if (d.Str("gameDir") != null)
                    {
                        dir = Game.GameDirOf(d.Str("gameDir"), out string notGame);
                        if (dir == null) return Response.Error(notGame);
                        build = Builds.Detect(dir);
                        if (build == Builds.Unsupported) return Response.Error("That copy of the game isn't supported.");
                        if (Builds.IsLegacy(build))
                        {
                            var copies = Settings.Data.Obj("copies") ?? new Dictionary<string, object>();
                            copies[build] = dir; // kept for later updates
                            Settings.Data["copies"] = copies;
                        }
                        else Settings.Data["gameDir"] = dir;
                        Settings.Save();
                    }
                    Game.RunSetup(build, dir);
                    return Ok(new Dictionary<string, object> { ["build"] = build });
                }
                case "/api/setup/browse":
                    return Ok(new Dictionary<string, object> { ["file"] = Game.BrowseForGame() });
                case "/api/firewall":
                    return Ok(new Dictionary<string, object> { ["ok"] = Firewall.Setup(_gamePort, _lobbyPort) });
                case "/api/open-folder":
                    Process.Start("explorer.exe", "\"" + Paths.Root + "\"");
                    return Ok();
            }
            return null;
        }

        private static Response SaveSettings(Dictionary<string, object> d)
        {
            if (d.ContainsKey("name"))
            {
                string n = Settings.CleanName(d.Str("name"));
                if (n == null) return Response.Error("Use letters, digits, - or _ in your name.");
                if (_session != null && n != Settings.Data.Str("name")) return Response.Error("Leave the lobby to change your name.");
                Settings.Data["name"] = n;
            }
            if (d.ContainsKey("car") && Catalog.CarId(d.Str("car")) is int car) Settings.Data["car"] = car.ToString();
            if (d.ContainsKey("skin"))
            {
                // skinCar: the car the skin is for when it isn't the settings' car (a drafted car in the lobby)
                string carName = Catalog.CarName(d.Str("skinCar") ?? Settings.Data.Str("car"));
                var skins = Settings.Data.Obj("skins") ?? new Dictionary<string, object>();
                if (carName != null) skins[carName] = System.Text.RegularExpressions.Regex.Replace(d.Str("skin") ?? "0", @"[\s#""']", "");
                Settings.Data["skins"] = skins;
            }
            if (d.ContainsKey("emotes"))
            {
                int count = Game.EmoteCount;
                var picks = d.List("emotes").Select(o => int.TryParse(o?.ToString(), out int n) ? n : -1).ToList();
                if (picks.Count != 4 || picks.Any(n => n < 0 || (count > 0 && n >= count))) return Response.Error("Pick four emotes.");
                Settings.Data["emotes"] = picks.Cast<object>().ToList();
            }
            if (d.ContainsKey("width")) Settings.Data["width"] = Math.Max(0, d.Int("width"));
            if (d.ContainsKey("height")) Settings.Data["height"] = Math.Max(0, d.Int("height"));
            if (d.ContainsKey("fullscreen")) Settings.Data["fullscreen"] = d.Bool("fullscreen", true);
            if (d.Str("lang") == "en" || d.Str("lang") == "pt") Settings.Data["lang"] = d.Str("lang"); // page language
            Settings.Save();
            _session?.SendChoices(null); // car/skin changes show in the lobby
            return Ok();
        }

        private static Response HostOpen(string build)
        {
            lock (Gate)
            {
                if (_lobby != null) return Ok();
                if (!Builds.Ready(build)) return Response.Error("Set up the game first.");
                Settings.Set("hostBuild", build);
                LeaveLobby();
                var lobby = new Lobby(_lobbyPort, _gamePort, build);
                if (!lobby.Open()) return Response.Error($"Port {_lobbyPort} is taken: is another HMM Revive launcher hosting on this PC?");
                _lobby = lobby;
                _session = Session.JoinOwn(lobby, out string error);
                if (_session == null)
                {
                    lobby.Close();
                    _lobby = null;
                    return Response.Error(error);
                }
                return Ok();
            }
        }

        private static Response HostClose()
        {
            lock (Gate)
            {
                _session?.Leave();
                _session = null;
                _lobby?.Close();
                _lobby = null;
                return Ok();
            }
        }

        private static Response JoinLobby(string address)
        {
            lock (Gate)
            {
                if (_lobby != null) return Response.Error("Close your own lobby first.");
                LeaveLobby();
                Session s = Session.Join(address, _lobbyPort, out string error);
                if (s == null) return Response.Error(error);
                _session = s;
                Settings.AddRecent(address.Trim());
                return Ok();
            }
        }

        private static void LeaveLobby()
        {
            lock (Gate)
            {
                _session?.Leave();
                _session = null;
            }
        }

        // Straight into a match server, like play.bat (servers started by host.bat, or a relay address).
        private static Response QuickJoin(string address)
        {
            // host.bat servers and relays run the Steam build; without it, the old build that is set up.
            string build = Builds.ReadyBuilds().FirstOrDefault();
            if (build == null) return Response.Error("Set up the game first.");
            if (!Session.ParseAddress(address, _gamePort, out string host, out int port)) return Response.Error("That doesn't look like an address.");
            Game.StartClient(build, host, port, null, 0);
            Settings.AddRecent(address.Trim());
            return Ok();
        }

        private static Dictionary<string, object> State()
        {
            Session s = _session;
            if (s != null && s.Closed)
            {
                _notice = s.Error ?? "";
                lock (Gate) if (_session == s) _session = null;
                s = null;
            }
            Game.EnsureImages();
            string kit = Game.KitVersion, game = Game.GameVersion;
            string setupLog;
            lock (Game.SetupLog) setupLog = Game.SetupLog.ToString();
            return new Dictionary<string, object>
            {
                ["version"] = Version,
                ["settings"] = Settings.Data,
                ["root"] = Paths.Root,
                ["instance"] = Paths.Instance,
                ["pathOk"] = Paths.PathIsPlain,
                ["gameReady"] = Paths.GameReady,
                ["gameVersion"] = game,
                ["kitVersion"] = kit,
                ["needsUpdate"] = Paths.GameReady && kit != null && game != kit,
                ["canSetup"] = Paths.Patcher != null,
                ["setupStatus"] = Game.SetupStatus,
                ["setupBuild"] = Game.SetupBuild,
                ["copies"] = Builds.CopiesJson(),
                ["builds"] = Builds.ReadyBuilds().ToArray(),
                ["hostBuild"] = _lobby?.Build,
                ["setupLog"] = setupLog.Length > 4000 ? setupLog.Substring(setupLog.Length - 4000) : setupLog,
                ["gameDir"] = Game.FindGame(),
                ["addresses"] = Net.Addresses().ToArray(),
                ["hosting"] = _lobby != null,
                ["hostSetup"] = _lobby?.SetupJson(),
                ["lobby"] = s?.Lobby,
                ["lobbyAddress"] = s?.Address,
                ["notice"] = _notice,
                ["gameRunning"] = Game.Running(Game.Client),
                ["images"] = Game.ImagesReady, // skin and emote pictures (/img/...)
                ["emoteCount"] = Game.EmoteCount,
                ["serverRunning"] = Game.Running(Game.Server),
                ["firewall"] = Firewall.Cached,
                ["lobbyPort"] = _lobbyPort,
                ["gamePort"] = _gamePort,
            };
        }

        // ---- page files (embedded web/*) ----

        // Skin and emote pictures the game made on this PC (Game.EnsureImages): /img/skins/CAR-N.jpg, /img/emotes/N.png.
        private static Response Picture(string path)
        {
            var m = System.Text.RegularExpressions.Regex.Match(path, @"^/img/(skins/\d+-\d+\.jpg|emotes/\d+\.png)$");
            string file = m.Success ? Path.Combine(Game.ImagesDir, m.Groups[1].Value.Replace('/', '\\')) : null;
            if (file == null || !File.Exists(file)) return Response.Error("not found", 404);
            return new Response { Body = File.ReadAllBytes(file), ContentType = file.EndsWith(".png") ? "image/png" : "image/jpeg" };
        }

        private static Response Static(string path)
        {
            if (path == "/") path = "/index.html";
            string name = "web" + path;
            using (Stream st = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
            {
                if (st == null) return Response.Error("not found", 404);
                var ms = new MemoryStream();
                st.CopyTo(ms);
                string type = path.EndsWith(".html") ? "text/html; charset=utf-8" : path.EndsWith(".js") ? "text/javascript; charset=utf-8"
                    : path.EndsWith(".css") ? "text/css; charset=utf-8" : path.EndsWith(".svg") ? "image/svg+xml" : "application/octet-stream";
                return new Response { Body = ms.ToArray(), ContentType = type };
            }
        }
    }

    /// <summary>Opens the page as an app window (Edge or Chrome --app: no address bar), else in the default browser.</summary>
    public static class Browser
    {
        public static void Open(string url)
        {
            foreach (string exe in new[] { "msedge.exe", "chrome.exe" })
            {
                string path = AppPath(exe);
                if (path == null) continue;
                try
                {
                    Process.Start(new ProcessStartInfo(path, $"--app={url} --window-size=1280,860") { UseShellExecute = false });
                    return;
                }
                catch { }
            }
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception e) { Program.Log("can't open a browser: " + e.Message + ". Open " + url + " yourself."); }
        }

        private static string AppPath(string exe)
        {
            foreach (var hive in new[] { Microsoft.Win32.Registry.CurrentUser, Microsoft.Win32.Registry.LocalMachine })
                try
                {
                    using (var k = hive.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\" + exe))
                        if (k?.GetValue(null) is string p && File.Exists(p)) return p;
                }
                catch { }
            return null;
        }
    }
}
