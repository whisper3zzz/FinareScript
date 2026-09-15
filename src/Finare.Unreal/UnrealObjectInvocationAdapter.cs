using System.Collections;
using System.Reflection;
using System.Runtime.ExceptionServices;

namespace Finare.Unreal;

/// <summary>Generated transport shape for a struct containing UObject views.</summary>
[AttributeUsage(AttributeTargets.Struct, Inherited = false)]
public sealed class UnrealWireTypeAttribute(Type type) : Attribute
{
    public Type Type { get; } = type;
}

internal static class UnrealObjectProjection<T>
{
    internal static readonly bool Required = UnrealObjectInvocationAdapter.ContainsObjectView(typeof(T));
}

/// <summary>
/// Converts typed views at the public binding boundary. The native value codec
/// still receives handles; it never extracts a handle without a World/lease check.
/// All recursive conversions carry the accessing bridge explicitly.
/// </summary>
internal static class UnrealObjectInvocationAdapter
{
    private static readonly MethodInfo Read = typeof(IUnrealInvocationBridge).GetMethod("ReadProperty")!;
    private static readonly MethodInfo Write = typeof(IUnrealInvocationBridge).GetMethod("WriteProperty")!;
    private static readonly MethodInfo FastRead = typeof(IUnrealFastInvocationBridge).GetMethod("ReadProperty")!;
    private static readonly MethodInfo FastWrite = typeof(IUnrealFastInvocationBridge).GetMethod("WriteProperty")!;
    private static readonly MethodInfo Wrap = typeof(UnrealObject).GetMethod("Wrap")!;

    internal static T ReadProperty<T>(UnrealObject owner, ulong id, string path, bool fast)
    {
        owner.EnsureAccessible();
        if (!UnrealObjectProjection<T>.Required)
            return fast && owner.InvocationBridge is IUnrealFastInvocationBridge f && f.SupportsFastInvocation
                ? f.ReadProperty<T>(owner.Handle, id, path)
                : owner.InvocationBridge.ReadProperty<T>(owner.Handle, path);
        bool useFast = fast && owner.InvocationBridge is IUnrealFastInvocationBridge { SupportsFastInvocation: true };
        object? value = InvokeReflected((useFast ? FastRead : Read).MakeGenericMethod(WireType(typeof(T))),
            owner.InvocationBridge, useFast ? [owner.Handle, id, path] : [owner.Handle, path]);
        return (T)Convert(value, typeof(T), owner.InvocationBridge, fromWire: true)!;
    }

    internal static void WriteProperty<T>(UnrealObject owner, ulong id, string path, T value, bool fast)
    {
        owner.EnsureAccessible();
        if (!UnrealObjectProjection<T>.Required)
        {
            if (fast && owner.InvocationBridge is IUnrealFastInvocationBridge f && f.SupportsFastInvocation)
                f.WriteProperty(owner.Handle, id, path, value);
            else owner.InvocationBridge.WriteProperty(owner.Handle, path, value);
            return;
        }
        object? wire = Convert(value, typeof(T), owner.InvocationBridge, fromWire: false);
        bool useFast = fast && owner.InvocationBridge is IUnrealFastInvocationBridge { SupportsFastInvocation: true };
        InvokeReflected((useFast ? FastWrite : Write).MakeGenericMethod(WireType(typeof(T))), owner.InvocationBridge,
            useFast ? [owner.Handle, id, path, wire] : [owner.Handle, path, wire]);
    }

    internal static T Invoke<T>(UnrealObject owner, ulong id, string path, IReadOnlyList<object?> args, bool fast)
    {
        owner.EnsureAccessible();
        if (!UnrealObjectProjection<T>.Required && !args.Any(a => a is not null && ContainsObjectView(a.GetType())))
            return fast && owner.InvocationBridge is IUnrealFastInvocationBridge f && f.SupportsFastInvocation
                ? f.Invoke<T>(owner.Handle, id, path, args)
                : owner.InvocationBridge.Invoke<T>(owner.Handle, path, args);
        return (T)InvokeWithOutputs(owner, id, path, typeof(T), args.Select(a =>
            new UnrealOutputArgument(a?.GetType() ?? typeof(UnrealObject), UnrealParameterDirection.Input, a)).ToArray()).ReturnValue!;
    }

    internal static void InvokeVoid(UnrealObject owner, ulong id, string path, IReadOnlyList<object?> args, bool fast)
    {
        owner.EnsureAccessible();
        if (args.Any(a => a is not null && ContainsObjectView(a.GetType())))
        {
            InvokeWithOutputs(owner, id, path, typeof(void), args.Select(a =>
                new UnrealOutputArgument(a?.GetType() ?? typeof(UnrealObject), UnrealParameterDirection.Input, a)).ToArray());
            return;
        }
        if (fast && owner.InvocationBridge is IUnrealFastInvocationBridge f && f.SupportsFastInvocation)
            f.Invoke(owner.Handle, id, path, args);
        else owner.InvocationBridge.Invoke(owner.Handle, path, args);
    }

    internal static UnrealOutputInvocationResult InvokeWithOutputs(
        UnrealObject owner, ulong id, string path, Type result, IReadOnlyList<UnrealOutputArgument> args)
    {
        owner.EnsureAccessible();
        if (owner.InvocationBridge is not IUnrealOutputInvocationBridge bridge)
            throw new NotSupportedException("Typed object calls require the reflected output invocation bridge.");
        if (!ContainsObjectView(result) && !args.Any(a => ContainsObjectView(a.Type)))
            return bridge.InvokeWithOutputs(owner.Handle, id, path, result, args);
        var wireArgs = args.Select(a => new UnrealOutputArgument(WireType(a.Type), a.Direction,
            a.Direction == UnrealParameterDirection.Output ? null : Convert(a.Value, a.Type, owner.InvocationBridge, false))).ToArray();
        UnrealOutputInvocationResult response = bridge.InvokeWithOutputs(owner.Handle, id, path, WireType(result), wireArgs);
        var outputs = args.Where(a => a.Direction != UnrealParameterDirection.Input).ToArray();
        if (response.Outputs.Count != outputs.Length)
            throw new InvalidOperationException("The native output count does not match the typed call signature.");
        // Complete every conversion before the generated caller assigns any ref/out slot.
        return new UnrealOutputInvocationResult(Convert(response.ReturnValue, result, owner.InvocationBridge, true),
            outputs.Select((a, i) => Convert(response.Outputs[i], a.Type, owner.InvocationBridge, true)).ToArray());
    }

    internal static bool ContainsObjectView(Type type)
    {
        if (typeof(UnrealObject).IsAssignableFrom(type)) return true;
        if (type.GetCustomAttribute<UnrealWireTypeAttribute>() is not null) return true;
        return Container(type) is { } shape && shape.Arguments.Any(ContainsObjectView);
    }

    internal static Type WireType(Type type)
    {
        if (typeof(UnrealObject).IsAssignableFrom(type)) return typeof(UnrealObjectHandle);
        if (type.GetCustomAttribute<UnrealWireTypeAttribute>() is { } wire) return wire.Type;
        if (Container(type) is { } shape)
            return shape.Definition.MakeGenericType(shape.Arguments.Select(WireType).ToArray());
        return type;
    }

    internal static bool TryCreateContractIdentity(Type type, out string identity)
    {
        if (!UnrealNativeValueCodecV3.TryCreateContractIdentity(WireType(type), out identity)) return false;
        if (!ContainsObjectView(type)) return true;
        var projection = new System.Text.StringBuilder(identity);
        AppendProjectionIdentity(type, projection);
        identity = projection.ToString();
        return true;
    }

    private static void AppendProjectionIdentity(Type type, System.Text.StringBuilder identity)
    {
        if (typeof(UnrealObject).IsAssignableFrom(type))
        {
            UnrealTypeAttribute? native = type.GetCustomAttribute<UnrealTypeAttribute>();
            string path = native?.Path ?? "/Script/CoreUObject.Object";
            identity.Append(native?.IsInterface == true ? "interface:" : "object:")
                .Append(path.Length).Append(':').Append(path).Append(';');
        }
        else if (type.GetCustomAttribute<UnrealWireTypeAttribute>() is not null)
        {
            identity.Append("struct{");
            foreach (PropertyInfo field in Fields(type))
            {
                identity.Append(field.GetCustomAttribute<UnrealStructFieldAttribute>()!.Index).Append(':');
                AppendProjectionIdentity(field.PropertyType, identity);
                identity.Append(';');
            }
            identity.Append('}');
        }
        else if (Container(type) is { } shape)
        {
            identity.Append('[');
            foreach (Type argument in shape.Arguments)
            {
                AppendProjectionIdentity(argument, identity);
                identity.Append(';');
            }
            identity.Append(']');
        }
    }

    internal static object? Convert(object? value, Type type, IUnrealInvocationBridge bridge, bool fromWire)
    {
        if (typeof(UnrealObject).IsAssignableFrom(type))
        {
            if (fromWire)
            {
                if (value is not UnrealObjectHandle handle)
                    throw new InvalidOperationException("A typed UObject result requires a native object handle.");
                return InvokeReflected(Wrap.MakeGenericMethod(type), null, [handle, bridge]);
            }
            if (value is null) return default(UnrealObjectHandle);
            if (value is not UnrealObject view || !type.IsInstanceOfType(view))
                throw new ArgumentException($"Expected an object view assignable to {type}.");
            if (!UnrealObject.AreCompatible(view, bridge))
                throw new InvalidOperationException("A UObject from another World cannot be passed to this call.");
            return view.Handle;
        }
        if (!ContainsObjectView(type)) return value;
        if (type.GetCustomAttribute<UnrealWireTypeAttribute>() is { } wire)
        {
            if (value is null) throw new ArgumentNullException(nameof(value));
            PropertyInfo[] fields = Fields(type);
            PropertyInfo[] wireFields = Fields(wire.Type);
            if (fields.Length != wireFields.Length) throw new InvalidOperationException("Generated struct transport shape differs.");
            object?[] values = fields.Select((f, i) => Convert(
                (fromWire ? wireFields[i] : f).GetValue(value), f.PropertyType, bridge, fromWire)).ToArray();
            return Activator.CreateInstance(fromWire ? type : wire.Type, values);
        }
        if (Container(type) is not { } shape || value is not IEnumerable items)
            throw new ArgumentException($"Expected a non-null {type} container.");
        Type[] destinationTypes = shape.Arguments.Select(t => fromWire ? t : WireType(t)).ToArray();
        if (shape.Definition == typeof(IReadOnlyDictionary<,>))
        {
            var result = (IDictionary)Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(destinationTypes))!;
            foreach (object item in items)
            {
                Type pair = item.GetType();
                object? key = Convert(pair.GetProperty("Key")!.GetValue(item), shape.Arguments[0], bridge, fromWire);
                object? mapped = Convert(pair.GetProperty("Value")!.GetValue(item), shape.Arguments[1], bridge, fromWire);
                if (key is null) throw new NotSupportedException("A null UObject map key cannot be represented by a managed dictionary.");
                result.Add(key, mapped);
            }
            return result;
        }
        var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(destinationTypes))!;
        foreach (object? item in items) list.Add(Convert(item, shape.Arguments[0], bridge, fromWire));
        return shape.Definition == typeof(IReadOnlySet<>)
            ? Activator.CreateInstance(typeof(HashSet<>).MakeGenericType(destinationTypes), list)!
            : list;
    }

    private static PropertyInfo[] Fields(Type type) => type.GetProperties()
        .Where(p => p.GetCustomAttribute<UnrealStructFieldAttribute>() is not null)
        .OrderBy(p => p.GetCustomAttribute<UnrealStructFieldAttribute>()!.Index).ToArray();

    private static (Type Definition, Type[] Arguments)? Container(Type type)
    {
        if (type.IsArray) return (typeof(IReadOnlyList<>), [type.GetElementType()!]);
        Type? contract = new[] { type }.Concat(type.GetInterfaces()).FirstOrDefault(t =>
            t.IsGenericType && t.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>))
            ?? new[] { type }.Concat(type.GetInterfaces()).FirstOrDefault(t =>
                t.IsGenericType && t.GetGenericTypeDefinition() == typeof(IReadOnlySet<>))
            ?? new[] { type }.Concat(type.GetInterfaces()).FirstOrDefault(t =>
                t.IsGenericType && t.GetGenericTypeDefinition() == typeof(IReadOnlyList<>));
        return contract is null ? null : (contract.GetGenericTypeDefinition(), contract.GetGenericArguments());
    }

    internal static object? InvokeReflected(MethodInfo method, object? target, object?[] arguments)
    {
        try { return method.Invoke(target, arguments); }
        catch (TargetInvocationException e) when (e.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(e.InnerException).Throw();
            throw;
        }
    }
}
