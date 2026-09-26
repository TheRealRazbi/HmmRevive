# Heavy Metal Machines: reverse-engineering findings

Game build: `Release.15.00.250`, Unity **2018.4.24f1**, **Mono** scripting backend with the
*legacy* (.NET 3.5) runtime (`boot.config: scripting-runtime-version=legacy`). The assemblies
aren't obfuscated. Decompile them with ILSpy (`ilspycmd`) into `decompiled/`, which is gitignored:
Hoplon's code must never be committed or published.

Labels: **[measured]** = seen in files or logs; **[inferred]** = read from code but not yet run.

## Architecture

| Piece | What it is | Status |
|---|---|---|
| Swordfish | Hoplon's HTTP REST backend (`https://live.heavymetalmachines.com/SwordfishService/api/...`) | dead (DNS fails) [measured] |
| Game server | Same Unity codebase, `NetworkServer` over Lidgren UDP, port from `[Server] Port` (default 9696) | code shipped, **server scene not shipped** [measured] |
| Client | `HMM.exe`, `NetworkClient` | works up to login [measured] |

## Config system (Pocketverse.ConfigLoader) [measured in code]

Config sources are loaded in order, and later ones override earlier ones:
1. `<game>/default.config`
2. `<game>/user.config` (optional; doesn't exist by default)
3. command line: `HMM.exe BeginConfig [Section] Key=Value ... EndConfig`

So the game can be redirected or reconfigured **without editing any game file**.

## Hoplon's dev LAN mode [inferred from code]

Hoplon's developers had an internal path that runs matches with no backend at all:

**Client** (`HeavyMetalMachines.Frontend.Starter.ContinueToNextState`):
```
[Debug] SkipSwordfish=true  DirectMatch=true  PlayerName=<name>
[Server] IP=<server ip>  Port=9696
```
The client skips Welcome/login, fakes a user id, unlocks every item type, and calls
`User.ConnectToServer`, then goes into character select when match data arrives.

**Server** (`HeavyMetalMachines.Server.ServerStartup`):
```
[Debug] SkipSwordfish=true IsDebug=true
[Game]  PlayerCount=<humans> ArenaIndex=<n> PickMode=<n> RedTeamBotsCount=<n> BluTeamBotsCount=<n>
[Server] Port=9696
```
`FetchServerInfo` reads all of this, opens `NetworkServer.StartServer(port)` and creates bots
(`AuthMan.CreateBots()`). It waits for `AuthMan.GameReady()`, counts down for 5 s, then moves
to character selection.

`SkipSwordfish` is checked in about 90 places across the client and server code, which means
the offline mode was properly maintained and isn't a leftover stub.

## The missing piece: server bootstrap [measured]

- The build has 61 scenes and **none is a server scene**. Scene 0 is `PreStartClient`, and the
  game scenes live under `Client-Bundle/`. Hoplon's dedicated server was a separate Unity build.
- `PreStart` loads `0StarterScene` (level7). That scene has `HMMHub-Client` (HMMHub +
  **NetworkClient**) and a `StateMachine` whose children are client GameStates
  (`0Starter, 3MainMenu, 6Game, 7Loading, 8Reconnect, ...`), wired through `ClientStateMachineInstaller`.
- The server classes are all present in `Assembly-CSharp-firstpass`:
  `NetworkServer`, `ServerStartup`, `CharacterSelectionServerState`, `PickModeServerSetup`,
  `ServerGame`, `ServerFinish`, `ServerStateMachineInstaller`, `ServerApplicationInstaller`,
  `ServerHubMonoInstaller`.
- The DI design is dual-role: `ServerMonoInstaller<T>` / `ClientMonoInstaller<T>` destroy
  themselves at runtime depending on `Hub.Net.IsServer()`. Shared prefabs therefore carry both
  roles' wiring.
- Arenas are asset bundles in `Content/Resources/scenes/*.hmm` (`arena_newsacrifice`,
  `arena_monster`, `arena03_pinball`, ...).

**Done**: the mod rebuilds the server at runtime (`mod/HmmRevive/ServerBootstrap.cs`). Harmony 2.x does **not** work on
this Mono 2.0 runtime (MonoMod needs `RuntimeType`), so every hook is a static IL edit made by `tools/Patcher`.

## More findings from getting it running [measured]
- `RedirectProjectContext` maps scene names to ProjectContext prefabs: `MainServer` → `ProjectContext_MainServer`
  (shipped, full server installer set). `0StarterScene` → `ProjectContext_InventoryInGameTool`.
- The `MainServer` scene held these components, which exist nowhere in the shipped data: `AFKController`,
  `BotAIHub`, `ScrapBank`, `HMMSensorController`, `CharacterSelectionServerBehaviour` (next to the empty
  `CharacterSelectionServerState`), plus the scene installers (`ServerStateMachineInstaller`, `CharacterSelectionMainServerInstaller`,
  `ServerApplicationInstaller`, `ServerCompetitiveModeInstaller`, `ArenaModifierInstaller`).
- The client **also** needs the mod: `LidgrenNetClient.SendCipherKeyToRemotePeer` busy-waits on
  `CryptographyKeyProvider.ServerPublicKey`, which only Swordfish login set. Hoplon's
  `SfNetConfiguration.AddTestCryptoKeys()` pair works for both sides.
- `NetworkServer` uses a `FreeToken` (accepts everyone). In SkipSwordfish mode, `AuthenticationManager.FakeRequest`
  assigns teams alternately and uses the client's `PlayerName`.
- Under `-nographics`, `ScreenResolutionController.Awake` → native `GetDedicatedVideoMemory` crashes
  (particlesystem.plugin.dll). It's skipped by a hook.

## Network [measured in code]
- `[SfNetwork] MaximumTransmissionUnit` defaults to **508** bytes, so there's no Tailscale MTU issue.
- `Lidgren.Network` UDP.

## Swordfish login flow (for a possible later fake backend) [measured from 2026-09-23 log]
17 login steps. Step 1 is `SteamAPI.Init()`. Step 3 fires `api/Log/LogInstallation` and
`api/Login/SteamLogin`. `live.heavymetalmachines.com` no longer resolves, so login times out
after about 5 s.
