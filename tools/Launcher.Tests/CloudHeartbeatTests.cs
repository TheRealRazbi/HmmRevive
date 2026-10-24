using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using HmmRevive.Launcher;
using Xunit;

namespace HmmRevive.Launcher.Tests
{
    public class CloudHeartbeatTests : IDisposable
    {
        private const string Secret = "lobby-secret-abc123";
        private readonly object _gate = new object();
        private readonly List<Request> _seen = new List<Request>();
        private readonly HttpServer _orch;
        private Func<Request, Response> _reply = r => Response.Json(new Dictionary<string, object> { ["action"] = "none" });

        public CloudHeartbeatTests()
        {
            _orch = new HttpServer(IPAddress.Loopback, 0, r =>
            {
                lock (_gate) _seen.Add(r);
                return _reply(r);
            });
            Assert.True(_orch.Start());
        }

        public void Dispose() { _orch.Stop(); }

        private string Url => "http://127.0.0.1:" + _orch.Port;
        private int Count() { lock (_gate) return _seen.Count; }

        private CloudHeartbeat Make(Func<Dictionary<string, object>> payload = null, Action close = null, int ms = 60, Action<string> log = null) =>
            new CloudHeartbeat(Url, "a1b2c3", Secret, payload ?? (() => new Dictionary<string, object> { ["phase"] = "lobby" }), close ?? (() => { }), TimeSpan.FromMilliseconds(ms), log);

        private static void WaitFor(Func<bool> c)
        {
            var until = DateTime.UtcNow.AddSeconds(5);
            while (!c() && DateTime.UtcNow < until) Thread.Sleep(10);
            Assert.True(c(), "condition not reached");
        }

        // COST-05: a report on every interval, with the secret header, to the lobby's own heartbeat route.
        [Fact]
        public void AHeartbeatArrivesEveryIntervalWithTheSecretHeader()
        {
            var hb = Make();
            hb.Start();
            WaitFor(() => Count() >= 3);
            hb.Stop();
            lock (_gate)
            {
                Assert.All(_seen, r =>
                {
                    Assert.Equal("POST", r.Method);
                    Assert.Equal("/internal/lobbies/a1b2c3/heartbeat", r.Path);
                    Assert.Equal(Secret, r.Header("X-Lobby-Secret"));
                });
            }
        }

        [Fact]
        public void ThePayloadCarriesVisibilityMembersAndPhase()
        {
            var hb = Make(() => new Dictionary<string, object> { ["visibility"] = "public", ["members"] = 3, ["phase"] = "playing", ["gameClients"] = 3 });
            Assert.True(hb.Beat());
            var body = Js.Read(_seen[0].Body);
            Assert.Equal("public", body.Str("visibility"));
            Assert.Equal(3, body.Int("members"));
            Assert.Equal("playing", body.Str("phase"));
            Assert.Equal(3, body.Int("gameClients"));
        }

        // COST-11: a delete in the orchestrator ends the lobby gracefully.
        [Fact]
        public void ACloseReplyStopsTheLobby()
        {
            _reply = r => Response.Json(new Dictionary<string, object> { ["action"] = "close" });
            int closed = 0;
            var hb = Make(close: () => Interlocked.Increment(ref closed));
            Assert.False(hb.Beat());
            Assert.Equal(1, closed);
        }

        [Fact]
        public void AnUnknownLobbyAnswer404AlsoStopsTheLobby()
        {
            _reply = r => Response.Error("No such lobby.", 404);
            int closed = 0;
            Assert.False(Make(close: () => Interlocked.Increment(ref closed)).Beat());
            Assert.Equal(1, closed);
        }

        [Fact]
        public void AFailedHeartbeatIsRetriedAtTheNextTickAndNeverStopsTheLobby()
        {
            int closed = 0, calls = 0;
            _reply = r => ++calls <= 2 ? Response.Error("boom", 500) : Response.Json(new Dictionary<string, object> { ["action"] = "none" });
            var hb = Make(close: () => Interlocked.Increment(ref closed));
            hb.Start();
            WaitFor(() => Count() >= 4);
            hb.Stop();
            Assert.Equal(0, closed);

            var unreachable = new CloudHeartbeat("http://127.0.0.1:1", "x", Secret, () => new Dictionary<string, object>(), () => Interlocked.Increment(ref closed), TimeSpan.FromMilliseconds(50));
            Assert.True(unreachable.Beat()); // no connection: still alive
            Assert.Equal(0, closed);
        }

        [Fact]
        public void TheSecretIsInTheHeaderOnlyNotInTheUrlOrTheBody()
        {
            Make().Beat();
            Request r = _seen[0];
            Assert.DoesNotContain(Secret, r.Path);
            Assert.DoesNotContain(Secret, string.Join("&", r.Query.Values));
            Assert.DoesNotContain(Secret, r.Body);
        }

        [Fact]
        public void TheSecretIsNeverWrittenToTheLog()
        {
            var lines = new List<string>();
            _reply = r => Response.Error("boom", 500);
            Make(log: lines.Add).Beat();
            new CloudHeartbeat("http://127.0.0.1:1", "x", Secret, () => new Dictionary<string, object>(), () => { }, TimeSpan.FromSeconds(1), lines.Add).Beat();
            _reply = r => Response.Error("gone", 404);
            Make(log: lines.Add).Beat();
            Assert.NotEmpty(lines);
            Assert.DoesNotContain(lines, l => l.Contains(Secret));
        }

        [Fact]
        public void AVisibilityChangeShowsInTheNextPayload()
        {
            string visibility = "public";
            var hb = Make(() => new Dictionary<string, object> { ["visibility"] = visibility });
            hb.Beat();
            visibility = "private";
            hb.Beat();
            Assert.Equal("public", Js.Read(_seen[0].Body).Str("visibility"));
            Assert.Equal("private", Js.Read(_seen[1].Body).Str("visibility"));
        }
    }
}
