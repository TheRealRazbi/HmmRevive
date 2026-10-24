using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using HmmRevive.Launcher;
using Xunit;

namespace HmmRevive.Launcher.Tests
{
    public class CloudHostingTests : IDisposable
    {
        private const string Key = "hmmk_testkeytestkeytestkeytestkey12";
        private readonly List<Session> _seats = new List<Session>();
        private readonly List<Lobby> _lobbies = new List<Lobby>();
        private readonly List<(string method, string url, string auth)> _calls = new List<(string, string, string)>();
        private readonly object _gate = new object();
        private Func<string, string, CloudReply> _orch;     // method, url -> reply for the orchestrator
        private Func<string, CloudReply> _lobby = r => new CloudReply { Status = 200, Body = "{\"ok\":true}" }; // lobby host routes

        public CloudHostingTests()
        {
            string dir = Path.Combine(Path.GetTempPath(), "hmmrevive-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(dir, "instance"));
            File.WriteAllText(Path.Combine(dir, "instance", "HMM.exe"), "");
            Paths.Init(dir, "instance");
            Settings.Data = new Dictionary<string, object> { ["name"] = "Andre", ["cloudKey"] = Key };
        }

        public void Dispose()
        {
            foreach (var s in _seats) s.Leave();
            foreach (var l in _lobbies) l.Close();
        }

        private static CloudReply Json(object o, int status = 200) => new CloudReply { Status = status, Body = Js.Write(o) };
        private static Dictionary<string, object> Obj(params object[] kv)
        {
            var d = new Dictionary<string, object>();
            for (int i = 0; i < kv.Length; i += 2) d[(string)kv[i]] = kv[i + 1];
            return d;
        }

        private Action<string> _onCreateBody;
        private int joined;
        private string joinedToken, joinedHost;
        private Session lastSeat;

        private CloudHosting Make(Func<string, string, CloudReply> orch)
        {
            _orch = orch;
            Cloud.Transport t = (m, u, h, b, ms) =>
            {
                if (m == "POST" && u.EndsWith("/v1/lobbies")) _onCreateBody?.Invoke(b);
                lock (_gate) _calls.Add((m, u, h.ContainsKey("Authorization") ? h["Authorization"] : null));
                if (u.Contains("/lobby/host/")) return _lobby(u);
                return _orch(m, u);
            };
            return new CloudHosting(() => new Cloud("https://api.hmm.ovidio.dev", Key, t), (url, tok) => new RemoteHost(url, tok, t),
                (host, tok) =>
                {
                    joined++; joinedHost = host; joinedToken = tok;
                    var lobby = new Lobby(0, 0, Builds.Steam);
                    _lobbies.Add(lobby);
                    string error;
                    lastSeat = Session.JoinOwn(lobby, out error);
                    if (lastSeat != null) _seats.Add(lastSeat);
                    return lastSeat;
                }, seat => { }, pollMs: 20);
        }

        private static void WaitFor(Func<bool> c, int ms = 5000)
        {
            var until = DateTime.UtcNow.AddMilliseconds(ms);
            while (!c() && DateTime.UtcNow < until) Thread.Sleep(10);
            Assert.True(c(), "condition not reached");
        }

        private static string StateOf(CloudHosting h) => (string)h.State()["state"];

        private Func<string, string, CloudReply> Orchestrator(params string[] states)
        {
            int polls = 0;
            return (m, u) =>
            {
                if (m == "POST") return Json(Obj("id", "a1b2c3", "hostname", "a1b2c3.hmm.ovidio.dev", "hostToken", "host-token-xyz"), 202);
                if (m == "DELETE") return Json(Obj("state", "closing"), 202);
                string s = states[Math.Min(polls++, states.Length - 1)];
                return Json(Obj("id", "a1b2c3", "state", s, "message", s == "creating" ? "Starting the cloud server..." : ""));
            };
        }

        [Fact]
        public void CreatePollsUntilReadyThenTakesTheHostSeatWithTheToken()
        {
            var h = Make(Orchestrator("creating", "creating", "ready"));
            Assert.Null(h.Create("1.5", "public", null, null));
            Assert.Equal("creating", StateOf(h));
            WaitFor(() => StateOf(h) == "ready");
            Assert.Equal(1, joined);
            Assert.Equal("a1b2c3.hmm.ovidio.dev", joinedHost);
            Assert.Equal("host-token-xyz", joinedToken);
            Assert.True(h.Active);
        }

        [Fact]
        public void ACreateTheOrchestratorRefusesFailsWithItsMessageAndSavesNothing()
        {
            var h = Make((m, u) => Json(Obj("error", "No cloud server is free."), 503));
            Assert.Equal("No cloud server is free.", h.Create("1.5", "public", null, null));
            Assert.Equal("failed", StateOf(h));
            Assert.Equal("No cloud server is free.", h.State()["message"]);
            Assert.False(h.Active);
            Assert.Null(CloudHosting.Saved());
            Assert.Equal(0, joined);
        }

        [Fact]
        public void WhileTheCloudLobbyIsReadyTheHostButtonsGoToTheLobbyThroughRemoteHost()
        {
            var h = Make(Orchestrator("ready"));
            h.Create("1.5", "public", null, null);
            WaitFor(() => StateOf(h) == "ready");
            Assert.Null(h.HostAction("start", Obj()));
            Assert.Null(h.HostAction("kick", Obj("id", "m1")));
            Assert.Null(h.HostAction("visibility", Obj("visibility", "private")));
            lock (_gate)
            {
                // The first host call is the settings the lobby is given when it becomes ready; the buttons come after it.
                var hostCalls = _calls.Where(c => c.url.Contains("/lobby/host/")).ToList();
                Assert.Equal("configure", hostCalls[0].url.Substring(hostCalls[0].url.LastIndexOf('/') + 1));
                hostCalls.RemoveAt(0);
                Assert.Equal(new[] { "start", "kick", "visibility" }, hostCalls.Select(c => c.url.Substring(c.url.LastIndexOf('/') + 1)).ToArray());
                Assert.All(hostCalls, c => { Assert.StartsWith("https://a1b2c3.hmm.ovidio.dev/", c.url); Assert.Equal("Bearer host-token-xyz", c.auth); });
            }
            _lobby = r => Json(Obj("error", "The Red team is empty: add a bot or a player."), 400);
            Assert.Equal("The Red team is empty: add a bot or a player.", h.HostAction("start", Obj()));
        }

        [Fact]
        public void LocalHostingIsUntouchedWhenNoCloudLobbyIsOpen()
        {
            var h = Make(Orchestrator("ready"));
            Assert.False(h.Active); // the page's host buttons keep using the local lobby
            Assert.Equal("Your cloud lobby is not ready yet.", h.HostAction("start", Obj()));
            h.Create("1.5", "public", null, null);
            WaitFor(() => StateOf(h) == "ready");
            h.Close();
            Assert.False(h.Active);
            lock (_gate) Assert.DoesNotContain(_calls, c => c.url.Contains("/lobby/host/") && !c.url.EndsWith("/configure")); // only the initial settings
        }

        [Fact]
        public void ClosingDeletesTheLobbyAtTheOrchestratorAndForgetsIt()
        {
            var h = Make(Orchestrator("ready"));
            h.Create("1.5", "public", null, null);
            WaitFor(() => StateOf(h) == "ready");
            Assert.Null(h.Close());
            lock (_gate) Assert.Contains(_calls, c => c.method == "DELETE" && c.url == "https://api.hmm.ovidio.dev/v1/lobbies/a1b2c3" && c.auth == null);
            Assert.Equal("idle", StateOf(h));
            Assert.Null(CloudHosting.Saved());
            Assert.False(Settings.Data.ContainsKey(CloudHosting.SavedKey));
        }

        [Fact]
        public void TheLobbyIdAndHostTokenAreSavedInTheSettings()
        {
            var h = Make(Orchestrator("creating"));
            h.Create("1.5", "private", "pw", "x");
            var saved = Settings.Data.Obj(CloudHosting.SavedKey);
            Assert.Equal("a1b2c3", saved.Str("id"));
            Assert.Equal("a1b2c3.hmm.ovidio.dev", saved.Str("hostname"));
            Assert.Equal("host-token-xyz", saved.Str("hostToken"));
            Assert.Equal("a1b2c3", CloudHosting.Saved().Str("id"));
            Assert.False(CloudHosting.Saved().ContainsKey("hostToken")); // what the page sees never has the token
            h.Close();
        }

        [Fact]
        public void RejoinAfterARestartTakesTheHostSeatBackWithTheSavedToken()
        {
            Settings.Data[CloudHosting.SavedKey] = Obj("id", "a1b2c3", "hostname", "a1b2c3.hmm.ovidio.dev", "hostToken", "saved-token");
            var h = Make(Orchestrator("ready"));
            Assert.Null(h.Rejoin());
            WaitFor(() => StateOf(h) == "ready");
            Assert.Equal("saved-token", joinedToken);
            Assert.Equal("a1b2c3.hmm.ovidio.dev", joinedHost);
            h.Close();
        }

        [Fact]
        public void RejoinWithADeadLobbyReportsItAndForgetsIt()
        {
            Settings.Data[CloudHosting.SavedKey] = Obj("id", "a1b2c3", "hostname", "a1b2c3.hmm.ovidio.dev", "hostToken", "saved-token");
            var h = Make((m, u) => Json(Obj("error", "No such cloud lobby."), 404));
            Assert.Equal("That cloud lobby has ended.", h.Rejoin());
            Assert.Equal("failed", StateOf(h));
            Assert.Null(CloudHosting.Saved());
            Assert.Equal(0, joined);

            Settings.Data[CloudHosting.SavedKey] = Obj("id", "a1b2c3", "hostname", "a1b2c3.hmm.ovidio.dev", "hostToken", "t");
            var h2 = Make(Orchestrator("closing"));
            Assert.Equal("That cloud lobby has ended.", h2.Rejoin());
            Assert.Null(CloudHosting.Saved());
        }

        [Fact]
        public void TheInviteKeyIsSavedOnlyWhenItIsANonEmptyStringOfAtMost128Characters()
        {
            Assert.NotNull(CloudHosting.SaveKey(""));
            Assert.NotNull(CloudHosting.SaveKey("   "));
            Assert.NotNull(CloudHosting.SaveKey(null));
            Assert.NotNull(CloudHosting.SaveKey(new string('k', 129)));
            Assert.NotNull(CloudHosting.SaveKey("bad\nkey"));
            Assert.Equal(Key, Settings.Data.Str("cloudKey")); // none of the refusals changed it
            Assert.Null(CloudHosting.SaveKey(new string('k', 128)));
            Assert.Equal(new string('k', 128), Settings.Data.Str("cloudKey"));
            Assert.Null(CloudHosting.SaveKey("  hmmk_new  "));
            Assert.Equal("hmmk_new", Settings.Data.Str("cloudKey"));
        }

        [Fact]
        public void AFailedLobbyReportsTheOrchestratorsMessage()
        {
            int polls = 0;
            var h = Make((m, u) => m == "POST"
                ? Json(Obj("id", "a1b2c3", "hostname", "a1b2c3.hmm.ovidio.dev", "hostToken", "t"), 202)
                : Json(Obj("id", "a1b2c3", "state", polls++ < 1 ? "creating" : "failed", "message", "The cloud server did not start in time.")));
            h.Create("1.5", "public", null, null);
            WaitFor(() => StateOf(h) == "failed");
            Assert.Equal("The cloud server did not start in time.", h.State()["message"]);
            Assert.Null(CloudHosting.Saved());
            Assert.False(h.Active);
        }

        [Fact]
        public void WhileCreatingTheStateCarriesTheProgressAndFollowsTheOrchestratorsStep()
        {
            int polls = 0;
            var h = Make((m, u) => m == "POST"
                ? Json(Obj("id", "a1b2c3", "hostname", "a1b2c3.hmm.ovidio.dev", "hostToken", "t"), 202)
                : Json(Obj("id", "a1b2c3", "state", "creating", "step", polls++ < 3 ? "reserving" : "pulling", "elapsedSeconds", polls < 4 ? 10 : 140, "message", "Starting the cloud server...")));
            Assert.Null(h.Create("1.5", "public", null, null));
            var first = (Dictionary<string, object>)h.State()["progress"];
            Assert.Equal("reserve", first["step"]); // the create call was answered: the orchestrator has it
            WaitFor(() => (string)((Dictionary<string, object>)h.State()["progress"])["step"] == "pull");
            var later = (Dictionary<string, object>)h.State()["progress"];
            Assert.True((int)later["elapsedSeconds"] >= 140, "elapsed time follows the orchestrator's clock (a rejoined lobby is older than this launcher run)");
            Assert.InRange((int)later["percent"], 50, 99);
            h.Close();
        }

        [Fact]
        public void ThereIsNoProgressOnceTheLobbyIsReady()
        {
            var h = Make(Orchestrator("ready"));
            h.Create("1.5", "public", null, null);
            WaitFor(() => StateOf(h) == "ready");
            Assert.False(h.State().ContainsKey("progress"));
            h.Close();
        }

        [Fact]
        public void TheRegionOfThePageGoesToTheOrchestratorAndIsRememberedForNextTime()
        {
            string body = null;
            var h = Make((m, u) =>
            {
                if (m == "POST") return Json(Obj("id", "a1b2c3", "hostname", "a1b2c3.hmm.ovidio.dev", "hostToken", "t"), 202);
                return Json(Obj("id", "a1b2c3", "state", "creating", "step", "reserving", "elapsedSeconds", 5));
            });
            _onCreateBody = b => body = b;
            Assert.Null(h.Create("1.5", "public", null, null, "spaincentral"));
            Assert.Equal("spaincentral", Js.Read(body).Str("region"));
            Assert.Equal("spaincentral", Settings.Data.Str("cloudRegion"));
            h.Close();
        }

        [Fact]
        public void ARegionThatIsNotOnTheListIsRefusedWithoutCallingTheCloud()
        {
            var h = Make(Orchestrator("creating"));
            string why = h.Create("1.5", "public", null, null, "mars");
            Assert.Contains("region", why);
            lock (_gate) Assert.Empty(_calls);
            Assert.Equal("failed", StateOf(h));
        }
    }
}
