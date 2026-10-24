using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using HmmRevive.Launcher;
using Xunit;

namespace HmmRevive.Launcher.Tests
{
    public class HostRoutesTests : IDisposable
    {
        private const string Token = "host-token-0123456789abcd";
        private readonly List<Lobby> _lobbies = new List<Lobby>();

        public HostRoutesTests()
        {
            // Lobby.Configure saves the host's setup: keep that in a scratch folder, never the player's settings file.
            string dir = Path.Combine(Path.GetTempPath(), "hmmrevive-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            Paths.Init(dir, "instance");
            Settings.Data = new Dictionary<string, object>(); // statics: start every test from the defaults
        }

        public void Dispose() { foreach (var l in _lobbies) l.Close(); }

        private Lobby NewLobby(string hostToken = Token)
        {
            var l = new Lobby(FreePort(), FreePort(), Builds.Steam) { CountdownSeconds = 0 };
            l.SetHostToken(hostToken);
            l.BuildReady = _ => true;
            l.StartServerHook = o => null; // no game in tests
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

        private static Request Post(string path, object body = null, string token = Token, string query = null)
        {
            var r = new Request { Method = "POST", Path = path, Body = Js.Write(body ?? new Dictionary<string, object>()), Remote = IPAddress.Loopback };
            if (token != null) r.Headers["Authorization"] = "Bearer " + token;
            if (query != null) r.Query["token"] = query;
            return r;
        }

        private static Dictionary<string, object> Obj(params object[] kv)
        {
            var d = new Dictionary<string, object>();
            for (int i = 0; i < kv.Length; i += 2) d[(string)kv[i]] = kv[i + 1];
            return d;
        }

        private static Lobby.Member Join(Lobby l, string name, bool host = false)
        {
            var m = l.Join(name, Program.Version, new[] { Builds.Steam }, host, out string error);
            Assert.True(m != null, error);
            return m;
        }

        private static void Ok(Response res) => Assert.True(res.Status == 200, res.Status + " " + System.Text.Encoding.UTF8.GetString(res.Body));

        private static List<Dictionary<string, object>> Members(Lobby l) =>
            ((Dictionary<string, object>[])l.Snapshot(null)["members"]).ToList();

        // ---- SEC-05: every host route needs the token ----

        public static IEnumerable<object[]> HostRoutes() =>
            new[] { "configure", "start", "stop", "kick", "move", "swap", "draft-reset", "visibility", "close" }.Select(r => new object[] { r });

        [Theory]
        [MemberData(nameof(HostRoutes))]
        public void EveryHostRouteRefusesARequestWithoutTheToken(string route)
        {
            var l = NewLobby();
            Response res = l.Handle(Post("/lobby/host/" + route, token: null));
            Assert.Equal(403, res.Status);
            Response wrong = l.Handle(Post("/lobby/host/" + route, token: "not-the-token"));
            Assert.Equal(403, wrong.Status);
        }

        [Fact]
        public void ATokenInTheUrlOrTheBodyIsIgnored()
        {
            var l = NewLobby();
            Assert.Equal(403, l.Handle(Post("/lobby/host/visibility", Obj("visibility", "private", "token", Token), token: null)).Status);
            Assert.Equal(403, l.Handle(Post("/lobby/host/visibility", Obj("visibility", "private"), token: null, query: Token)).Status);
            Assert.Equal("public", l.Visibility);
        }

        [Fact]
        public void TheTokenOnlyWorksAsABearerHeader()
        {
            var l = NewLobby();
            var r = Post("/lobby/host/visibility", Obj("visibility", "private"), token: null);
            r.Headers["Authorization"] = Token; // no "Bearer"
            Assert.Equal(403, l.Handle(r).Status);
            r.Headers["Authorization"] = "Basic " + Token;
            Assert.Equal(403, l.Handle(r).Status);
        }

        // ---- the actions run with the token ----

        [Fact]
        public void ConfigureChangesTheMatchSettings()
        {
            var l = NewLobby();
            Assert.Equal(200, l.Handle(Post("/lobby/host/configure", Obj("score", 7, "ballSpeed", 150))).Status);
            Assert.Equal(7, l.Score);
            Assert.Equal(150, l.BallSpeed);
        }

        [Fact]
        public void StartRunsTheActionAndReportsWhyItCannot()
        {
            var l = NewLobby();
            Response res = l.Handle(Post("/lobby/host/start"));
            Assert.Equal(400, res.Status); // nobody is in the lobby: the action ran and said so
            Assert.Contains("Nobody", System.Text.Encoding.UTF8.GetString(res.Body));

            int starts = 0;
            l.StartServerHook = o => { starts++; return null; };
            var host = Join(l, "Host", host: true);
            Join(l, "Bia");
            l.Update(host.Token, Obj("team", "blue"));
            Ok(l.Handle(Post("/lobby/host/start")));
            Assert.Equal(1, starts);
        }

        [Fact]
        public void KickMoveSwapStopAndDraftResetRunTheirActions()
        {
            var l = NewLobby();
            Join(l, "Host", host: true);
            var ana = Join(l, "Ana");
            var bia = Join(l, "Bia");
            string anaBefore = ana.Team, biaBefore = bia.Team;
            Assert.NotEqual(anaBefore, biaBefore);
            Ok(l.Handle(Post("/lobby/host/swap", Obj("a", ana.Id, "b", bia.Id))));
            Assert.Equal(biaBefore, ana.Team);
            Assert.Equal(anaBefore, bia.Team);
            string other = ana.Team == "blue" ? "red" : "blue";
            Ok(l.Handle(Post("/lobby/host/move", Obj("id", ana.Id, "team", other))));
            Assert.Equal(other, ana.Team);
            Ok(l.Handle(Post("/lobby/host/stop")));
            Ok(l.Handle(Post("/lobby/host/draft-reset")));
            Ok(l.Handle(Post("/lobby/host/kick", Obj("id", bia.Id))));
            Assert.DoesNotContain(bia.Id, Members(l).Select(m => (string)m["id"]));
        }

        [Fact]
        public void ChangingVisibilityThroughTheRouteShowsInInfoAtOnce()
        {
            var l = NewLobby();
            Assert.Equal(200, l.Handle(Post("/lobby/host/visibility", Obj("visibility", "private"))).Status);
            Assert.Equal("private", l.Info()["visibility"]);
            Assert.Equal(400, l.Handle(Post("/lobby/host/visibility", Obj("visibility", "secret"))).Status);
            Assert.Equal("private", l.Info()["visibility"]);
        }

        [Fact]
        public void CloseAnswersFirstAndThenRaisesTheCloseEvent()
        {
            var l = NewLobby();
            var closed = new ManualResetEventSlim();
            l.CloseRequested += () => closed.Set();
            Assert.Equal(200, l.Handle(Post("/lobby/host/close")).Status);
            Assert.True(closed.Wait(3000), "the close event was not raised");
        }

        // ---- joining as the host ----

        [Fact]
        public void AJoinWithTheTokenMakesTheMemberTheHostAndSkipsThePassword()
        {
            var l = NewLobby();
            l.SetPassword("pw" + Guid.NewGuid().ToString("N").Substring(0, 10)); // any password: the host skips it
            var req = Post("/lobby/join", Obj("name", "Andre", "version", Program.Version, "builds", new[] { Builds.Steam }));
            Response res = l.Handle(req);
            Assert.Equal(200, res.Status);
            Assert.Equal(true, Js.Read(System.Text.Encoding.UTF8.GetString(res.Body))["host"]);
            Assert.Equal("Andre", l.Info()["host"]);

            var guest = Post("/lobby/join", Obj("name", "Ana", "version", Program.Version, "builds", new[] { Builds.Steam }), token: null);
            Assert.Equal(409, l.Handle(guest).Status); // a guest still needs the password
            Assert.Equal("Andre", l.Info()["host"]);
        }

        [Fact]
        public void ASecondJoinWithTheTokenReclaimsTheSameHostSeat()
        {
            var l = NewLobby();
            Response first = l.Handle(Post("/lobby/join", Obj("name", "Andre", "version", Program.Version)));
            Response again = l.Handle(Post("/lobby/join", Obj("name", "Andre", "version", Program.Version)));
            Response renamed = l.Handle(Post("/lobby/join", Obj("name", "Andre2", "version", Program.Version)));
            Assert.Equal(200, first.Status);
            Assert.Equal(200, again.Status);
            Assert.Equal(200, renamed.Status);
            var members = Members(l);
            Assert.Single(members); // one host seat, held by the latest join
            Assert.Equal("Andre2", members[0]["name"]);
            Assert.Equal(true, members[0]["host"]);
        }

        [Fact]
        public void AGuestWithoutTheTokenNeverBecomesTheHost()
        {
            var l = NewLobby();
            Join(l, "Host", host: true);
            var req = Post("/lobby/join", Obj("name", "Ana", "version", Program.Version), token: null);
            Assert.Equal(200, l.Handle(req).Status);
            Assert.Equal("Host", l.Info()["host"]);
        }

        // ---- HOST-02: the lobby carries on without its host ----

        [Fact]
        public void WithTheHostGoneAndAutoStartOnTheMatchStartsWhenEveryPlayerIsReady()
        {
            var l = NewLobby();
            int starts = 0;
            l.StartServerHook = o => { Interlocked.Increment(ref starts); return null; };
            var host = Join(l, "Host", host: true);
            l.Update(host.Token, Obj("team", Lobby.NoTeam)); // the host just runs the lobby; its page is now closed
            var ana = Join(l, "Ana");
            var bia = Join(l, "Bia");
            Assert.True(l.Open(IPAddress.Loopback, null, discovery: false));

            Assert.Null(l.Update(ana.Token, Obj("ready", true)));
            Assert.Null(l.Update(bia.Token, Obj("ready", true)));
            var until = DateTime.UtcNow.AddSeconds(8);
            while (Volatile.Read(ref starts) == 0 && DateTime.UtcNow < until) Thread.Sleep(50);
            Assert.Equal(1, Volatile.Read(ref starts));
        }

        // ---- local lobbies are untouched ----

        [Fact]
        public void ALobbyWithNoHostTokenExposesNoHostRoutes()
        {
            var l = NewLobby(hostToken: null);
            foreach (string route in new[] { "configure", "start", "stop", "kick", "move", "swap", "draft-reset", "visibility", "close" })
                Assert.Null(l.Handle(Post("/lobby/host/" + route))); // not found: the route does not exist
            // and a bearer header on join does not make anyone the host
            var join = Post("/lobby/join", Obj("name", "Ana", "version", Program.Version));
            Assert.Equal(200, l.Handle(join).Status);
            Assert.Equal("", l.Info()["host"]);
        }
    }
}
