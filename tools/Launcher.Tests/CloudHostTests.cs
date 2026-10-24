using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using HmmRevive.Launcher;
using Xunit;

namespace HmmRevive.Launcher.Tests
{
    public class CloudHostTests : IDisposable
    {
        public CloudHostTests()
        {
            string dir = Path.Combine(Path.GetTempPath(), "hmmrevive-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(dir, "instance"));
            Paths.Init(dir, "instance");
            Settings.Data = new Dictionary<string, object>();
        }

        public void Dispose() { try { Game.Server?.Kill(); } catch { } Game.Server = null; }

        private static int FreePort()
        {
            var t = new TcpListener(IPAddress.Loopback, 0);
            t.Start();
            int p = ((IPEndPoint)t.LocalEndpoint).Port;
            t.Stop();
            return p;
        }

        private static Func<string, string> Env(Dictionary<string, string> v) => k => v.TryGetValue(k, out string s) ? s : null;

        private static Dictionary<string, string> Good() => new Dictionary<string, string>
        {
            ["LOBBY_ID"] = "a1b2c3", ["HOST_TOKEN"] = "host-token", ["LOBBY_SECRET"] = "secret", ["ORCH_URL"] = "https://api.hmm.ovidio.dev",
            ["VISIBILITY"] = "private", ["LOBBY_PASSWORD"] = "pw", ["PUBLIC_HOSTNAME"] = "a1b2c3.hmm.ovidio.dev", ["GAME_PORT"] = "9696",
            ["DEADLINE"] = "2026-10-18T15:00:00Z",
        };

        [Fact]
        public void TheEnvironmentIsParsed()
        {
            var c = CloudHostConfig.Parse(Env(Good()), out string error);
            Assert.Null(error);
            Assert.Equal("a1b2c3", c.LobbyId);
            Assert.Equal("host-token", c.HostToken);
            Assert.Equal("secret", c.LobbySecret);
            Assert.Equal("private", c.Visibility);
            Assert.Equal("pw", c.Password);
            Assert.Equal(9696, c.GamePort);
            Assert.Equal(9697, c.LobbyPort);
            Assert.Equal(new DateTime(2026, 10, 18, 15, 0, 0, DateTimeKind.Utc), c.Deadline.Value.ToUniversalTime());
        }

        [Theory]
        [InlineData("LOBBY_ID")]
        [InlineData("HOST_TOKEN")]
        public void AMissingRequiredVariableIsAClearError(string name)
        {
            var env = Good();
            env.Remove(name);
            Assert.Null(CloudHostConfig.Parse(Env(env), out string error));
            Assert.Contains(name, error);
            env[name] = "   ";
            Assert.Null(CloudHostConfig.Parse(Env(env), out error));
            Assert.Contains(name, error);
        }

        [Fact]
        public void BadValuesAreRefused()
        {
            foreach (var kv in new[] { ("VISIBILITY", "secret"), ("GAME_PORT", "x"), ("DEADLINE", "tomorrow-ish") })
            {
                var env = Good();
                env[kv.Item1] = kv.Item2;
                Assert.Null(CloudHostConfig.Parse(Env(env), out string error));
                Assert.Contains(kv.Item1, error);
            }
        }

        [Fact]
        public void TheNormalStartIsNotCloudMode()
        {
            Assert.False(CloudHost.Requested(new[] { "--no-browser", "--ui-port", "9690" }));
            Assert.True(CloudHost.Requested(new[] { "--root", "x", "--cloud-host" }));
        }

        [Fact]
        public void CloudLimitsAreOnAndTrustTheProxy()
        {
            HttpLimits l = CloudHost.Limits();
            Assert.Equal(64 * 1024, l.MaxBodyBytes);
            Assert.Equal(64, l.MaxConnections);
            Assert.Equal(20, l.RequestsPerSecond);
            Assert.True(l.TrustForwardedFor);
        }

        [Fact]
        public void RunOpensTheLobbyOnLoopbackAnswersAsCloudAndExitsZeroAtTheDeadlineAfterStoppingTheGame()
        {
            var env = Good();
            env["LOBBY_PORT"] = FreePort().ToString();
            env["DEADLINE"] = DateTime.UtcNow.AddSeconds(3).ToString("o");
            env.Remove("ORCH_URL"); // no orchestrator in this test
            var cfg = CloudHostConfig.Parse(Env(env), out string error);
            Assert.Null(error);

            Process game = Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 60 127.0.0.1 >nul") { UseShellExecute = false, CreateNoWindow = true });
            Game.Server = game;

            string info = null;
            var runner = new Thread(() => { Exit = CloudHost.Run(cfg, tickMs: 200); }) { IsBackground = true };
            runner.Start();
            var until = DateTime.UtcNow.AddSeconds(2);
            while (info == null && DateTime.UtcNow < until)
            {
                try { info = Session.Get($"http://127.0.0.1:{cfg.LobbyPort}/lobby/info", 1000); } catch { Thread.Sleep(50); }
            }
            Assert.NotNull(info);
            var d = Js.Read(info);
            Assert.Equal(true, d["cloud"]);
            Assert.Equal("private", d.Str("visibility"));
            Assert.Equal(true, d["passwordRequired"]);

            Assert.True(runner.Join(10000), "Run did not return at the deadline");
            Assert.Equal(0, Exit);
            Assert.True(game.WaitForExit(3000), "the game server was not stopped");
        }

        private int Exit = -1;

        [Fact]
        public void TheGuardHonoursTheOrchestratorsDeadline()
        {
            var t0 = new DateTime(2026, 10, 18, 12, 0, 0, DateTimeKind.Utc);
            var g = new CloudGuard(t0, t0.AddMinutes(30));
            Assert.Equal(GuardAction.Continue, g.Check(t0.AddMinutes(29), 2, "lobby", 0));
            Assert.Equal(GuardAction.StopLobby, g.Check(t0.AddMinutes(30), 2, "lobby", 0));
        }

        [Fact]
        public void ShorterGuardLimitsForContainerTestsAreParsedAndAppliedByTheGuard()
        {
            var env = Good();
            env["GUARD_EMPTY_SECONDS"] = "8";
            env["GUARD_NEVER_JOINED_SECONDS"] = "10";
            env["HEARTBEAT_SECONDS"] = "2";
            var c = CloudHostConfig.Parse(Env(env), out string error);
            Assert.Null(error);
            Assert.Equal(TimeSpan.FromSeconds(8), c.GuardEmpty);
            Assert.Equal(TimeSpan.FromSeconds(10), c.GuardNeverJoined);
            Assert.Equal(TimeSpan.FromSeconds(2), c.HeartbeatInterval);
            Assert.Null(c.GuardMatch);

            var t0 = new DateTime(2026, 10, 18, 12, 0, 0, DateTimeKind.Utc);
            var g = new CloudGuard(t0, null, c.GuardEmpty, c.GuardNeverJoined, c.GuardMatch);
            Assert.Equal(GuardAction.Continue, g.Check(t0.AddSeconds(9), 0, "lobby", 0));
            Assert.Equal(GuardAction.StopLobby, g.Check(t0.AddSeconds(10), 0, "lobby", 0));

            env["GUARD_EMPTY_SECONDS"] = "soon";
            Assert.Null(CloudHostConfig.Parse(Env(env), out error));
            Assert.Contains("GUARD_EMPTY_SECONDS", error);
        }

        [Fact]
        public void TheRegionOfTheContainerIsReadAndReportedByTheLobby()
        {
            var env = Good();
            env["REGION"] = "spaincentral";
            var c = CloudHostConfig.Parse(Env(env), out string error);
            Assert.Null(error);
            Assert.Equal("spaincentral", c.Region);
            var lobby = new Lobby(0, 0, Builds.Steam) { Cloud = true, Region = c.Region };
            Assert.Equal("spaincentral", lobby.Info()["region"]);
            Assert.Equal("spaincentral", lobby.Snapshot(null)["region"]);
            Assert.Null(new Lobby(0, 0, Builds.Steam).Info()["region"]); // a local lobby has none
        }
    }
}
