using System;
using System.Collections.Generic;

namespace HmmRevive.Launcher
{
    /// <summary>
    /// The host's controls for a lobby that runs in the cloud: the same actions as the local host page, sent to the lobby's
    /// <c>/lobby/host/*</c> routes over HTTPS with the host token in the Authorization header (and nowhere else).
    /// </summary>
    public sealed class RemoteHost
    {
        private readonly string _baseUrl, _token;
        private readonly Cloud.Transport _send;

        public RemoteHost(string lobbyBaseUrl, string hostToken, Cloud.Transport transport = null)
        {
            _baseUrl = (lobbyBaseUrl ?? "").TrimEnd('/');
            _token = hostToken;
            _send = transport ?? Cloud.Http;
        }

        public void Configure(Dictionary<string, object> setup) => Post("configure", setup);
        public void Start() => Post("start", null);
        public void Stop() => Post("stop", null);
        public void Kick(string id) => Post("kick", new Dictionary<string, object> { ["id"] = id });
        public void Move(string id, string team) => Post("move", new Dictionary<string, object> { ["id"] = id, ["team"] = team });
        public void Swap(string a, string b) => Post("swap", new Dictionary<string, object> { ["a"] = a, ["b"] = b });
        public void DraftReset() => Post("draft-reset", null);
        public void SetVisibility(string visibility) => Post("visibility", new Dictionary<string, object> { ["visibility"] = visibility });
        public void Close() => Post("close", null);

        private void Post(string route, Dictionary<string, object> body)
        {
            if (!_baseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                throw new CloudException("Host controls only work over https, so the host token is never sent in clear text.");
            var headers = new Dictionary<string, string> { ["Authorization"] = "Bearer " + _token };
            CloudReply reply;
            try { reply = _send("POST", _baseUrl + "/lobby/host/" + route, headers, Js.Write(body ?? new Dictionary<string, object>()), Cloud.TimeoutMs); }
            catch (Exception e) when (e is System.Net.WebException || e is TimeoutException || e is System.IO.IOException)
            {
                throw new CloudException("Couldn't reach the lobby. Try again.");
            }
            if (reply.Status >= 200 && reply.Status < 300) return;
            string message = null;
            try { message = Js.Read(reply.Body).Str("error"); } catch { }
            throw new CloudException(message ?? $"The lobby answered {reply.Status}.", reply.Status);
        }
    }
}
