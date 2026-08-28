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
That cost is reported rather than hidden and is routed to M11.1 for prewarming
or ReadyToRun work; it is not a frame-time cost. The packaged BlockBreaker gate
proves both Player and Rifle start from the staged chunk for 300 frames, and the
generic Development release gate independently proves packaged chunk startup.
