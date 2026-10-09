using System;
using System.Globalization;
using System.IO;
using HeavyMetalMachines;
using HeavyMetalMachines.Combat;
using HeavyMetalMachines.HMMChat;
using HeavyMetalMachines.Match;
using Pocketverse;
using UnityEngine;

namespace HmmRevive.Legacy
{
    /// <summary>
    /// Server: ball (bomb) speed multiplier k, the same as the Steam mod's BallSpeed.cs. The ball only moves on the server.
    /// - release: when the last link to the ball goes (spin release, drop, carrier killed), its velocity is multiplied by k;
    /// - gadget impulses on a free ball are multiplied by k (not those in the frame of a release: the release covers them);
    /// - drag: the drag curve's value is divided by k, so it keeps its speed longer.
    /// --hmmrevive-ball-speed=K (0.5-3), --hmmrevive-host=NAME (may type /ballspeed K), --hmmrevive-live=FILE ("ballspeed=K",
    /// written by the launcher when the host changes it during the match), --hmmrevive-ball-trace (test log).
    /// Chat: the server catches /ballspeed (ChatService.ReceiveMessage) and answers with "@@hmmrevive:say:" messages that
    /// every player's mod shows as a system line (ChatService.ClientReceiveMessage).
    /// </summary>
    public static class Ball
    {
        public const float Min = 0.5f, Max = 3f;
        private const string Marker = "@@hmmrevive:";
        public static float Factor = 1f;
        public static string Host;
        private static string _liveFile;
        private static DateTime _liveStamp;
        private static bool _trace, _wasLinked, _announced;
        private static CombatMovement _ball;
        private static Rigidbody _body;
        private static float _nextTick;

        public static void Init(string[] args)
        {
            string k = Arg(args, "--hmmrevive-ball-speed=");
            float f;
            if (k != null && TryParse(k, out f)) Factor = f;
            Host = Arg(args, "--hmmrevive-host=");
            _liveFile = Arg(args, "--hmmrevive-live=");
            _trace = Array.Exists(args, a => a.Equals("--hmmrevive-ball-trace", StringComparison.OrdinalIgnoreCase));
        }

        public static string Describe() { return Show(Factor) + " host=" + (Host ?? "-"); }

        private static string Arg(string[] args, string prefix)
        {
            string a = Array.Find(args, x => x.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
            string v = a == null ? null : a.Substring(prefix.Length).Trim('"', '\'', ' ');
            return string.IsNullOrEmpty(v) ? null : v;
        }

        public static bool TryParse(string s, out float k)
        {
            k = 1f;
            try { k = float.Parse(s.Trim().TrimEnd('x', 'X').Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture); }
            catch { k = 1f; return false; }
            if (float.IsNaN(k) || k < Min || k > Max) { k = 1f; return false; }
            k = Mathf.Round(k * 100f) / 100f;
            return true;
        }

        private static string Show(float k) { return k.ToString("0.##", CultureInfo.InvariantCulture) + "x"; }

        private static bool Free(CombatMovement m)
        {
            foreach (CombatLink l in m.Links) if (!l.IsBroken) return false;
            return true;
        }

        // ---------------------------------------------------------------- hooks (server)

        /// <summary>CombatMovement.ApplyDrag, after GetDrag.</summary>
        public static float Drag(float drag, CombatMovement m) { return Factor != 1f && m is BombMovement ? drag / Factor : drag; }

        /// <summary>Prologue of CombatMovement.MovementFixedUpdate (the ball doesn't override it in this build).</summary>
        public static void FixedUpdate(CombatMovement m)
        {
            if (!Entry.ServerMode || !(m is BombMovement)) return;
            if (_ball != m) { _ball = m; _body = m.GetComponent<Rigidbody>(); _wasLinked = false; }
            if (_body == null) return;
            bool linked = !Free(m);
            if (_wasLinked && !linked)
            {
                Vector3 v = _body.velocity;
                if (Factor != 1f) _body.velocity = v * Factor;
                if (_trace) Log.Info("BALL release speed " + v.magnitude.ToString("F1") + " -> " + _body.velocity.magnitude.ToString("F1") + " (k=" + Factor + ")");
            }
            _wasLinked = linked;
        }

        /// <summary>Replaces CombatController's Movement.Push for impulse modifiers.</summary>
        public static void Push(CombatMovement m, Vector3 direction, bool pct, float magnitude, bool ignorePushReceived)
        {
            if (m is BombMovement && Free(m) && !(_ball == m && _wasLinked))
            {
                if (_trace) Log.Info("BALL impulse " + direction.magnitude.ToString("F1") + " pct=" + pct + " (k=" + Factor + ")");
                direction *= Factor;
            }
            m.Push(direction, pct, magnitude, ignorePushReceived);
        }

        // ---------------------------------------------------------------- changes during the match

        // Every frame from Watch.Update; works once a second.
        public static void Tick(HMMHub hub)
        {
            if (!Entry.ServerMode || hub == null || Time.realtimeSinceStartup < _nextTick) return;
            _nextTick = Time.realtimeSinceStartup + 1f;
            if (_liveFile != null)
            {
                try
                {
                    DateTime stamp = File.Exists(_liveFile) ? File.GetLastWriteTime(_liveFile) : DateTime.MinValue;
                    if (stamp != _liveStamp)
                    {
                        _liveStamp = stamp;
                        if (stamp != DateTime.MinValue)
                            foreach (string line in File.ReadAllLines(_liveFile))
                            {
                                float k;
                                if (line.StartsWith("ballspeed=", StringComparison.OrdinalIgnoreCase) && TryParse(line.Substring(10), out k) && k != Factor)
                                    Set(k, "the host");
                            }
                    }
                }
                catch (Exception e) { Log.Error("live settings: " + e.Message); }
            }
            if (!_announced && Factor != 1f && hub.Match != null && hub.Match.State == MatchData.MatchState.MatchStarted)
            {
                _announced = true;
                SayAll("Ball speed in this match: [ffcc00]" + Show(Factor) + "[-]");
            }
        }

        private static void Set(float k, string who)
        {
            Log.Info("BALLSPEED " + Show(Factor) + " -> " + Show(k) + " by " + who);
            Factor = k;
            _announced = true;
            SayAll("Ball speed is now [ffcc00]" + Show(k) + "[-] (changed by " + who + ")");
        }

        private static void SayAll(string text)
        {
            try { GameHubBehaviour<HMMHub>.Hub.Chat.DispatchReliable(GameHubBehaviour<HMMHub>.Hub.AddressGroups.GetGroup(0)).ClientReceiveMessage(false, Marker + "say:" + text, 0); }
            catch (Exception e) { Log.Error("ball speed message: " + e.Message); }
        }

        private static void Say(ChatService chat, PlayerData to, string text)
        {
            chat.DispatchReliable(to.PlayerAddress).ClientReceiveMessage(false, Marker + "say:" + text, 0);
        }

        /// <summary>Server, start of ChatService.ReceiveMessage(group, msg): true = handled, don't broadcast it.</summary>
        public static bool ServerChat(object chatService, string msg)
        {
            if (!Entry.ServerMode || msg == null) return false;
            string[] words = msg.Trim().Split(new[] { ' ' }, 2, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0 || words[0].ToLowerInvariant() != "/ballspeed") return false;
            try
            {
                var chat = (ChatService)chatService;
                PlayerData player = GameHubBehaviour<HMMHub>.Hub.Players.GetPlayerByAddress(chat.Sender);
                if (player == null) return true;
                string arg = words.Length > 1 ? words[1].Trim() : "";
                bool host = Host != null && !player.IsBot && string.Equals(player.Name, Host, StringComparison.OrdinalIgnoreCase);
                float k;
                if (arg.Length == 0)
                    Say(chat, player, "Ball speed: [ffcc00]" + Show(Factor) + "[-]." + (host ? " Type /ballspeed NUMBER (" + Show(Min) + " to " + Show(Max) + ", 1 = normal) to change it." : ""));
                else if (!host) Say(chat, player, "Only the lobby host can change the ball speed.");
                else if (!TryParse(arg, out k)) Say(chat, player, "Use a number from " + Min + " to " + Max + ", like /ballspeed 1.5 (1 = normal).");
                else Set(k, player.Name);
            }
            catch (Exception e) { Log.Error("ballspeed command failed: " + e); }
            return true;
        }

        /// <summary>Client, start of ChatService.ClientReceiveMessage(group, msg, address): true = ours, shown as a system line.</summary>
        public static bool ClientChat(object chatService, string msg)
        {
            if (msg == null || !msg.StartsWith(Marker, StringComparison.Ordinal)) return false;
            string body = msg.Substring(Marker.Length);
            if (body.StartsWith("say:", StringComparison.Ordinal))
            {
                Log.Info("chat> " + body.Substring(4));
                try { ((ChatService)chatService).ClientReceiveLogMessage(body.Substring(4)); }
                catch (Exception e) { Log.Error("chat line: " + e.Message); }
            }
            return true;
        }
    }
}
