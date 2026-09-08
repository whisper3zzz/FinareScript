using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace UnifiedScript.Unreal;

/// <summary>
/// Cached bridge between managed Script Class state and generated Blueprint
/// properties. Declaration IR owns the shape; this layer only moves values at
/// managed lifecycle boundaries and never discovers arbitrary native members.
/// </summary>
internal static class UnrealScriptStateSynchronizer
{
    // Script types live in collectible AssemblyLoadContexts. A process-wide
    // dictionary would root both the Type and its compiled accessors forever,
    // preventing hot-reload generations from unloading. ConditionalWeakTable
    // keeps the per-type fast path without extending the type's lifetime.
    private static readonly ConditionalWeakTable<Type, StateContract> Contracts = new();

    public static StateSession? Open(
        UnrealObjectScript script,
        UnrealScriptContext context)
    {
        StateContract contract = Contracts.GetValue(script.GetType(), StateContract.Create);
        if (contract.Bindings.Count == 0)
        {
            return null;
        }

        UnrealObjectHandle handle = contract.Kind == UnrealScriptClassKind.ActorComponent
            ? context.Host
            : context.Target;
        return new StateSession(script, context.Bridge, handle, context.Target, contract.Bindings);
    }

    internal sealed class StateSession
    {
        private readonly UnrealObjectScript _script;
        private readonly IUnrealInvocationBridge _bridge;
        private readonly UnrealObjectHandle _handle;
        private readonly UnrealObjectHandle _actor;
        private readonly IReadOnlyList<StateBinding> _bindings;

        internal StateSession(
            UnrealObjectScript script,
            IUnrealInvocationBridge bridge,
            UnrealObjectHandle handle,
            UnrealObjectHandle actor,
            IReadOnlyList<StateBinding> bindings)
        {
            _script = script;
            _bridge = bridge;
            _handle = handle;
            _actor = actor;
            _bindings = bindings;
        }

        public void Hydrate()
        {
            foreach (StateBinding binding in _bindings)
            {
                binding.Hydrate(_script, _bridge, _handle);
            }
        }

        public void Flush()
        {
            bool authority = !_bindings.Any(b => b.Replicated) ||
                _bridge.Invoke<bool>(_actor, "/Script/Engine.Actor:HasAuthority", Array.Empty<object?>());
            foreach (StateBinding binding in _bindings)
            {
                if (binding.Replicated && !authority) binding.Hydrate(_script, _bridge, _handle);
                else binding.Flush(_script, _bridge, _handle);
            }
        }
    }

    private sealed record StateContract(
        UnrealScriptClassKind Kind,
        IReadOnlyList<StateBinding> Bindings)
    {
        public static StateContract Create(Type scriptType)
        {
            UnrealScriptClassAttribute? classAttribute = scriptType
                .GetCustomAttribute<UnrealScriptClassAttribute>(inherit: false);
            if (classAttribute is null ||
                string.IsNullOrWhiteSpace(classAttribute.GeneratedClassPath))
            {
                return new StateContract(default, []);
            }
            if (classAttribute.Kind == UnrealScriptClassKind.BlueprintFunctionLibrary)
            {
                return new StateContract(classAttribute.Kind, []);
            }

            string generatedClassPath = classAttribute.GeneratedClassPath.Trim();
            if (!generatedClassPath.StartsWith("/Game/", StringComparison.Ordinal) ||
                !generatedClassPath.EndsWith("_C", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Script Class '{scriptType.FullName}' has invalid generated class path " +
                    $"'{classAttribute.GeneratedClassPath}'.");
            }

            StringComparer stateNameComparer = classAttribute is not null
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal;
            var selectedProperties = new Dictionary<string, (Type Owner, PropertyInfo Property, global::UnifiedScript.ExposeAttribute? Exposure)>(stateNameComparer);
            var bindings = new List<StateBinding>();
            foreach (Type owner in EnumerateTypeHierarchy(scriptType))
            {
                bool ownsAllProperties = owner
                    .GetCustomAttribute<global::UnifiedScript.ScriptAssetAttribute>(inherit: false) is not null;
                foreach (MemberInfo member in owner
                             .GetMembers(
                                 BindingFlags.Instance |
                                 BindingFlags.Public |
                                 BindingFlags.NonPublic |
                                 BindingFlags.DeclaredOnly)
                             .OrderBy(static member => member.Name, StringComparer.Ordinal))
                {
                    PropertyInfo? property = member as PropertyInfo;
                    global::UnifiedScript.ExposeAttribute? exposure = member
                        .GetCustomAttribute<global::UnifiedScript.ExposeAttribute>(inherit: false);
                    bool isStatic = property is not null &&
                        (property.GetMethod?.IsStatic == true || property.SetMethod?.IsStatic == true);
                    bool isCandidate =
                        (property is not null && !isStatic && ownsAllProperties) ||
                        exposure is not null;
                    if (!isCandidate)
                    {
                        if (property is not null && !isStatic && selectedProperties.ContainsKey(property.Name))
                        {
                            throw new InvalidOperationException(
                                $"Script state '{scriptType.FullName}.{member.Name}' hides an inherited state property; state ownership is ambiguous.");
                        }
                        continue;
                    }

                    if (property is null || isStatic)
                    {
                        throw new InvalidOperationException(
                            $"Script state '{scriptType.FullName}.{member.Name}' must be a non-static, readable, writable instance property.");
                    }

                    if (selectedProperties.ContainsKey(property.Name))
                    {
                        throw new InvalidOperationException(
                            $"Script state '{scriptType.FullName}.{property.Name}' conflicts with an inherited state property; state ownership is ambiguous.");
                    }

                    selectedProperties[property.Name] = (owner, property, exposure);
                }
            }

            foreach ((Type owner, PropertyInfo property, global::UnifiedScript.ExposeAttribute? exposure) in selectedProperties.Values)
            {
                if (property.GetIndexParameters().Length != 0 ||
                    property.GetMethod is null || property.SetMethod is null ||
                    property.GetMethod.IsStatic || property.SetMethod.IsStatic)
                {
                    throw new InvalidOperationException(
                        $"Script state '{scriptType.FullName}.{property.Name}' must be a readable, writable instance property.");
                }

                string memberPath = $"{generatedClassPath}:{property.Name}";
                StateBinding binding = StateBinding.Create(property, memberPath);
                binding.Replicated = exposure?.Replicated == true;
                bindings.Add(binding);
            }
            return new StateContract(classAttribute!.Kind, bindings);
        }

        private static IEnumerable<Type> EnumerateTypeHierarchy(Type type)
        {
            var chain = new List<Type>();
            for (Type? current = type; current is not null; current = current.BaseType)
            {
                chain.Add(current);
            }

            chain.Reverse();
            return chain;
        }
    }

    internal abstract class StateBinding(string memberPath)
    {
        public bool Replicated { get; internal set; }
        protected string MemberPath { get; } = memberPath;

        public abstract void Hydrate(
            UnrealObjectScript script,
            IUnrealInvocationBridge bridge,
            UnrealObjectHandle handle);

        public abstract void Flush(
            UnrealObjectScript script,
            IUnrealInvocationBridge bridge,
            UnrealObjectHandle handle);

        public static StateBinding Create(PropertyInfo property, string memberPath)
        {
            Type type = property.PropertyType;
            if (type == typeof(bool))
            {
                return StateBinding<bool>.CreateTyped(property, memberPath);
            }
            if (type == typeof(byte))
            {
                return StateBinding<byte>.CreateTyped(property, memberPath);
            }
            if (type == typeof(int))
            {
                return StateBinding<int>.CreateTyped(property, memberPath);
            }
            if (type == typeof(long))
            {
                return StateBinding<long>.CreateTyped(property, memberPath);
            }
            if (type == typeof(float))
            {
                return StateBinding<float>.CreateTyped(property, memberPath);
            }
            if (type == typeof(double))
            {
                return StateBinding<double>.CreateTyped(property, memberPath);
            }
            if (type == typeof(string))
            {
                return StringStateBinding.CreateTyped(property, memberPath);
            }
            if (type == typeof(UnrealName))
            {
                return StateBinding<UnrealName>.CreateTyped(property, memberPath);
            }
            if (type == typeof(UnrealText))
            {
                return StateBinding<UnrealText>.CreateTyped(property, memberPath);
            }
            if (type == typeof(UnrealObjectHandle))
            {
                return StateBinding<UnrealObjectHandle>.CreateTyped(property, memberPath);
            }
            if (type == typeof(UnrealClassHandle))
            {
                return StateBinding<UnrealClassHandle>.CreateTyped(property, memberPath);
            }
            if (type == typeof(UnrealSoftObjectHandle))
            {
                return StateBinding<UnrealSoftObjectHandle>.CreateTyped(property, memberPath);
            }
            if (type == typeof(UnrealSoftClassHandle))
            {
                return StateBinding<UnrealSoftClassHandle>.CreateTyped(property, memberPath);
            }
            if (type == typeof(UnrealVector))
            {
                return StateBinding<UnrealVector>.CreateTyped(property, memberPath);
            }
            if (type == typeof(UnrealRotator))
            {
                return StateBinding<UnrealRotator>.CreateTyped(property, memberPath);
            }
            if (type == typeof(UnrealTransform))
            {
                return StateBinding<UnrealTransform>.CreateTyped(property, memberPath);
            }
            bool isCollection = type.IsGenericType &&
                (type.GetGenericTypeDefinition() == typeof(IReadOnlyList<>) ||
                 type.GetGenericTypeDefinition() == typeof(IReadOnlySet<>) ||
                 type.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>));
            if (isCollection)
            {
                Type bindingType = typeof(ReferenceStateBinding<>).MakeGenericType(type);
                MethodInfo? factory = bindingType.GetMethod(
                    "CreateTyped",
                    BindingFlags.Public | BindingFlags.Static);
                if (factory?.Invoke(null, [property, memberPath]) is StateBinding binding)
                {
                    return binding;
                }
            }
            if (type.IsValueType &&
                type.GetCustomAttribute<UnrealEnumAttribute>(inherit: false) is not null &&
                typeof(IUnrealEnumValue).IsAssignableFrom(type))
            {
                Type bindingType = typeof(StateBinding<>).MakeGenericType(type);
                MethodInfo? factory = bindingType.GetMethod(
                    "CreateTyped",
                    BindingFlags.Public | BindingFlags.Static);
                if (factory?.Invoke(null, [property, memberPath]) is StateBinding binding)
                {
                    return binding;
                }
            }

            throw new NotSupportedException(
                $"Script state '{property.DeclaringType?.FullName}.{property.Name}' uses " +
                $"'{type.FullName}', which is not in the reviewed Blueprint state matrix.");
        }
    }

    private sealed class StateBinding<T>(
        string memberPath,
        Func<UnrealObjectScript, T> getter,
        Action<UnrealObjectScript, T> setter)
        : StateBinding(memberPath)
        where T : struct
    {
        public override void Hydrate(
            UnrealObjectScript script,
            IUnrealInvocationBridge bridge,
            UnrealObjectHandle handle) =>
            setter(script, bridge.ReadProperty<T>(handle, MemberPath));

        public override void Flush(
            UnrealObjectScript script,
            IUnrealInvocationBridge bridge,
            UnrealObjectHandle handle) =>
            bridge.WriteProperty(handle, MemberPath, getter(script));

        public static StateBinding<T> CreateTyped(PropertyInfo property, string memberPath) =>
            new(
                memberPath,
                CompileGetter<T>(property),
                CompileSetter<T>(property));
    }

    private sealed class StringStateBinding(
        string memberPath,
        Func<UnrealObjectScript, string> getter,
        Action<UnrealObjectScript, string> setter)
        : StateBinding(memberPath)
    {
        public override void Hydrate(
            UnrealObjectScript script,
            IUnrealInvocationBridge bridge,
            UnrealObjectHandle handle) =>
            setter(script, bridge.ReadProperty<string>(handle, MemberPath));

        public override void Flush(
            UnrealObjectScript script,
            IUnrealInvocationBridge bridge,
            UnrealObjectHandle handle) =>
            bridge.WriteProperty(handle, MemberPath, getter(script) ?? string.Empty);

        public static StringStateBinding CreateTyped(PropertyInfo property, string memberPath) =>
            new(
                memberPath,
                CompileGetter<string>(property),
                CompileSetter<string>(property));
    }

    private sealed class ReferenceStateBinding<T>(
        string memberPath,
        Func<UnrealObjectScript, T> getter,
        Action<UnrealObjectScript, T> setter)
        : StateBinding(memberPath)
    {
        public override void Hydrate(
            UnrealObjectScript script,
            IUnrealInvocationBridge bridge,
            UnrealObjectHandle handle) =>
            setter(script, bridge.ReadProperty<T>(handle, MemberPath));

        public override void Flush(
            UnrealObjectScript script,
            IUnrealInvocationBridge bridge,
            UnrealObjectHandle handle) =>
            bridge.WriteProperty(handle, MemberPath, getter(script));

        public static ReferenceStateBinding<T> CreateTyped(PropertyInfo property, string memberPath) =>
            new(
                memberPath,
                CompileGetter<T>(property),
                CompileSetter<T>(property));
    }

    private static Func<UnrealObjectScript, T> CompileGetter<T>(PropertyInfo property)
    {
        ParameterExpression script = Expression.Parameter(typeof(UnrealObjectScript), "script");
        UnaryExpression instance = Expression.Convert(
            script,
            property.DeclaringType ?? throw new InvalidOperationException(
                $"Script state '{property.Name}' has no declaring type."));
        return Expression.Lambda<Func<UnrealObjectScript, T>>(
            Expression.Property(instance, property),
            script).Compile();
    }

    private static Action<UnrealObjectScript, T> CompileSetter<T>(PropertyInfo property)
    {
        ParameterExpression script = Expression.Parameter(typeof(UnrealObjectScript), "script");
        ParameterExpression value = Expression.Parameter(typeof(T), "value");
        UnaryExpression instance = Expression.Convert(
            script,
            property.DeclaringType ?? throw new InvalidOperationException(
                $"Script state '{property.Name}' has no declaring type."));
        return Expression.Lambda<Action<UnrealObjectScript, T>>(
            Expression.Assign(Expression.Property(instance, property), value),
            script,
            value).Compile();
    }
}
