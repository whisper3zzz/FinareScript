# Roadmap

## M0 — Repository foundation

Outcome: deterministic .NET solution with documented boundaries and automated
verification.

- [x] .NET 10 solution and central package versions
- [x] Core, CSharp, Graph, CLI, and Tests projects
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
- [ ] golden JSON schema fixture

## M2 — Semantic model and graph-safe types

Outcome: Roslyn symbols bind into a language-independent type/effect model.

- primitive and nominal type descriptors
- nullable flow state
- method overload resolution capture
- graph-safe signature validation
- pure/impure/latent effect descriptors
- deterministic symbol catalog
- semantic diagnostics for unsupported APIs

## M3 — Bidirectional source projection

Outcome: graph patches produce localized, format-preserving C# edits.

- persistent semantic ID sidecar
- semantic patch protocol
- HIR diff and conflict detection
- Roslyn syntax construction
- trivia/comment preservation
- graph layout sidecar
- source-to-graph-to-source golden tests

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

- Roslyn compilation host
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
