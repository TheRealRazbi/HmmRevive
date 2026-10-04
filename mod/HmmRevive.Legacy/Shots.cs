using System;
using System.IO;
using System.Linq;
using UnityEngine;

namespace HmmRevive.Legacy
{
    /// <summary>
    /// Test aid for hidden clients (--hmmrevive-shots=N): every 8 s, from 20 s after start, renders every camera into an
    /// off-screen texture and writes hmmrevive-shot-&lt;pid&gt;-&lt;n&gt;.png next to HMM.exe (N pictures in all). Shows what
    /// a player would see (menu, pick screen, match) without opening a window, e.g. whether textures load.
    /// </summary>
    public static class Shots
    {
        private static float _next = -1f;
        private static int _taken;

        public static void Tick()
        {
            if (Entry.Shots <= 0 || _taken >= Entry.Shots) return;
            if (_next < 0f) _next = Time.realtimeSinceStartup + 20f;
            if (Time.realtimeSinceStartup < _next) return;
            _next = Time.realtimeSinceStartup + 8f;
            try { Take(++_taken); }
            catch (Exception e) { Log.Error("test shot failed: " + e); }
        }

        private static void Take(int n)
        {
            const int W = 1280, H = 720;
            var rt = new RenderTexture(W, H, 24);
            RenderTexture previous = RenderTexture.active;
            Camera[] cams = Camera.allCameras.Where(c => c.enabled && c.gameObject.activeInHierarchy).OrderBy(c => c.depth).ToArray();
            foreach (Camera cam in cams)
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
                "hmmrevive-shot-" + System.Diagnostics.Process.GetCurrentProcess().Id + "-" + n + ".png");
            File.WriteAllBytes(file, tex.EncodeToPNG());
            UnityEngine.Object.Destroy(tex);
            rt.Release();
            UnityEngine.Object.Destroy(rt);
            Log.Info("test shot " + n + ": " + file + " cameras=" + string.Join(",", cams.Select(c => c.name).ToArray()));
        }
    }
}
