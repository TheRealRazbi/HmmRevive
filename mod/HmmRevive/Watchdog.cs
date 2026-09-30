using System;
using System.Diagnostics;
using System.Threading;
using HeavyMetalMachines;
using Pocketverse;
using UnityEngine;

namespace HmmRevive
{
    /// <summary>Logs a compact status line whenever the hub's network/match state changes (game logs are buffered).</summary>
    public class Watchdog : MonoBehaviour
    {
        private string _last;
        private float _next;
        private int _pingTick;

        private static volatile int _frame;
        private static Thread _mainThread;

        public static void Attach(HMMHub hub)
        {
            if (hub.GetComponent<Watchdog>() != null) return;
            hub.gameObject.AddComponent<Watchdog>();
            _mainThread = Thread.CurrentThread;
            new Thread(FreezeMonitor) { IsBackground = true, Name = "HmmRevive.FreezeMonitor" }.Start();
        }

        // Background thread: if the main thread stops producing frames, log its stack once.
        private static void FreezeMonitor()
        {
            int lastFrame = -1, stuckFor = 0;
            bool dumped = false;
            while (true)
            {
                Thread.Sleep(1000);
                int f = _frame;
                if (f != lastFrame)
                {
                    if (stuckFor >= 5) Log.Info($"UNFROZE after {stuckFor}s, frame {lastFrame} -> {f}");
                    lastFrame = f; stuckFor = 0; dumped = false; continue;
                }
                if (++stuckFor < 5 || (dumped && stuckFor % 15 != 0)) continue;
                dumped = true;
                string stack;
                try
                {
#pragma warning disable 618
                    _mainThread.Suspend();
                    try { stack = new StackTrace(_mainThread, false).ToString(); }
                    finally { _mainThread.Resume(); }
#pragma warning restore 618
                }
                catch (Exception e) { stack = "stack capture failed: " + e.Message; }
                Log.Error($"FREEZE main thread stuck {stuckFor}s at frame {f}:\n{stack}\nlast traced methods (oldest first):\n{Hooks.DumpTrace()}");
            }
        }

        private void Update()
        {
            _frame = Time.frameCount;
            if (Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + 1f;
            Entry.HideOwnWindows(); // Unity re-shows the blank -batchmode window after startup
            UpgradeDump.Tick(GameHubBehaviour.Hub);
            HealScan.Tick(GameHubBehaviour.Hub);
            // Client round-trip time to the server (Lidgren average), every 10 s while connected.
            if (++_pingTick % 10 == 0 && GameHubBehaviour.Hub?.Net is NetworkClient pc && pc.IsConnected())
                Log.Info($"PING rtt={pc.GetPing():F0}ms");
            string status;
            try
            {
                HMMHub hub = GameHubBehaviour.Hub;
                string net = hub.Net is NetworkClient nc ? $"client:{nc.State}" : hub.Net is NetworkServer ns ? $"server:clients={ns.ConnectedClientsCount}" : "net:?";
                status = $"{net} clockSync={hub.Clock?.IsSynchronized} match={hub.Match?.State} kind={hub.Match?.Kind} " +
                         $"players={hub.Players?.Players?.Count} bots={hub.Players?.Bots?.Count} state={hub.State?.Current?.name} " +
                         $"waitingServer={hub.User?.HackIsWaitingForMatchServerConnection}";
            }
            catch (Exception e)
            {
                status = "status error: " + e.Message;
            }
            if (status == _last) return;
            _last = status;
            Log.Info("WATCH " + status);
        }
    }
}
