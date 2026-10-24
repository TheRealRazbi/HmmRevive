using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;

namespace HmmRevive.Launcher
{
    /// <summary>The orchestrator said no (or could not be reached); the message is meant for the player.</summary>
    public class CloudException : Exception
    {
        public readonly int Status;
        public CloudException(string message, int status = 0) : base(message) { Status = status; }
    }

    public sealed class CloudReply
    {
        public int Status;
        public string Body = "";
    }

    /// <summary>
    /// The launcher's client of the cloud orchestrator (<c>api.hmm.ovidio.dev</c>): create, read, delete and list cloud lobbies.
    /// HTTPS only: the invite key travels in a header and must never go out in clear text, so an <c>http://</c> address is
    /// refused before anything is sent.
    /// </summary>
    public sealed class Cloud
    {
        public const string DefaultUrl = "https://api.hmm.ovidio.dev";
        public const int TimeoutMs = 30000;

        /// <summary>How a request reaches the network; tests replace it. Throws on timeouts and connection failures.</summary>
        public delegate CloudReply Transport(string method, string url, IDictionary<string, string> headers, string body, int timeoutMs);

        private readonly string _baseUrl, _apiKey;
        private readonly Transport _send;

        public Cloud(string baseUrl, string apiKey, Transport transport = null)
        {
            _baseUrl = (baseUrl ?? "").TrimEnd('/');
            _apiKey = apiKey;
            _send = transport ?? Http;
        }

        /// <summary>The client for the player's saved settings (invite key and, for tests of a staging stack, a different address).</summary>
        public static Cloud FromSettings() =>
            new Cloud(Settings.Data.Str("cloudUrl", DefaultUrl), Settings.Data.Str("cloudKey"));

        /// <summary>Creates a lobby: returns id, hostname and the host token (shown only in this reply).</summary>
        public Dictionary<string, object> Create(string version, string visibility, string password, string label, string region = null)
        {
            if (string.IsNullOrEmpty(_apiKey)) throw new CloudException("Enter your cloud invite key first.");
            var body = new Dictionary<string, object> { ["version"] = version, ["visibility"] = visibility ?? "public" };
            if (!string.IsNullOrEmpty(password)) body["password"] = password;
            if (!string.IsNullOrEmpty(label)) body["label"] = label;
            if (!string.IsNullOrEmpty(region)) body["region"] = region; // none: the orchestrator uses Brazil
            // Not retried: if the first attempt did reach the orchestrator, a second would start a second (billed) lobby.
            return Js.Read(Call("POST", "/v1/lobbies", body, auth: true, retry: false));
        }

        public Dictionary<string, object> Get(string id) => Js.Read(Call("GET", "/v1/lobbies/" + Uri.EscapeDataString(id), null, auth: true, retry: true));

        public void Delete(string id) => Call("DELETE", "/v1/lobbies/" + Uri.EscapeDataString(id), null, auth: true, retry: true);

        /// <summary>The public lobbies that are ready. Needs no key.</summary>
        public List<Dictionary<string, object>> List() =>
            Js.Read(Call("GET", "/v1/lobbies", null, auth: false, retry: true)).List("lobbies").OfType<Dictionary<string, object>>().ToList();

        private string Call(string method, string path, object body, bool auth, bool retry)
        {
            if (!_baseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                throw new CloudException("The cloud address must start with https:// so your invite key is never sent in clear text.");
            var headers = new Dictionary<string, string>();
            if (auth && !string.IsNullOrEmpty(_apiKey)) headers["X-Api-Key"] = _apiKey;
            string json = body == null ? null : Js.Write(body);

            for (int attempt = 1; ; attempt++)
            {
                CloudReply reply;
                try { reply = _send(method, _baseUrl + path, headers, json, TimeoutMs); }
                catch (Exception e) when (e is WebException || e is TimeoutException || e is IOException || e is System.Net.Sockets.SocketException)
                {
                    if (retry && attempt == 1) continue;
                    throw new CloudException("The cloud is unavailable right now. Try again in a minute.");
                }
                if (reply.Status >= 200 && reply.Status < 300) return reply.Body;
                string message = null;
                try { message = Js.Read(reply.Body).Str("error"); } catch { }
                if (reply.Status >= 500 && retry && attempt == 1 && message == null) continue; // a gateway error while the app wakes up
                throw new CloudException(message ?? $"The cloud answered {reply.Status}.", reply.Status);
            }
        }

        internal static CloudReply Http(string method, string url, IDictionary<string, string> headers, string body, int timeoutMs)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = method;
            req.Timeout = req.ReadWriteTimeout = timeoutMs;
            req.UserAgent = "HMM-Revive/" + Program.Version;
            foreach (var h in headers) req.Headers[h.Key] = h.Value;
            if (body != null)
            {
                req.ContentType = "application/json";
                byte[] b = Encoding.UTF8.GetBytes(body);
                using (Stream s = req.GetRequestStream()) s.Write(b, 0, b.Length);
            }
            try
            {
                using (var res = (HttpWebResponse)req.GetResponse()) return Reply(res);
            }
            catch (WebException e) when (e.Response is HttpWebResponse bad)
            {
                using (bad) return Reply(bad); // an HTTP error is an answer, not a failure to reach the cloud
            }
        }

        private static CloudReply Reply(HttpWebResponse res)
        {
            using (var r = new StreamReader(res.GetResponseStream(), Encoding.UTF8))
                return new CloudReply { Status = (int)res.StatusCode, Body = r.ReadToEnd() };
        }
    }
}
