using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HeavyMetalMachines;
using HeavyMetalMachines.CharacterSelection.Server;
using HeavyMetalMachines.Frontend;
using HeavyMetalMachines.Infra.DependencyInjection.Installers;
using HeavyMetalMachines.Infra.DependencyInjection.Installers.Arena;
using HeavyMetalMachines.Server;
using HeavyMetalMachines.Swordfish;
using Pocketverse;
using UnityEngine;
using UnityEngine.SceneManagement;
using Zenject;
using Object = UnityEngine.Object;

namespace HmmRevive
{
    /// <summary>
    /// Turns the client's 0StarterScene into Hoplon's (unshipped) "MainServer" scene:
    /// - DI: RedirectProjectContext resolves the shipped ProjectContext_MainServer prefab.
    /// - Hub: NetworkClient is swapped for NetworkServer.
    /// - StateMachine: client GameStates are parked; server GameStates are built and wired.
    /// - SceneContext: client scene installers replaced with the server ones.
    /// </summary>
    public static class ServerBootstrap
    {
        private const string StarterScene = "0StarterScene";
        private static bool _rewired;
        private static GameObject _deferred;

        public static void ActivateDeferred()
        {
            if (_deferred == null || _deferred.activeSelf) return;
            _deferred.SetActive(true);
            HMMHub hub = GameHubBehaviour.Hub;
            hub.ClientApi.BaseUrl = hub.Config.GetValue(ConfigAccess.SFBaseUrl);
            Log.Info("activated deferred server components; ClientApi.BaseUrl=" + hub.ClientApi.BaseUrl);
            LogArenas(hub);
        }

        // "arenas: 1=Arena_SacrificeLegacy(arena_newsacrifice, custom), ..." so -Arena N can be picked by name.
        private static void LogArenas(HMMHub hub)
        {
            try
            {
                var parts = new List<string>();
                for (int i = 0; i < hub.ArenaConfig.GetNumberOfArenas(); i++)
                {
#pragma warning disable 612, 618
                    var a = hub.ArenaConfig.GetArenaByIndex(i);
#pragma warning restore 612, 618
                    if (a == null) continue;
                    string name = global::Language.Get(a.DraftName, HeavyMetalMachines.Localization.TranslationContext.MainMenuGui);
                    parts.Add($"{i}={name}({a.SceneName}{(a.IsTutorial ? ", tutorial" : "")}{(a.IsCustomOnly ? ", custom" : "")}{(a.IsOnCheatsEnabledOnly ? ", cheats" : "")})");
                }
                Log.Info("arenas: " + string.Join(", ", parts.ToArray()));
            }
            catch (Exception e)
            {
                Log.Error("arena list failed: " + e.Message);
            }
        }

        private static readonly HashSet<string> ClientOnlyRoots = new HashSet<string> { "UI Root (2D)" };
        private static readonly string[] ClientOnlyHubChildren = { "PlotKids_Controllers" };
        private static readonly string[] ClientOnlyComponents =
        {
            "SwordfishStore", "HeavyMetalMachines.Frontend.HudWindowManager",
            "HeavyMetalMachines.Counselor.ClientCounselorController", "HeavyMetalMachines.Frontend.CursorManager",
            "HeavyMetalMachines.Audio.Music.MusicManager",
        };
        // HMMHub fields that pointed at server-only components of the MainServer scene.
        private static readonly string[] ServerHubComponents = { "BotAIHub", "ScrapBank", "SensorController" };

        public static void TryRewire(object caller)
        {
            if (_rewired) return;
            Scene scene = SceneManager.GetActiveScene();
            var hub = Object.FindObjectOfType<HMMHub>();
            if (hub == null)
            {
                Log.Info($"RewirePrefix from {caller?.GetType().Name}: no hub yet (scene={scene.name})");
                return;
            }
            _rewired = true;
            Log.Info($"Rewire triggered by {caller?.GetType().Name}.Awake in scene {hub.gameObject.scene.name}");
            Rewire(hub);
        }

        private static void Rewire(HMMHub hub)
        {
            // 1. network role
            GameObject hubGo = hub.gameObject;
            var client = hubGo.GetComponent<NetworkClient>();
            var server = hubGo.AddComponent<NetworkServer>();
            hub.Net = server;
            if (client != null) Object.DestroyImmediate(client);
            Log.Info("hub: NetworkClient -> NetworkServer");

            // Server-only components the MainServer scene used to provide.
            if (hub.OldAfkController == null)
            {
                hub.OldAfkController = hubGo.AddComponent<AFKController>();
                Log.Info("hub: added AFKController");
            }
            // Added under an inactive holder: their Awake needs an initialized hub (activated from HMMHub.Start).
            _deferred = new GameObject("HmmRevive_ServerComponents");
            _deferred.SetActive(false);
            _deferred.transform.SetParent(hubGo.transform, false);
            foreach (string fieldName in ServerHubComponents)
            {
                FieldInfo f = Field(hub.GetType(), fieldName);
                if (f.GetValue(hub) as Object != null) continue;
                f.SetValue(hub, _deferred.AddComponent(f.FieldType));
                Log.Info($"hub: added {f.FieldType.Name} as {fieldName} (deferred)");
            }
            // Some bindings shared with the client (e.g. SwordfishQueueConfigurationProvider) take services from
            // Hub.ClientApi. The real server only created it when connecting to Swordfish; construct an idle one.
            if (hub.ClientApi == null)
            {
                hub.ClientApi = new ClientAPI.SwordfishClientApi(false); // BaseUrl set in ActivateDeferred (config is injected later)
                Log.Info("hub: created idle SwordfishClientApi");
            }
            LogNullHubReferences(hub);

            // 2. strip client-only UI (Hoplon's MainServer scene had no UI; its objects need client-only bindings)
            foreach (GameObject root in hubGo.scene.GetRootGameObjects())
                if (ClientOnlyRoots.Contains(root.name))
                {
                    Log.Info("destroying client-only root: " + root.name);
                    Object.DestroyImmediate(root);
                }
            foreach (string child in ClientOnlyHubChildren)
            {
                Transform t = hubGo.transform.Find(child);
                if (t == null) continue;
                Log.Info("destroying client-only hub child: " + child);
                Object.DestroyImmediate(t.gameObject);
            }

            foreach (string typeName in ClientOnlyComponents)
            {
                Type t = FindType(typeName);
                if (t == null) { Log.Error("type not found: " + typeName); continue; }
                foreach (GameObject root in hubGo.scene.GetRootGameObjects())
                foreach (Component c in root.GetComponentsInChildren(t, true))
                {
                    Log.Info($"destroying client-only component {typeName} on {c.gameObject.name}");
                    Object.DestroyImmediate(c);
                }
            }

            // 3. state machine
            var sm = Object.FindObjectOfType<StateMachine>();
            if (sm == null)
                sm = Resources.FindObjectsOfTypeAll<StateMachine>().First(s => s.gameObject.scene == hubGo.scene);
            var parking = new GameObject("HmmRevive_ClientStates");
            parking.SetActive(false);
            foreach (GameState st in sm.GetComponentsInChildren<GameState>(true))
                st.transform.SetParent(parking.transform, false);
            // Move them out of the scene so SceneContext doesn't inject client-only dependencies into them.
            Object.DontDestroyOnLoad(parking);
            Log.Info("parked client states: " + string.Join(",", parking.GetComponentsInChildren<GameState>(true).Select(s => s.name).ToArray()));

            var finish = NewState<ServerFinish>(sm, "ServerFinish", GameState.GameStateKind.GameWrapUp);
            var game = NewState<ServerGame>(sm, "ServerGame", GameState.GameStateKind.Game);
            game.GameOver = finish;
            var loading = NewState<LoadingState>(sm, "ServerLoading", GameState.GameStateKind.Loading);
            loading.gameState = game;
            var pick = NewState<PickModeServerSetup>(sm, "PickModeServerSetup", GameState.GameStateKind.Pick);
            pick.loadingState = loading;
            var charSel = NewState<CharacterSelectionServerState>(sm, "CharacterSelectionServer", GameState.GameStateKind.Pick);
            charSel.gameObject.AddComponent<CharacterSelectionServerBehaviour>(); // runs selection when the state activates
            var startup = NewState<ServerStartup>(sm, "ServerStartup", GameState.GameStateKind.Stater);
            startup.ServerGameState = game;
            startup.ServerLoadingState = loading;
            startup.Relay = hubGo.AddComponent<ServerRelay>();
            Field(typeof(ServerStartup), "_swordfishServices").SetValue(startup, Object.FindObjectOfType<SwordfishServices>()
                ?? Resources.FindObjectsOfTypeAll<SwordfishServices>().FirstOrDefault());
            sm.First = startup;
            Log.Info("server states built; First=ServerStartup");

            // 4. scene installers
            var ctx = Object.FindObjectsOfType<SceneContext>().FirstOrDefault(c => c.gameObject.scene == hubGo.scene);
            if (ctx == null)
            {
                Log.Error("no SceneContext in starter scene");
                return;
            }
            var installers = ctx.Installers.Where(i => !(i is ClientStateMachineInstaller)).ToList();
            GameObject ig = new GameObject("HmmRevive_ServerInstallers");
            ig.SetActive(false);
            ig.transform.SetParent(ctx.transform, false);

            var smi = ig.AddComponent<ServerStateMachineInstaller>();
            Field(typeof(ServerStateMachineInstaller), "_stateMachine").SetValue(smi, sm);
            Field(typeof(ServerStateMachineInstaller), "_loadingState").SetValue(smi, loading);
            var csi = ig.AddComponent<CharacterSelectionMainServerInstaller>();
            Field(typeof(CharacterSelectionMainServerInstaller), "_legacyServerPickState").SetValue(csi, pick);
            Field(typeof(CharacterSelectionMainServerInstaller), "_characterSelectionServerState").SetValue(csi, charSel);
            installers.Add(smi);
            installers.Add(csi);
            installers.Add(ig.AddComponent<ServerApplicationInstaller>());
            installers.Add(ig.AddComponent<ServerCompetitiveModeInstaller>());
            installers.Add(ig.AddComponent<ArenaModifierInstaller>());
            ig.SetActive(true);
            ctx.Installers = installers;
            Log.Info("scene installers: " + string.Join(",", installers.Select(i => i.GetType().Name).ToArray()));
        }

        private static T NewState<T>(StateMachine sm, string name, GameState.GameStateKind kind) where T : GameState
        {
            var go = new GameObject(name);
            go.SetActive(false);
            go.transform.SetParent(sm.transform, false);
            var st = go.AddComponent<T>();
            st.StateKind = kind;
            st.SceneReference = string.Empty;
            return st;
        }
    
        private static void LogNullHubReferences(HMMHub hub)
        {
            var nulls = new List<string>();
            for (Type t = hub.GetType(); t != null && t != typeof(MonoBehaviour); t = t.BaseType)
                foreach (FieldInfo f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    if (typeof(Object).IsAssignableFrom(f.FieldType) && (f.GetValue(hub) as Object) == null)
                        nulls.Add($"{f.FieldType.Name} {f.Name}");
            Log.Info("hub null references: " + string.Join(", ", nulls.ToArray()));
        }

        private static Type FindType(string name)
        {
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type t = a.GetType(name, false);
                if (t != null) return t;
            }
            return null;
        }

        private static FieldInfo Field(Type t, string name) =>
            t.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(t.FullName, name);
    }
}
