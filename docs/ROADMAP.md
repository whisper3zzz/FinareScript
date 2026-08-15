# Roadmap

## M0 — Repository foundation

Outcome: deterministic .NET solution with documented boundaries and automated
verification.

- [x] .NET 10 solution and central package versions
- [x] Core, Semantics, CSharp, Graph, CLI, and Tests projects
- [x] repository validator
- [x] architecture and contribution rules

## M1 — Roslyn-to-graph vertical slice

Outcome: a supported `[Visual]` method emits deterministic HIR and graph JSON,
while unsupported syntax fails with actionable diagnostics.

- [x] deterministic semantic ID primitive
- [x] source spans and diagnostics
- [x] profile v0.1 validator
- [x] declaration/statement/expression HIR
- [x] syntax-to-HIR lowering
- [x] graph schema and projection
- [x] CLI emission
- [x] unit tests and sample
- [x] persistent identity sidecar design
- [x] golden JSON schema fixture

## M2 — Semantic model and graph-safe types

Outcome: Roslyn symbols bind into a language-independent type/effect model.

- [x] primitive and nominal type descriptors
- [x] nullable type annotations
- [x] method overload resolution capture
- [x] graph-safe signature validation
- [x] pure/impure/latent effect descriptors
- [x] deterministic bound symbol references
- [x] deterministic multi-document compilation requests
- [x] cross-file type and overload binding
- [x] versioned method-effect catalog overrides
- [x] semantic configuration identity for references and effect rules
- [ ] flow-sensitive nullability state
- [ ] `.csproj`/MSBuild and NuGet reference loading
- [ ] configurable API allow/deny catalog
- [ ] semantic diagnostics for unsupported APIs

## M3 — Bidirectional source projection

Outcome: graph patches produce localized, format-preserving C# edits.

- [x] versioned semantic patch protocol
- [x] source compilation and semantic configuration preconditions
- [x] localized literal replacement by graph/HIR `SemanticId`
- [x] atomic rejection of stale, invalid, or semantically unsafe patches
- [x] Roslyn syntax construction with trivia/comment preservation
- [x] source-to-graph-to-source literal round-trip tests
- [x] persistent semantic ID sidecar
- [x] structural/shape/context reconciliation with ambiguity diagnostics
- [x] tombstones for deleted semantic nodes
- [x] explicit identity remapping for graph-authored replacements
- [x] unary/binary operator changes with semantic type preconditions
- [x] same-invocation argument reconnection with type/effect guards
- [x] overlapping structural-operation conflict detection
- [ ] insertion/deletion and general structural HIR operations
- [ ] graph layout sidecar
- [ ] localized insertion, deletion, and data/control rewiring

## M4 — Structured control flow expansion

Outcome: practical gameplay methods are fully visualizable.

- `while`, `for`, and `foreach`
- `switch` and supported patterns
- conditional and null-conditional expressions
- explicit graph regions for branches and loops
- reducibility validation
- deterministic temporary naming

## M5 — CoreCLR execution backend

Outcome: text and graph versions compile to and execute the same IL.

- Roslyn IL-emission host
- assembly and PDB emission
- method/node source map
- reloadable development assembly context
- backend-neutral execution contract
- shared breakpoint identity

## M6 — Unreal Engine adapter

Outcome: the proven language pipeline becomes usable from UE.

- reflection symbol extraction
- C# binding source generator
- UObject lifetime bridge
- world-scoped runtime domain
- visual editor integration
- generated script host class/component
- cook and packaging strategy
