using System.Reflection;

namespace Finare.Unreal;

public abstract partial class UnrealObjectScript
{
    private bool _reflectedPropertyAccessActive;

    /// <summary>Whether generated reflected accessors may use the live host.</summary>
    protected bool IsReflectedPropertyAccessActive => _reflectedPropertyAccessActive;

    /// <summary>
    /// Reads a generated reflected property from the live script host. Before
    /// OnStart the generated accessor may still use its C# initializer; once a
    /// script stops, every access fails instead of falling back to that value.
    /// </summary>
    protected T ReadReflectedProperty<T>(string propertyName)
    {
        IUnrealInvocationBridge bridge = RequireReflectedPropertyAccess();
        UnrealScriptClassAttribute classAttribute = GetRequiredScriptClassAttribute();
        UnrealObject target = UnrealObject.InvocationTarget(Context.Host, bridge);
        return UnrealObjectInvocationAdapter.ReadProperty<T>(
            target,
            0,
            UnrealScriptClassIdentity.MemberPath(
                GetType(),
                classAttribute.GeneratedClassPath!,
                propertyName),
            fast: false);
    }

    /// <summary>
    /// Writes a generated reflected property to the live script host. A
    /// replicated write checks authority before crossing the native boundary.
    /// </summary>
    protected void WriteReflectedProperty<T>(
        string propertyName,
        T value,
        bool replicated = false)
    {
        IUnrealInvocationBridge bridge = RequireReflectedPropertyAccess();
        if (replicated && !HasActorAuthority(bridge))
        {
            throw new InvalidOperationException(
                $"Replicated reflected property '{GetType().FullName}.{propertyName}' " +
                "cannot be written by a non-authoritative Actor.");
        }

        UnrealScriptClassAttribute classAttribute = GetRequiredScriptClassAttribute();
        UnrealObject target = UnrealObject.InvocationTarget(Context.Host, bridge);
        UnrealObjectInvocationAdapter.WriteProperty(
            target,
            0,
            UnrealScriptClassIdentity.MemberPath(
                GetType(),
                classAttribute.GeneratedClassPath!,
                propertyName),
            value,
            fast: false);
    }

    private IUnrealInvocationBridge RequireReflectedPropertyAccess()
    {
        if (!_reflectedPropertyAccessActive || !IsRunning)
        {
            throw new ObjectDisposedException(
                nameof(UnrealObjectScript),
                "Reflected Unreal properties are available only while the script is running.");
        }

        return Context.Bridge;
    }

    private UnrealScriptClassAttribute GetRequiredScriptClassAttribute()
    {
        UnrealScriptClassAttribute? attribute = GetType().GetCustomAttribute<UnrealScriptClassAttribute>(false);
        if (attribute is null || string.IsNullOrWhiteSpace(attribute.GeneratedClassPath))
        {
            throw new InvalidOperationException(
                $"Script '{GetType().FullName}' must declare an UnrealScriptClass GeneratedClassPath " +
                "to use reflected properties.");
        }

        return attribute;
    }

    private bool HasActorAuthority(IUnrealInvocationBridge bridge)
    {
        if (Context.Target.IsNull)
        {
            throw new InvalidOperationException(
                "Replicated reflected properties require an Actor target.");
        }

        return bridge.Invoke<bool>(
            Context.Target,
            "/Script/Engine.Actor:HasAuthority",
            Array.Empty<object?>());
    }

    internal void ActivateReflectedPropertyAccess() => _reflectedPropertyAccessActive = true;

}
