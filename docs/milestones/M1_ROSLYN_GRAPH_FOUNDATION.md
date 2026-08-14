# M1: Roslyn-to-Graph Foundation

## Goal

Prove the smallest useful cross-projection contract: a supported `[Visual]` C#
method can be parsed, validated, lowered to deterministic HIR, and projected to
a graph containing both control and data edges.

## Acceptance criteria

1. Identical source, path, and options produce identical semantic IDs and JSON.
2. Unsupported syntax stops artifact emission with a stable `UVS` diagnostic.
3. Non-visual methods are ignored by the profile validator.
4. HIR preserves declarations, source spans, statements, and expression trees.
5. Graph projection contains an entry node, control edges, expression nodes, and
   data edges.
6. A CLI emits HIR and graph artifacts from the sample.
7. The full solution builds with warnings treated as errors.
8. All tests and repository validation pass.

## Work packages

### Foundation

- solution and dependency boundaries
- deterministic semantic IDs
- source spans and diagnostics
- polymorphic JSON configuration

### Visual Profile

- `[Visual]` discovery
- allowlist-based syntax validation
- explicit diagnostics for dangerous/high-level constructs
- profile tests

### HIR

- compilation unit, type, method, and parameter nodes
- block, variable, expression, if, and return statements
- literal, identifier, member, invocation, operator, assignment, await, and cast
  expressions
- deterministic lowering tests

### Graph

- nodes, ports, and typed edges
- structured branch projection
- expression dependency projection
- deterministic graph serialization

### Integration

- CLI diagnostics
- sample input
- artifact emission
- clean build and test execution

## Deliberate exclusions

M1 does not implement semantic type binding, C# compilation to IL, graph edits
back to source, a visual UI, persistent layout, UE integration, or runtime code.
These exclusions keep the milestone focused on validating the shared model.

