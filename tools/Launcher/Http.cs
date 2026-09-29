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
    /// Minimal HTTP/1.1 server on a TcpListener. HttpListener (http.sys) would need an admin URL reservation to listen on
    /// anything but localhost, and the lobby must be reachable from other players.
    /// </summary>
    public class HttpServer
    {
        private readonly TcpListener _listener;
        private readonly Func<Request, Response> _handler;
        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public HttpServer(IPAddress address, int port, Func<Request, Response> handler)
        {
            _listener = new TcpListener(address, port);
            _handler = handler;
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
                    Request req = Read(s);
                    if (req == null) return;
                    req.Remote = ((IPEndPoint)client.Client.RemoteEndPoint).Address;
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
            }
        }

        private static Request Read(Stream s)
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
                if (len > 1 << 20) return null;
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

        public static string Str(this Dictionary<string, object> d, string key, string fallback = null) =>
            d != null && d.TryGetValue(key, out object v) && v != null ? Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture) : fallback;

        public static int Int(this Dictionary<string, object> d, string key, int fallback = 0) =>
            d != null && d.TryGetValue(key, out object v) && v != null && int.TryParse(Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture), out int i) ? i : fallback;

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
