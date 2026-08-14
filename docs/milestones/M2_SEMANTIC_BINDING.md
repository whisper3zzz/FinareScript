# M2: Semantic Binding and Graph-Safe Types

## Goal

Move UnifiedScript from syntax-shaped graphs to a language-neutral semantic
model in which every relevant expression has a resolved type, every identifier
can point to its declaration, every call names an exact overload, and Graph
control flow reflects call effects.

## Implemented vertical slice

- single-source deterministic `CSharpCompilation` host
- runtime and `UnifiedScript.Core` metadata references
- Roslyn `SemanticModel` binding after Visual Profile validation
- language-neutral type, method, parameter, nullability, and effect descriptors
- HIR-node side tables for declared symbols, referenced symbols, types, and calls
- exact method signatures after overload resolution
- graph-safe visual method signature validation (`UVS2001`)
- pure bootstrap catalog for builtin operators and `System.Math`/`System.MathF`
- conservative effect defaults for unknown calls
- async/task-like latent classification
- explicit control ports and ordered control edges for calls, assignments, and awaits
- versioned semantic JSON and CLI emission
- golden HIR/semantics/graph artifact digests

## Acceptance evidence

The semantic test fixture proves that:

1. parameter and local types resolve to graph-safe `System.Single` descriptors;
2. separate uses of a local variable reference its declaration `SemanticId`;
3. `MathF.Max` is a data-only pure invocation;
4. a user method resolves to its complete containing type and parameter signature;
5. an unknown user method receives conservative read/write effects;
6. an impure void call has control input/output and no value output;
7. unsafe object parameters and return types stop graph emission;
8. identical inputs produce identical semantic JSON;
9. all three artifact schemas match their committed golden digests.

## Remaining M2 work

- multi-document compilation requests
- `.csproj`/MSBuild loading at the CLI boundary
- deterministic external reference manifests
- flow-sensitive nullable state per expression
- configurable pure/impure/latent API catalog
- supported object-handle and user-struct policies
- properties/indexers and accessor effects
- semantic diagnostics for banned namespaces and APIs
- generic type substitution in the language-neutral descriptor model
- richer evaluation regions before structured loops are introduced

## Deliberate exclusions

This milestone does not host CoreCLR, emit IL, edit C# from Graph, render a UI,
or bind Unreal Engine symbols. Those consumers must use the semantic contracts
rather than changing them.
