using System.Linq.Expressions;
using System.Reflection;

namespace Finare.Unreal;

/// <summary>Reverse invocation uses the same value projection as forward calls.</summary>
internal static class UnrealObjectDelegateAdapter
{
    internal static Delegate Adapt(Delegate handler, IUnrealInvocationBridge bridge)
    {
        MethodInfo invoke = handler.GetType().GetMethod("Invoke")!;
        ParameterInfo[] parameters = invoke.GetParameters();
        Type[] types = parameters.Select(p => p.ParameterType.IsByRef
            ? p.ParameterType.GetElementType()! : p.ParameterType).ToArray();
        if (!types.Append(invoke.ReturnType).Any(UnrealObjectInvocationAdapter.ContainsObjectView)) return handler;

        ParameterExpression[] wireParameters = parameters.Select((p, i) =>
        {
            Type type = UnrealObjectInvocationAdapter.WireType(types[i]);
            return Expression.Parameter(p.ParameterType.IsByRef ? type.MakeByRefType() : type, p.Name);
        }).ToArray();
        var outputs = Expression.Variable(typeof(object[]), "outputs");
        var inputs = Expression.NewArrayInit(typeof(object), wireParameters.Select((p, i) =>
            parameters[i].IsOut ? (Expression)Expression.Constant(null, typeof(object)) : Expression.Convert(p, typeof(object))));
        Func<object?[], object?[]> dispatch = values =>
        {
            var args = types.Select((t, i) => parameters[i].IsOut
                ? (t.IsValueType ? Activator.CreateInstance(t) : null)
                : UnrealObjectInvocationAdapter.Convert(values[i], t, bridge, true)).ToArray();
            object? result = UnrealObjectInvocationAdapter.InvokeReflected(invoke, handler, args);
            // Validate the complete return and output set before mutating native slots.
            var projected = new object?[args.Length + 1];
            projected[0] = UnrealObjectInvocationAdapter.Convert(result, invoke.ReturnType, bridge, false);
            for (int i = 0; i < args.Length; i++)
                if (parameters[i].ParameterType.IsByRef)
                    projected[i + 1] = UnrealObjectInvocationAdapter.Convert(args[i], types[i], bridge, false);
            return projected;
        };
        var body = new List<Expression> { Expression.Assign(outputs, Expression.Invoke(Expression.Constant(dispatch), inputs)) };
        for (int i = 0; i < parameters.Length; i++)
            if (parameters[i].ParameterType.IsByRef)
                body.Add(Expression.Assign(wireParameters[i], Expression.Convert(
                    Expression.ArrayIndex(outputs, Expression.Constant(i + 1)),
                    UnrealObjectInvocationAdapter.WireType(types[i]))));
        body.Add(invoke.ReturnType == typeof(void) ? Expression.Empty() : Expression.Convert(
            Expression.ArrayIndex(outputs, Expression.Constant(0)), UnrealObjectInvocationAdapter.WireType(invoke.ReturnType)));
        return Expression.Lambda(Expression.Block([outputs], body), wireParameters).Compile();
    }
}
