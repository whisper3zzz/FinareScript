# Structured Loop Foundation

## Purpose

M4.5 introduces `while`, `break`, and `continue` as first-class constructs in
the shared language model. They are not reconstructed from arbitrary cyclic
edges and they do not use a second execution model. C# text, HIR, semantic
bindings, Graph regions, and the derived CFG all identify the same loop and the
same transfer targets.

M4.5 was deliberately forward-only: existing C# loops could be visualized,
serialized, analyzed, and identity-reconciled. M4.6 now builds on this contract
with bounded Graph-authored while insertion and symbolic body population; see
[Bounded Graph-Authored Loop Patching](LOOP_PATCHING.md).

## HIR contract

HIR schema `unifiedscript.hir/v0.2` adds three polymorphic statements:

- `HirWhileStatement`, containing one condition expression and one body block;
- `HirBreakStatement`, representing a transfer to the nearest loop exit;
- `HirContinueStatement`, representing a transfer to the nearest loop header.

The Visual C# profile accepts braced and single-statement `while` bodies.
Single statements lower through an explicit implicit HIR block, just as an
unbraced `if` body does. `break` and `continue` are accepted only when a
`while` ancestor exists; malformed standalone transfers receive
`UVS1020` before Graph projection.

Traversal, semantic expression binding, persistent identity reconciliation,
identity rewriting, and patch indexing all descend through the condition and
body. Loop, break, and continue identities therefore survive formatting and
comment-only changes under the same `.usids` rules as existing statements.

## Graph v0.4 contract

Graph schema `unifiedscript.graph/v0.4` adds two region kinds:

| Kind | Source owner | Contents |
| --- | --- | --- |
| `loop` | `HirWhileStatement` | condition nodes and the `loop` decision node |
| `loopBody` | the HIR body block | statements and nested regions in the body |

A `loop` region has exactly one ordered `loopBody` child and directly owns its
source loop node. The loop node has:

- control input `in`;
- control outputs `body` and `exit`;
- data input `condition`.

`break` and `continue` are ordinary source-backed control nodes with `in` and
`then` ports. They keep their lexical direct region owner—for example, a
continue inside an `if` remains inside that `then` region. Their semantic loop
ownership is recorded by the derived CFG rather than duplicating a Graph node
across regions.

The false `exit` output and every break from the nearest loop converge on the
same following control node, or the stable synthetic function exit when the
loop is terminal. Normal body fallthrough and every continue converge on the
same header entry.

## Repeated condition evaluation

A while condition may contain effectful calls or assignments admitted by the
current expression profile. Those effects must execute before the first test
and again before every later test.

The projector therefore distinguishes the source loop node from its effective
header entry:

```text
incoming ──> condition effects ──> loop decision
                 ^                    │
                 │                    ├── body ──> ... ──┐
                 │                    └── exit           │
                 └──────── continue / fallthrough <─────┘
```

For a pure condition, the loop decision itself is the header entry. For an
effectful condition, the first effect node is the header entry. Back edges and
continues target that first effect, so projection cannot accidentally evaluate
an impure condition only once.

## Derived CFG loop facts

`GraphControlFlowFunction.Loops` exposes one deterministic
`GraphControlFlowLoop` per source loop:

- loop decision node, loop region, and body region IDs;
- effective header entry;
- first body successor;
- loop exit successor;
- real back-edge arc IDs;
- break and continue node IDs owned by that nearest loop.

The analyzer validates ordered `body/exit` ports, one header entry per loop
region, and exact nearest-loop targets. A continue redirected to an outer loop
or to the exit is invalid. A break redirected to the header or to another
continuation is invalid.

Existing SCC analysis remains authoritative for reducibility. A cyclic loop
must have one entry that dominates the entire component. An empty body becomes
a valid self back edge. A loop whose body always returns or breaks may have no
cyclic SCC, while still retaining explicit loop metadata—source structure and
runtime reachability are related but not conflated.

## Identity, layout, and patch boundaries

Loop region IDs derive from the persistent while-statement ID plus their
structural role. Existing loop nodes therefore retain visual placement when
source formatting changes. New loop nodes remain unplaced through the normal
layout synchronizer, and no CFG cache is persisted into `.usgraph`.

The patch index recognizes loop bodies and conditions, so existing bounded
literal/operator/value operations can still resolve nodes inside a loop.
Insertion after a direct `break` or `continue` is rejected as unreachable, and
patch CFG anchors do not mistake those terminal transfers for normal block
fallthrough.

The current loop slices do not provide:

- moving statements into or out of a loop;
- moving or deleting an existing while loop (M4.7 can replace its condition);
- changing a break/continue target;
- `foreach`; post-test `do ... while` is now covered separately by
  [DO_WHILE_LOOPS.md](DO_WHILE_LOOPS.md);
- restructuring/replacement of clauses on an existing `for`; M4.16 admits
  text-authored loops and M4.17 adds bounded whole-loop construction through
  [FOR_LOOPS.md](FOR_LOOPS.md);
- constant-condition folding or proof of loop termination;
- short-circuit expression CFG, exception edges, or async state-machine edges.

Those operations must extend the same HIR/Graph/CFG contract rather than
introducing editor-only control semantics.

## Sample

Analyze the checked-in loop sample with persistent identity:

```bash
dotnet run --project src/UnifiedScript.Cli -- \
  analyze samples/Loops/Countdown.cs \
  --identity samples/Loops/Countdown.cs.usids \
  --out artifacts/loops
```

The emitted HIR contains explicit while/break/continue statements, Graph v0.4
contains `loop` and `loopBody` regions, and CFG analysis reports the loop
header, exit, back edges, and nearest-loop transfers.
