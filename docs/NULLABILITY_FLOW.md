# Flow-sensitive nullability

M8.5 promotes Roslyn's nullable flow analysis into the versioned Finare
semantic and Graph contracts. A type annotation and the value known at one
control-flow location are deliberately separate facts:

- `TypeDescriptor.Nullability` records the effective type annotation used by
  that bound expression (`unknown`, `oblivious`, `notNull`, or `nullable`).
- `BoundNodeInfo.NullFlowState` records Roslyn's state at that exact expression
  use (`unknown`, `notNull`, or `maybeNull`).

Semantics schema `unifiedscript.semantics/v0.6` serializes both. This avoids a
client guessing null safety from source spelling or from a declaration that may
have been narrowed, assigned, or merged before the selected node.

## Control-flow behavior

For this ordinary Visual method:

```csharp
static int Measure(string? value)
{
    if (value == null)
    {
        return 0;
    }

    int guarded = value.Length;
    value = null;
    return guarded + (value?.Length ?? 0);
}
```

the identifier used by `value.Length` is `notNull` because the null branch
returns. The later conditional-access receiver is `maybeNull` because the
assignment invalidates that narrowing. The binder asks the Roslyn semantic
model for every source-backed HIR expression, so branch conditions, early
returns, assignments, pattern variables, coalescing, and conditional access all
share the compiler's flow result rather than a second custom analysis.

Definitions receive a conservative initial state from their declared symbol:
non-nullable reference/value types are `notNull`, nullable references and
`Nullable<T>` are `maybeNull`, and oblivious metadata remains `unknown`.

## Graph and Authoring Surface

Every semantically bound Graph node publishes `nullFlowState`. A node with a
resolved type also publishes `typeNullability`:

```json
{
  "typeNullability": "nullable",
  "nullFlowState": "maybeNull"
}
```

These properties pass unchanged through `AuthoringActionSurface.Properties`,
so Slate, browser, and AI clients consume one compiler-owned fact. `GraphJson`
rejects unknown enum values instead of silently interpreting them. Existing
exact-type connection guards still apply: a nullable source cannot masquerade
as a non-null input, while a Roslyn-narrowed expression can carry the effective
non-null type and state at the safe use site.

## Diagnostics and boundaries

This metadata does not suppress C# nullable diagnostics and does not invent
cross-method lifecycle guarantees. A field assigned in `OnBeginPlay` is not
automatically considered non-null in `OnTick`; code must retain a guard,
conditional access, constructor guarantee, or explicit null-forgiving boundary
when Roslyn cannot prove the lifecycle fact. Deliberate source-only escape
points use the versioned opaque-code boundary documented in
[`VISUAL_CSHARP_PROFILE.md`](VISUAL_CSHARP_PROFILE.md).

Round-trip semantic digests include `NullFlowState`, so Visual and C# candidates
cannot pass equivalence after changing a guard or assignment in a way that
changes null safety even when their nominal CLR types still match.
