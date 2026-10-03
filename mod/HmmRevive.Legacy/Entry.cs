using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using UnityEngine;

namespace HmmRevive.Legacy
{
    /// <summary>
    /// Command line and logging of the mod for the older game builds. The old builds have no Infra.PreStart, so
    /// initialization runs from the first patched method that fires (Hooks.BeforeAwake, or FMOD's init).
    /// </summary>
    public static class Entry
    {
        private static bool _initialized;

        /// <summary>Which old build this DLL was compiled for (the patcher checks it against the game copy).</summary>
#if Y2016
        public const string Build = "2016";
#else
        public const string Build = "2017";
#endif

        public static bool ServerMode { get; private set; }

        /// <summary>No audio output (FMOD NOSOUND): the server, and background test clients (--hmmrevive-mute).</summary>
        public static bool Mute { get; private set; }

        /// <summary>Client: join [Server] IP/Port from the main menu by itself (--hmmrevive-connect).</summary>
        public static bool Connect { get; private set; }

        /// <summary>Client: team to join (--hmmrevive-team=red|blue), sent with the login name. null = the server's choice.</summary>
        public static string Team { get; private set; }

        /// <summary>Server: seconds to wait after the match ends before quitting (--hmmrevive-end-quit=SECONDS, 0 = stay up).
        /// Hoplon's ServerFinish idles forever; the host's launcher needs the process to end to offer a rematch.</summary>
        public static float EndQuitDelay { get; private set; } = 30f;

        /// <summary>Points needed to win (--hmmrevive-score=N), 0 = the arena's own rule. Mostly for testing the match end.</summary>
        public static int ScoreTarget { get; private set; }

        /// <summary>Server: pick screen time in seconds (--hmmrevive-pick-time=SECONDS). Hoplon's server config: 90 s pick,
        /// 10 s customization. The pick ends early once every player confirmed.</summary>
        public static float PickTime { get; private set; } = 90f;
        public static float CustomizationTime { get; private set; } = 10f;

        /// <summary>Server: value overrides file (--hmmrevive-balance=FILE|off). Default: balance-&lt;build&gt;.txt next to this DLL.</summary>
        public static string BalanceFile { get; private set; }

#if !Y2016
        /// <summary>Server: bot AI level per team (--hmmrevive-difficulty-red=easy|medium|hard, same for -blue).
        /// Invalid = the game's own choice (other team's MMR).</summary>
        public static HeavyMetalMachines.BotAI.BotAIGoal.BotDifficulty RedBotDifficulty { get; private set; }
        public static HeavyMetalMachines.BotAI.BotAIGoal.BotDifficulty BluBotDifficulty { get; private set; }
#endif

        public static string Version
        {
            get { Version v = typeof(Entry).Assembly.GetName().Version; return v.Major + "." + v.Minor; }
        }

        private static string Arg(string[] args, string prefix)
        {
            string a = Array.Find(args, x => x.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
            return a == null ? null : a.Substring(prefix.Length).Trim('"', '\'', ' ');
        }

        private static bool Flag(string[] args, string name) => Array.Exists(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));

        private static float Seconds(string v, float def)
        {
            float f;
            return v != null && float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out f) ? f : def;
        }

#if !Y2016
        private static HeavyMetalMachines.BotAI.BotAIGoal.BotDifficulty Difficulty(string v)
        {
            switch ((v ?? "").ToLowerInvariant())
            {
                case "easy": return HeavyMetalMachines.BotAI.BotAIGoal.BotDifficulty.Easy;
                case "medium": return HeavyMetalMachines.BotAI.BotAIGoal.BotDifficulty.Medium;
                case "hard": return HeavyMetalMachines.BotAI.BotAIGoal.BotDifficulty.Hard;
                default: return HeavyMetalMachines.BotAI.BotAIGoal.BotDifficulty.Invalid; // "auto"
            }
        }
#endif

        public static void Init()
        {
            if (_initialized) return;
            _initialized = true;
            string[] args = Environment.GetCommandLineArgs();
            ServerMode = Flag(args, "--hmmrevive-server");
            Mute = ServerMode || Flag(args, "--hmmrevive-mute");
            Connect = Flag(args, "--hmmrevive-connect");
            Team = (Arg(args, "--hmmrevive-team=") ?? "").ToLowerInvariant();
            if (Team != "red" && Team != "blue") Team = null;
            EndQuitDelay = Seconds(Arg(args, "--hmmrevive-end-quit="), EndQuitDelay);
            ScoreTarget = (int)Seconds(Arg(args, "--hmmrevive-score="), 0);
            PickTime = Seconds(Arg(args, "--hmmrevive-pick-time="), PickTime);
            BalanceFile = Arg(args, "--hmmrevive-balance=");
            if (BalanceFile == null) BalanceFile = Path.Combine(Path.GetDirectoryName(typeof(Entry).Assembly.Location) ?? ".", "balance-" + Build + ".txt");
            else if (BalanceFile.Equals("off", StringComparison.OrdinalIgnoreCase)) BalanceFile = null;
#if !Y2016
            RedBotDifficulty = Difficulty(Arg(args, "--hmmrevive-difficulty-red="));
            BluBotDifficulty = Difficulty(Arg(args, "--hmmrevive-difficulty-blue="));
#endif
            var seen = new System.Collections.Generic.Dictionary<string, int>();
            Application.RegisterLogCallback((msg, stack, type) =>
            {
                if (type != LogType.Exception && type != LogType.Error) return;
                // The same error every frame would fill the disk: log the first two, then every 1000th.
                string key = msg + stack;
                int n;
                seen.TryGetValue(key, out n);
                seen[key] = ++n;
                if (n <= 2 || n % 1000 == 0) Log.Error("unity " + type + " (x" + n + "): " + msg + "\n" + stack);
            });
            AppDomain.CurrentDomain.UnhandledException += (s, e) => Log.Error("unhandled: " + e.ExceptionObject);
            HideOwnWindows();
            Log.Info("HmmRevive init. Version=" + Version + " Build=" + Build + " ServerMode=" + ServerMode + " Mute=" + Mute
                     + " Connect=" + Connect + " Team=" + Team + " EndQuit=" + EndQuitDelay + "s ScoreTarget=" + ScoreTarget
#if !Y2016
                     + " Bots Red=" + RedBotDifficulty + " Blue=" + BluBotDifficulty
#endif
                     + " Unity=" + Application.unityVersion + " args=" + string.Join(" ", args));
        }

        // A -batchmode client may still show a blank window: hide this process's windows (test clients only).
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        public static void HideOwnWindows()
        {
            if (ServerMode || !Flag(Environment.GetCommandLineArgs(), "-batchmode")) return;
            try
            {
                uint self = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
                EnumWindows((h, _) =>
                {
                    uint pid;
                    GetWindowThreadProcessId(h, out pid);
                    if (pid == self) ShowWindow(h, 0); // SW_HIDE
                    return true;
                }, IntPtr.Zero);
            }
            catch (Exception e) { Log.Error("hiding windows failed: " + e.Message); }
        }
    }

    /// <summary>Unbuffered log next to HMM.exe: hmmrevive-server-&lt;port&gt;.log / hmmrevive-client-&lt;pid&gt;.log (the game's
    /// own log is buffered and lost when the process is killed).</summary>
    public static class Log
    {
        private static readonly object Gate = new object();
        private static string _path;

        private static string FilePath
        {
            get
            {
                if (_path != null) return _path;
                string dir = Path.GetDirectoryName(Application.dataPath) ?? ".";
                if (Entry.ServerMode)
                {
                    string port = Array.Find(Environment.GetCommandLineArgs(), a => a.StartsWith("Port=", StringComparison.OrdinalIgnoreCase));
                    return _path = Path.Combine(dir, "hmmrevive-server-" + (port != null ? port.Substring(5) : "9696") + ".log");
                }
                return _path = Path.Combine(dir, "hmmrevive-client-" + System.Diagnostics.Process.GetCurrentProcess().Id + ".log");
            }
        }

        public static void Info(string msg) { Write("INFO", msg); }
        public static void Error(string msg) { Write("ERROR", msg); }

        private static void Write(string level, string msg)
        {
            lock (Gate)
            {
                try { File.AppendAllText(FilePath, "[" + DateTime.Now.ToString("HH:mm:ss.fff") + "] [" + level + "] " + msg + Environment.NewLine); }
                catch { }
            }
        }
    }
}
