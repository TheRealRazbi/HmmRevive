using HmmRevive.Launcher;
using Xunit;

namespace HmmRevive.Launcher.Tests
{
    public class JsTests
    {
        // The foundation test: the project builds against the launcher assembly and can call into it.
        [Fact]
        public void WriteThenReadKeepsTheValues()
        {
            string json = Js.Write(new { name = "lobby", players = 3, open = true });
            var back = Js.Read(json);
            Assert.Equal("lobby", back.Str("name"));
            Assert.Equal(3, back.Int("players"));
            Assert.True(back.Bool("open"));
        }
    }
}
