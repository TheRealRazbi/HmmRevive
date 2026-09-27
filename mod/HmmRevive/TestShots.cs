using System;
using System.IO;
using System.Linq;
using HeavyMetalMachines;
using Pocketverse;
using UnityEngine;

namespace HmmRevive
{
    /// <summary>
    /// Test aid for hidden clients (--hmmrevive-shots=N): renders every camera into an off-screen texture every 6 s once
    /// the match scene is up, and writes hmmrevive-shot-&lt;pid&gt;-&lt;n&gt;.png next to HMM.exe, N pictures in all. Lets a
    /// -batchmode client show what a player would see (skins, HUD) without opening a window.
    /// </summary>
    public class TestShots : MonoBehaviour
    {
        private float _next = -1f;
        private int _taken;

        public static void Attach(HMMHub hub)
        {
            if (Entry.ServerMode || Entry.Headless || Entry.Shots <= 0 || hub.GetComponent<TestShots>() != null) return;
            hub.gameObject.AddComponent<TestShots>();
        }

        private void Update()
        {
            HMMHub hub = GameHubBehaviour.Hub;
            if (_taken >= Entry.Shots || hub == null || hub.State.Current == null || hub.State.Current.StateKind != GameState.GameStateKind.Game) return;
            if (_next < 0f) _next = Time.unscaledTime + 8f;
            if (Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + 6f;
            try
            {
                Take(++_taken);
            }
            catch (Exception e)
            {
                Log.Error("test shot failed: " + e);
            }
        }

        private static void Take(int n)
        {
            const int W = 1280, H = 720;
            var rt = new RenderTexture(W, H, 24);
            RenderTexture previous = RenderTexture.active;
            foreach (Camera cam in Camera.allCameras.Where(c => c.enabled && c.gameObject.activeInHierarchy).OrderBy(c => c.depth))
            {
                RenderTexture target = cam.targetTexture;
                cam.targetTexture = rt;
                cam.Render();
                cam.targetTexture = target;
            }
            RenderTexture.active = rt;
            var tex = new Texture2D(W, H, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, W, H), 0, 0);
            tex.Apply();
            RenderTexture.active = previous;
            string file = Path.Combine(Path.GetDirectoryName(Application.dataPath) ?? ".",
                $"hmmrevive-shot-{System.Diagnostics.Process.GetCurrentProcess().Id}-{n}.png");
            File.WriteAllBytes(file, tex.EncodeToPNG());
            Destroy(tex);
            rt.Release();
            Destroy(rt);
            Log.Info($"test shot {n}: {file} cameras={string.Join(",", Camera.allCameras.Select(c => c.name).ToArray())}");
        }
    }
}
