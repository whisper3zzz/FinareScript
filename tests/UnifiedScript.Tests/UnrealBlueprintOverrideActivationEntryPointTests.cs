using System.Runtime.InteropServices;
using UnifiedScript.Unreal;

namespace UnifiedScript.Tests;

public sealed unsafe partial class InProcessUnrealNativeInvocationTransportTests
{
    [Fact]
    public void LegacyAndValidatedCreateExportsEnforceActivationContract()
    {
        string markerPath = Path.Combine(
            Path.GetTempPath(),
            "UnifiedScriptActivationEntryPoint",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.GetDirectoryName(markerPath)!);
        const string markerVariable = "UNIFIEDSCRIPT_ACTIVATION_ENTRYPOINT_MARKER";
        string? previousMarker = Environment.GetEnvironmentVariable(markerVariable);
        Environment.SetEnvironmentVariable(markerVariable, markerPath);
        const ulong worldId = 901001;
        bool worldCreated = false;
        string assemblyPath = typeof(ActivationEntryPointScript).Assembly.Location;
        string typeName = typeof(ActivationEntryPointScript).FullName!;
        string matchingContract = CreateEntryPointContract("in");
        delegate* unmanaged[Cdecl]<ulong, int> destroyWorld =
            &UnrealWorldManagedEntryPoint.DestroyWorld;
        try
        {
            delegate* unmanaged[Cdecl]<ulong, nint, nint, int> createWorld =
                &UnrealWorldManagedEntryPoint.CreateWorld;
            delegate* unmanaged[Cdecl]<ulong, ulong, ulong, ulong, nint, int, nint, int, int>
                createLegacy = &UnrealWorldManagedEntryPoint.CreateScript;
            delegate* unmanaged[Cdecl]<
                ulong, ulong, ulong, ulong, nint, int, nint, int, nint, int, int>
                createValidated = &UnrealWorldManagedEntryPoint.CreateScriptWithBlueprintContract;
            Assert.Equal(
                (int)UnrealWorldManagedEntryStatus.Success,
                createWorld(worldId, new nint(0x1234), _tablePointer));
            worldCreated = true;

            using Utf8Pointer assembly = new(assemblyPath);
            using Utf8Pointer type = new(typeName);
            int legacyResult = createLegacy(
                worldId,
                1,
                UnrealObjectHandle.Create(1, 901).Value,
                UnrealObjectHandle.Create(2, 901).Value,
                assembly.Pointer,
                assembly.Length,
                type.Pointer,
                type.Length);
            Assert.Equal(
                (int)UnrealWorldManagedEntryStatus.ScriptContractMismatch,
                legacyResult);
            Assert.False(File.Exists(markerPath));

            Assert.Equal(
                (int)UnrealWorldManagedEntryStatus.InvalidArgument,
                createValidated(
                    worldId,
                    2,
                    UnrealObjectHandle.Create(1, 901).Value,
                    UnrealObjectHandle.Create(2, 901).Value,
                    assembly.Pointer,
                    assembly.Length,
                    type.Pointer,
                    type.Length,
                    nint.Zero,
                    0));

            using (var invalidSchema = new Utf8Pointer(
                       matchingContract.Replace(
                           "v0.1",
                           "v9",
                           StringComparison.Ordinal)))
            {
                Assert.Equal(
                    (int)UnrealWorldManagedEntryStatus.ScriptContractMismatch,
                    createValidated(
                        worldId,
                        2,
                        UnrealObjectHandle.Create(1, 901).Value,
                        UnrealObjectHandle.Create(2, 901).Value,
                        assembly.Pointer,
                        assembly.Length,
                        type.Pointer,
                        type.Length,
                        invalidSchema.Pointer,
                        invalidSchema.Length));
            }
            Assert.False(File.Exists(markerPath));

            using (var contract = new Utf8Pointer(matchingContract))
            {
                Assert.Equal(
                    (int)UnrealWorldManagedEntryStatus.Success,
                    createValidated(
                        worldId,
                        2,
                        UnrealObjectHandle.Create(1, 901).Value,
                        UnrealObjectHandle.Create(2, 901).Value,
                        assembly.Pointer,
                        assembly.Length,
                        type.Pointer,
                        type.Length,
                        contract.Pointer,
                        contract.Length));
            }
            Assert.True(File.Exists(markerPath));
            Assert.Contains("ctor", File.ReadAllText(markerPath), StringComparison.Ordinal);
            Assert.Contains("start", File.ReadAllText(markerPath), StringComparison.Ordinal);
        }
        finally
        {
            if (worldCreated)
            {
                Assert.Equal(
                    (int)UnrealWorldManagedEntryStatus.Success,
                    destroyWorld(worldId));
            }
            Environment.SetEnvironmentVariable(markerVariable, previousMarker);
            try
            {
                File.Delete(markerPath);
                Directory.Delete(Path.GetDirectoryName(markerPath)!);
            }
            catch
            {
                // The collectible load context can finish unloading after the test.
            }
        }
    }

    private static string CreateEntryPointContract(string direction) =>
        """
        {"schema":"unifiedscript.blueprint-override-contract/v0.1","overrides":[
          {"managedName":"Evaluate","declaringClassPath":"/Script/Engine.Actor",
           "functionName":"ReceiveBeginPlay","superMode":"explicit",
           "returnType":{"kind":"value","nativeName":"int32","typePath":""},
           "parameters":[{"direction":"__DIRECTION__",
                          "type":{"kind":"value","nativeName":"int32","typePath":""}}]}]}
        """.Replace("__DIRECTION__", direction, StringComparison.Ordinal);

    private sealed class Utf8Pointer : IDisposable
    {
        public Utf8Pointer(string value)
        {
            Pointer = Marshal.StringToCoTaskMemUTF8(value);
            Length = System.Text.Encoding.UTF8.GetByteCount(value);
        }

        public nint Pointer { get; private set; }

        public int Length { get; }

        public void Dispose()
        {
            if (Pointer != nint.Zero)
            {
                Marshal.FreeCoTaskMem(Pointer);
                Pointer = nint.Zero;
            }
        }
    }
}

public sealed class ActivationEntryPointScript : IUnrealManagedScript
{
    [UnrealBlueprintOverride("/Script/Engine.Actor", "ReceiveBeginPlay")]
    public int Evaluate(int value) => value + 1;

    public void OnStart(UnrealScriptContext context)
    {
        AppendMarker("start");
    }

    public void OnTick(float deltaSeconds) { }

    public void OnStop() { }

    public ActivationEntryPointScript()
    {
        AppendMarker("ctor");
    }

    private static void AppendMarker(string value)
    {
        string? path = Environment.GetEnvironmentVariable(
            "UNIFIEDSCRIPT_ACTIVATION_ENTRYPOINT_MARKER");
        if (!string.IsNullOrEmpty(path))
        {
            File.AppendAllText(path, value + Environment.NewLine);
        }
    }
}
