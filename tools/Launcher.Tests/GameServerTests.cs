using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using HmmRevive.Launcher;
using Xunit;

namespace HmmRevive.Launcher.Tests
{
    public class GameServerTests : IDisposable
    {
        private readonly Func<ProcessStartInfo, Process> _spawn = Game.Spawn;
        private readonly Action<LaunchCommand> _run = Game.RunToEnd;

        public GameServerTests()
        {
            string dir = Path.Combine(Path.GetTempPath(), "hmmrevive-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(dir, "instance"));
            Paths.Init(dir, "instance");
            Settings.Data = new Dictionary<string, object>();
        }

        public void Dispose()
        {
            Game.Spawn = _spawn;
            Game.RunToEnd = _run;
            Platform.OverrideLinux = null;
            try { Game.Server?.Kill(); } catch { }
            Game.Server = null;
        }

        private static Process Sleeper() => Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 30 127.0.0.1 >nul") { UseShellExecute = false, CreateNoWindow = true });

        private static Process Finished()
        {
            var p = Process.Start(new ProcessStartInfo("cmd.exe", "/c exit 0") { UseShellExecute = false, CreateNoWindow = true });
            p.WaitForExit();
            return p;
        }

        // PLAY-02: on Windows the server is the same process as before.
        [Fact]
        public void OnWindowsTheServerIsHmmExeItselfWithTheSameArguments()
        {
            ProcessStartInfo seen = null;
            Game.Spawn = psi => { seen = psi; return Finished(); };
            Platform.OverrideLinux = false;
            Game.StartServer(new Game.ServerOptions { Port = 9797 });
            Assert.EndsWith("HMM.exe", seen.FileName);
            Assert.StartsWith("-batchmode -nographics", seen.Arguments);
            Assert.Contains("--hmmrevive-server", seen.Arguments);
            Assert.Contains("Port=9797", seen.Arguments);
        }

        // PLAY-03: on Linux the same arguments go through setsid, a virtual display and Wine.
        [Fact]
        public void OnLinuxTheServerIsStartedThroughTheShim()
        {
            ProcessStartInfo seen = null;
            Game.Spawn = psi => { seen = psi; return Finished(); };
            Platform.OverrideLinux = true;
            Game.StartServer(new Game.ServerOptions { Port = 9797 });
            Assert.Equal("/usr/bin/setsid", seen.FileName);
            Assert.StartsWith("/usr/bin/xvfb-run -a /usr/lib/wine/wine64 ", seen.Arguments);
            Assert.Contains("HMM.exe -batchmode -nographics", seen.Arguments);
            Assert.Contains("Port=9797", seen.Arguments);
        }

        [Fact]
        public void APriorityChangeThatFailsDoesNotStopTheServerStart()
        {
            Process finished = Finished(); // setting PriorityClass on an exited process throws
            Game.Spawn = psi => finished;
            Platform.OverrideLinux = true;
            Process started = Game.StartServer(new Game.ServerOptions());
            Assert.Same(finished, started);
        }

        [Fact]
        public void OnLinuxStoppingTheServerKillsTheProcessGroup()
        {
            Process p = Sleeper();
            Game.Server = p;
            LaunchCommand ran = null;
            Game.RunToEnd = c => ran = c;
            Platform.OverrideLinux = true;
            Game.StopServer();
            Assert.NotNull(ran);
            Assert.Equal("-c \"kill -TERM -- -" + p.Id + "\"", ran.Arguments);
            Assert.Null(Game.Server);
            try { p.Kill(); } catch { }
        }

        [Fact]
        public void OnWindowsStoppingTheServerKillsTheProcessAsBefore()
        {
            Process p = Sleeper();
            Game.Server = p;
            bool ranCommand = false;
            Game.RunToEnd = c => ranCommand = true;
            Platform.OverrideLinux = false;
            Game.StopServer();
            Assert.False(ranCommand);
            Assert.True(p.WaitForExit(3000));
        }
    }
}
