using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Assets.ClientApiObjects;
using Assets.ClientApiObjects.Components;
using HeavyMetalMachines;
using Pocketverse;
using SharedUtils.Loading;
using UnityEngine;

namespace HmmRevive
{
    /// <summary>
    /// Pictures for the launcher (--hmmrevive-dump-images=DIR, a hidden client that then quits): every skin's card art
    /// (DIR/skins/CARID-NUMBER.jpg, numbers as in CarChoice.SkinsOf / skins.txt) and the first frame of every emote's
    /// sprite sheet (DIR/emotes/NUMBER.png, numbers as in Emotes.List). Made on each player's PC from their own game
    /// files, so no game art ships with HMM Revive. Writes DIR/done.txt (with the mod version) when finished.
    /// </summary>
    public class ImageDump : IDynamicAssetListener<Texture2D>
    {
        private class Job { public string Asset, File; public Rect Uv; public int Width; }

        private static ImageDump _running; // the loader keeps listeners by weak reference
        private static bool _started;
        private readonly Dictionary<string, List<Job>> _byAsset = new Dictionary<string, List<Job>>();
        private int _left, _written, _failed;
        private float _deadline;
        private string _dir;

        public static void Tick(HMMHub hub)
        {
            if (Entry.DumpImages == null || Entry.ServerMode) return;
            if (_running != null) { _running.Check(); return; }
            if (_started || hub.InventoryColletion == null || hub.InventoryColletion.AllItemTypes.Count == 0) return;
            _started = true;
            try { _running = new ImageDump(); _running.Start(hub, Entry.DumpImages); }
            catch (Exception e)
            {
                Log.Error("image dump failed: " + e);
                Application.Quit();
            }
        }

        private void Start(HMMHub hub, string dir)
        {
            _dir = dir;
            Directory.CreateDirectory(Path.Combine(dir, "skins"));
            Directory.CreateDirectory(Path.Combine(dir, "emotes"));
            var jobs = new List<Job>();
            foreach (var car in hub.InventoryColletion.AllCharactersByCharacterId.OrderBy(c => c.Key))
            {
                List<IItemType> skins = CarChoice.SkinsOf(car.Value.Id);
                for (int n = 0; n < skins.Count; n++)
                {
                    string sprite = null;
                    try { sprite = skins[n].GetComponent<SkinPrefabItemTypeComponent>().SkinSpriteName; } catch { }
                    if (!string.IsNullOrEmpty(sprite))
                        jobs.Add(new Job { Asset = sprite, File = Path.Combine(dir, $"skins/{car.Key}-{n}.jpg"), Uv = new Rect(0, 0, 1, 1), Width = 320 });
                }
            }
            List<ItemTypeScriptableObject> emotes = Emotes.List();
            for (int n = 0; n < emotes.Count; n++)
            {
                // Sprite sheets are 3x3 frames (EmoteItemTypeComponent.SpriteSheetSize); the first is the top-left one.
                string sheet = emotes[n].GetComponent<EmoteItemTypeComponent>().spriteSheetName;
                jobs.Add(new Job { Asset = sheet, File = Path.Combine(dir, $"emotes/{n}.png"), Uv = new Rect(0, 2f / 3f, 1f / 3f, 1f / 3f), Width = 128 });
            }
            foreach (Job j in jobs)
            {
                List<Job> list;
                if (!_byAsset.TryGetValue(j.Asset, out list)) _byAsset[j.Asset] = list = new List<Job>();
                list.Add(j);
            }
            _left = _byAsset.Count;
            _deadline = Time.unscaledTime + 180f;
            Log.Info($"image dump: {jobs.Count} pictures from {_byAsset.Count} textures into {dir}");
            foreach (string asset in _byAsset.Keys.ToList())
                if (!Hoplon.Unity.Loading.Loading.TextureManager.GetAssetAsync(asset, this))
                {
                    Log.Info("image dump: no texture " + asset);
                    _failed++;
                    _left--;
                }
        }

        public void OnAssetLoaded(string name, Texture2D texture)
        {
            List<Job> jobs;
            if (!_byAsset.TryGetValue(name, out jobs) || jobs == null) return;
            _byAsset[name] = null;
            _left--;
            if (texture == null) { _failed++; return; }
            foreach (Job j in jobs)
            {
                try { Save(texture, j); _written++; }
                catch (Exception e) { _failed++; Log.Error($"image dump {j.File}: {e.Message}"); }
            }
        }

        private static void Save(Texture2D tex, Job j)
        {
            int w = Mathf.Min(j.Width, Mathf.RoundToInt(tex.width * j.Uv.width));
            int h = Mathf.Max(1, Mathf.RoundToInt(w * tex.height * j.Uv.height / (tex.width * j.Uv.width)));
            RenderTexture rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            RenderTexture prev = RenderTexture.active;
            try
            {
                Graphics.Blit(tex, rt, new Vector2(j.Uv.width, j.Uv.height), new Vector2(j.Uv.x, j.Uv.y));
                RenderTexture.active = rt;
                var img = new Texture2D(w, h, TextureFormat.RGBA32, false);
                img.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                img.Apply();
                bool png = j.File.EndsWith(".png");
                File.WriteAllBytes(j.File, png ? img.EncodeToPNG() : img.EncodeToJPG(85));
                UnityEngine.Object.Destroy(img);
            }
            finally
            {
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(rt);
            }
        }

        private void Check()
        {
            if (_left > 0 && Time.unscaledTime < _deadline) return;
            Log.Info($"image dump done: {_written} written, {_failed} failed, {_left} not loaded");
            File.WriteAllText(Path.Combine(_dir, "done.txt"), Entry.Version + "\r\n");
            _running = null;
            _left = int.MaxValue;
            Application.Quit();
        }
    }
}
