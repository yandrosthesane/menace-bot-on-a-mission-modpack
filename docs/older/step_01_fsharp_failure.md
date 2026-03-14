# Step 1 Post-Mortem: FSharp.Core Cannot Load Under Wine/Proton

## Summary

F# compilation works. The modkit compiles `.fs` files to valid .NET DLLs. The compiled BOAM.dll (18KB) loads and runs correctly under MelonLoader. But **FSharp.Core.dll cannot be loaded** by the Windows CoreCLR 6.0.36 running under Wine/Proton. Every loading strategy fails with `BadImageFormatException: Bad format`.

This is a Wine/Proton CLR compatibility issue, not a modkit issue.

## Timeline of Attempts

### 1. Initial deployment (FSharp.Core 9.0.101, netstandard2.1)

- MCP compiled BOAM F# sources successfully
- FSharp.Core.dll copied to `Mods/BOAM/dlls/` alongside BOAM.dll
- **Result:** `Assembly.LoadFrom` fails with "Bad format"
- **Diagnosis:** Initially suspected wrong TFM (net10.0 vs net6.0)

### 2. Fixed FindFSharpCore() to use netstandard2.1 from NuGet cache

- Changed `FSharpCompilationService.FindFSharpCore()` to search `~/.nuget/packages/fsharp.core/*/lib/netstandard2.1/`
- Deployed correct netstandard2.1 FSharp.Core (2,318,136 bytes)
- **Result:** Same "Bad format" error

### 3. Skip FSharp.Core from direct loading + AssemblyResolve handler

- Modified `DllLoader.cs` to skip FSharp.Core from `Assembly.LoadFrom`
- Registered `AppDomain.CurrentDomain.AssemblyResolve` handler
- Handler uses `Assembly.LoadFile(path)` on demand
- **Result:** Handler never fires — the CLR fails before reaching it

### 4. Proactive loading via Assembly.Load(byte[])

- Read FSharp.Core.dll as byte array, call `Assembly.Load(bytes)`
- Bypasses file-path-based loading entirely
- **Result:** `"Format of the executable (.exe) or library (.dll) is invalid."` — same underlying BadImageFormatException

### 5. Strong-name stripping

- Compared PE headers: FSharp.Core has `CorFlags=0x09` (StrongNameSigned=True), working DLLs have `0x01`
- Wrote Python script to patch CorFlags, zero StrongNameSignature directory entry
- **Result:** Still "Bad format" — strong name was not the issue
- **Note:** Newtonsoft.Json.dll also has StrongName=True and loads fine from MelonLoader/net6/

### 6. Downgraded to FSharp.Core 6.0.7

- Hypothesis: FSharp.Core 9.0.x targets APIs too new for MelonLoader's net6.0
- Installed FSharp.Core 6.0.7 (3,081,384 bytes, netstandard2.1)
- **Result:** Same "Bad format" — not version-specific

### 7. Placed FSharp.Core in MelonLoader/net6/

- Copied alongside other framework DLLs that load fine (0Harmony, MelonLoader.dll, etc.)
- **Result:** Same "Bad format"

### 8. AssemblyLoadContext.Default.LoadFromAssemblyPath

- Used .NET Core's `AssemblyLoadContext` instead of legacy `Assembly.LoadFrom`
- **Result:** Same "Bad format"

### 9. Placed FSharp.Core in Wine prefix .NET runtime

- Copied to `pfx/drive_c/Program Files/dotnet/shared/Microsoft.NETCore.App/6.0.36/`
- **Result:** "File not found" (CLR doesn't probe there for mod-loaded assemblies)

### 10. ILRepack merge (FSharp.Core embedded into BOAM.dll)

- Used `dotnet-ilrepack` to merge FSharp.Core into BOAM.dll
- With `--internalize` to hide FSharp.Core types
- Produced 3MB merged DLL with no external FSharp.Core reference
- **Result:** Merged DLL also fails with "Bad format" — the FSharp.Core IL/metadata inside triggers the same rejection

### 11. F# compiler --standalone / --staticlink:FSharp.Core

- `--standalone`: fails because it pulls all transitive references (MelonLoader, Mono.Cecil, etc.) and can't inline them
- `--staticlink:FSharp.Core`: fails with `MethodDefNotFound` for `FSharpInterfaceDataVersionAttribute` — known F# compiler bug with static linking

### 12. Stripped all legacy PE artifacts

- Zeroed: entry point, import table (mscoree.dll), IAT, relocation table, strong name
- Based on dotnet/runtime#63639 where legacy PE structures caused BadImageFormatException
- **Result:** Still "Bad format" — PE headers are not the issue

## Root Cause Analysis

### What we know

- BOAM.dll (18KB, compiled by F# compiler, references FSharp.Core) loads fine
- FSharp.Core.dll (any version, any loading method) always fails
- Other large DLLs load fine (MelonLoader.dll = 2MB, Assembly-CSharp = 6.7MB)
- All assemblies have identical PE structure (PE32, i386, same sections, same relocation types)
- The Windows CoreCLR 6.0.36 runs inside Wine/Proton (installed at `pfx/drive_c/Program Files/dotnet/`)

### What we conclude

The failure is in .NET **metadata table parsing**, not PE headers. FSharp.Core.dll contains metadata structures (likely related to F# compiler-generated attributes, generic type definitions, or inlined IL) that Wine's implementation of the Windows CoreCLR cannot process. This is a Wine/Proton bug.

Evidence: Even when FSharp.Core types are merged into BOAM.dll via ILRepack, the resulting DLL fails — the problematic metadata moves with the types.

### Community context

- [gilzoide/unity-fsharp](https://github.com/gilzoide/unity-fsharp) and other F#-in-Unity projects all ship FSharp.Core.dll alongside and rely on the runtime loading it normally — this works on native Windows but not Wine
- No documented cases of FSharp.Core working under Wine/Proton with MelonLoader or similar mod loaders
- [dotnet/runtime#63639](https://github.com/dotnet/runtime/issues/63639) documents similar BadImageFormatException from metadata issues, but the specific FSharp.Core + Wine combination appears to be unreported

## Decision

**F# cannot run inside MelonLoader under Wine/Proton.** Rather than abandoning F#, we pivot to a **sidecar architecture**: the F# graph engine runs as a separate native Linux process, communicating with a thin C# plugin inside MelonLoader via IPC. See `sidecar_architecture.md`.

## Files Modified During These Attempts

| File | Changes | Status |
|------|---------|--------|
| `MenaceAssetPacker/src/Menace.Modkit.App/Services/FSharpCompilationService.cs` | FindFSharpCore() NuGet search, StripStrongName(), --standalone/--staticlink attempts | Needs cleanup |
| `MenaceAssetPacker/src/Menace.ModpackLoader/DllLoader.cs` | AssemblyResolve handler, dependency skip logic, AssemblyLoadContext | Needs revert to clean state |
| `BOAM-modpack/src/Domain.fs` | F# domain types (working, kept) | Keep |
| `BOAM-modpack/src/BoamPlugin.fs` | F# plugin entry point (working, kept) | Keep |
