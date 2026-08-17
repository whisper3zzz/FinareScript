# M3 Bidirectional Source Projection

## Goal

Prove that a visual edit can become a localized C# source edit without creating
a second language implementation or bypassing the existing compiler checks.

Status: complete. The remaining general control/data-flow expansion belongs to
M4 and builds on this frozen bidirectional transaction boundary.

## Implemented vertical slice

- `unifiedscript.patch/v0.7` polymorphic JSON protocol
- `replaceLiteral` operation targeted by HIR/Graph `SemanticId`
- unary/binary `changeOperator` with result and operand type preconditions
- same-invocation `reconnectArgument` with endpoint, type, and effect guards
- typed `insertStatement` for graph-safe local-literal declarations
- versioned graph-authored expressions for scalar literals, local/parameter
  references, casts, and bounded unary/binary operators
- typed `if` insertion with explicit empty `then` and `else` blocks
- typed terminal `return` insertion for non-async value and `void` methods
- bounded `deleteStatement` with symbol-use and effect safety checks
- `introduceTemporary` for stable, pure return/local-initializer expressions
- `reconnectValue` from an existing local to an exactly typed stable consumer
- `unifiedscript.patch-plan/v0.5` ordered multi-stage transaction protocol
- plan steps for every bounded semantic patch operation
- symbolic plan handles for local IDs allocated by `introduceTemporary` and
  local-literal `insertStatement`
- symbolic `then`/`else` handles for blocks allocated by `if` insertion, with
  ordered local-literal and typed-return branch-body population
- terminal branch-handle tracking that rejects unreachable later appends
- unaffected-node identity pins around nested replacements
- initial-context validation and safe internal baseline/sibling rebasing
- repeated insertion-gap and invocation-endpoint rebasing
- persistent-sidecar adoption between in-memory plan stages
- HIR expression-location and use/definition indexes
- structured block hierarchy and statement-dominance checks
- exact parent block and adjacent-sibling structural preconditions
- overlapping source-range conflict detection
- expected-old-literal precondition
- content-sensitive source compilation baseline
- semantic configuration baseline covering assembly, references, and effects
- Graph schema v0.2 exposure of both baseline IDs
- multi-document in-memory patch application
- complete Roslyn/profile/semantic reanalysis before commit
- localized Roslyn replacement with leading/trailing trivia preservation
- single-document `apply-patch` and `apply-plan` CLI commands with non-in-place,
  atomic output writing
- deterministic sample patch and round-trip tests
- opt-in `unifiedscript.identity/v0.1` sidecar model and deterministic JSON
- cryptographically random persistent semantic IDs
- structural, shape, parent, token-context, and anchor reconciliation evidence
- ambiguity warnings instead of silent identity guessing
- tombstones for unmatched deleted nodes
- Roslyn-annotation identity pins for graph-authored replacements
- `identity-init`, repeatable `--identity`, and rollback-capable `--out-identity`
- `unifiedscript.graph-layout/v0.1` semantic-independent canvas sidecar
- deterministic node placement, comment region, and edge-routing JSON
- graph-driven layout initialization and synchronization CLI commands
- explicit unplaced state plus missing-node/function orphan recovery

## Failure contract

The applier returns no patched documents when any operation fails. Stable
diagnostic codes distinguish:

- `UVS3001`: source compilation baseline mismatch
- `UVS3002`: semantic configuration mismatch
- `UVS3003`: target node or source syntax not found
- `UVS3004`: target is not the operation's required HIR kind
- `UVS3005`: an expected old value, operator, endpoint, or semantic type no longer matches
- `UVS3006`: replacement is not one complete C# literal
- `UVS3007`: operator is unsupported or crosses a result-preserving family
- `UVS3008`: invocation argument index is outside the current argument list
- `UVS3009`: reconnected ports are not exactly type-compatible
- `UVS3010`: two operations overlap in the same source range
- `UVS3011`: argument reconnection would change effect evaluation order
- `UVS3012`: invocation shape cannot be safely represented by this operation
- `UVS3013`: parent block or adjacent-sibling preconditions no longer match
- `UVS3014`: the requested statement shape is outside the supported slice
- `UVS3015`: a local declaration is still referenced
- `UVS3016`: deleting the statement could remove effects, reads, or throws
- `UVS3017`: an inserted local name conflicts with a parameter or local
- `UVS3018`: temporary introduction would cross unstable or effectful evaluation
- `UVS3019`: the requested local source is unavailable, out of scope, shadowed,
  or does not dominate the consumer
- `UVS3020`: reconnecting the value would discard unstable evaluation, effects,
  comments, or directives
- `UVS3021`: a multi-stage plan targets a document without persistent identity
- `UVS3022`: plan handles or method relationships cannot be resolved safely
- `UVS3023`: a graph-authored expression shape/operator is invalid or unsafe
- `UVS3101`: identity evidence is ambiguous; allocate a new ID instead of guessing
- `UVS3102`: an explicit identity pin is invalid, missing, duplicate, or tombstoned

The proposed source is also rejected if normal C# compilation, Visual Profile
validation, semantic binding, or graph-safe signature validation fails after the
edit. CLI failures never write the output source.

## Proven scenarios

The test and sample suite proves that:

1. patch JSON retains its derived operation type deterministically;
2. duplicate operation targets and unknown schemas are rejected;
3. single-file compilation identity changes when source content changes;
4. source and semantic-configuration mismatches reject stale graph edits;
5. changed old values and wrong target kinds are reported explicitly;
6. expression-shaped replacements cannot masquerade as literal edits;
7. semantically invalid literal changes reject the whole transaction;
8. comments and formatting survive a successful replacement;
9. a multi-file request changes only the target document;
10. reanalysis projects the new literal back into Graph;
11. unchanged source emits a byte-identical sidecar;
12. comments and whitespace preserve the complete live ID set;
13. deleted nodes move to tombstones and their IDs are not reused;
14. ambiguous evidence allocates a new ID with `UVS3101`;
15. changing `1` to `100` preserves every live persistent ID;
16. unary and binary operators change without changing their persistent node IDs;
17. exactly typed pure invocation arguments exchange Graph ports and retain IDs;
18. different types and effectful argument subtrees reject reconnection;
19. overlapping structural operations reject the complete transaction;
20. local-literal insertion works in empty and non-empty explicit blocks;
21. insertion preserves every existing live ID and allocates IDs only for the
    new declaration and literal;
22. stale sibling/type anchors and method-wide name conflicts reject insertion;
23. deleting an unused literal local preserves exterior comments and tombstones
    its complete subtree;
24. referenced locals and effectful calls reject deletion;
25. a resolved pure static call with effect-free arguments can be deleted;
26. a nested stable expression moves into a temporary initializer without
    changing its ID or the IDs of its original ancestors;
27. a new declaration and replacement identifier are the only IDs allocated by
    temporary introduction;
28. return expressions and local initializers both support extraction with exact
    statement/block/sibling preconditions;
29. the replacement identifier binds to the newly declared local and projects
    that `referencedSymbolId` back into Graph;
30. stale containing statements and effectful evaluation contexts reject the
    complete transaction;
31. a stable consumer expression can be replaced by an identifier bound to an
    existing, exactly typed local declaration;
32. the source declaration, consumer ancestors, statement, siblings, and block
    retain their IDs while the removed consumer subtree is tombstoned and the
    new identifier receives one fresh ID;
33. a root-block local structurally dominates consumers in later nested blocks;
34. a branch-local definition cannot feed an outer or sibling-branch consumer;
35. effectful/unknown consumer evaluation and mismatched semantic types reject
    value reconnection;
36. the checked-in CLI sample reanalyzes to a Graph identifier whose
    `referencedSymbolId` is the requested source definition;
37. patch-plan JSON preserves ordered polymorphic steps and rejects duplicate
    targets, future source-handle references, and handle type mismatches;
38. one introduction and two overlapping consumer replacements execute in
    order despite all three editing the same original C# block;
39. a symbolic `sourceStepId` resolves the temporary declaration's newly
    allocated random ID after the first in-memory reconciliation;
40. all three final identifiers bind to that one declaration while both removed
    consumer subtrees become tombstones;
41. a later effectful reconnection rejects the complete plan after earlier safe
    stages without returning any intermediate source or sidecar;
42. stale original sibling context fails before internal stage rebasing begins;
43. plans without persistent identity fail before executing their first step;
44. the checked-in `apply-plan` sample writes one final source/sidecar pair and
    reanalyzes to three Graph references to the shared declaration;
45. layout JSON normalizes deterministically and rejects contradictory placement
    or invalid geometry;
46. semantic patch insertion preserves every surviving placed node while its
    two new Graph nodes receive explicit unplaced state;
47. missing nodes retain their last visual state as reasoned orphans;
48. an orphan restores its placement when the same function/node identity
    reappears;
49. patch-plan JSON round-trips every bounded operation step and accepts an
    inserted local as a typed symbolic source;
50. one four-stage plan inserts a local, reconnects a consumer to it, changes an
    operator, and deletes an obsolete declaration as one commit;
51. repeated insertions into the same original block gap preserve plan order;
52. argument reconnection relocates endpoint indices by persistent node ID after
    an earlier literal replacement;
53. deleting a statement before a later descendant step fails atomically without
    returning the successful deletion; and
54. both the shared-expression and composed v0.5 CLI samples produce
    their documented final source and identity sidecars;
55. patch JSON round-trips all five graph-authored expression variants and
    rejects expression trees deeper than the protocol bound;
56. one `if` insertion composes a cast, literal, parameter references,
    comparison, logical negation, and conditional conjunction into a
    compiler-bound Boolean condition;
57. branch insertion always emits explicit `then` and `else` blocks, while a
    later local source or an unsupported potentially throwing operator is
    rejected before source mutation;
58. every pre-existing identity and placed Graph node survives branch
    insertion, while the branch and condition Graph nodes receive fresh IDs and
    explicit unplaced layout state;
59. patch-plan v0.3 composes a non-producing `if` insertion with a later direct
    edit and rejects attempts to consume the branch step as a local handle;
60. the checked-in `InsertGuard.patch.json` sample applies and emits the
    documented branch skeleton;
61. patch-plan v0.4 resolves the random `then` and `else` block IDs produced by
    an earlier `if` step and appends local-literal declarations in plan order;
62. branch-body locals become typed plan handles, but a late attempt to use one
    outside its branch fails dominance checks and rolls back every prior stage;
63. original identities and Graph layout survive atomic branch population,
    while every newly projected branch/condition/local node is unplaced; and
64. the checked-in `PopulateGuard.plan.json` sample applies without exposing a
    generated block ID;
65. value and `void` return payloads round-trip, bind to the current method's
    exact return type, and reject mismatched or async/task-like methods;
66. a return may be inserted only at a block end, and a later append to the same
    symbolic branch is rejected as unreachable before plan execution;
67. an early-return plan preserves every original identity and placed Graph
    node while projecting the new return and control edges as unplaced nodes;
68. a late wrong-type return rolls back the earlier generated `if` and branch;
69. nested insertion pins unaffected exterior nodes, so a new same-valued branch
    literal cannot steal a later target's persistent ID; and
70. the checked-in `ReturnGuard.plan.json` sample emits a typed early return and
    edits the original fallback literal in one atomic transaction.

## Deliberate limits

This is a bounded structural-edit slice, not general graph-to-source generation.
It does not apply multi-file patches through the CLI, synthesize arbitrary
expression payload shapes or general control-flow statements, perform
arbitrary data/control rewiring, delete
returns/branches/blocks, reconcile declaration renames, compact tombstones, or
automatically place graph nodes. Layout v0.1 does not persist viewport/session
state, define a three-way merge driver, or choose an auto-layout algorithm.
Standalone insertion accepts a local-literal declaration, an `if` skeleton, or
a terminal return.
The `if` condition accepts only graph-safe scalar types already present in bound
semantics, a closed pure expression subset, and same-method references that are
available at the exact insertion gap. It always creates empty explicit branch
blocks. Patch-plan v0.5 can resolve those blocks symbolically, but branch-body
payloads are limited to local declarations with literal initializers and typed
returns. A return must be last, must exactly match the current method return
type, and does not yet support async or task-like methods.
Deletion accepts only an unused local with no initializer or a literal
initializer, or a resolved pure static call with effect-free simple arguments.
Temporary introduction accepts only stable, pure value expressions in return or
local-initializer statements inside explicit blocks. Short-circuit,
division/remainder, decimal, and string operators remain excluded until builtin
operator effects model conditional evaluation, allocation, and throws.
Value reconnection accepts only an existing, unshadowed local or a local
produced by an earlier plan step that structurally dominates the containing
statement and has the same graph-safe semantic type as
the removable stable consumer. Patch-plan v0.5 composes every current bounded
operation and requires sidecars on target documents. A source handle may
reference only an earlier temporary introduction, root-block local-literal
insertion, or branch local-literal insertion in the same method with the same
expected type. Dominance still governs where that handle may be consumed. Only
insertion steps may repeat a target block; repeated non-insertion old-value
preconditions remain ambiguous. Project-wide edits require a multi-file
transaction coordinator.

## Follow-on milestone

M4.1 has now delivered the versioned `GraphRegion` model proposed here for
function, block, `if`, `then`, and `else` ownership, including early-return
projection. M4.2 has also delivered CFG reachability, dominance,
post-dominance, merge, and reducibility indexes on those regions. M4.3 can now
grow bounded assignment and data rewiring before nested-branch construction and
`while`/`break`/`continue`.
