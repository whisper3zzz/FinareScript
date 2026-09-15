using System.Text;
using System.Text.Json;

namespace Finare.Unreal;

internal static class UnrealNativeObjectBindingGenerator
{
    private const string Runtime = "global::Finare.Unreal.";

    internal static void Emit(StringBuilder source, UnrealReflectedType type, string name,
        IReadOnlyDictionary<string, string> names, ICollection<UnrealBindingDiagnostic> diagnostics)
    {
        source.AppendLine($"[{Runtime}UnrealNativeObjectTypeAttribute({JsonSerializer.Serialize(type.Path)})]");
        source.AppendLine($"public partial class {name} : {(type.BaseTypePath is null ? Runtime + "UnrealNativeObject" : names[type.BaseTypePath])}");
        source.AppendLine("{");
        source.AppendLine($"    protected internal {name}({Runtime}UnrealNativeObjectLease lease) : base(lease) {{ }}");
        var emittedSignatures = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in type.Properties)
        {
            if (UnrealNativeObjectBindings.Unsupported(field) is { } reason)
            {
                diagnostics.Add(new("USUE5410", reason, field.Path));
                continue;
            }
            string managed = UnrealBindingGenerator.GetManagedType(field.Type, names)!;
            if (UnrealNativeObjectBindings.IsMemory(field.Type))
            {
                if (field.Access.Readable)
                    source.AppendLine($"    public {Runtime}UnrealNativeBuffer<{UnrealBindingGenerator.GetManagedType(field.Type.ElementType!, names)}> @{field.Name} => new(Lease, {Id(field.Path + "#memory")}, {(field.Access.Writable ? "true" : "false")});");
                continue;
            }
            bool text = UnrealNativeObjectBindings.IsText(field.Type);
            source.AppendLine($"    public {managed} @{field.Name}");
            source.AppendLine("    {");
            if (field.Access.Readable)
            {
                if (text)
                {
                    source.AppendLine("        get");
                    source.AppendLine("        {");
                    source.AppendLine($"            var __text = new {Runtime}UnrealNativeObjectTextArguments();");
                    source.AppendLine($"            using var __call = Lease.BeginText({Id(field.Path + "#get")}, __text.ToValues(), __text.ToBytes());");
                    source.AppendLine("            return __call.Read().DecodeString();");
                    source.AppendLine("        }");
                }
                else source.AppendLine($"        get => {Runtime}UnrealNativeObjectValues.Decode<{managed}>(Lease.Invoke({Id(field.Path + "#get")}, []).Value);");
            }
            if (field.Access.Writable)
            {
                if (text)
                {
                    source.AppendLine("        set");
                    source.AppendLine("        {");
                    source.AppendLine($"            var __text = new {Runtime}UnrealNativeObjectTextArguments();");
                    source.AppendLine("            __text.AddValue(__text.Add(value));");
                    source.AppendLine($"            using var __call = Lease.BeginText({Id(field.Path + "#set")}, __text.ToValues(), __text.ToBytes());");
                    source.AppendLine("            _ = __call.Read();");
                    source.AppendLine("        }");
                }
                else source.AppendLine($"        set => Lease.Invoke({Id(field.Path + "#set")}, [{Runtime}UnrealNativeObjectValues.Encode(value)]);");
            }
            source.AppendLine("    }");
        }
        foreach (var function in type.Functions)
        {
            if (UnrealNativeObjectBindings.Unsupported(function) is { } reason)
            {
                diagnostics.Add(new("USUE5410", reason, function.Path));
                continue;
            }
            string signatureKey = (function.NativeBinding!.Operation == UnrealNativeOperation.Constructor ? "$constructor" : function.Name) +
                "(" + string.Join(",", function.Parameters.Select(p => UnrealBindingGenerator.GetManagedType(p.Type, names))) + ")";
            if (!emittedSignatures.Add(signatureKey))
            {
                diagnostics.Add(new("USUE5414", $"Native-object overload '{function.Path}' collides after managed projection; exact FString and std::string overloads require distinct C# identities.", function.Path));
                continue;
            }
            bool text = UnrealNativeObjectBindings.IsText(function.ReturnType) || function.Parameters.Any(p => UnrealNativeObjectBindings.IsText(p.Type));
            var parameters = function.Parameters.Select((p, i) =>
                $"{UnrealBindingGenerator.GetManagedType(p.Type, names)}{(UnrealNativeObjectBindings.AllowsNull(function.NativeBinding!.Parameters[i]) ? "?" : "")} @{p.Name}").ToList();
            string bridgeName = UniqueName(function, "__bridge"), scopeName = UniqueName(function, "__scope");
            if (function.Traits.Static) parameters.Insert(0, $"{Runtime}IUnrealInvocationBridge {bridgeName}");
            string argumentScope = function.Traits.Static ? $"{Runtime}UnrealNativeObjectScope.FromBridge({bridgeName})" : "Lease";
            string returnType = UnrealBindingGenerator.GetManagedType(function.ReturnType, names)!;
            if (text && function.NativeBinding!.Operation == UnrealNativeOperation.Constructor)
            {
                string helper = UniqueName(function, "__BuildTextArguments");
                string helperText = UniqueName(function, "__text");
                source.AppendLine($"    private static {Runtime}UnrealNativeObjectTextArguments {helper}({string.Join(", ", parameters)})");
                source.AppendLine("    {");
                source.AppendLine($"        var {helperText} = new {Runtime}UnrealNativeObjectTextArguments();");
                EmitArguments(source, function, argumentScope, helperText);
                source.AppendLine($"        return {helperText};");
                source.AppendLine("    }");
                source.AppendLine($"    public {name}({string.Join(", ", parameters)})");
                source.AppendLine($"        : base({Runtime}UnrealNativeObjectScope.FromBridge({bridgeName}).ConstructText({Id(function.Path)}, {helper}({string.Join(", ", parameters.Select(ParameterName))}))) {{ }}");
            }
            else if (function.NativeBinding!.Operation == UnrealNativeOperation.Constructor)
            {
                string arguments = "[" + string.Join(", ", function.Parameters.Select((p, i) => ArgumentExpression(p, i, argumentScope, function))) + "]";
                source.AppendLine($"    public {name}({string.Join(", ", parameters)})");
                source.AppendLine($"        : base({Runtime}UnrealNativeObjectScope.FromBridge({bridgeName}).Construct({Id(function.Path)}, {arguments})) {{ }}");
            }
            source.AppendLine($"    public {(function.Traits.Static ? "static " : "")}{returnType} @{function.Name}({string.Join(", ", parameters)})");
            source.AppendLine("    {");
            if (function.Traits.Static) source.AppendLine($"        var {scopeName} = {Runtime}UnrealNativeObjectScope.FromBridge({bridgeName});");
            if (text)
            {
                string textName = UniqueName(function, "__text");
                string callName = UniqueName(function, "__call");
                string resultName = UniqueName(function, "__result");
                source.AppendLine($"        var {textName} = new {Runtime}UnrealNativeObjectTextArguments();");
                EmitArguments(source, function, argumentScope, textName);
                if (function.ReturnType.Kind == UnrealValueKind.NativeObject)
                {
                    string acquire = function.Traits.Static
                        ? $"{scopeName}.AcquireText(default, {Id(function.Path)}, {textName}.ToValues(), {textName}.ToBytes())"
                        : $"Lease.AcquireText({Id(function.Path)}, {textName}.ToValues(), {textName}.ToBytes())";
                    source.AppendLine($"        return new {returnType}({acquire});");
                }
                else
                {
                    string targetCall = function.Traits.Static ? $"{scopeName}.BeginText(default, {Id(function.Path)}, {textName}.ToValues(), {textName}.ToBytes())" : $"Lease.BeginText({Id(function.Path)}, {textName}.ToValues(), {textName}.ToBytes())";
                    source.AppendLine($"        using var {callName} = {targetCall};");
                    source.AppendLine($"        var {resultName} = {callName}.Read();");
                    if (function.ReturnType.Kind == UnrealValueKind.Void) { }
                    else if (UnrealNativeObjectBindings.IsText(function.ReturnType)) source.AppendLine($"        return {resultName}.DecodeString();");
                    else source.AppendLine($"        return {Runtime}UnrealNativeObjectValues.Decode<{returnType}>({resultName}.Call.Value);");
                }
            }
            else if (function.ReturnType.Kind == UnrealValueKind.NativeObject)
            {
                string arguments = "[" + string.Join(", ", function.Parameters.Select((p, i) => ArgumentExpression(p, i, argumentScope, function))) + "]";
                string acquire = function.Traits.Static ? scopeName + ".Construct" : "Lease.Acquire";
                source.AppendLine($"        return new {returnType}({acquire}({Id(function.Path)}, {arguments}));");
            }
            else
            {
                string arguments = "[" + string.Join(", ", function.Parameters.Select((p, i) => ArgumentExpression(p, i, argumentScope, function))) + "]";
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

    private static void EmitArguments(StringBuilder source, UnrealReflectedFunction function, string argumentScope, string textName)
    {
        foreach (var (parameter, index) in function.Parameters.Select((p, i) => (p, i)))
        {
            string expression = UnrealNativeObjectBindings.IsText(parameter.Type)
                ? $"{textName}.Add(@{parameter.Name})"
                : ArgumentExpression(parameter, index, argumentScope, function);
            source.AppendLine($"        {textName}.AddValue({expression});");
        }
    }

    private static string ArgumentExpression(UnrealReflectedParameter parameter, int index, string argumentScope, UnrealReflectedFunction function) =>
        UnrealNativeObjectBindings.IsStdFunction(parameter.Type) ? $"{argumentScope}.EncodeCallback(@{parameter.Name})" : parameter.Type.Kind == UnrealValueKind.NativeObject
            ? $"{argumentScope}.EncodeObject(@{parameter.Name}, {Id(parameter.Type.TypePath!)}, {(UnrealNativeObjectBindings.AllowsNull(function.NativeBinding!.Parameters[index]) ? "true" : "false")})"
            : $"{Runtime}UnrealNativeObjectValues.Encode(@{parameter.Name})";

    private static string ParameterName(string parameter) => parameter[(parameter.LastIndexOf(' ') + 1)..];
    private static string Id(string path) => $"0x{UnrealMemberId.FromPath(path):X16}UL";
    private static string UniqueName(UnrealReflectedFunction function, string name)
    {
        while (function.Parameters.Any(p => p.Name == name)) name += "_";
        return name;
    }
}
