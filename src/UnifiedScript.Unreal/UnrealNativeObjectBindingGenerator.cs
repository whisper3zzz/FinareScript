using System.Text;
using System.Text.Json;

namespace UnifiedScript.Unreal;

internal static class UnrealNativeObjectBindingGenerator
{
    private const string Runtime = "global::UnifiedScript.Unreal.";
    internal static void Emit(StringBuilder source, UnrealReflectedType type, string name,
        IReadOnlyDictionary<string, string> names, ICollection<UnrealBindingDiagnostic> diagnostics)
    {
        source.AppendLine($"[{Runtime}UnrealNativeObjectTypeAttribute({JsonSerializer.Serialize(type.Path)})]");
        source.AppendLine($"public sealed partial class {name} : {Runtime}UnrealNativeObject");
        source.AppendLine("{");
        source.AppendLine($"    internal {name}({Runtime}UnrealNativeObjectLease lease) : base(lease) {{ }}");
        foreach (var field in type.Properties)
        {
            if (UnrealNativeObjectBindings.Unsupported(field) is { } reason)
            {
                diagnostics.Add(new("USUE5410", reason, field.Path));
                continue;
            }
            string managed = UnrealBindingGenerator.GetManagedType(field.Type, names)!;
            source.AppendLine($"    public {managed} @{field.Name}");
            source.AppendLine("    {");
            if (field.Access.Readable)
                source.AppendLine($"        get => {Runtime}UnrealNativeObjectValues.Decode<{managed}>(Lease.Invoke({Id(field.Path + "#get")}, []).Value);");
            if (field.Access.Writable)
                source.AppendLine($"        set => Lease.Invoke({Id(field.Path + "#set")}, [{Runtime}UnrealNativeObjectValues.Encode(value)]);");
            source.AppendLine("    }");
        }
        foreach (var function in type.Functions)
        {
            if (UnrealNativeObjectBindings.Unsupported(function) is { } reason)
            {
                diagnostics.Add(new("USUE5410", reason, function.Path));
                continue;
            }
            var parameters = function.Parameters.Select((p, i) =>
                $"{UnrealBindingGenerator.GetManagedType(p.Type, names)}{(UnrealNativeObjectBindings.AllowsNull(function.NativeBinding!.Parameters[i]) ? "?" : "")} @{p.Name}").ToList();
            string bridgeName = UniqueName(function, "__bridge"), scopeName = UniqueName(function, "__scope");
            if (function.Traits.Static) parameters.Insert(0, $"{Runtime}IUnrealInvocationBridge {bridgeName}");
            string argumentScope = function.Traits.Static ? $"{Runtime}UnrealNativeObjectScope.FromBridge({bridgeName})" : "Lease";
            string arguments = "[" + string.Join(", ", function.Parameters.Select((p, i) => p.Type.Kind == UnrealValueKind.NativeObject
                ? $"{argumentScope}.EncodeObject(@{p.Name}, {Id(p.Type.TypePath!)}, {(UnrealNativeObjectBindings.AllowsNull(function.NativeBinding!.Parameters[i]) ? "true" : "false")})"
                : $"{Runtime}UnrealNativeObjectValues.Encode(@{p.Name})")) + "]";
            string returnType = UnrealBindingGenerator.GetManagedType(function.ReturnType, names)!;
            if (function.NativeBinding!.Operation == UnrealNativeOperation.Constructor)
            {
                source.AppendLine($"    public {name}({string.Join(", ", parameters)})");
                source.AppendLine($"        : base({Runtime}UnrealNativeObjectScope.FromBridge({bridgeName}).Construct({Id(function.Path)}, {arguments})) {{ }}");
            }
            source.AppendLine($"    public {(function.Traits.Static ? "static " : "")}{returnType} @{function.Name}({string.Join(", ", parameters)})");
            source.AppendLine("    {");
            if (function.Traits.Static) source.AppendLine($"        var {scopeName} = {Runtime}UnrealNativeObjectScope.FromBridge({bridgeName});");
            if (function.ReturnType.Kind == UnrealValueKind.NativeObject)
            {
                string acquire = function.Traits.Static ? scopeName + ".Construct" : "Lease.Acquire";
                source.AppendLine($"        return new {returnType}({acquire}({Id(function.Path)}, {arguments}));");
            }
            else
            {
                string invoke = function.Traits.Static ? $"{scopeName}.Invoke(default, {Id(function.Path)}, {arguments})" : $"Lease.Invoke({Id(function.Path)}, {arguments})";
                source.AppendLine(function.ReturnType.Kind == UnrealValueKind.Void
                    ? $"        {invoke};"
                    : $"        return {Runtime}UnrealNativeObjectValues.Decode<{returnType}>({invoke}.Value);");
            }
            source.AppendLine("    }");
        }
        source.AppendLine("}");
        source.AppendLine();
    }
    private static string Id(string path) => $"0x{UnrealMemberId.FromPath(path):X16}UL";
    private static string UniqueName(UnrealReflectedFunction function, string name)
    {
        while (function.Parameters.Any(p => p.Name == name)) name += "_";
        return name;
    }
}
