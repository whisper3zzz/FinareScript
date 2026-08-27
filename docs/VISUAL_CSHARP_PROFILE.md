# Visual C# profile and opaque-code boundary

UnifiedScript publishes `unifiedscript.visual-csharp-profile/v0.3` as the
machine-readable contract for code that can be edited both as C# and as a
visual graph. `unifiedscript.visual-csharp-profile-report/v0.1` classifies every
method in a type that contains at least one `[Visual]` method.

## Authoring contract

- A `[Visual]` method is `visualGraph`: its body must pass the profile validator
  and may be edited from either C# or the Authoring Surface.
- An unmarked method in the same type is `opaqueCode` with a `sourceOnly`
  boundary. A call to it appears as one graph node, but its body is never
  expanded or edited by the graph.
- An opaque call must have graph-safe value parameters and a graph-safe return
  type. `ref`, `out`, and `in` parameters and ref returns are rejected. This
  keeps the node boundary deterministic even when its implementation uses full
  C# internally.
- A method that cannot satisfy the visual profile should normally move the
  unsupported logic into an unmarked helper and keep a graph-safe signature.
  Profile diagnostics include this action directly.

For example:

```csharp
[Visual]
int Resolve(int value)
{
    return ClampInSource(value);
}

// Source-only implementation; projected as one opaque call node.
static int ClampInSource(int value)
{
    try
    {
        return Math.Clamp(value, 0, 100);
    }
    catch (ArgumentException)
    {
        return 0;
    }
}
```

The projected call node publishes the exact boundary metadata:

```text
calleeBodyRepresentation = opaqueCode
authoringBoundary = sourceOnly
calleeBodyEditable = false
```

Graph and Authoring Surface JSON readers reject incomplete or contradictory
combinations of these fields.

## Supported visual body

Profile v0.3 admits blocks, local declarations, expression statements,
`if`, `while`, `do`, `for`, synchronous and asynchronous `foreach`, `switch`,
`break`, `continue`, `return`, and empty statements. Expressions include
literals, identifiers, `this`, member and conditional access, invocation,
binary and prefix-unary operators, increment/decrement, assignment, `await`,
casts, parentheses, and conditional expressions. Constant, declaration, type,
`var`, discard, relational, logical, and parenthesized patterns are supported.

The versioned report is the authority rather than this prose. It also lists the
current rejected-feature set, including `dynamic`, unsafe/pointer code, `goto`,
`yield`, lambdas, local functions, exception control flow, locks, query syntax,
`stackalloc`, mutable-ref iteration, borrowed async iteration, ref signatures,
default parameters, named arguments, and multiple local declarators.

## Diagnostics and consumers

`UnifiedScript.Cli analyze --out <path>` writes `<path>.profile.json` even when
a `[Visual]` body fails validation. This lets tooling show the exact unsupported
method and source span without inventing a graph. The authoring workspace v0.5
includes the same report; the browser prototype and native UE Workbench validate
it, show the number of source-only methods, and expose an explicit read-only
boundary when an opaque call node is selected.

The profile controls visual representability, not general C# compilation. Full
C# is deliberately available behind an opaque boundary, but calls across that
boundary still obey UnifiedScript's graph-safe type and lifetime contract.
