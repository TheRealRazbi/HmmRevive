using System;
using System.Collections.Generic;
using System.Linq;

namespace HmmRevive.Launcher
{
    /// <summary>A process to start: what <see cref="Platform"/> decided for the current operating system.</summary>
    public sealed class LaunchCommand
    {
        public string FileName, Arguments, WorkingDirectory;
    }

    /// <summary>
    /// Where the launcher runs. On Windows the game is HMM.exe itself. In the cloud the launcher runs on Mono in a Linux
    /// container and the game server runs under Wine on a virtual display (docs/spikes/s1-mono.md in the server repo).
    /// </summary>
    public static class Platform
    {
        /// <summary>Tests force an operating system here; null means the real one.</summary>
        internal static bool? OverrideLinux;

        /// <summary>Mono reports every Unix, Linux included, as PlatformID.Unix.</summary>
        public static bool IsLinux => OverrideLinux ?? Environment.OSVersion.Platform == PlatformID.Unix;

        /// <summary>Arguments with a space are quoted; the game's own arguments never contain a quote.</summary>
        public static string Quote(string a) => a.IndexOf(' ') >= 0 ? "\"" + a + "\"" : a;

        /// <summary>
        /// How to start the game with these arguments. Linux: <c>setsid</c> gives the game its own process group (so it can be
        /// stopped without stopping the launcher), <c>xvfb-run</c> a virtual display, <c>wine64</c> runs the Windows exe.
        /// </summary>
        public static LaunchCommand GameCommand(string gameExe, IEnumerable<string> args, string workingDirectory)
        {
            string tail = string.Join(" ", args.Select(Quote));
            if (!IsLinux)
                return new LaunchCommand { FileName = gameExe, Arguments = tail, WorkingDirectory = workingDirectory };
            return new LaunchCommand
            {
                FileName = "/usr/bin/setsid",
                Arguments = "/usr/bin/xvfb-run -a /usr/lib/wine/wine64 " + Quote(gameExe) + (tail.Length > 0 ? " " + tail : ""),
                WorkingDirectory = workingDirectory,
            };
        }

        /// <summary>
        /// The command that stops the game and everything it started, or null when killing the process is enough (Windows).
        /// The launched <c>setsid</c> process leads its own group, so a negative pid signals the whole group: HMM.exe, Wine
        /// and the virtual display, which killing only the launched process would leave running with UDP 9696 bound.
        /// </summary>
        public static LaunchCommand KillTreeCommand(int pid) =>
            IsLinux ? new LaunchCommand { FileName = "/bin/bash", Arguments = "-c \"kill -TERM -- -" + pid + "\"" } : null;
    }
}
