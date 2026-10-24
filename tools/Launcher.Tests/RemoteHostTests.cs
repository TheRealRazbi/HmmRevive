using System;
using System.Collections.Generic;
using System.Net;
using HmmRevive.Launcher;
using Xunit;

namespace HmmRevive.Launcher.Tests
{
    public class RemoteHostTests
    {
        private const string Token = "host-token-0123456789";
        private const string Base = "https://a1b2c3.hmm.ovidio.dev";

        private class Sent { public string Method, Url, Body; public IDictionary<string, string> Headers; }

        private static (RemoteHost host, List<Sent> sent) Make(Func<CloudReply> reply = null, string url = Base)
        {
            var sent = new List<Sent>();
            var host = new RemoteHost(url, Token, (m, u, h, b, t) =>
            {
                sent.Add(new Sent { Method = m, Url = u, Headers = new Dictionary<string, string>(h), Body = b });
                return reply != null ? reply() : new CloudReply { Status = 200, Body = "{\"ok\":true}" };
            });
            return (host, sent);
        }

        public static IEnumerable<object[]> Actions()
        {
            yield return new object[] { "configure", (Action<RemoteHost>)(h => h.Configure(new Dictionary<string, object> { ["score"] = 5 })), "score" };
            yield return new object[] { "start", (Action<RemoteHost>)(h => h.Start()), null };
            yield return new object[] { "stop", (Action<RemoteHost>)(h => h.Stop()), null };
            yield return new object[] { "kick", (Action<RemoteHost>)(h => h.Kick("m1")), "id" };
            yield return new object[] { "move", (Action<RemoteHost>)(h => h.Move("m1", "red")), "team" };
            yield return new object[] { "swap", (Action<RemoteHost>)(h => h.Swap("m1", "m2")), "b" };
            yield return new object[] { "draft-reset", (Action<RemoteHost>)(h => h.DraftReset()), null };
            yield return new object[] { "visibility", (Action<RemoteHost>)(h => h.SetVisibility("private")), "visibility" };
            yield return new object[] { "close", (Action<RemoteHost>)(h => h.Close()), null };
        }

        // Each action posts to its own route, with the token in the Authorization header and the body the local route takes.
        [Theory]
        [MemberData(nameof(Actions))]
        public void EachActionPostsToItsRouteWithTheTokenInTheHeaderOnly(string route, Action<RemoteHost> act, string bodyKey)
        {
            var (host, sent) = Make();
            act(host);
            Sent s = Assert.Single(sent);
            Assert.Equal("POST", s.Method);
            Assert.Equal(Base + "/lobby/host/" + route, s.Url);
            Assert.Equal("Bearer " + Token, s.Headers["Authorization"]);
            Assert.DoesNotContain(Token, s.Url);
            Assert.DoesNotContain(Token, s.Body);
            if (bodyKey != null) Assert.True(Js.Read(s.Body).ContainsKey(bodyKey), "body lacks " + bodyKey + ": " + s.Body);
        }

        [Fact]
        public void TheBodiesCarryTheValuesTheLocalRoutesTake()
        {
            var (host, sent) = Make();
            host.Move("m1", "red");
            host.Swap("m1", "m2");
            host.SetVisibility("private");
            Assert.Equal("m1", Js.Read(sent[0].Body).Str("id"));
            Assert.Equal("red", Js.Read(sent[0].Body).Str("team"));
            Assert.Equal("m1", Js.Read(sent[1].Body).Str("a"));
            Assert.Equal("m2", Js.Read(sent[1].Body).Str("b"));
            Assert.Equal("private", Js.Read(sent[2].Body).Str("visibility"));
        }

        [Theory]
        [InlineData(403, "Only the host can do that.")]
        [InlineData(409, "The Red team is empty: add a bot or a player.")]
        [InlineData(400, "That team is full.")]
        public void ARefusalSurfacesTheLobbysMessage(int status, string message)
        {
            var (host, _) = Make(() => new CloudReply { Status = status, Body = Js.Write(new Dictionary<string, object> { ["error"] = message }) });
            var e = Assert.Throws<CloudException>(() => host.Start());
            Assert.Equal(message, e.Message);
            Assert.Equal(status, e.Status);
        }

        [Fact]
        public void AnHttpBaseUrlIsRefusedBeforeAnyRequest()
        {
            var (host, sent) = Make(url: "http://a1b2c3.hmm.ovidio.dev");
            Assert.Throws<CloudException>(() => host.Start());
            Assert.Throws<CloudException>(() => host.Close());
            Assert.Empty(sent);
        }

        [Fact]
        public void ANetworkFailureBecomesAFriendlyError()
        {
            var host = new RemoteHost(Base, Token, (m, u, h, b, t) => throw new WebException("reset"));
            var e = Assert.Throws<CloudException>(() => host.Stop());
            Assert.Contains("reach the lobby", e.Message);
        }
    }
}
