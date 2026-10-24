using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using HmmRevive.Launcher;
using Xunit;

namespace HmmRevive.Launcher.Tests
{
    public class HttpLimitsTests : IDisposable
    {
        private readonly List<HttpServer> _servers = new List<HttpServer>();
        private readonly List<TcpClient> _clients = new List<TcpClient>();
        private int _handled;

        public void Dispose()
        {
            foreach (var c in _clients) { try { c.Close(); } catch { } }
            foreach (var s in _servers) s.Stop();
        }

        private HttpServer Start(HttpLimits limits)
        {
            var server = new HttpServer(IPAddress.Loopback, 0, req =>
            {
                Interlocked.Increment(ref _handled);
                return Response.Json(new Dictionary<string, object> { ["ok"] = true, ["bytes"] = req.Body.Length });
            }, limits);
            Assert.True(server.Start());
            _servers.Add(server);
            return server;
        }

        private TcpClient Connect(HttpServer server)
        {
            var c = new TcpClient();
            c.Connect(IPAddress.Loopback, server.Port);
            c.ReceiveTimeout = 5000;
            _clients.Add(c);
            return c;
        }

        /// <summary>Sends one request and returns the status code, or 0 when the server closed the connection without answering.</summary>
        private int Send(HttpServer server, string method, string body = "", string extraHeaders = "")
        {
            using (var c = new TcpClient())
            {
                c.Connect(IPAddress.Loopback, server.Port);
                c.ReceiveTimeout = 5000;
                var s = c.GetStream();
                byte[] payload = Encoding.UTF8.GetBytes(body);
                string head = method + " /x HTTP/1.1\r\nHost: t\r\n" + extraHeaders + "Content-Length: " + payload.Length + "\r\nConnection: close\r\n\r\n";
                byte[] h = Encoding.ASCII.GetBytes(head);
                try
                {
                    s.Write(h, 0, h.Length);
                    s.Write(payload, 0, payload.Length);
                    var reader = new StreamReader(s, Encoding.ASCII);
                    string line = reader.ReadLine();
                    if (line == null) return 0;
                    return int.Parse(line.Split(' ')[1]);
                }
                catch (IOException) { return 0; }
            }
        }

        [Fact]
        public void ABodyExactlyAtTheLimitIsServed()
        {
            var server = Start(new HttpLimits { MaxBodyBytes = 64 * 1024 });
            Assert.Equal(200, Send(server, "POST", new string('a', 64 * 1024)));
            Assert.Equal(1, _handled);
        }

        [Fact]
        public void ABodyOneByteOverTheLimitClosesTheConnectionWithoutRunningTheHandler()
        {
            var server = Start(new HttpLimits { MaxBodyBytes = 64 * 1024 });
            Assert.Equal(0, Send(server, "POST", new string('a', 64 * 1024 + 1)));
            Assert.Equal(0, _handled);
        }

        [Fact]
        public void TheDefaultServerKeepsTheOldOneMebibyteLimitAndNothingElse()
        {
            var server = Start(null); // what the local UI and LAN lobbies use
            Assert.Equal(200, Send(server, "POST", new string('a', 1 << 20)));
            Assert.Equal(0, Send(server, "POST", new string('a', (1 << 20) + 1)));
            for (int i = 0; i < 50; i++) Assert.Equal(200, Send(server, "GET")); // no rate limit by default
        }

        [Fact]
        public void The65thConcurrentConnectionIsRefusedWhile64AreOpen()
        {
            var server = Start(new HttpLimits { MaxConnections = 64 });
            var open = new List<TcpClient>();
            for (int i = 0; i < 64; i++) open.Add(Connect(server)); // connected, sending nothing: each holds a slot
            WaitFor(() => server.ActiveConnections == 64);

            var extra = Connect(server);
            Assert.Equal(0, extra.GetStream().Read(new byte[16], 0, 16)); // closed by the server: end of stream
            Assert.Equal(64, server.ActiveConnections);

            open[0].Close(); // a slot frees up, so a new request is served again
            WaitFor(() => server.ActiveConnections < 64);
            Assert.Equal(200, Send(server, "GET"));
        }

        [Fact]
        public void The21stRequestInOneSecondFromOneClientGets429()
        {
            long now = 1000;
            var server = Start(new HttpLimits { RequestsPerSecond = 20, NowMs = () => Interlocked.Read(ref now) });
            for (int i = 1; i <= 20; i++) Assert.Equal(200, Send(server, "GET"));
            Assert.Equal(429, Send(server, "GET"));
            Assert.Equal(20, _handled);

            Interlocked.Add(ref now, 1001); // the window has passed
            Assert.Equal(200, Send(server, "GET"));
        }

        [Fact]
        public void AnotherForwardedClientIsNotAffectedWhenTheProxyIsTrusted()
        {
            long now = 1000;
            var server = Start(new HttpLimits { RequestsPerSecond = 20, TrustForwardedFor = true, NowMs = () => Interlocked.Read(ref now) });
            for (int i = 1; i <= 20; i++) Assert.Equal(200, Send(server, "GET", "", "X-Forwarded-For: 203.0.113.7\r\n"));
            Assert.Equal(429, Send(server, "GET", "", "X-Forwarded-For: 203.0.113.7\r\n"));
            Assert.Equal(200, Send(server, "GET", "", "X-Forwarded-For: 198.51.100.9\r\n"));
        }

        [Fact]
        public void OnlyTheLastForwardedEntryCountsBecauseTheProxyAppendsIt()
        {
            long now = 1000;
            var server = Start(new HttpLimits { RequestsPerSecond = 2, TrustForwardedFor = true, NowMs = () => Interlocked.Read(ref now) });
            // The client forges the first entries; the proxy appends the real address last.
            Assert.Equal(200, Send(server, "GET", "", "X-Forwarded-For: 1.1.1.1, 203.0.113.7\r\n"));
            Assert.Equal(200, Send(server, "GET", "", "X-Forwarded-For: 2.2.2.2, 203.0.113.7\r\n"));
            Assert.Equal(429, Send(server, "GET", "", "X-Forwarded-For: 3.3.3.3, 203.0.113.7\r\n"));
        }

        [Fact]
        public void TheForwardedHeaderIsIgnoredWhenTheProxyIsNotTrusted()
        {
            long now = 1000;
            var server = Start(new HttpLimits { RequestsPerSecond = 3, TrustForwardedFor = false, NowMs = () => Interlocked.Read(ref now) });
            Assert.Equal(200, Send(server, "GET", "", "X-Forwarded-For: 1.1.1.1\r\n"));
            Assert.Equal(200, Send(server, "GET", "", "X-Forwarded-For: 2.2.2.2\r\n"));
            Assert.Equal(200, Send(server, "GET", "", "X-Forwarded-For: 3.3.3.3\r\n"));
            Assert.Equal(429, Send(server, "GET", "", "X-Forwarded-For: 4.4.4.4\r\n")); // a forged header gains nothing
        }

        private static void WaitFor(Func<bool> condition)
        {
            var until = DateTime.UtcNow.AddSeconds(5);
            while (!condition() && DateTime.UtcNow < until) Thread.Sleep(10);
            Assert.True(condition(), "condition not reached in time");
        }
    }
}
