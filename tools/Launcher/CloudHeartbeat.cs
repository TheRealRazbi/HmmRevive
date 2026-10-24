using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;

namespace HmmRevive.Launcher
{
    /// <summary>
    /// Tells the orchestrator every 30 seconds that this cloud lobby lives and what it looks like (the public list is built
    /// from these reports), and obeys its answer: <c>{"action":"close"}</c>, or a 404 because the orchestrator no longer
    /// knows the lobby, both mean "end now". A failed beat is only logged: a lobby must not die because the orchestrator
    /// was briefly unreachable (the orchestrator's janitor ends lobbies whose heartbeat stops, not the other way round).
    /// </summary>
    public sealed class CloudHeartbeat
    {
        private readonly string _url, _secret;
        private readonly Func<Dictionary<string, object>> _payload;
        private readonly Action _close;
        private readonly TimeSpan _interval;
        private readonly Action<string> _log;
        private volatile bool _stopped;

        public CloudHeartbeat(string orchestratorUrl, string lobbyId, string lobbySecret, Func<Dictionary<string, object>> payload,
            Action close, TimeSpan interval, Action<string> log = null)
        {
            _url = orchestratorUrl.TrimEnd('/') + "/internal/lobbies/" + Uri.EscapeDataString(lobbyId) + "/heartbeat";
            _secret = lobbySecret;
            _payload = payload;
            _close = close;
            _interval = interval;
            _log = log ?? (_ => { });
        }

        public void Start() => new Thread(Loop) { IsBackground = true, Name = "heartbeat" }.Start();

        public void Stop() { _stopped = true; }

        private void Loop()
        {
            while (!_stopped)
            {
                if (!Beat()) return;
                for (var waited = TimeSpan.Zero; waited < _interval && !_stopped; waited += TimeSpan.FromMilliseconds(50))
                    Thread.Sleep(50);
            }
        }

        /// <summary>One report. Returns false when the orchestrator said the lobby must end (the close action was run).</summary>
        internal bool Beat()
        {
            try
            {
                byte[] body = Encoding.UTF8.GetBytes(Js.Write(_payload()));
                var req = (HttpWebRequest)WebRequest.Create(_url);
                req.Method = "POST";
                req.ContentType = "application/json";
                req.Timeout = req.ReadWriteTimeout = 10000;
                req.Headers["X-Lobby-Secret"] = _secret; // header only: never in the URL or the body
                using (Stream s = req.GetRequestStream()) s.Write(body, 0, body.Length);
                using (var res = (HttpWebResponse)req.GetResponse())
                using (var r = new StreamReader(res.GetResponseStream(), Encoding.UTF8))
                {
                    if (Js.Read(r.ReadToEnd()).Str("action") == "close") return Close("the orchestrator asked to close");
                }
                return true;
            }
            catch (WebException e) when (e.Response is HttpWebResponse bad && bad.StatusCode == HttpStatusCode.NotFound)
            {
                return Close("the orchestrator no longer knows this lobby");
            }
            catch (Exception e)
            {
                _log("heartbeat failed, will retry: " + e.Message);
                return true;
            }
        }

        private bool Close(string why)
        {
            _log("closing: " + why);
            _stopped = true;
            try { _close(); } catch (Exception e) { _log("close failed: " + e.Message); }
            return false;
        }
    }
}
