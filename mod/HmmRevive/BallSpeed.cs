using System;
using System.Globalization;
using System.IO;
using HeavyMetalMachines;
using HeavyMetalMachines.Combat;
using HeavyMetalMachines.HMMChat;
using HeavyMetalMachines.Match;
using Pocketverse;
using UnityEngine;

namespace HmmRevive
{
    /// <summary>
    /// Server: ball (bomb) speed multiplier k, asked for by players (2026-10-05: one number that makes the ball both
    /// fly faster and slow down less). The ball only moves on the server (clients destroy its rigidbody and follow the
    /// server's positions), so nothing changes on the players' side. Three hooks, all on the ball only:
    /// - release: when the last link to the ball goes (spin release, drop, carrier killed), its velocity is multiplied by k;
    /// - gadget impulses (EffectKind.Impulse) on a free ball are multiplied by k;
    /// - drag: the drag curve's value is divided by k, so it keeps its speed longer.
    /// Set with --hmmrevive-ball-speed=K (0.5-3, 1 = the game's own). Mid-match: the lobby host types /ballspeed K in chat
    /// (--hmmrevive-host=NAME names them), or the launcher writes "ballspeed=K" into the --hmmrevive-live=FILE file.
    /// Test flag --hmmrevive-ball-trace logs every release and impulse with the ball's speed.
    /// </summary>
    public static class BallSpeed
    {
        public const float Min = 0.5f, Max = 3f;
        public static float Factor { get; private set; } = 1f;
        public static string Host { get; private set; }
        private static string _liveFile;
        private static DateTime _liveStamp;
        private static bool _trace, _wasLinked, _announced;
        private static Rigidbody _body;
        private static BombMovement _bodyOf;
        private static float _traceUntil, _tracePeak;

        public static void Init(string[] args)
        {
            string k = Arg(args, "--hmmrevive-ball-speed=");
            if (k != null && TryParse(k, out float f)) Factor = f;
            Host = Arg(args, "--hmmrevive-host=");
            _liveFile = Arg(args, "--hmmrevive-live=");
            _trace = Array.Exists(args, a => a.Equals("--hmmrevive-ball-trace", StringComparison.OrdinalIgnoreCase));
        }

        public static string Describe() => $"{Factor.ToString("0.##", CultureInfo.InvariantCulture)}x host={Host ?? "-"} live={_liveFile ?? "-"}";

        private static string Arg(string[] args, string prefix)
        {
            string a = Array.Find(args, x => x.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
            string v = a?.Substring(prefix.Length).Trim('"', '\'', ' ');
            return string.IsNullOrEmpty(v) ? null : v;
        }

        /// <summary>"1.5", "1,5" or "1.5x", 0.5-3.</summary>
        public static bool TryParse(string s, out float k)
        {
            bool ok = float.TryParse(s.Trim().TrimEnd('x', 'X').Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out k);
            if (!ok || float.IsNaN(k) || k < Min || k > Max) { k = 1f; return false; }
            k = Mathf.Round(k * 100f) / 100f;
            return true;
        }

        private static bool Free(CombatMovement m)
        {
            foreach (ICombatLink l in m.Links) if (l.IsEnabled) return false;
            return true;
        }

        // ---------------------------------------------------------------- hooks (server)

        /// <summary>CombatMovement.ApplyDrag, after GetDrag: the ball's drag / k.</summary>
        public static float Drag(float drag, CombatMovement m) => Factor != 1f && m is BombMovement ? drag / Factor : drag;

        /// <summary>Prologue of BombMovement.MovementFixedUpdate: the link to the ball just went away = a release.</summary>
        public static void BombFixedUpdate(BombMovement m)
        {
            if (!Entry.ServerMode) return;
            if (_bodyOf != m) { _bodyOf = m; _body = m.GetComponent<Rigidbody>(); _wasLinked = false; }
            if (_body == null) return;
            bool linked = !Free(m);
            if (_wasLinked && !linked)
            {
                Vector3 v = _body.velocity;
                if (Factor != 1f) _body.velocity = v * Factor;
                if (_trace) { Log.Info($"BALL release speed {v.magnitude:F1} -> {_body.velocity.magnitude:F1} (k={Factor})"); _traceUntil = Time.time + 1.5f; _tracePeak = 0f; }
            }
            _wasLinked = linked;
            if (_trace && _traceUntil > 0f)
            {
                _tracePeak = Mathf.Max(_tracePeak, _body.velocity.magnitude);
                if (Time.time >= _traceUntil) { Log.Info($"BALL after release: peak {_tracePeak:F1}, now {_body.velocity.magnitude:F1}, linked={linked}"); _traceUntil = 0f; }
            }
        }

        /// <summary>Replaces CombatController's Movement.Push for EffectKind.Impulse: a free ball gets k times the push.</summary>
        public static void Push(CombatMovement m, Vector3 direction, bool pct, float magnitude, bool ignorePushReceived)
        {
            // A spin release also pushes the ball in the frame its link breaks; BombFixedUpdate then still remembers it linked
            // and multiplies the whole release velocity next step, so those pushes must not be multiplied twice.
            if (m is BombMovement && Free(m) && !(_bodyOf == m && _wasLinked))
            {
                if (_trace) Log.Info($"BALL impulse {direction.magnitude:F1} pct={pct} (k={Factor})");
                direction *= Factor;
            }
            m.Push(direction, pct, magnitude, ignorePushReceived);
        }

        // ---------------------------------------------------------------- changes during the match

        // Once a second from Watchdog.Update.
        public static void Tick(HMMHub hub)
        {
            if (!Entry.ServerMode || hub == null) return;
            if (_liveFile != null)
            {
                try
                {
                    DateTime stamp = File.Exists(_liveFile) ? File.GetLastWriteTimeUtc(_liveFile) : DateTime.MinValue;
                    if (stamp != _liveStamp)
                    {
                        _liveStamp = stamp;
                        foreach (string line in stamp == DateTime.MinValue ? new string[0] : File.ReadAllLines(_liveFile))
                            if (line.StartsWith("ballspeed=", StringComparison.OrdinalIgnoreCase) && TryParse(line.Substring(10), out float k) && k != Factor)
                                Set(k, "the host");
                    }
                }
                catch (Exception e) { Log.Error("live settings: " + e.Message); }
            }
            // Tell everyone once the match is on, when the host changed it from the game's own.
            if (!_announced && Factor != 1f && hub.Match != null && hub.Match.State == MatchData.MatchState.MatchStarted)
            {
                _announced = true;
                SayAll($"Ball speed in this match: [ffcc00]{Show(Factor)}[-]");
            }
        }

        private static string Show(float k) => k.ToString("0.##", CultureInfo.InvariantCulture) + "x";

        private static void Set(float k, string who)
        {
            Log.Info($"BALLSPEED {Show(Factor)} -> {Show(k)} by {who}");
            Factor = k;
            _announced = true;
            SayAll($"Ball speed is now [ffcc00]{Show(k)}[-] (changed by {who})");
        }

        private static void SayAll(string text)
        {
            try { CarSwap.SayAll(GameHubBehaviour.Hub.Chat, text); }
            catch (Exception e) { Log.Error("ball speed message: " + e.Message); }
        }

        /// <summary>/ballspeed [K] in match chat. Only the lobby host may change it.</summary>
        public static void Chat(ChatService chat, PlayerData player, string arg)
        {
            if (arg.Length == 0)
            {
                CarSwap.Say(chat, player, $"Ball speed: [ffcc00]{Show(Factor)}[-]." + (IsHost(player) ? $" Type /ballspeed NUMBER ({Show(Min)} to {Show(Max)}, 1 = normal) to change it." : ""));
                return;
            }
            if (!IsHost(player)) { CarSwap.Say(chat, player, "Only the lobby host can change the ball speed."); return; }
            if (!TryParse(arg, out float k)) { CarSwap.Say(chat, player, $"Use a number from {Min} to {Max}, like /ballspeed 1.5 (1 = normal)."); return; }
            Set(k, player.Name);
        }

        private static bool IsHost(PlayerData player) => Host != null && player != null && !player.IsBot && string.Equals(player.Name, Host, StringComparison.OrdinalIgnoreCase);
    }
}
