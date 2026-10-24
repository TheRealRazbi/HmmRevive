using System;
using System.Collections.Generic;
using System.IO;

namespace HmmRevive.Launcher
{
    /// <summary>
    /// The game download of a cloud lobby container (HmmRevive-server AD-008): the image has no game, and get-game.sh
    /// streams it from the region's storage while the lobby is already open. It keeps one line in GAME_STATUS_FILE:
    /// "downloading &lt;percent&gt; &lt;seconds left, -1 unknown&gt;", "ready" or "failed &lt;reason&gt;".
    /// </summary>
    public sealed class GameDownload
    {
        public string State = "downloading"; // downloading, ready, failed
        public int Percent;
        public int SecondsLeft = -1;
        public string Reason;

        public bool Ready => State == "ready";
        public bool Failed => State == "failed";

        /// <summary>Reads one status line; an empty or unknown line is a download that has just begun.</summary>
        public static GameDownload Parse(string line)
        {
            string[] p = (line ?? "").Trim().Split(new[] { ' ' }, 2);
            var g = new GameDownload();
            switch (p[0])
            {
                case "ready": g.State = "ready"; g.Percent = 100; g.SecondsLeft = 0; break;
                case "failed": g.State = "failed"; g.Reason = p.Length > 1 ? p[1].Trim() : "unknown error"; break;
                case "downloading":
                    string[] n = p.Length > 1 ? p[1].Split(' ') : new string[0];
                    if (n.Length > 0) int.TryParse(n[0], out g.Percent);
                    if (n.Length > 1 && !int.TryParse(n[1], out g.SecondsLeft)) g.SecondsLeft = -1;
                    g.Percent = Math.Max(0, Math.Min(99, g.Percent));
                    break;
            }
            return g;
        }

        /// <summary>
        /// The reader a cloud lobby polls: null (no download, the game is in place) when <paramref name="path"/> is not
        /// set; a download that has not written its first line yet counts as just begun.
        /// </summary>
        public static Func<GameDownload> FromFile(string path)
        {
            if (string.IsNullOrEmpty(path)) return () => null;
            return () =>
            {
                try { return Parse(File.Exists(path) ? File.ReadAllText(path) : ""); }
                catch (IOException) { return Parse(""); } // replaced while reading: next tick
            };
        }

        public Dictionary<string, object> Json() => new Dictionary<string, object>
        {
            ["state"] = State, ["percent"] = Percent, ["secondsLeft"] = SecondsLeft, ["reason"] = Reason,
        };

        /// <summary>Why a match can't start yet, or null when the game is ready.</summary>
        public string Blocker() =>
            Ready ? null
            : Failed ? $"The server couldn't get the game ({Reason})."
            : $"The server is still downloading the game ({Percent}%).";

        public string Line => Ready ? "ready" : Failed ? "failed " + Reason : $"downloading {Percent} {SecondsLeft}";
    }
}
