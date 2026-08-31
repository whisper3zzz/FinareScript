using System.Text.Json;
using UnifiedScript.Unreal;

namespace UnifiedScript.Tests;

public sealed class UnrealManagedReloadTransactionTests
{
    [Fact]
    public void PreparedCandidateCanCommitAndFinalizeWithoutAdvancingEarly()
    {
        var transport = new ReloadTransport();
        var bridge = new NativeUnrealInvocationBridge(transport, worldHostId: 17);
        UnrealObjectHandle host = UnrealObjectHandle.Create(1, 1);
        using var instance = UnrealWorldManagedEntryPoint.ManagedScriptInstance.Create(
            worldId: 17,
            instanceId: 31,
            bridge,
            host,
            host,
            typeof(StableReloadScript).Assembly.Location,
            typeof(StableReloadScript).FullName!);
        instance.Start();

        int candidate = instance.PrepareReload(
            typeof(PreparedReloadScript).Assembly.Location,
            typeof(PreparedReloadScript).FullName!);

        Assert.Equal(2, candidate);
        Assert.Equal(1, instance.GetDiagnosticContext().Generation);
        Assert.Throws<InvalidOperationException>(() => instance.Tick(0.016f));
        Assert.Equal("prepared-start", transport.LastMarker);
        Assert.Equal("prepared", ReadReloadState(instance).GetProperty("state").GetString());

        Assert.Equal(2, instance.CommitPreparedReload(candidate));
        Assert.Equal(2, instance.GetDiagnosticContext().Generation);
        Assert.Equal(
            "commitPendingFinalize",
            ReadReloadState(instance).GetProperty("state").GetString());

        instance.FinalizePreparedReload(candidate);
        instance.Tick(0.016f);
        Assert.Equal("prepared-tick", transport.LastMarker);
        JsonElement committed = ReadReloadState(instance);
        Assert.Equal("committed", committed.GetProperty("state").GetString());
        Assert.Contains(
            committed.GetProperty("phases").EnumerateArray(),
            static phase => phase.GetProperty("name").GetString() == "batchPrepare");
        Assert.Contains(
            committed.GetProperty("phases").EnumerateArray(),
            static phase => phase.GetProperty("name").GetString() == "batchCommit");
        Assert.Contains(
            committed.GetProperty("phases").EnumerateArray(),
            static phase => phase.GetProperty("name").GetString() == "oldContextRetirement");
    }

    [Fact]
    public void AbortAfterCommitRestoresRetainedGeneration()
    {
        var transport = new ReloadTransport();
        var bridge = new NativeUnrealInvocationBridge(transport, worldHostId: 18);
        UnrealObjectHandle host = UnrealObjectHandle.Create(1, 1);
        using var instance = UnrealWorldManagedEntryPoint.ManagedScriptInstance.Create(
            worldId: 18,
            instanceId: 32,
            bridge,
            host,
            host,
            typeof(StableReloadScript).Assembly.Location,
            typeof(StableReloadScript).FullName!);
        instance.Start();

        int candidate = instance.PrepareReload(
            typeof(PreparedReloadScript).Assembly.Location,
            typeof(PreparedReloadScript).FullName!);
        _ = instance.CommitPreparedReload(candidate);
        instance.AbortPreparedReload(candidate);
        instance.Tick(0.016f);

        Assert.Equal(1, instance.GetDiagnosticContext().Generation);
        Assert.Equal("stable-tick", transport.LastMarker);
        Assert.Equal("rolledBack", ReadReloadState(instance).GetProperty("state").GetString());
    }

    [Fact]
    public void CandidateStartFailureNeverPublishesOrRetiresOldGeneration()
    {
        var transport = new ReloadTransport();
        var bridge = new NativeUnrealInvocationBridge(transport, worldHostId: 19);
        UnrealObjectHandle host = UnrealObjectHandle.Create(1, 1);
        using var instance = UnrealWorldManagedEntryPoint.ManagedScriptInstance.Create(
            worldId: 19,
            instanceId: 33,
            bridge,
            host,
            host,
            typeof(StableReloadScript).Assembly.Location,
            typeof(StableReloadScript).FullName!);
        instance.Start();

        InvalidOperationException failure = Assert.Throws<InvalidOperationException>(() =>
            instance.PrepareReload(
                typeof(FailingPreparedReloadScript).Assembly.Location,
                typeof(FailingPreparedReloadScript).FullName!));
        instance.Tick(0.016f);

        Assert.Contains("injected prepared start failure", failure.Message);
        Assert.Equal(1, instance.GetDiagnosticContext().Generation);
        Assert.Equal("stable-tick", transport.LastMarker);
        Assert.Equal("rolledBack", ReadReloadState(instance).GetProperty("state").GetString());
    }

    private static JsonElement ReadReloadState(
        UnrealWorldManagedEntryPoint.ManagedScriptInstance instance)
    {
        using JsonDocument document = JsonDocument.Parse(instance.GetReloadState());
        return document.RootElement.Clone();
    }

    private sealed class ReloadTransport : IUnrealNativeInvocationTransport
    {
        public string LastMarker { get; private set; } = string.Empty;

        public UnrealInvocationResponse ReadProperty(
            UnrealObjectHandle target,
            string propertyPath) => UnrealInvocationResponse.Failed(
                UnrealInvocationError.PropertyNotFound,
                propertyPath);

        public UnrealInvocationResponse WriteProperty(
            UnrealObjectHandle target,
            string propertyPath,
            UnrealInvocationValue value)
        {
            LastMarker = value.StringValue ?? string.Empty;
            return UnrealInvocationResponse.Succeeded(UnrealInvocationValue.Void);
        }

        public UnrealInvocationResponse Invoke(
            UnrealObjectHandle target,
            string functionPath,
            IReadOnlyList<UnrealInvocationValue> arguments) =>
            UnrealInvocationResponse.Failed(
                UnrealInvocationError.FunctionNotFound,
                functionPath);
    }
}

public sealed class StableReloadScript : IUnrealManagedScript
{
    private UnrealScriptContext context;

    public void OnStart(UnrealScriptContext value)
    {
        context = value;
        context.Bridge.WriteProperty(context.Host, "Marker", "stable-start");
    }

    public void OnTick(float deltaSeconds) =>
        context.Bridge.WriteProperty(context.Host, "Marker", "stable-tick");

    public void OnStop() =>
        context.Bridge.WriteProperty(context.Host, "Marker", "stable-stop");
}

public sealed class PreparedReloadScript : IUnrealManagedScript
{
    private UnrealScriptContext context;

    public void OnStart(UnrealScriptContext value)
    {
        context = value;
        context.Bridge.WriteProperty(context.Host, "Marker", "prepared-start");
    }

    public void OnTick(float deltaSeconds) =>
        context.Bridge.WriteProperty(context.Host, "Marker", "prepared-tick");

    public void OnStop() =>
        context.Bridge.WriteProperty(context.Host, "Marker", "prepared-stop");
}

public sealed class FailingPreparedReloadScript : IUnrealManagedScript
{
    public void OnStart(UnrealScriptContext context) =>
        throw new InvalidOperationException("injected prepared start failure");

    public void OnTick(float deltaSeconds)
    {
    }

    public void OnStop()
    {
    }
}
