namespace UnifiedScript.Unreal.Demo;

/// <summary>
/// Runtime integration fixture for native-object ownership. The native CMD test
/// installs the ordinary C++ members; this is not yet a generated authoring API.
/// </summary>
public sealed class NativeObjectLifetimeDemoScript : IUnrealManagedScript
{
    private static readonly ulong ConstructId = UnrealMemberId.FromPath("/Native/Tests.Counter:Construct(int32)");
    private static readonly ulong ChildId = UnrealMemberId.FromPath("/Native/Tests.Counter:Child()");
    private static readonly ulong AddId = UnrealMemberId.FromPath("/Native/Tests.CounterChild:Add(int32)");
    private UnrealNativeObjectLease? _owner;
    private UnrealNativeObjectLease? _child;

    public void OnStart(UnrealScriptContext context)
    {
        // Reload rollback restarts the retained generation. Its native objects
        // must still be usable until commit actually retires that generation.
        if (_owner is not null)
        {
            _child!.Invoke(AddId, [Integer(0)]);
            return;
        }
        var scope = UnrealNativeObjectScope.FromBridge(context.Bridge);
        _owner = scope.Construct(ConstructId, [Integer(10)]);
        _child = _owner.Borrow(ChildId, []);
        if (_child.Invoke(AddId, [Integer(5)]).Value.Payload0 != 15)
            throw new InvalidOperationException("Native borrowed member did not update the owned C++ object.");
        // Explicit disposal is safe and invalidates already-created borrowed aliases.
        _owner.Dispose();
        try
        {
            _child.Invoke(AddId, [Integer(1)]);
            throw new InvalidOperationException("A borrowed object survived its native owner.");
        }
        catch (UnrealNativeObjectException exception) when (exception.Status == UnrealNativeObjectStatus.InvalidHandle) { }
        _child.Dispose();
        _owner = scope.Construct(ConstructId, [Integer(20)]);
        _child = _owner.Borrow(ChildId, []);
    }

    public void OnTick(float deltaSeconds) => _child!.Invoke(AddId, [Integer(1)]);

    // Intentionally omit lease disposal: generation shutdown must retire both.
    public void OnStop() { }

    private static UnrealNativeValueV3 Integer(int value) =>
        new() { Kind = UnrealNativeValueKindV3.Int32, Payload0 = unchecked((uint)value) };
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
