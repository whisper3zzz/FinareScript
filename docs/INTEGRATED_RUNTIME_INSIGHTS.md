# Integrated runtime insights

Status: M12.3 product slice, schemas
`unifiedscript.authoring-insight-request/v0.1` and
`unifiedscript.authoring-insight/v0.1`.

## Purpose

Runtime Insight joins three versioned sources that previously appeared in
separate tools:

- the current Authoring Surface supplies stable method/node identities, graph
  action titles, and exact C# source spans;
- `gameplay-profile/v0.3` supplies bounded frame, allocation, semantic-node,
  and Unreal boundary costs;
- the generated API browser supplies the Contract route catalog for
  DirectThunk, CallFrame, DataPlaneOperation, and ReflectionFallback.

The result is one deterministic snapshot ordered by measured node cost. Each
row can navigate back to the same graph action and C# source line used by the
debugger. A row retains its semantic IDs when the current Surface cannot
resolve it and is shown as **unresolved** instead of jumping to a plausible but
incorrect node.

## Workbench workflow

In Unreal, open the Finare debugger for a running component:

1. choose **Start Profile**, reproduce the interval, then **Capture Profile**
   or **Stop Profile**;
2. inspect the **Semantic Hotspots** list directly below the live controls;
3. select any row to focus its graph action and source span; the row and
   tooltip show total/hit cost, residual `managedCode`, and every attributed
   Unreal boundary;
4. optionally choose **Save JSON** for comparison or offline inspection.

In the browser Workbench, choose **Runtime Insights**, import that saved JSON,
and use the three columns to inspect aggregate boundaries, ranked semantic
hotspots, and one node's graph/source/runtime details. **Focus Graph + Source**
returns to the exact Surface action. **Open API Route** opens the generated API
browser filtered to the dominant Contract route.

The loopback endpoint is:

```text
POST /api/unreal/authoring-insight
```

The request contains one bounded v0.3 trace and an optional debugger-focused
method/node pair. The server always resolves it against its current Surface;
clients cannot submit source labels or locations.

## Attribution boundary

Node and aggregate times/counts are measured values from the trace. The
snapshot adds residual `managedCode` as node or frame time not attributed to a
bridge/data-plane boundary. Percentages are derived from those integer
nanosecond totals.

The API catalog join is intentionally route-level. For example, a
`directThunk` cost is exact runtime attribution, while the listed DirectThunk
members are examples from the generated Contract that use that route. The
current profiler does not claim that every catalog example was called by the
node, nor does it invent per-member timing. Exact per-member tracing can be
added in a later bounded schema without changing this distinction.

## Safety and determinism

- Input is limited by the existing 1 MiB loopback request cap plus the trace's
  4,096-frame/node and bounded boundary limits.
- Source and semantic configuration IDs in the response always come from the
  active server workspace.
- Duplicate semantic locations, negative counters, unsupported schemas,
  malformed pressure records, and empty captures are rejected before display.
- Sorting is deterministic: cost, hit count, method ID, then node ID.
- Import and inspection are read-only; no C# source, graph, history, generated
  binding, or runtime generation is changed.

The contract tests cover source resolution, stale-node visibility, residual
managed cost, generated route counts/examples, deterministic JSON, and the
live HTTP join. Native Automation continues to validate strict v0.3 profile
parsing and semantic graph navigation.
