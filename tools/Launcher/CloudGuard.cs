using System;

namespace HmmRevive.Launcher
{
    public enum GuardAction { Continue, StopGameServer, StopLobby }

    /// <summary>
    /// Decides when a cloud lobby's container ends, from facts passed in (no clock, no I/O, so it is tested to the second).
    /// Every cloud minute costs money, so the lobby ends itself: when empty, when nobody ever came, and at a hard deadline.
    /// A match that nobody plays only stops the game server, because the lobby may still have people in it.
    /// </summary>
    public sealed class CloudGuard
    {
        public static readonly TimeSpan EmptyAfterMembers = TimeSpan.FromSeconds(180);
        public static readonly TimeSpan NeverJoined = TimeSpan.FromMinutes(10);
        public static readonly TimeSpan MatchWithoutClients = TimeSpan.FromMinutes(10);
        public static readonly TimeSpan Deadline = TimeSpan.FromMinutes(180);

        // The limits of this guard: the ones above, or shorter ones for container tests (GUARD_* variables).
        private readonly TimeSpan _empty, _never, _match;

        private readonly DateTime _start;
        private bool _hadMembers;
        private DateTime? _emptySince, _noClientsSince;

        private readonly DateTime? _deadline;

        /// <param name="deadline">the orchestrator's absolute deadline, when it passed one; the 180 minute limit applies anyway</param>
        public CloudGuard(DateTime start, DateTime? deadline = null, TimeSpan? empty = null, TimeSpan? neverJoined = null, TimeSpan? matchWithoutClients = null)
        {
            _start = start;
            _deadline = deadline;
            _empty = empty ?? EmptyAfterMembers;
            _never = neverJoined ?? NeverJoined;
            _match = matchWithoutClients ?? MatchWithoutClients;
        }

        /// <param name="members">people connected to the lobby right now (the host included)</param>
        /// <param name="phase">the lobby phase: "lobby", "draft", "starting" or "playing"</param>
        /// <param name="gameClients">game clients connected to the match server</param>
        public GuardAction Check(DateTime now, int members, string phase, int gameClients)
        {
            if (now - _start >= Deadline || (_deadline != null && now >= _deadline.Value)) return GuardAction.StopLobby; // whatever is going on, even a match

            if (members > 0) { _hadMembers = true; _emptySince = null; }
            else if (_hadMembers)
            {
                if (_emptySince == null) _emptySince = now;
                if (now - _emptySince.Value >= _empty) return GuardAction.StopLobby;
            }
            else if (now - _start >= _never) return GuardAction.StopLobby;

            bool match = phase == "starting" || phase == "playing";
            if (match && gameClients == 0)
            {
                if (_noClientsSince == null) _noClientsSince = now;
                if (now - _noClientsSince.Value >= _match)
                {
                    _noClientsSince = null; // the lobby returns to "lobby" once the server is gone
                    return GuardAction.StopGameServer;
                }
            }
            else _noClientsSince = null;
            return GuardAction.Continue;
        }
    }
}
