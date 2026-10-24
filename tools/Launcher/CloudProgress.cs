using System;
using System.Collections.Generic;

namespace HmmRevive.Launcher
{
    /// <summary>
    /// What the host sees while a cloud lobby is being created: which of the four steps is running, a progress percentage and
    /// how long is left. The durations are what was measured on the real stack in Brazil (about 70 seconds since the game left
    /// the image, HmmRevive-server AD-008); the screen says so when it takes longer instead of pretending.
    /// </summary>
    public static class CloudProgress
    {
        /// <summary>The steps in order. contact: asking the orchestrator (it may need to wake up); reserve: Azure finds room for
        /// the server; pull: the game image is downloaded; start: containers and lobby start; connect: the name and the secure link.</summary>
        public static readonly string[] Steps = { "contact", "reserve", "pull", "start", "connect" };

        // Typical seconds per step, measured in Brazil South on 2026-10-24 (lobby open after 70 s). The image is small now; the
        // game itself downloads after the lobby opens, and the lobby screen shows that part (CREATE-11).
        private static readonly int[] Typical = { 4, 10, 28, 12, 16 };
        private const int Total = 70;
        // Times the typical total: past this the estimate is dropped. East US and Spain Central open in 105-115 s, which
        // must not count as slow.
        private const double SlowAfter = 2.0;

        /// <summary>The orchestrator's creation step ("reserving", "starting", "connecting") as one of <see cref="Steps"/>.</summary>
        public static string FromOrchestrator(string step) =>
            step == "reserving" ? "reserve" : step == "pulling" ? "pull" : step == "starting" ? "start" : step == "connecting" ? "connect" : "contact";

        public static Dictionary<string, object> Compute(string step, double elapsedSeconds)
        {
            elapsedSeconds = Math.Max(0, elapsedSeconds);
            // Azure only reports a group's image events once it is running, so the orchestrator says "reserving" for the
            // whole download. A server that has been reserved for longer than the usual reserving time is downloading.
            if (step == "reserve" && elapsedSeconds >= Typical[0] + Typical[1]) step = "pull";
            int idx = Math.Max(0, Array.IndexOf(Steps, step));
            int before = 0;
            for (int i = 0; i < idx; i++) before += Typical[i];
            // How far into the current step we probably are, never claiming it is finished before the next step starts.
            double within = Math.Min(0.95, Math.Max(0, (elapsedSeconds - before) / Typical[idx]));
            double percent = (before + Typical[idx] * within) * 100.0 / Total;
            int after = 0;
            for (int i = idx + 1; i < Typical.Length; i++) after += Typical[i];
            bool slow = elapsedSeconds > Total * SlowAfter;
            int left = (int)Math.Round(Math.Max(within >= 0.95 ? 10 : Typical[idx] * (1 - within), 5) + after);

            var steps = new List<object>();
            for (int i = 0; i < Steps.Length; i++)
                steps.Add(new Dictionary<string, object> { ["key"] = Steps[i], ["state"] = i < idx ? "done" : i == idx ? "current" : "todo" });
            return new Dictionary<string, object>
            {
                ["step"] = Steps[idx],
                ["number"] = idx + 1,
                ["count"] = Steps.Length,
                ["percent"] = (int)Math.Min(99, Math.Round(percent)),
                ["etaSeconds"] = slow ? (object)null : left,
                ["slow"] = slow,
                ["elapsedSeconds"] = (int)elapsedSeconds,
                ["steps"] = steps,
            };
        }
    }
}
