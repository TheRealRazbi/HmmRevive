using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using HmmRevive.Launcher;
using Xunit;

namespace HmmRevive.Launcher.Tests
{
    public class CloudClientTests
    {
        private const string Key = "hmmk_secretsecretsecretsecretsecret12";

        private class Sent
        {
            public string Method, Url, Body;
            public IDictionary<string, string> Headers;
            public int TimeoutMs;
        }

        private static (Cloud cloud, List<Sent> sent) Make(Func<int, CloudReply> reply, string url = "https://api.hmm.ovidio.dev", string key = Key)
        {
            var sent = new List<Sent>();
            var cloud = new Cloud(url, key, (m, u, h, b, t) =>
            {
                sent.Add(new Sent { Method = m, Url = u, Headers = new Dictionary<string, string>(h), Body = b, TimeoutMs = t });
                return reply(sent.Count);
            });
            return (cloud, sent);
        }

        private static CloudReply Ok(object o, int status = 200) => new CloudReply { Status = status, Body = Js.Write(o) };

        // NET-07: the key must never leave in clear text.
        [Fact]
        public void AnHttpAddressIsRefusedBeforeAnythingIsSent()
        {
            var (cloud, sent) = Make(n => Ok(new Dictionary<string, object>()), url: "http://api.hmm.ovidio.dev");
            Assert.Throws<CloudException>(() => cloud.Create("1.5", "public", null, null));
            Assert.Throws<CloudException>(() => cloud.Get("a1b2c3"));
            Assert.Throws<CloudException>(() => cloud.Delete("a1b2c3"));
            Assert.Throws<CloudException>(() => cloud.List());
            Assert.Empty(sent);
        }

        [Fact]
        public void CreateSendsTheKeyAndTheChoicesAndReturnsIdHostnameAndHostToken()
        {
            var (cloud, sent) = Make(n => Ok(new Dictionary<string, object> { ["id"] = "a1b2c3", ["hostname"] = "a1b2c3.hmm.ovidio.dev", ["hostToken"] = "tok" }, 202));
            var r = cloud.Create("1.5", "private", "pw", "Friday cup");
            Assert.Equal("a1b2c3", r.Str("id"));
            Assert.Equal("a1b2c3.hmm.ovidio.dev", r.Str("hostname"));
            Assert.Equal("tok", r.Str("hostToken"));
            Sent s = Assert.Single(sent);
            Assert.Equal("POST", s.Method);
            Assert.Equal("https://api.hmm.ovidio.dev/v1/lobbies", s.Url);
            Assert.Equal(Key, s.Headers["X-Api-Key"]);
            var body = Js.Read(s.Body);
            Assert.Equal("1.5", body.Str("version"));
            Assert.Equal("private", body.Str("visibility"));
            Assert.Equal("pw", body.Str("password"));
            Assert.Equal("Friday cup", body.Str("label"));
        }

        [Fact]
        public void GetReadsOneLobbyWithTheKey()
        {
            var (cloud, sent) = Make(n => Ok(new Dictionary<string, object> { ["id"] = "a1b2c3", ["state"] = "ready" }));
            Assert.Equal("ready", cloud.Get("a1b2c3").Str("state"));
            Assert.Equal("GET", sent[0].Method);
            Assert.Equal("https://api.hmm.ovidio.dev/v1/lobbies/a1b2c3", sent[0].Url);
            Assert.Equal(Key, sent[0].Headers["X-Api-Key"]);
        }

        [Fact]
        public void DeleteSendsDeleteWithTheKey()
        {
            var (cloud, sent) = Make(n => Ok(new Dictionary<string, object> { ["state"] = "closing" }, 202));
            cloud.Delete("a1b2c3");
            Assert.Equal("DELETE", sent[0].Method);
            Assert.Equal("https://api.hmm.ovidio.dev/v1/lobbies/a1b2c3", sent[0].Url);
            Assert.Equal(Key, sent[0].Headers["X-Api-Key"]);
        }

        [Fact]
        public void ListNeedsNoKeyAndReturnsTheLobbies()
        {
            var (cloud, sent) = Make(n => Ok(new Dictionary<string, object>
                { ["lobbies"] = new object[] { new Dictionary<string, object> { ["id"] = "a1b2c3", ["players"] = 3 }, new Dictionary<string, object> { ["id"] = "d4e5f6", ["players"] = 0 } } }), key: null);
            var list = cloud.List();
            Assert.Equal(new[] { "a1b2c3", "d4e5f6" }, list.Select(l => l.Str("id")).ToArray());
            Assert.False(sent[0].Headers.ContainsKey("X-Api-Key"));
        }

        // CREATE-08: a slow orchestrator (cold start) is waited for 30 s, tried once more, then reported.
        [Fact]
        public void ATimeoutIsRetriedOnceAndThenReportedAsUnavailable()
        {
            var (cloud, sent) = Make(n => throw new WebException("timed out", WebExceptionStatus.Timeout));
            var e = Assert.Throws<CloudException>(() => cloud.Get("a1b2c3"));
            Assert.Contains("unavailable", e.Message);
            Assert.Equal(2, sent.Count);
            Assert.All(sent, s => Assert.Equal(30000, s.TimeoutMs));
        }

        [Fact]
        public void AFailureThatRecoversOnTheRetryWorks()
        {
            var (cloud, sent) = Make(n => n == 1 ? throw new TimeoutException() : Ok(new Dictionary<string, object> { ["state"] = "ready" }));
            Assert.Equal("ready", cloud.Get("a1b2c3").Str("state"));
            Assert.Equal(2, sent.Count);
        }

        [Fact]
        public void CreateIsNeverRetriedBecauseASecondLobbyWouldBeBilled()
        {
            var (cloud, sent) = Make(n => throw new TimeoutException());
            Assert.Throws<CloudException>(() => cloud.Create("1.5", "public", null, null));
            Assert.Single(sent);
        }

        [Fact]
        public void TheOrchestratorsMessageReachesThePlayer()
        {
            var (cloud, sent) = Make(n => new CloudReply { Status = 429, Body = Js.Write(new Dictionary<string, object> { ["error"] = "You already have 2 cloud lobbies." }) });
            var e = Assert.Throws<CloudException>(() => cloud.Create("1.5", "public", null, null));
            Assert.Equal("You already have 2 cloud lobbies.", e.Message);
            Assert.Equal(429, e.Status);
            Assert.Single(sent); // an answer is final: no retry
        }

        [Fact]
        public void TheKeyTravelsOnlyInTheHeader()
        {
            var (cloud, sent) = Make(n => Ok(new Dictionary<string, object> { ["id"] = "a1b2c3" }, 202));
            cloud.Create("1.5", "public", "pw", "x");
            cloud.Get("a1b2c3");
            Assert.All(sent, s =>
            {
                Assert.DoesNotContain(Key, s.Url);
                Assert.DoesNotContain(Key, s.Body ?? "");
            });
        }

        [Fact]
        public void CreateWithoutAKeyAsksForOneWithoutCallingTheCloud()
        {
            var (cloud, sent) = Make(n => Ok(new Dictionary<string, object>()), key: "");
            var e = Assert.Throws<CloudException>(() => cloud.Create("1.5", "public", null, null));
            Assert.Contains("invite key", e.Message);
            Assert.Empty(sent);
        }

        // CREATE-09: the region the host picked travels with the create request.
        [Fact]
        public void CreateSendsTheRegionWhenOneIsPickedAndNothingWhenNot()
        {
            var (cloud, sent) = Make(n => Ok(new Dictionary<string, object> { ["id"] = "a1b2c3" }, 202));
            cloud.Create("1.5", "public", null, null, "eastus");
            cloud.Create("1.5", "public", null, null);
            Assert.Equal("eastus", Js.Read(sent[0].Body).Str("region"));
            Assert.False(Js.Read(sent[1].Body).ContainsKey("region")); // the orchestrator then uses Brazil
        }
    }
}
