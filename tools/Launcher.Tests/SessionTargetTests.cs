using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using HmmRevive.Launcher;
using Xunit;

namespace HmmRevive.Launcher.Tests
{
    public class SessionTargetTests : IDisposable
    {
        private readonly List<Lobby> _lobbies = new List<Lobby>();

        public SessionTargetTests()
        {
            string dir = Path.Combine(Path.GetTempPath(), "hmmrevive-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(dir, "instance"));
            File.WriteAllText(Path.Combine(dir, "instance", "HMM.exe"), ""); // the game counts as set up
            Paths.Init(dir, "instance");
            Settings.Data = new Dictionary<string, object> { ["name"] = "Ana" };
        }

        public void Dispose() { foreach (var l in _lobbies) l.Close(); }

        private static bool Resolve(string input, out string baseUrl, out string gameHost, out bool cloud, out string error) =>
            Session.ResolveTarget(input, 9697, out baseUrl, out gameHost, out _, out cloud, out error);

        [Fact]
        public void ALobbyCodeBecomesTheLobbysHttpsAddress()
        {
            Assert.True(Resolve("a1b2c3", out string url, out string host, out bool cloud, out _));
            Assert.Equal("https://a1b2c3.hmm.ovidio.dev", url);
            Assert.Equal("a1b2c3.hmm.ovidio.dev", host);
            Assert.True(cloud);
            Assert.True(Resolve("  A1B2C3 ", out url, out _, out _, out _)); // case and spaces do not matter
            Assert.Equal("https://a1b2c3.hmm.ovidio.dev", url);
        }

        [Fact]
        public void AFullUrlResolvesToItself()
        {
            Assert.True(Resolve("https://a1b2c3.hmm.ovidio.dev/", out string url, out string host, out bool cloud, out _));
            Assert.Equal("https://a1b2c3.hmm.ovidio.dev", url);
            Assert.Equal("a1b2c3.hmm.ovidio.dev", host);
            Assert.True(cloud);
        }

        [Fact]
        public void ABareCloudHostnameResolvesToHttps()
        {
            Assert.True(Resolve("a1b2c3.hmm.ovidio.dev", out string url, out string host, out bool cloud, out _));
            Assert.Equal("https://a1b2c3.hmm.ovidio.dev", url);
            Assert.True(cloud);
        }

        [Fact]
        public void LocalAddressesKeepUsingHttpAsBefore()
        {
            Assert.True(Resolve("100.64.1.2", out string url, out string host, out bool cloud, out _));
            Assert.Equal("http://100.64.1.2:9697", url);
            Assert.Equal("100.64.1.2", host);
            Assert.False(cloud);
            Assert.True(Resolve("http://192.168.0.5:9700", out url, out host, out cloud, out _));
            Assert.Equal("http://192.168.0.5:9700", url);
            Assert.False(cloud);
            Assert.True(Resolve("myhost.lan", out url, out host, out cloud, out _));
            Assert.Equal("http://myhost.lan:9697", url);
        }

        [Fact]
        public void ACloudLobbyOverPlainHttpIsRefused()
        {
            Assert.False(Resolve("http://a1b2c3.hmm.ovidio.dev", out _, out _, out _, out string error));
            Assert.Contains("https", error);
            Assert.False(Resolve("a1b2c3.hmm.ovidio.dev:80", out _, out _, out _, out error)); // a port would also mean plain http
            Assert.False(Resolve("", out _, out _, out _, out error));
            Assert.False(Resolve("not a host!", out _, out _, out _, out error));
        }

        // The base URL chosen at join time is used by every later call: a session on a lobby at some port works end to end.
        [Fact]
        public void EveryCallOfASessionGoesToItsBaseUrl()
        {
            int port = FreePort();
            var lobby = new Lobby(port, FreePort(), Builds.Steam);
            _lobbies.Add(lobby);
            Assert.True(lobby.Open(IPAddress.Loopback, null, discovery: false));

            Session s = Session.Join("127.0.0.1:" + port, 9697, out string error);
            Assert.True(s != null, error);
            Assert.Equal("127.0.0.1:" + port, s.Address);
            Assert.False(s.Cloud);
            Assert.Equal("127.0.0.1", s.GameIp);
            Assert.Null(s.SendChoices(true));                 // /lobby/update
            Assert.Null(s.Say("hello"));                      // /lobby/chat
            var until = DateTime.UtcNow.AddSeconds(5);
            while (s.Lobby == null && DateTime.UtcNow < until) System.Threading.Thread.Sleep(20); // /lobby/state long poll
            Assert.NotNull(s.Lobby);
            s.Leave();                                        // /lobby/leave
            Assert.Equal(0, (int)lobby.Info()["players"]);
        }

        [Fact]
        public void AJoinWithTheWrongPasswordSurfacesTheLobbysReason()
        {
            int port = FreePort();
            var lobby = new Lobby(port, FreePort(), Builds.Steam);
            _lobbies.Add(lobby);
            lobby.SetPassword("pw");
            Assert.True(lobby.Open(IPAddress.Loopback, null, discovery: false));
            Assert.Null(Session.Join("127.0.0.1:" + port, 9697, "nope", out string error));
            Assert.Equal("Wrong password.", error);
            Session ok = Session.Join("127.0.0.1:" + port, 9697, "pw", out error);
            Assert.True(ok != null, error);
            ok.Leave();
        }

        [Fact]
        public void TheGameHostIsTheLobbyHostnameInTheCloudAndTheHostIpLocally()
        {
            Session.ResolveTarget("a1b2c3", 9697, out _, out string cloudGame, out _, out bool cloud, out _);
            Session.ResolveTarget("100.64.1.2", 9697, out _, out string localGame, out _, out bool local, out _);
            Assert.Equal("a1b2c3.hmm.ovidio.dev", cloudGame);
            Assert.True(cloud);
            Assert.Equal("100.64.1.2", localGame);
            Assert.False(local);
        }

        private static int FreePort()
        {
            var t = new TcpListener(IPAddress.Loopback, 0);
            t.Start();
            int p = ((IPEndPoint)t.LocalEndpoint).Port;
            t.Stop();
            return p;
        }
    }
}
