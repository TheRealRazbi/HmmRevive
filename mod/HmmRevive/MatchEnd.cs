using HeavyMetalMachines;
using HeavyMetalMachines.Match;
using Pocketverse;
using UnityEngine;

namespace HmmRevive
{
    /// <summary>
    /// What happens after a match ends, so the launcher can start the next one.
    /// Server: quit a while after the match ends. Hoplon's ServerFinish only stops the timer and idles forever (their
    /// infrastructure killed the process), so without this a finished server keeps its port and ~2 GB of memory. The delay
    /// (--hmmrevive-end-quit=SECONDS, default 30, 0 = stay up) leaves players time for the results screen.
    /// Client: once the match is over and the server has gone, quit instead of sitting on a dead results screen; the
    /// launcher sees the game exit and shows the lobby again.
    /// </summary>
    public class MatchEnd : MonoBehaviour
    {
        private static float _quitAt = -1f;
        private float _nextCheck;

        public static void Attach(HMMHub hub)
        {
            if (hub.GetComponent<MatchEnd>() == null) hub.gameObject.AddComponent<MatchEnd>();
        }

        // Hooks.StateEnabled
        public static void OnState(object state)
        {
            if (!Entry.ServerMode || !(state is HeavyMetalMachines.Server.ServerFinish) || Entry.EndQuitDelay <= 0f) return;
            _quitAt = Time.realtimeSinceStartup + Entry.EndQuitDelay;
            Log.Info($"MATCH END: server quits in {Entry.EndQuitDelay:0}s");
        }

        private static bool MatchOver(MatchData.MatchState s) =>
            s == MatchData.MatchState.MatchOverRedWins || s == MatchData.MatchState.MatchOverBluWins || s == MatchData.MatchState.MatchOverTie;

        private void Update()
        {
            if (_quitAt >= 0f && Time.realtimeSinceStartup >= _quitAt)
            {
                _quitAt = -1f;
                Log.Info("MATCH END: quitting");
                Application.Quit();
                return;
            }
            if (Entry.ServerMode || Time.unscaledTime < _nextCheck) return;
            _nextCheck = Time.unscaledTime + 1f;
            HMMHub hub = GameHubBehaviour.Hub;
            if (_quitAt < 0f && hub != null && hub.Match != null && MatchOver(hub.Match.State)
                && hub.Net is NetworkClient nc && !nc.IsConnected())
            {
                _quitAt = Time.realtimeSinceStartup + 2f;
                Log.Info("MATCH END: server closed after the match, client quits in 2s");
            }
        }
    }
}
