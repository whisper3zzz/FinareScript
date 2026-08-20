# M5 CoreCLR Execution

## Outcome

Text-authored and visually authored programs compile to and execute as the same
validated CoreCLR method body, retaining semantic identity for future engine
and debugger integration.

## M5.1 implemented vertical slice

- new `UnifiedScript.Runtime` project separating invocation/result contracts
  from Roslyn, Graph, CLI, and future UE adapters
- deterministic in-memory PE plus portable-PDB emission after Visual Profile,
  compiler, semantic, Graph, and CFG validation
- strict `unifiedscript.execution-result/v0.1` returned/faulted schema carrying
  compilation, configuration, and method IDs
- stable semantic-method-ID invocation with qualified-name resolution only at
  the CLI boundary
- exact JSON conversion for graph-safe scalar, enum, array, enumerable, and
  list arguments; canonical scalar and enum return encoding
- synchronous static `[Visual]` entry-point boundary with explicit rejection of
  instance and async entry points
- target exception unwrapping into deterministic type/message faults
- collectible assembly load context, shared dependency resolution, explicit
  disposal, and a GC-proven unload test after source-defined enum execution
- `execute` CLI command with returned value, structured fault, compilation
  failure, and invocation-error exit semantics
- checked-in enumerable execution sample and an automated text-authored versus
  visual-plan-authored result-equivalence proof

## M5.1 acceptance criteria

1. Invalid profile or semantic programs never produce a PE image.
2. Identical requests emit byte-identical PE and portable PDB hashes.
3. Emission revalidates Graph and derived CFG before loading executable code.
4. A CLI-qualified Visual method resolves to its existing semantic method ID.
5. Invocation checks Visual/static/synchronous shape, argument count, and exact
   JSON conversion before calling reflection.
6. Scalar, enum, array, and enumerable arguments cross the backend-neutral
   boundary without exposing CLR reflection objects.
7. Successful scalar/enum returns use canonical JSON plus semantic type name.
8. Target exceptions become structured faults without
   `TargetInvocationException` leakage.
9. Returned/faulted JSON strictly validates schema and mutually exclusive
   payloads.
10. Disposing a session releases its collectible load context after no results
    retain source-assembly objects.
11. The public CLI executes both returned and faulted sample paths with stable
    output and exit status.
12. A real checked-in visual patch plan and an independently text-authored
    equivalent method return the same typed value through the same host.
13. Runtime boundaries and the absence of a security sandbox are documented.
14. The full solution, 313-test suite, format check, repository validator, and
    execution CLI regressions pass.

## Next slice

M5.2 should emit a method/node source map from semantic IDs to IL offsets and
portable-PDB sequence points, then use it to define shared breakpoint identity.
Program-image caching and reload generation IDs should follow before UE hosts a
world-scoped runtime domain.
