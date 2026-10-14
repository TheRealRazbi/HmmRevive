using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace HmmRevive.Launcher
{
    /// <summary>
    /// Checks GitHub Releases for a newer HMM Revive kit and applies it in place.
    /// The update replaces kit files (exe, mod, patcher, scripts, docs) while preserving
    /// instance/, launcher-settings.json, hmmrevive-images/ and user configurations.
    /// </summary>
    public static class Updater
    {
        private const string ReleasesUrl = "https://api.github.com/repos/TheRealRazbi/HmmRevive/releases";

        public static volatile string Status = "";  // "", "checking", "available", "downloading", "applying", "done", "failed: ..."
        public static volatile string LatestVersion;
        public static volatile string ReleaseNotes;
        public static volatile string ReleaseUrl;
        public static volatile bool IsPreview;
        public static volatile int DownloadPercent;

        private static string _assetUrl;
        private static string _expectedSha256;
        private static long _assetSize;

        public static void Check(bool includePreview)
        {
            if (Status == "checking" || Status == "downloading" || Status == "applying") return;
            Status = "checking";
            LatestVersion = null;
            ReleaseNotes = null;

            new Thread(() =>
            {
                try
                {
                    string json = Get(ReleasesUrl);
                    List<object> releases = Js.ReadArray(json);

                    foreach (Dictionary<string, object> rel in releases.OfType<Dictionary<string, object>>())
                    {
                        bool pre = rel.Bool("prerelease");
                        if (pre && !includePreview) continue;

                        string tag = rel.Str("tag_name") ?? "";
                        string ver = tag.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? tag.Substring(1) : tag;
                        if (!IsNewer(ver, Program.Version)) continue;

                        var assets = rel.List("assets").OfType<Dictionary<string, object>>();
                        var zip = assets.FirstOrDefault(a => (a.Str("name") ?? "").EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
                        if (zip == null) continue;

                        LatestVersion = ver;
                        ReleaseNotes = rel.Str("body") ?? "";
                        ReleaseUrl = rel.Str("html_url") ?? "";
                        IsPreview = pre;
                        _assetUrl = zip.Str("browser_download_url");
                        _assetSize = (long)zip.Double("size");

                        string digest = zip.Str("digest") ?? "";
                        _expectedSha256 = digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? digest.Substring(7) : null;

                        Status = "available";
                        Program.Log($"update available: {ver} (preview={pre})");
                        return;
                    }

                    Status = "";
                    Program.Log("update check: up to date");
                }
                catch (Exception e)
                {
                    Status = "failed: " + e.Message;
                    Program.Log("update check failed: " + e.Message);
                }
            }) { IsBackground = true, Name = "update-check" }.Start();
        }

        public static void Apply()
        {
            if (Status != "available" || string.IsNullOrEmpty(_assetUrl)) return;
            if (Game.Running(Game.Server) || Game.Running(Game.Client))
            {
                Status = "failed: Close running match / game before updating";
                return;
            }

            Status = "downloading";
            DownloadPercent = 0;

            new Thread(() =>
            {
                string zipPath = Path.Combine(Paths.Root, "hmmrevive-update.zip");
                try
                {
                    Download(_assetUrl, zipPath, _assetSize);

                    if (!string.IsNullOrEmpty(_expectedSha256))
                    {
                        string actual = Sha256(zipPath);
                        if (!actual.Equals(_expectedSha256, StringComparison.OrdinalIgnoreCase))
                            throw new Exception("Download checksum mismatch. Corrupted file.");
                    }

                    Status = "applying";
                    ExtractAndReplace(zipPath, Paths.Root);

                    try { File.Delete(zipPath); } catch { }
                    Status = "done";
                    Program.Log("update applied successfully: " + LatestVersion);

                    Restart();
                }
                catch (Exception e)
                {
                    Status = "failed: " + e.Message;
                    Program.Log("update apply failed: " + e.Message);
                    try { File.Delete(zipPath); } catch { }
                }
            }) { IsBackground = true, Name = "update-apply" }.Start();
        }

        public static bool IsNewer(string candidate, string current)
        {
            if (string.Equals(candidate, current, StringComparison.OrdinalIgnoreCase)) return false;

            string cNum = Regex.Match(candidate ?? "", @"^[\d.]+").Value;
            string rNum = Regex.Match(current ?? "", @"^[\d.]+").Value;
            string[] c = cNum.Split(new[] { '.' }, StringSplitOptions.RemoveEmptyEntries);
            string[] r = rNum.Split(new[] { '.' }, StringSplitOptions.RemoveEmptyEntries);

            for (int i = 0; i < Math.Max(c.Length, r.Length); i++)
            {
                int cv = i < c.Length && int.TryParse(c[i], out int a) ? a : 0;
                int rv = i < r.Length && int.TryParse(r[i], out int b) ? b : 0;
                if (cv != rv) return cv > rv;
            }

            bool cPre = (candidate ?? "").Contains("-");
            bool rPre = (current ?? "").Contains("-");
            if (cPre != rPre) return rPre; // e.g. 1.5 is newer than 1.5-preview

            return false;
        }

        private static void Download(string url, string path, long expectedSize)
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.UserAgent = "HmmRevive-Launcher/" + Program.Version;
            req.Timeout = 30000;
            req.ReadWriteTimeout = 30000;
            req.Proxy = null;

            using (var res = (HttpWebResponse)req.GetResponse())
            using (Stream src = res.GetResponseStream())
            using (var dst = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                long total = res.ContentLength > 0 ? res.ContentLength : expectedSize;
                byte[] buf = new byte[65536];
                long done = 0;
                int n;
                while ((n = src.Read(buf, 0, buf.Length)) > 0)
                {
                    dst.Write(buf, 0, n);
                    done += n;
                    if (total > 0) DownloadPercent = (int)Math.Min(100, done * 100 / total);
                }
            }
            DownloadPercent = 100;
        }

        private static string Sha256(string path)
        {
            using (var sha = SHA256.Create())
            using (var f = File.OpenRead(path))
            {
                byte[] hash = sha.ComputeHash(f);
                var sb = new StringBuilder(hash.Length * 2);
                foreach (byte b in hash) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        private static void ExtractAndReplace(string zipPath, string root)
        {
            string temp = Path.Combine(root, ".hmmrevive-update-temp");
            if (Directory.Exists(temp))
            {
                try { Directory.Delete(temp, true); } catch { }
            }

            string psCmd = $"Expand-Archive -LiteralPath '{zipPath.Replace("'", "''")}' -DestinationPath '{temp.Replace("'", "''")}' -Force";
            var psi = new ProcessStartInfo("powershell", "-NoProfile -NonInteractive -Command " + psCmd)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
            };

            using (Process p = Process.Start(psi))
            {
                string err = p.StandardError.ReadToEnd();
                p.WaitForExit(120000);
                if (p.ExitCode != 0)
                    throw new Exception("Extraction failed: " + err.Trim());
            }

            string source = temp;
            string[] subDirs = Directory.GetDirectories(temp);
            if (subDirs.Length == 1 && Directory.GetFiles(temp).Length == 0)
                source = subDirs[0];

            var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "instance",
                "instance-2017",
                "instance-2017sep",
                "launcher-settings.json",
                "launcher-settings.log",
                "hmmrevive-images",
                "host-settings.json",
                "play-settings.json",
                "logs",
                ".hmmrevive-update-temp",
                "hmmrevive-update.zip"
            };

            string activeExe = Paths.LauncherExe;
            string oldExe = Path.ChangeExtension(activeExe, ".old.exe");
            try { if (File.Exists(oldExe)) File.Delete(oldExe); } catch { }
            try { if (File.Exists(activeExe)) File.Move(activeExe, oldExe); } catch { }

            CopyDirectory(source, root, skip);

            try { Directory.Delete(temp, true); } catch { }
        }

        private static void CopyDirectory(string src, string dst, HashSet<string> skip)
        {
            foreach (string file in Directory.GetFiles(src))
            {
                string name = Path.GetFileName(file);
                if (skip.Contains(name)) continue;
                string target = Path.Combine(dst, name);
                try { if (File.Exists(target)) File.Delete(target); } catch { }
                File.Copy(file, target, true);
            }

            foreach (string dir in Directory.GetDirectories(src))
            {
                string name = Path.GetFileName(dir);
                if (skip.Contains(name)) continue;
                string target = Path.Combine(dst, name);
                if (!Directory.Exists(target)) Directory.CreateDirectory(target);
                CopyDirectory(dir, target, skip);
            }
        }

        private static void Restart()
        {
            string exe = Paths.LauncherExe;
            if (!File.Exists(exe))
                exe = Path.Combine(Paths.Root, "HMM-Revive.exe");

            try
            {
                Browser.CloseAppWindows();
                Program.StopUi();
                Thread.Sleep(300);

                var psi = new ProcessStartInfo(exe)
                {
                    WorkingDirectory = Paths.Root,
                    UseShellExecute = true,
                };
                Process.Start(psi);
                Environment.Exit(0);
            }
            catch (Exception e)
            {
                Program.Log("restart after update failed: " + e.Message);
                Status = "done";
            }
        }

        private static string Get(string url)
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.UserAgent = "HmmRevive-Launcher/" + Program.Version;
            req.Timeout = 15000;
            req.ReadWriteTimeout = 15000;
            req.Proxy = null;

            using (var res = (HttpWebResponse)req.GetResponse())
            using (var r = new StreamReader(res.GetResponseStream(), System.Text.Encoding.UTF8))
                return r.ReadToEnd();
        }
    }
}
