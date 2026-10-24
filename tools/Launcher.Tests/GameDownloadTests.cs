using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using HmmRevive.Launcher;
using Xunit;

namespace HmmRevive.Launcher.Tests
{
    /// <summary>
    /// HmmRevive-server CREATE-10: a cloud lobby opens while its container downloads the game; matches wait for the
    /// download, every member sees it, and a failed download closes the lobby a minute later.
    /// </summary>
    public class GameDownloadTests : IDisposable
    {
        private readonly List<Lobby> _lobbies = new List<Lobby>();
        private GameDownload _game;
        private int _serverStarts;

        public GameDownloadTests()
        {
            string dir = Path.Combine(Path.GetTempPath(), "hmmrevive-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            Paths.Init(dir, "instance");
            Settings.Data = new Dictionary<string, object>();
        }

        public void Dispose() { foreach (var l in _lobbies) l.Close(); }

        // A cloud lobby with one ready player on Blue and a bot on Red; the download state is whatever _game holds.
        private Lobby CloudLobby(string status, out Lobby.Member player)
        {
            _game = GameDownload.Parse(status);
            var l = new Lobby(FreePort(), FreePort(), Builds.Steam) { Cloud = true, CountdownSeconds = 0, GameFailedCloseSeconds = 60 };
            l.BuildReady = _ => true;
            l.StartServerHook = o => { _serverStarts++; return null; };
            l.ServerGame = () => _game;
            l.Configure(new Dictionary<string, object> { ["red"] = new Dictionary<string, object> { ["bots"] = 1 } });
            player = l.Join("Andre", Program.Version, new[] { Builds.Steam }, false, out string error);
            Assert.True(player != null, error);
            _lobbies.Add(l);
            return l;
        }

        private static int FreePort()
        {
            var t = new TcpListener(IPAddress.Loopback, 0);
            t.Start();
            int port = ((IPEndPoint)t.LocalEndpoint).Port;
            t.Stop();
            return port;
        }

        [Fact]
        public void EveryMemberSeesTheServersDownloadInTheLobbyState()
        {
            var l = CloudLobby("downloading 42 15", out _);
            var g = (Dictionary<string, object>)l.Snapshot(null)["serverGame"];
            Assert.Equal("downloading", g["state"]);
            Assert.Equal(42, g["percent"]);
            Assert.Equal(15, g["secondsLeft"]);
        }

        [Fact]
        public void StartWhileDownloadingAnswersTheProgressAndStartsNothing()
        {
            var l = CloudLobby("downloading 42 15", out _);
            Assert.Equal("The server is still downloading the game (42%).", l.Start());
            Assert.Equal("lobby", l.Phase);
            Assert.Equal(0, _serverStarts);
        }

        [Fact]
        public void StartWorksOnceTheGameIsReady()
        {
            var l = CloudLobby("downloading 90 2", out _);
            _game = GameDownload.Parse("ready");
            Assert.Null(l.Start());
            Assert.Equal(1, _serverStarts);
        }

        [Fact]
        public void EveryoneReadyWhileDownloadingStartsByItselfWhenTheGameIsReady()
        {
            var l = CloudLobby("downloading 10 20", out Lobby.Member player);
            Assert.Null(l.Update(player.Token, new Dictionary<string, object> { ["ready"] = true }));
            l.Step(DateTime.UtcNow);
            l.Step(DateTime.UtcNow.AddSeconds(5));
            Assert.Equal("lobby", l.Phase);
            Assert.Equal(0, _serverStarts);

            _game = GameDownload.Parse("ready");
            l.Step(DateTime.UtcNow); // the countdown begins (0 s in this test)
            l.Step(DateTime.UtcNow.AddSeconds(1));
            Assert.Equal(1, _serverStarts);
        }

        [Fact]
        public void AFailedDownloadIsAnnouncedAndClosesTheLobbyAMinuteLater()
        {
            var l = CloudLobby("downloading 30 20", out _);
            var closed = new ManualResetEventSlim();
            l.CloseRequested += () => closed.Set();
            DateTime t0 = DateTime.UtcNow;
            l.Step(t0);

            _game = GameDownload.Parse("failed the download of the game was cut");
            l.Step(t0.AddSeconds(1));
            Assert.Contains("the download of the game was cut", l.Message);
            Assert.Contains("closes in 60 seconds", l.Message);
            Assert.Equal("failed", ((Dictionary<string, object>)l.Snapshot(null)["serverGame"])["state"]);
            l.Step(t0.AddSeconds(50));
            Assert.False(closed.Wait(300));

            l.Step(t0.AddSeconds(62));
            Assert.True(closed.Wait(2000));
        }

        [Fact]
        public void WithoutAStatusFileTheGameIsInPlace()
        {
            Assert.Null(GameDownload.FromFile(null)());
            string missing = Path.Combine(Path.GetTempPath(), "hmmrevive-no-status-" + Guid.NewGuid().ToString("N"));
            GameDownload begun = GameDownload.FromFile(missing)();
            Assert.Equal("downloading", begun.State); // the script has not written its first line yet
            Assert.Equal(0, begun.Percent);

            var local = new Lobby(FreePort(), FreePort(), Builds.Steam);
            _lobbies.Add(local);
            Assert.Null(local.Snapshot(null)["serverGame"]);
        }
    }
}
