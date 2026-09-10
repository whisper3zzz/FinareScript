using Finare;
using Finare.Unreal.Generated;

namespace Finare.Unreal.Demo;

/// <summary>Ordinary generated calls to C++ members without UHT member metadata.</summary>
public sealed class NativeExtensionDemoScript : IUnrealManagedScript
{
    private FinareNativeExtensionFixture? _fixture;

    [Visual]
    public int ScalePreview(FinareNativeExtensionFixture fixture, int value)
    {
        return fixture.Scale(value);
    }

    public void OnStart(UnrealScriptContext context)
    {
        _fixture = new FinareNativeExtensionFixture(context.Target, context.Bridge);
        _fixture.Reset();
        if (ScalePreview(_fixture, 7) != 42 || _fixture.Scale(0.5) != 3.0 || _fixture.Multiply(3, 4) != 12)
            throw new InvalidOperationException("Generated native extension overload/static dispatch failed.");
        int total = 10;
        if (!_fixture.Accumulate(new[] { 1, 2, 3 }, ref total, out string label) || total != 16 || label != "total:16")
            throw new InvalidOperationException("Generated native extension container/ref/out dispatch failed.");
        _fixture.Counter = total;
        if (_fixture.Counter != 16)
            throw new InvalidOperationException("Generated non-UPROPERTY field access failed.");
    }

    public void OnTick(float deltaSeconds)
    {
        if (_fixture is null) throw new InvalidOperationException("Native extension script was not started.");
        _fixture.Counter += 1;
    }

    public void OnStop() => _fixture = null;
}
