# 2026-09-04 — Common values across path and generated calls

Status: complete (bounded transport and connected-example scope)

## Outcome and scope

Common scalar, enum, Vector/Rotator/Transform values must work through ordinary
property access and RPC as well as generated member-ID calls. Preserve the frozen
native table/value layouts, synchronous ownership and existing kind encodings.
Protected files stay untouched; no external project or platform expansion.

## Decisions and remaining work

- Small integers use the existing Int32 cell with signed/range validation;
  UInt32 uses its bits. Int64/UInt64 use eight little-endian bytes.
- Rotator uses three binary64 cells; Transform uses quaternion XYZW, translation
  XYZ and scale XYZ (ten binary64 cells, matching ABI v3 component order).
- Enum payloads carry signed value, stable type ID and an optional UTF-8 type
  path. This preserves identity for generated results that only have a type ID,
  without guessing a path. Typed managed consumers must verify that identity.
- Native and managed codecs, boxed typed-enum arguments and fallback coverage
  tests are implemented. The public component example adds long state and a
  long/enum/Rotator/Transform Server/Client RPC exchange with reload.
- FRotator's native network serializer compresses axes to 16-bit values and
  wraps angles. The example validates that exact UE wire result. Local legacy
  codec tests still require original binary64 cells; no quantization is added
  by the bridge.
- ID-only enums can be read through the matching typed wrapper. Untyped reads
  that require an unavailable path fail explicitly instead of guessing identity.
- This does not complete every Script Class state/default declaration shape,
  network condition combination, cooked path or platform. Those remain on the
  roadmap, including state/default gaps needed by composed gameplay examples.

## Relevant verification

UE 5.8.1 Editor build and Demo chunk emission pass. The 85 affected managed cases
pass in `artifacts/m13-5-values/managed/common-values-contracts-final.trx`; native
ABI and both connected scenarios pass in `artifacts/m13-5-values/common-values-verified.log`.
The connected long value is 9007199254740993 and survives client reload exactly.
Earlier full-suite results are not results for these later edits.
