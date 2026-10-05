// Game builds the patcher knows, and the patch for the older ones (Unity 4.6.8, mod/HmmRevive.Legacy).
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Mono.Cecil;
using Mono.Cecil.Cil;

static class Builds
{
    public const string Steam = "steam", Unsupported = "unsupported";

    // sha256 prefix of the original HMM_Data/Managed/Assembly-CSharp-firstpass.dll -> build id. Keep in sync with
    // tools/Launcher (GameCopies.Known). The 2016 build is recognized but not supported yet.
    static readonly Dictionary<string, string> Known = new()
    {
        ["0c5527141b81820e"] = "2017",
        ["6b253add011d2318"] = "2017sep", // September 2017 (experimental)
        ["1c5bd4c1219a9229"] = Unsupported, // 2016: planned
    };

    public static string Hash(string game)
    {
        using var s = File.OpenRead(Path.Combine(game, "HMM_Data", "Managed", "Assembly-CSharp-firstpass.dll"));
        return Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant();
    }

    // Anything not in the table is treated as the current build, as before builds were told apart.
    public static string Detect(string game)
    {
        string hash = Hash(game);
        foreach (var (prefix, id) in Known)
            if (hash.StartsWith(prefix)) return id;
        return Steam;
    }
}

static class Legacy
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CreateHardLink(string newFile, string existingFile, IntPtr sa);

    const string TargetDll = "Assembly-CSharp-firstpass.dll", ModDll = "HmmReviveLegacy.dll", SteamDll = "Steamworks.NET.dll";

    public static int Run(string build, string game, string inst, string modRoot)
    {
        string managedRel = Path.Combine("HMM_Data", "Managed");
        // The kit keeps each old build's mod in mod/<build>/.
        string mod = File.Exists(Path.Combine(modRoot, build, ModDll)) ? Path.Combine(modRoot, build) : modRoot;
        if (!File.Exists(Path.Combine(mod, ModDll)))
        {
            Console.Error.WriteLine($"{ModDll} for this game build is missing (looked in {Path.Combine(modRoot, build)})");
            return 4;
        }
        if (File.Exists(Path.Combine(game, managedRel, ModDll)))
        {
            Console.Error.WriteLine("That's an HMM Revive copy of the game. Choose the original game folder.");
            return 5;
        }

        int linked = 0, copied = 0, repaired = 0;
        foreach (string src in Directory.EnumerateFiles(game, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(game, src);
            if (rel.StartsWith("logs" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
            if (rel.Equals(Path.Combine(managedRel, TargetDll), StringComparison.OrdinalIgnoreCase)) continue;
            if (rel.Equals(Path.Combine(managedRel, SteamDll), StringComparison.OrdinalIgnoreCase)) continue;
            string dst = Path.Combine(inst, rel);
            long declared = BundleLength(src);
            if (declared > 0)
            {
                // Over-long asset bundle: Unity 4 fails on the extra bytes ("Bad file length") and crashes while loading a
                // match. The instance gets a copy cut to the length in the bundle's own header.
                if (File.Exists(dst) && new FileInfo(dst).Length == declared) continue;
                if (File.Exists(dst)) File.Delete(dst); // never write through a hardlink
                Directory.CreateDirectory(Path.GetDirectoryName(dst));
                using (var from = File.OpenRead(src))
                using (var to = File.Create(dst))
                {
                    byte[] buf = new byte[1 << 20];
                    for (long left = declared; left > 0;)
                    {
                        int n = from.Read(buf, 0, (int)Math.Min(buf.Length, left));
                        if (n <= 0) throw new IOException("short read in " + rel);
                        to.Write(buf, 0, n);
                        left -= n;
                    }
                }
                repaired++;
                continue;
            }
            if (File.Exists(dst)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(dst));
            if (CreateHardLink(dst, src, IntPtr.Zero)) { linked++; continue; }
            int err = Marshal.GetLastWin32Error();
            if (err != 17) // ERROR_NOT_SAME_DEVICE: instance on another drive than the game, so copy (uses disk space)
                throw new IOException($"CreateHardLink failed ({err}) for {rel}");
            if (copied++ == 0) Console.WriteLine("instance is on another drive than the game: copying files instead of hardlinking (~7 GB)...");
            File.Copy(src, dst);
        }
        Console.WriteLine($"hardlinked {linked} new files, copied {copied}, repaired {repaired} asset bundles");

        string managed = Path.Combine(inst, managedRel);
        foreach (string f in Directory.EnumerateFiles(mod))
        {
            string dst = Path.Combine(managed, Path.GetFileName(f));
            if (File.Exists(dst)) File.Delete(dst);
            File.Copy(f, dst);
            Console.WriteLine($"copied mod {Path.GetFileName(f)}");
        }

        Patch(Path.Combine(game, managedRel, TargetDll), Path.Combine(managed, TargetDll), Path.Combine(managed, ModDll));
        SteamOff(Path.Combine(game, managedRel, SteamDll), Path.Combine(managed, SteamDll));
        return 0;
    }

    /// <summary>
    /// Old builds call Steam at startup on the client and the server (ClientAPI's SteamClient: SteamAPI.Init, then
    /// SteamUser.GetSteamID on a null interface when Steam isn't running). Many players have these copies without Steam,
    /// so every call into Steam's native code (CSteamworks.dll, sdkencryptedappticket.dll) is replaced with "no
    /// Steam": arguments dropped, the default value returned (false, 0, null). Steam then never loads in the game, open
    /// or not. Only friends, overlay, billing and groups use it, none of which exist offline.
    /// </summary>
    static void SteamOff(string original, string output)
    {
        if (!File.Exists(original)) return;
        var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(Path.GetDirectoryName(original));
        using var asm = AssemblyDefinition.ReadAssembly(original, new ReaderParameters { AssemblyResolver = resolver, ReadingMode = ReadingMode.Immediate });
        // InteropHelp.TestIfAvailableClient/GameServer throw "Steamworks is not initialized." before every call; without
        // Steam that killed Hoplon's connection setup and the game hung on its splash screen.
        foreach (var check in asm.MainModule.GetType("Steamworks.InteropHelp").Methods.Where(m => m.Name.StartsWith("TestIfAvailable") && m.HasBody))
        {
            check.Body.Instructions.Clear();
            check.Body.ExceptionHandlers.Clear();
            check.Body.GetILProcessor().Emit(OpCodes.Ret);
        }
        int calls = 0;
        foreach (var type in asm.MainModule.GetTypes())
            foreach (var m in type.Methods.Where(m => m.HasBody))
            {
                var steamCalls = m.Body.Instructions.Where(i => i.Operand is MethodDefinition d && d.IsPInvokeImpl
                    && !d.PInvokeInfo.Module.Name.StartsWith("kernel32", StringComparison.OrdinalIgnoreCase)).ToList();
                if (steamCalls.Count == 0) continue;
                var il = m.Body.GetILProcessor();
                foreach (var call in steamCalls)
                {
                    var target = (MethodDefinition)call.Operand;
                    // pop every argument (static P/Invokes: no this), then push the return type's default from a fresh
                    // local. The call instruction becomes the first of these, so branches to it still land right.
                    var seq = target.Parameters.Select(_ => il.Create(OpCodes.Pop)).ToList();
                    if (target.ReturnType.MetadataType == MetadataType.Void) seq.Add(il.Create(OpCodes.Nop));
                    else
                    {
                        var local = new VariableDefinition(target.ReturnType);
                        m.Body.Variables.Add(local);
                        m.Body.InitLocals = true;
                        seq.Add(il.Create(OpCodes.Ldloc, local));
                    }
                    call.OpCode = seq[0].OpCode; call.Operand = seq[0].Operand;
                    var last = call;
                    foreach (var ins in seq.Skip(1)) { il.InsertAfter(last, ins); last = ins; }
                    calls++;
                }
            }
        if (File.Exists(output)) File.Delete(output); // never write through a hardlink
        asm.Write(output);
        Console.WriteLine($"patched {SteamDll}: {calls} Steam calls off (the game runs without Steam)");
    }

    /// <summary>
    /// For an asset bundle (Content/**/*.hmm, .scn, .shr) that is longer than its header says: the declared length.
    /// 0 when the file is fine or isn't such a bundle. Header (big-endian): signature "UnityRaw"/"UnityWeb" + NUL,
    /// uint32 format version (3), player version + NUL, engine version + NUL, uint32 minimumStreamedBytes, uint32
    /// headerSize, uint32 levelsBeforeStreaming, int32 levelCount, levelCount x (uint32 compressed, uint32 uncompressed),
    /// uint32 completeFileSize, ...
    /// </summary>
    public static long BundleLength(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext != ".hmm" && ext != ".scn" && ext != ".shr") return 0;
        if (path.IndexOf(Path.DirectorySeparatorChar + "Content" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) < 0) return 0;
        using var s = File.OpenRead(path);
        using var r = new BinaryReader(s);
        string sig = CString(r);
        if (sig != "UnityRaw" && sig != "UnityWeb") return 0;
        if (U32(r) != 3) return 0;
        CString(r);
        CString(r);
        U32(r); U32(r); U32(r);
        uint levels = U32(r);
        if (levels < 1 || levels > 4096) return 0;
        s.Seek(8L * levels, SeekOrigin.Current);
        long complete = U32(r);
        return complete > 0 && complete < s.Length ? complete : 0;
    }

    static string CString(BinaryReader r)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < 64; i++)
        {
            byte b = r.ReadByte();
            if (b == 0) break;
            sb.Append((char)b);
        }
        return sb.ToString();
    }

    static uint U32(BinaryReader r)
    {
        byte[] b = r.ReadBytes(4);
        if (b.Length < 4) throw new EndOfStreamException();
        return (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]);
    }

    // --- patch table (keep in sync with mod/HmmRevive.Legacy/Hooks.cs) ---
    static void Patch(string original, string output, string modDll)
    {
        var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(Path.GetDirectoryName(original));
        var rp = new ReaderParameters { AssemblyResolver = resolver, ReadingMode = ReadingMode.Immediate };
        using var asm = AssemblyDefinition.ReadAssembly(original, rp);
        using var modAsm = AssemblyDefinition.ReadAssembly(modDll, rp);
        var module = asm.MainModule;
        const string H = "HmmRevive.Legacy.Hooks";

        MethodReference Hook(string name) =>
            module.ImportReference(modAsm.MainModule.GetType(H)?.Methods.Single(m => m.Name == name)
                                   ?? throw new InvalidOperationException($"mod type not found: {H}"));
        MethodDefinition Target(string type, string name) =>
            module.GetType(type)?.Methods.Single(m => m.Name == name && m.HasBody)
            ?? throw new InvalidOperationException($"type not found: {type}");

        foreach (var (type, name) in new[] { ("HeavyMetalMachines.HMMHub", "Awake"), ("Pocketverse.StateMachine", "Awake") })
            Prologue(Target(type, name), il => new[] { il.Create(OpCodes.Ldarg_0), il.Create(OpCodes.Call, Hook("BeforeAwake")) });
        Prologue(Target("HeavyMetalMachines.HMMHub", "Start"), il => new[] { il.Create(OpCodes.Ldarg_0), il.Create(OpCodes.Call, Hook("HubStart")) });
        Prologue(Target("Pocketverse.GameState", "EnableState"), il => new[] { il.Create(OpCodes.Ldarg_0), il.Create(OpCodes.Call, Hook("StateEnabled")) });
        Prologue(Target("HeavyMetalMachines.HMMHub", "Quit"), il => new[] { il.Create(OpCodes.Call, Hook("LogQuit")) });

        // Mute: FMOD_StudioSystem.Init calls setAdvancedSettings before initializing the audio device.
        var fmodInit = Target("FMOD_StudioSystem", "Init");
        var fmodCalls = fmodInit.Body.Instructions.Where(i => i.Operand is MethodReference mr && mr.Name == "setAdvancedSettings").ToList();
        if (fmodCalls.Count == 0) throw new InvalidOperationException("setAdvancedSettings not found in FMOD_StudioSystem.Init");
        foreach (var ins in fmodCalls) { ins.OpCode = OpCodes.Call; ins.Operand = Hook("SetAdvancedSettings"); }
        Console.WriteLine($"  redirect -> FMOD_StudioSystem::Init setAdvancedSettings x{fmodCalls.Count} -> SetAdvancedSettings");

        // Team choice: the client adds "#team" to the name it logs in with, the server takes it off in FakeRequest.
        var send = Target("Pocketverse.NetworkClient", "SendAuthenticateMessage");
        var names = send.Body.Instructions.Where(i =>
            (i.Operand is MethodReference mr && mr.Name == "get_Name" && mr.DeclaringType.Name == "Player") ||
            (i.Operand is FieldReference fr && fr.Name == "Name" && fr.DeclaringType.Name == "Player")).ToList();
        if (names.Count == 0) throw new InvalidOperationException("player name not found in NetworkClient.SendAuthenticateMessage");
        var sil = send.Body.GetILProcessor();
        foreach (var ins in names) sil.InsertAfter(ins, sil.Create(OpCodes.Call, Hook("LoginName")));
        Console.WriteLine($"  transform -> NetworkClient::SendAuthenticateMessage name x{names.Count} -> LoginName");
        var fake = Target("Pocketverse.AuthenticationManager", "FakeRequest");
        Prologue(fake, il => new[]
        {
            il.Create(OpCodes.Ldarg_0),
            il.Create(OpCodes.Ldarg_1),
            il.Create(OpCodes.Call, Hook("TakeTeam")),
            il.Create(OpCodes.Starg_S, fake.Parameters[0]),
        });

        // Hoplon's native particle renderer (NativeRendering.dll) only knows Direct3D 9 and crashes in its per-frame update
        // on anything else (tried -force-d3d11 / -force-opengl against the untextured cars: no crash with this, but the
        // game's shaders are Direct3D 9 only, so everything turns magenta). Also logs the graphics API.
        // if (NativeRenderOff()) return;
        var late = module.GetType("NativePlugins")?.Methods.SingleOrDefault(m => m.Name == "LateUpdate" && m.HasBody);
        if (late != null)
        {
            var lateBody = late.Body.Instructions[0];
            Prologue(late, il => new[] { il.Create(OpCodes.Call, Hook("NativeRenderOff")), il.Create(OpCodes.Brfalse, lateBody), il.Create(OpCodes.Ret) });
        }

        // Bot level per team (2017): if (HasBotDifficulty(team)) return BotDifficulty(team);
        var getDiff = module.GetType("HeavyMetalMachines.MatchPlayers")?.Methods.SingleOrDefault(m => m.Name == "GetBotDifficulty" && m.HasBody);
        if (getDiff != null)
        {
            var body = getDiff.Body.Instructions[0];
            Prologue(getDiff, il => new[]
            {
                il.Create(OpCodes.Ldarg_1),
                il.Create(OpCodes.Call, Hook("HasBotDifficulty")),
                il.Create(OpCodes.Brfalse, body),
                il.Create(OpCodes.Ldarg_1),
                il.Create(OpCodes.Call, Hook("BotDifficulty")),
                il.Create(OpCodes.Ret),
            });
        }

        // Ball speed and /ballspeed (mod/HmmRevive.Legacy/Ball.cs). Each piece is skipped, with a message, if a build lacks it.
        MethodReference Ball(string name) =>
            module.ImportReference(modAsm.MainModule.GetType("HmmRevive.Legacy.Ball").Methods.Single(m => m.Name == name));
        var applyDrag = module.GetType("HeavyMetalMachines.Combat.CombatMovement")?.Methods.SingleOrDefault(m => m.Name == "ApplyDrag" && m.HasBody);
        var getDrag = applyDrag?.Body.Instructions.SingleOrDefault(i => i.Operand is MethodReference mr && mr.Name == "GetDrag");
        if (getDrag != null)
        {
            var dil = applyDrag.Body.GetILProcessor();
            dil.InsertAfter(getDrag, dil.Create(OpCodes.Call, Ball("Drag")));
            dil.InsertAfter(getDrag, dil.Create(OpCodes.Ldarg_0));
            Console.WriteLine("  transform -> CombatMovement::ApplyDrag GetDrag -> Ball.Drag");
        }
        else Console.WriteLine("  (no CombatMovement.ApplyDrag/GetDrag: ball drag unchanged)");
        var moveFixed = module.GetType("HeavyMetalMachines.Combat.CombatMovement")?.Methods.SingleOrDefault(m => m.Name == "MovementFixedUpdate" && m.HasBody);
        if (moveFixed != null) Prologue(moveFixed, il => new[] { il.Create(OpCodes.Ldarg_0), il.Create(OpCodes.Call, Ball("FixedUpdate")) });
        else Console.WriteLine("  (no CombatMovement.MovementFixedUpdate: ball release speed unchanged)");
        var impulse = module.GetType("HeavyMetalMachines.Combat.CombatController")?.Methods.Where(m => m.HasBody)
            .SelectMany(m => m.Body.Instructions).Where(i => i.Operand is MethodReference mr && mr.Name == "Push" && mr.DeclaringType.Name == "CombatMovement").ToList();
        if (impulse != null && impulse.Count == 1)
        {
            impulse[0].OpCode = OpCodes.Call;
            impulse[0].Operand = Ball("Push");
            Console.WriteLine("  redirect -> CombatController impulse Movement.Push -> Ball.Push");
        }
        else Console.WriteLine($"  (CombatController has {impulse?.Count ?? 0} Movement.Push calls: ball impulses unchanged)");
        foreach (var (name, hook) in new[] { ("ReceiveMessage", "ServerChat"), ("ClientReceiveMessage", "ClientChat") })
        {
            var chat = module.GetType("HeavyMetalMachines.HMMChat.ChatService")?.Methods.SingleOrDefault(m => m.Name == name && m.HasBody);
            if (chat == null) { Console.WriteLine($"  (no ChatService.{name}: no /ballspeed)"); continue; }
            var first = chat.Body.Instructions[0];
            var cil = chat.Body.GetILProcessor();
            cil.InsertBefore(first, cil.Create(OpCodes.Ldarg_0));
            cil.InsertBefore(first, cil.Create(OpCodes.Ldarg_2));
            cil.InsertBefore(first, cil.Create(OpCodes.Call, Ball(hook)));
            cil.InsertBefore(first, cil.Create(OpCodes.Brfalse, first));
            cil.InsertBefore(first, cil.Create(OpCodes.Ret));
            Console.WriteLine($"  skip-if {hook} -> ChatService::{name}");
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
}
