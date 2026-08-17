# Control Flow Analysis

## Purpose

M4.2 derives a deterministic control-flow index from Graph v0.3 control edges
and semantic regions. It gives the compiler, visual editor, and future patch
algebra one executable definition of reachability, dominance, branch merging,
and reducibility instead of relying on node positions or lexical guesses.

The analysis is intentionally derived rather than embedded in Graph JSON.
Dominance and post-dominance sets can be quadratic in method size and become
stale after any edit; Graph nodes, edges, and regions are the durable contract,
while the CFG is recomputed from them.

## API

```csharp
GraphDocument graph = new HirGraphProjector().Project(hir, semantics);
GraphControlFlowAnalysis analysis =
    new GraphControlFlowAnalyzer().AnalyzeAndValidate(graph);
```

`Analyze` returns reducibility facts even for an irreducible input so tools can
diagnose it. `AnalyzeAndValidate` throws when a reachable cyclic component has
more than one valid entry/header and therefore cannot be represented as
structured control flow without changing behavior.

`HirGraphProjector` runs `AnalyzeAndValidate` before returning a Graph document.
Every Graph emitted by the normal analysis CLI has therefore passed CFG port,
entry, and reducibility validation.

## CFG nodes and arcs

Only Graph nodes with control ports participate in the CFG:

- function entry;
- variable/evaluate statements;
- branch and return nodes;
- effectful invocation, assignment, and await nodes.

Pure literal, identifier, member, operator, cast, and pure-call nodes remain
data dependencies. They continue to belong to semantic regions but are not
mistaken for executable CFG steps.

Real Graph control edges become CFG arcs after their source/output and
target/input ports are validated. Every control output must have at most one
successor. A branch has exactly the ordered `then` and `else` outputs; other
current nodes have at most one control output.

Each function receives a stable synthetic exit ID. The analyzer adds synthetic
arcs for:

- every `return` node;
- every unconnected control output, including an empty method or branch that
  falls off the end.

These arcs are analysis-only and do not modify Graph JSON. They make all normal
function exits converge on one node, which is required for post-dominance.

## Reachability

Reachability starts at the single function entry and follows CFG successors.
Every CFG node records `IsReachable`; the function also exposes sorted
`UnreachableNodeIds`.

Unreachable nodes remain in the index because C# may compile them with warnings
and persistent identities/layout can still refer to them. They receive empty
dominance and post-dominance relations rather than fabricated facts.

`HasCompleteExitCoverage` means every reachable CFG node has at least one path
to the synthetic exit. It does not claim that every possible path terminates;
that distinction matters once loops are added.

## Dominance

For every reachable node `n`, `DominatorIds` contains exactly the nodes that
occur on every entry-to-`n` path, including `n`. `ImmediateDominatorId` points
to the nearest strict dominator and is null for the entry.

The implementation uses deterministic fixed-point intersection over sorted
predecessors. Examples:

- a sequential declaration immediately dominates the following return;
- a branch dominates both branch bodies and their shared merge;
- a declaration in only `then` does not dominate the post-branch merge.

This Graph-level relation is distinct from M3's conservative lexical block
check. A later patch slice can replace or reinforce those lexical checks with
the executable CFG result.

## Post-dominance and merge facts

Post-dominance runs backward from the synthetic exit. For nodes that are both
reachable from entry and able to reach exit, `PostDominatorIds` contains the
nodes on every finite path to exit. `ImmediatePostDominatorId` is the nearest
strict post-dominator.

For a normal `if/else` whose paths rejoin, the branch's immediate
post-dominator is the first shared control node. That node becomes
`GraphControlFlowBranch.MergeNodeId`.

For early-return shapes, the nearest common post-dominator may be the synthetic
exit. The public `MergeNodeId` is then null because there is no real source/
Graph node at which both branches rejoin.

Each branch fact also exposes:

- the matching `if`, `then`, and `else` region IDs;
- the first CFG node reached from each branch port;
- whether each side exits the function before leaving its own region subtree.

This distinguishes:

| Shape | Then exits | Else exits | Real merge |
| --- | --- | --- | --- |
| both sides fall through | no | no | first shared node |
| `then` early return, implicit else | yes | no | none |
| both sides return | yes | yes | none |
| nested branch returns on one path | path-dependent | path-dependent | derived |

## Reducibility

The analyzer computes deterministic strongly connected components over
reachable CFG nodes. A cyclic component is reducible only when it has one entry
header and that header dominates every node in the component.

This accepts the structured shape required by a future `while` loop:

```text
entry → header ⇄ body
          └────→ exit
```

It rejects a two-entry cycle:

```text
          ┌→ B ─┐
entry → A │     ├→ ...
          └→ C ─┘
             ↑ ↓
```

Such a component cannot be converted to structured C#/visual regions without
introducing state or duplicating behavior. `Analyze` reports its node and entry
IDs; `AnalyzeAndValidate` rejects it.

## Current limits

M4.2 analyzes the current acyclic HIR and can classify manually supplied cyclic
Graphs, but the language profile still rejects loop syntax. The CFG is not yet
serialized, used as a patch target, or used to rewrite M3's lexical dominance
guards. It does not compute exception edges, short-circuit expression CFG,
async suspension-state transitions, loop headers/back-edge roles, control
dependence, or SSA form.

The next patch slice should consume these facts for bounded local assignment
and control-safe data rewiring before loop HIR/regions are introduced.
