using System.Text;

namespace UnifiedScript.Unreal;

internal static class UnrealNativeObjectThunkGenerator
{
    internal static void Emit(StringBuilder source, UnrealReflectionManifest manifest, string cppNamespace,
        ICollection<UnrealNativeThunkDiagnostic> diagnostics)
    {
        var objects = manifest.Types.Where(t => t.Kind == UnrealReflectedTypeKind.NativeObject).ToArray();
        if (objects.Length == 0) return;
        UnrealNativeObjectBindings.ValidateReferences(manifest);
        source.AppendLine("#include \"UnifiedScriptNativeObjectValues.h\"");
        foreach (string header in manifest.Types.Select(t => t.HeaderPath).Where(h => h is not null).Cast<string>().Distinct().Order(StringComparer.Ordinal))
            source.AppendLine($"#include \"{header}\"");
        source.AppendLine($"namespace {cppNamespace}::NativeObjects");
        source.AppendLine("{");
        source.AppendLine("using Status = EUnifiedScriptNativeObjectStatus;");
        source.AppendLine("namespace Values = UE::UnifiedScript::NativeObjectValues;");
        var entries = new List<string>();
        foreach (var type in objects)
        {
            source.AppendLine($"static_assert(!std::is_base_of_v<UObject, {type.NativeName}>, \"Native object bindings cannot own UObjects\");");
            foreach (var field in type.Properties)
            {
                if (UnrealNativeObjectBindings.Unsupported(field) is { } reason)
                {
                    diagnostics.Add(new("USUE5410", reason, field.Path));
                    continue;
                }
                foreach (bool write in new[] { false, true })
                {
                    if (write ? !field.Access.Writable : !field.Access.Readable) continue;
                    Begin(source, entries, field.Path + (write ? "#set" : "#get"), write ? 1 : 0);
                    Target(source, type);
                    string cpp = UnrealNativeObjectBindings.CppType(field.Type);
                    source.AppendLine($"    static_assert(std::is_same_v<std::remove_cv_t<decltype(TypedTarget->{field.Name})>, {cpp}>, \"Native field declaration mismatch\");");
                    if (write)
                    {
                        Decode(source, field.Type, 0);
                        source.AppendLine($"    TypedTarget->{field.Name} = Argument0;");
                    }
                    else source.AppendLine($"    OutValue = Values::Write(TypedTarget->{field.Name}, {TypeId(field.Type)});");
                    End(source);
                }
            }
            foreach (var function in type.Functions)
            {
                if (UnrealNativeObjectBindings.Unsupported(function) is { } reason)
                {
                    diagnostics.Add(new("USUE5410", reason, function.Path));
                    continue;
                }
                Begin(source, entries, function.Path, function.Parameters.Count);
                if (!function.Traits.Static) Target(source, type);
                else source.AppendLine("    if (Target.Value) return Status::InvalidArgument;");
                for (int index = 0; index < function.Parameters.Count; index++)
                    Decode(source, function.Parameters[index].Type, index, function.NativeBinding!.Parameters[index]);
                var binding = function.NativeBinding!;
                string args = string.Join(", ", Enumerable.Range(0, function.Parameters.Count).Select(i =>
                    (UnrealNativeObjectBindings.IsObjectReference(binding.Parameters[i]) ? "*" : "") + $"Argument{i}"));
                string cppReturn = UnrealNativeObjectBindings.CppType(function.ReturnType);
                string invocation;
                if (binding.Operation == UnrealNativeOperation.Constructor)
                    invocation = $"new {type.NativeName}({args})";
                else
                {
                    string parameters = string.Join(", ", function.Parameters.Select((p, i) =>
                        UnrealNativeObjectBindings.ParameterCppType(p.Type, binding.Parameters[i])));
                    string pointer = function.Traits.Static
                        ? $"{cppReturn} (*)({parameters})"
                        : $"{cppReturn} ({type.NativeName}::*)({parameters}){(binding.Const ? " const" : "")}";
                    string cast = $"static_cast<{pointer}>(&{type.NativeName}::{function.Name})";
                    invocation = function.Traits.Static ? $"({cast})({args})" : $"(TypedTarget->*{cast})({args})";
                }
                if (function.ReturnType.Kind == UnrealValueKind.NativeObject)
                {
                    if (binding.ReturnOwnership == UnrealNativeReturnOwnership.Owned)
                    {
                        string cppObject = cppReturn[..^1];
                        source.AppendLine($"    TUniquePtr<{cppObject}> Object({invocation});");
                        source.AppendLine($"    const Status Result = Registry.RegisterOwned(Scope, {TypeId(function.ReturnType)}, Object.Get(),");
                        source.AppendLine($"        [](void* Pointer) {{ delete static_cast<{cppObject}*>(Pointer); }}, OutHandle);");
                        source.AppendLine("    // A rejected duplicate address still belongs to its existing owner.");
                        source.AppendLine("    if (Result == Status::Success || Result == Status::WrongOwner) static_cast<void>(Object.Release());");
                        source.AppendLine("    return Result;");
                    }
                    else
                    {
                        source.AppendLine($"    auto* Object = {invocation};");
                        source.AppendLine($"    return Registry.RegisterBorrowed(Scope, {TypeId(function.ReturnType)}, Object, Target, OutHandle);");
                    }
                    source.AppendLine("}");
                }
                else
                {
                    source.AppendLine(function.ReturnType.Kind == UnrealValueKind.Void
                        ? $"    {invocation};" : $"    OutValue = Values::Write({invocation}, {TypeId(function.ReturnType)});");
                    End(source);
                }
            }
        }
        if (entries.Count > 0)
        {
            source.AppendLine("static const FUnifiedScriptNativeObjectMemberEntry Members[] =");
            source.AppendLine("{");
            foreach (string entry in entries) source.AppendLine(entry);
            source.AppendLine("};");
            source.AppendLine("static FUnifiedScriptNativeObjectLibrary Library(MakeArrayView(Members));");
        }
        source.AppendLine("}");
    }

    private static void Begin(StringBuilder source, List<string> entries, string path, int count)
    {
        string id = Id(path), name = $"Member_{UnrealMemberId.FromPath(path):X16}";
        string entry = $"    {{{id}, &{name}}},";
        if (entries.Contains(entry)) throw new InvalidOperationException("Native object member ID collision.");
        entries.Add(entry);
        source.AppendLine($"static Status {name}(FUnifiedScriptNativeObjectRegistry& Registry, uint64 Scope,");
        source.AppendLine("    FUnifiedScriptNativeObjectHandle Target, TConstArrayView<FUnifiedScriptNativeValueV3> Arguments,");
        source.AppendLine("    FUnifiedScriptNativeValueV3& OutValue, FUnifiedScriptNativeObjectHandle& OutHandle)");
        source.AppendLine("{");
        source.AppendLine($"    if (Arguments.Num() != {count}) return Status::InvalidArgument;");
    }
    private static void Target(StringBuilder source, UnrealReflectedType owner)
    {
        source.AppendLine("    void* Pointer = nullptr;");
        source.AppendLine($"    const Status Resolved = Registry.Resolve(Scope, Target, {Id(owner.Path)}, Pointer);");
        source.AppendLine("    if (Resolved != Status::Success) return Resolved;");
        source.AppendLine($"    auto* TypedTarget = static_cast<{owner.NativeName}*>(Pointer);");
    }
    private static void Decode(StringBuilder source, UnrealTypeReference type, int index,
        UnrealNativeParameterPassing passing = UnrealNativeParameterPassing.Value)
    {
        if (type.Kind == UnrealValueKind.NativeObject)
        {
            string pointerType = UnrealNativeObjectBindings.ParameterCppType(type, passing).Replace('&', '*');
            source.AppendLine($"    void* ArgumentPointer{index} = nullptr;");
            source.AppendLine($"    const Status ArgumentStatus{index} = Values::ReadObject(Registry, Scope, Arguments[{index}], {TypeId(type)}, {(UnrealNativeObjectBindings.AllowsNull(passing) ? "true" : "false")}, ArgumentPointer{index});");
            source.AppendLine($"    if (ArgumentStatus{index} != Status::Success) return ArgumentStatus{index};");
            source.AppendLine($"    auto* Argument{index} = static_cast<{pointerType}>(ArgumentPointer{index});");
            return;
        }
        source.AppendLine($"    {UnrealNativeObjectBindings.CppType(type)} Argument{index}{{}};");
        source.AppendLine($"    if (!Values::Read(Arguments[{index}], Argument{index}, {TypeId(type)})) return Status::InvalidArgument;");
    }
    private static void End(StringBuilder source) { source.AppendLine("    return Status::Success;"); source.AppendLine("}"); }
    private static string TypeId(UnrealTypeReference type) => type.Kind is UnrealValueKind.Enum or UnrealValueKind.NativeObject ? Id(type.TypePath!) : "0";
    private static string Id(string path) => $"0x{UnrealMemberId.FromPath(path):X16}ull";
}
