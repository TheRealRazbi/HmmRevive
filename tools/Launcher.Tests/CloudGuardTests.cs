using System;
using HmmRevive.Launcher;
using Xunit;

namespace HmmRevive.Launcher.Tests
{
    public class CloudGuardTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 10, 18, 12, 0, 0, DateTimeKind.Utc);
        private static DateTime At(double seconds) => T0.AddSeconds(seconds);

        // COST-01: empty for 180 s after having had members.
        [Fact]
        public void EmptyFor179SecondsKeepsRunningAndFor180Stops()
        {
            var g = new CloudGuard(T0);
            Assert.Equal(GuardAction.Continue, g.Check(At(10), 2, "lobby", 0));
            Assert.Equal(GuardAction.Continue, g.Check(At(100), 0, "lobby", 0)); // everyone left at 100
            Assert.Equal(GuardAction.Continue, g.Check(At(100 + 179), 0, "lobby", 0));
            Assert.Equal(GuardAction.StopLobby, g.Check(At(100 + 180), 0, "lobby", 0));
        }

        [Fact]
        public void SomeoneReturningResetsTheEmptyTimer()
        {
            var g = new CloudGuard(T0);
            g.Check(At(10), 1, "lobby", 0);
            g.Check(At(20), 0, "lobby", 0);
            Assert.Equal(GuardAction.Continue, g.Check(At(150), 1, "lobby", 0)); // back after 130 s
            g.Check(At(160), 0, "lobby", 0);
            Assert.Equal(GuardAction.Continue, g.Check(At(160 + 179), 0, "lobby", 0));
            Assert.Equal(GuardAction.StopLobby, g.Check(At(160 + 180), 0, "lobby", 0));
        }

        // COST-02: nobody ever joined for 10 minutes.
        [Fact]
        public void NeverJoinedAt9Min59KeepsRunningAndAt10MinStops()
        {
            var g = new CloudGuard(T0);
            Assert.Equal(GuardAction.Continue, g.Check(At(10 * 60 - 1), 0, "lobby", 0));
            Assert.Equal(GuardAction.StopLobby, g.Check(At(10 * 60), 0, "lobby", 0));
        }

        [Fact]
        public void TheNeverJoinedRuleEndsOnceSomeoneHasJoined()
        {
            var g = new CloudGuard(T0);
            g.Check(At(60), 1, "lobby", 0);
            Assert.Equal(GuardAction.Continue, g.Check(At(11 * 60), 1, "lobby", 0)); // still there past 10 minutes
        }

        // COST-03: a match nobody plays stops the game server, not the container.
        [Fact]
        public void AMatchWithNoGameClientStopsTheGameServerAt10Minutes()
        {
            var g = new CloudGuard(T0);
            Assert.Equal(GuardAction.Continue, g.Check(At(30), 1, "playing", 0));
            Assert.Equal(GuardAction.Continue, g.Check(At(30 + 599), 1, "playing", 0));
            Assert.Equal(GuardAction.StopGameServer, g.Check(At(30 + 600), 1, "playing", 0));
            Assert.Equal(GuardAction.Continue, g.Check(At(30 + 601), 1, "lobby", 0)); // back in the lobby: the container lives on
        }

        [Fact]
        public void AGameClientAppearingResetsTheMatchTimer()
        {
            var g = new CloudGuard(T0);
            g.Check(At(30), 1, "playing", 0);
            Assert.Equal(GuardAction.Continue, g.Check(At(30 + 500), 1, "playing", 1)); // a client connected
            Assert.Equal(GuardAction.Continue, g.Check(At(30 + 510), 1, "playing", 0)); // and left: timer restarts
            Assert.Equal(GuardAction.Continue, g.Check(At(30 + 510 + 599), 1, "playing", 0));
            Assert.Equal(GuardAction.StopGameServer, g.Check(At(30 + 510 + 600), 1, "playing", 0));
        }

        [Fact]
        public void TheMatchRuleOnlyAppliesWhileAMatchRuns()
        {
            var g = new CloudGuard(T0);
            for (int t = 10; t < 3000; t += 100) Assert.Equal(GuardAction.Continue, g.Check(At(t), 1, "lobby", 0));
        }

        // COST-04: the hard deadline.
        [Fact]
        public void At179Min59TheLobbyRunsAndAt180MinItStops()
        {
            var g = new CloudGuard(T0);
            Assert.Equal(GuardAction.Continue, g.Check(At(180 * 60 - 1), 3, "lobby", 0));
            Assert.Equal(GuardAction.StopLobby, g.Check(At(180 * 60), 3, "lobby", 0));
        }

        [Fact]
        public void TheDeadlineStopsTheLobbyEvenDuringAMatchWithPlayers()
        {
            var g = new CloudGuard(T0);
            Assert.Equal(GuardAction.StopLobby, g.Check(At(180 * 60 + 5), 8, "playing", 8));
        }

        [Fact]
        public void AFullyHealthyLobbyKeepsRunningForHours()
        {
            var g = new CloudGuard(T0);
            for (int t = 0; t < 170 * 60; t += 30) Assert.Equal(GuardAction.Continue, g.Check(At(t), 4, "playing", 4));
        }
    }
}
