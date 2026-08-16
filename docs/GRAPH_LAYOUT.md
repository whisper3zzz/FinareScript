# Graph Layout Sidecar

## Purpose

`unifiedscript.graph-layout/v0.1` stores visual-editor state in a `.usgraph`
sidecar without making that state part of program semantics. C# source, HIR,
bound semantics, and `.usids` remain the program truth. Moving or collapsing a
node therefore cannot change compilation or persistent semantic identity.

The sidecar is keyed by function, node, and edge `SemanticId`. Persistent
`.usids` identities let a layout survive formatting, comments, and supported
semantic patches; the sidecar itself does not duplicate source spans,
compilation IDs, types, effects, or graph properties.

## Schema v0.1

A document contains active function layouts plus orphaned node layouts:

```json
{
  "schemaVersion": "unifiedscript.graph-layout/v0.1",
  "functions": [
    {
      "functionId": "us_...",
      "nodes": [
        {
          "nodeId": "us_...",
          "placement": "placed",
          "position": { "x": 40, "y": 210 },
          "size": { "width": 180, "height": 72 },
          "collapsed": false
        },
        {
          "nodeId": "us_...",
          "placement": "unplaced",
          "collapsed": false
        }
      ],
      "commentRegions": [],
      "routingHints": []
    }
  ],
  "orphanedNodes": []
}
```

Node placement is explicit:

- `placed` requires a finite position and a finite, positive size;
- `unplaced` forbids position and size, so a newly projected node cannot
  accidentally inherit coordinates from another node;
- `collapsed` is independent of placement and survives synchronization.

Comment regions have a layout-local string ID, title, finite positive bounds,
collapsed state, and a set of member node IDs. Routing hints are keyed by graph
edge ID and preserve an ordered list of finite control points. Comment region
IDs and routing edge IDs are unique within a function.

Serialization sorts functions, nodes, region IDs, region membership, routing
hints, and orphan keys. Control-point order is preserved because it describes a
route. Identical normalized inputs therefore emit byte-identical JSON.

## Synchronization contract

`GraphLayoutSynchronizer` accepts only a semantic Graph document plus a layout
sidecar and returns a new layout document. It has no API reference to HIR or an
identity sidecar and cannot modify either artifact.

For every current graph function:

1. a node with an active layout keeps its complete placement and collapsed
   state;
2. a matching orphan is restored with its previous layout;
3. a node with no prior entry receives explicit `unplaced` state;
4. comment regions survive, but membership is limited to currently present
   nodes;
5. routing hints survive only while the referenced edge remains present.

An active node missing from the new graph moves to `orphanedNodes`:

- `missingNode` means its function still exists;
- `missingFunction` means its complete function disappeared.

Orphans retain their last placement and collapsed state. This makes a transient
projection change recoverable if the same semantic ID later reappears, while
tombstoned identities remain visibly detached instead of being silently reused.
The synchronizer never invents semantic IDs.

## CLI workflow

First analyze source with persistent identities:

```bash
dotnet run --project src/UnifiedScript.Cli -- \
  analyze samples/Patching/DamageRules.cs \
  --identity samples/Patching/DamageRules.cs.usids \
  --out artifacts/layout
```

Initialize a layout in which every current node is explicitly unplaced:

```bash
dotnet run --project src/UnifiedScript.Cli -- \
  layout-init artifacts/layout/DamageRules.graph.json \
  --out artifacts/layout/DamageRules.usgraph
```

After a text or graph semantic edit produces a new Graph document, merge the
old visual state into it:

```bash
dotnet run --project src/UnifiedScript.Cli -- \
  layout-sync artifacts/updated/DamageRules.graph.json \
  --layout artifacts/layout/DamageRules.usgraph \
  --out artifacts/updated/DamageRules.usgraph
```

Both commands write atomically and refuse to overwrite their semantic Graph
input. `layout-sync` also requires a distinct output from its input layout, so a
caller can inspect or transactionally adopt the result. A placed example with a
comment region and routing hint is checked in at
`samples/Layout/DamageRules.usgraph`.

## Source control and merge policy

`.usgraph` is intended to be source controlled when teams want shared canvas
organization. Layout-only conflicts can be resolved without recompiling or
editing `.usids`; after resolving JSON, run `layout-sync` against the current
Graph artifact to remove stale active references and expose missing nodes as
orphans.

The v0.1 schema deliberately excludes viewport zoom, selection, per-user
preferences, colors, breakpoints, and automatic placement policy. Those are
editor/session concerns or later versioned additions. It also does not define a
three-way JSON merge driver yet.
