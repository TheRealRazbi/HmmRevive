using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace HmmRevive.Launcher
{
    /// <summary>
    /// Game builds the launcher can run. The current (Steam) build is "steam"; a supported older build has its own id,
    /// mod (kit mod\&lt;id&gt;\), patched copy (instance-&lt;id&gt;\) and lobby rules (the game's own pick screen: no car or skin in
    /// the lobby, one arena). A copy is recognized by the sha256 of its HMM_Data\Managed\Assembly-CSharp-firstpass.dll.
    /// Keep the table in sync with tools/Patcher/Legacy.cs.
    /// </summary>
    public static class Builds
    {
        public const string Steam = "steam", Unsupported = "unsupported";

        private static readonly Dictionary<string, string> Known = new Dictionary<string, string>
        {
            ["0c5527141b81820e"] = "2017",
            ["6b253add011d2318"] = "2017sep", // September 2017
            ["1c5bd4c1219a9229"] = Unsupported, // planned
        };

        /// <summary>Old builds this launcher supports, in the order the page lists them.</summary>
        public static readonly string[] Legacy = { "2017", "2017sep" };

        /// <summary>English name, used in messages from the lobby (the page has its own translated names).</summary>
        public static string Label(string build) => build == "2017" ? "Heavy Metal Machines (Nov 2017)" : build == "2017sep" ? "Heavy Metal Machines (Sep 2017)"
            : "Heavy Metal Machines (Steam version)";

        public static bool IsLegacy(string build) => build != null && build != Steam;

        private static readonly Dictionary<string, (long, DateTime, string)> Cache = new Dictionary<string, (long, DateTime, string)>();

        /// <summary>Which build a game folder holds: a known old build, <see cref="Unsupported"/>, or Steam (any other copy of
        /// the current game, as before builds were told apart).</summary>
        public static string Detect(string gameDir)
        {
            string dll = Path.Combine(gameDir, "HMM_Data", "Managed", "Assembly-CSharp-firstpass.dll");
            try
            {
                var fi = new FileInfo(dll);
                if (!fi.Exists) return Unsupported;
                lock (Cache)
                    if (Cache.TryGetValue(dll, out var c) && c.Item1 == fi.Length && c.Item2 == fi.LastWriteTimeUtc) return c.Item3;
                string hash;
                using (var s = File.OpenRead(dll))
                using (var sha = SHA256.Create())
                    hash = BitConverter.ToString(sha.ComputeHash(s)).Replace("-", "").ToLowerInvariant();
                string build = Known.FirstOrDefault(k => hash.StartsWith(k.Key)).Value;
                // An unknown hash is the current build only if it looks like it (its code is split into many DLLs).
                if (build == null)
                    build = Directory.GetFiles(Path.GetDirectoryName(dll), "HeavyMetalMachines*.dll").Length > 0 ? Steam : Unsupported;
                lock (Cache) Cache[dll] = (fi.Length, fi.LastWriteTimeUtc, build);
                return build;
            }
            catch (Exception e)
            {
                Program.Log($"can't read {dll}: {e.Message}");
                return Unsupported;
            }
        }

        public static string Instance(string build) => IsLegacy(build) ? Paths.Instance + "-" + build : Paths.Instance;
        public static string GameExe(string build) => Path.Combine(Instance(build), "HMM.exe");
        public static bool Ready(string build) => File.Exists(GameExe(build));
        public static string ModDll(string build) => IsLegacy(build) ? "HmmReviveLegacy.dll" : "HmmRevive.dll";
        public static string KitModDll(string build) => IsLegacy(build) ? Path.Combine(Paths.ModDir, build, ModDll(build)) : Path.Combine(Paths.ModDir, ModDll(build));
        public static string KitVersion(string build) => Game.ModVersion(KitModDll(build));
        public static string InstanceVersion(string build) => Game.ModVersion(Path.Combine(Instance(build), "HMM_Data", "Managed", ModDll(build)));

        /// <summary>Builds whose game copy is set up here (the ones this player can host or join).</summary>
        public static List<string> ReadyBuilds() => new[] { Steam }.Concat(Legacy).Where(Ready).ToList();

        /// <summary>The original folder of an old build's copy, as the player added it.</summary>
        public static string CopyDir(string build)
        {
            if (!IsLegacy(build)) return Game.FindGame();
            string dir = Settings.Data.Obj("copies")?.Str(build);
            return dir != null && File.Exists(Path.Combine(dir, "HMM.exe")) ? dir : null;
        }

        /// <summary>Every game copy for Settings: the Steam one (found or chosen) and each supported old build.</summary>
        public static object[] CopiesJson()
        {
            var list = new List<object>();
            foreach (string b in new[] { Steam }.Concat(Legacy))
            {
                string dir = CopyDir(b);
                if (IsLegacy(b) && dir == null && !Ready(b)) continue;
                string kit = KitVersion(b), inst = InstanceVersion(b);
                list.Add(new Dictionary<string, object>
                {
                    ["build"] = b,
                    ["dir"] = dir,
                    ["instance"] = Instance(b),
                    ["ready"] = Ready(b),
                    ["version"] = inst,
                    ["kitVersion"] = kit,
                    ["canSetup"] = Paths.Patcher != null && kit != null,
                    ["needsUpdate"] = Ready(b) && kit != null && inst != kit,
                });
            }
            return list.ToArray();
        }
    }
}
