# Remote debug framing

Status: M9.3, `unifiedscript.debug-frame/v0.1` over loopback TCP.

## Purpose

The remote transport carries the complete `ICoreClrDebugTransport` contract
between processes without changing semantic breakpoint, generation, execution,
pause, frame, or node identities. It is intended for an editor client and a
local managed debug host. It does not introduce a second debugger model or a
second script VM.

Start a host with:

```powershell
dotnet run --project src/Finare.Cli -- `
  debug-frame-serve samples/Execution/RuntimeRules.cs `
  --port 4174 `
  --max-connections 32
```

`--source`, `--identity`, `--effects`, `--assembly`, and repeatable
`--reference` use the same server-side compilation inputs as `debug-serve`.
Port `0` requests an ephemeral port. The first stdout line is a machine-readable
`unifiedscript.debug-frame-server/v0.1` readiness object containing the actual
endpoint, loaded program generation, frame version, header/payload limits, and
connection cap.

## Wire contract

Every request uses one TCP connection and receives one response. The fixed
24-byte little-endian header is:

| Offset | Size | Field |
| --- | ---: | --- |
| 0 | 4 | ASCII `USDB` magic |
| 4 | 2 | protocol version, currently `1` |
| 6 | 2 | kind: request `1`, response `2` |
| 8 | 4 | UTF-8 JSON payload byte count |
| 12 | 4 | reserved, must be zero |
| 16 | 8 | non-zero request/correlation ID |

The JSON envelope uses `unifiedscript.debug-frame/v0.1` and repeats the request
ID. Header/envelope mismatch is rejected. Operations are bounded lowercase
identifiers and cover reload, breakpoint set/remove, method resolution,
start/wait, stack, locals, evaluation, pause, continue, all three step modes,
and completion wait.

Payloads are capped at 1 MiB before allocation. Reload accepts at most 256
sources, 1,024 compile references, 1,024 runtime references, 4,096 effect
rules, and 256 preprocessor symbols. Execution accepts at most 256 arguments
and 4,096 receiver state overrides. The TCP host caps active connections at
1-256 (32 by default).

Protocol failures use `UDBF1001`-`UDBF1006`; remote adapter errors retain their
existing `UDB...` code and are reconstructed as `BreakpointProtocolException`
when applicable. Unknown server failures become `UDBF2000`, and compilation
failures become `UDBF2001`.

## Concurrency and cancellation

A wait-for-pause or wait-for-completion call may hold one connection while a
separate connection sends Pause, Continue, Step, stack, locals, or evaluation.
The server dispatches connections concurrently under its connection cap, so a
long poll cannot serialize the control plane. Disconnecting a waiting client
cancels its server-side wait; shutdown cancels and drains active connections.

## Security boundary

The built-in TCP listener binds only `127.0.0.1`. It is development plumbing,
not an authenticated internet service. Compilation paths and references are
resolved by the server process, so clients must be trusted local tools. Remote
machine access, authentication, encryption, DAP adaptation, and async
state-machine stepping remain separate product work.

The managed tests exercise codec round trips, version/size/correlation
rejection, the complete reload-break-inspect-continue path, stale-generation
error mapping, and a real concurrent TCP wait/Pause/Continue sequence.
