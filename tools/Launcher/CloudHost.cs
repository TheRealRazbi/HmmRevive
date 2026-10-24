using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;

namespace HmmRevive.Launcher
{
    /// <summary>
    /// What a cloud lobby container is told through environment variables (set by the orchestrator when it creates the
    /// container group). Secrets arrive as secure variables and are never logged.
    /// </summary>
    public sealed class CloudHostConfig
    {
        public string LobbyId, HostToken, LobbySecret, OrchestratorUrl, Visibility = "public", Password, PublicHostname, Region;
        /// <summary>Where run-lobby.sh reports the game download (GAME_STATUS_FILE); null = the game is already in place.</summary>
        public string GameStatusFile;
        public int LobbyPort = 9697, GamePort = 9696;
        public DateTime? Deadline;
        public TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(30);
        /// <summary>Shorter guard limits for container tests (GUARD_EMPTY_SECONDS, GUARD_NEVER_JOINED_SECONDS, GUARD_MATCH_SECONDS); null = the real ones.</summary>
        public TimeSpan? GuardEmpty, GuardNeverJoined, GuardMatch;

        /// <summary>Reads the configuration; <paramref name="error"/> names what is missing or wrong.</summary>
        public static CloudHostConfig Parse(Func<string, string> env, out string error)
        {
            error = null;
            string Get(string k) { string v = env(k); return string.IsNullOrWhiteSpace(v) ? null : v.Trim(); }
            var c = new CloudHostConfig
            {
                LobbyId = Get("LOBBY_ID"), HostToken = Get("HOST_TOKEN"), LobbySecret = Get("LOBBY_SECRET"),
                OrchestratorUrl = Get("ORCH_URL"), Password = Get("LOBBY_PASSWORD"), PublicHostname = Get("PUBLIC_HOSTNAME"), Region = Get("REGION"),
                GameStatusFile = Get("GAME_STATUS_FILE"),
            };
            var missing = new[] { "LOBBY_ID", "HOST_TOKEN" }.Where(k => Get(k) == null).ToList();
            if (missing.Count > 0) { error = "Cloud host mode needs these environment variables: " + string.Join(", ", missing) + "."; return null; }
            string vis = Get("VISIBILITY");
            if (vis != null && vis != "public" && vis != "private") { error = "VISIBILITY must be public or private."; return null; }
            if (vis != null) c.Visibility = vis;
            if (Get("LOBBY_PORT") != null && !int.TryParse(Get("LOBBY_PORT"), out c.LobbyPort)) { error = "LOBBY_PORT must be a number."; return null; }
            if (Get("GAME_PORT") != null && !int.TryParse(Get("GAME_PORT"), out c.GamePort)) { error = "GAME_PORT must be a number."; return null; }
            foreach (var g in new[] { ("GUARD_EMPTY_SECONDS", 0), ("GUARD_NEVER_JOINED_SECONDS", 1), ("GUARD_MATCH_SECONDS", 2), ("HEARTBEAT_SECONDS", 3) })
            {
                if (Get(g.Item1) == null) continue;
                if (!int.TryParse(Get(g.Item1), out int secs) || secs < 1) { error = g.Item1 + " must be a whole number of seconds."; return null; }
                var span = TimeSpan.FromSeconds(secs);
                if (g.Item2 == 0) c.GuardEmpty = span; else if (g.Item2 == 1) c.GuardNeverJoined = span; else if (g.Item2 == 2) c.GuardMatch = span; else c.HeartbeatInterval = span;
            }
            if (Get("DEADLINE") != null)
            {
                if (!DateTime.TryParse(Get("DEADLINE"), System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out DateTime d))
                { error = "DEADLINE must be a date and time (RFC 3339)."; return null; }
                c.Deadline = d;
            }
            return c;
        }
    }

    /// <summary>
    /// <c>--cloud-host</c>: the launcher as the lobby service of a cloud container. No UI, no browser, no firewall rules, no
    /// discovery: only the lobby on localhost behind the TLS sidecar, with request limits, a heartbeat to the orchestrator and
    /// a guard that ends the process when the lobby is no longer worth its cost.
    /// </summary>
    public static class CloudHost
    {
        public static bool Requested(IEnumerable<string> args) => args.Contains("--cloud-host");

        /// <summary>Limits for a lobby that strangers can reach (spec SEC-08, SEC-09). Caddy appends the real client to X-Forwarded-For.</summary>
        public static HttpLimits Limits() => new HttpLimits { MaxBodyBytes = 64 * 1024, MaxConnections = 64, RequestsPerSecond = 20, TrustForwardedFor = true };

        /// <summary>Entry from <c>Main</c>: reads the environment, prepares the launcher's folders, runs until the lobby ends.</summary>
        public static int RunFromArgs(string[] args, Func<string, string> env)
        {
            var cfg = CloudHostConfig.Parse(env, out string error);
            if (cfg == null) { Console.Error.WriteLine(error); return 2; }
            string root = null, instance = "instance";
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--root" && i + 1 < args.Length) root = System.IO.Path.GetFullPath(args[++i]);
                else if (args[i] == "--instance" && i + 1 < args.Length) instance = args[++i];
            }
            Paths.Init(root, instance);
            Catalog.Load();
            Settings.Load();
            return Run(cfg);
        }

        /// <summary>Runs the lobby until the guard, the orchestrator or the host ends it; returns the process exit code (0).</summary>
        public static int Run(CloudHostConfig cfg, int tickMs = 5000)
        {
            var lobby = new Lobby(cfg.LobbyPort, cfg.GamePort, Builds.Steam) { Cloud = true, Region = cfg.Region, ServerGame = GameDownload.FromFile(cfg.GameStatusFile) };
            lobby.SetHostToken(cfg.HostToken);
            lobby.SetPassword(cfg.Password);
            lobby.SetVisibility(cfg.Visibility);
            if (!lobby.Open(IPAddress.Loopback, Limits(), discovery: false))
            {
                Console.Error.WriteLine($"Cannot listen on 127.0.0.1:{cfg.LobbyPort}.");
                return 1;
            }
            Program.Log($"cloud lobby {cfg.LobbyId} open on 127.0.0.1:{cfg.LobbyPort}");

            var stop = new ManualResetEventSlim();
            lobby.CloseRequested += () => stop.Set();
            HeartbeatFor(cfg, lobby, stop)?.Start();

            var guard = new CloudGuard(DateTime.UtcNow, cfg.Deadline, cfg.GuardEmpty, cfg.GuardNeverJoined, cfg.GuardMatch);
            while (!stop.Wait(tickMs))
            {
                GuardAction a = guard.Check(DateTime.UtcNow, lobby.ConnectedMembers(), lobby.Phase, Game.ServerClients(cfg.GamePort));
                if (a == GuardAction.StopGameServer) { Program.Log("no game client for 10 minutes: stopping the match server"); Game.StopServer(); }
                else if (a == GuardAction.StopLobby) { Program.Log("the cloud guard ends this lobby"); stop.Set(); }
            }
            lobby.Close(); // stops the game server too
            Program.Log("cloud lobby closed");
            return 0;
        }

        private static CloudHeartbeat HeartbeatFor(CloudHostConfig cfg, Lobby lobby, ManualResetEventSlim stop)
        {
            if (cfg.OrchestratorUrl == null || cfg.LobbySecret == null) return null; // nothing to report to
            return new CloudHeartbeat(cfg.OrchestratorUrl, cfg.LobbyId, cfg.LobbySecret, () =>
            {
                Dictionary<string, object> body = lobby.Info();
                body["members"] = lobby.ConnectedMembers();
                body["gameClients"] = Game.ServerClients(cfg.GamePort);
                return body;
            }, () => stop.Set(), cfg.HeartbeatInterval, Program.Log);
        }
    }
}
