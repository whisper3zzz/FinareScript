using System.Reflection;

namespace UnifiedScript.Unreal;

public abstract partial class UnrealObjectScript
{
    /// <summary>The owning Actor's current UE authority; no cached role survives travel/reload.</summary>
    public bool HasAuthority => Bridge.Invoke<bool>(TargetHandle, "/Script/Engine.Actor:HasAuthority", Array.Empty<object?>());

    protected void SendRpc(Action method) => SendRpcDelegate(method, [], []);
    protected void SendRpc<T>(Action<T> method, T value) => SendRpcDelegate(method, [typeof(T)], [value]);
    protected void SendRpc<T0, T1>(Action<T0, T1> method, T0 a, T1 b) => SendRpcDelegate(method, [typeof(T0), typeof(T1)], [a, b]);
    protected void SendRpc<T0, T1, T2>(Action<T0, T1, T2> method, T0 a, T1 b, T2 c) => SendRpcDelegate(method, [typeof(T0), typeof(T1), typeof(T2)], [a, b, c]);
    protected void SendRpc<T0, T1, T2, T3>(Action<T0, T1, T2, T3> method, T0 a, T1 b, T2 c, T3 d) => SendRpcDelegate(method, [typeof(T0), typeof(T1), typeof(T2), typeof(T3)], [a, b, c, d]);

    // Name-based overloads also fit Visual call boundaries. The actual declared
    // signature is checked before dispatch; nameof keeps source rename support.
    protected void SendRpc(string method) => SendRpcNamed(method, [], []);
    protected void SendRpc<T>(string method, T value) => SendRpcNamed(method, [typeof(T)], [value]);
    protected void SendRpc<T0, T1>(string method, T0 a, T1 b) => SendRpcNamed(method, [typeof(T0), typeof(T1)], [a, b]);
    protected void SendRpc<T0, T1, T2>(string method, T0 a, T1 b, T2 c) => SendRpcNamed(method, [typeof(T0), typeof(T1), typeof(T2)], [a, b, c]);
    protected void SendRpc<T0, T1, T2, T3>(string method, T0 a, T1 b, T2 c, T3 d) => SendRpcNamed(method, [typeof(T0), typeof(T1), typeof(T2), typeof(T3)], [a, b, c, d]);

    private void SendRpcDelegate(Delegate method, Type[] types, object?[] arguments)
    {
        ArgumentNullException.ThrowIfNull(method);
        if (!ReferenceEquals(method.Target, this) || method.GetInvocationList().Length != 1)
            throw new ArgumentException("An RPC delegate must name one method on this live Script Class.", nameof(method));
        SendRpcCore(method.Method, types, arguments);
    }

    private void SendRpcNamed(string name, Type[] types, object?[] arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        MethodInfo method = GetType().GetMethod(name, BindingFlags.Public | BindingFlags.Instance, types) ??
            throw new MissingMethodException($"RPC '{name}' has no matching typed Script Class declaration.");
        SendRpcCore(method, types, arguments);
    }

    private void SendRpcCore(MethodInfo method, Type[] types, object?[] arguments)
    {
        _ = RequireRunning();
        var rpc = method.GetCustomAttribute<UnrealRpcAttribute>();
        var scriptClass = GetType().GetCustomAttribute<UnrealScriptClassAttribute>();
        if (rpc is null || !Enum.IsDefined(rpc.Target) || !method.IsPublic || method.IsStatic || method.IsGenericMethod ||
            method.ReturnType != typeof(void) || !method.GetParameters().Select(p => p.ParameterType).SequenceEqual(types) ||
            scriptClass?.Kind is not (UnrealScriptClassKind.Actor or UnrealScriptClassKind.ActorComponent) ||
            string.IsNullOrWhiteSpace(scriptClass.GeneratedClassPath))
            throw new InvalidOperationException("RPC sends require a generated gameplay class and a public instance void RPC with the exact input signature.");
        UnrealObjectHandle receiver = scriptClass.Kind == UnrealScriptClassKind.ActorComponent ? HostHandle : TargetHandle;
        // A listen-server/local call can synchronously enter the receive body.
        // Publish outer edits before it hydrates; then retain its resulting state.
        _stateSession?.Flush();
        Bridge.Invoke(receiver, $"{scriptClass.GeneratedClassPath}:{method.Name}", arguments);
        _stateSession?.Hydrate();
    }
}
