using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HeavyMetalMachines;
using HeavyMetalMachines.Frontend;
using HeavyMetalMachines.Server;
using Pocketverse;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HmmRevive.Legacy
{
    /// <summary>
    /// Turns the old client's first scene into a match server, the same idea as mod/HmmRevive/ServerBootstrap.cs for the
    /// current build but without dependency injection. The client build ships the server code (ServerStartup,
    /// PickModeServerSetup, ServerGame, ServerFinish, NetworkServer, AuthenticationManager's server side) but not the
    /// server scene, so the client scene is rewired: NetworkClient becomes NetworkServer, the client UI goes, and the
    /// server's states replace the client's.
    /// </summary>
    public static class ServerBootstrap
    {
        private static bool _rewired, _deferredDone;

        // First of HMMHub.Awake / StateMachine.Awake.
        public static void TryRewire(object caller)
        {
            if (_rewired) return;
            var hub = Object.FindObjectOfType(typeof(HMMHub)) as HMMHub;
            if (hub == null) { Log.Info("no hub yet (" + caller.GetType().Name + ")"); return; }
            _rewired = true;
            Log.Info("rewire from " + caller.GetType().Name + ".Awake, level=" + Application.loadedLevelName);
            Rewire(hub);
        }

        private static void Rewire(HMMHub hub)
        {
            GameObject hubGo = hub.gameObject;
            var client = hubGo.GetComponent<NetworkClient>();
            var server = hubGo.AddComponent<NetworkServer>();
            hub.Net = server;
            if (client != null) Object.DestroyImmediate(client);
            Log.Info("hub: NetworkClient -> NetworkServer");
            // Hoplon's server scene had no GUI; LoadingState etc. only touch it "if (hub.GuiScripts)".
            hub.GuiScripts = null;

            // Client-only UI: NGUI roots and the car camera (kept only where they share a root with the state machine/hub).
            var roots = new List<string>();
            foreach (Transform t in Object.FindObjectsOfType(typeof(Transform)))
            {
                if (t == null || t.parent != null) continue;
                bool ui = t.GetComponentInChildren(typeof(UIRoot)) != null || t.GetComponentInChildren(typeof(CarCamera)) != null;
                bool keep = t.GetComponentInChildren(typeof(StateMachine)) != null || t.GetComponentInChildren(typeof(GameHub)) != null;
                roots.Add(t.name + (ui ? (keep ? "(ui,kept)" : "(ui,destroyed)") : ""));
                if (ui && !keep) Object.DestroyImmediate(t.gameObject);
            }
            Log.Info("scene roots: " + string.Join(", ", roots.ToArray()));
            foreach (string name in new[] { "Assets.Standard_Assets.Scripts.HMM.PlotKids.SpectatorController", "HeavyMetalMachines.VFX.SocialModalGUI" })
            {
                Type t = typeof(HMMHub).Assembly.GetType(name);
                if (t == null) continue;
                foreach (Object c in Object.FindObjectsOfType(t)) { Log.Info("destroying client-only " + name); Object.DestroyImmediate(c); }
            }

            // Park the client's states where the state machine can't see them, then build the server's.
            var sm = Object.FindObjectOfType(typeof(StateMachine)) as StateMachine;
            var parking = new GameObject("HmmRevive_ClientStates");
            parking.SetActive(false);
            Object.DontDestroyOnLoad(parking);
            foreach (GameState st in sm.GetComponentsInChildren<GameState>(true))
                st.transform.parent = parking.transform;
            Log.Info("parked client states: " + string.Join(",", parking.GetComponentsInChildren<GameState>(true).Select(s => s.name).ToArray()));

#if Y2016
            var finish = NewState<ServerFinish>(sm, "ServerFinish", GameState.GameStateKind.Game);
#else
            var finish = NewState<ServerFinish>(sm, "ServerFinish", GameState.GameStateKind.GameWrapUp);
#endif
            var game = NewState<ServerGame>(sm, "ServerGame", GameState.GameStateKind.Game);
            game.GameOver = finish;
            var loading = NewState<LoadingState>(sm, "ServerLoading", GameState.GameStateKind.Loading);
            loading.gameState = game;
            var pick = NewState<PickModeServerSetup>(sm, "PickModeServerSetup", GameState.GameStateKind.Pick);
            pick.loadingState = loading;
            // Hoplon's ServerPickConfig asset wasn't shipped with the client: make the same config at runtime.
            var cfg = ScriptableObject.CreateInstance<ScreenConfig>();
#if Y2016 || Y2017SEP
            cfg.ConfigDic["PickTime"] = Entry.PickTime.ToString(System.Globalization.CultureInfo.InvariantCulture);
            cfg.ConfigDic["CustomizationTime"] = Entry.CustomizationTime.ToString(System.Globalization.CultureInfo.InvariantCulture);
#else
            cfg.PickTime = Entry.PickTime;
            cfg.CustomizationTime = Entry.CustomizationTime;
#endif
            pick.PickServerConfig = cfg;
            var startup = NewState<ServerStartup>(sm, "ServerStartup", GameState.GameStateKind.Stater);
            startup.PickModeState = pick;
            startup.ServerGameState = game;
            startup.ServerLoadingState = loading;
            startup.Relay = hubGo.GetComponent<ServerRelay>() ?? hubGo.AddComponent<ServerRelay>();
            sm.First = startup;
            Log.Info("server states built; First=ServerStartup, pick " + Entry.PickTime + "s + " + Entry.CustomizationTime + "s");
        }

        private static T NewState<T>(StateMachine sm, string name, GameState.GameStateKind kind) where T : GameState
        {
            var go = new GameObject(name);
            go.SetActive(false);
            go.transform.parent = sm.transform;
            var st = go.AddComponent<T>();
            st.StateKind = kind;
            st.SceneReference = string.Empty;
            return st;
        }

        // HMMHub.Start: server-only hub components whose Awake needs an initialized hub.
        public static void ActivateDeferred(HMMHub hub)
        {
            if (_deferredDone) return;
            _deferredDone = true;
            GameObject go = hub.gameObject;
            if (hub.afkController == null) hub.afkController = go.AddComponent<AFKController>();
            if (hub.BotAIHub == null) hub.BotAIHub = go.AddComponent<HeavyMetalMachines.BotAI.BotAIHub>();
            if (hub.ScrapBank == null) hub.ScrapBank = go.AddComponent<HeavyMetalMachines.Bank.ScrapBank>();
            Log.Info("server components added; null hub refs: " + NullRefs(hub));
        }

        private static string NullRefs(HMMHub hub)
        {
            var nulls = new List<string>();
            for (Type t = hub.GetType(); t != null && t != typeof(MonoBehaviour); t = t.BaseType)
                foreach (FieldInfo f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    if (typeof(Object).IsAssignableFrom(f.FieldType) && (f.GetValue(hub) as Object) == null)
                        nulls.Add(f.FieldType.Name + " " + f.Name);
            return string.Join(", ", nulls.ToArray());
        }

        // GameState.EnableState, server side.
        public static void OnState(GameState st)
        {
#if !Y2016
            // Without humans the pick never finishes: bots only pick once "every player selected" (CharacterService.
            // _allSelected, computed when a player selects) and once UpdateAllBot ran in the Picks stage (IsBotPicking).
            if (st is PickModeServerSetup && GameHubBehaviour<HMMHub>.Hub.Players.Players.Count == 0)
            {
                var characters = GameHubBehaviour<HMMHub>.Hub.Characters;
#if !Y2017SEP
                characters.IsBotPicking = true;
#endif
                FieldInfo selected = characters.GetType().GetField("_allSelected", BindingFlags.Instance | BindingFlags.NonPublic);
                if (selected != null) selected.SetValue(characters, true);
                Log.Info("no players: bots pick by themselves" + (selected == null ? " (CharacterService._allSelected not found)" : ""));
            }
#endif
        }
    }
}
