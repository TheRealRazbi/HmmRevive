using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using HmmRevive.Launcher;
using Xunit;

namespace HmmRevive.Launcher.Tests
{
    public class PingTests
    {
        private static int FreePort()
        {
            var t = new TcpListener(IPAddress.Loopback, 0);
            t.Start();
            int p = ((IPEndPoint)t.LocalEndpoint).Port;
            t.Stop();
            return p;
        }

        [Fact]
        public void ARealTcpHandshakeToAListeningPortGivesAFewMilliseconds()
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            try
            {
                int? ms = Ping.TcpRtt(IPAddress.Loopback, ((IPEndPoint)l.LocalEndpoint).Port, 1000);
                Assert.NotNull(ms);
                Assert.InRange(ms.Value, 1, 200);
            }
            finally { l.Stop(); }
        }

        [Fact]
        public void AClosedPortGivesNoNumberAndDoesNotHang()
        {
            var watch = Stopwatch.StartNew();
            Assert.Null(Ping.TcpRtt(IPAddress.Loopback, FreePort(), 1000));
            Assert.True(watch.ElapsedMilliseconds < 1500, "took " + watch.ElapsedMilliseconds + " ms");
        }

        [Fact]
        public void TheMeasurementIsTheMedianOfTheAnsweredProbes()
        {
            var values = new Queue<int?>(new int?[] { 90, null, 30 });
            int? ms = Ping.Measure("127.0.0.1", 9, 3, 100, (ip, port, t) => values.Dequeue());
            Assert.Equal(90, ms); // 30 and 90 answered: the upper middle of two
            var five = new Queue<int?>(new int?[] { 20, 400, 25, 22, 24 });
            Assert.Equal(24, Ping.Measure("127.0.0.1", 9, 5, 100, (ip, port, t) => five.Dequeue())); // one spike does not move it
            Assert.Null(Ping.Measure("127.0.0.1", 9, 3, 100, (ip, port, t) => null));
        }

        [Fact]
        public void ANameThatDoesNotResolveGivesNoNumber()
        {
            Assert.Null(Ping.Measure("this-name-does-not-exist.invalid", 443, 1, 300, (ip, port, t) => 5));
        }

        [Theory]
        [InlineData("https://a1b2c3.hmm.ovidio.dev", "a1b2c3.hmm.ovidio.dev", 443)]
        [InlineData("100.64.1.2", "100.64.1.2", 9697)]
        [InlineData("192.168.0.5:9700", "192.168.0.5", 9700)]
        [InlineData("http://myhost.lan:9697/", "myhost.lan", 9697)]
        public void TheTimedPortIsTheLobbysOwn(string address, string host, int port)
        {
            Assert.True(Ping.Target(address, 9697, out string h, out int p));
            Assert.Equal(host, h);
            Assert.Equal(port, p);
        }

        [Fact]
        public void AnEmptyAddressHasNoTarget()
        {
            Assert.False(Ping.Target("", 9697, out _, out _));
            Assert.False(Ping.Target(null, 9697, out _, out _));
        }

        [Fact]
        public void TheStatsShowCurrentAverageBestWorstAndLoss()
        {
            var s = new PingStats();
            Assert.Null(s.Snapshot()); // nothing yet
            foreach (int? v in new int?[] { 20, 40, null, 30 }) s.Add(v);
            var d = s.Snapshot();
            Assert.Equal(30, d["ms"]);
            Assert.Equal(30, d["avg"]);
            Assert.Equal(20, d["min"]);
            Assert.Equal(40, d["max"]);
            Assert.Equal(25, d["lossPercent"]); // one of four lost
        }

        [Fact]
        public void TheStatsOnlyRememberTheLastReadingsAndShowALostLatestAsNull()
        {
            var s = new PingStats(3);
            foreach (int? v in new int?[] { 500, 10, 20, 30 }) s.Add(v);
            Assert.Equal(20, s.Snapshot()["avg"]); // the 500 fell out of the window
            s.Add(null);
            Assert.Null(s.Snapshot()["ms"]);
            var lost = new PingStats();
            lost.Add(null);
            Assert.Null(lost.Snapshot()["avg"]);
            Assert.Equal(100, lost.Snapshot()["lossPercent"]);
        }

        [Fact]
        public void TheLobbyListHasNoPingUnlessTheCallerAsksForIt()
        {
            var cloud = new Func<List<Dictionary<string, object>>>(() => new List<Dictionary<string, object>>
            {
                new Dictionary<string, object> { ["id"] = "a1b2c3", ["hostname"] = "a1b2c3.hmm.ovidio.dev" },
                new Dictionary<string, object> { ["id"] = "d4e5f6", ["hostname"] = "d4e5f6.hmm.ovidio.dev" },
            });
            Settings.Data = new Dictionary<string, object>();
            var asked = new List<string>();
            Ping.Probe probe = (ip, port, t) => { lock (asked) asked.Add(port.ToString()); return 42; };
            var without = Discovery.Find(9697, cloud, includeLocal: false, pinger: probe); // measurePing is off by default
            Assert.Equal(2, without.Count);
            Assert.All(without, l => Assert.False(l.ContainsKey("ping")));
            Assert.Empty(asked);
        }

        [Fact]
        public void ALobbyThatAnswersGetsItsPingAndOneThatDoesNotGetsNone()
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            try
            {
                int open = ((IPEndPoint)l.LocalEndpoint).Port, closed = FreePort();
                Settings.Data = new Dictionary<string, object>();
                var list = new List<Dictionary<string, object>>
                {
                    new Dictionary<string, object> { ["id"] = "up", ["address"] = "127.0.0.1:" + open },
                    new Dictionary<string, object> { ["id"] = "down", ["address"] = "127.0.0.1:" + closed },
                };
                var type = typeof(Discovery).GetMethod("AddPing", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                type.Invoke(null, new object[] { list, 9697, null });
                Assert.NotNull(list[0]["ping"]);
                Assert.Null(list[1]["ping"]);
            }
            finally { l.Stop(); }
        }
    }
}
