using System.Reflection;
using System.Collections.ObjectModel;
using UnifiedScript.Core;

namespace UnifiedScript.Unreal;

/// <summary>
/// The immutable, generation-local portion of a Blueprint override contract.
/// It deliberately stores only stable strings and the already published V2 ABI
/// canonical signature, so no collectible Type or MethodInfo can escape a
/// generation.
/// </summary>
internal sealed record UnrealBlueprintOverrideRoute(
    string ManagedMethodIdentity,
    string DeclaringClassPath,
    string FunctionName,
    UnrealBlueprintEventSuperMode SuperMode,
    string AbiSignature)
{
    internal string RouteKey =>
        DeclaringClassPath + "\0" + FunctionName;

    internal string DisplayName =>
        $"{DeclaringClassPath}:{FunctionName}";
}

internal static class UnrealBlueprintOverrideContract
{
    private const BindingFlags DeclaredMethodFlags =
        BindingFlags.Instance | BindingFlags.Static |
        BindingFlags.Public | BindingFlags.NonPublic |
        BindingFlags.DeclaredOnly;

    internal static IReadOnlyDictionary<string, UnrealBlueprintOverrideRoute> Create(
        Type scriptType)
    {
        ArgumentNullException.ThrowIfNull(scriptType);
        MethodInfo[] activeInstanceMethods = scriptType.GetMethods(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.FlattenHierarchy);
        var routes = new Dictionary<string, UnrealBlueprintOverrideRoute>(
            StringComparer.OrdinalIgnoreCase);
        foreach (MethodInfo method in EnumerateMethods(scriptType))
        {
            UnrealBlueprintOverrideAttribute? attribute =
                method.GetCustomAttribute<UnrealBlueprintOverrideAttribute>(inherit: false);
            if (attribute is null)
            {
                continue;
            }

            ValidateMethod(method, attribute, scriptType);
            if (!activeInstanceMethods.Contains(method))
            {
                throw new InvalidOperationException(
                    $"Blueprint override method '{GetMethodIdentity(method, scriptType)}' is hidden " +
                    "from the active managed instance dispatch surface.");
            }
            if (!UnrealManagedFunctionDispatcherV2.TryCreateReviewedSignature(
                    method,
                    out UnrealManagedFunctionDispatcherV2.UnrealManagedFunctionSignature? signature) ||
                signature is null)
            {
                throw new InvalidOperationException(
                    $"Blueprint override method '{GetMethodIdentity(method, scriptType)}' " +
                    "does not satisfy the V2 managed function contract; its return or " +
                    "parameter shape is unsupported or cannot be bound.");
            }

            var route = new UnrealBlueprintOverrideRoute(
                GetMethodIdentity(method, scriptType),
                attribute.DeclaringClassPath,
                attribute.FunctionName,
                attribute.SuperMode,
                signature.Canonical);
            if (!routes.TryAdd(route.RouteKey, route))
            {
                UnrealBlueprintOverrideRoute existing = routes[route.RouteKey];
                throw new InvalidOperationException(
                    $"Duplicate Blueprint override route '{route.DisplayName}' " +
                    $"(case-insensitive) is declared by '{existing.ManagedMethodIdentity}' " +
                    $"and '{route.ManagedMethodIdentity}'.");
            }
        }

        return new ReadOnlyDictionary<string, UnrealBlueprintOverrideRoute>(routes);
    }

    private static IEnumerable<MethodInfo> EnumerateMethods(Type scriptType)
    {
        for (Type? current = scriptType; current is not null; current = current.BaseType)
        {
            foreach (MethodInfo method in current.GetMethods(DeclaredMethodFlags))
            {
                yield return method;
            }
        }
    }

    private static void ValidateMethod(
        MethodInfo method,
        UnrealBlueprintOverrideAttribute attribute,
        Type scriptType)
    {
        string methodIdentity = GetMethodIdentity(method, scriptType);
        if (!method.IsPublic || method.IsStatic)
        {
            throw new InvalidOperationException(
                $"Blueprint override method '{methodIdentity}' must be a public instance method.");
        }
        if (method.ContainsGenericParameters || method.IsGenericMethod)
        {
            throw new InvalidOperationException(
                $"Blueprint override method '{methodIdentity}' cannot be generic.");
        }
        if (attribute.SuperMode != UnrealBlueprintEventSuperMode.Explicit)
        {
            throw new InvalidOperationException(
                $"Blueprint override method '{methodIdentity}' supports only " +
                "SuperMode=Explicit.");
        }
        if (!ScriptAssetJson.IsValidBlueprintOverrideClassPath(attribute.DeclaringClassPath))
        {
            throw new InvalidOperationException(
                $"Blueprint override method '{methodIdentity}' has an invalid declaring " +
                $"class path '{attribute.DeclaringClassPath}'.");
        }
        if (!ScriptAssetJson.IsValidBlueprintOverrideFunctionName(attribute.FunctionName))
        {
            throw new InvalidOperationException(
                $"Blueprint override method '{methodIdentity}' has an invalid function " +
                $"name '{attribute.FunctionName}'.");
        }

        var conflicts = new List<string>();
        if (method.GetCustomAttribute<UnrealBlueprintEventAttribute>(inherit: false) is not null)
        {
            conflicts.Add(nameof(UnrealBlueprintEventAttribute));
        }
        if (method.GetCustomAttributes<UnrealEventAttribute>(inherit: false).Any())
        {
            conflicts.Add(nameof(UnrealEventAttribute));
        }
        if (method.GetCustomAttribute<UnrealRpcAttribute>(inherit: false) is not null)
        {
            conflicts.Add(nameof(UnrealRpcAttribute));
        }
        if (method.GetCustomAttribute<UnrealRepNotifyAttribute>(inherit: false) is not null)
        {
            conflicts.Add(nameof(UnrealRepNotifyAttribute));
        }
        if (method.GetCustomAttributes<UnrealInputActionAttribute>(inherit: false).Any())
        {
            conflicts.Add(nameof(UnrealInputActionAttribute));
        }
        if (method.GetCustomAttributes<UnrealInterfaceFunctionAttribute>(inherit: false).Any())
        {
            conflicts.Add(nameof(UnrealInterfaceFunctionAttribute));
        }
        if (conflicts.Count != 0)
        {
            throw new InvalidOperationException(
                $"Blueprint override method '{methodIdentity}' cannot be combined with " +
                $"{string.Join(", ", conflicts)}.");
        }
    }

    private static string GetMethodIdentity(MethodInfo method, Type scriptType)
    {
        string declaringType = method.DeclaringType?.FullName ??
            scriptType.FullName ?? scriptType.Name;
        return $"{declaringType}.{method.Name}";
    }
}
