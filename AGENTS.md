# AI Working Rules

Deliver the feature, fix, or judgment the user needs. Do not optimize for more
process, reports, or tool calls. These rules reduce wasted work without weakening
correctness, protection of user files, or necessary testing.

## Default to implementation

- Follow the current explicit request. Consult the cursor in `docs/ROADMAP.md`
  only for an unqualified request to continue iteration. Explanation, status,
  and review requests do not authorize incidental code changes.
- Read enough relevant source and working-tree state to locate the issue, then
  implement. Continue from reliable context; do not rescan the repository,
  establish a baseline, or reverify the same facts every turn.
- `docs/HARNESS.md` is an on-demand tool reference. `status`, `plan`, and
  profiles are optional. When the change and relevant tests are already clear,
  use the existing commands directly.
- Solve the current requirement completely. "Minimal change" does not mean
  doing only the easiest fragment. Make necessary related changes and local
  refactors; avoid unrelated cleanup or frameworks for hypothetical needs.
- Do not create plans, ledgers, or acceptance documents for ordinary tasks.
  Use `docs/plans/TEMPLATE.md` only for work spanning sessions, material
  architectural decisions, or an explicit request. Keep useful decisions and
  unfinished work, not a transcript of execution.

## Validate according to risk, not ritual

Choose checks that can catch concrete errors introduced by this change. Make
this judgment internally; a written justification or scorecard is not required.

| Change | Default verification | When to broaden coverage |
| --- | --- | --- |
| Wording, documentation, AI rules | Review content, references, and contradictions | Test the relevant entrypoint if executable examples or configuration semantics change |
| Local C#, Python, or tooling logic | Relevant tests and necessary project compilation | Shared behavior changes, regressions extend beyond the local area, or focused tests are insufficient |
| Generators, serialization, ABI | Relevant contract tests; check generated results when affected | Add the appropriate UE build/Automation when native consumers or compatibility are affected |
| UE behavior, World lifecycle, Blueprint calls | Build affected targets and run relevant CMD Automation | Run the complete relevant suite for broad impact or milestone acceptance |
| Cook, distribution, loading, releases | Affected packaging/runtime path | Apply the corresponding acceptance requirements for a release, complete Demo delivery, or explicit performance claim |

- File paths suggest possible impact, not mandatory checks. Harness profiles
  are coarse-grained bundles. Run focused tests directly when appropriate;
  do not run an entire dependency chain merely to use the Harness.
- One successful check is normally enough for the same effective inputs and
  verification purpose. After failure, diagnose, fix, and rerun affected checks.
  Do not repeat smoke tests, dry runs, full suites, or hash checks without cause.
- If the final edit only changes explanatory documentation, do not rebuild,
  rerun UE, or repackage just to make the latest report green. When a report is
  `stale`, explain any test-relevant differences; never relabel old evidence as
  a new result.
- Stop when the requirement is met, relevant checks are sufficient, and material
  risks are resolved or clearly handed off. Deliver without another precautionary
  acceptance round. Formal release requirements remain unchanged.
- Do not hide failures, remove necessary tests, or fabricate results. Briefly
  identify unrelated failures without expanding the repair scope unilaterally.

## Additional process needs a concrete trigger

- Backups: use them when there is a real risk of loss and existing recovery is
  insufficient. Do not create extra backup directories for ordinary text edits.
- Baselines and hashes: use them for performance comparisons, deterministic
  generation, transfer integrity, or specific identity/environment-contamination
  issues. Let existing tools record them; do not manually duplicate that work.
- Dry runs: use them for operations with side effects when a preview can expose
  mistakes, not as a prerequisite for every command.
- Full matrices and clean checkouts: use them for release acceptance, broad
  impact, or confirmed environment contamination, not as the starting condition
  for every edit. If protected files conflict, first assess whether the relevant
  verification actually needs that environment.
- Do not turn these triggers into another mandatory approval form. Make routine
  technical judgments independently; surface changes to scope, cost, or risk.

## Communicate outcomes, not procedural theater

- Start by stating what this turn will solve. During long tasks, briefly report
  new findings, results, or blockers. Do not repeat the plan without new
  information; during a long wait, simply say what is still running.
- Avoid stock transitions such as "establish a single baseline," "create a
  traceable backup," "make the smallest change," "run another smoke test," or
  "complete the acceptance matrix." When an action is genuinely needed, name
  the concrete target and reason.
- Do not narrate each file read, search, or hash check. Gate counts are not a
  substitute for explaining what was delivered.
- Final responses should normally state what changed, the key verification
  conclusion, and any material limitations. Omit irrelevant unrun checks.
  Leave detailed logs in tool output; expand when the user requests an audit
  or full report.

## Non-negotiable boundaries

- Preserve existing user changes and respect `protectedPaths` in
  `harness.local.json`. Do not edit, regenerate over, stage, or restore those
  files without authorization. Do not commit, push, or expand into external
  projects without authorization.
- Do not spawn subagents unless explicitly requested. Seek confirmation for
  irreversible actions, conflicting requirements, or material scope changes;
  make ordinary implementation choices independently.
- Keep Core/Graph/Semantics engine-neutral. Contract changes must cover the
  affected producers, serialization, and consumers. Generate generated files
  through their generators. Preserve stable IDs, World/generation lifetimes,
  and transactional failure behavior.
- Prefer CMD/NullRHI. Interactive Editor verification is for visual appearance
  and interaction. Consult `CONTRIBUTING.md` and `docs/ARCHITECTURE.md` for
  architectural details, and `docs/HARNESS.md` for tool usage, as needed.
