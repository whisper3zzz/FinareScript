# Unreal Async Service ABI v5

ABI v5 is the World-owned asynchronous service boundary used by managed
gameplay scripts. It complements synchronous generated bindings and Gameplay
Data ABI v4: generated calls remain the default for ordinary engine APIs,
while operations that suspend across frames use this explicit request and
completion contract.

## Ownership model

Every request carries a non-zero World-local script instance ID and collectible
generation. This makes each request **generation-owned** rather than merely
associated with an object handle.

- `UUnifiedScriptWorldSubsystem` owns one native async service per `UWorld`.
- `UnrealObjectScript` owns one `UnrealAsyncScope` per managed generation.
- reload, EndPlay, failed start, and World teardown synchronously cancel the
  scope before its collectible AssemblyLoadContext is released.
- the native bridge detaches before the World registry and service are
  destroyed, so retained managed transports reject late submissions.

No raw `UObject*`, allocator, task object, delegate, or callback crosses the
boundary. Async asset results return the existing World-scoped,
generation-checked `UnrealObjectHandle`.

## Wire contract

`FUnifiedScriptAsyncTransportV5` is append-only and advertises fixed sizes for
its request, completion, and borrowed completion-view rows. The v2 bootstrap
table exposes it through its optional `AsyncTransport` suffix.

The service currently supports:

- game-thread Delay/next-Tick completion;
- asynchronous object and class loading through `FStreamableManager`;
- per-request cancellation;
- game-time timeout;
- generation-wide cancellation for reload and EndPlay;
- deterministic rejection after World teardown.

`SubmitRequest` copies the UTF-8 asset path before returning. `PollCompletions`
returns a borrowed view that managed code copies immediately. `PumpOwner`
advances only the requesting generation, which prevents multiple scripts in a
World from advancing each other's timers.

## Managed surface

`UnrealObjectScript` and `UnrealActorScript` expose protected helpers:

- `DelayAsync(seconds)` and the timeout/cancellation overload;
- `NextTickAsync()`;
- `LoadObjectAsync(path, timeout, cancellationToken)`;
- `LoadClassAsync(path, timeout, cancellationToken)`.

Continuation completion happens while the owning script is pumped on the game
thread. A timeout faults with `TimeoutException`; native load failures fault
with `UnrealAsyncServiceException`; cancellation, reload, EndPlay, and World
teardown cancel the task.

## M7.2 validation

The managed tests lock the 56-byte request, 40-byte completion, 40-byte view,
and 88-byte transport layouts, then cover delay, load, timeout, caller
cancellation, borrowed-row copying, and generation cleanup. UE Automation
`UnifiedScript.Unreal.AsyncService.DelayLoadCancelAndLifecycle` exercises the
real World service, class load, cancellation, rollover cleanup, retained native
context, and teardown rejection.
