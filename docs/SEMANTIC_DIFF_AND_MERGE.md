# Semantic Graph Diff and Merge

UnifiedScript reviews source-backed behavior as stable compiler semantics, not
as serialized Blueprint editor state. Graph v0.13 remains a generated
projection of canonical C#; the review layer identifies meaningful function,
node, edge, region, value, and binding changes while excluding source spans,
presentation labels, canvas positions, and other layout noise.

## Versioned contracts

`unifiedscript.semantic-graph-diff/v0.1` records:

- the baseline/target compilation IDs and shared semantic-configuration ID;
- added, removed, and modified Visual functions;
- stable function/entity/change IDs;
- typed `signature`, `controlFlow`, `dataFlow`, `value`, `binding`, or
  `structure` categories;
- the changed semantic facet plus exact normalized before/after values.

`unifiedscript.semantic-graph-merge/v0.1` records a three-way decision for
each changed Visual function. Both schemas are strict, deterministically
ordered, and validate their own summaries. Empty diffs are valid and mean that
only non-executable source/declaration presentation changed.

Fingerprints deliberately omit `SourceSpan` and node labels. They include
stable IDs, qualified function names, node kinds/ports/properties, edge
endpoints, and region structure. Inputs with different semantic-configuration
IDs are rejected rather than compared as though they used the same compiler
policy.

## CLI review

```powershell
unifiedscript semantic-diff baseline.graph.json target.graph.json `
  --out semantic-diff.json --fail-on-change

unifiedscript semantic-merge baseline.graph.json ours.graph.json theirs.graph.json `
  --report semantic-merge.json --out merged.graph.json
```

`semantic-diff` returns exit code `3` with `--fail-on-change` when semantic
changes exist, while still writing the review report. `semantic-merge` returns
`3` on conflict, writes the conflict report, and does not create a merged
Graph. A clean merge publishes its report and Graph transactionally.

The merge boundary is intentionally conservative. Disjoint function changes,
one-sided changes, identical changes, additions, and removals merge
automatically. Different changes to the same Visual function produce an
explicit conflict containing both typed diffs. v0.1 does not attempt node-level
auto-merge inside one function and never edits C# or Blueprint assets by
itself; the report is evidence for a subsequent reviewed source transaction.

## Workbench review

Authoring preview protocol `unifiedscript.authoring-preview/v0.2` embeds the
same semantic diff beside exact C# before/after files. Both native Slate and
the browser Workbench show the typed stable-ID summary before commit. The
accepted client resends the unchanged transaction identity, payload, baseline,
group identity, and SHA-256 preview token. Cancel performs no source, identity,
artifact, history, or runtime write.

Declaration-only edits may legitimately show zero graph changes while still
showing exact C# text. Behavior edits expose the changed executable values,
bindings, and control/data structure independently of line moves or graph
layout. Server-side compile, semantic preconditions, atomic publication,
rollback, and CoreCLR reload remain the commit authority.

## Guarantees and limits

- C# is still canonical; Graph/diff/merge documents are generated review
  artifacts, not a second source of truth.
- Stable semantic identities allow review across formatting and line movement,
  but rewritten code that loses identity continuity is not guessed into a
  match.
- Merge compatibility requires Graph v0.13 and the same semantic configuration.
- Clean Graph merge output proves structural mergeability, not a committed C#
  merge. Source regeneration/patching must pass the normal authoring pipeline.
- Serialized `.uasset` or Blueprint graph diffs are outside this contract.
