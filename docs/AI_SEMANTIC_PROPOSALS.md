# Typed AI semantic patch proposals

Finare treats an AI-authored change as a versioned, reviewable product
artifact rather than executable model output. An AI tool proposes a typed
`SemanticPatchPlanDocument`; the authoring host validates its exact contract,
previews the compiler-produced candidate, and commits only the proposal that
owns the reviewed SHA-256 token.

The current product does not bundle or trust one particular model. The browser
Workbench includes a deterministic suggestion producer for demonstration, and
external agents can emit the same contract through the CLI or loopback API.

## Proposal contract and identity

An untrusted producer starts with
`unifiedscript.ai-semantic-patch-proposal-draft/v0.1`:

```json
{
  "schemaVersion": "unifiedscript.ai-semantic-patch-proposal-draft/v0.1",
  "title": "Raise clamp minimum",
  "intent": "Replace the selected stable-ID literal.",
  "producer": {
    "tool": "gameplay-agent",
    "version": "1.0",
    "model": "optional-model-id"
  },
  "plan": {
    "schemaVersion": "unifiedscript.patch-plan/v0.21",
    "sourceCompilationId": "us_...",
    "semanticConfigurationId": "us_...",
    "steps": [
      {
        "step": "replaceLiteral",
        "stepId": "raiseMinimum",
        "targetNodeId": "us_...",
        "expectedValueText": "10f",
        "newValueText": "12f"
      }
    ]
  }
}
```

`POST /api/authoring/ai-semantic-proposal/draft` rejects unknown fields,
unsupported schemas, invalid stable IDs, malformed plan steps, untrimmed or
oversized metadata, and all ordinary Patch Plan contract violations. A valid
draft becomes `unifiedscript.ai-semantic-patch-proposal/v0.1` with a
deterministic `proposalId`. That ID hashes length-delimited title, intent,
producer identity, optional model, and canonical compact Patch Plan JSON, so
metadata or plan changes create a different identity.

The same canonical envelope can be created offline:

```powershell
finare ai-proposal-pack plan.json `
  --title "Raise clamp minimum" `
  --intent "Replace the selected stable-ID literal." `
  --producer gameplay-agent `
  --producer-version 1.0 `
  --model optional-model-id `
  --out proposal.json
```

Repeated packing of identical inputs produces byte-identical JSON.

## Preview and commit

The authoring endpoints are:

- `POST /api/authoring/preview/ai-semantic-proposal`;
- `POST /api/authoring/ai-semantic-proposal`.

Both accept the complete canonical proposal and the normal baseline,
transaction, label, group, and preview headers. Preview applies `proposal.plan`
through the existing `CSharpAuthoringWorkspace`, recompiles it, regenerates
Graph and authoring artifacts, and returns the stable-ID semantic diff plus
exact C# before/after text without writing source or reloading the runtime.

The preview token additionally binds `proposalId`. Commit must resend the same
proposal, transaction identity, baseline/group context, and token. A different
but otherwise valid proposal is rejected with `UAW1012`; a stale revision is
rejected with `UAW1005`; compiler or plan failure uses the existing `UAW1003`
rollback path. Accepted proposals use the same atomic source, identity,
artifact, history, CoreCLR reload, grouped draft, Undo, and Redo boundaries as
human graph edits.

The browser Workbench selects an explicit edit capability from the generated
Authoring Surface, creates a typed single-step plan, asks the host to assign its
proposal identity, and then opens the normal semantic/C# review dialog. Native
Slate recognizes the same proposal preview and commit envelopes and displays
the proposal identity in its existing diff review.

## Safety boundary

The proposal contract proves what change was reviewed; it does not prove that
the requested gameplay intent is desirable. Stable target IDs, old-value/type
preconditions, Roslyn compilation, semantic analysis, transaction isolation,
and human diff confirmation remain authoritative. Screen coordinates, prose,
model confidence, and hidden tool state never become executable semantics.
