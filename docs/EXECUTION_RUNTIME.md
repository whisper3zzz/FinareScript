# CoreCLR Execution Runtime

## Purpose

M5.1 closes the first executable text/visual equivalence loop. A `[Visual]`
method authored as C# or produced by a semantic patch plan passes through the
same profile, HIR, bound semantics, Graph/CFG gate, deterministic Roslyn emit,
and collectible CoreCLR host. The visual form is not interpreted by a second
VM and IL is never decompiled back into Graph.

```text
C# text ───────────────┐
                      ├─→ HIR + semantics + Graph/CFG ─→ deterministic PE/PDB
visual semantic patch ┘                                      │
                                                             ▼
                                              collectible CoreCLR session
                                                             │
                                          semantic method ID + JSON arguments
                                                             │
                                                             ▼
                                            returned value or structured fault
```

## Project boundary

`UnifiedScript.CSharp.CSharpProgramEmitter` owns Roslyn emission. It first runs
the complete semantic pipeline and Graph/CFG projection, then emits a
deterministic in-memory DLL and portable PDB. No executable image is returned
when profile, compiler, semantic, Graph, or CFG validation fails.

`UnifiedScript.Runtime` owns the backend-neutral invocation/result records and
the CoreCLR implementation:

- `ExecutionInvocation` targets the existing stable method `SemanticId`;
- `ExecutionArgument` carries one validated JSON value;
- `ExecutionResult` uses strict schema
  `unifiedscript.execution-result/v0.1`;
- `CoreClrExecutionHost` compiles and loads one program;
- `CoreClrExecutionSession` invokes multiple methods and owns the collectible
  `AssemblyLoadContext` lifetime.

The Runtime project does not parse C#, construct Graph nodes, or apply visual
edits. The CLI resolves a human-readable qualified method name to the same
semantic method ID before invoking it. Overloaded entry points can instead use
`--method-id us_...` to bypass name ambiguity.

## M5.1 invocation boundary

The initial entry-point contract deliberately accepts only synchronous static
`[Visual]` methods. Object receivers require the future UE object/lifetime
bridge, and async methods require a suspension-aware scheduler contract.

JSON arguments currently materialize exact graph-safe scalar values, enums,
one-dimensional arrays, `IEnumerable<T>` interfaces, and `List<T>`. The
execution result currently serializes scalar, nullable-scalar, string, and enum
returns to canonical JSON. Unsupported conversions fail before reflection
invocation with a stable `UVR` diagnostic code.

Target exceptions do not escape as `TargetInvocationException`. They become:

```json
{
  "outcome": "faulted",
  "fault": {
    "typeMetadataName": "System.OverflowException",
    "message": "..."
  }
}
```

Returned and faulted results both retain source-compilation, semantic-
configuration, and method IDs. This is the minimum identity required for later
source maps, breakpoints, and engine-domain routing.

## Collectible lifetime

Every session loads the emitted PE/PDB into a collectible
`AssemblyLoadContext`. Framework and already-loaded shared assemblies are
reused; explicit metadata references can be loaded into the collectible
context. `Dispose` clears strong assembly references and calls `Unload`.

Tests force garbage collection after executing a source-defined enum and prove
that the load context is released. Invocation results contain only IDs and
strings, so no `Type`, `MethodInfo`, exception, or return object from the
collectible assembly crosses the session boundary.

## CLI

Execute an enumerable loop:

```bash
dotnet run --project src/UnifiedScript.Cli -- \
  execute samples/Execution/RuntimeRules.cs \
  --method Samples.Execution.RuntimeRules.LastNonNegative \
  --args '[[1,-2,9],4]'
```

The returned value is `System.Int32` with canonical JSON `9`. Execute the
checked fault path:

```bash
dotnet run --project src/UnifiedScript.Cli -- \
  execute samples/Execution/RuntimeRules.cs \
  --method Samples.Execution.RuntimeRules.Absolute \
  --args '[-2147483648]'
```

The CLI prints a structured `System.OverflowException` fault and exits with
status `3`. Compilation/profile failure exits with status `2`; command-line or
invocation-boundary errors use their existing data/usage status.

## Text/visual equivalence proof

The execution-equivalence test applies the checked-in
`BuildEnumerableForEach.plan.json`, executes the generated C# through the
CoreCLR host, executes a separately text-authored equivalent method through
the same host, and compares their typed results. Both return `9` for the same
two enumerable inputs and seed.

This proves the intended ownership boundary: text and visual editing may
produce source differently, but execution consumes one validated program model
and one CoreCLR backend.

## Security and deliberate limits

The collectible load context is a reload boundary, not a security sandbox.
Executing source runs trusted development code inside the CLI process. CPU,
memory, filesystem, network, native interop, API allow/deny policy, and process
isolation are not enforced by M5.1.

The next execution slices should add semantic-ID-to-IL/PDB sequence-point
mapping, cached program images, async invocation, engine-owned receivers,
cancellation/time budgets, and debugger/breakpoint identity. UE embedding must
consume this contract rather than bypassing it with a separate scripting VM.
