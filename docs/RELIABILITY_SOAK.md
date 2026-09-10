# Reliability and capacity soak

M11.1 turns the existing lifecycle regression into a configurable reliability
gate. The same `Finare.Unreal.WorldHost.LifecycleStress` implementation
runs in two independent UE 5.8.1 processes:

- a normal `WindowsEditor` process;
- an `UnrealEditor-Cmd -server` process that reports
  `dedicatedServer: true` and exercises server-only runtime branches.

This is dedicated-server **process mode**, not evidence for a cooked
`WindowsServer` target. Server-target build, cook, staging, and clean-machine
closure belong to the M11.2 compatibility matrix.

## What the soak exercises

Each lane creates and tears down travel, PIE, and concurrently active Worlds.
It deliberately reuses owner/request identifiers across Worlds, repeatedly
reloads a collectible managed generation, broadcasts its reflected delegate,
and alternates successful reloads with missing-type rollback failures.

The expanded v0.4 workload also performs:

- transient UObject registration/release followed by UE garbage collection;
- exact async-service capacity fill, capacity-plus-one rejection, cancellation,
  and oversized-payload rejection;
- command execution beyond the completion-event capacity so deterministic
  event dropping and telemetry are exercised;
- capacity-plus-one command, trace, and overlap reservations;
- physical-memory baseline, sampled high-water mark, final growth, and a
  caller-selected budget;
- exact verification of every World's ordered shutdown phases;
- bounded process crash breadcrumbs under lifecycle and fault pressure;
- final checks for managed Worlds, native contexts, worker dispatch, async
  work, registered objects, event subscriptions, owned resources, semantic
  debug sessions, and retired collectible load contexts.

Intentional missing-type diagnostics are expected fault-injection evidence.
The Automation result must still be `Success` and every final ownership delta
must be zero.

## Runtime bounds

The native async service now enforces both per-World and process-wide limits:

| Resource | Per World | Process | Notes |
| --- | ---: | ---: | --- |
| pending + unpolled async completions | 4,096 | 65,536 | total remains constant when pending moves to completed |
| generation-owned async resources | 4,096 | 16,384 | excess loads complete as failed |
| one async request payload | 1 MiB | 1 MiB | rejected before UTF-8 decoding/allocation |
| worker-to-GameThread bridge calls | 1,024 pending | 1,024 process-wide | excess dispatch is rejected before enqueue |
| semantic-debug pause notifications | 1 | 1 | the debug worker is suspended until the pause is consumed |
| detached runtime breadcrumbs | 64 entries | 64 process-wide | oldest-first eviction is counted; only the newest 8 enter CrashContext |

Capacity and payload rejection counters are process-wide telemetry and are
included in the soak report. Managed/native failures and World lifecycle phases
enter `unifiedscript.runtime-breadcrumbs/v0.1`: kind, operation, message,
World/instance/generation identity, sequence, and timestamp are detached from
UObjects and collectible contexts. Kind, operation, and message are capped at
64, 96, and 512 characters. The last eight records are copied into Unreal
CrashContext game data without source text, locals, managed references, or raw
pointers. Gameplay command, event, trace, overlap, generated event, and
shared-snapshot limits remain published by the generated ABI layout.

World shutdown is an asserted sequence: suspend worker dispatch, destroy the
managed World, detach the native context, stop data/async services, invalidate
handles, then complete. A managed bridge fault stops and releases only its
component; Automation ticks a healthy sibling afterward to prove containment.

## Running the gate

Run the normal quick regression:

```powershell
powershell -ExecutionPolicy Bypass `
  -File tools/run-unreal-lifecycle-stress.ps1 `
  -EngineRoot D:\UE_5.8
```

Run the two-lane high-pressure gate:

```powershell
powershell -ExecutionPolicy Bypass `
  -File tools/run-unreal-reliability-soak.ps1 `
  -EngineRoot D:\UE_5.8
```

The combined report uses
`unifiedscript.unreal-reliability-soak/v0.2`; each lane embeds a
`unifiedscript.unreal-lifecycle-stress/v0.4` result and its matrix, capacity,
memory, deterministic-shutdown, and zero-leak gates.

## Verified UE 5.8.1 result

The 2026-08-28 local run under
`artifacts/ue/reliability-soak/20260828-160743` passed both lanes. Combined it
completed 64 travel cycles, 64 PIE cycles, 128 successful reloads, 128 rejected
reload rollbacks, 8,192 UObject registrations, 34 UE garbage collections, and
131,072 commands. Exactly 65,536 completion events beyond the bounded buffers
were dropped and counted. All 152 World shutdowns followed the required phase
order. The 64-entry ring counted 1,368 oldest-entry evictions across the two
processes while remaining at capacity. Final physical-memory growth was
20,193,280 bytes in the editor lane and 31,555,584 bytes in server mode, both
below the 256 MiB budget; all ownership/leak deltas and retired load-context
counts were zero.
