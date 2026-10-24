using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace HmmRevive.Launcher
{
    /// <summary>One HTTP request (one per connection, Connection: close).</summary>
    public class Request
    {
        public string Method, Path, Body = "";
        public Dictionary<string, string> Query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public IPAddress Remote;

        public string Header(string name) => Headers.TryGetValue(name, out string v) ? v : null;
        public string Arg(string name) => Query.TryGetValue(name, out string v) ? v : null;
    }

    public class Response
    {
        public int Status = 200;
        public string ContentType = "application/json; charset=utf-8";
        public byte[] Body = new byte[0];

        public static Response Json(object o, int status = 200) =>
            new Response { Status = status, Body = Encoding.UTF8.GetBytes(Js.Write(o)) };
        public static Response Error(string message, int status = 400) => Json(new Dictionary<string, object> { ["error"] = message }, status);
    }

    /// <summary>
    /// Protection for a server that strangers can reach (the cloud lobby). The defaults are the old behavior: a 1 MiB body
    /// cap and nothing else, which is what the local UI and LAN lobbies keep using.
    /// </summary>
    public class HttpLimits
    {
        /// <summary>Largest request body accepted; a bigger one gets the connection closed without the handler running.</summary>
        public int MaxBodyBytes = 1 << 20;
        /// <summary>Most connections served at once (0 = no limit); further ones are closed at accept.</summary>
        public int MaxConnections;
        /// <summary>Most requests per client per second (0 = no limit); the next one gets 429.</summary>
        public int RequestsPerSecond;
        /// <summary>
        /// The server sits behind one proxy that appends the client address to X-Forwarded-For: count the last entry of
        /// that header as the client. Off, the header is ignored because anyone can send it.
        /// </summary>
        public bool TrustForwardedFor;
        /// <summary>Milliseconds clock for the rate limit (tests inject their own).</summary>
        public Func<long> NowMs = () => System.Diagnostics.Stopwatch.GetTimestamp() * 1000 / System.Diagnostics.Stopwatch.Frequency;
    }

    /// <summary>
    /// Minimal HTTP/1.1 server on a TcpListener. HttpListener (http.sys) would need an admin URL reservation to listen on
    /// anything but localhost, and the lobby must be reachable from other players.
    /// </summary>
    public class HttpServer
    {
        private readonly TcpListener _listener;
        private readonly Func<Request, Response> _handler;
        private readonly HttpLimits _limits;
        private readonly Dictionary<string, Queue<long>> _recent = new Dictionary<string, Queue<long>>();
        private int _active;
        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
        /// <summary>Connections being served right now.</summary>
        public int ActiveConnections => Volatile.Read(ref _active);

        public HttpServer(IPAddress address, int port, Func<Request, Response> handler, HttpLimits limits = null)
        {
            _listener = new TcpListener(address, port);
            _handler = handler;
            _limits = limits ?? new HttpLimits();
        }

        /// <summary>Starts listening; false when the port is taken.</summary>
        public bool Start()
        {
            try { _listener.Start(); }
            catch (SocketException) { return false; }
            new Thread(AcceptLoop) { IsBackground = true, Name = "http:" + Port }.Start();
            return true;
        }

        public void Stop()
        {
            try { _listener.Stop(); } catch { }
        }

        private void AcceptLoop()
        {
            while (true)
            {
                TcpClient c;
                try { c = _listener.AcceptTcpClient(); }
                catch { return; }
                // Over the cap the connection is dropped before it costs a thread (cloud lobby: strangers can connect).
                if (Interlocked.Increment(ref _active) > (_limits.MaxConnections > 0 ? _limits.MaxConnections : int.MaxValue))
                {
                    Interlocked.Decrement(ref _active);
                    try { c.Close(); } catch { }
                    continue;
                }
                // A thread per connection, not the pool: lobby long polls hold theirs for 15 s, and the pool only adds
                // threads slowly past the CPU count, which would stall the lobby with 8 players on a 4-core host.
                new Thread(() => Serve(c)) { IsBackground = true, Name = "http-conn" }.Start();
            }
        }

        private void Serve(TcpClient client)
        {
            using (client)
            {
                try
                {
                    client.ReceiveTimeout = 10000;
                    NetworkStream s = client.GetStream();
                    Request req = Read(s, _limits.MaxBodyBytes);
                    if (req == null) return;
                    req.Remote = ((IPEndPoint)client.Client.RemoteEndPoint).Address;
                    if (_limits.RequestsPerSecond > 0 && !AllowRequest(ClientKey(req)))
                    {
                        Write(s, Response.Error("Too many requests. Slow down.", 429));
                        return;
                    }
                    Response res;
                    try { res = _handler(req) ?? Response.Error("not found", 404); }
                    catch (Exception e)
                    {
                        Program.Log("request " + req.Path + " failed: " + e);
                        res = Response.Error(e.Message, 500);
                    }
                    Write(s, res);
                }
                catch (IOException) { }
                catch (SocketException) { }
                catch (Exception e) { Program.Log("http: " + e.Message); }
                finally { Interlocked.Decrement(ref _active); }
            }
        }

        /// <summary>Who a request counts against: the forwarded client when the proxy is trusted, else the peer address.</summary>
        private string ClientKey(Request req)
        {
            if (_limits.TrustForwardedFor)
            {
                string xff = req.Header("X-Forwarded-For");
                if (!string.IsNullOrWhiteSpace(xff))
                {
                    string last = xff.Substring(xff.LastIndexOf(',') + 1).Trim(); // the entry the proxy appended
                    if (last.Length > 0) return last;
                }
            }
            return req.Remote?.ToString() ?? "";
        }

        /// <summary>Sliding one-second window per client.</summary>
        private bool AllowRequest(string key)
        {
            long now = _limits.NowMs();
            lock (_recent)
            {
                if (_recent.Count > 4096)
                {
                    var stale = new List<string>();
                    foreach (var kv in _recent)
                        if (kv.Value.Count == 0 || now - kv.Value.Peek() >= 1000) stale.Add(kv.Key);
                    foreach (string k in stale) _recent.Remove(k);
                }
                if (!_recent.TryGetValue(key, out Queue<long> q)) _recent[key] = q = new Queue<long>();
                while (q.Count > 0 && now - q.Peek() >= 1000) q.Dequeue();
                if (q.Count >= _limits.RequestsPerSecond) return false;
                q.Enqueue(now);
                return true;
            }
        }

        private static Request Read(Stream s, int maxBody)
        {
            // Headers are ASCII; read byte by byte up to the blank line, then the body by Content-Length.
            var head = new StringBuilder();
            int b, crlf = 0;
            while (crlf < 4 && (b = s.ReadByte()) >= 0)
            {
                char ch = (char)b;
                head.Append(ch);
                crlf = (ch == '\r' || ch == '\n') ? crlf + 1 : 0;
                if (head.Length > 16384) return null;
            }
            string[] lines = head.ToString().Split(new[] { "\r\n" }, StringSplitOptions.None);
            string[] first = lines[0].Split(' ');
            if (first.Length < 2) return null;
            var req = new Request { Method = first[0].ToUpperInvariant() };
            string target = first[1];
            int q = target.IndexOf('?');
            req.Path = Uri.UnescapeDataString(q < 0 ? target : target.Substring(0, q));
            if (q >= 0)
                foreach (string kv in target.Substring(q + 1).Split('&'))
                {
                    if (kv.Length == 0) continue;
                    int e = kv.IndexOf('=');
                    string k = Uri.UnescapeDataString((e < 0 ? kv : kv.Substring(0, e)).Replace('+', ' '));
                    req.Query[k] = e < 0 ? "" : Uri.UnescapeDataString(kv.Substring(e + 1).Replace('+', ' '));
                }
            for (int i = 1; i < lines.Length; i++)
            {
                int c = lines[i].IndexOf(':');
                if (c > 0) req.Headers[lines[i].Substring(0, c).Trim()] = lines[i].Substring(c + 1).Trim();
            }
            if (int.TryParse(req.Header("Content-Length"), out int len) && len > 0)
            {
                if (len > maxBody) return null;
                byte[] body = new byte[len];
                int got = 0, n;
                while (got < len && (n = s.Read(body, got, len - got)) > 0) got += n;
                req.Body = Encoding.UTF8.GetString(body, 0, got);
            }
            return req;
        }

        private static void Write(Stream s, Response res)
        {
            string head = $"HTTP/1.1 {res.Status} {(res.Status == 200 ? "OK" : "Error")}\r\n" +
                          $"Content-Type: {res.ContentType}\r\nContent-Length: {res.Body.Length}\r\n" +
                          "Cache-Control: no-store\r\nConnection: close\r\n\r\n";
            byte[] h = Encoding.ASCII.GetBytes(head);
            s.Write(h, 0, h.Length);
            s.Write(res.Body, 0, res.Body.Length);
            s.Flush();
        }
    }

    /// <summary>JSON through the framework's JavaScriptSerializer (dictionaries and lists, no model classes).</summary>
    public static class Js
    {
        private static readonly System.Web.Script.Serialization.JavaScriptSerializer S =
            new System.Web.Script.Serialization.JavaScriptSerializer { MaxJsonLength = 8 << 20 };

        public static string Write(object o) => S.Serialize(o);

        public static Dictionary<string, object> Read(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return new Dictionary<string, object>();
            return S.DeserializeObject(json) as Dictionary<string, object> ?? new Dictionary<string, object>();
        }

        public static List<object> ReadArray(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return new List<object>();
            object o = S.DeserializeObject(json);
            if (o is object[] a) return new List<object>(a);
            if (o is List<object> l) return l;
            return new List<object>();
        }

        public static string Str(this Dictionary<string, object> d, string key, string fallback = null) =>
            d != null && d.TryGetValue(key, out object v) && v != null ? Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture) : fallback;

        public static int Int(this Dictionary<string, object> d, string key, int fallback = 0) =>
            d != null && d.TryGetValue(key, out object v) && v != null && int.TryParse(Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture), out int i) ? i : fallback;

        public static double Double(this Dictionary<string, object> d, string key, double fallback = 0) =>
            d != null && d.TryGetValue(key, out object v) && v != null && double.TryParse(Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double res) ? res : fallback;

        public static bool Bool(this Dictionary<string, object> d, string key, bool fallback = false) =>
            d != null && d.TryGetValue(key, out object v) && v is bool b ? b : fallback;

        public static Dictionary<string, object> Obj(this Dictionary<string, object> d, string key) =>
            d != null && d.TryGetValue(key, out object v) ? v as Dictionary<string, object> : null;

        public static List<object> List(this Dictionary<string, object> d, string key)
        {
            if (d == null || !d.TryGetValue(key, out object v) || v == null) return new List<object>();
            if (v is object[] a) return new List<object>(a);
            return v as List<object> ?? new List<object>();
        }
    }
}
