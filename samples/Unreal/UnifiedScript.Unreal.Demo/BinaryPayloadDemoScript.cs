using UnifiedScript;
using UnifiedScript.Unreal.Generated;

namespace UnifiedScript.Unreal.Demo;

public sealed class BinaryPayloadDemoScript : IUnrealManagedScript
{
    private UnrealByteBuffer _snapshot;

    [Visual]
    public UnrealByteBuffer Read(UnifiedScriptNativeExtensionFixture target)
    {
        return target.ReadBytes();
    }

    public void OnStart(UnrealScriptContext context)
    {
        var target = new UnifiedScriptNativeExtensionFixture(context.Target, context.Bridge);
        byte[] bytes = new byte[65536];
        for (int index = 0; index < bytes.Length; index++) bytes[index] = (byte)(index % 251);
        target.Payload = UnrealByteBuffer.Borrow(bytes);
        bytes[0] = 99;
        _snapshot = target.Payload;
        if (_snapshot.IsBorrowed || _snapshot.Length != bytes.Length || _snapshot[0] != 0 || _snapshot[250] != 250)
            throw new InvalidOperationException("Native binary property is not an independent snapshot.");

        int before = target.BinaryCalls;
        var result = Read(target); // Large return must retry output sizing, not execute twice.
        if (target.BinaryCalls != before + 1 || !result.Span.SequenceEqual(_snapshot.Span))
            throw new InvalidOperationException("Binary return retry repeated native side effects or corrupted bytes.");
        var inOut = default(UnrealByteBuffer);
        before = target.BinaryCalls;
        result = target.TransformBytes(_snapshot, ref inOut, out int calls);
        if (calls != before + 1 || target.BinaryCalls != calls || result.Length != 73728 ||
            result[0] != 0xA5 || result[250] != (250 ^ 0xA5) || result[65536] != 0 ||
            !result.Span.SequenceEqual(inOut.Span))
            throw new InvalidOperationException("Binary input/ref/out/return exchange failed.");
        target.StoreBytes(UnrealByteBuffer.Borrow(bytes).Slice(250, 3));
        var slice = target.Payload;
        if (slice.Length != 3 || slice[0] != 250 || slice[1] != 0 || slice[2] != 1)
            throw new InvalidOperationException("Binary slice offset was not preserved.");
        target.StoreBytes(default);
        if (target.Payload.Length != 0 || _snapshot[0] != 0 || _snapshot[250] != 250)
            throw new InvalidOperationException("Empty transfer or snapshot ownership failed.");
    }
    public void OnTick(float deltaSeconds) { }
    public void OnStop()
    {
        if (_snapshot.Length != 0 && (_snapshot.Length != 65536 || _snapshot[250] != 250))
            throw new InvalidOperationException("Owned binary result changed after its native storage was replaced.");
    }
}
