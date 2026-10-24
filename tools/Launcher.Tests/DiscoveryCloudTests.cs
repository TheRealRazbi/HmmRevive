using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using HmmRevive.Launcher;
using Xunit;

namespace HmmRevive.Launcher.Tests
{
    public class DiscoveryCloudTests
    {
        public DiscoveryCloudTests() { Settings.Data = new Dictionary<string, object>(); }

        private static Dictionary<string, object> Lobby(string id, int players = 2) => new Dictionary<string, object>
            { ["id"] = id, ["hostname"] = id + ".hmm.ovidio.dev", ["host"] = "Ana", ["version"] = "1.5", ["players"] = players, ["phase"] = "lobby", ["passwordRequired"] = false };

        [Fact]
        public void ACloudLobbyAppearsWithViaCloudAndItsHttpsAddress()
        {
            var found = Discovery.Find(9697, () => new List<Dictionary<string, object>> { Lobby("a1b2c3") }, includeLocal: false);
            var l = Assert.Single(found);
            Assert.Equal("cloud", l.Str("via"));
            Assert.Equal("https://a1b2c3.hmm.ovidio.dev", l.Str("address"));
            Assert.Equal(true, l["cloud"]);
            Assert.Equal(2, l.Int("players"));
        }

        [Fact]
        public void CloudLobbiesAreMergedWithTheLocalSearch()
        {
            var found = Discovery.Find(19697, () => new List<Dictionary<string, object>> { Lobby("a1b2c3"), Lobby("d4e5f6") });
            Assert.Equal(new[] { "a1b2c3", "d4e5f6" }, found.Where(f => f.Str("via") == "cloud").Select(f => f.Str("id")).OrderBy(x => x).ToArray());
        }

        [Fact]
        public void AFailingCloudIsIgnoredAndTheRestIsStillReturned()
        {
            var found = Discovery.Find(19697, () => throw new Exception("cloud down"));
            Assert.DoesNotContain(found, f => f.Str("via") == "cloud");
        }

        [Fact]
        public void ASlowCloudNeverDelaysTheResultsBeyondItsOwnShortTimeout()
        {
            var gate = new ManualResetEventSlim();
            var watch = Stopwatch.StartNew();
            var found = Discovery.Find(9697, () => { gate.Wait(20000); return new List<Dictionary<string, object>> { Lobby("a1b2c3") }; }, includeLocal: false);
            watch.Stop();
            gate.Set();
            Assert.Empty(found);
            Assert.True(watch.ElapsedMilliseconds < Discovery.CloudWaitMs + 1500, "took " + watch.ElapsedMilliseconds + " ms");
        }

        [Fact]
        public void TheCloudIsNotCalledUnlessTheCallerAsks()
        {
            int calls = 0;
            Discovery.Find(19697); // the default search, as before this task
            Discovery.Find(19697, null);
            Assert.Equal(0, calls);
            Discovery.Find(9697, () => { calls++; return new List<Dictionary<string, object>>(); }, includeLocal: false);
            Assert.Equal(1, calls);
        }

        [Fact]
        public void ALobbyListedTwiceByIdAppearsOnce()
        {
            var found = Discovery.Find(9697, () => new List<Dictionary<string, object>> { Lobby("a1b2c3", 1), Lobby("a1b2c3", 3) }, includeLocal: false);
            var l = Assert.Single(found);
            Assert.Equal("a1b2c3", l.Str("id"));
        }

        [Fact]
        public void EntriesWithoutAnIdOrHostnameAreSkipped()
        {
            var bad = new Dictionary<string, object> { ["players"] = 1 };
            var found = Discovery.Find(9697, () => new List<Dictionary<string, object>> { bad, Lobby("a1b2c3") }, includeLocal: false);
            Assert.Single(found);
        }

        [Fact]
        public void TheRegionOfACloudLobbyIsKeptInTheList()
        {
            var l = Lobby("a1b2c3");
            l["region"] = "eastus";
            var found = Discovery.Find(9697, () => new List<Dictionary<string, object>> { l }, includeLocal: false);
            Assert.Equal("eastus", Assert.Single(found).Str("region"));
        }
    }
}
