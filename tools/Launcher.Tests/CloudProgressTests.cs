using System.Collections.Generic;
using System.Linq;
using HmmRevive.Launcher;
using Xunit;

namespace HmmRevive.Launcher.Tests
{
    public class CloudProgressTests
    {
        private static int Percent(string step, double t) => (int)CloudProgress.Compute(step, t)["percent"];
        private static int? Eta(string step, double t) => (int?)CloudProgress.Compute(step, t)["etaSeconds"];

        [Fact]
        public void TheOrchestratorsStepsMapToTheFourScreenSteps()
        {
            Assert.Equal("contact", CloudProgress.FromOrchestrator(null));
            Assert.Equal("reserve", CloudProgress.FromOrchestrator("reserving"));
            Assert.Equal("pull", CloudProgress.FromOrchestrator("pulling"));
            Assert.Equal("start", CloudProgress.FromOrchestrator("starting"));
            Assert.Equal("connect", CloudProgress.FromOrchestrator("connecting"));
            Assert.Equal("contact", CloudProgress.FromOrchestrator("something new"));
        }

        [Fact]
        public void ItStartsNearZeroAndNeverReaches100BeforeTheLobbyIsReady()
        {
            Assert.InRange(Percent("contact", 0), 0, 3);
            Assert.True(Percent("connect", 10_000) <= 99);
        }

        [Fact]
        public void ProgressNeverGoesBackwardsOnATypicalTimelineAndOnASlowOne()
        {
            // The step changes at these elapsed seconds: typical (measured) and a run where the download took twice as long.
            foreach (var timeline in new[] { new[] { 0, 4, 14, 42, 54 }, new[] { 0, 6, 25, 100, 125 } })
            {
                int last = -1;
                for (int t = 0; t <= 300; t += 2)
                {
                    int idx = timeline.Count(at => t >= at) - 1;
                    int p = Percent(CloudProgress.Steps[idx], t);
                    Assert.True(p >= last, $"timeline {string.Join("/", timeline)}: {p}% at {t}s after {last}%");
                    last = p;
                }
            }
        }

        [Fact]
        public void AStepThatFinishedEarlyJumpsToTheStartOfTheNextBand()
        {
            // The download step reached at 8 s (faster than usual): the bar sits at the start of that step, not at the clock.
            Assert.True(Percent("pull", 8) >= Percent("reserve", 13));
        }

        [Fact]
        public void TheEstimateShrinksAsTimePassesWithinAStep()
        {
            Assert.True(Eta("pull", 20) > Eta("pull", 35));
            Assert.True(Eta("pull", 20) > Eta("connect", 60));
        }

        [Fact]
        public void TheEstimateAtTheStartIsTheMeasuredTimeToOpenALobby()
        {
            Assert.InRange((int)Eta("contact", 0), 60, 80); // 70 s measured in Brazil South with the game outside the image
        }

        [Fact]
        public void ALongOverrunDropsTheEstimateAndSaysItIsTakingLonger()
        {
            var late = CloudProgress.Compute("pull", 300);
            Assert.Equal(true, late["slow"]);
            Assert.Null(late["etaSeconds"]);
            Assert.Equal(false, CloudProgress.Compute("connect", 115)["slow"]); // a normal East US or Madrid lobby
        }

        [Fact]
        public void AStepThatRunsOverStillPromisesSomethingSensible()
        {
            int? eta = Eta("pull", 100); // past the typical end of the step but not yet slow
            Assert.InRange((int)eta, 30, 45); // ten seconds for the step plus the steps still to come
        }

        [Fact]
        public void TheStepListMarksDoneCurrentAndTodo()
        {
            var steps = ((List<object>)CloudProgress.Compute("pull", 60)["steps"]).Cast<Dictionary<string, object>>().ToList();
            Assert.Equal(new[] { "contact", "reserve", "pull", "start", "connect" }, steps.Select(s => (string)s["key"]).ToArray());
            Assert.Equal(new[] { "done", "done", "current", "todo", "todo" }, steps.Select(s => (string)s["state"]).ToArray());
            var d = CloudProgress.Compute("pull", 60);
            Assert.Equal(3, d["number"]);
            Assert.Equal(5, d["count"]);
        }

        [Fact]
        public void ALongReservingIsShownAsTheDownloadBecauseAzureOnlyReportsItLater()
        {
            Assert.Equal("reserve", CloudProgress.Compute("reserve", 10)["step"]);
            Assert.Equal("pull", CloudProgress.Compute("reserve", 60)["step"]);
            Assert.Equal("pull", CloudProgress.Compute("reserve", 200)["step"]); // a far region downloads for minutes
            Assert.Equal("start", CloudProgress.Compute("start", 60)["step"]);   // the other steps are not guessed
        }
    }
}
