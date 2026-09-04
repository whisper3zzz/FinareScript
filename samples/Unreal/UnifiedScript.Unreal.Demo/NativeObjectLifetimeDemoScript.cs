using UnifiedScript;
using UnifiedScript.Unreal.Generated;

namespace UnifiedScript.Unreal.Demo;

/// <summary>
/// Generated ordinary C++ bindings; the API contract owns every native member
/// and the native table installs automatically when its module loads.
/// </summary>
public sealed class NativeObjectLifetimeDemoScript : IUnrealManagedScript
{
    private NativeCounter? _owner;
    private NativeCounterChild? _child;

    [Visual]
    public int Preview(NativeCounter counter, int value)
    {
        return counter.Scale(value);
    }

    public void OnStart(UnrealScriptContext context)
    {
        // Reload rollback restarts the retained generation. Its native objects
        // must still be usable until commit actually retires that generation.
        if (_owner is not null)
        {
            _child!.Add(0);
            return;
        }
        _owner = new NativeCounter(context.Bridge, 10);
        _child = _owner.GetChild();
        if (_child.Add(5) != 15 || Preview(_owner, 2) != 30 || _owner.Scale(0.5) != 7.5 ||
            NativeCounter.Multiply(context.Bridge, 3, 4) != 12 || _owner.Flip(true))
            throw new InvalidOperationException("Native borrowed member did not update the owned C++ object.");
        _owner.Bias = 3;
        if (_owner.Bias != 3 || _owner.Shift(new(1, 2, 3)) != new UnrealVector(4, 5, 6))
            throw new InvalidOperationException("Generated field or const-vector-reference binding failed.");
        try
        {
            using var invalid = _owner.InvalidOwnedAliasForTest();
            throw new InvalidOperationException("Duplicate owned pointer was accepted.");
        }
        catch (UnrealNativeObjectException exception) when (exception.Status == UnrealNativeObjectStatus.WrongOwner) { }
        if (_owner.Scale(2) != 30 || _child.Value != 15)
            throw new InvalidOperationException("Rejected duplicate ownership damaged the original object.");
        using (var clone = _owner.Clone())
        {
            using var child = clone.GetChild();
            child.Value = 99;
            if (child.Value != 99 || _child.Value != 15)
                throw new InvalidOperationException("Owned native clone unexpectedly aliases its original.");
            clone.Reset();
            if (child.Value != 0) throw new InvalidOperationException("Void native method failed.");
        }
        using (var empty = NativeCounter.Create(context.Bridge))
            if (empty.Scale(2) != 2) throw new InvalidOperationException("Default native constructor failed.");
        // Explicit disposal is safe and invalidates already-created borrowed aliases.
        _owner.Dispose();
        try
        {
            _child.Add(1);
            throw new InvalidOperationException("A borrowed object survived its native owner.");
        }
        catch (UnrealNativeObjectException exception) when (exception.Status == UnrealNativeObjectStatus.InvalidHandle) { }
        _child.Dispose();
        _owner = NativeCounter.Create(context.Bridge, 20);
        _child = _owner.GetChild();
    }

    public void OnTick(float deltaSeconds) => _child!.Add(1);

    // Intentionally omit lease disposal: generation shutdown must retire both.
    public void OnStop() { }

}

public sealed class NativeObjectStartFailureDemoScript : IUnrealManagedScript
{
    private readonly NativeObjectLifetimeDemoScript _inner = new();
    public void OnStart(UnrealScriptContext context)
    {
        _inner.OnStart(context);
        throw new InvalidOperationException("NativeObjectStartFailureDemoScript expected startup failure.");
    }
    public void OnTick(float deltaSeconds) { }
    public void OnStop() => _inner.OnStop();
}
