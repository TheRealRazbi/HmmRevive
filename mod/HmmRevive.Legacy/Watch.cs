using System;
using System.Reflection;
using HeavyMetalMachines;
using HeavyMetalMachines.Match;
using HeavyMetalMachines.Server;
using Pocketverse;
using UnityEngine;

namespace HmmRevive.Legacy
{
    /// <summary>
    /// A WATCH status line every 5 s (the game's own log is buffered and lost on kill), the match-end timer, and the
    /// server's bots-only start: the server only tells clients to start once every human reported "loaded"
    /// (ServerInfo.OnServerPlayerReady), so a match without humans never starts on its own.
    /// </summary>
    public class Watch : MonoBehaviour
    {
        private float _next, _gameSince = -1f;
        private bool _kicked;

        private void Update()
        {
            HMMHub hub = GameHubBehaviour<HMMHub>.Hub;
            if (hub == null) return;
            MatchEnd.Tick(hub);
            Ball.Tick(hub);
            GameState cur = hub.State != null ? hub.State.Current : null;
            if (cur is ServerGame) { if (_gameSince < 0f) _gameSince = Time.realtimeSinceStartup; }
            else _gameSince = -1f;
            if (Entry.ServerMode) BotsOnlyStart(hub, cur);
            else Shots.Tick();
            if (Time.realtimeSinceStartup < _next) return;
            _next = Time.realtimeSinceStartup + 5f;
            try { Log.Info(Line(hub, cur)); }
            catch (Exception e) { Log.Info("WATCH failed: " + e.Message); }
        }

        private void BotsOnlyStart(HMMHub hub, GameState cur)
        {
            if (_kicked || !(cur is ServerGame) || hub.MatchMan == null || Time.realtimeSinceStartup - _gameSince < 5f) return;
            if (hub.Match.State != MatchData.MatchState.PreMatch || Humans(hub) > 0) return;
            _kicked = true;
            try
            {
                typeof(ServerInfo).GetMethod("SendServerSetEventToAllClients", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(hub.Server, null);
                Log.Info("no players: sent the start signal (ServerSet) myself");
            }
            catch (Exception e) { Log.Error("bots-only start failed: " + e); }
        }

        private static int Humans(HMMHub hub)
        {
#if Y2016
            return hub.Match.Players.Count;
#else
            return hub.Players.Players.Count;
#endif
        }

        private static string Line(HMMHub hub, GameState cur)
        {
            string line = "WATCH state=" + (cur ? cur.name : "-") + " match=" + (hub.Match != null ? hub.Match.State.ToString() : "-");
            if (!Entry.ServerMode)
            {
                var nc = hub.Net as NetworkClient;
                return line + " net=" + (nc != null ? nc.State.ToString() : "-");
            }
            if (hub.BombManager != null && hub.BombManager.ScoreBoard != null)
                line += " score=" + hub.BombManager.ScoreBoard.BombScoreRed + "-" + hub.BombManager.ScoreBoard.BombScoreBlue;
            var pick = cur as PickModeServerSetup;
            if (pick != null)
            {
                var c = hub.Characters;
                line += " stage=" + pick.Stage + " pickTime=" + c.PickTime.ToString("0.0") + " allConfirmed=" + c.AllConfirmed;
#if !Y2016 && !Y2017SEP
                line += " botPicking=" + c.IsBotPicking;
#endif
            }
#if Y2016
            foreach (var p in hub.Match.PlayersAndBots)
                line += " | " + p.Name + (p.IsBot ? "(bot)" : "") + " " + p.Team + " car=" + p.CharacterId + " grid=" + p.GridIndex;
#else
            foreach (var p in hub.Players.PlayersAndBots)
#if Y2017SEP
                line += " | " + p.Name + (p.IsBot ? "(bot)" : "") + " " + p.Team + " car=" + p.CharacterId + " grid=" + p.GridIndex
#else
                line += " | " + p.Name + (p.IsBot ? "(bot)" : "") + " " + p.Team + " car=" + p.PickedCharId + " grid=" + p.GridIndex
#endif
                        + (p.IsBot ? "" : p.Connected ? "" : " (disconnected)");
#endif
            return line;
        }
    }
}
