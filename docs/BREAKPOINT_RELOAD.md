# Reload Generations and Semantic Breakpoints

## Purpose

M5.3 turns the M5.2 source map into a reload-safe debugger protocol boundary.
Breakpoints target persistent semantic node IDs rather than source lines or raw
IL offsets. Every loaded program receives a distinct generation identity, so a
hit from an unloaded assembly cannot be mistaken for a hit in the current
image.

```text
Semantic breakpoint ID + semantic node ID
                    │
                    ▼
        current program generation
                    │
                    ▼
MethodDef token + one or more IL/PDB locations
                    │
                    ▼
generation-aware hit -> breakpoint/node resolution
```

This is the contract needed by a future UE debugger adapter or managed debug
transport. M5.3 does not install CLR traps, pause threads, or implement step
operations.

## Program image identity and cache

`CoreClrProgramImageCache` has two identity layers:

- a request fingerprint covers the assembly/configuration ID, logical source
  paths and text, input identity sidecars, and explicit identity pins; it lets
  an identical in-process request skip Roslyn analysis and emission;
- `ProgramImageKey` is a `sha256:` content address over the emitted assembly
  name, PE, portable PDB, and canonical source map; it identifies the actual
  executable/debug image across reload generations.

The cache is thread-safe, bounded, and least-recently-used. Successful images
are cached; profile, semantic, or compilation failures are not. Hit/miss counts
remain lifetime statistics when the cache contents are cleared.

An empty identity sidecar may allocate new persistent IDs during compilation.
Stable cross-process reload therefore requires saving the updated sidecar. The
program image and reload result now carry reconciled sidecars, and the `emit`
CLI writes them transactionally beside the DLL, PDB, and source map.

## Reload generation

`CoreClrReloadDomain` owns one active collectible execution session. Each
successful `Reload` creates:

- a stable domain ID;
- a monotonically increasing generation number;
- a generation semantic ID derived from domain, number, and image key;
- the content-addressed image key;
- source-compilation and semantic-configuration IDs.

The new PE/PDB is loaded and all semantic breakpoints are rebound before the
old session is disposed. A failed compilation or load leaves the previous
generation, bindings, and executable session untouched. A repeated request can
reuse the cached program image, but still receives a new generation ID because
it is a distinct load lifetime.

The domain serializes reload, execution, breakpoint mutation, and hit
resolution. Disposing it unloads the active context, clears the active binding
snapshot, and permanently closes the domain.

## Breakpoint binding

`SemanticBreakpoint` contains a persistent breakpoint ID, target semantic node
ID, and enabled state. `SemanticBreakpointManager` binds every enabled request
against the current `unifiedscript.source-map/v0.1` document.

Binding states are:

- `bound`: the node exists and has one or more real MethodDef/IL/PDB locations;
- `pending`: the node is absent or non-executable in this generation;
- `disabled`: the request is retained but publishes no runtime location.

When a sidecar-preserved node moves because of formatting or a visual edit, the
same node ID binds to its new IL/PDB locations. When it is deleted, its identity
becomes a sidecar tombstone and the breakpoint becomes pending. If that semantic
node later reappears in a loaded image, the retained request automatically
returns to bound.

The binding schema is:

```text
unifiedscript.breakpoint-bindings/v0.1
```

It rejects duplicate breakpoint IDs, invalid generation identity, malformed
MethodDef tokens, duplicate offsets, invalid spans, and inconsistent
bound/pending/disabled payloads.

## Hit routing and stale protection

A runtime adapter reports:

```text
unifiedscript.breakpoint-hit/v0.1
```

with domain ID, generation ID, image key, MethodDef token, and IL offset. All
three lifetime identities must match the active generation. A hit from a prior
reload fails with stable code `UDB1002`; a current location without an enabled
bound breakpoint fails with `UDB1003`.

A valid hit becomes:

```text
unifiedscript.breakpoint-resolution/v0.1
```

and resolves back to the semantic method plus every matching breakpoint/node
pair. Multiple editor breakpoints can intentionally share one emitted IL
location.

## Runtime use

```csharp
var cache = new CoreClrProgramImageCache(capacity: 32);
using var domain = new CoreClrReloadDomain(cache);

CoreClrReloadResult loaded = domain.Reload(request);
domain.SetBreakpoint(new SemanticBreakpoint(breakpointId, nodeId));

SemanticBreakpointBinding binding =
    domain.CurrentBreakpoints!.Bindings.Single();
CoreClrBreakpointLocation location = binding.Locations[0];

CoreClrBreakpointHit hit = CoreClrBreakpointHit.Create(
    loaded.Generation,
    location.MetadataToken,
    location.IlOffset);
BreakpointHitResolution resolution = domain.ResolveHit(hit);
```

After any reload, adapters must use the new generation returned by `Reload` and
reinstall the locations in its binding snapshot. They must never reuse token or
offset pairs from an older generation, even when the image key happens to be
the same.

## Deliberate boundary

M5.3 supplies cache, reload lifetime, breakpoint migration, strict JSON, and
hit identity resolution. M5.4 now consumes the same generation and semantic
node identities in a cooperative CoreCLR adapter with real pause/resume,
step-in/over/out, stack frames, bounded locals for every active Visual frame,
bounded read-only snapshot evaluation, and an in-process loopback transport.
Native managed-debug traps, async state-machine locations, serialized remote
transport, and UE editor UI remain later work. See
[DEBUG_ADAPTER.md](DEBUG_ADAPTER.md).
