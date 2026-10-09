using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HeavyMetalMachines;
using HeavyMetalMachines.Combat;
using HeavyMetalMachines.Match;
using Pocketverse;
using UnityEngine;

namespace HmmRevive.Legacy
{
    /// <summary>
    /// Client test aid (--hmmrevive-dropper-scan), how the invisible ball droppers were found (DropperLook.cs): 20 s into
    /// the match, logs every BombTargetTrigger (goal or ball dropper: team, place, drawn or not) and saves a top-down
    /// screenshot of the whole arena and of each cluster of droppers (hmmrevive-dropper-&lt;n&gt;.png next to HMM.exe).
    /// The picture comes from the back buffer, so it has Hoplon's native particles and works with the window hidden
    /// (--hmmrevive-hide-window, needs a windowed client: Unity 4 draws nothing in -batchmode). While it runs, the camera
    /// is pinned over each view, the HUD cameras are off, and the game is told the ball is there, which lights the
    /// bomb-blocker walls around it (their shader fades them in near _BombPosition).
    /// </summary>
    public class DropperScan : MonoBehaviour
    {
        private float _start = -1f, _next;
        private List<KeyValuePair<string, Vector3>> _views;
        private int _view = -1;
        private bool _aimed;

        private void Update()
        {
            HMMHub hub = GameHubBehaviour<HMMHub>.Hub;
            if (hub == null || hub.Match == null || hub.Match.State != MatchData.MatchState.MatchStarted) return;
            if (_start < 0f) _start = Time.realtimeSinceStartup;
            if (Time.realtimeSinceStartup - _start < 20f || Time.realtimeSinceStartup < _next) return;
            if (_views == null) { _views = Scan(); return; }
            Camera cam = CarCamera.Singleton != null ? CarCamera.Singleton.camera : Camera.main;
            if (cam == null) return;
            if (_aimed) // the camera has been on view _view for a while: take it
            {
                string file = Path.Combine(Path.GetDirectoryName(Application.dataPath) ?? ".", "hmmrevive-dropper-" + _view + ".png");
                Application.CaptureScreenshot(file);
                Log.Info("dropper shot " + _view + " " + _views[_view].Key + " at " + _views[_view].Value + ": " + file);
                _aimed = false;
                _next = Time.realtimeSinceStartup + 0.5f;
                return;
            }
            if (++_view >= _views.Count)
            {
                Destroy(cam.GetComponent<Pin>());
                Destroy(this);
                return;
            }
            string key = _views[_view].Key;
            Pin pin = cam.GetComponent<Pin>() ?? cam.gameObject.AddComponent<Pin>();
            pin.Position = _views[_view].Value + Vector3.up * (key == "arena" ? 420f : 130f);
            pin.Rotation = Quaternion.Euler(90f, 0f, 0f);
            pin.Ball = key != "arena";
            foreach (Camera other in Camera.allCameras) if (other != cam) other.enabled = false; // no HUD in the pictures
            _aimed = true;
            _next = Time.realtimeSinceStartup + 1.5f;
        }

        // Holds the camera where the scan wants it: OnPreCull runs after every script moved the camera this frame.
        private class Pin : MonoBehaviour
        {
            public Vector3 Position;
            public Quaternion Rotation;
            public bool Ball;

            private void OnPreCull()
            {
                transform.position = Position;
                transform.rotation = Rotation;
                if (Ball) Shader.SetGlobalVector("_BombPosition", new Vector3(Position.x, 0f, Position.z));
            }
        }

        private static List<KeyValuePair<string, Vector3>> Scan()
        {
            var clusters = new Dictionary<string, List<Vector3>>();
            foreach (BombTargetTrigger t in Resources.FindObjectsOfTypeAll(typeof(BombTargetTrigger)).Cast<BombTargetTrigger>())
            {
                string path = t.name;
                for (Transform p = t.transform.parent; p != null; p = p.parent) path = p.name + "/" + path;
                var r = t.GetComponent<Renderer>();
                Log.Info("dropper trigger team=" + t.TeamOwner + (t.ExplodeOnDrop ? " goal" : "") + " at " + t.transform.position
                         + " active=" + t.gameObject.activeInHierarchy + " drawn=" + (r != null && r.enabled) + " " + path);
                if (t.ExplodeOnDrop || !t.gameObject.activeInHierarchy) continue;
                // one view per cluster of droppers (within 60 units of the cluster's first one)
                string k = clusters.Keys.FirstOrDefault(n => n.StartsWith(t.TeamOwner + " ", StringComparison.Ordinal)
                                                             && (clusters[n][0] - t.transform.position).magnitude < 60f);
                if (k == null) clusters[k = t.TeamOwner + " " + clusters.Count] = new List<Vector3>();
                clusters[k].Add(t.transform.position);
            }
            var views = new List<KeyValuePair<string, Vector3>> { new KeyValuePair<string, Vector3>("arena", Vector3.zero) };
            foreach (var c in clusters)
                views.Add(new KeyValuePair<string, Vector3>("droppers " + c.Key, c.Value.Aggregate(Vector3.zero, (a, b) => a + b) / c.Value.Count));
            Log.Info("dropper scan: " + Application.loadedLevelName + ", " + clusters.Count + " dropper clusters");
            return views;
        }
    }
}
