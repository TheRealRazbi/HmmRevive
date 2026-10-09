using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace HmmRevive.Launcher
{
    /// <summary>Where things are: the kit layout (exe next to instance\, mod\, Patcher.exe, skins.txt) or this repo's.</summary>
    public static class Paths
    {
        public static string Root, Instance, Settings;

        public static void Init(string root, string instanceName)
        {
            string exeDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
            Root = root ?? FindRoot(exeDir);
            Instance = Path.Combine(Root, instanceName);
            Settings = Path.Combine(Root, "launcher-settings.json");
        }

        // The folder that holds instance\ (or the kit's mod\ before setup); the repo's build\launcher\ looks two levels up.
        private static string FindRoot(string dir)
        {
            for (string d = dir; d != null; d = Path.GetDirectoryName(d))
                if (Directory.Exists(Path.Combine(d, "instance")) || File.Exists(Path.Combine(d, "Patcher.exe")) || File.Exists(Path.Combine(d, "VERSION")))
                    return d;
            return dir;
        }

        public static string GameExe => Path.Combine(Instance, "HMM.exe");
        public static string LauncherExe => Process.GetCurrentProcess().MainModule.FileName;
        public static string Patcher => First(Path.Combine(Root, "Patcher.exe"), Path.Combine(Root, "build", "patcher", "Patcher.exe"));
        public static string ModDir => Directory.Exists(Path.Combine(Root, "mod")) && File.Exists(Path.Combine(Root, "mod", "HmmRevive.dll"))
            ? Path.Combine(Root, "mod") : Path.Combine(Root, "build", "mod");
        public static string SkinsFile => First(Path.Combine(Root, "skins.txt"), Path.Combine(Root, "launcher", "skins.txt"));

        private static string First(params string[] candidates) => candidates.FirstOrDefault(File.Exists);

        /// <summary>The game's native logger crashes HMM.exe under a path with non-ASCII letters.</summary>
        public static bool PathIsPlain => !Regex.IsMatch(Root, @"[^\x20-\x7E]");

        public static bool GameReady => File.Exists(GameExe);
    }

    /// <summary>Launcher settings (launcher-settings.json next to the kit): player, display and host choices.</summary>
    public static class Settings
    {
        private static readonly object Gate = new object();
        public static Dictionary<string, object> Data = new Dictionary<string, object>();

        public static void Load()
        {
            lock (Gate)
            {
                try { if (File.Exists(Paths.Settings)) Data = Js.Read(File.ReadAllText(Paths.Settings)); }
                catch (Exception e) { Program.Log("settings unreadable, starting fresh: " + e.Message); }
                if (!Data.ContainsKey("name")) Import();
                Default("name", Environment.UserName);
                Default("car", "1");
                Default("skins", new Dictionary<string, object>());
                Default("width", 0);
                Default("height", 0);
                Default("fullscreen", true);
                Default("recent", new object[0]);
                Default("host", new Dictionary<string, object>());
                Data["name"] = CleanName(Data.Str("name")) ?? "Player";
            }
        }

        private static void Default(string k, object v)
        {
            if (!Data.ContainsKey(k) || Data[k] == null) Data[k] = v;
        }

        // First run: take name, car and skins from the .bat launchers' settings.
        private static void Import()
        {
            foreach (string f in new[] { Path.Combine(Paths.Root, "play-settings.json"), Path.Combine(Paths.Root, "launcher", "settings.json") })
            {
                if (!File.Exists(f)) continue;
                try
                {
                    var old = Js.Read(File.ReadAllText(f));
                    if (old.Str("Name") != null) Data["name"] = old.Str("Name");
                    if (!string.IsNullOrEmpty(old.Str("Car"))) Data["car"] = Catalog.CarId(old.Str("Car"))?.ToString() ?? "1";
                    if (old.Obj("Skins") != null) Data["skins"] = old.Obj("Skins");
                    if (old.Int("Width") > 0) { Data["width"] = old.Int("Width"); Data["height"] = old.Int("Height"); }
                    if (old.ContainsKey("Fullscreen")) Data["fullscreen"] = old.Bool("Fullscreen", true);
                    Program.Log("imported settings from " + f);
                    return;
                }
                catch { }
            }
        }

        public static void Save()
        {
            lock (Gate)
            {
                try { File.WriteAllText(Paths.Settings, Js.Write(Data)); }
                catch (Exception e) { Program.Log("can't save settings: " + e.Message); }
            }
        }

        public static void Set(string key, object value)
        {
            lock (Gate) Data[key] = value;
            Save();
        }

        public static void AddRecent(string address)
        {
            lock (Gate)
            {
                var list = Data.List("recent").Select(o => o.ToString()).Where(a => !a.Equals(address, StringComparison.OrdinalIgnoreCase)).ToList();
                list.Insert(0, address);
                Data["recent"] = list.Take(8).ToArray();
            }
            Save();
        }

        /// <summary>Player names travel in the login ("name#car#skin#team") and in file names: letters, digits, - and _.</summary>
        public static string CleanName(string name)
        {
            if (name == null) return null;
            string n = Regex.Replace(name, @"[^A-Za-z0-9_\-]", "");
            if (n.Length > 16) n = n.Substring(0, 16);
            return n.Length == 0 ? null : n;
        }

        /// <summary>The four emotes for the emote wheel ("3,7,0,12", numbers in hmmrevive-emotes.txt), or null for the game's first four.</summary>
        public static string Emotes()
        {
            var list = Data.List("emotes").Select(o => int.TryParse(o?.ToString(), out int n) ? n : -1).ToList();
            return list.Count == 4 && list.All(n => n >= 0) ? string.Join(",", list) : null;
        }

        public static string SkinFor(string carId)
        {
            string car = Catalog.CarName(carId);
            var skins = Data.Obj("skins");
            return (car != null ? skins.Str(car) : null) ?? "0";
        }
    }

    /// <summary>Cars and skins from skins.txt ("carId TAB car TAB number TAB skin"), arenas from the game's arena config.</summary>
    public static class Catalog
    {
        public class Car { public int Id; public string Name; public List<string> Skins = new List<string>(); }

        public static readonly List<Car> Cars = new List<Car>();

        public static readonly object[] Arenas =
        {
            new Dictionary<string, object> { ["id"] = 1, ["name"] = "Legacy: Temple of Sacrifice" },
            new Dictionary<string, object> { ["id"] = 2, ["name"] = "Metal God Arena" },
            new Dictionary<string, object> { ["id"] = 3, ["name"] = "Cursed Necropolis" },
            new Dictionary<string, object> { ["id"] = 4, ["name"] = "Sacrifice Sanctuary" },
            new Dictionary<string, object> { ["id"] = 13, ["name"] = "Sacrifice Sanctuary (alpha version)" },
            new Dictionary<string, object> { ["id"] = 5, ["name"] = "Arena Void (test map)" },
        };

        /// <summary>The 2017 builds' arenas (their GameArenaConfig: 0 = tutorial, 1 = Arena_Monster, 2 = Arena_Test). The
        /// ids mean the same arenas as on Steam, so the lobby keeps one arena setting.</summary>
        public static readonly object[] LegacyArenas =
        {
            new Dictionary<string, object> { ["id"] = 1, ["name"] = "Temple of Sacrifice" },
            new Dictionary<string, object> { ["id"] = 2, ["name"] = "Metal God Arena" },
        };
        public static int LegacyArena(int arena) => arena == 2 ? 2 : 1;

        public static void Load()
        {
            Cars.Clear();
            string file = Paths.SkinsFile;
            if (file != null)
                foreach (string line in File.ReadAllLines(file, Encoding.UTF8))
                {
                    string[] f = line.Split('\t');
                    if (f.Length < 4 || !int.TryParse(f[0], out int id)) continue;
                    Car car = Cars.FirstOrDefault(c => c.Id == id);
                    if (car == null) Cars.Add(car = new Car { Id = id, Name = f[1] });
                    car.Skins.Add(f[3]);
                }
            if (Cars.Count == 0) // no skins.txt: the car list the .bat launchers had
                foreach (var (id, name) in new[] { (1, "Stingray"), (2, "Black Lotus"), (3, "Artificer"), (4, "Rampage"), (5, "Dirt Devil"),
                    (6, "Wildfire"), (7, "Full Metal Judge"), (8, "Windrider"), (9, "Icebringer"), (10, "Metal Herald"), (13, "Little Monster"),
                    (14, "Clunker"), (15, "Stargazer"), (16, "Peacemaker"), (17, "Vulture"), (18, "Calamity"), (19, "Photon"), (20, "Killer J.") })
                    Cars.Add(new Car { Id = id, Name = name, Skins = { "Original" } });
        }

        public static string CarName(string id) => int.TryParse(id, out int i) ? Cars.FirstOrDefault(c => c.Id == i)?.Name : null;

        /// <summary>Car id from an id, a name or part of one ("fmj" won't work, "full metal" will).</summary>
        public static int? CarId(string want)
        {
            if (string.IsNullOrWhiteSpace(want)) return null;
            if (int.TryParse(want, out int i)) return Cars.Any(c => c.Id == i) ? i : (int?)null;
            string k = Regex.Replace(want, @"[\s'._]", "").ToLowerInvariant();
            Car hit = Cars.FirstOrDefault(c => Regex.Replace(c.Name, @"[\s'._]", "").ToLowerInvariant() == k)
                      ?? Cars.FirstOrDefault(c => Regex.Replace(c.Name, @"[\s'._]", "").ToLowerInvariant().Contains(k));
            return hit?.Id;
        }

        public static object ToJson() => new Dictionary<string, object>
        {
            ["cars"] = Cars.Select(c => new Dictionary<string, object> { ["id"] = c.Id, ["name"] = c.Name, ["skins"] = c.Skins.ToArray() }).ToArray(),
            ["arenas"] = Arenas,
            ["legacyArenas"] = LegacyArenas,
        };
    }

    /// <summary>Starting and watching HMM.exe: the match server (host) and this player's game.</summary>
    public static class Game
    {
        public static Process Server, Client;
        public static bool TestBackground; // --test-background: hidden, muted clients (automated tests)

        public static string ModVersion(string dll)
        {
            if (!File.Exists(dll)) return null;
            string v = FileVersionInfo.GetVersionInfo(dll).ProductVersion;
            return Regex.IsMatch(v ?? "", @"^\d+\.\d+$") ? v : "pre-1.0";
        }

        public static string KitVersion => ModVersion(Path.Combine(Paths.ModDir, "HmmRevive.dll"));
        public static string GameVersion => ModVersion(Path.Combine(Paths.Instance, "HMM_Data", "Managed", "HmmRevive.dll"));

        public static bool Running(Process p)
        {
            try { return p != null && !p.HasExited; } catch { return false; }
        }

        private static string Quote(string a) => a.IndexOf(' ') >= 0 ? "\"" + a + "\"" : a;

        private static Process Start(string build, List<string> args, bool hidden)
        {
            var psi = new ProcessStartInfo(Builds.GameExe(build), string.Join(" ", args.Select(Quote)))
            {
                WorkingDirectory = Builds.Instance(build),
                UseShellExecute = false,
                CreateNoWindow = hidden,
            };
            if (hidden) psi.WindowStyle = ProcessWindowStyle.Hidden;
            Process p = Process.Start(psi);
            Program.Log($"started {psi.FileName} pid={p.Id}: {psi.Arguments}");
            return p;
        }

        public class ServerOptions
        {
            public int Port = 9696, Players = 1, Arena = 1, Score = 0, RedBots, BluBots, EndQuit = 30;
            public int BallSpeed = 100; // percent: 100 = the game's own ball speed (mod BallSpeed.cs)
            public string Build = Builds.Steam, RedDifficulty = "auto", BluDifficulty = "auto", RedBotCars = "", BluBotCars = "";
            public string HostName; // the lobby host's player name: only they may type /ballspeed in the match
            public string Cooldown = ""; // seconds for every car's abilities, "" = the game's own
            public string CarStats; // the lobby's car stats for --hmmrevive-car-stats, null = the game's own
        }

        /// <summary>The file a running server reads every second for changes the host makes during the match.</summary>
        public static string LiveFile(string build, int port) => Path.Combine(Builds.Instance(build), $"hmmrevive-live-{port}.txt");

        public static string BallSpeedArg(int percent) => (percent / 100.0).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>The host changed the ball speed: the running server picks it up within a second and tells the players.</summary>
        public static void SetLiveBallSpeed(string build, int port, int percent)
        {
            try { File.WriteAllText(LiveFile(build, port), "ballspeed=" + BallSpeedArg(percent) + "\r\n"); }
            catch (Exception e) { Program.Log("live settings: " + e.Message); }
        }

        // Ball speed, host and live-settings arguments, the same for every build's server.
        private static IEnumerable<string> LiveArgs(ServerOptions o)
        {
            string live = LiveFile(o.Build, o.Port);
            try { File.Delete(live); } catch { }
            if (o.BallSpeed != 100) yield return "--hmmrevive-ball-speed=" + BallSpeedArg(o.BallSpeed);
            if (!string.IsNullOrEmpty(o.HostName)) yield return "--hmmrevive-host=" + o.HostName;
            yield return "--hmmrevive-live=" + live;
        }

        // ---- the lobby's car stats (mod CarStats.cs): {"default": {hp, move, damage}, "<car id>": {...}}, percent 10-300 ----

        /// <summary>The host's car stats with only known keys and numbers in range; cars equal to "default" are dropped.</summary>
        public static Dictionary<string, object> CleanCarStats(Dictionary<string, object> root)
        {
            var clean = new Dictionary<string, object>();
            if (root == null) return clean;
            Func<Dictionary<string, object>, Dictionary<string, object>> stats = o => new Dictionary<string, object>
            {
                ["hp"] = ClampPct(o?.Int("hp", 100) ?? 100), ["move"] = ClampPct(o?.Int("move", 100) ?? 100), ["damage"] = ClampPct(o?.Int("damage", 100) ?? 100),
            };
            var def = stats(root.Obj("default"));
            clean["default"] = def;
            foreach (var kv in root)
            {
                if (!Regex.IsMatch(kv.Key, @"^\d{1,3}$")) continue; // car ids only
                var s = stats(kv.Value as Dictionary<string, object>);
                if (s.Keys.Any(k => (int)s[k] != (int)def[k])) clean[kv.Key] = s;
            }
            return clean;
        }

        /// <summary>--hmmrevive-car-stats value ("default:100,120,100;8:150,100,100"), null when every car keeps the game's own.</summary>
        public static string CompactCarStats(Dictionary<string, object> root)
        {
            root = CleanCarStats(root);
            var parts = root.Select(kv => (Dictionary<string, object>)kv.Value)
                .Zip(root.Keys, (s, k) => k + ":" + s["hp"] + "," + s["move"] + "," + s["damage"]).ToList();
            return parts.Count == 1 && parts[0] == "default:100,100,100" ? null : string.Join(";", parts);
        }

        /// <summary>A lobby's car stats as sent to the players: passed to our game only if it has exactly that shape.</summary>
        public static string CleanCompactCarStats(string s) =>
            s != null && Regex.IsMatch(s, @"^(default|\d{1,3}):\d{1,3},\d{1,3},\d{1,3}(;\d{1,3}:\d{1,3},\d{1,3},\d{1,3})*$") ? s : null;

        private static int ClampPct(int v) => Math.Max(10, Math.Min(300, v));

        /// <summary>Headless match server, the same command line as tools/run_server.ps1.</summary>
        public static Process StartServer(ServerOptions o)
        {
            StopServer();
            string inst = Builds.Instance(o.Build);
            File.Delete(Path.Combine(inst, $"hmmrevive-server-{o.Port}.log"));
            if (Builds.IsLegacy(o.Build)) return StartLegacyServer(o, inst);
            var a = new List<string> { "-batchmode", "-nographics" };
            if (o.Score > 0) a.Add("--hmmrevive-score=" + o.Score);
            a.Add("--hmmrevive-end-quit=" + o.EndQuit);
            a.AddRange(LiveArgs(o));
            if (o.RedBotCars != "") a.Add("--hmmrevive-bot-cars-red=" + o.RedBotCars);
            if (o.BluBotCars != "") a.Add("--hmmrevive-bot-cars-blue=" + o.BluBotCars);
            if (o.RedDifficulty != "auto") a.Add("--hmmrevive-difficulty-red=" + o.RedDifficulty);
            if (o.BluDifficulty != "auto") a.Add("--hmmrevive-difficulty-blue=" + o.BluDifficulty);
            if (o.Cooldown != "") a.Add("--hmmrevive-cooldown=" + o.Cooldown);
            if (!string.IsNullOrEmpty(o.CarStats)) a.Add("--hmmrevive-car-stats=" + o.CarStats);
            a.AddRange(new[] { "-logFile", Path.Combine(Paths.Instance, $"server_unity_{o.Port}.log"), "--hmmrevive-server", "--Drafter=0",
                "BeginConfig", "[Debug]", "SkipSwordfish=true", "IsDebug=true",
                "[Game]", "PlayerCount=" + o.Players, "ArenaIndex=" + o.Arena, "RedTeamBotsCount=" + o.RedBots, "BluTeamBotsCount=" + o.BluBots,
                "AllPlayersOnBluTeam=false", "[Server]", "Port=" + o.Port, "EndConfig" });
            Server = Start(o.Build, a, true);
            try { Server.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
            return Server;
        }

        /// <summary>An old build's server (mod/HmmRevive.Legacy): players pick cars in the game's own pick screen.</summary>
        private static Process StartLegacyServer(ServerOptions o, string inst)
        {
            var a = new List<string> { "-batchmode", "-nographics", "-silent-crashes", "--hmmrevive-server", "--hmmrevive-end-quit=" + o.EndQuit };
            a.AddRange(LiveArgs(o));
            if (o.Score > 0) a.Add("--hmmrevive-score=" + o.Score);
            if (o.RedDifficulty != "auto") a.Add("--hmmrevive-difficulty-red=" + o.RedDifficulty);
            if (o.BluDifficulty != "auto") a.Add("--hmmrevive-difficulty-blue=" + o.BluDifficulty);
            a.AddRange(new[] { "-logFile", Path.Combine(inst, $"server_unity_{o.Port}.log"),
                "BeginConfig", "[Debug]", "SkipSwordfish=true", "IsDebug=true",
                "[Game]", "PlayerCount=" + o.Players, "ArenaIndex=" + Catalog.LegacyArena(o.Arena), "RedTeamBotsCount=" + o.RedBots, "BluTeamBotsCount=" + o.BluBots,
                "AllPlayersOnBluTeam=false", "[Server]", "Port=" + o.Port, "EndConfig" });
            Server = Start(o.Build, a, true);
            try { Server.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
            return Server;
        }

        public static void StopServer()
        {
            if (Running(Server)) { try { Server.Kill(); Server.WaitForExit(3000); } catch { } }
            Server = null;
        }

        /// <summary>True once the server listens on its UDP port (it takes ~20-30 s to load).</summary>
        public static bool ServerListening(int port) =>
            Running(Server) && IPGlobalProperties.GetIPGlobalProperties().GetActiveUdpListeners().Any(e => e.Port == port);

        /// <summary>This player's game in direct-connect mode, like play.bat. Team is "red", "blue" or null.</summary>
        /// <param name="team">red, blue, or spec: watch the match as one of the game's spectators (narrators)</param>
        /// <param name="car">car id, or null for the one in the settings</param>
        /// <param name="cooldown">the lobby's ability cooldown in seconds ("0.1"), null or "" for the game's own</param>
        /// <param name="carStats">the lobby's car stats (Game.CompactCarStats), null for the game's own</param>
        /// <param name="wasd">old builds: drive with WASD (the lobby host's choice)</param>
        public static Process StartClient(string build, string ip, int port, string team, int score, string car = null, string cooldown = null, string carStats = null, bool wasd = false)
        {
            StopClient();
            if (Builds.IsLegacy(build)) return StartLegacyClient(build, ip, port, team, wasd);
            string name = Settings.Data.Str("name");
            bool spectate = team == Lobby.Spectator;
            car = spectate ? null : string.IsNullOrEmpty(car) ? Settings.Data.Str("car") : car;
            string skin = spectate ? "0" : Settings.SkinFor(car);
            var a = new List<string>();
            if (TestBackground) a.AddRange(new[] { "-batchmode", "--hmmrevive-mute" });
            a.AddRange(new[] { "-logFile", Path.Combine(Paths.Instance, $"client_{name}.log") });
            if (score > 0) a.Add("--hmmrevive-score=" + score);
            if (!string.IsNullOrEmpty(cooldown)) a.Add("--hmmrevive-cooldown=" + cooldown);
            if (!string.IsNullOrEmpty(carStats)) a.Add("--hmmrevive-car-stats=" + carStats);
            if (!string.IsNullOrEmpty(car)) a.Add("--hmmrevive-car=" + car);
            if (skin != "0") a.Add("--hmmrevive-skin=" + Regex.Replace(skin, @"[\s#""']", ""));
            if (team == "red" || team == "blue") a.Add("--hmmrevive-team=" + team);
            if (spectate) a.Add("--hmmrevive-spectate");
            string emotes = Settings.Emotes();
            if (!spectate && emotes != null) a.Add("--hmmrevive-emotes=" + emotes);
            int w = Settings.Data.Int("width"), h = Settings.Data.Int("height");
            if (w > 0 && h > 0) a.AddRange(new[] { "-screen-width", w.ToString(), "-screen-height", h.ToString() });
            a.AddRange(new[] { "-screen-fullscreen", Settings.Data.Bool("fullscreen", true) ? "1" : "0" });
            a.AddRange(new[] { "BeginConfig", "[Debug]", "SkipSwordfish=true", "DirectMatch=true", "PlayerName=" + name,
                "[Server]", "IP=" + ip, "Port=" + port, "EndConfig" });
            Client = Start(build, a, TestBackground);
            return Client;
        }

        /// <summary>An old build's game: it opens its main menu and joins the server by itself (--hmmrevive-connect); the
        /// player then picks a car in the game's own pick screen.</summary>
        private static Process StartLegacyClient(string build, string ip, int port, string team, bool wasd)
        {
            string name = Settings.Data.Str("name");
            var a = new List<string>();
            if (TestBackground) a.AddRange(new[] { "-batchmode", "-nographics", "-silent-crashes", "--hmmrevive-mute" });
            a.AddRange(new[] { "-logFile", Path.Combine(Builds.Instance(build), $"client_{name}.log"), "--hmmrevive-connect" });
            if (team == "red" || team == "blue") a.Add("--hmmrevive-team=" + team);
            if (wasd) a.Add("--hmmrevive-wasd");
            int w = Settings.Data.Int("width"), h = Settings.Data.Int("height");
            if (w > 0 && h > 0) a.AddRange(new[] { "-screen-width", w.ToString(), "-screen-height", h.ToString() });
            a.AddRange(new[] { "-screen-fullscreen", Settings.Data.Bool("fullscreen", true) ? "1" : "0" });
            a.AddRange(new[] { "BeginConfig", "[Debug]", "SkipSwordfish=true", "PlayerName=" + name });
            if (TestBackground) a.Add("AutoTest=true"); // picks a car by itself (nobody clicks in a test)
            a.AddRange(new[] { "[Server]", "IP=" + ip, "Port=" + port,
                "[Game]", "SkipTutorial=true", "SkipSplashPlayer=true", "SkipTutorialSplashes=true", "EndConfig" });
            Client = Start(build, a, TestBackground);
            return Client;
        }

        // --- pictures for the page: skin card art and emotes, made from the player's own game files by a hidden game
        // (mod ImageDump, --hmmrevive-dump-images) once per setup. Nothing of the game's art ships with the kit. ---
        public static string ImagesDir => Path.Combine(Paths.Instance, "hmmrevive-images");
        private static Process _images;
        private static bool _imagesTried;

        /// <summary>The pictures exist and were made by the mod that is set up now.</summary>
        public static bool ImagesReady
        {
            get
            {
                try { return File.ReadAllText(Path.Combine(ImagesDir, "done.txt")).Trim() == GameVersion; }
                catch { return false; }
            }
        }

        /// <summary>Emotes in the game (hmmrevive-emotes.txt, written by the mod), 0 before the pictures were made.</summary>
        public static int EmoteCount
        {
            get
            {
                try { return File.ReadAllLines(Path.Combine(Paths.Instance, "hmmrevive-emotes.txt")).Count(l => l.Contains("	")); }
                catch { return 0; }
            }
        }

        /// <summary>Called on every page poll: makes the pictures once when they're missing (takes ~15 s, hidden).</summary>
        public static void EnsureImages()
        {
            if (_imagesTried || Running(_images) || SetupStatus == "running" || !Builds.Ready(Builds.Steam) || ImagesReady) return;
            _imagesTried = true;
            var a = new List<string> { "-batchmode", "--hmmrevive-mute", "-logFile", Path.Combine(Paths.Instance, "client_images.log"),
                "--hmmrevive-dump-images=" + ImagesDir,
                "BeginConfig", "[Debug]", "SkipSwordfish=true", "DirectMatch=true", "PlayerName=HmmRevivePictures",
                "[Server]", "IP=127.0.0.1", "Port=1", "EndConfig" };
            try
            {
                _images = Start(Builds.Steam, a, true);
                try { _images.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
                Process p = _images;
                new Thread(() => { if (!p.WaitForExit(240000)) try { p.Kill(); } catch { } }) { IsBackground = true }.Start();
            }
            catch (Exception e) { Program.Log("couldn't make the pictures: " + e.Message); }
        }

        public static void StopClient()
        {
            if (Running(Client)) { try { Client.Kill(); Client.WaitForExit(3000); } catch { } }
            Client = null;
        }

        // --- setup (kit): patch the player's game folder (Steam or any copy) into .\instance ---
        public static volatile string SetupStatus = ""; // "", "running", "done", "failed: ..."
        public static volatile string SetupBuild = Builds.Steam; // the copy being (or last) set up
        public static readonly StringBuilder SetupLog = new StringBuilder();

        /// <summary>The game folder setup copies from: the one the player chose before, else the first Steam library that has it.</summary>
        public static string FindGame()
        {
            string chosen = GameDirOf(Settings.Data.Str("gameDir"), out _);
            if (chosen != null && Builds.Detect(chosen) == Builds.Steam) return chosen;
            var candidates = new List<string>();
            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
                {
                    string steam = key?.GetValue("SteamPath") as string;
                    if (steam != null)
                    {
                        candidates.Add(Path.Combine(steam, "steamapps", "common", "Heavy Metal Machines"));
                        string vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
                        if (File.Exists(vdf))
                            foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                                candidates.Add(Path.Combine(m.Groups[1].Value.Replace(@"\\", @"\"), "steamapps", "common", "Heavy Metal Machines"));
                    }
                }
            }
            catch { }
            return candidates.Select(c => GameDirOf(c, out _)).FirstOrDefault(c => c != null && Builds.Detect(c) == Builds.Steam);
        }

        /// <summary>
        /// The original game folder for what the player picked: HMM.exe itself, its folder, or a folder right above it
        /// (a Steam library, or a folder holding "Heavy Metal Machines"). Null, with the reason, when it isn't one.
        /// </summary>
        public static string GameDirOf(string picked, out string why)
        {
            why = "Heavy Metal Machines not found in '" + picked + "'";
            if (string.IsNullOrWhiteSpace(picked)) return null;
            string p;
            try
            {
                p = picked.Trim().Trim('"');
                if (File.Exists(p)) p = Path.GetDirectoryName(p);
                p = Path.GetFullPath(p);
            }
            catch { return null; }
            string dir = new[] { p, Path.Combine(p, "Heavy Metal Machines"), Path.Combine(p, "steamapps", "common", "Heavy Metal Machines") }
                .FirstOrDefault(d => File.Exists(Path.Combine(d, "HMM.exe")) && Directory.Exists(Path.Combine(d, "HMM_Data", "Managed")));
            if (dir == null) return null;
            // Patching HMM Revive's own copy (or another kit's) would patch the game twice.
            string managed = Path.Combine(dir, "HMM_Data", "Managed");
            if (new[] { Builds.Steam }.Concat(Builds.Legacy).Any(b => string.Equals(dir.TrimEnd('\\'), Builds.Instance(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                || File.Exists(Path.Combine(managed, "HmmRevive.dll")) || File.Exists(Path.Combine(managed, "HmmReviveLegacy.dll")))
            {
                why = "That's an HMM Revive copy of the game. Choose the original game folder.";
                return null;
            }
            return dir;
        }

        private static int _browsing;

        /// <summary>Windows' open-file window, asking for HMM.exe; the chosen file, or null when cancelled.</summary>
        public static string BrowseForGame()
        {
            if (Interlocked.Exchange(ref _browsing, 1) == 1) throw new Exception("The window to choose the game is already open.");
            string chosen = null;
            Exception error = null;
            var t = new Thread(() =>
            {
                try
                {
                    bool pt = Settings.Data.Str("lang") == "pt";
                    string start = FindGame();
                    // The file window must open in front of the launcher page. Windows doesn't let a program that isn't the
                    // active one (the page's browser is) put a window in front, so a never-shown topmost owner wasn't
                    // enough and the window opened behind the page. Now a tiny invisible owner is shown at the mouse and
                    // pulled to the front with the active window's input attached, and the file window opens over it.
                    using (var owner = new System.Windows.Forms.Form
                    {
                        TopMost = true, ShowInTaskbar = false, FormBorderStyle = System.Windows.Forms.FormBorderStyle.None,
                        Opacity = 0, StartPosition = System.Windows.Forms.FormStartPosition.Manual,
                        Location = System.Windows.Forms.Cursor.Position, Size = new System.Drawing.Size(1, 1),
                    })
                    {
                        owner.Shown += (s, e) =>
                        {
                            Native.ToFront(owner.Handle);
                            using (var dlg = new System.Windows.Forms.OpenFileDialog
                            {
                                Title = pt ? "Escolha o HMM.exe na pasta do Heavy Metal Machines" : "Choose HMM.exe in your Heavy Metal Machines folder",
                                Filter = "HMM.exe|HMM.exe",
                                CheckFileExists = true,
                                RestoreDirectory = true,
                                InitialDirectory = start ?? Environment.GetFolderPath(Environment.SpecialFolder.MyComputer),
                            })
                            {
                                if (dlg.ShowDialog(owner) == System.Windows.Forms.DialogResult.OK) chosen = dlg.FileName;
                            }
                            owner.Close();
                        };
                        owner.ShowDialog();
                    }
                }
                catch (Exception e) { error = e; }
            });
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            t.Join();
            _browsing = 0;
            if (error != null) throw error;
            return chosen;
        }

        public static void RunSetup(string build, string gameDir)
        {
            if (SetupStatus == "running") return;
            SetupStatus = "running";
            SetupBuild = build;
            lock (SetupLog) SetupLog.Clear();
            new System.Threading.Thread(() =>
            {
                try
                {
                    if (Running(Client) || Running(Server)) throw new Exception("close the game and stop the match first");
                    if (Paths.Patcher == null) throw new Exception("Patcher.exe is missing next to HMM-Revive.exe");
                    if (gameDir == null || !File.Exists(Path.Combine(gameDir, "HMM.exe"))) throw new Exception("Heavy Metal Machines not found in '" + gameDir + "'");
                    if (Builds.Detect(gameDir) != build) throw new Exception("That copy of the game isn't supported.");
                    var psi = new ProcessStartInfo(Paths.Patcher, $"\"{gameDir}\" \"{Builds.Instance(build)}\" \"{Paths.ModDir}\"")
                    { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
                    using (Process p = Process.Start(psi))
                    {
                        p.OutputDataReceived += (s, e) => { if (e.Data != null) lock (SetupLog) SetupLog.AppendLine(e.Data); };
                        p.ErrorDataReceived += (s, e) => { if (e.Data != null) lock (SetupLog) SetupLog.AppendLine(e.Data); };
                        p.BeginOutputReadLine();
                        p.BeginErrorReadLine();
                        p.WaitForExit();
                        if (p.ExitCode != 0) throw new Exception("patcher exit code " + p.ExitCode);
                    }
                    SetupStatus = "done";
                    _imagesTried = false; // new game files or mod: make the pictures again
                }
                catch (Exception e)
                {
                    SetupStatus = "failed: " + e.Message;
                }
                Program.Log("setup: " + SetupStatus);
            }) { IsBackground = true }.Start();
        }
    }

    /// <summary>This PC's addresses on the networks players use (Radmin VPN 26.x.y.z, Tailscale 100.64.0.0/10, ZeroTier, LAN, and a public
    /// "Internet" address, which only servers/VPSes have on an interface; home PCs sit behind a router).</summary>
    public static class Net
    {
        public static List<Dictionary<string, object>> Addresses()
        {
            var list = new List<Dictionary<string, object>>();
            foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (UnicastIPAddressInformation u in ni.GetIPProperties().UnicastAddresses)
                {
                    if (u.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    byte[] b = u.Address.GetAddressBytes();
                    if (b[0] == 169 && b[1] == 254) continue;
                    string kind = b[0] == 100 && (b[1] & 0xC0) == 64 ? "Tailscale"
                        : ni.Description.IndexOf("Radmin", StringComparison.OrdinalIgnoreCase) >= 0 || ni.Name.IndexOf("Radmin", StringComparison.OrdinalIgnoreCase) >= 0 ? "Radmin"
                        : ni.Description.IndexOf("ZeroTier", StringComparison.OrdinalIgnoreCase) >= 0 || ni.Name.IndexOf("ZeroTier", StringComparison.OrdinalIgnoreCase) >= 0 ? "ZeroTier"
                        : ni.Description.IndexOf("WireGuard", StringComparison.OrdinalIgnoreCase) >= 0 ? "WireGuard"
                        : b[0] == 10 || (b[0] == 172 && (b[1] & 0xF0) == 16) || (b[0] == 192 && b[1] == 168) || (b[0] == 100 && (b[1] & 0xC0) == 64) ? "LAN"
                        : "Internet";
                    list.Add(new Dictionary<string, object> { ["ip"] = u.Address.ToString(), ["kind"] = kind, ["mask"] = u.IPv4Mask?.ToString() });
                }
            }
            return list.OrderBy(a => a["kind"] as string == "Radmin" ? 0 : a["kind"] as string == "Tailscale" ? 1 : a["kind"] as string == "ZeroTier" ? 2 : 3).ToList();
        }

        /// <summary>Directed broadcast address of every IPv4 interface (Radmin VPN, ZeroTier and LANs carry broadcasts; Tailscale doesn't).</summary>
        public static List<IPAddress> Broadcasts()
        {
            var list = new List<IPAddress> { IPAddress.Broadcast };
            foreach (var a in Addresses())
            {
                if (a["mask"] == null || a["kind"] as string == "Tailscale" || a["kind"] as string == "Internet") continue;
                byte[] ip = IPAddress.Parse((string)a["ip"]).GetAddressBytes(), mask = IPAddress.Parse((string)a["mask"]).GetAddressBytes();
                for (int i = 0; i < 4; i++) ip[i] = (byte)(ip[i] | ~mask[i]);
                list.Add(new IPAddress(ip));
            }
            return list;
        }

        public static string TailscaleExe()
        {
            foreach (string p in new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tailscale", "tailscale.exe"), @"C:\Program Files\Tailscale\tailscale.exe" })
                if (File.Exists(p)) return p;
            return null;
        }

        /// <summary>Online Tailscale peers' IPv4 addresses (`tailscale status --json`); empty without Tailscale.</summary>
        public static List<string> TailscalePeers()
        {
            var ips = new List<string>();
            string exe = TailscaleExe();
            if (exe == null) return ips;
            try
            {
                var psi = new ProcessStartInfo(exe, "status --json") { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8 };
                using (Process p = Process.Start(psi))
                {
                    string json = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(3000);
                    var peers = Js.Read(json).Obj("Peer");
                    if (peers == null) return ips;
                    foreach (var kv in peers)
                    {
                        var peer = kv.Value as Dictionary<string, object>;
                        if (peer == null || !peer.Bool("Online")) continue;
                        string ip = peer.List("TailscaleIPs").Select(o => o.ToString()).FirstOrDefault(s => s.Contains('.'));
                        if (ip != null) ips.Add(ip);
                    }
                }
            }
            catch (Exception e) { Program.Log("tailscale status failed: " + e.Message); }
            return ips;
        }
    }

    /// <summary>Windows Firewall rules for hosting: game UDP, lobby TCP+UDP, from Tailscale and local subnets (Radmin VPN, ZeroTier, LAN).</summary>
    public static class Firewall
    {
        public const string GameRule = "HMM Revive game", LobbyRule = "HMM Revive lobby", FindRule = "HMM Revive lobby discovery";

        // netsh can read rules without admin. Its text is localized, so only the exit code and the (unlocalized) program
        // path are used: a rule made for a kit in another folder doesn't count.
        private static bool RuleExists(string name, string program)
        {
            try
            {
                var psi = new ProcessStartInfo("netsh", $"advfirewall firewall show rule name=\"{name}\" verbose") { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
                using (Process p = Process.Start(psi))
                {
                    string output = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(5000);
                    return p.ExitCode == 0 && output.IndexOf(program, StringComparison.OrdinalIgnoreCase) >= 0;
                }
            }
            catch { return false; }
        }

        private static string GameRuleOf(string build) => Builds.IsLegacy(build) ? GameRule + " " + build : GameRule;

        public static bool Ready() => Builds.ReadyBuilds().DefaultIfEmpty(Builds.Steam).All(b => RuleExists(GameRuleOf(b), Builds.GameExe(b)))
            && RuleExists(LobbyRule, Paths.LauncherExe) && RuleExists(FindRule, Paths.LauncherExe);

        public static volatile bool Cached;
        public static void Refresh() => Cached = Ready();

        /// <summary>Adds the rules in an elevated PowerShell (Windows asks for admin). Also disables inbound Block rules
        /// Windows created for these two programs when someone pressed Cancel on its "allow access" popup: they win over
        /// any Allow rule.</summary>
        public static bool Setup(int gamePort, int lobbyPort)
        {
            string me = Paths.LauncherExe.Replace("'", "''");
            var games = Builds.ReadyBuilds().DefaultIfEmpty(Builds.Steam).Select(b => (rule: GameRuleOf(b), exe: Builds.GameExe(b).Replace("'", "''"))).ToList();
            // A PC with a public address (a server/VPS) is joined straight over the internet: allow any address then.
            bool publicHost = Net.Addresses().Any(a => a["kind"] as string == "Internet");
            string from = $"-RemoteAddress {(publicHost ? "Any" : "LocalSubnet,100.64.0.0/10")} -Action Allow -Profile Any";
            string cmd =
                $"Remove-NetFirewallRule -DisplayName {string.Join(",", games.Select(g => $"'{g.rule}'"))},'{LobbyRule}','{FindRule}' -ErrorAction SilentlyContinue; " +
                string.Concat(games.Select(g => $"New-NetFirewallRule -DisplayName '{g.rule}' -Direction Inbound -Protocol UDP -LocalPort {gamePort} -Program '{g.exe}' {from} | Out-Null; ")) +
                $"New-NetFirewallRule -DisplayName '{LobbyRule}' -Direction Inbound -Protocol TCP -LocalPort {lobbyPort} -Program '{me}' {from} | Out-Null; " +
                $"New-NetFirewallRule -DisplayName '{FindRule}' -Direction Inbound -Protocol UDP -LocalPort {lobbyPort} -Program '{me}' {from} | Out-Null; " +
                $"Get-NetFirewallApplicationFilter | Where-Object {{ {string.Concat(games.Select(g => $"$_.Program -eq '{g.exe}' -or "))}$_.Program -eq '{me}' }} | Get-NetFirewallRule | " +
                "Where-Object { $_.Action -eq 'Block' -and $_.Direction -eq 'Inbound' } | Disable-NetFirewallRule";
            string enc = Convert.ToBase64String(Encoding.Unicode.GetBytes(cmd));
            try
            {
                var psi = new ProcessStartInfo("powershell", "-NoProfile -EncodedCommand " + enc) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
                using (Process p = Process.Start(psi)) p.WaitForExit(60000);
            }
            catch (Exception e) { Program.Log("firewall setup cancelled: " + e.Message); }
            Refresh();
            return Cached;
        }
    }
    /// <summary>Win32 calls to bring the launcher's own windows (the game folder chooser) in front of the page.</summary>
    internal static class Native
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr pid);
        [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool AttachThreadInput(uint from, uint to, bool attach);
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr hWnd);

        /// <summary>Make the window the active one, borrowing the active window's input queue (Windows only lets the
        /// active program hand over focus).</summary>
        public static void ToFront(IntPtr hWnd)
        {
            uint fg = GetWindowThreadProcessId(GetForegroundWindow(), IntPtr.Zero), me = GetCurrentThreadId();
            bool attached = fg != 0 && fg != me && AttachThreadInput(me, fg, true);
            try
            {
                BringWindowToTop(hWnd);
                SetForegroundWindow(hWnd);
            }
            finally
            {
                if (attached) AttachThreadInput(me, fg, false);
            }
        }
    }
}
