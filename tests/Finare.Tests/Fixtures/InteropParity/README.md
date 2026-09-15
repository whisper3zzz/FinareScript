# Native views and C# shared memory

`ParityScript.cs` is an executable example using generated bindings. It covers
interface arrays/Map values, interface-bearing USTRUCTs, collections of those
structs, in-place UE container/field operations, ordinary C++ base methods,
retained std::function callbacks and shared native storage.

```csharp
// One native container operation, without replacing the whole property.
fixture.NumbersNative.Add(10);
fixture.GroupNative.Items.ElementFields(0).Field<int>("Value").Value = 7;

// The native allocation is owned by C++; C# borrows it synchronously.
fixture.NumbersNative.WithSpan(values =>
{
    for (int i = 0; i < values.Length; ++i) values[i] *= 2;
});

using var buffer = new ParityBuffer(context.Bridge);
buffer.Samples.WithSpan(values => values.Fill(42)); // std::vector<int32>
ParityBase parent = buffer;                       // same ownership lease
parent.Add(5);                                    // native pointer adjustment

using var callback = UnrealCallback<CallbackHandler>.Create(context.Bridge, value => value + 9);
buffer.Save(callback);                            // C++ may retain std::function
int result = buffer.Call(5);                      // 14
```

The declaration uses `baseTypePath` for the ordinary C++ base, `Array` with exact
`std::vector<int32>`/element metadata for memory, and a `Callback` with the exact
`std::function<int32(int32)>` signature. No per-call binding record is needed.
`tools/prepare-interop-parity.ps1` shows these declarations and generates both
languages' bindings. In a game project put such declarations in the ordinary
`bindings.nativeExtensions` input; reflected UE types are discovered normally.

Spans support exact byte/int/uint/long/float/double layouts, up to 64 MiB, on the
game thread. No reference-bearing USTRUCT is reinterpreted. Do not await, retain
an unsafe pointer, reenter UE/native methods, or mutate/resize the same storage
from another native thread while borrowed. Calls that could invalidate storage
through this bridge are rejected during the callback. Subsequent calls resolve
the current allocation after resizing. Exceptions release the borrow; edits
already made remain in native memory.

Normal property getters still return values. Array element views identify the
current index; insertions/removals can change which element occupies it. Set
elements and Map keys are values to keep hashes valid. Native view access checks
read-only/replication authority, owner liveness and generation. C++ callback
leases expire on Dispose, generation retirement or World teardown; an expired
retained function returns the default result. Worker calls to these ordinary
C++ callbacks do not invoke C#.

Run from the repository root with the local UE installation:

```powershell
& D:/UE_5.8/Engine/Build/BatchFiles/Build.bat FinareHostEditor Win64 Development -Project="$PWD/HostProject/FinareHost.uproject" -WaitMutex -NoHotReloadFromIDE
./tools/prepare-interop-parity.ps1 -EngineRoot D:/UE_5.8
& D:/UE_5.8/Engine/Build/BatchFiles/Build.bat FinareHostEditor Win64 Development -Project="$PWD/HostProject/FinareHost.uproject" -WaitMutex -NoHotReloadFromIDE
& D:/UE_5.8/Engine/Binaries/Win64/UnrealEditor-Cmd.exe "$PWD/HostProject/FinareHost.uproject" '-ExecCmds=Automation RunTests Finare.Unreal.InteropParity' '-TestExit=Automation Test Queue Empty' -unattended -NullRHI -NoConfigChanges
```

The first build makes UHT metadata available; preparation exports reflection,
generates bindings/native code and builds the C# fixture. The second build
compiles that generated native code. `Managed` tests actual interop, exceptions,
resize, read-only denial, pointer adjustment, reload and callback retirement.
`MemoryIdentity` compares the callback pointer to the native `TArray::GetData()`.
The API-library CI lane includes both. Acceptance is currently Win64 UE 5.8.2
Editor/CMD, without a new Cook or comparative performance claim.
