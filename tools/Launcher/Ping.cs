using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;

namespace HmmRevive.Launcher
{
    /// <summary>
    /// Latency to a lobby's server. The game talks UDP to the server's address, and a UDP packet cannot be timed without
    /// speaking the game's protocol, so this times the TCP handshake to the lobby's port on the same address instead: one round
    /// trip, no TLS, no application. ICMP ping is not used because cloud servers do not answer it. The number is the network
    /// distance to the server, not the game's own measurement (which also includes the server's tick).
    /// </summary>
    public static class Ping
    {
        /// <summary>Probe: connect to ip:port and return milliseconds, or null on failure. Tests substitute it.</summary>
        public delegate int? Probe(IPAddress ip, int port, int timeoutMs);

        public static int? TcpRtt(IPAddress ip, int port, int timeoutMs)
        {
            try
            {
                using (var s = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true })
                {
                    var watch = Stopwatch.StartNew();
                    IAsyncResult r = s.BeginConnect(new IPEndPoint(ip, port), null, null);
                    if (!r.AsyncWaitHandle.WaitOne(timeoutMs)) return null;
                    s.EndConnect(r);
                    watch.Stop();
                    return (int)Math.Max(1, Math.Round(watch.Elapsed.TotalMilliseconds));
                }
            }
            catch { return null; }
        }

        /// <summary>The median of <paramref name="samples"/> probes (the first one pays for slow paths), or null if none answered.</summary>
        public static int? Measure(string host, int port, int samples = 3, int timeoutMs = 1500, Probe probe = null)
        {
            IPAddress ip;
            if (!IPAddress.TryParse(host, out ip))
            {
                try { ip = Dns.GetHostAddresses(host).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork); }
                catch { return null; }
                if (ip == null) return null;
            }
            probe = probe ?? TcpRtt;
            var ok = new List<int>();
            for (int i = 0; i < samples; i++)
            {
                int? ms = probe(ip, port, timeoutMs);
                if (ms.HasValue) ok.Add(ms.Value);
            }
            if (ok.Count == 0) return null;
            ok.Sort();
            return ok[ok.Count / 2];
        }

        /// <summary>Host and port to time for a lobby address: "https://name" is name:443, "ip:port" and "ip" use the lobby port.</summary>
        public static bool Target(string address, int lobbyPort, out string host, out int port)
        {
            host = null;
            port = lobbyPort;
            if (string.IsNullOrWhiteSpace(address)) return false;
            string a = address.Trim();
            if (a.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) { port = 443; a = a.Substring(8); }
            else if (a.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) a = a.Substring(7);
            a = a.TrimEnd('/');
            int colon = a.LastIndexOf(':');
            if (colon > 0 && a.IndexOf(':') == colon)
            {
                if (!int.TryParse(a.Substring(colon + 1), out port)) return false;
                a = a.Substring(0, colon);
            }
            host = a;
            return a.Length > 0;
        }
    }

    /// <summary>The last readings of a running measurement: current, average, best, worst and how many were lost.</summary>
    public sealed class PingStats
    {
        private readonly object _gate = new object();
        private readonly Queue<int?> _window = new Queue<int?>();
        private readonly int _size;

        public PingStats(int windowSize = 30) { _size = windowSize; }

        public void Add(int? ms)
        {
            lock (_gate)
            {
                _window.Enqueue(ms);
                while (_window.Count > _size) _window.Dequeue();
            }
        }

        /// <summary>Null until the first reading. "ms" is null when the latest reading was lost.</summary>
        public Dictionary<string, object> Snapshot()
        {
            lock (_gate)
            {
                if (_window.Count == 0) return null;
                var ok = _window.Where(x => x.HasValue).Select(x => x.Value).ToList();
                int lost = _window.Count - ok.Count;
                return new Dictionary<string, object>
                {
                    ["ms"] = _window.Last(),
                    ["avg"] = ok.Count == 0 ? (object)null : (int)Math.Round(ok.Average()),
                    ["min"] = ok.Count == 0 ? (object)null : ok.Min(),
                    ["max"] = ok.Count == 0 ? (object)null : ok.Max(),
                    ["lossPercent"] = (int)Math.Round(100.0 * lost / _window.Count),
                };
            }
        }
    }
}
