# Visual Authoring Model

## Product correction

The compiler/runtime foundation proves that text, HIR, Graph, source maps, and
CoreCLR execution can share semantic identity. It does not yet constitute a
visual authoring product.

The current `UnifiedScript.Graph` artifact projects executable method bodies.
It does not make type-level script state, defaults, exposure metadata, or asset
configuration first-class. A user could therefore still be forced to declare
state in a Blueprint or another engine-owned binary surface even if all method
logic lived in UnifiedScript. That would preserve the worst Blueprint/AI
failure mode and is not an acceptable product boundary.

Before UE binding, UnifiedScript needs a shared Declaration IR and a real
authoring vertical slice.

## The visual editor is not a code sentence renderer

The editor must not turn every C# line, identifier, literal, and arithmetic
operator into a separate card and wire. That reproduces Blueprint density with
different colors.

The intended granularity is:

- one card for a semantic action such as assignment, engine call, or return;
- one owned region for `if`, loop, cleanup, or other structured control flow;
- ordinary expressions rendered inline inside the owning action;
- reusable or cross-region values promoted to visible data dependencies only
  when the relationship materially helps understanding;
- function signatures and script state listed as declarations rather than
  executable nodes;
- canonical code always available as a split view and escape hatch.

Control flow is shown by ordered regions and a quiet execution rail. Wires are
reserved for non-local dependencies, not used as punctuation.

## Declaration IR

The next shared model should represent a script asset independently of UE,
Roslyn, and any editor layout:

```text
ScriptAsset
  semantic ID
  namespace / qualified name
  base/host contract
  state declarations[]
    semantic ID
    name
    type ID
    canonical default value
    mutability
    engine exposure metadata
      instance editable
      save game
      replicated
      category / display metadata
  function declarations[]
    existing semantic method ID
    parameters / return type
    Visual body HIR
```

The C# front end lowers properties/fields and their supported attributes into
this model. Visual declaration edits produce typed semantic patches against the
same IDs and are applied back to Roslyn source transactionally. AI can edit
either canonical source or the same compact patch protocol. UE is a consumer:
its adapter maps exposure metadata to generated/reflected host properties, but
does not own the declaration.

No gameplay variable may exist only inside a Blueprint asset in the target
workflow.

## Three synchronized surfaces

### Declaration surface

The asset sidebar owns state, functions, events, and bound engine APIs. State
rows show type, default, and exposure status. The inspector edits those values
and previews the exact canonical text that will be persisted.

### Behavior surface

The structure canvas shows semantic actions and owned control-flow regions.
It optimizes for reading intent at a glance, not for exposing compiler syntax.
Selecting a card reveals source location and semantic identity. Debug state is
painted on the same statement/region cards.

### AI surface

AI receives a coordinate-free semantic outline such as:

```text
state BaseDamage: int = 20
  expose: instanceEditable

function ApplyDamage(incomingDamage: int, critical: bool) -> int
  let scaled = incomingDamage * BaseDamage
  when critical
    scaled *= CriticalMultiplier
  scaled = Math.Clamp(scaled, 0, MaxDamage)
  return scaled
```

Layout positions, spline control points, collapsed panels, and viewport state
are excluded. AI proposes semantic declaration/body patches with explicit IDs,
types, and preconditions; it does not manipulate pixels or Blueprint nodes.

## Acceptance criteria for the connected vertical slice

1. A user can create a state declaration in text or visual mode without opening
   Blueprint.
2. Name, type, default, and supported exposure metadata round-trip both ways
   while preserving semantic identity.
3. The generated AI outline contains every declaration and behavior region but
   no layout coordinates.
4. Ordinary expressions remain inline; visual node count is substantially
   lower than syntax-node or Blueprint projections of the same behavior.
5. Selecting a declaration or action highlights the exact canonical source.
6. Text edits, visual edits, and AI patches pass the same compiler/semantic
   transaction gate.
7. Debug breakpoints and pause state use the same statement IDs displayed by
   the structure view.
8. UE exposure is generated from Declaration IR; there are zero required
   Blueprint-only variables in the sample workflow.

## Experience prototype

The dependency-free prototype in `prototype/visual-editor` demonstrates the
intended information density and interactions. It includes declaration editing,
Structure/Split/Code views, coordinate-free AI outline and patch preview, and a
debug-pause presentation. It is deliberately marked as a product-model
prototype: method-body Graph and live CoreCLR debugging exist in the backend,
but Declaration IR and UI-to-compiler transport are not connected yet.
