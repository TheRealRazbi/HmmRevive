using System;
using System.Collections.Generic;
using System.Threading;

namespace HmmRevive.Launcher
{
    /// <summary>
    /// The host's side of a cloud lobby inside the launcher: ask the orchestrator for one, wait until it is ready, take the host seat,
    /// route the host's buttons to the lobby over HTTPS, and delete the lobby when the host closes it. The lobby id and host token are
    /// saved so a restarted launcher can take the seat back (the lobby keeps running without its host, it does not move to anyone else).
    /// </summary>
    public sealed class CloudHosting
    {
        public const string SavedKey = "cloudLobby";
        public const int MaxKeyLength = 128;

        /// <summary>Where a lobby's server can run: the orchestrator's region names and what the page shows.</summary>
        public static readonly Dictionary<string, string> Regions = new Dictionary<string, string>
        {
            ["brazilsouth"] = "Brazil", ["eastus"] = "United States", ["spaincentral"] = "Europe (Madrid)",
        };
        public const string DefaultRegion = "brazilsouth";
        private static readonly TimeSpan GiveUpAfter = TimeSpan.FromMinutes(10); // the orchestrator fails a lobby at 8 minutes

        private readonly Func<Cloud> _cloud;
        private readonly Func<string, string, RemoteHost> _remoteFor;      // lobby base URL, host token
        private readonly Func<string, string, Session> _joinAsHost;         // hostname, host token
        private readonly Action<Session> _onSeat;
        private readonly int _pollMs;
        private readonly object _gate = new object();

        private string _state = "idle", _message = "", _id, _hostname, _token;
        private string _step = "contact";                 // see CloudProgress.Steps
        private DateTime _started = DateTime.UtcNow;      // when this creation began (re-synced from the orchestrator's clock)
        private RemoteHost _remote;
        private int _generation;
        // The host's match settings as the page shows them. The cloud lobby starts from the settings the player used last
        // (the same file a local lobby reads) and every configure the host sends is kept here too.
        private readonly Lobby _setup = new Lobby(0, 0, Builds.Steam);

        public CloudHosting(Func<Cloud> cloud, Func<string, string, RemoteHost> remoteFor, Func<string, string, Session> joinAsHost, Action<Session> onSeat, int pollMs = 3000)
        {
            _cloud = cloud;
            _remoteFor = remoteFor;
            _joinAsHost = joinAsHost;
            _onSeat = onSeat;
            _pollMs = pollMs;
        }

        /// <summary>True while a cloud lobby is the one the host buttons control (creating or ready).</summary>
        public bool Active { get { lock (_gate) return _state == "creating" || _state == "ready"; } }

        /// <summary>"idle", "creating", "ready" or "failed", with the player's message and the lobby's id and hostname.</summary>
        public Dictionary<string, object> State()
        {
            lock (_gate)
            {
                var d = new Dictionary<string, object> { ["state"] = _state, ["message"] = _message, ["id"] = _id, ["hostname"] = _hostname };
                if (_state == "creating") d["progress"] = CloudProgress.Compute(_step, (DateTime.UtcNow - _started).TotalSeconds);
                return d;
            }
        }

        /// <summary>The match settings of the cloud lobby, in the shape the host page edits.</summary>
        public Dictionary<string, object> HostSetup() { lock (_gate) return _setup.SetupJson(); }

        /// <summary>The lobby saved by the last launcher run, if any (id and hostname, never the token).</summary>
        public static Dictionary<string, object> Saved()
        {
            var d = Settings.Data.Obj(SavedKey);
            return d == null || string.IsNullOrEmpty(d.Str("id")) ? null : new Dictionary<string, object> { ["id"] = d.Str("id"), ["hostname"] = d.Str("hostname") };
        }

        /// <summary>The invite key is kept only when it is a non-empty string of at most 128 characters without control characters.</summary>
        public static string SaveKey(string key)
        {
            key = key?.Trim();
            if (string.IsNullOrEmpty(key) || key.Length > MaxKeyLength || key.IndexOfAny(new[] { '\r', '\n', '\t', '\0' }) >= 0)
                return "The invite key must be 1 to 128 characters.";
            Settings.Set("cloudKey", key);
            return null;
        }

        /// <summary>Asks for a lobby and, in the background, follows it until it is ready. Returns an error for the page, or null.</summary>
        public string Create(string version, string visibility, string password, string label, string region = null)
        {
            region = string.IsNullOrWhiteSpace(region) ? Settings.Data.Str("cloudRegion", DefaultRegion) : region.Trim().ToLowerInvariant();
            if (!Regions.ContainsKey(region)) return Fail("Pick a region from the list.");
            Dictionary<string, object> made;
            lock (_gate)
            {
                if (Active) return "You already have a cloud lobby open.";
                _state = "creating";
                _message = "Asking the cloud for a server...";
                _id = _hostname = _token = null;
                _step = "contact";
                _started = DateTime.UtcNow;
            }
            try { made = _cloud().Create(version, visibility, password, label, region); }
            catch (CloudException e) { return Fail(e.Message); }
            lock (_gate)
            {
                _id = made.Str("id");
                _hostname = made.Str("hostname");
                _token = made.Str("hostToken");
                _message = "Starting your server...";
                _step = "reserve";
            }
            Settings.Set("cloudRegion", region); // the next lobby starts from the same choice
            Settings.Set(SavedKey, new Dictionary<string, object> { ["id"] = _id, ["hostname"] = _hostname, ["hostToken"] = _token });
            Follow();
            return null;
        }

        /// <summary>Takes the host seat of the lobby saved by the last run. Returns an error for the page, or null.</summary>
        public string Rejoin()
        {
            var saved = Settings.Data.Obj(SavedKey);
            if (saved == null || string.IsNullOrEmpty(saved.Str("id"))) return "There is no cloud lobby to rejoin.";
            lock (_gate)
            {
                if (Active) return "You already have a cloud lobby open.";
                _state = "creating";
                _step = "contact";
                _started = DateTime.UtcNow;
                _message = "Reconnecting to your cloud lobby...";
                _id = saved.Str("id");
                _hostname = saved.Str("hostname");
                _token = saved.Str("hostToken");
            }
            try
            {
                var lobby = _cloud().Get(_id);
                string state = lobby.Str("state");
                if (state != "ready" && state != "creating") { Forget(); return Fail("That cloud lobby has ended."); }
            }
            catch (CloudException e)
            {
                if (e.Status == 404) { Forget(); return Fail("That cloud lobby has ended."); }
                return Fail(e.Message);
            }
            Follow();
            return null;
        }

        /// <summary>The host's button for <paramref name="route"/> (configure, start, ...); null on success or the lobby's message.</summary>
        public string HostAction(string route, Dictionary<string, object> d)
        {
            RemoteHost remote;
            lock (_gate)
            {
                if (_state != "ready" || _remote == null) return "Your cloud lobby is not ready yet.";
                remote = _remote;
            }
            try
            {
                switch (route)
                {
                    case "configure": remote.Configure(d); lock (_gate) _setup.Configure(d); break;
                    case "start": remote.Start(); break;
                    case "stop": remote.Stop(); break;
                    case "kick": remote.Kick(d.Str("id")); break;
                    case "move": remote.Move(d.Str("id"), d.Str("team")); break;
                    case "swap": remote.Swap(d.Str("a"), d.Str("b")); break;
                    case "draft-reset": remote.DraftReset(); break;
                    case "visibility": remote.SetVisibility(d.Str("visibility")); break;
                    default: return "Unknown host action.";
                }
                return null;
            }
            catch (CloudException e) { return e.Message; }
        }

        /// <summary>Ends the lobby: the orchestrator deletes the cloud server (which stops the cost), and the saved lobby is forgotten.</summary>
        public string Close()
        {
            string id;
            lock (_gate)
            {
                id = _id;
                _generation++; // stops a poller still waiting for ready
                _state = "idle";
                _message = "";
                _remote = null;
            }
            Forget();
            if (id == null) return null;
            try { _cloud().Delete(id); return null; }
            catch (CloudException e) { return e.Message; }
        }

        private string Fail(string message)
        {
            lock (_gate) { _state = "failed"; _message = message; _remote = null; }
            return message;
        }

        private static void Forget()
        {
            Settings.Data.Remove(SavedKey);
            Settings.Save();
        }

        // Polls the orchestrator until the lobby is ready, then takes the host seat.
        private void Follow()
        {
            int generation;
            lock (_gate) generation = ++_generation;
            new Thread(() =>
            {
                DateTime until = DateTime.UtcNow + GiveUpAfter;
                while (DateTime.UtcNow < until)
                {
                    lock (_gate) if (generation != _generation) return;
                    try
                    {
                        var lobby = _cloud().Get(_id);
                        string state = lobby.Str("state");
                        if (state == "ready") { TakeSeat(generation); return; }
                        if (state == "failed" || state == "closing" || state == "gone")
                        {
                            Forget();
                            lock (_gate) if (generation == _generation) { _state = "failed"; _message = lobby.Str("message") ?? "The cloud server could not start."; }
                            return;
                        }
                        lock (_gate)
                            if (generation == _generation)
                            {
                                if (!string.IsNullOrEmpty(lobby.Str("message"))) _message = lobby.Str("message");
                                _step = CloudProgress.FromOrchestrator(lobby.Str("step"));
                                // The orchestrator's clock is the truth (a rejoined lobby may be minutes old).
                                if (lobby.ContainsKey("elapsedSeconds")) _started = DateTime.UtcNow.AddSeconds(-lobby.Int("elapsedSeconds"));
                            }
                    }
                    catch (CloudException e)
                    {
                        if (e.Status == 404) { Forget(); lock (_gate) if (generation == _generation) { _state = "failed"; _message = "The cloud lobby is gone."; } return; }
                        // the cloud is briefly unavailable: keep waiting
                    }
                    Thread.Sleep(_pollMs);
                }
                lock (_gate) if (generation == _generation) { _state = "failed"; _message = "The cloud server took too long to start."; }
            }) { IsBackground = true, Name = "cloud-wait" }.Start();
        }

        private void TakeSeat(int generation)
        {
            string hostname, token;
            lock (_gate) { hostname = _hostname; token = _token; }
            Session seat = _joinAsHost(hostname, token);
            lock (_gate)
            {
                if (generation != _generation) { seat?.Leave(); return; }
                if (seat == null) { _state = "failed"; _message = "Your cloud lobby is ready but you could not take the host seat."; return; }
                _remote = _remoteFor("https://" + hostname, token);
                _state = "ready";
                _message = "";
            }
            // Make the lobby match the settings the page shows (the player's last ones).
            try { _remote.Configure(HostSetup()); } catch (CloudException e) { Program.Log("could not send the host settings to the cloud lobby: " + e.Message); }
            _onSeat(seat);
        }
    }
}
