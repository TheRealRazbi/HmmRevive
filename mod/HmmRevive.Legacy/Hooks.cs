using System;
using System.Collections;
using System.Reflection;
using HeavyMetalMachines;
using HeavyMetalMachines.Frontend;
using HeavyMetalMachines.Match;
using HeavyMetalMachines.Server;
using Pocketverse;
using UnityEngine;

namespace HmmRevive.Legacy
{
    /// <summary>
    /// Static entry points that tools/Patcher splices into the old build's Assembly-CSharp-firstpass.dll.
    /// Keep signatures in sync with the legacy patch table in tools/Patcher/Program.cs.
    /// </summary>
    public static class Hooks
    {
        // Start of HMMHub.Awake and StateMachine.Awake (whichever runs first rewires the scene for the server).
        public static void BeforeAwake(object self)
        {
            try
            {
                Entry.Init();
                if (Entry.ServerMode) ServerBootstrap.TryRewire(self);
            }
            catch (Exception e) { Log.Error("rewire failed: " + e); }
        }

        // Start of HMMHub.Start: the hub is initialized.
        public static void HubStart(HMMHub hub)
        {
            try
            {
                Entry.Init();
                if (Entry.ServerMode) ServerBootstrap.ActivateDeferred(hub);
                if (hub.GetComponent<Watch>() == null) hub.gameObject.AddComponent<Watch>();
                Entry.HideOwnWindows();
            }
            catch (Exception e) { Log.Error("HubStart failed: " + e); }
        }

        // Start of Pocketverse.GameState.EnableState: unbuffered state trace, plus what each role does on a state.
        public static void StateEnabled(GameState state)
        {
            try
            {
                Log.Info("STATE +" + state.name + " (" + state.GetType().Name + ")");
                ApplyScoreTarget();
                if (Entry.ServerMode)
                {
                    ServerBootstrap.OnState(state);
                    Balance.Apply(state.name);
                }
                else Client.OnState(state);
                MatchEnd.OnState(state);
            }
            catch (Exception e) { Log.Error("StateEnabled failed: " + e); }
        }

        private static void ApplyScoreTarget()
        {
            if (Entry.ScoreTarget <= 0) return;
            var bombManager = GameHubBehaviour<HMMHub>.Hub != null ? GameHubBehaviour<HMMHub>.Hub.BombManager : null;
            if (bombManager == null || bombManager.Rules == null || bombManager.Rules.BombScoreTarget == Entry.ScoreTarget) return;
            bombManager.Rules.BombScoreTarget = Entry.ScoreTarget;
            Log.Info("BombScoreTarget = " + Entry.ScoreTarget);
        }

        // Replaces FMOD_StudioSystem.Init's call to setAdvancedSettings, which comes before system.initialize: the server
        // and --hmmrevive-mute clients get no audio device.
        public static FMOD.RESULT SetAdvancedSettings(FMOD.System sys, ref FMOD.ADVANCEDSETTINGS settings)
        {
            Entry.Init();
            if (Entry.Mute) Log.Info("FMOD muted: " + sys.setOutput(FMOD.OUTPUTTYPE.NOSOUND));
            return sys.setAdvancedSettings(ref settings);
        }

        // Start of HMMHub.Quit.
        public static void LogQuit()
        {
            Log.Info("QUIT");
        }

        // ---- team choice (the launcher's lobby) ----
        private const char Separator = '#';

        // Client, NetworkClient.SendAuthenticateMessage: the name that is sent is LoginName(name). "name" or "name#team".
        public static string LoginName(string name)
        {
            Entry.Init();
            return Entry.Team == null || name == null ? name : name + Separator + Entry.Team;
        }

        // Server, start of AuthenticationManager.FakeRequest(username, ...): username = TakeTeam(this, username).
        // FakeRequest puts the player on Red when _nextPlayerOnRedTeam is set (unless the config puts everyone on Blue) and
        // then flips it; a player who asked for a team gets it. A returning player is found by the bare name.
        public static string TakeTeam(object authManager, string login)
        {
            int i = login != null ? login.IndexOf(Separator) : -1;
            if (i < 0) return login;
            string name = login.Substring(0, i), team = login.Substring(i + 1).ToLowerInvariant();
            if (team == "red" || team == "blue")
            {
                FieldInfo next = authManager.GetType().GetField("_nextPlayerOnRedTeam", BindingFlags.Instance | BindingFlags.NonPublic);
                if (next != null) next.SetValue(authManager, team == "red");
                else Log.Error("AuthenticationManager._nextPlayerOnRedTeam not found");
            }
            Log.Info("player " + name + " wants team '" + team + "'");
            return name;
        }

#if !Y2016
        // ---- bot difficulty per team: start of MatchPlayers.GetBotDifficulty(team) ----
        // if (HasBotDifficulty(team)) return BotDifficulty(team);  The game picks it from the other team's MMR, which is
        // a fixed config value without Swordfish.
        public static bool HasBotDifficulty(TeamKind team)
        {
            var d = BotDifficulty(team);
            Log.Info("bot difficulty " + team + ": " + (d == HeavyMetalMachines.BotAI.BotAIGoal.BotDifficulty.Invalid ? "auto" : d.ToString()));
            return d != HeavyMetalMachines.BotAI.BotAIGoal.BotDifficulty.Invalid;
        }

        public static HeavyMetalMachines.BotAI.BotAIGoal.BotDifficulty BotDifficulty(TeamKind team)
        {
            return team == TeamKind.Red ? Entry.RedBotDifficulty
                : team == TeamKind.Blue ? Entry.BluBotDifficulty
                : HeavyMetalMachines.BotAI.BotAIGoal.BotDifficulty.Invalid;
        }
#endif
    }

    /// <summary>
    /// Client side for the launcher: join the match server from the main menu by itself (--hmmrevive-connect). Under
    /// SkipSwordfish the menu's Play goes straight to [Server] IP/Port; players then use the game's own pick screen.
    /// </summary>
    public static class Client
    {
        private static bool _connecting, _played;
        private static int _attempts;

        public static void OnState(GameState state)
        {
            if (state.StateKind == GameState.GameStateKind.Pick || state.StateKind == GameState.GameStateKind.Game) _played = true;
            var menu = state as MainMenu;
            if (menu == null || !Entry.Connect) return;
            if (_played)
            {
                // Back in the menu after a match (or after losing the server): the launcher's lobby takes over.
                MatchEnd.QuitSoon("client back in the main menu after the match");
                return;
            }
            if (!_connecting) GameHubBehaviour<HMMHub>.Hub.StartCoroutine(Connect(menu));
        }

        private static IEnumerator Connect(MainMenu menu)
        {
            _connecting = true;
            while (!_played && _attempts < 6)
            {
                yield return new WaitForSeconds(_attempts == 0 ? 3f : 5f);
                if (_played || GameHubBehaviour<HMMHub>.Hub.State.Current != menu) break;
                _attempts++;
                Log.Info("direct connect, attempt " + _attempts);
#if Y2016
                menu.SearchForAMatch("CoopVsBots");
#else
                menu.SearchForAMatch("CoopVsBots", () => Log.Info("direct connect failed"));
#endif
                yield return new WaitForSeconds(10f);
            }
            _connecting = false;
        }
    }

    /// <summary>
    /// After the match. Server: quit EndQuitDelay seconds after ServerFinish (Hoplon's only stops the clock and idles; their
    /// infrastructure killed the process), so the host's launcher can offer a rematch. Client (launcher-started): quit
    /// once the match is over and the server has gone, instead of sitting on a dead results screen.
    /// </summary>
    public static class MatchEnd
    {
        private static float _quitAt = -1f;

        public static void OnState(GameState state)
        {
            if (!Entry.ServerMode || !(state is ServerFinish) || Entry.EndQuitDelay <= 0f) return;
            _quitAt = Time.realtimeSinceStartup + Entry.EndQuitDelay;
            Log.Info("MATCH END: server quits in " + Entry.EndQuitDelay + "s");
        }

        public static void QuitSoon(string why)
        {
            if (_quitAt >= 0f) return;
            _quitAt = Time.realtimeSinceStartup + 2f;
            Log.Info("MATCH END: " + why + ", quitting in 2s");
        }

        public static bool MatchOver(MatchData.MatchState s)
        {
            return s == MatchData.MatchState.MatchOverRedWins || s == MatchData.MatchState.MatchOverBluWins || s == MatchData.MatchState.MatchOverTie;
        }

        // Called every frame by Watch.
        public static void Tick(HMMHub hub)
        {
            if (_quitAt >= 0f && Time.realtimeSinceStartup >= _quitAt)
            {
                _quitAt = float.MaxValue;
                Log.Info("MATCH END: quitting");
                Application.Quit();
                return;
            }
            if (Entry.ServerMode || !Entry.Connect || _quitAt >= 0f || hub.Match == null || !MatchOver(hub.Match.State)) return;
            var nc = hub.Net as NetworkClient;
            if (nc != null && nc.State != SessionState.Established) QuitSoon("server closed after the match");
        }
    }
}
