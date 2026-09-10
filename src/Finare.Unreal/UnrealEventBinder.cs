using System.Reflection;
using System.Runtime.ExceptionServices;

namespace Finare.Unreal;

/// <summary>
/// Resolves declaration-authored UE event handlers against generated wrapper
/// endpoints. Subscriptions remain owned by the generation-scoped bridge.
/// </summary>
public static class UnrealEventBinder
{
    public static IReadOnlyList<IUnrealEventSubscription> BindAttributedEvents(
        object script,
        UnrealScriptContext context)
    {
        ArgumentNullException.ThrowIfNull(script);

        var subscriptions = new List<IUnrealEventSubscription>();
        try
        {
            foreach ((MethodInfo handlerMethod, UnrealEventAttribute binding) in
                     EnumerateBindings(script.GetType()))
            {
                UnrealObject source = ResolveSource(script, context, binding);
                MethodInfo endpoint = ResolveEndpoint(binding);
                ParameterInfo[] endpointParameters = endpoint.GetParameters();
                if (endpointParameters.Length != 1 ||
                    !typeof(Delegate).IsAssignableFrom(endpointParameters[0].ParameterType))
                {
                    throw new InvalidOperationException(
                        $"Generated UE event endpoint '{endpoint.DeclaringType?.FullName}." +
                        $"{endpoint.Name}' does not accept exactly one delegate.");
                }

                Delegate handler = Delegate.CreateDelegate(
                    endpointParameters[0].ParameterType,
                    script,
                    handlerMethod,
                    throwOnBindFailure: true) ??
                    throw new InvalidOperationException(
                        $"Method '{handlerMethod.DeclaringType?.FullName}.{handlerMethod.Name}' " +
                        $"does not match UE event '{binding.EventPath}'.");
                object? result;
                try
                {
                    result = endpoint.Invoke(source, [handler]);
                }
                catch (TargetInvocationException exception)
                    when (exception.InnerException is not null)
                {
                    ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
                    throw;
                }

                if (result is not IUnrealEventSubscription subscription)
                {
                    throw new InvalidOperationException(
                        $"Generated UE event endpoint '{endpoint.DeclaringType?.FullName}." +
                        $"{endpoint.Name}' did not return an event subscription.");
                }
                subscriptions.Add(subscription);
            }
            return subscriptions;
        }
        catch
        {
            foreach (IUnrealEventSubscription subscription in subscriptions)
            {
                subscription.Dispose();
            }
            throw;
        }
    }

    private static IEnumerable<(MethodInfo Method, UnrealEventAttribute Binding)>
        EnumerateBindings(Type scriptType) => scriptType
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic |
                BindingFlags.DeclaredOnly)
            .OrderBy(static method => method.MetadataToken)
            .SelectMany(method => method
                .GetCustomAttributes<UnrealEventAttribute>(inherit: false)
                .Select(binding => (method, binding)));

    private static UnrealObject ResolveSource(
        object script,
        UnrealScriptContext context,
        UnrealEventAttribute binding)
    {
        if (binding.SourceMember is not null)
        {
            const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public |
                BindingFlags.NonPublic;
            Type scriptType = script.GetType();
            object? value = scriptType.GetField(binding.SourceMember, Flags)?.GetValue(script) ??
                scriptType.GetProperty(binding.SourceMember, Flags)?.GetValue(script);
            if (value is not UnrealObject wrapper ||
                !binding.SourceType.IsInstanceOfType(wrapper))
            {
                throw new InvalidOperationException(
                    $"UE event source member '{scriptType.FullName}.{binding.SourceMember}' " +
                    $"must contain a '{binding.SourceType.FullName}' wrapper after OnStart.");
            }
            return wrapper;
        }

        UnrealObjectHandle handle = binding.Source == UnrealEventSource.Host
            ? context.Host
            : context.Target;
        object? source = Activator.CreateInstance(binding.SourceType, handle, context.Bridge);
        return source as UnrealObject ??
            throw new InvalidOperationException(
                $"UE event source wrapper '{binding.SourceType.FullName}' must expose a public " +
                $"constructor ({nameof(UnrealObjectHandle)}, {nameof(IUnrealInvocationBridge)}).");
    }

    private static MethodInfo ResolveEndpoint(UnrealEventAttribute binding)
    {
        MethodInfo[] matches = binding.SourceType
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic |
                BindingFlags.DeclaredOnly)
            .Where(method => method.GetCustomAttribute<UnrealEventEndpointAttribute>(false)
                is { } endpoint && endpoint.EventPath == binding.EventPath)
            .ToArray();
        return matches.Length switch
        {
            1 => matches[0],
            0 => throw new InvalidOperationException(
                $"Generated wrapper '{binding.SourceType.FullName}' has no event endpoint for " +
                $"'{binding.EventPath}'. Regenerate its Unreal bindings."),
            _ => throw new InvalidOperationException(
                $"Generated wrapper '{binding.SourceType.FullName}' has multiple event endpoints " +
                $"for '{binding.EventPath}'."),
        };
    }
}
