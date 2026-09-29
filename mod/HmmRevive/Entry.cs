using System;
using System.IO;
using System.Runtime.InteropServices;
using UnityEngine;

namespace HmmRevive
{
    /// <summary>Called first thing from Infra.PreStart.Awake (IL-injected by tools/Patcher).</summary>
    public static class Entry
    {
        private static bool _initialized;

        public static bool ServerMode { get; private set; }

        /// <summary>Running without a GPU device (-nographics).</summary>
        public static bool Headless { get; private set; }

        /// <summary>No audio output (FMOD NOSOUND). For background test clients.</summary>
        public static bool Mute { get; private set; }

        /// <summary>Points needed to win (--hmmrevive-score=N), 0 = arena default (3). Set it on server and clients.</summary>
        public static int ScoreTarget { get; private set; }

        /// <summary>Client: car to play (--hmmrevive-car=NAME|ID), sent to the server with the login. null = slot default.</summary>
        public static string Car { get; private set; }

        /// <summary>Client: skin to wear (--hmmrevive-skin=NUMBER|NAME|random), sent with the login like the car. null = default skin.</summary>
        public static string Skin { get; private set; }

        /// <summary>Client: team to join (--hmmrevive-team=red|blue), sent with the login like the car. null = the server's choice.</summary>
        public static string Team { get; private set; }

        /// <summary>Server: seconds to wait after the match ends before quitting (--hmmrevive-end-quit=SECONDS, 0 = stay up),
        /// so players see the results and the host's launcher can start the next match.</summary>
        public static float EndQuitDelay { get; private set; } = 30f;

        /// <summary>Server: bot cars per team in slot order (--hmmrevive-bot-cars-red=6,wildfire,random, same for -blue).
        /// null = the game's slot defaults.</summary>
        public static string[] RedBotCars { get; private set; }
        public static string[] BluBotCars { get; private set; }

        /// <summary>Server test mode (--hmmrevive-chaos): every car switches to a random one on each death and each round.</summary>
        public static bool Chaos { get; private set; }

        /// <summary>Test: log everything loaded in the match that can heal (--hmmrevive-scan-heal, HealScan.cs).</summary>
        public static bool ScanHeal { get; private set; }

        /// <summary>Chaos only picks from these car ids (--hmmrevive-chaos-cars=8,7), e.g. to repeat one swap pair. null = any car.</summary>
        public static int[] ChaosCars { get; private set; }

        /// <summary>Server: bot AI level per team (--hmmrevive-difficulty-red=easy|medium|hard, same for -blue).
        /// Invalid = the game's own choice (MMR tier table of the arena).</summary>
        public static HeavyMetalMachines.BotAI.BotAIGoal.BotDifficulty RedBotDifficulty { get; private set; }
        public static HeavyMetalMachines.BotAI.BotAIGoal.BotDifficulty BluBotDifficulty { get; private set; }

        /// <summary>Server: out-of-combat repair (--hmmrevive-repair=DELAY,RATE or =off; RATE is HP/s, e.g. "100hp", or %
        /// of max HP/s, e.g. "21" / "21%"). A car that took no damage for RepairDelay seconds repairs RATE until full.
        /// Default 100 HP/s flat, the same as the arenas' own repair areas (Repair_Hazard). RepairDelay &lt; 0 = off.</summary>
        public static float RepairDelay { get; private set; } = 5f;
        public static float RepairPercentPerSecond { get; private set; } // 0 = flat RepairHpPerSecond
        public static float RepairHpPerSecond { get; private set; } = 100f;
        public static string RepairRate => RepairPercentPerSecond > 0f ? RepairPercentPerSecond + "% max HP/s" : RepairHpPerSecond + " HP/s";

        /// <summary>Client test aid (--hmmrevive-shots=N): N off-screen pictures of the match, see TestShots.</summary>
        public static int Shots { get; private set; }

        /// <summary>Client test aid (--hmmrevive-autochat="/cars;/car wildfire"): chat lines sent once, 3 s into the match.</summary>
        public static string[] AutoChat { get; private set; }

        // Clients got the match server's RSA public key from Swordfish at login, and servers got the private key.
        // Without Swordfish the client spins forever in LidgrenNetClient.SendCipherKeyToRemotePeer. Hoplon shipped a
        // test key pair for offline use (SfNetConfiguration.AddTestCryptoKeys); install it on both sides.
        private static void InstallTestCryptoKeys()
        {
            var cfg = new Swordfish.Network.Impl.SfNetConfiguration(null);
            cfg.AddTestCryptoKeys();
            if (Swordfish.Network.Security.CryptographyKeyProvider.ServerPublicKey == null)
                Swordfish.Network.Security.CryptographyKeyProvider.ServerPublicKey = cfg.PublicKey;
            if (Swordfish.Network.Security.CryptographyKeyProvider.ServerPrivateKey == null)
                Swordfish.Network.Security.CryptographyKeyProvider.ServerPrivateKey = cfg.PrivateKey;
        }

        // -batchmode clients still open a blank (white) window, and Unity ignores a hidden start. Hide our own windows.
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        public static void HideOwnWindows()
        {
            if (ServerMode || !Array.Exists(Environment.GetCommandLineArgs(), a => a.Equals("-batchmode", StringComparison.OrdinalIgnoreCase))) return;
            uint self = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
            EnumWindows((h, _) =>
            {
                uint pid;
                GetWindowThreadProcessId(h, out pid);
                if (pid == self) ShowWindow(h, 0); // SW_HIDE
                return true;
            }, IntPtr.Zero);
        }

        private static HeavyMetalMachines.BotAI.BotAIGoal.BotDifficulty ParseDifficulty(string[] args, string prefix)
        {
            string a = Array.Find(args, x => x.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
            if (a == null) return HeavyMetalMachines.BotAI.BotAIGoal.BotDifficulty.Invalid;
            switch (a.Substring(prefix.Length).Trim('"', '\'', ' ').ToLowerInvariant())
            {
                case "easy": return HeavyMetalMachines.BotAI.BotAIGoal.BotDifficulty.Easy;
                case "medium": return HeavyMetalMachines.BotAI.BotAIGoal.BotDifficulty.Medium;
                case "hard": return HeavyMetalMachines.BotAI.BotAIGoal.BotDifficulty.Hard;
                default: return HeavyMetalMachines.BotAI.BotAIGoal.BotDifficulty.Invalid; // "auto"
            }
        }

        // "--prefix=a,b,c" -> {"a","b","c"}; null when absent, empty or "default".
        private static string[] ParseList(string[] args, string prefix)
        {
            string a = Array.Find(args, x => x.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
            if (a == null) return null;
            string[] v = Array.FindAll(a.Substring(prefix.Length).Trim('"', '\'', ' ').Split(','), x => x.Trim().Length > 0);
            v = Array.ConvertAll(v, x => x.Trim());
            return v.Length == 0 || (v.Length == 1 && v[0].Equals("default", StringComparison.OrdinalIgnoreCase)) ? null : v;
        }

        // Release number (MAJOR.MINOR) from the repo's VERSION file, via the assembly version (HmmRevive.csproj).
        public static string Version
        {
            get { Version v = typeof(Entry).Assembly.GetName().Version; return v.Major + "." + v.Minor; }
        }

        private static string Join(string[] v) => v == null ? "default" : string.Join(",", v);

        public static void Init()
        {
            if (_initialized) return;
            _initialized = true;
            string[] args = Environment.GetCommandLineArgs();
            ServerMode = Array.Exists(args, a => a.Equals("--hmmrevive-server", StringComparison.OrdinalIgnoreCase));
            Headless = Array.Exists(args, a => a.Equals("-nographics", StringComparison.OrdinalIgnoreCase));
            string score = Array.Find(args, a => a.StartsWith("--hmmrevive-score=", StringComparison.OrdinalIgnoreCase));
            if (score != null) ScoreTarget = int.Parse(score.Substring("--hmmrevive-score=".Length));
            string car = Array.Find(args, a => a.StartsWith("--hmmrevive-car=", StringComparison.OrdinalIgnoreCase));
            if (car != null && car.Length > "--hmmrevive-car=".Length) Car = car.Substring("--hmmrevive-car=".Length).Trim('"', '\'', ' ');
            string skin = Array.Find(args, a => a.StartsWith("--hmmrevive-skin=", StringComparison.OrdinalIgnoreCase));
            if (skin != null) Skin = skin.Substring("--hmmrevive-skin=".Length).Trim('"', '\'', ' ').Replace("#", "");
            if (Skin == "" || Skin == "0" || "default".Equals(Skin, StringComparison.OrdinalIgnoreCase)) Skin = null;
            string team = Array.Find(args, a => a.StartsWith("--hmmrevive-team=", StringComparison.OrdinalIgnoreCase));
            if (team != null) Team = team.Substring("--hmmrevive-team=".Length).Trim('"', '\'', ' ').ToLowerInvariant();
            if (Team != "red" && Team != "blue") Team = null;
            string endQuit = Array.Find(args, a => a.StartsWith("--hmmrevive-end-quit=", StringComparison.OrdinalIgnoreCase));
            if (endQuit != null) EndQuitDelay = float.Parse(endQuit.Substring("--hmmrevive-end-quit=".Length), System.Globalization.CultureInfo.InvariantCulture);
            RedBotCars = ParseList(args, "--hmmrevive-bot-cars-red=");
            BluBotCars = ParseList(args, "--hmmrevive-bot-cars-blue=");
            Chaos = Array.Exists(args, a => a.Equals("--hmmrevive-chaos", StringComparison.OrdinalIgnoreCase));
            ScanHeal = Array.Exists(args, a => a.Equals("--hmmrevive-scan-heal", StringComparison.OrdinalIgnoreCase));
            string chaosCars = Array.Find(args, a => a.StartsWith("--hmmrevive-chaos-cars=", StringComparison.OrdinalIgnoreCase));
            if (chaosCars != null)
            {
                Chaos = true;
                ChaosCars = Array.ConvertAll(chaosCars.Substring("--hmmrevive-chaos-cars=".Length).Split(','), int.Parse);
            }
            string shots = Array.Find(args, a => a.StartsWith("--hmmrevive-shots=", StringComparison.OrdinalIgnoreCase));
            if (shots != null) Shots = int.Parse(shots.Substring("--hmmrevive-shots=".Length));
            string chat = Array.Find(args, a => a.StartsWith("--hmmrevive-autochat=", StringComparison.OrdinalIgnoreCase));
            if (chat != null) AutoChat = chat.Substring("--hmmrevive-autochat=".Length).Trim('"', '\'').Split(';');
            string repair = Array.Find(args, a => a.StartsWith("--hmmrevive-repair=", StringComparison.OrdinalIgnoreCase));
            if (repair != null)
            {
                string[] v = repair.Substring("--hmmrevive-repair=".Length).Trim('"', '\'', ' ').Split(',');
                if (v[0].Equals("off", StringComparison.OrdinalIgnoreCase)) RepairDelay = -1f;
                else
                {
                    RepairDelay = float.Parse(v[0], System.Globalization.CultureInfo.InvariantCulture);
                    if (v.Length > 1)
                    {
                        string rate = v[1].Trim().ToLowerInvariant();
                        bool hp = rate.EndsWith("hp");
                        float n = float.Parse(rate.TrimEnd('%', 'h', 'p'), System.Globalization.CultureInfo.InvariantCulture);
                        if (hp) { RepairHpPerSecond = n; RepairPercentPerSecond = 0f; }
                        else RepairPercentPerSecond = n;
                    }
                }
            }
            RedBotDifficulty = ParseDifficulty(args, "--hmmrevive-difficulty-red=");
            BluBotDifficulty = ParseDifficulty(args, "--hmmrevive-difficulty-blue=");
            Mute = ServerMode || Array.Exists(args, a => a.Equals("--hmmrevive-mute", StringComparison.OrdinalIgnoreCase));
            Application.logMessageReceived += (msg, stack, type) =>
            {
                if (type == LogType.Exception) Log.Error("unity exception: " + msg + "\n" + stack);
            };
            AppDomain.CurrentDomain.UnhandledException += (s, e) => Log.Error("unhandled: " + e.ExceptionObject);
            InstallTestCryptoKeys();
            HideOwnWindows();
            Log.Info($"HmmRevive init. Version={Version} ServerMode={ServerMode} Headless={Headless} Mute={Mute} ScoreTarget={ScoreTarget} Car={Car} Skin={Skin} Team={Team} EndQuit={EndQuitDelay}s BotCars Red={Join(RedBotCars)} Blue={Join(BluBotCars)} Chaos={Chaos} Repair={RepairDelay}s/{RepairRate} Bots Red={RedBotDifficulty} Blue={BluBotDifficulty} args={string.Join(" ", args)}");
        }
    }

    public static class Log
    {
        // hmmrevive-server-<port>.log / hmmrevive-client-<pid>.log next to HMM.exe (per port so parallel servers don't mix)
        private static string _filePath;

        private static string FilePath => _filePath ?? (_filePath = Path.Combine(
            Path.GetDirectoryName(Application.dataPath) ?? ".",
            Entry.ServerMode ? $"hmmrevive-server-{ServerPort()}.log" : $"hmmrevive-client-{System.Diagnostics.Process.GetCurrentProcess().Id}.log"));

        private static readonly object Gate = new object();

        private static string ServerPort()
        {
            string port = Array.Find(Environment.GetCommandLineArgs(), a => a.StartsWith("Port=", StringComparison.OrdinalIgnoreCase));
            return port != null ? port.Substring("Port=".Length) : "9696";
        }

        public static void Info(string msg) => Write("INFO", msg);
        public static void Error(string msg) => Write("ERROR", msg);

        private static void Write(string level, string msg)
        {
            string line = $"[{DateTime.Now:HH:mm:ss.fff}] [{level}] {msg}";
            lock (Gate)
            {
                try { File.AppendAllText(FilePath, line + Environment.NewLine); } catch { }
            }
        }
    }
}
