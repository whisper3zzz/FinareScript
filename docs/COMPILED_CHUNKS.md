# UnifiedScript compiled chunks

`.uschunk` is the deterministic delivery container for a compiled
UnifiedScript program. It is analogous to the packaging role of a Lua chunk,
but it does **not** introduce another interpreter or bytecode virtual machine.
Restricted C# still compiles to deterministic PE/IL and executes on CoreCLR;
the chunk removes loose-file discovery and JSON framing from the shipping load
path while preserving the existing collectible-generation model.

## Why JSON is not the frame-time problem

The current gameplay hot path does not evaluate JSON. Managed gameplay runs
compiled IL and reaches Unreal through generated ABI thunks, shared snapshots,
and command/event batches. JSON currently owns authoring, generated-contract,
declaration, and diagnostic artifacts. Its cost is concentrated in editor
generation, World start, and reload.

The chunk therefore targets startup, reload, cook/stage integrity, deterministic
caching, and file-count overhead. It must not replace the existing fast ABI or
create a second runtime whose semantics can drift from C# and the visual graph.

## v0.1 binary envelope

All integers are unsigned little-endian. The canonical layout is:

```text
96-byte header
64-byte section directory entries, sorted by numeric section kind
zero padding to a 16-byte boundary
section payloads, each starting on a 16-byte boundary
```

The header contains `USCHUNK\0`, format `1.0`, fixed header/directory sizes,
section count, canonical directory/payload/file offsets, and a SHA-256 digest of
the entire file with the digest field zeroed. Each directory entry contains a
section kind, flags, offset, stored/logical lengths, and its own SHA-256 digest.
v0.1 stores sections without compression; stored and logical lengths must be
identical.

The reader rejects unknown format versions, non-canonical ordering or padding,
duplicates, unsupported flags, zero-length sections, overflow/out-of-bounds
ranges, trailing data, and whole-file or per-section digest mismatches before a
payload is handed to CoreCLR or Unreal.

## Program profile

A runtime program chunk requires five independently versioned binary payloads:

| Section | Purpose |
| --- | --- |
| `RuntimeManifest` | program identity, entry types, compatibility and payload-codec versions |
| `ManagedAssembly` | the existing deterministic CoreCLR PE/IL image |
| `DeclarationData` | runtime state defaults and stable IDs |
| `BindingManifest` | required generated ABI/schema identities and capability bounds |
| `DependencyManifest` | assembly/native runtime dependencies and compatible versions |

These sections must carry `RuntimeRequired`. PDB, source map, authoring surface,
identity, HIR, semantic, and Script Asset source artifacts have dedicated
debug/editor section kinds and cannot also be marked runtime-required. Shipping
will normally place those payloads in an optional `.uschunk.debug` sidecar or
omit them.

The envelope keeps section framing independent from payload schemas. The four
metadata payloads now use deterministic versioned binary codecs, while the PE
section contains the deterministic managed assembly directly. JSON remains the
canonical readable authoring/import/export format; the World runtime consumes
the compiled binary payloads and does not parse declaration JSON when a chunk
is configured.

## Current tooling

Pack already-produced runtime artifacts deterministically:

```powershell
dotnet run --project src/UnifiedScript.Cli -- chunk-pack `
  --out Game.uschunk `
  --runtime-manifest Game.runtime.bin `
  --assembly Game.dll `
  --declaration Game.declarations.bin `
  --bindings Game.bindings.bin `
  --dependencies Game.dependencies.bin `
  --pdb Game.pdb `
  --source-map Game.sourcemap.bin
```

Inspect and verify the envelope before staging it:

```powershell
dotnet run --project src/UnifiedScript.Cli -- chunk-inspect Game.uschunk
```

`chunk-pack` writes transactionally. Repacking identical payload bytes in any
input order produces byte-for-byte identical output and the same content
digest. `chunk-inspect` applies the strict runtime-program profile and only then
emits a JSON tooling report; that report is not consumed by the game runtime.

The normal compiler path does not require manually preparing those payloads.
`unifiedscript emit` writes the loose migration artifacts and a sibling
`.uschunk` from the same semantic program. Debug emission embeds the portable
PDB section; Release emission omits it from the runtime chunk while retaining
the sibling loose PDB for diagnostics. The Unreal demo helper builds and stages
that product artifact:

```powershell
.\tools\emit-unreal-demo-chunk.ps1 -EngineRoot D:\UE_5.8 -Configuration Release
```

Managed hosts verify the complete candidate and load PE/PDB streams directly
into a collectible `AssemblyLoadContext`; no temporary DLL extraction occurs.
The native World host independently verifies the envelope and declaration
section before materializing the Unreal PropertyBag. Script Assets prefer
`CompiledChunkPath` and retain `ScriptAssemblyPath` plus `DeclarationPath` as a
migration fallback.

## Integration sequence

1. **Complete:** define deterministic binary codecs for runtime manifest, declarations,
   bindings, and dependencies, plus a compiler command that consumes the same
   semantic program used by C# and the visual graph.
2. **Complete:** add managed chunk loading directly from verified PE/PDB slices and native
   Unreal section access without extracting temporary loose files.
3. **Complete:** add `CompiledChunkPath` to Script Assets while retaining loose `ScriptAssemblyPath` and
   declaration fallback for migration.
4. **Complete:** make reload verify a complete candidate chunk, construct the next
   collectible generation, and swap only after declaration and ABI validation;
   failures keep the previous generation.
5. **Complete:** stage runtime chunks in cook/package, keep debug/editor payloads optional,
   and measure startup, reload, package size, and loose-path equivalence.

The editor/World vertical slice is complete: UE 5.8 Automation starts and
reloads the demo from one chunk while materializing state with no loose
declaration path. A strict deployment commandlet validates all runtime sections
before updating Script Assets, and `-UnifiedScriptDisableCompiledChunks`
selects the loose DLL/JSON compatibility path for migration and A/B evidence.

## M11.4 phased load profiling

`chunk-profile` measures the current loader before v2 changes its storage
model. It writes a versioned report without embedding the input path or a wall
clock timestamp:

```powershell
dotnet UnifiedScript.Cli.dll chunk-profile Game.uschunk `
  --out Game.chunk-profile.json `
  --trials 5 `
  --reference UnifiedScript.Unreal.dll
```

Each trial opens a fresh stream and collectible load context. Trial one is
marked as the process-first load; later trials expose the same-process warm
baseline. The report separates file open/read, the two current whole-file
materializations, header and directory validation, whole-file and per-section
SHA-256, program-profile checks, four binary metadata decoders, identity and
dependency checks, load-context construction, PE/PDB materialization, CoreCLR
assembly load, entry-type validation, JIT preparation, and unload request.
Every phase carries nanoseconds plus any bytes read, bytes copied, or methods
prepared, and the report includes per-phase medians without hiding trial data.
Dependency validation additionally reports exact cache hit/miss counts and
miss-side snapshot bytes read/copied per trial, so warm results cannot be
confused with a process-first integrity check or hide retained cache memory.

JIT preparation uses `RuntimeHelpers.PrepareMethod` over concrete methods
declared by chunk entry types. It is a controlled first-code-generation probe,
not a substitute for the later matched World-level first Tick measurement. Use
`--no-jit-prepare` when profiling loader-only cost. The current profile
intentionally accounts for both whole-file copies and PE/PDB copies so the v2
memory-mapped reader must demonstrate that it actually removes them.

For a cold-process distribution, use the repository runner. It launches a new
CLI process for every one-trial report, requires a stable chunk identity, and
publishes the individual reports plus one median suite:

```powershell
.\tools\measure-compiled-chunk-load.ps1 `
  -ChunkPath Game.uschunk `
  -ReferencePath UnifiedScript.Unreal.dll `
  -Trials 5
```

The runner does not flush the operating-system file cache, so it describes
process-cold CoreCLR and loader behavior under normal repeated development or
server startup conditions, not worst-case physical-disk latency.

The first M11.4 baseline profiled a freshly emitted 84,145-byte Release Demo
chunk in five independent .NET 10.0.203 processes. Median load was 49.4572 ms;
load plus explicit entry-method JIT preparation was 59.4990 ms. The largest
median phases were dependency validation/hash at 10.5419 ms, runtime-manifest
decode and first-use code at 8.1901 ms, entry-type validation at 7.3501 ms,
whole-file SHA-256 at 6.3039 ms, and 12-method JIT preparation at 6.2841 ms.

The same report accounts for 250,722 copied bytes per trial: two 84,145-byte
whole-file materializations plus the 82,432-byte PE image. Their three median
materialization times total only 0.1024 ms. For this representative image,
memory mapping cannot close the startup gap by itself. The measured next
priority is a content-identity keyed dependency-verification cache followed by
a controlled ReadyToRun/prewarming experiment. Zero-copy section views remain
valuable for memory scaling and larger chunks, but will be promoted only when
the large-project profile demonstrates a material latency or allocation win.
Evidence is
`artifacts/chunk-profile/m11.4-demo-process-v2/process-profile.json`.
Two 180-payload M11.4 SDK archives reproduced SHA-256
`f5fb0a649fed106c73462a6cf24f44d90c3c914e39d57120f6a78531a4788403`;
the extracted SDK's packaged runner then completed three isolated trials using
the packaged CLI, not a source checkout.

## M11.4 verified dependency snapshot cache

CoreCLR and Unreal World hosts now share one content-addressed managed
dependency cache across collectible script generations. A miss opens the file
with writes excluded, reads it once into a bounded byte array, verifies the
exact SHA-256 identity, and reads assembly name, version, and module MVID from
CLR metadata. A dependency already loaded in the default context must also
match that verified MVID; the process permanently binds that shared assembly
instance to its first accepted content digest. This prevents a later reload
from claiming different bytes for an assembly that CoreCLR cannot replace.

The verified immutable byte snapshot, rather than the path that was checked,
is passed to a collectible `AssemblyLoadContext`. Consequently there is no
check-file/load-file race. A later request for the same manifest digest may be
satisfied from the cached snapshot even if the path changed after the first
load; clearing cached content forces disk evidence to be read again, while the
non-replaceable shared-assembly binding remains enforced.

The process cache defaults to 128 entries and 256 MiB, uses deterministic LRU
eviction, and refuses an individual dependency larger than 512 MiB. An image
larger than the configured cache budget is still fully verified and used for
that load but is not retained. Verification is single-flight under concurrent
World loads, so one digest produces one miss and subsequent consumers reuse the
same immutable snapshot. The profiler exposes hit/miss counts alongside the
dependency phase.

On the same six-dependency 84,145-byte Release Demo chunk, 15 trials produced
six misses in the first trial and six hits in every later trial. The 14 warm
trials reduced dependency-validation median from the earlier 3.0522 ms baseline
to 0.1338 ms, while complete warm load fell from 4.3903 ms to 1.0748 ms. Five
independent processes still performed six misses each and reported a 45.5446 ms
cold-load median versus the earlier 49.4572 ms baseline, confirming that a new
process continues to perform full integrity and identity verification. Each
cold trial now explicitly accounts for 2,507,264 dependency snapshot bytes and
2,757,986 total copied bytes. Evidence is
`artifacts/chunk-profile/m11.4-dependency-cache-warm-v3.json` and
`artifacts/chunk-profile/m11.4-dependency-cache-process-v2/process-profile.json`.
The next measured experiment is ReadyToRun/prewarming; the current copy timings
still do not justify promoting memory mapping ahead of first-code-generation
cost.

The installed UE 5.8.1 WorldHost Lifecycle Automation also passed the compiled
chunk Start/Tick/reload/Stop path with generation 3 and zero legacy calls. Two
SDK builds each contained 179 manifest-listed payloads plus the manifest and
reproduced archive SHA-256
`0e969ad5a81f591de21ab4d4fc9f8f6555dd6563d755b95ad3ff0f95eb2c6c92`.
After extraction, the packaged profiler completed three independent cold
process trials against the same chunk without a source checkout.

## Measured BlockBreaker impact

`comparison/measure-blockbreaker-chunk-delivery.ps1` runs alternating cold
processes and requires the same 100 measured frames, 30 warmup frames, shot,
hit, command completions, ABI v3 calls, zero legacy calls, and committed reload
on both paths. The 2026-08-28 three-trial median is:

| Metric | Release chunk | Loose compatibility | Chunk / loose |
| --- | ---: | ---: | ---: |
| Actor-tree cold startup | 162.0194 ms | 110.3650 ms | 1.468032 |
| Transactional reload | 75.6999 ms | 79.1182 ms | 0.956795 |
| Managed generation load inside reload | 2.4490 ms | 1.6900 ms | 1.449112 |
| Runtime program delivery | 1 file / 44,886 B | 2 files / 43,992 B | 1.020322 bytes |

The chunk removes runtime declaration JSON and consolidates delivery, but its
first-use verification and binary-codec JIT currently add cold startup cost.
That cost is reported rather than hidden and is routed to M11.4 for measured
zero-copy section access, verification caching, and prewarming/ReadyToRun
evaluation; it is not a frame-time cost. M11.4 keeps CoreCLR PE/IL as executable
code instead of adding a second custom bytecode VM. The packaged BlockBreaker
gate proves both Player and Rifle start from the staged chunk for 300 frames,
and the generic Development release gate independently proves packaged chunk
startup.

The M11.1 large-project gate also compiles 256 independent Script Assets into
one 141,249-byte Release chunk. Its verified cold emit is 5,398.7625 ms and a
single-source default edit rebuilds the assembly and chunk in 5,123.4274 ms;
the resulting 256-entry deployment validates in a cold UE 5.8.1 process. See
`docs/LARGE_PROJECT_SCALE.md` for the workload, budgets, and artifact path.
