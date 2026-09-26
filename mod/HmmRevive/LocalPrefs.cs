using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace HmmRevive
{
    /// <summary>
    /// Game options (instructor, HP-bar cooldowns, controls, ...) lived in a Swordfish inventory bag, so in SkipSwordfish
    /// mode they reset every launch. Keep HMMPlayerPrefs' values in hmmrevive-prefs-&lt;PlayerName&gt;.txt next to HMM.exe.
    /// </summary>
    public static class LocalPrefs
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

        private static string FilePath
        {
            get
            {
                string arg = Array.Find(Environment.GetCommandLineArgs(), a => a.StartsWith("PlayerName=", StringComparison.OrdinalIgnoreCase));
                string name = arg != null ? arg.Substring("PlayerName=".Length) : "default";
                foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
                return Path.Combine(Path.GetDirectoryName(Application.dataPath) ?? ".", $"hmmrevive-prefs-{name}.txt");
            }
        }

        private static Dictionary<string, string> Values(object prefs) =>
            (Dictionary<string, string>)prefs.GetType().GetField("_values", Private).GetValue(prefs);

        // Start of HMMPlayerPrefs.SkipSwordfishLoad (which then fires the "prefs loaded" callbacks with our values).
        public static void Load(object prefs)
        {
            if (Entry.ServerMode) return;
            try
            {
                string path = FilePath;
                if (!File.Exists(path)) { Log.Info("no saved options yet (" + Path.GetFileName(path) + ")"); return; }
                var values = Values(prefs);
                foreach (string line in File.ReadAllLines(path, Encoding.UTF8))
                {
                    int tab = line.IndexOf('\t');
                    if (tab > 0) values[Unescape(line.Substring(0, tab))] = Unescape(line.Substring(tab + 1));
                }
                Log.Info($"loaded {values.Count} saved options from {Path.GetFileName(path)}");
            }
            catch (Exception e)
            {
                Log.Error("loading saved options failed: " + e.Message);
            }
        }

        // Start of HMMPlayerPrefs.Save / SaveNow (the game only sent them to Swordfish, a minute later).
        public static void Save(object prefs)
        {
            if (Entry.ServerMode) return;
            try
            {
                if (!(bool)prefs.GetType().GetField("_loaded", Private).GetValue(prefs)) return;
                var sb = new StringBuilder();
                foreach (var kv in Values(prefs)) sb.Append(Escape(kv.Key)).Append('\t').Append(Escape(kv.Value)).Append('\n');
                string path = FilePath, tmp = path + ".tmp";
                File.WriteAllText(tmp, sb.ToString(), Encoding.UTF8);
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
            }
            catch (Exception e)
            {
                Log.Error("saving options failed: " + e.Message);
            }
        }

        // The "your preferences are from an older version" popup: only meaningful for Swordfish bags. In SkipSwordfish
        // mode prefs count as loaded immediately, so HMMPlayerPrefs.ExecOnPrefsWrongVersion fired it on every launch.
        public static bool SkipWrongVersionPopup() => true;

        private static string Escape(string s) => (s ?? "").Replace("\\", "\\\\").Replace("\t", "\\t").Replace("\n", "\\n").Replace("\r", "\\r");

        private static string Unescape(string s)
        {
            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c != '\\' || i + 1 == s.Length) { sb.Append(c); continue; }
                char n = s[++i];
                sb.Append(n == 't' ? '\t' : n == 'n' ? '\n' : n == 'r' ? '\r' : n);
            }
            return sb.ToString();
        }
    }
}
