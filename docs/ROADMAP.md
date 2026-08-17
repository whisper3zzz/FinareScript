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

Status: complete. Broader control/data-flow editing continues under M4.

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
- [x] local-literal statement insertion with parent/sibling/type/name guards
- [x] safe statement deletion with reference/effect guards and tombstones
- [x] exact sibling preconditions for concurrent structural edits
- [x] HIR use/definition and expression-location indexes
- [x] stable pure-expression extraction into a single-evaluation temporary
- [x] structured block dominance for local value availability
- [x] typed existing-local-to-consumer reconnection with effect guards and tombstones
- [x] atomic temporary-introduction and multi-consumer patch plans
- [x] symbolic plan handles for newly allocated local declaration IDs
- [x] in-memory stage rebasing with late-failure rollback
- [x] atomic plan composition of all bounded patch operations
- [x] insertion-produced local handles and structural gap/endpoint rebasing
- [x] versioned scalar graph-authored expression payload
- [x] first typed `if` synthesis with explicit empty branch blocks
- [x] symbolic branch-block handles and ordered local-literal branch-body population
- [x] typed branch return payloads and terminal-step/unreachable guards
- [x] unaffected-node identity pinning for nested staged insertions
- [x] graph layout sidecar with deterministic JSON, orphan recovery, and CLI sync

## M4 — Structured control flow expansion

Outcome: practical gameplay methods are fully visualizable.

- [x] Graph v0.3 regions for functions, blocks, `if`, `then`, and `else`
- [x] exact direct node ownership and explicit/implicit branch preservation
- [x] CFG entry/exit, reachability, dominance, and post-dominance indexes
- [x] structured branch merge/exit facts and SCC reducibility validation
- [ ] bounded local assignment and general data/control rewiring
- [ ] nested branch construction and structural HIR operations
- [ ] `while`, `break`, and `continue`
- [ ] extend regions to loop bodies, exits, and continuations
- [ ] `for` and `foreach`
- [ ] `switch` and supported patterns
- [ ] conditional and null-conditional expressions
- [ ] deterministic temporary naming

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
