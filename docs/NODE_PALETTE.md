# Native Node Palette

## Purpose

M8.2 adds a Blueprint-style discovery surface to the native Unreal authoring
workspace. The Palette is a view over compiler- and contract-derived actions;
it is not a second node registry and it never mutates source directly.

The same action ID is used by the docked Palette and `SGraphEditor` context
menu. Placement always builds a structured declaration/semantic patch and posts
it through the Workbench transaction boundary. Failed construction, stale
preconditions, compilation failure, or publication failure leaves the
compiler-owned graph unchanged.

## Context generation

`RebuildNodePalette` derives the available action set from the active Surface:

- built-in locals, expressions, and control nodes require the semantic types
  used by their structured patch templates;
- statement actions require an explicit editable source block;
- Break is shown only inside loop or switch ancestry, and Continue only inside
  loop ancestry;
- reflected properties and functions require a compatible typed receiver,
  result type, and every required argument in the active Visual function;
- reflected delegates create declaration-level Event Entries and remain
  discoverable at function scope without a statement block.

The generated reflection catalog remains the product API authority. Palette
filtering only prevents known-invalid choices; Roslyn analysis and the
Workbench remain the final correctness authority.

## Search and ranking

`FUnifiedScriptNodePaletteModel` performs deterministic token search across:

- author-facing title;
- hierarchical category;
- tooltip;
- stable action ID;
- explicit built-in or reflection-derived keywords.

Every query token must match. Non-empty searches rank textual relevance first,
then Favorites, Recent rank, category, title, and action ID. An empty query
ranks Favorites first, followed by the eight most recently accepted actions and
then the stable alphabetical catalog. Identical inputs produce identical order.

## Interaction and persistence

- `Ctrl+Space` focuses and selects Palette search from anywhere in the authoring
  widget;
- Enter places the first filtered result;
- double-click or the row `+` button places the chosen action;
- the star button toggles a Favorite;
- a node reaches Recent only after a successful Workbench response, including
  an accepted grouped-draft stage.

Favorites and Recents are stored in `GEditorPerProjectIni` under the
`UnifiedScript.AuthoringSurface.NodePalette` section. They are user preferences,
so they do not enter C#, `.usids`, `.usgraph`, Graph, Surface, source control, or
runtime generations.

## Current boundary

This tranche closes context search, Favorites/Recents, and the first keyboard
placement workflow. Drag-from-pin creation does not yet auto-connect a new
node, and inline pin promotion/default editors, comment/reroute authoring,
minimap/viewport persistence, and broader Blueprint shortcuts remain later
M8.2 slices. The Palette does not claim those operations until their structured
source contracts exist.
