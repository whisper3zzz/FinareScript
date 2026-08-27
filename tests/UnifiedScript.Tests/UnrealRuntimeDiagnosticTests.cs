using System.Text.Json;
using UnifiedScript.Unreal;

namespace UnifiedScript.Tests;

public sealed class UnrealRuntimeDiagnosticTests
{
    [Fact]
    public void ManagedFailureCarriesWorldGenerationHandlesAndSource()
    {
        Exception failure = Assert.Throws<InvalidOperationException>(ThrowManagedFailure);
        var context = new UnrealRuntimeDiagnosticContext(
            7,
            "D:/Project/Demo.dll",
            "Demo.PlayerScript",
            UnrealObjectHandle.Create(2, 3),
            UnrealObjectHandle.Create(4, 5));

        UnrealRuntimeDiagnostic diagnostic = UnrealRuntimeDiagnosticBuilder.Create(
            41,
            73,
            "TickScript",
            failure,
            context);

        Assert.Equal(UnrealRuntimeDiagnostic.CurrentSchemaVersion, diagnostic.SchemaVersion);
        Assert.Equal("managedException", diagnostic.Kind);
        Assert.Equal("41", diagnostic.WorldHostId);
        Assert.Equal("73", diagnostic.InstanceId);
        Assert.Equal(7, diagnostic.Generation);
        Assert.Equal("0x0000000300000003", diagnostic.HostHandle);
        Assert.Equal("0x0000000500000005", diagnostic.TargetHandle);
        Assert.EndsWith("UnrealRuntimeDiagnosticTests.cs", diagnostic.Source?.FilePath);
        Assert.True(diagnostic.Source?.Line > 0);
        Assert.Null(diagnostic.Bridge);
    }

    [Fact]
    public void NativeBridgeFailureCarriesExactInvocationSiteAndStableJson()
    {
        UnrealObjectHandle target = UnrealObjectHandle.Create(8, 9);
        var bridge = new NativeUnrealInvocationBridge(new FailingTransport());
        UnrealInvocationException failure = Assert.Throws<UnrealInvocationException>(() =>
            bridge.ReadProperty<int>(target, "/Script/Demo.Actor:Health"));
        var context = new UnrealRuntimeDiagnosticContext(
            2,
            "D:/Project/Demo.dll",
            "Demo.PlayerScript",
            UnrealObjectHandle.Create(2, 3),
            target);

        UnrealRuntimeDiagnostic diagnostic = UnrealRuntimeDiagnosticBuilder.Create(
            11,
            12,
            "TickScript",
            failure,
            context);
        string json = UnrealRuntimeDiagnosticBuilder.CreateJson(
            11,
            12,
            "TickScript",
            failure,
            context);
        using JsonDocument document = JsonDocument.Parse(json);

        Assert.Equal("nativeBridgeFailure", diagnostic.Kind);
        Assert.Equal("ObjectHandleFailure", diagnostic.Bridge?.Error);
        Assert.Equal("StaleHandle", diagnostic.Bridge?.HandleError);
        Assert.Equal("0x0000000900000009", diagnostic.Bridge?.TargetHandle);
        Assert.Equal("/Script/Demo.Actor:Health", diagnostic.Bridge?.MemberPath);
        Assert.Equal(
            UnrealRuntimeDiagnostic.CurrentSchemaVersion,
            document.RootElement.GetProperty("schemaVersion").GetString());
        Assert.Equal(JsonValueKind.String, document.RootElement.GetProperty("worldHostId").ValueKind);
        Assert.Equal(JsonValueKind.String, document.RootElement.GetProperty("targetHandle").ValueKind);
    }

    private static void ThrowManagedFailure() =>
        throw new InvalidOperationException("The scripted tick failed.");

    private sealed class FailingTransport : IUnrealNativeInvocationTransport
    {
        public UnrealInvocationResponse ReadProperty(
            UnrealObjectHandle target,
            string propertyPath) => UnrealInvocationResponse.Failed(
                UnrealInvocationError.ObjectHandleFailure,
                "The UObject generation is stale.",
                UnrealObjectHandleError.StaleHandle);

        public UnrealInvocationResponse WriteProperty(
            UnrealObjectHandle target,
            string propertyPath,
            UnrealInvocationValue value) => throw new NotSupportedException();

        public UnrealInvocationResponse Invoke(
            UnrealObjectHandle target,
            string functionPath,
            IReadOnlyList<UnrealInvocationValue> arguments) =>
            throw new NotSupportedException();
    }
}
