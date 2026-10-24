using System;
using HmmRevive.Launcher;
using Xunit;

namespace HmmRevive.Launcher.Tests
{
    public class PlatformTests : IDisposable
    {
        public void Dispose() { Platform.OverrideLinux = null; }

        private static readonly string[] Args = { "-batchmode", "-nographics", "--hmmrevive-server", "BeginConfig", "[Debug]", "SkipSwordfish=true", "EndConfig" };

        // On Windows nothing changes: the exe itself, the arguments as they were joined before the shim existed.
        [Fact]
        public void OnWindowsTheCommandIsThePlainExeWithTheSameArguments()
        {
            Platform.OverrideLinux = false;
            LaunchCommand c = Platform.GameCommand(@"C:\kit\instance\HMM.exe", Args, @"C:\kit\instance");
            Assert.Equal(@"C:\kit\instance\HMM.exe", c.FileName);
            Assert.Equal("-batchmode -nographics --hmmrevive-server BeginConfig [Debug] SkipSwordfish=true EndConfig", c.Arguments);
        }

        // PLAY-05: Wine on a virtual display, in its own process group, every argument kept in order.
        [Fact]
        public void OnLinuxTheCommandRunsWineOnAVirtualDisplayWithEveryArgument()
        {
            Platform.OverrideLinux = true;
            LaunchCommand c = Platform.GameCommand("/opt/hmm/HMM.exe", Args, "/opt/hmm");
            Assert.Equal("/usr/bin/setsid", c.FileName);
            Assert.Equal("/usr/bin/xvfb-run -a /usr/lib/wine/wine64 /opt/hmm/HMM.exe -batchmode -nographics --hmmrevive-server BeginConfig [Debug] SkipSwordfish=true EndConfig", c.Arguments);
        }

        [Fact]
        public void ArgumentsWithSpacesAreQuotedOnBothSystems()
        {
            var args = new[] { "-logFile", "/opt/my files/server.log", "PlayerName=Ana" };
            Platform.OverrideLinux = false;
            Assert.Equal("-logFile \"/opt/my files/server.log\" PlayerName=Ana", Platform.GameCommand("HMM.exe", args, ".").Arguments);
            Platform.OverrideLinux = true;
            Assert.EndsWith("HMM.exe -logFile \"/opt/my files/server.log\" PlayerName=Ana", Platform.GameCommand("HMM.exe", args, ".").Arguments);
        }

        [Fact]
        public void TheWorkingDirectoryIsCarriedOnBothSystems()
        {
            Platform.OverrideLinux = false;
            Assert.Equal(@"C:\kit\instance", Platform.GameCommand("HMM.exe", Args, @"C:\kit\instance").WorkingDirectory);
            Platform.OverrideLinux = true;
            Assert.Equal("/opt/hmm", Platform.GameCommand("HMM.exe", Args, "/opt/hmm").WorkingDirectory);
        }

        // The group, not just the launched process: a negative pid.
        [Fact]
        public void TheKillCommandSignalsTheWholeProcessGroupOnLinuxAndIsNotNeededOnWindows()
        {
            Platform.OverrideLinux = true;
            LaunchCommand k = Platform.KillTreeCommand(4242);
            Assert.Equal("/bin/bash", k.FileName); // dash has no "--" in its kill builtin
            Assert.Equal("-c \"kill -TERM -- -4242\"", k.Arguments);
            Platform.OverrideLinux = false;
            Assert.Null(Platform.KillTreeCommand(4242)); // Process.Kill of the single game process is enough
        }

        [Fact]
        public void TheOperatingSystemCanBeOverriddenAndOtherwiseIsTheRealOne()
        {
            Platform.OverrideLinux = null;
            Assert.Equal(Environment.OSVersion.Platform == PlatformID.Unix, Platform.IsLinux);
            Platform.OverrideLinux = true;
            Assert.True(Platform.IsLinux);
            Platform.OverrideLinux = false;
            Assert.False(Platform.IsLinux);
        }
    }
}
