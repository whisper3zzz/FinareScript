# Unreal API Contract

The project API contract is the product boundary between broad Unreal
reflection, explicit native extensions, and the API actually shipped to a UnifiedScript game module. One
strict allowlist drives all generated consumers:

```text
selected UClass reflection
        + optional native-extension declarations
        + project API contract
        -> typed C# wrappers
        -> direct native C++ thunks
        -> binding coverage report
        -> visual API node catalog
        -> searchable API/diagnostic browser
```

This prevents managed code, the native dispatcher, and the visual palette from
silently exposing different Unreal members.

## Native extension declarations

An optional `nativeExtensions` object in the API contract admits public C++
members that are not marked `UFUNCTION` or `UPROPERTY`. It uses its own schema,
`unifiedscript.unreal-native-extensions/v0.2`. Legacy v0.1 UObject extensions
remain readable and retain their canonical representation; ordinary C++ types
require v0.2. Contracts without this object remain unchanged. The extension is applied
before member selection, dependency closure, source-driven expansion and all
generators. The normal `ue-bindings --contract` and project setup commands need
no separate native-extension step.

The checked-in
[Demo contract](../samples/Unreal/UEUnifiedScriptRuntime.api-contract.json)
contains the complete example. Each extension has a `name` and `types`. A type
declares its `path`, `nativeName`, `headerPath`,
`properties`, and `functions`. Properties reuse the typed property declaration;
each function contains a typed `declaration`, a `const` method flag, and an
ordered `parameters` list of C++ passing modes:

| Passing mode | C++ signature | Managed direction |
| --- | --- | --- |
| `value` | `T` | input |
| `constReference` | `T const&` | input |
| `reference` | `T&` | `ref` or `out`, as declared |

Every native function path includes its signature, including `()` for zero
parameters. For example, `Scale(int32)` and `Scale(double)` are distinct members;
the sample's combined container/ref/out path ends in
`Accumulate(TArray<int32>const&,int32&,FString&)`. Spaces are omitted in the path,
not in generated C++. A single function can be selected by name; overloaded
functions require the exact signature selector.

Generated C++ methods use typed member/function pointers to select the exact overload,
including const qualification, and compile-time assertions for field types.
Declarations that disagree with the real header cannot silently convert to a
different member signature. Value and const-reference overloads with the same
managed input types are rejected because C# cannot distinguish those overloads.
Extensions cannot replace existing reflected members or claim that an ordinary
field is Editor-visible. Private/protected members, invalid directions, unknown
owners/headers and unsupported native codecs fail before artifact publication.

Generated wrappers register native-only paths with the World bridge through its
generation-scoped view. Missing native tables or unavailable codecs fail with an
actionable error; they cannot fall back to reflection. Typed fixed calls and
the existing ABI v3 call-frame/ref/out routes are reused, with no JSON on the
invocation path. Node and coverage descriptors retain `nativeExtension` origin.

The
[managed example](../samples/Unreal/UnifiedScript.Unreal.Demo/NativeExtensionDemoScript.cs)
calls unreflected fields, const integer/double overloads, a static method, a
zero-argument void method, and a container/ref/out method. CMD tests
`UnifiedScript.Unreal.NativeExtensions.GeneratedDispatch` and
`UnifiedScript.Unreal.NativeExtensions.ManagedRoundTrip` exercise the real
generated native table, CoreCLR calls, generation replacement and World teardown.

With `kind` omitted (or `class`), owners must match existing exported native
UObject classes. Ordinary C++ declarations use `kind: "nativeObject"` and the
separate ownership rules below; they do not extend UObject reflection.
The normal value codecs and their bounds still apply; this is not arbitrary
pointer/reference interop. A non-UPROPERTY field does not acquire GC rooting,
serialization, replication or a reflected Blueprint property merely because it
has a generated accessor. Native code remains responsible for its storage and
referenced object ownership. Packaged execution of this new sample is not yet
acceptance evidence.

## Binary payload interop

`UnrealByteBuffer` and `FUnifiedScriptByteBuffer` provide a contiguous binary
value for generated, non-reflected UObject members. This addresses the two-way
payload use case in Puerts' [ArrayBuffer examples](https://puerts.github.io/docs/puerts/unreal/script_call_uclass/#arraybuffer),
not JavaScript object compatibility or identical memory ownership. The same
contract generates the native calls, C# signatures and visual node descriptions.
The type reference is:

```json
{ "kind": "byteBuffer", "nativeName": "FUnifiedScriptByteBuffer", "headerPath": "UnifiedScriptByteBuffer.h" }
```

See the complete contract and
[binary example](../samples/Unreal/UnifiedScript.Unreal.Demo/BinaryPayloadDemoScript.cs):

```csharp
byte[] packet = [21, 0, 31];
target.StoreBytes(UnrealByteBuffer.Borrow(packet));
UnrealByteBuffer received = target.ReadBytes();
byte[] ownedBytes = received.ToArray();
var response = target.TransformBytes(received, ref received, out int calls);
```

Direct properties, input/value/const-reference parameters, returns, `ref` and
`out` use the existing generated call-frame route. Typed bridge overloads with
zero through four arguments use the same binary route; larger generated
signatures already use `InvokeWithOutputs`. Binary calls do not fall back to
JSON or reflection when the native extension is missing.

- `CopyFrom(ReadOnlySpan<byte>)` creates an owned managed snapshot.
  `Borrow(byte[])` retains the managed array without a local copy; later array
  mutations are visible. Do not mutate that array concurrently with a call.
  `Slice(start, length)` retains the same storage and ownership mode. `Span`
  is read-only; `ToArray()` creates another independent copy. `default` is empty.
- **Crossing the native boundary always copies.** Native input decode retains
  no pointer into the managed call arena. Managed result decode creates an owned
  snapshot independent of arena reuse, native storage replacement and context
  lifetime. These plain byte values contain no World or generation handle.
- Native `CopyFrom(TConstArrayView<uint8>)` owns a copy; `TakeOwnership(TArray&&)`
  transfers storage whose existing aliases must no longer mutate it. Copies of
  `FUnifiedScriptByteBuffer` share immutable storage. `GetView()` borrows it only
  while at least one buffer retains that storage.
- A wire buffer is at most **16 MiB**. A binary invocation's input/output arena
  is bounded by **64 MiB**, including input bytes and result copies; the managed
  input allocation reserves 4 KiB for other values. Native constructors may
  hold larger local data, but such values cannot cross the bridge. Large inputs
  allocate beyond the usual 4 KiB stack arena; oversized retry requests fail
  before allocating their requested capacity.

ABI v3 appends value kind **25** and capability **`ByteBuffers = 1 << 11`**.
The value cell stays 48 bytes and the call frame stays 72 bytes. One ArenaBacked
cell records byte offset in `Payload0` and byte length in `Payload1`; all other
metadata is zero. Empty buffers use length zero, not the null flag. Payload
storage is exactly the contiguous bytes, not one 48-byte cell per byte. Existing
schema channels and the shared-data protocol are unchanged. Older transports
reject this capability before native invocation.

Variable-output retries reuse the native pending result, so an invoked method
is not executed twice merely to enlarge the return arena. Managed `ref/out`
variables are assigned only after every result decodes successfully. This does
not roll back arbitrary C++ side effects if the function returns invalid data.

The current boundary excludes buffer container elements/keys/values, reflected
Blueprint properties/functions, serialized Script Class state, raw-pointer
views, native-object fixed-lane buffer arguments and native-to-managed callbacks.
Unsupported declarations are rejected rather than emitted as usable APIs.
`[Visual]` call boundaries and generated descriptors are supported; unrestricted
interactive palette insertion and cooked execution of this sample are not yet
established. This is **not cross-language shared memory or a zero-copy API**.

`UnifiedScript.Unreal.BinaryBuffers.WireValidation` and `.ManagedRoundTrip`
exercise real native/CoreCLR calls, 64 KiB input, 72 KiB return plus `ref` output,
embedded zeros, sliced/empty buffers, malformed metadata, output retry, reload
and teardown. Managed tests additionally cover the 16 MiB limit, bounded arena
requests, old transports, direct overloads and transactional output publication.

## Ordinary C++ object runtime

The v0.2 declarations generate ordinary C++ constructors, fields, instance/static
methods, overloads and ownership-qualified object returns. The checked-in
[native header](../HostProject/Plugins/UEUnifiedScript/Source/UEUnifiedScriptRuntime/Public/UnifiedScriptNativeCounter.h)
has no UObject or reflection macros. The same Demo contract generates its native
dispatch/registration, typed C# classes, coverage and visual node descriptions.
The real CoreCLR
[example](../samples/Unreal/UnifiedScript.Unreal.Demo/NativeObjectLifetimeDemoScript.cs)
uses those generated APIs, with no per-World test-member registration:

```csharp
using var counter = new NativeCounter(context.Bridge, 10);
using var child = counter.GetChild();   // Borrowed from counter; never deletes C++ storage.
child.Add(5);
int scaled = counter.Scale(2);          // 30; exact int32 overload.
double fractional = counter.Scale(0.5); // 7.5; exact double overload.
using var clone = counter.Clone();     // Independent owned result.
counter.Bias = 3;
```

Declare an ordinary type with `kind: "nativeObject"`, a stable
`/Native/Module.Type` path, its exact namespace-qualified C++ `nativeName` and
header. Object references use the same path/header and `nativeName` ending in
`*`. Native class declarations are not UHT exports and cannot claim UObject
traits, Blueprint inheritance or reflected state.

- A constructor declaration is static, has `operation: "constructor"` and
  `returnOwnership: "owned"`, and returns its own native type. It emits both
  a C# constructor and a named static factory for visual calls. Native
  constructors use normal C++ overload resolution; exact member-pointer
  validation applies to methods, not constructors.
- An object-returning method explicitly selects `owned` or `borrowed`.
  Owned results must be newly transferred deleting ownership; a duplicate
  registered address is rejected without deleting its existing owner. Borrowed
  results require a non-static receiver whose lifetime owns the storage.
  Such calls are effectful even when the C++ method is const: they publish leases.
- Scalar/enum/vector/rotator fields and methods use fixed values. A dependency
  on a native return type emits its wrapper shell, not unselected members.
  Static calls require the generation's `IUnrealInvocationBridge`; no fabricated
  UObject receiver or process-global managed scope is used.
- Generated wrappers and that context can cross `[Visual]` call boundaries.
  Semantic/Graph projection and chunk emission are tested. This does not make
  native handles serializable Script Class state or reflected Blueprint pins.
  Full interactive palette insertion is not established: the current palette
  still has receiver/type/return-shape restrictions beyond the generated catalog.

`FUnifiedScriptNativeObjectRegistry` belongs to one native World context. Its
opaque object and scope tokens are process-unique and non-recycled. An owned
registration transfers a pointer and native destructor only on success. An
address cannot acquire two deleting owners. A borrowed registration requires a
live same-scope owner and never deletes the borrowed pointer. Releasing an owner
invalidates all descendant aliases first. Before native code moves/replaces child
storage, it must call `InvalidateBorrowed`; merely retaining an unrelated object
does not make an arbitrary pointer safe.

Generated `FUnifiedScriptNativeObjectLibrary` registration serves existing and
future Worlds. Conflicting member IDs fail closed, including conflicts with
explicit registrations. Library retirement removes its dispatch entries and
retires its allocations and aliases while destructor code is still loaded.
Registration/retirement with live Worlds is game-thread-only. This does not
promise arbitrary hot unloading during an active native invocation, cross-module
inheritance lifetime management or automatic cleanup of manually registered
objects outside that library. Their native code must remain loaded until they
are released or their context detaches.

Managed code obtains a generation scope through
`UnrealNativeObjectScope.FromBridge(context.Bridge)`. Construction returns an
`UnrealNativeObjectLease`; `Acquire` wraps an object return, `Invoke` uses a
stable member ID, and `Dispose` releases the lease. Generated wrappers hide
these IDs and low-level calls. The registry owns the actual
ownership policy, not a caller-provided managed Boolean. Forgotten leases are
released when their generation/transport is disposed or their World detaches.
Explicit calls and disposal are game-thread-only. Worker disposal fails before
changing ownership and can be retried on the game thread; there is no native
destruction from a managed finalizer.

During reload, the stopped previous generation keeps its native objects until
commit retires that generation. A failed candidate's objects are destroyed;
the previous generation can restart using its retained handles. `OnStop` does
not itself clear that retained scope. If `OnStart` allocates replacement resources
when restarting, script code must dispose or reuse the previous leases. Native
handles are not transferable between generation scopes or Worlds.

Native Object ABI v1 is an optional 64-byte table appended to the v2 transport.
It shares context retain/release functions and the existing 48-byte value cell,
without changing the existing UObject transport prefixes or call-frame layout.
Its initial synchronous lane accepts up to 16 fixed scalar/enum/vector/rotator
inputs and returns one fixed value plus an optional native-object handle. It
rejects strings, containers, other arena values and UObject cells; it does not
fall back to reflection. Failed factories discard unpublished allocations and
outputs, not arbitrary native side effects. Native-object arguments, strings,
containers, richer structs, ref/out, default arguments, inheritance, converters
and native callbacks remain unsupported on this ordinary-object lane. They
must not silently use UObject codecs or reflection fallback. The broader
UObject-member call-frame support above is a different route.

`UnifiedScript.Unreal.NativeObjects.Ownership`, `.TransportValidation`,
`.GeneratedLibrary` and `.ManagedLifetime` verify the native registry, ABI
rejection, automatic dispatch, library retirement and actual generated CoreCLR
startup/rollback/reload/teardown. The sample also injects a duplicate owned return
to prove that rejection preserves the existing object. The 10,000-level borrow-chain test checks
iterative invalidation, not Puerts performance. No cooked result is claimed yet.

## Contract schema

`unifiedscript.unreal-api-contract/v0.2` requires a contract name, managed
namespace, generated-thunk header and C++ namespace, plus one or more exact
type/member selectors. Type paths and member names must exist in the supplied
`unifiedscript.unreal-reflection/v0.5` document or explicit native extensions.
Unknown fields, duplicate
selectors, stale types, and stale members fail generation.

```json
{
  "schemaVersion": "unifiedscript.unreal-api-contract/v0.2",
  "contractName": "Game.Gameplay",
  "managedNamespace": "Game.UnifiedScript.Generated",
  "nativeHeader": "GameGeneratedThunks.h",
  "nativeNamespace": "UE::UnifiedScript::Generated::Game",
  "types": [
    {
      "path": "/Script/Game.Player",
      "members": ["BatchMove", "UnsafeWeakValue"]
    }
  ],
  "policy": {
    "defaultAction": "allow",
    "rules": [
      {
        "memberPath": "/Script/Game.Player:BatchMove",
        "action": "allow",
        "route": "dataPlaneOperation"
      },
      {
        "memberPath": "/Script/Game.Player:UnsafeWeakValue",
        "action": "deny",
        "route": "auto",
        "diagnosticCode": "USUE5201",
        "reason": "Weak references require a generation-safe handle contract."
      }
    ]
  },
  "coverageBaseline": {
    "minimumMemberCount": 2,
    "minimumDirectThunkCount": 0,
    "minimumCallFrameCount": 0,
    "minimumDataPlaneOperationCount": 1,
    "minimumReflectionFallbackCount": 0,
    "maximumRejectedCount": 1
  }
}
```

Member lists are explicit rather than wildcard-based. Adding a Blueprint API
to an engine class therefore cannot accidentally expand the shipped scripting
surface after an engine upgrade.

Overload selectors use `Name(type,...)`, for example `Scale(int32)` and
`Scale(float)`. The generator also closes required base classes, interfaces,
nested structs, and enums so every emitted managed type is self-contained.

## Policy and coverage gate

Policy rules are exact member-path decisions. `allow` can retain automatic
routing or require `directThunk`, `callFrame`, `dataPlaneOperation`, or
`reflectionFallback`; `deny` requires a stable diagnostic code and reason.
Unmatched members follow `defaultAction`.

The `unifiedscript.unreal-binding-coverage/v0.2` report records one of five
dispositions for every selected member:

- `directThunk`: fixed ABI call with at most four supported input arguments;
- `callFrame`: generated ABI v3 descriptor/dynamic/container/ref/out path;
- `dataPlaneOperation`: generated shared gameplay batch/command boundary;
- `reflectionFallback`: explicitly permitted safe cold-path reflection;
- `rejected`: denied or unsupported, always with a stable reason.

The checked-in baseline is monotonic: CI fails if member or route counts fall
below their minimums, rejected members exceed the maximum, or handwritten
member-ID dispatch appears outside generated/test sources.

## Reflection export

The editor exporter supports an exact class allowlist so projects can reflect
selected Engine classes without exporting every Engine UObject:

```powershell
UnrealEditor-Cmd.exe Game.uproject -run=UnifiedScriptExport `
  -Output=D:\Game\Generated\Game.reflection.json `
  '-Modules="Game,Engine"' `
  '-Classes="/Script/Game.Player,/Script/Engine.Actor,/Script/Engine.Pawn"' `
  -unattended -nop4 -nosplash
```

## Source-driven development surface

An adopted project's normal Setup action can expand a safe local development
surface without asking the gameplay author to edit the reviewed Contract. Source
can use the generated namespace like an ordinary typed C# API:

```csharp
using Game.UnifiedScript.Generated;

_ = new Actor(context.Target, context.Bridge).GetActorLocation();
_ = new Pawn(context.Target, context.Bridge).GetController();
```

Aliases and fully qualified names remain valid. The bootstrap analyzer combines
the normal import scope with Roslyn symbols: a source-defined or referenced type
with the same short name wins and is never treated as an Unreal wrapper. Bare
types that do belong to the generated namespace work in parameters, fields,
properties, construction, casts, and target-typed `var` initialization even when
the referenced wrapper has not been emitted yet. Generated files beginning with
`// <auto-generated` are excluded. Each recognized member use produces a stable
`USAPI-*` identity and exact source spans in
`unifiedscript.unreal-api-dependency-requests/v0.1`.

Before the main reflection export, Setup runs `ue-reflection-hints` over the same
project compilation. It writes
`unifiedscript.unreal-api-source-type-hints/v0.1`, containing only generated-
namespace type names that do not bind to a user or referenced C# symbol, plus
exact source locations and source/Contract fingerprints. The UE exporter resolves
each hint to exactly one native `/Script` class or Blueprint generated class. It
uses the Asset Registry only to find matching Blueprint asset names, loads only
those bounded candidates, and records the exact generated-class object path such
as `/Game/Gameplay/BP_Door.BP_Door_C` in reflection, the Contract, member IDs,
and generated consumers. Missing and same-name candidates fail explicitly.
`reflection.classes` can select an exact native class;
`reflection.blueprintAssets` can select an exact Blueprint asset object path to
disambiguate or guarantee loading. Asset-generated members have no native C++
header, so they are never given a fake DirectThunk: supported cold-path shapes
use the safe reflected route and unsupported ownership/signature shapes remain
source-linked diagnostics.

```powershell
unifiedscript ue-reflection-hints `
  --project Managed\Game.UnifiedScript\Game.UnifiedScript.csproj `
  --base Contracts\Game.unreal-api-contract.json `
  --out Generated\Game.api-type-hints.json
```

For reflected overload groups, invocation argument types and `ref`/`out`
directions select the exact Contract selector when there is one deterministic
match. For example, `Scale(1)` requests `Scale(int32)` while `Scale(1.0f)`
requests `Scale(float)`. Unknown expressions such as `Scale(default)` remain an
`USUE3104` ambiguity instead of being guessed.

Generated event helpers retain the reflected delegate identity as well:
`target.SubscribeOnChanged(handler)` requests the `OnChanged` Contract member,
then regenerates the handler type, event descriptor, subscription method, visual
event node, native proxy, and coverage entry from that one selector.

`ue-surface-expand` resolves those requests against the current reflection and
the reviewed base Contract:

```powershell
unifiedscript ue-surface-expand Generated\Game.reflection.json `
  --project Managed\Game.UnifiedScript\Game.UnifiedScript.csproj `
  --base Contracts\Game.unreal-api-contract.json `
  --requests-out Generated\Game.api-requests.json `
  --overlay-out Generated\Game.development-api-contract.json `
  --report Generated\Game.development-surface.json `
  --lock-out Generated\Game.generated-surface.lock.json `
  --fail-on-open-decisions
```

The report uses
`unifiedscript.unreal-api-development-surface/v0.1` and fingerprints source,
reflection, and the base Contract. A request is applied only when reflection
resolves one exact identity and the real managed/native generators plus coverage
policy accept its route. `selected` and `already-selected` decisions are safe;
missing types/members, overload ambiguity, manual construction/data-plane work,
or policy/signature rejection are open decisions.

Application is all-or-nothing. One open decision keeps the effective Contract
identical to the reviewed base and emits a source-linked `USUE31xx` diagnostic;
it never produces a partial native/C#/visual surface. In particular, `USUE3105`
means reflection found the member but the configured generation policy cannot
ship a supported route. The proposed Contract remains diagnostic evidence even
when `--fail-on-open-decisions` returns exit code 65.

When expansion is applied, `--lock-out` writes the strict
`unifiedscript.unreal-api-generated-surface-lock/v0.1` artifact. It fingerprints
the project, managed namespace, engine identity, base/effective Contracts, and
each selected reflected type independently. Member edits therefore report a
localized `changed:<type-path>` entry while unrelated type fingerprints remain
stable. Passing the accepted file through `--verify-lock` makes drift a
pre-write failure: `USUE3201` lists stable project/namespace/engine/Contract and
`added`, `removed`, or `changed` type differences and exits 65 before replacing
the request, overlay, report, lock, or downstream generated surface. An open
decision cannot be frozen and fails as `USUE3202`.

Author-facing C# and visual names may intentionally differ from the stored
reflection selector. For example, UE exposes the Blueprint-callable function as
`K2_GetActorLocation`; UnifiedScript generates `GetActorLocation` while the
Contract, coverage, member ID, and native thunk retain
`/Script/Engine.Actor:K2_GetActorLocation`. The deterministic `K2_` mapping is
part of generated naming, not a fuzzy lookup, and collisions remain explicit
overloads or diagnostics.

Project-local Setup consumes the development Contract only for the current
generated development surface. Its default `update` lock mode follows safe local
source expansion and refreshes the generated lock; `frozen` verifies the current
candidate against the accepted lock before writing output. ValidateOnly always
regenerates a temporary candidate, verifies the accepted lock, and compares
exact bytes. The reviewed base Contract remains the policy boundary and the
generated lock becomes the exact team/release/CI surface boundary: promoting,
shrinking, or policy-changing either remains an explicit source-review
operation.

## Review-only Contract proposals

Migration discovery does not write the shipped Contract. The migration planner's
`contract-requests.json`, a current reflection export, and the current reviewed
Contract instead produce a separate
`unifiedscript.unreal-api-contract-proposal/v0.1` artifact:

```powershell
unifiedscript ue-contract-propose Generated\Game.reflection.json `
  --requests Migration\contract-requests.json `
  --base Contracts\Game.unreal-api-contract.json `
  --out Review\Game.unreal-api-contract-proposal.json `
  --fail-on-open-decisions
```

The proposal fingerprints all three inputs, preserves the base Contract's
policy and coverage baseline, and records exactly one decision for every
dependency request. A unique reflected member is `selected`; an exact member
already in the base is `already-selected`. Missing types/members remain
`unresolved-*`, overloads and duplicate type names remain `ambiguous-*`, and
type construction or data-plane schema work remains `manual-review`. The
generator never chooses an overload by name, exposes a type without an explicit
member, or converts a data-plane request into a reflected call.

`--fail-on-open-decisions` writes the review artifact first and then exits 65 if
any manual, unresolved, or ambiguous decision remains. A request set carrying a
Contract fingerprint is rejected when it no longer matches the base, so an old
scan cannot expand a newer surface. Reviewers copy the accepted selectors into
the project-owned Contract; only the ordinary `ue-bindings` transaction changes
the native, managed, visual, browser, and coverage outputs.

## Atomic generation

`ue-bindings` accepts the contract and writes every requested output as one
transaction. Contract metadata owns both generated namespaces.

```powershell
unifiedscript ue-bindings Generated\Game.reflection.json `
  --contract Contracts\Game.unreal-api-contract.json `
  --out Managed\Generated\Game.bindings.generated.cs `
  --native-out Source\Game\Generated\Game.native-thunks.generated.cpp `
  --nodes-out Generated\Game.api-nodes.json `
  --browser-out Generated\Game.api-browser.json `
  --report Generated\Game.binding-coverage.json `
  --fail-on-rejected
```

The `unifiedscript.unreal-api-node-catalog/v0.2` output is a
reflection-compatible visual catalog with stable member IDs, generated managed
type names, categories, tooltips, purity, typed control/data pins and pin
defaults. Each node also carries latent/latent-info, game-thread/any-thread,
required/optional World-context, and deprecation policy derived from the same
native reflection metadata. Script Assets can set `ApiNodeCatalogPath`; the
Slate authoring surface builds its Unreal API palette from that
product-specific catalog instead of reconstructing presentation policy in the
editor.

The same transaction emits `unifiedscript.unreal-api-browser/v0.1`. It joins
node presentation with the exact coverage disposition and diagnostic codes for
each Contract path, then fingerprints the canonical Contract, node catalog, and
coverage inputs. The local Workbench serves this validated document read-only at
`GET /api/unreal/browser`; see
[Project templates and generated API browser](PROJECT_TEMPLATES_AND_API_BROWSER.md).

Functions with more than four parameters use the generic ABI v3 CallFrame
path, including names and enums. Fixed signatures with zero to four parameters
retain the specialized fast path.

The current runtime product contract selects 103 members across seven generated
types. It includes the World static-service surface and a supported 17-field
`FHitResult` projection, producing 25 direct thunks, 76 CallFrame members, two
safe delegate fallbacks, 101 native paths, and zero rejected members. Operations
that suspend across frames use Async Service ABI v5 instead of pretending to be
synchronous reflected calls.

## BlockBreaker slice

BlockBreaker selects nine native types and 40 members. Its bootstrap path uses
generated `Actor`, `Pawn`, `SceneComponent`, `CameraComponent`,
`StaticMeshComponent`, and `UnifiedScriptWorldLibrary` wrappers. The World
service loads assets/classes, retains loaded resources for the World-host
lifetime, and spawns Actors. The selected `FHitResult` shape remains because the
generated SceneComponent transform call carries its native sweep result, while
the current player shot routes trace, effect, and damage through the data plane.

The migrated rifle performs its visibility query through one Gameplay Data
Batch Trace and writes emitter/damage work into one command reservation. It
observes the two completion events on the following managed frame. Seven Gun
facade functions and their transient cached hit/effect state remain deleted.
The current standalone 10,000-frame data-plane run executes one complete
midpoint shot/hit and reports 120,031 generated ABI v3 calls with zero legacy
calls. The current matched three-trial gameplay gate prepares both runtimes
before measurement, alternates execution order, and reports a median of
4,927.191 ms for UnifiedScript versus 4,759.930 ms for Puerts on the local UE
5.8.1 machine (`UnifiedScript/Puerts = 1.035139`). It is functional evidence,
not a performance claim; M10 owns the release performance threshold.
