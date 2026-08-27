# Compact Nodes and Advanced Details

## Purpose

M8.2 separates information needed to read gameplay logic from information
needed to diagnose the compiler contract. The Authoring Surface still retains
every exact field, but it no longer renders semantic IDs, generated API paths,
raw effect flags, and source excerpts as the default node-selection experience.

This is a presentation policy only. It does not remove fields from Authoring
Surface v0.4 or change source, HIR, Graph JSON, `.usids`, `.usgraph`, patch
preconditions, or runtime behavior.

## Three information levels

### Canvas card

The default card shows:

- an author-facing title;
- a bounded short subtitle only where it adds meaning;
- visible control/data pins with concise Blueprint-style labels;
- inline literal/identifier values folded into their consumer pins.

Event Entries display their C# handler signature instead of the canonical
`/Script/...` event path. Member reads display the receiver as `From ...`
instead of repeating the member title. Long summaries are flattened and
bounded so generated signatures cannot dominate the canvas.

### Hover contract

Hovering a semantic node exposes a non-editable exact contract:

- graph kind and category;
- raw effect and `graphSafe` facts;
- resolved type and Type ID;
- generated method/member/event path and symbol identities when present;
- every port's IR name, direction, display type, and Type ID;
- source span and semantic node ID.

Pin tooltips continue to carry their own exact pin/type/inline-source identity.

### Advanced Node Details

Selecting an action shows a collapsed **Advanced Node Details** area. Expanding
it reveals:

- semantic ID with Copy ID;
- raw node/effect/type contract;
- complete typed port contract;
- sorted compiler and generated API metadata;
- declaration references and supported source-edit operations;
- exact source span, Open Source, and a read-only source preview.

Ordinary Pin Authoring remains outside the advanced area, so Set Value, Reset,
and Promote to Local are not buried under diagnostic metadata. Non-action tree
items retain the existing direct kind/source/identity inspector because they do
not compete with node authoring controls.

## Fidelity boundary

Compact text is derived from the lossless action record every time the graph is
rebuilt. Tooltip and Advanced values read the original properties and IDs,
never the compact subtitle. Display truncation therefore cannot become an edit
precondition or replace generated API identity.

The focused native Automation test proves the Event Entry card omits its UE
path while tooltip details retain that path, effects, exact payload types, and
semantic ID. It also verifies compact member-receiver text and generated
symbol/type identity in the on-demand contract.
