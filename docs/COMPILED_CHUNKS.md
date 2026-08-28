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

The v0.1 envelope deliberately treats section contents as opaque bytes. The
next vertical slice assigns deterministic binary codecs to the five runtime
sections, then connects the managed and native World hosts. JSON remains the
canonical readable authoring/import/export format, but the product loader will
consume the compiled binary payloads.

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

## Integration sequence

1. Define deterministic binary codecs for runtime manifest, declarations,
   bindings, and dependencies, plus a compiler command that consumes the same
   semantic program used by C# and the visual graph.
2. Add managed chunk loading directly from verified PE/PDB slices and native
   Unreal section access without extracting temporary loose files.
3. Add `ChunkPath` to Script Assets while retaining loose `AssemblyPath` and
   declaration fallback for migration.
4. Make reload verify a complete candidate chunk, construct the next
   collectible generation, and swap only after declaration and ABI validation;
   failures keep the previous generation.
5. Stage runtime chunks in cook/package, keep debug/editor sidecars optional,
   and measure startup, reload, package size, and deterministic cache hits.

The envelope is complete only as a foundation. M11.0 is complete when the UE
Script Asset and packaged BlockBreaker path load this container end to end with
no runtime JSON parsing and loose-file fallback remains covered.
