# Null-Conditional Access

M4.15 adds C# null-conditional member access (`receiver?.Member`) to the shared
source/HIR/Graph contract. The operation is preserved as conditional evaluation;
it is not rewritten into a normal member read, an eager function call, a Boolean
conditional, or an editor-only node.

The current strict-reader contracts are:

- HIR `unifiedscript.hir/v0.8`;
- Graph `unifiedscript.graph/v0.13`;
- standalone patch `unifiedscript.patch/v0.22`;
- patch plan `unifiedscript.patch-plan/v0.21`.

## HIR structure

Roslyn represents `value?.Length` with two distinct executable syntax nodes:
the full conditional access and the member binding that is evaluated only after
the receiver is known to be non-null. HIR v0.4 preserves that distinction:

```text
HirConditionalAccessExpression
├── Receiver: HirIdentifierExpression("value")
└── WhenNotNull: HirConditionalMemberBindingExpression("Length")
```

The member binding deliberately has no ordinary HIR target child. Its target is
the receiver owned by the enclosing conditional-access node. Keeping that
relationship explicit prevents a chained form from evaluating the receiver a
second time.

The same representation also supports text-authored conditional calls and
chains. For `value?.Trim()`, `WhenNotNull` is an invocation whose target is a
conditional member binding. For nested conditional access, the inner operation
establishes a new receiver context without losing the outer context required by
its own receiver expression.

Both new nodes participate in HIR traversal, semantic binding, patch indexing,
structural fingerprints, persistent identity reconciliation, identity rewriting,
source-span matching, and JSON polymorphism. Formatting-only source changes can
therefore preserve their `.usids` identities just like existing expression
nodes.

## Graph evaluation contract

Graph v0.8 introduced one `conditionalAccess` node with two data inputs and one data
output:

```json
{
  "kind": "conditionalAccess",
  "label": "?.",
  "ports": ["receiver", "whenNotNull", "value"],
  "properties": {
    "evaluation": "conditionalWhenNotNull",
    "whenNotNullEvaluation": "whenReceiverNonNull",
    "resultSelection": "whenNotNullOrNull"
  }
}
```

The Graph data edges make both the explicit and implicit relationships visible:

```text
receiver.value ───────> conditionalAccess.receiver
       └──────────────> conditionalMemberBinding.target
memberBinding.value ──> conditionalAccess.whenNotNull
conditionalAccess.value ──> consumer
```

A shared VM must evaluate `receiver` exactly once. If the result is null, it
must skip the complete `whenNotNull` subtree and produce null. If the result is
non-null, that same receiver value supplies the implicit target of the
conditional member binding, and the selected subtree supplies the final value.
The visual runtime must not treat the two incoming data edges as permission to
evaluate both inputs eagerly.

Every node retains its bound `typeId` and `resolvedType`. The full conditional
access carries the lifted nullable result; the selected member/call carries its
ordinary Roslyn-bound type.

## Type boundary

Roslyn remains authoritative for C# syntax, overload resolution, member
selection, and candidate types. The Visual Profile then requires:

- a graph-safe receiver type;
- a graph-safe selected value and graph-safe result;
- a non-void nullable result;
- selected and result metadata types that differ only by nullability.

Unsupported void or hidden-conversion shapes produce `UVS2007`. This keeps the
lift operation visible to the future VM instead of relying on a conversion that
exists only inside Roslyn.

M4.15 records declared and bound nullability. It does not yet serialize
flow-sensitive nullable-state facts.

## Effect safety

The receiver subtree may contain effects because C# always evaluates it exactly
once. Those effects remain in the containing statement's normal control chain.
For example, an unknown `Observe(value)` call receives control ports and is
sequenced before the return in:

```csharp
return Observe(value)?.Length;
```

The complete `whenNotNull` subtree is conditional. Graph does not yet have a
conditional effect subgraph for a lazy data input, so assignment, `await`,
increment/decrement, or a call with effects other than `None` produces
`UVS2006`:

```csharp
return value?.Trim(); // rejected unless Trim is explicitly catalogued pure
```

Pure member selection and catalogued-pure calls are valid, but a consumer must
still honor `whenNotNullEvaluation`. When the selected path needs observable
effects, use explicit statement-level control flow until conditional effect
regions are represented.

## Visual construction

Patch v0.20 adds a bounded `conditionalMemberAccess` expression payload:

```json
{
  "expression": "conditionalMemberAccess",
  "receiver": {
    "expression": "localReference",
    "sourceDefinitionId": "us_...",
    "expectedName": "value",
    "expectedTypeId": "us_..."
  },
  "memberName": "Length",
  "expectedTypeId": "us_..."
}
```

The receiver is any existing closed graph-authored expression admitted by the
bounded algebra. In this slice, visual construction emits only a simple member
binding with one valid unescaped identifier. It does not synthesize invocation
arguments, generic names, indexers, extension-method calls, null-forgiving
operators, or hidden conversions.

Construction accepts reference-like `string` receivers and nullable graph-safe
scalar receivers, and requires a nullable declared result. Ordinary C# is then
reanalyzed. The patcher correlates the emitted tree back to HIR and checks every
authored expression node against Roslyn's exact pre-conversion type as well as
the bound semantic type. A member that exists only through an implicit result
conversion therefore fails atomically instead of leaking a conversion into the
shared-VM contract.

Patch-plan v0.20 treats `receiver` as the canonical editable child of a
`conditionalMemberAccess` payload. A producing step publishes the actual
post-analysis conditional-access root; a later
`replaceProducedExpressionInput` step can replace its receiver without
predicting a generated semantic ID. The ordinary `replaceExpressionInput`
operation edits the receiver of an existing conditional-access node with exact
old-endpoint, input-type, result-type, stable-pure, reachability, trivia, and
identity checks.

The `whenNotNull` port is serialized for evaluation but is not exposed as a
general source-edit port in M4.15. Replacing it with an arbitrary ordinary
expression could produce invalid member-binding syntax or detach the implicit
receiver. Future call/member construction must introduce a closed conditional
binding payload before that edit becomes safe.

## Checked-in sample

Generate the sample artifacts with:

```bash
dotnet run --project src/UnifiedScript.Cli -- \
  analyze samples/Expressions/NullConditionalRules.cs \
  --out artifacts/null-conditional
```

The sample covers a direct receiver, an effectful always-evaluated receiver,
and composition with a null-coalescing receiver. Every `?.` emits HIR v0.7 and
Graph v0.12 conditional-access/member-binding nodes with the explicit receiver
evaluation contract.

## Deliberate limits

- Conditional effect subgraphs are not represented; effectful selected calls
  are rejected.
- Visual construction is limited to simple member access, while supported pure
  text-authored calls and chains can still project through the same HIR shape.
- The conditional `whenNotNull` subtree is read-only as a general patch port.
- Conditional element/indexer binding (`?[]`) is not admitted.
- Flow-sensitive nullable state and redundant-access warnings are not
  serialized.
- Null-coalescing assignment (`??=`), null-forgiving (`!`), unconstrained
  generic receivers, dynamic dispatch, and user-defined hidden conversions
  remain outside this slice.
