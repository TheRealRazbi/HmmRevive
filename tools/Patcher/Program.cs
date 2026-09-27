// Builds a patched, hardlinked instance of Heavy Metal Machines.
//
//   Patcher <gameDir> <instanceDir> <modDir>
//
// Every file of the original install is hardlinked into <instanceDir> (no extra disk space,
// original install is never written to). Only Assembly-CSharp-firstpass.dll is replaced with a
// real copy in which Infra.PreStart.Awake() first calls HmmRevive.Entry.Init(). The mod DLLs from
// <modDir> are copied into <instanceDir>/HMM_Data/Managed.
using System.Runtime.InteropServices;
using Mono.Cecil;
using Mono.Cecil.Cil;

static class Program
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CreateHardLink(string newFile, string existingFile, IntPtr sa);

    const string TargetDll = "Assembly-CSharp-firstpass.dll";

    static int Main(string[] args)
    {
        if (args.Length != 3)
        {
            Console.Error.WriteLine("usage: Patcher <gameDir> <instanceDir> <modDir>");
            return 2;
        }
        string game = Path.GetFullPath(args[0]), inst = Path.GetFullPath(args[1]), mod = Path.GetFullPath(args[2]);
        string managedRel = Path.Combine("HMM_Data", "Managed");

        int linked = 0, copied = 0;
        foreach (string src in Directory.EnumerateFiles(game, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(game, src);
            if (rel.StartsWith("logs" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
            if (rel.Equals(Path.Combine(managedRel, TargetDll), StringComparison.OrdinalIgnoreCase)) continue;
            string dst = Path.Combine(inst, rel);
            if (File.Exists(dst)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(dst));
            if (CreateHardLink(dst, src, IntPtr.Zero)) { linked++; continue; }
            int err = Marshal.GetLastWin32Error();
            if (err != 17) // ERROR_NOT_SAME_DEVICE: instance on another drive than the game, so copy (uses disk space)
                throw new IOException($"CreateHardLink failed ({err}) for {rel}");
            if (copied++ == 0) Console.WriteLine("instance is on another drive than the game: copying files instead of hardlinking (~9 GB)...");
            File.Copy(src, dst);
        }
        Console.WriteLine($"hardlinked {linked} new files, copied {copied}");

        string managed = Path.Combine(inst, managedRel);
        foreach (string dll in Directory.EnumerateFiles(mod, "*.dll"))
        {
            string dst = Path.Combine(managed, Path.GetFileName(dll));
            if (File.Exists(dst)) File.Delete(dst); // never write through a hardlink
            File.Copy(dll, dst);
            Console.WriteLine($"copied mod {Path.GetFileName(dll)}");
        }

        PatchPreStart(Path.Combine(game, managedRel, TargetDll), Path.Combine(managed, TargetDll), Path.Combine(managed, "HmmRevive.dll"));
        return 0;
    }

    static void PatchPreStart(string original, string output, string modDll)
    {
        var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(Path.GetDirectoryName(original));
        var rp = new ReaderParameters { AssemblyResolver = resolver, ReadingMode = ReadingMode.Immediate };
        using var asm = AssemblyDefinition.ReadAssembly(original, rp);
        using var modAsm = AssemblyDefinition.ReadAssembly(modDll, rp);
        var module = asm.MainModule;

        MethodReference Hook(string type, string name) =>
            module.ImportReference(modAsm.MainModule.GetType(type).Methods.Single(m => m.Name == name));
        MethodDefinition Target(string type, string name) =>
            module.GetType(type)?.Methods.Single(m => m.Name == name && m.HasBody)
            ?? throw new InvalidOperationException($"type not found: {type}");

        // --- patch table (keep in sync with mod/HmmRevive/Hooks.cs) ---
        Prologue(Target("Infra.PreStart", "Awake"), il => new[] { il.Create(OpCodes.Call, Hook("HmmRevive.Entry", "Init")) });

        var find = Target("HeavyMetalMachines.Infra.DependencyInjection.RedirectProjectContext", "FindProjectContext");
        Prologue(find, il => new[]
        {
            il.Create(OpCodes.Ldarg_1),
            il.Create(OpCodes.Call, Hook("HmmRevive.Hooks", "MapProjectContextScene")),
            il.Create(OpCodes.Starg_S, find.Parameters[0]),
        });

        foreach (var (type, name) in new[] { ("Zenject.SceneContext", "Awake"), ("HeavyMetalMachines.HMMHub", "Awake"), ("Pocketverse.StateMachine", "Awake") })
            Prologue(Target(type, name), il => new[] { il.Create(OpCodes.Ldarg_0), il.Create(OpCodes.Call, Hook("HmmRevive.Hooks", "BeforeAwake")) });

        Prologue(Target("Pocketverse.GameState", "EnableState"), il => new[] { il.Create(OpCodes.Ldarg_0), il.Create(OpCodes.Call, Hook("HmmRevive.Hooks", "StateEnabled")) });
        Prologue(Target("HeavyMetalMachines.HMMHub", "Start"), il => new[] { il.Create(OpCodes.Ldarg_0), il.Create(OpCodes.Call, Hook("HmmRevive.Hooks", "HubStart")) });
        Prologue(Target("HeavyMetalMachines.Frontend.HudWindowManager", "GameState_ListenToStateChanged"), il => new[] { il.Create(OpCodes.Ldarg_0), il.Create(OpCodes.Call, Hook("HmmRevive.Hooks", "HudStateChanged")) });

        SkipIf(Target("HeavyMetalMachines.Windows.WindowsPlatform", "CheckSingleApplicationInstance"), Hook("HmmRevive.Hooks", "SkipSingleInstanceCheck"), OpCodes.Ldc_I4_1);
        SkipIf(Target("HeavyMetalMachines.Frontend.ScreenResolutionController", "Awake"), Hook("HmmRevive.Hooks", "SkipWhenHeadless"), null);
        SkipIf(Target("NativePlugins.UnityInterface", "GetDedicatedVideoMemorySize"), Hook("HmmRevive.Hooks", "SkipWhenHeadless"), OpCodes.Ldc_I4_0);

        Prologue(Target("HeavyMetalMachines.Windows.WindowsGameQuitHandler", "Quit"), il => new[] { il.Create(OpCodes.Ldarg_1), il.Create(OpCodes.Call, Hook("HmmRevive.Hooks", "LogQuit")) });
        Prologue(Target("HeavyMetalMachines.ServerEmergencyQuit", "Quit"), il => new[] { il.Create(OpCodes.Ldc_I4_M1), il.Create(OpCodes.Call, Hook("HmmRevive.Hooks", "LogQuit")) });

        Prologue(Target("StudioSystemExtensions", "Initialize"), il => new[] { il.Create(OpCodes.Ldarg_0), il.Create(OpCodes.Call, Hook("HmmRevive.Hooks", "BeforeFmodInit")) });

        var open = Target("Pocketverse.NetworkClient", "OpenConnection");
        var port33000 = open.Body.Instructions.Single(i => i.OpCode == OpCodes.Ldc_I4 && (int)i.Operand == 33000);
        port33000.OpCode = OpCodes.Call;
        port33000.Operand = Hook("HmmRevive.Hooks", "ClientLocalPort");
        Console.WriteLine("  redirect -> Pocketverse.NetworkClient::OpenConnection port 33000 -> ClientLocalPort");

        // Car choice at launch (mod/HmmRevive/CarChoice.cs)
        var auth = Target("Pocketverse.AuthenticationSerializer", "SerializeAuthenticationRequest");
        var firstWriteString = auth.Body.Instructions.First(i => i.Operand is MethodReference mr && mr.Name == "Write"
            && mr.Parameters.Count == 1 && mr.Parameters[0].ParameterType.FullName == "System.String");
        auth.Body.GetILProcessor().InsertBefore(firstWriteString, Instruction.Create(OpCodes.Call, Hook("HmmRevive.CarChoice", "LoginName")));
        Console.WriteLine("  transform -> AuthenticationSerializer::SerializeAuthenticationRequest name -> CarChoice.LoginName");

        var fakeAuth = Target("Pocketverse.AuthenticationManager", "FakeAuthentication");
        Prologue(fakeAuth, il => new[]
        {
            il.Create(OpCodes.Ldarg_1),
            il.Create(OpCodes.Call, Hook("HmmRevive.CarChoice", "TakeFromLogin")),
            il.Create(OpCodes.Starg_S, fakeAuth.Parameters[0]),
        });

        var getChar = Target("HeavyMetalMachines.CharacterSelection.Server.Swordfish.SkipSwordfishServerExecuteCharacterSelection", "GetCharacterId");
        var getInt = getChar.Body.Instructions.Single(i => i.Operand is MethodReference mr && mr.Name == "GetIntValue");
        var gil = getChar.Body.GetILProcessor();
        gil.InsertBefore(getInt, gil.Create(OpCodes.Ldarg_1));
        gil.InsertBefore(getInt, gil.Create(OpCodes.Ldarg_0));
        getInt.OpCode = OpCodes.Call;
        getInt.Operand = Hook("HmmRevive.CarChoice", "CharacterIndex");
        Console.WriteLine("  redirect -> SkipSwordfishServerExecuteCharacterSelection::GetCharacterId GetIntValue -> CarChoice.CharacterIndex");

        // Skin choice at launch (CarChoice.SkinItemName): GetSkinId(player, characterId) compares configLoader.GetValue(inst)
        // with the car's skin item names.
        var getSkin = Target("HeavyMetalMachines.CharacterSelection.Server.Swordfish.SkipSwordfishServerExecuteCharacterSelection", "GetSkinId");
        var getValue = getSkin.Body.Instructions.Single(i => i.Operand is MethodReference mr && mr.Name == "GetValue");
        var sil = getSkin.Body.GetILProcessor();
        sil.InsertBefore(getValue, sil.Create(OpCodes.Ldarg_1));
        sil.InsertBefore(getValue, sil.Create(OpCodes.Ldarg_2));
        getValue.OpCode = OpCodes.Call;
        getValue.Operand = Hook("HmmRevive.CarChoice", "SkinItemName");
        Console.WriteLine("  redirect -> SkipSwordfishServerExecuteCharacterSelection::GetSkinId GetValue -> CarChoice.SkinItemName");

        // Options saved locally instead of in a Swordfish bag (mod/HmmRevive/LocalPrefs.cs)
        foreach (var name in new[] { "Save", "SaveNow" })
            Prologue(Target("HeavyMetalMachines.HMMPlayerPrefs", name), il => new[] { il.Create(OpCodes.Ldarg_0), il.Create(OpCodes.Call, Hook("HmmRevive.LocalPrefs", "Save")) });
        Prologue(Target("HeavyMetalMachines.HMMPlayerPrefs", "SkipSwordfishLoad"), il => new[] { il.Create(OpCodes.Ldarg_0), il.Create(OpCodes.Call, Hook("HmmRevive.LocalPrefs", "Load")) });
        SkipIf(Target("HeavyMetalMachines.HMMPlayerPrefs", "ExecOnPrefsWrongVersion"), Hook("HmmRevive.LocalPrefs", "SkipWrongVersionPopup"), null);

        // Mid-match car swap (mod/HmmRevive/CarSwap.cs): preload every car, "/car" commands and swap notices over chat.
        var loadAssets = module.GetType("HeavyMetalMachines.Frontend.LoadingState").NestedTypes
            .Single(t => t.Name.Contains("LoadAssetsAsync")).Methods.Single(m => m.Name == "MoveNext");
        int preCaches = 0;
        foreach (var ins in loadAssets.Body.Instructions.Where(i => i.Operand is MethodReference mr && mr.Name == "CarPreCache").ToList())
        {
            ins.Operand = Hook("HmmRevive.CarSwap", "CarPreCache");
            preCaches++;
        }
        Console.WriteLine($"  redirect -> LoadingState::LoadAssetsAsync CarPreCache x{preCaches} -> CarSwap.CarPreCache");
        SkipIf(Target("HeavyMetalMachines.HMMChat.ChatService", "ReceiveMessage"), Hook("HmmRevive.CarSwap", "ServerChat"), null, 0, 2);
        SkipIf(Target("HeavyMetalMachines.HMMChat.ChatService", "ClientReceiveMessage"), Hook("HmmRevive.CarSwap", "ClientChat"), null, 0, 2);

        // Bot difficulty per team from the launcher (Hooks.BotDifficulty): if (HasBotDifficulty(team)) return BotDifficulty(team);
        var getDiff = Target("HeavyMetalMachines.BotAI.GetBotDifficulty", "Get");
        var getDiffBody = getDiff.Body.Instructions[0];
        Prologue(getDiff, il => new[]
        {
            il.Create(OpCodes.Ldarg_1),
            il.Create(OpCodes.Call, Hook("HmmRevive.Hooks", "HasBotDifficulty")),
            il.Create(OpCodes.Brfalse, getDiffBody),
            il.Create(OpCodes.Ldarg_1),
            il.Create(OpCodes.Call, Hook("HmmRevive.Hooks", "BotDifficulty")),
            il.Create(OpCodes.Ret),
        });

        var lazy = Target("Zenject.LazyInstanceInjector", "LazyInjectAll");
        var injectCall = lazy.Body.Instructions.Single(i => (i.OpCode == OpCodes.Callvirt || i.OpCode == OpCodes.Call)
            && i.Operand is MethodReference mr && mr.Name == "Inject" && mr.DeclaringType.Name == "DiContainer");
        injectCall.OpCode = OpCodes.Call;
        injectCall.Operand = Hook("HmmRevive.Hooks", "SafeInject");
        Console.WriteLine("  redirect -> Zenject.LazyInstanceInjector::LazyInjectAll Inject -> SafeInject");

        // Optional tracing build: breadcrumb every method of selected types (HMM_TRACE=1).
        if (Environment.GetEnvironmentVariable("HMM_TRACE") == "1")
        {
            var trace = Hook("HmmRevive.Hooks", "Trace");
            string[] prefixes = (Environment.GetEnvironmentVariable("HMM_TRACE_PREFIXES") ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            int n = 0;
            foreach (var t in module.GetTypes())
            {
                if (prefixes.Length > 0 && !prefixes.Any(p => t.FullName.StartsWith(p))) continue;
                if (t.FullName.StartsWith("Pocketverse.BitLogger") || t.FullName.StartsWith("<")) continue;
                foreach (var m in t.Methods)
                {
                    if (!m.HasBody || m.Body.Instructions.Count == 0) continue;
                    if (m.IsConstructor || m.IsGetter || m.IsSetter) continue; // noise
                    var il = m.Body.GetILProcessor();
                    var first = m.Body.Instructions[0];
                    il.InsertBefore(first, il.Create(OpCodes.Ldstr, t.Name + "::" + m.Name));
                    il.InsertBefore(first, il.Create(OpCodes.Call, trace));
                    n++;
                }
            }
            Console.WriteLine($"  TRACE build: instrumented {n} methods");
        }

        if (File.Exists(output)) File.Delete(output);
        asm.Write(output);
        Console.WriteLine($"patched {TargetDll}");
    }

    static void Prologue(MethodDefinition m, Func<ILProcessor, Instruction[]> build)
    {
        var il = m.Body.GetILProcessor();
        var first = m.Body.Instructions[0];
        foreach (var ins in build(il)) il.InsertBefore(first, ins);
        Console.WriteLine($"  prologue -> {m.DeclaringType.FullName}::{m.Name}");
    }

    // if (hook(args...)) return <retValue>;   (retValue null = void method; args are argument indexes, 0 = this)
    static void SkipIf(MethodDefinition m, MethodReference hook, OpCode? retValue, params int[] args)
    {
        var il = m.Body.GetILProcessor();
        var first = m.Body.Instructions[0];
        foreach (int a in args) il.InsertBefore(first, il.Create(OpCodes.Ldarg, a == 0 && m.HasThis ? m.Body.ThisParameter : m.Parameters[m.HasThis ? a - 1 : a]));
        il.InsertBefore(first, il.Create(OpCodes.Call, hook));
        il.InsertBefore(first, il.Create(OpCodes.Brfalse, first));
        if (retValue.HasValue) il.InsertBefore(first, il.Create(retValue.Value));
        il.InsertBefore(first, il.Create(OpCodes.Ret));
        Console.WriteLine($"  skip-if {hook.Name} -> {m.DeclaringType.FullName}::{m.Name}");
    }
}
