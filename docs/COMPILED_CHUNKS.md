# Finare compiled chunks

`.uschunk` is the deterministic delivery container for a compiled
Finare program. It is analogous to the packaging role of a Lua chunk,
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
dotnet run --project src/Finare.Cli -- chunk-pack `
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
dotnet run --project src/Finare.Cli -- chunk-inspect Game.uschunk
```

`chunk-pack` writes transactionally. Repacking identical payload bytes in any
input order produces byte-for-byte identical output and the same content
digest. `chunk-inspect` applies the strict runtime-program profile and only then
emits a JSON tooling report; that report is not consumed by the game runtime.

The normal compiler path does not require manually preparing those payloads.
`finare emit` writes the loose migration artifacts and a sibling
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
before updating Script Assets, and `-FinareDisableCompiledChunks`
selects the loose DLL/JSON compatibility path for migration and A/B evidence.

## M11.4 phased load profiling

`chunk-profile` measures the current loader before v2 changes its storage
model. It writes a versioned report without embedding the input path or a wall
clock timestamp:

```powershell
dotnet Finare.Cli.dll chunk-profile Game.uschunk `
  --out Game.chunk-profile.json `
  --trials 5 `
  --reference Finare.Unreal.dll
```

Each trial opens a fresh stream and collectible load context. Trial one is
marked as the process-first load; later trials expose the same-process warm
baseline. The current seekable-stream reader owns one exact-length whole-file
materialization instead of copying through an intermediate `MemoryStream`.
The report separates file open/read, that owned materialization, header and
directory validation, whole-file and per-section
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
intentionally accounts for the owned whole-file copy and PE/PDB copies so any
future memory-mapped reader must demonstrate that it actually removes them.

For a cold-process distribution, use the repository runner. It launches a new
CLI process for every one-trial report, requires a stable chunk identity, and
publishes the individual reports plus one median suite:

```powershell
.\tools\measure-compiled-chunk-load.ps1 `
  -ChunkPath Game.uschunk `
  -ReferencePath Finare.Unreal.dll `
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
that load but is not retained. Published cache updates are atomic under
concurrent World loads. File read, hash, and PE identity work occurs outside
the cache lock, so background prewarm cannot block activation on its I/O;
concurrent contenders converge on one immutable snapshot and one published
miss. The profiler exposes hit/miss counts alongside the dependency phase.

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
The current copy timings still do not justify promoting memory mapping ahead
of first-code-generation cost; the following ReadyToRun experiment tests that
cost directly.

The installed UE 5.8.1 WorldHost Lifecycle Automation also passed the compiled
chunk Start/Tick/reload/Stop path with generation 3 and zero legacy calls. Two
SDK builds each contained 179 manifest-listed payloads plus the manifest and
reproduced archive SHA-256
`0e969ad5a81f591de21ab4d4fc9f8f6555dd6563d755b95ad3ff0f95eb2c6c92`.
After extraction, the packaged profiler completed three independent cold
process trials against the same chunk without a source checkout.

## M11.4 ReadyToRun decision

`tools/measure-readytorun-chunk.ps1` extracts the exact IL assembly from a
verified Release chunk, runs the Crossgen2 version matching the installed UE
.NET runtime, and requires the R2R output to preserve assembly name, version,
and MVID. It then repacks only the managed assembly section, launches matched
independent process distributions for IL and R2R, and publishes
`unifiedscript.readytorun-experiment/v0.1`. The decision gate requires at least
5% lower cold load, at least 5% lower entry-method preparation, and no more than
1.5x chunk growth; completing a valid experiment is a pass even when evidence
rejects promotion.

```powershell
.\tools\measure-readytorun-chunk.ps1 `
  -ChunkPath Game.uschunk `
  -Crossgen2Path $crossgen2 `
  -ReferencePath $managedReferences `
  -Trials 9 `
  -EngineRoot D:\UE_5.8
```

The exact-identity Demo experiment used .NET/Crossgen2 10.0.7 and nine cold
processes per variant. IL was 84,145 bytes with a 51.0447 ms load median and
6.9704 ms entry-method preparation. R2R was 173,745 bytes with a 52.2050 ms
load median and 6.7121 ms preparation. The ratios were therefore 1.022731 load,
0.962943 preparation, and 2.064829 size. R2R failed all three promotion gates
and is not part of the product delivery path. Evidence is
`artifacts/chunk-profile/m11.4-r2r-controlled/readytorun-experiment.json`.

Two SDK archives with 180 manifest-listed payloads plus the manifest reproduced
SHA-256
`a401a8c45f6b2c6a235db5e879bc022d84dafff4b203d20cd4631b332c73be38`.
The extracted SDK's packaged R2R tool then completed a three-trial-per-variant
smoke using the packaged CLI. Its first attempt correctly rejected a stale
chunk dependency digest before the current Release chunk was emitted.

This outcome matches CoreCLR's stream-loading implementation: the official
runtime source describes a stream-based image as being passed onward as IL.
Switching to path-backed R2R solely for this optimization would conflict with
the current immutable in-memory chunk and collectible-generation model; the
.NET load-context design also limits reuse of precompiled code when one R2R
location is loaded into multiple contexts. The next slice therefore keeps the
portable deterministic IL chunk and evaluates bounded, cancellable prewarming
after activation and before the generation's first Tick.

[CoreCLR stream load source]: https://github.com/dotnet/runtime/blob/main/src/coreclr/vm/assemblynative.cpp
[.NET AssemblyLoadContext R2R constraint]: https://github.com/dotnet/runtime/blob/main/docs/design/features/assemblyloadcontext.md

## M11.4 generation-owned background prewarm

The World host now creates one prewarm operation with each collectible script
generation. Reload candidates schedule it only after `OnStart` and reflected
event binding succeed; initial actor-tree generations schedule it after their
first Tick so one component's JIT work cannot contend with another component's
cold activation. Activation, first Tick, and transactional rollback therefore
never await JIT preparation. The default operation prepares at most 64
concrete methods and checks an 8 ms elapsed-time budget between methods; hard
product limits reject more than 1,024 methods or more than 100 ms. A single
`RuntimeHelpers.PrepareMethod` call is not interruptible, so cancellation is
cooperative between methods and never waits on the GameThread.

The first Tick observes, but never awaits, the generation's operation. Initial
generations start the operation after that observation and gameplay callback;
reload generations may already have prepared methods before their first Tick.
Its snapshot reports candidate/prepared/skipped methods, terminal state,
elapsed time, prepared coverage at the first Tick, and only a failure type. Optional
optimization failures cannot fail script activation or Tick. Reload rejection,
generation retirement, and World teardown cancel the operation before the
generation bridge is released and the collectible load context is asked to
unload. The existing managed reload-transaction v0.1 JSON exposes the live
snapshot as an additive property that the native v0.1 reader safely ignores.
`FINARE_DISABLE_BACKGROUND_PREWARM=1` exists only for the controlled
compatibility/A-B lane.

`tools/measure-generation-prewarm.ps1` emits a deterministic no-native-bridge
probe chunk and alternates disabled/background variants in new processes. Each
variant receives the same 16 ms frame gap before its first Tick. The report
contains no source or machine path and uses
`unifiedscript.generation-prewarm-experiment/v0.1`:

```powershell
.\tools\measure-generation-prewarm.ps1 `
  -EngineRoot D:\UE_5.8 `
  -Trials 15
```

The 15-trial-per-variant run measured a 353,800 ns disabled first-Tick median
and a 26,600 ns background-prewarm median (ratio `0.075184`, 92.5% lower).
All 15 background trials completed before the first Tick and prepared 28
methods. Scheduling took 1,145,300 ns median and cancellation plus collectible
session unload took 334,800 ns, both below their 2 ms non-blocking gates.
Evidence is
`artifacts/chunk-profile/m11.4-background-prewarm/generation-prewarm-experiment.json`.
Two SDK builds each contained 182 manifest-listed payloads plus the manifest
and reproduced archive SHA-256
`df0e40d1421f4ac8708746758f12e0137b3c15970f8cb72d07fa53f3f22f717c`.
After extraction, the packaged tool emitted its own probe and passed five
trials per variant without a source checkout: first-Tick ratio `0.058004`,
1,156,200 ns schedule median, 344,900 ns teardown median, and 100% completion.
A deterministic safe-default diagnostics bundle admitted 31 prewarm reports,
performed 151 string redactions, and reproduced SHA-256
`6b99087fe41ce7a7c015674573bf32a875556051d79e4fc3ba14eba658ebfa2c`.

## M11.4 process loader prewarm and cold-start closure

The process loader now starts one bounded operation when the managed World host
is created. It prepares verified dependency snapshots before warming the strict
binary codec, so chunk dependency integrity work can overlap host setup while
the first chunk does not pay binary decoder JIT. Script activation never waits
for the operation. Defaults are 32 dependencies, 64 MiB, and a cooperative
50 ms active-work budget; hard limits are 256 dependencies, 512 MiB, and
500 ms. The budget is checked between dependency operations, while the fixed
1,729-byte codec probe is finite but not interruptible. Failures are contained
as type-only diagnostics, the live snapshot is copied into reload transactions,
and `FINARE_DISABLE_PROGRAM_LOADER_PREWARM=1` is reserved for controlled
A/B runs.

The seekable chunk reader also removed one redundant whole-file copy. A proposed
zero-copy `MemoryStream` view over a section was rejected after the matched
BlockBreaker gate regressed to a 1.737637 chunk/loose startup ratio. Loader R2R
was also rejected: an isolated CLI experiment improved to 0.820809 of IL, but
the actual five-trial BlockBreaker startup ratio was 1.052608. Product delivery
therefore remains deterministic CoreCLR IL with owned section materialization.

`comparison/measure-blockbreaker-chunk-delivery.ps1` now emits
`unifiedscript.blockbreaker-chunk-delivery/v0.2`, requires terminal safe loader
prewarm with a prepared codec and zero failures, and exits nonzero when the
matched startup ratio exceeds `1.0`. The final five alternating cold-process
trials passed: chunk startup was 75.8024 ms versus 78.1151 ms loose
(`0.970394`), reload was 75.2471 ms versus 75.6904 ms (`0.994143`), and reload
generation load was 1.2190 ms versus 1.6740 ms (`0.728196`). Both paths ran the
same 100 measured plus 30 warmup frames, one shot, one hit, two command
completions, ABI v3 fast/CallFrame calls, zero legacy calls, and committed
reload. Evidence is
`artifacts/comparison/chunk-delivery/20260828-203527/chunk-delivery.json`.
The full managed suite passes 588 / 588 and repository validation passes
81 / 81. Installed UE 5.8.1 rebuilt the component plus Lifecycle and
LifecycleStress tests; the stress lane completed 8 travel, 8 PIE, 16 successful
and 16 rolled-back reloads with zero retained collectible contexts. Two
182-payload SDK archives reproduced SHA-256
`3351ad754c7f3d1b7978454d31f500fd4ea7aa27802875dfafb35ce5b70eb507`,
and the extracted SDK passed isolated transactional installation.

## Measured BlockBreaker impact

`comparison/measure-blockbreaker-chunk-delivery.ps1` runs alternating cold
processes and requires the same 100 measured frames, 30 warmup frames, shot,
hit, command completions, ABI v3 calls, zero legacy calls, committed reload,
safe process-loader prewarm, and a chunk/loose startup ratio no greater than
`1.0`. The final 2026-08-28 five-trial median is:

| Metric | Release chunk | Loose compatibility | Chunk / loose |
| --- | ---: | ---: | ---: |
| Actor-tree cold startup | 75.8024 ms | 78.1151 ms | 0.970394 |
| Transactional reload | 75.2471 ms | 75.6904 ms | 0.994143 |
| Managed generation load inside reload | 1.2190 ms | 1.6740 ms | 0.728196 |
| Runtime program delivery | 1 file / 44,886 B | 2 files / 43,992 B | 1.020322 bytes |

The chunk removes runtime declaration JSON, consolidates delivery, and now
passes the matched cold-start gate without weakening verification. M11.4 keeps
CoreCLR PE/IL as executable code instead of adding a second custom bytecode VM.
The packaged BlockBreaker
gate proves both Player and Rifle start from the staged chunk for 300 frames,
and the generic Development release gate independently proves packaged chunk
startup.

The M11.1 large-project gate also compiles 256 independent Script Assets into
one 141,249-byte Release chunk. Its verified cold emit is 5,398.7625 ms and a
single-source default edit rebuilds the assembly and chunk in 5,123.4274 ms;
the resulting 256-entry deployment validates in a cold UE 5.8.1 process. See
`docs/LARGE_PROJECT_SCALE.md` for the workload, budgets, and artifact path.
