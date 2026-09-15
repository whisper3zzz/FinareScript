using Finare.Unreal;
using Finare.InteropParity.Generated;

namespace Finare.InteropParity;

public sealed class ParityScript : IUnrealManagedScript
{
    private ParityBuffer? _buffer;
    private UnrealCallback<CallbackHandler>? _callback;
    private UnrealArrayView<int>? _view;
    public void OnStart(UnrealScriptContext context)
    {
        var fixture = new FinareInteropParityFixture(context.Target, context.Bridge);
        _view = fixture.NumbersNative;
        _view.Clear(); _view.Add(1); _view.Add(2); _view.Add(3);
        _view.WithSpan(values => { for (int i = 0; i < values.Length; ++i) values[i] *= 10; });
        Check(_view[1] == 20, "UE array shared-memory write");
        _view.ReadSpan(values => Check(values.SequenceEqual(new[] { 10, 20, 30 }), "UE span contents"));
        var index = _view.Element(0);
        for (int i = 0; i < 100; ++i) _view.Add(i);
        index.Value = 41;
        Check(_view[0] == 41, "Array view after reallocation");
        try { _view.WithSpan(values => { values[0] = 42; throw new ApplicationException("span probe"); }); }
        catch (ApplicationException error) when (error.Message == "span probe") { }
        Check(_view[0] == 42, "Span exception releases borrow and preserves in-place edits");
        _view.WithSpan(values => ExpectRejected(() => _view.Add(1)));
        ExpectRejected(() => fixture.ReadOnlyNumbersNative.WithSpan(values => values[0] = 99));
        fixture.BytesNative.WithSpan(bytes => { bytes[0] = 0xAB; bytes[1] = 0xCD; });

        var target = fixture.TargetsNative[0];
        Check(target is not null, "Interface array element");
        fixture.TargetsNative.Add(target);
        fixture.TargetMapNative[2] = target;
        Check(fixture.TargetMapNative[2]!.Handle == target!.Handle, "Interface map identity");
        fixture.IntSetNative.Add(7); fixture.IntSetNative.Add(7);
        Check(fixture.IntSetNative.Count == 1 && fixture.IntSetNative.Remove(7), "Set in-place operations");
        var item = fixture.GroupNative.Items[0];
        Check(item.Target!.Handle == target.Handle, "Struct interface field");
        fixture.GroupNative.Items.Add(item);
        fixture.GroupNative.Items.ElementFields(1).Field<int>("Value").Value = 73;
        Check(fixture.GroupNative.Items[1].Value == 73, "Array of structs field in-place");
        fixture.GroupNative.Lookup[4] = item;
        Check(fixture.GroupNative.Lookup[4].Target!.Handle == target.Handle, "Struct map value with interface");
        // Exercise both the generated value surface and the live surface.
        var snapshot = fixture.Group;
        fixture.Group = snapshot;
        Check(fixture.Group.Items.Count == 2, "Struct containing custom-struct collection roundtrip");

        _buffer = new ParityBuffer(context.Bridge);
        ParityBase baseView = _buffer;
        Check(baseView.Add(5) == 12, "Adjusted native base pointer");
        _buffer.Samples.WithSpan(values => { values[0] = 100; values[1] = 200; values[2] = 300; });
        Check(_buffer.Sum() == 600, "std::vector is the same native allocation");
        _buffer.Samples.WithSpan(values => ExpectRejected(() => _buffer.Resize(50)));
        _buffer.Resize(4);
        _buffer.Samples.WithSpan(values => values[3] = 400);
        Check(_buffer.Sum() == 1000, "Native buffer reacquired after resize");
        _callback = UnrealCallback<CallbackHandler>.Create(context.Bridge, value => value + 9);
        _buffer.Save(_callback);
        Check(_buffer.Call(5) == 14, "Retained std::function invokes C#");
        _callback.Dispose();
        Check(_buffer.Call(5) == 0, "Disposed callback cannot enter C#");
        _callback = UnrealCallback<CallbackHandler>.Create(context.Bridge, value => value + 19);
        _buffer.Save(_callback);
        fixture.Result = 1234;
    }
    public void OnTick(float deltaSeconds)
    {
        Check(_buffer!.Call(5) == 24 && _view![0] == 42, "Active generation retains views and callbacks");
    }
    // Intentionally rely on generation cleanup for both native ownership and callback registrations.
    public void OnStop() { }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void ExpectRejected(Action action)
    {
        try { action(); } catch (InvalidOperationException) { return; }
        throw new Exception("Invalid native access was accepted.");
    }
}
