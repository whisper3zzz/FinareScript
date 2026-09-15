using System.Reflection;

namespace Finare.Unreal;

public abstract partial class UnrealObjectScript
{
    private bool _reflectedPropertyAccessActive;

    /// <summary>Whether generated reflected accessors may use the live host.</summary>
    protected bool IsReflectedPropertyAccessActive => _reflectedPropertyAccessActive;

    protected UnrealArrayView<T> NativeArray<T>(string propertyName) => new(NativeViewOwner(), NativeViewPath(propertyName));
    protected UnrealSetView<T> NativeSet<T>(string propertyName) => new(NativeViewOwner(), NativeViewPath(propertyName));
    protected UnrealMapView<K, V> NativeMap<K, V>(string propertyName) where K : notnull => new(NativeViewOwner(), NativeViewPath(propertyName));
    protected UnrealNativeView NativeStruct(string propertyName) => new(NativeViewOwner(), NativeViewPath(propertyName));
    private UnrealObject NativeViewOwner() => UnrealObject.InvocationTarget(_constructionContext?.HostHandle ?? Context.Host, RequireReflectedPropertyAccess());
    private string NativeViewPath(string propertyName) => UnrealScriptClassIdentity.MemberPath(GetType(), GetRequiredScriptClassAttribute().GeneratedClassPath!, propertyName);

    /// <summary>
    /// Reads a generated property from the constructing or live native host.
    /// Before activation the accessor may use its C# initializer; once its
    /// construction/lifecycle scope ends, access fails rather than returning it.
    /// </summary>
    protected T ReadReflectedProperty<T>(string propertyName)
    {
        IUnrealInvocationBridge bridge = RequireReflectedPropertyAccess();
        UnrealScriptClassAttribute classAttribute = GetRequiredScriptClassAttribute();
        UnrealObject target = UnrealObject.InvocationTarget(_constructionContext?.HostHandle ?? Context.Host, bridge);
        string path = UnrealScriptClassIdentity.MemberPath(GetType(), classAttribute.GeneratedClassPath!, propertyName);
        return UnrealObjectInvocationAdapter.ReadProperty<T>(
            target,
            UnrealMemberId.FromPath(path), path, fast: true);
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
        if (replicated && _constructionContext is null && !HasActorAuthority(bridge))
        {
            throw new InvalidOperationException(
                $"Replicated reflected property '{GetType().FullName}.{propertyName}' " +
                "cannot be written by a non-authoritative Actor.");
        }

        UnrealScriptClassAttribute classAttribute = GetRequiredScriptClassAttribute();
        UnrealObject target = UnrealObject.InvocationTarget(_constructionContext?.HostHandle ?? Context.Host, bridge);
        string path = UnrealScriptClassIdentity.MemberPath(GetType(), classAttribute.GeneratedClassPath!, propertyName);
        UnrealObjectInvocationAdapter.WriteProperty(
            target,
            UnrealMemberId.FromPath(path), path, value, fast: true);
    }

    private IUnrealInvocationBridge RequireReflectedPropertyAccess()
    {
        if (_constructionContext is not null) return _constructionContext.Bridge;
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
