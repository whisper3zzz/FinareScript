# Switch and pattern control flow

UnifiedScript HIR v0.8 and Graph v0.13 preserve supported C# `switch`
statements instead of lowering them into nested branches. The compiler keeps the
governing expression, ordered sections, labels, pattern variables, optional
`when` guards, and section bodies as stable semantic nodes.

## Supported profile

Visual C# currently accepts:

- classic constant labels such as `case 0:`;
- declaration and type patterns such as `case int value:` and `case string:`;
- `var` and discard patterns;
- relational patterns such as `case < 0:`;
- bounded `and`, `or`, `not`, and parenthesized combinations;
- one optional `when` guard per pattern label;
- multiple labels sharing one section and one optional `default` label.

Recursive property, positional, list, and other patterns that cannot yet be
represented losslessly are rejected with `UVS1022`. Pattern constants and guards
must also satisfy the normal graph-safe type and effect rules.

## Graph and CFG ownership

Each `switch` owns one `switch` region and an ordered `switchSection` child for
every C# section. A section region's `sourceNodeId` is the synthetic HIR body
block, not the label syntax node. This gives graph editing an exact block target
and sibling list while `switchCase` actions remain visible compiler-owned labels,
not deletable statements.

The governing expression is evaluated once. Case and guard routing is explicit,
pattern variables are typed data nodes scoped to their matched section, and CFG
analysis records the label set, default label, section regions, break nodes, and
single continuation. `break` targets the nearest loop or switch; `continue`
continues to require the nearest loop, including when a switch is nested inside
that loop.

## Authoring status

Text-authored switches round-trip through C#, HIR, Graph, Authoring Surface, and
CFG with stable identities. The UE palette can insert `break` inside a selected
switch section and rejects `continue` when no loop encloses that section. Visual
creation, relabeling, reordering, and deletion of whole switch sections remain
part of M8.1's structured-control editing work; the editor never manufactures an
invalid intermediate C# switch.
