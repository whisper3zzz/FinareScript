# Semantic IL Source Map

## Purpose

M5.2 connects stable UnifiedScript semantic identity to the executable CLR
image. Text-authored and visually patched C# now produce the same versioned
mapping:

```text
Semantic method ID -> MethodDef metadata token
Semantic node ID   -> one or more IL offsets
IL offset          -> portable-PDB document and source span
```

The map is generated from the emitted PE and portable PDB. It does not infer
offsets from syntax order, rewrite the PDB, or decompile IL. This keeps C# text,
the executable HIR identities carried into Graph, execution, and a future
debugger anchored to the semantic IDs that already survive visual patch
reconciliation.

## Schema

`unifiedscript.source-map/v0.1` contains compilation-wide identity and an
ordered method table. Each method records:

- its stable semantic method ID and canonical metadata name;
- its real MethodDef metadata token;
- its first visible IL sequence-point offset, when one exists;
- every visible portable-PDB sequence point and normalized source span;
- executable HIR node IDs with the IL offsets whose PDB spans overlap that
  node's source span.

A node can map to several offsets. That is intentional for structured control
flow: a loop, condition, conditional expression, or cleanup-bearing statement
can have multiple meaningful suspension or execution positions. Several nested
nodes can also share one offset when the compiler emits a statement-level
sequence point.

The serializer sorts methods, sequence points, nodes, and node offsets. It
rejects duplicate method IDs, duplicate MethodDef tokens, duplicate node IDs,
invalid spans, unsupported schema versions, and node mappings that reference an
IL offset absent from the same method's PDB sequence-point table.

## Generation

`CSharpProgramEmitter` runs the full profile, semantic, Graph, and CFG gates,
emits the deterministic DLL and portable PDB, then reads both artifacts through
`System.Reflection.Metadata`. Emitted overloads are resolved by containing
type, method name, parameter arity, exact CLR parameter types, and return type.
Nullable value types are normalized to `System.Nullable<T>` while nullable
reference annotations are correctly treated as CLR signature metadata erasure.

Only executable HIR statements, expressions, loop variables, and loop
declarations become node mappings. Parameters and purely structural blocks do
not receive invented execution offsets.

## CLI artifacts

Emit one source file:

```bash
dotnet run --project src/UnifiedScript.Cli -- \
  emit samples/Execution/RuntimeRules.cs \
  --out artifacts/runtime-image
```

The command writes:

```text
RuntimeRules.dll
RuntimeRules.pdb
RuntimeRules.sourcemap.json
```

`emit-files` accepts multiple source files and the same `--identity`,
`--effects`, `--reference`, and `--assembly` inputs as analysis. Emission is
all-or-nothing: invalid programs do not produce PE, PDB, or source-map
artifacts.

## Text/visual identity

The execution-equivalence test applies the checked-in visual foreach plan,
recompiles the generated source, locates the generated loop's persistent
semantic ID in the source map, and then executes the program. The visual loop
therefore has both an editable Graph identity and a real CoreCLR IL location;
there is no visual-only breakpoint namespace.

Repeated identical emission produces byte-identical PE and PDB files and
canonical-identical source-map JSON. A collectible execution session exposes
the immutable map without retaining reflection or source-assembly objects.

## Deliberate boundary

Synchronous methods map directly to their MethodDef sequence points. For C#
`async` methods, Roslyn places user sequence points on the generated state
machine's `MoveNext` method rather than the kickoff method. M5.1 already rejects
async invocation, so source-map v0.1 records the kickoff token with a null entry
offset and empty sequence-point/node tables instead of publishing a false
location. Mapping state-machine states back to the original semantic method
belongs with the future suspension-aware execution and debugger contract.

M5.2 provides location identity. M5.3 now consumes it for breakpoint
set/bind/hit records, reload-generation migration, and stale-image rejection;
see [BREAKPOINT_RELOAD.md](BREAKPOINT_RELOAD.md). M5.4 adds the first cooperative
CoreCLR adapter over that identity, with actual pause/resume, stack inspection,
top-frame locals, and step-over; see [DEBUG_ADAPTER.md](DEBUG_ADAPTER.md).
Native managed-debug traps and async state-machine stepping remain later work.
