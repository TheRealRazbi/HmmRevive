using System;
using System.Reflection;
using Zenject;

namespace HmmRevive
{
    /// <summary>
    /// Static entry points that tools/Patcher splices into game methods.
    /// Keep signatures in sync with the patch table in tools/Patcher/Program.cs.
    /// </summary>
    public static class Hooks
    {
        // RedirectProjectContext.FindProjectContext(string sceneName): sceneName = MapProjectContextScene(sceneName)
        public static string MapProjectContextScene(string sceneName)
        {
            if (!Entry.ServerMode) return sceneName;
            Log.Info($"FindProjectContext({sceneName}) -> MainServer");
            return "MainServer";
        }

        // Start of SceneContext.Awake / HMMHub.Awake / StateMachine.Awake
        public static void BeforeAwake(object self)
        {
            if (!Entry.ServerMode) return;
            try
            {
                ServerBootstrap.TryRewire(self);
            }
            catch (Exception e)
            {
                Log.Error("Rewire failed: " + e);
            }
        }

        // Replaces DiContainer.Inject(item) inside Zenject.LazyInstanceInjector.LazyInjectAll.
        // Server: log and continue so one run reveals every missing binding. Client: unchanged.
        public static void SafeInject(DiContainer container, object item)
        {
            if (!Entry.ServerMode)
            {
                container.Inject(item);
                return;
            }
            try
            {
                container.Inject(item);
            }
            catch (Exception e)
            {
                string msg = e is ZenjectException ? e.Message : e.ToString();
                int cut = msg.IndexOf("Object graph", StringComparison.Ordinal);
                if (cut > 0) msg = msg.Substring(0, cut).Trim();
                if (msg.Length > 3000) msg = msg.Substring(0, 3000);
                Log.Error($"inject failed for {item?.GetType().FullName}: {msg}");
            }
        }

        // Start of WindowsGameQuitHandler.Quit(GameQuitReason, string) and ServerEmergencyQuit.Quit()
        public static void LogQuit(int reason)
        {
            Log.Info($"QUIT requested reason={reason} stack:\n{Environment.StackTrace}");
        }

        // Start of StudioSystemExtensions.Initialize(FMOD.Studio.System, ...): route audio to NOSOUND when muted.
        public static void BeforeFmodInit(FMOD.Studio.System studio)
        {
            if (!Entry.Mute) return;
            FMOD.System core;
            var r1 = studio.getCoreSystem(out core);
            var r2 = core.setOutput(FMOD.OUTPUTTYPE.NOSOUND);
            Log.Info($"FMOD muted (getCoreSystem={r1}, setOutput={r2})");
        }

        // Spectators chat with everyone like in the game's custom matches (only there did Hoplon allow it).
        // ChatService.ClientSendMessage: replaces SpectatorController.IsSpectating in "spectators may not chat".
        public static bool SpectatorChatBlocked() => false;

        // Start of ChatService.IsValidChatSender (server): if (AnyoneMayChat()) return true;
        public static bool AnyoneMayChat() => true;

        // Start of HMMHub.Start: hub is initialized, safe to wake server-only components.
        public static void HubStart(object hub)
        {
            if (Entry.ServerMode) ServerBootstrap.ActivateDeferred();
            Entry.HideOwnWindows();
            Watchdog.Attach((HeavyMetalMachines.HMMHub)hub);
            CarSwap.Attach((HeavyMetalMachines.HMMHub)hub);
            OutOfCombatRepair.Attach((HeavyMetalMachines.HMMHub)hub);
            TestShots.Attach((HeavyMetalMachines.HMMHub)hub);
            MatchEnd.Attach((HeavyMetalMachines.HMMHub)hub);
        }

        // Start of Pocketverse.GameState.EnableState: unbuffered state trace for both roles.
        public static void StateEnabled(object state)
        {
            Log.Info("STATE +" + ((UnityEngine.Object)state).name + " (" + state.GetType().Name + ")");
            ApplyScoreTarget();
            if (state is HeavyMetalMachines.Frontend.LoadingState) CarSwap.OnLoadingStarted();
            MatchEnd.OnState(state);
        }

        // Same override the tutorial uses (StartMatchTutorialBehaviour): shorter matches for testing the match end.
        private static void ApplyScoreTarget()
        {
            if (Entry.ScoreTarget <= 0) return;
            var bombManager = Pocketverse.GameHubBehaviour.Hub?.BombManager;
            if (bombManager == null || bombManager.Rules == null || bombManager.Rules.BombScoreTarget == Entry.ScoreTarget) return;
            bombManager.Rules.BombScoreTarget = Entry.ScoreTarget;
            Log.Info("BombScoreTarget = " + Entry.ScoreTarget);
        }

        // Start of HudWindowManager.GameState_ListenToStateChanged. The HUD only learns it is in a match (State=Game,
        // which the ESC/options menu requires) through listeners that InstallListeners() adds when the client passes the
        // Welcome/login state. DirectMatch skips Welcome, so install them on the first state change instead.
        public static void HudStateChanged(object hudWindowManager)
        {
            if (Entry.ServerMode) return;
            const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
            Type t = hudWindowManager.GetType();
            FieldInfo installed = t.GetField("_stateListenersInstalled", Private);
            if ((bool)installed.GetValue(hudWindowManager)) return;
            try
            {
                t.GetMethod("InstallListeners", Private).Invoke(hudWindowManager, null);
                Log.Info("HUD state listeners installed");
            }
            catch (Exception e)
            {
                // InstallListeners sets its flag first, so a partial failure is not retried. The one listener that
                // matters for ESC is the bomb phase change; make sure it is there.
                Log.Error("HUD InstallListeners failed, subscribing bomb phase only: " + (e.InnerException ?? e));
                var bombManager = Pocketverse.GameHubBehaviour.Hub.BombManager;
                EventInfo ev = bombManager.GetType().GetEvent("ListenToPhaseChange");
                ev.AddEventHandler(bombManager, Delegate.CreateDelegate(ev.EventHandlerType, hudWindowManager,
                    t.GetMethod("BombManager_ListenToPhaseChange", Private)));
            }
        }

        // Start of GetBotDifficulty.Get(TeamKind): if (HasBotDifficulty(team)) return BotDifficulty(team);
        // The game picks the level from the other team's MMR (config RedMMR/BlueMMR, 500 without Swordfish); the arena tables
        // map 400+ to Hard, so "auto" is Hard. The launcher can set each team instead.
        public static bool HasBotDifficulty(HeavyMetalMachines.Match.TeamKind team)
        {
            var d = BotDifficulty(team);
            Log.Info($"bot difficulty {team}: {(d == HeavyMetalMachines.BotAI.BotAIGoal.BotDifficulty.Invalid ? "auto" : d.ToString())}");
            return d != HeavyMetalMachines.BotAI.BotAIGoal.BotDifficulty.Invalid;
        }

        public static HeavyMetalMachines.BotAI.BotAIGoal.BotDifficulty BotDifficulty(HeavyMetalMachines.Match.TeamKind team) =>
            team == HeavyMetalMachines.Match.TeamKind.Red ? Entry.RedBotDifficulty
            : team == HeavyMetalMachines.Match.TeamKind.Blue ? Entry.BluBotDifficulty
            : HeavyMetalMachines.BotAI.BotAIGoal.BotDifficulty.Invalid;

        // Tracing builds only (HMM_TRACE=1): method-entry breadcrumbs for the main thread.
        private static readonly string[] _trace = new string[256];
        private static int _traceIdx;
        private static System.Threading.Thread _traceMain;

        public static void Trace(string method)
        {
            if (_traceMain == null) _traceMain = System.Threading.Thread.CurrentThread;
            if (System.Threading.Thread.CurrentThread != _traceMain) return;
            _trace[_traceIdx++ & 255] = method;
        }

        public static string DumpTrace()
        {
            var sb = new System.Text.StringBuilder();
            int end = _traceIdx;
            for (int i = Math.Max(0, end - 256); i < end; i++) sb.Append("  ").Append(_trace[i & 255]).AppendLine();
            return sb.ToString();
        }

        // Replaces the constant local UDP port 33000 in NetworkClient.OpenConnection. A fixed port means a second
        // client on the same PC fails to bind; 0 lets the OS pick a free one (the server never connects back to it).
        public static int ClientLocalPort() => 0;

        // WindowsPlatform.CheckSingleApplicationInstance: allow server + several clients on one PC.
        public static bool SkipSingleInstanceCheck() => true;

        // ScreenResolutionController.Awake and other GPU-only code: skip when there is no GPU device.
        public static bool SkipWhenHeadless() => Entry.Headless;
    }
}
