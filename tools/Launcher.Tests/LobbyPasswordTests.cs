using System.Collections.Generic;
using HmmRevive.Launcher;
using Xunit;

namespace HmmRevive.Launcher.Tests
{
    public class LobbyPasswordTests
    {
        private static Lobby NewLobby() => new Lobby(19697, 19696, Builds.Steam);

        // A fresh lobby password for every run: the tests need any password, never a fixed one in the source.
        private static readonly string Pw = "pw" + System.Guid.NewGuid().ToString("N").Substring(0, 10);

        private static Lobby.Member Join(Lobby lobby, string name, string password, out string error) =>
            lobby.Join(name, Program.Version, new[] { Builds.Steam }, false, out error, password);

        // SEC-07: with no password set a join works exactly as before, whatever password the client sends.
        [Fact]
        public void WithNoPasswordSetAnyoneJoins()
        {
            var lobby = NewLobby();
            Assert.NotNull(Join(lobby, "Ana", null, out string e1));
            Assert.Null(e1);
            Assert.NotNull(Join(lobby, "Bia", "whatever", out string e2));
            Assert.Null(e2);
        }

        [Fact]
        public void TheRightPasswordJoins()
        {
            var lobby = NewLobby();
            lobby.SetPassword(Pw);
            Assert.NotNull(Join(lobby, "Ana", Pw, out string error));
            Assert.Null(error);
        }

        [Fact]
        public void AWrongPasswordIsRefusedWithAReasonAndNobodyIsAdded()
        {
            var lobby = NewLobby();
            lobby.SetPassword(Pw);
            Assert.Null(Join(lobby, "Ana", Pw.Substring(0, Pw.Length - 1), out string error));
            Assert.Equal("Wrong password.", error);
            Assert.Null(Join(lobby, "Bia", Pw.ToUpperInvariant(), out error)); // case matters
            Assert.Equal(0, (int)lobby.Info()["players"]);
        }

        [Fact]
        public void AMissingPasswordIsRefusedWithAReason()
        {
            var lobby = NewLobby();
            lobby.SetPassword(Pw);
            Assert.Null(Join(lobby, "Ana", null, out string error));
            Assert.Equal("This lobby needs a password.", error);
            Assert.Null(Join(lobby, "Ana", "", out error));
            Assert.Equal("This lobby needs a password.", error);
        }

        [Fact]
        public void ThePasswordAlsoProtectsAPrivateLobby()
        {
            var lobby = NewLobby();
            Assert.True(lobby.SetVisibility("private"));
            lobby.SetPassword(Pw);
            Assert.Null(Join(lobby, "Ana", "nope", out string error));
            Assert.NotNull(error);
            Assert.NotNull(Join(lobby, "Ana", Pw, out error));
        }

        // LIST-02: private only means unlisted; anyone who knows the address (and the password, if any) still joins.
        [Fact]
        public void APrivateLobbyIsStillJoinedByAClientThatKnowsItsAddress()
        {
            var lobby = NewLobby();
            Assert.True(lobby.SetVisibility("private"));
            Assert.NotNull(Join(lobby, "Ana", null, out string error));
            Assert.Null(error);
            Assert.False(lobby.SetVisibility("secret")); // only the two known values
            Assert.Equal("private", lobby.Visibility);
        }

        [Fact]
        public void ACloudLobbyReportsItsVisibilityPasswordAndCloudFlag()
        {
            var lobby = NewLobby();
            lobby.Cloud = true;
            lobby.SetVisibility("private");
            lobby.SetPassword(Pw);
            Dictionary<string, object> info = lobby.Info();
            Assert.Equal("private", info["visibility"]);
            Assert.Equal(true, info["passwordRequired"]);
            Assert.Equal(true, info["cloud"]);
            Assert.Equal("private", lobby.Snapshot(null)["visibility"]);
            Assert.False(info.ContainsValue(Pw)); // the password itself is never reported
        }

        [Fact]
        public void ALocalLobbyReportsPublicNoPasswordAndNotCloud()
        {
            Dictionary<string, object> info = NewLobby().Info();
            Assert.Equal("public", info["visibility"]);
            Assert.Equal(false, info["passwordRequired"]);
            Assert.Equal(false, info["cloud"]);
        }
    }
}
