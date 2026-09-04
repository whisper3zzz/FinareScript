using System.Text;

namespace UnifiedScript.Unreal;

public sealed record UnrealNativeThunkGeneratorOptions(
    string RuntimeHeader = "UnifiedScriptGeneratedThunks.h",
    string CppNamespace = "UE::UnifiedScript::Generated");

public sealed record UnrealNativeThunkDiagnostic(string Code, string Message, string MemberPath);

public sealed record UnrealNativeThunkGenerationResult(
    string Source,
    IReadOnlyList<UnrealNativeThunkDiagnostic> Diagnostics);

/// <summary>
/// Emits the generated fast-binding half of ABI v3. Supported public reflected members are
/// dispatched by their stable ID and call native fields/functions directly.
/// </summary>
public sealed class UnrealNativeThunkGenerator
{
    public UnrealNativeThunkGenerationResult Generate(
        UnrealReflectionManifest manifest,
        UnrealNativeThunkGeneratorOptions? options = null)
    {
        UnrealReflectionManifest normalized = UnrealReflectionJson.NormalizeAndValidate(manifest);
        options ??= new UnrealNativeThunkGeneratorOptions();
        if (!IsHeaderPath(options.RuntimeHeader))
        {
            throw new ArgumentException(
                $"'{options.RuntimeHeader}' is not a valid generated thunk runtime header.",
                nameof(options));
        }
        if (!IsCppNamespace(options.CppNamespace))
        {
            throw new ArgumentException(
                $"'{options.CppNamespace}' is not a valid C++ namespace.",
                nameof(options));
        }

        var diagnostics = new List<UnrealNativeThunkDiagnostic>();
        var properties = new List<(UnrealReflectedType Type, UnrealReflectedProperty Property)>();
        var functions = new List<(UnrealReflectedType Type, UnrealReflectedFunction Function)>();
        UnrealReflectedType[] structTypes = normalized.Types
            .Where(static type => type.Kind == UnrealReflectedTypeKind.Struct)
            .ToArray();
        HashSet<string> supportedStructPaths = GetSupportedStructPaths(structTypes);

        foreach (UnrealReflectedType type in normalized.Types)
        {
            if (type.Kind == UnrealReflectedTypeKind.NativeObject) continue;
            if (!type.Path.StartsWith("/Script/", StringComparison.Ordinal))
            {
                foreach (UnrealReflectedProperty property in type.Properties)
                {
                    diagnostics.Add(new UnrealNativeThunkDiagnostic(
                        "USUE3010",
                        "Blueprint asset members use the generation-safe reflected route because asset-generated classes have no native C++ header.",
                        property.Path));
                }
                foreach (UnrealReflectedFunction function in type.Functions)
                {
                    diagnostics.Add(new UnrealNativeThunkDiagnostic(
                        "USUE3010",
                        "Blueprint asset members use the generation-safe reflected route because asset-generated classes have no native C++ header.",
                        function.Path));
                }
                continue;
            }
            if (type.Kind == UnrealReflectedTypeKind.Struct)
            {
                foreach (UnrealReflectedProperty field in type.Properties)
                {
                    if (field.Access.NativeAccess != UnrealNativeAccess.Public ||
                        !IsNativeValueType(field.Type, allowVoid: false, supportedStructPaths))
                    {
                        diagnostics.Add(new UnrealNativeThunkDiagnostic(
                            "USUE3007",
                            "Only public fields with ABI v3 value types can participate in a generated struct codec.",
                            field.Path));
                    }
                }
                continue;
            }
            if (type.Kind != UnrealReflectedTypeKind.Class)
            {
                if (type.Kind != UnrealReflectedTypeKind.Enum)
                {
                    diagnostics.Add(new UnrealNativeThunkDiagnostic(
                        "USUE3001",
                        $"Reflected {type.Kind.ToString().ToLowerInvariant()} native thunks are deferred.",
                        type.Path));
                }
                continue;
            }

            foreach (UnrealReflectedProperty property in type.Properties)
            {
                if (property.Access.NativeAccess != UnrealNativeAccess.Public)
                {
                    diagnostics.Add(new UnrealNativeThunkDiagnostic(
                        "USUE3002",
                        "Only public native properties can use direct generated thunks.",
                        property.Path));
                    continue;
                }
                if (!IsNativeValueType(property.Type, allowVoid: false, supportedStructPaths))
                {
                    diagnostics.Add(new UnrealNativeThunkDiagnostic(
                        "USUE3003",
                        $"Property type '{property.Type.NativeName}' is not supported by generated fast bindings.",
                        property.Path));
                    continue;
                }
                properties.Add((type, property));
            }

            foreach (UnrealReflectedFunction function in type.Functions)
            {
                if (function.Traits.NativeAccess != UnrealNativeAccess.Public)
                {
                    diagnostics.Add(new UnrealNativeThunkDiagnostic(
                        "USUE3004",
                        "Only public native functions can use direct generated thunks.",
                        function.Path));
                    continue;
                }
                if (!IsNativeValueType(function.ReturnType, allowVoid: true, supportedStructPaths) ||
                    function.Parameters.Any(parameter =>
                        !IsNativeValueType(parameter.Type, allowVoid: false, supportedStructPaths)))
                {
                    diagnostics.Add(new UnrealNativeThunkDiagnostic(
                        "USUE3006",
                        "The native function signature contains a value type not supported by generated fast bindings.",
                        function.Path));
                    continue;
                }
                functions.Add((type, function));
            }
        }

        EnsureUniqueMemberIds(properties, functions);

        IEnumerable<string> referencedHeaders = properties
            .SelectMany(static item => EnumerateTypeReferences(item.Property.Type))
            .Concat(functions.SelectMany(static item =>
                EnumerateTypeReferences(item.Function.ReturnType)
                    .Concat(item.Function.Parameters.SelectMany(parameter =>
                        EnumerateTypeReferences(parameter.Type)))))
            .Concat(structTypes
                .Where(type => supportedStructPaths.Contains(type.Path))
                .SelectMany(static type => type.Properties.SelectMany(field =>
                    EnumerateTypeReferences(field.Type))))
            .Select(static reference => reference.HeaderPath)
            .Where(static header => !string.IsNullOrWhiteSpace(header))
            .Select(static header => header!);
        string[] headers = properties.Select(static item => HeaderFor(item.Type))
            .Concat(functions.Select(static item => HeaderFor(item.Type)))
            .Concat(structTypes
                .Where(type => supportedStructPaths.Contains(type.Path))
                .Select(HeaderFor))
            .Concat(referencedHeaders)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        var source = new StringBuilder();
        source.AppendLine("// <auto-generated />");
        source.Append("#include \"").Append(options.RuntimeHeader).AppendLine("\"");
        if (properties.Any(static item => item.Property.NativeExtension is not null))
            source.AppendLine("#include <type_traits>");
        foreach (string header in headers)
        {
            source.Append("#include \"").Append(header).AppendLine("\"");
        }
        source.AppendLine();
        EmitHelpers(source, options.CppNamespace);
        EmitStructCodecs(
            source,
            structTypes.Where(type => supportedStructPaths.Contains(type.Path)).ToArray(),
            options.CppNamespace);
        EmitResultDescriptors(source, properties, functions, options.CppNamespace);
        EmitReadDispatcher(source, properties, options.CppNamespace);
        EmitWriteDispatcher(source, properties, options.CppNamespace);
        EmitInvokeDispatcher(source, functions, options.CppNamespace);
        EmitInvokeInt32x2Dispatcher(source, functions, options.CppNamespace);
        EmitInvokeFixedDispatcher(source, functions, options.CppNamespace);
        UnrealNativeObjectThunkGenerator.Emit(source, normalized, options.CppNamespace, diagnostics);

        return new UnrealNativeThunkGenerationResult(
            source.ToString().TrimEnd().ReplaceLineEndings("\n") + "\n",
            diagnostics
                .OrderBy(static diagnostic => diagnostic.MemberPath, StringComparer.Ordinal)
                .ThenBy(static diagnostic => diagnostic.Code, StringComparer.Ordinal)
                .ToArray());
    }

    private static void EmitHelpers(StringBuilder source, string cppNamespace)
    {
        source.Append("namespace ").Append(cppNamespace).AppendLine("::Private");
        source.AppendLine("{");
        source.AppendLine("void Succeed(FUnifiedScriptInvocationResult& OutResult)");
        source.AppendLine("{");
        source.AppendLine("    OutResult.Error = EUnifiedScriptInvocationError::None;");
        source.AppendLine("    OutResult.HandleError = EUnifiedScriptObjectHandleError::None;");
        source.AppendLine("    OutResult.Message.Reset();");
        source.AppendLine("}");
        source.AppendLine();
        source.AppendLine("bool Fail(");
        source.AppendLine("    FUnifiedScriptInvocationResult& OutResult,");
        source.AppendLine("    EUnifiedScriptInvocationError Error,");
        source.AppendLine("    FString Message)");
        source.AppendLine("{");
        source.AppendLine("    OutResult.Error = Error;");
        source.AppendLine("    OutResult.HandleError = EUnifiedScriptObjectHandleError::None;");
        source.AppendLine("    OutResult.Message = MoveTemp(Message);");
        source.AppendLine("    return false;");
        source.AppendLine("}");
        source.AppendLine();
        source.AppendLine("template <typename TObjectType>");
        source.AppendLine("TObjectType* ResolveTarget(");
        source.AppendLine("    UObject* Target,");
        source.AppendLine("    const TCHAR* ExpectedTypePath,");
        source.AppendLine("    FUnifiedScriptInvocationResult& OutResult)");
        source.AppendLine("{");
        source.AppendLine("    TObjectType* TypedTarget = Cast<TObjectType>(Target);");
        source.AppendLine("    if (TypedTarget == nullptr)");
        source.AppendLine("    {");
        source.AppendLine("        Fail(");
        source.AppendLine("            OutResult,");
        source.AppendLine("            EUnifiedScriptInvocationError::TargetTypeMismatch,");
        source.AppendLine("            FString::Printf(");
        source.AppendLine("                TEXT(\"Object '%s' is not a generated thunk target of type '%s'.\"),");
        source.AppendLine("                *GetNameSafe(Target),");
        source.AppendLine("                ExpectedTypePath));");
        source.AppendLine("    }");
        source.AppendLine("    return TypedTarget;");
        source.AppendLine("}");
        source.AppendLine();
        source.AppendLine("bool RequireArgumentCount(");
        source.AppendLine("    TConstArrayView<FUnifiedScriptValue> Arguments,");
        source.AppendLine("    int32 Required,");
        source.AppendLine("    FUnifiedScriptInvocationResult& OutResult)");
        source.AppendLine("{");
        source.AppendLine("    return Arguments.Num() == Required || Fail(");
        source.AppendLine("        OutResult,");
        source.AppendLine("        EUnifiedScriptInvocationError::ArgumentCountMismatch,");
        source.AppendLine("        FString::Printf(");
        source.AppendLine("            TEXT(\"Generated native thunk requires %d arguments but received %d.\"),");
        source.AppendLine("            Required,");
        source.AppendLine("            Arguments.Num()));");
        source.AppendLine("}");
        source.AppendLine();
        source.AppendLine("bool FailUnknownMember(");
        source.AppendLine("    FUnifiedScriptInvocationResult& OutResult,");
        source.AppendLine("    uint64 MemberId)");
        source.AppendLine("{");
        source.AppendLine("    return Fail(");
        source.AppendLine("        OutResult,");
        source.AppendLine("        EUnifiedScriptInvocationError::InvalidMemberPath,");
        source.AppendLine("        FString::Printf(");
        source.AppendLine("            TEXT(\"Generated native member ID 0x%016llX is not registered.\"),");
        source.AppendLine("            static_cast<unsigned long long>(MemberId)));");
        source.AppendLine("}");
        source.AppendLine();
        source.AppendLine("template <typename TObjectType>");
        source.AppendLine("UObject* GetObjectPointer(TObjectType* Object)");
        source.AppendLine("{");
        source.AppendLine("    return reinterpret_cast<UObject*>(Object);");
        source.AppendLine("}");
        source.AppendLine();
        source.AppendLine("template <typename TObjectType>");
        source.AppendLine("UObject* GetObjectPointer(const TObjectPtr<TObjectType>& Object)");
        source.AppendLine("{");
        source.AppendLine("    return reinterpret_cast<UObject*>(Object.Get());");
        source.AppendLine("}");
        source.AppendLine();
        source.AppendLine("template <typename TObjectType>");
        source.AppendLine("UObject* GetObjectPointer(const TSubclassOf<TObjectType>& Object)");
        source.AppendLine("{");
        source.AppendLine("    return Object.Get();");
        source.AppendLine("}");
        source.AppendLine();
        source.AppendLine("bool EncodeObject(");
        source.AppendLine("    FUnifiedScriptObjectRegistry& Registry,");
        source.AppendLine("    UObject* Object,");
        source.AppendLine("    bool bClass,");
        source.AppendLine("    FUnifiedScriptValue& OutValue,");
        source.AppendLine("    FUnifiedScriptInvocationResult& OutResult)");
        source.AppendLine("{");
        source.AppendLine("    if (Object == nullptr)");
        source.AppendLine("    {");
        source.AppendLine("        OutValue = FUnifiedScriptValue::FromObjectHandle({}, bClass);");
        source.AppendLine("        return true;");
        source.AppendLine("    }");
        source.AppendLine("    FUnifiedScriptObjectHandleResult HandleResult;");
        source.AppendLine("    const FUnifiedScriptObjectHandle Handle = Registry.RegisterObject(Object, HandleResult);");
        source.AppendLine("    if (!HandleResult.IsSuccess())");
        source.AppendLine("    {");
        source.AppendLine("        OutResult.Error = EUnifiedScriptInvocationError::ObjectHandleFailure;");
        source.AppendLine("        OutResult.HandleError = HandleResult.Error;");
        source.AppendLine("        OutResult.Message = MoveTemp(HandleResult.Message);");
        source.AppendLine("        return false;");
        source.AppendLine("    }");
        source.AppendLine("    OutValue = FUnifiedScriptValue::FromObjectHandle(Handle, bClass);");
        source.AppendLine("    return true;");
        source.AppendLine("}");
        source.AppendLine();
        source.AppendLine("template <typename TObjectType>");
        source.AppendLine("bool DecodeObject(");
        source.AppendLine("    FUnifiedScriptObjectRegistry& Registry,");
        source.AppendLine("    const FUnifiedScriptValue& Value,");
        source.AppendLine("    bool bClass,");
        source.AppendLine("    TObjectType*& OutObject,");
        source.AppendLine("    FUnifiedScriptInvocationResult& OutResult)");
        source.AppendLine("{");
        source.AppendLine("    FUnifiedScriptObjectHandle Handle;");
        source.AppendLine("    if (!Value.TryGetObjectHandle(Handle, bClass))");
        source.AppendLine("    {");
        source.AppendLine("        return Fail(");
        source.AppendLine("            OutResult,");
        source.AppendLine("            EUnifiedScriptInvocationError::ValueTypeMismatch,");
        source.AppendLine("            TEXT(\"Generated native object argument has an incompatible value kind.\"));");
        source.AppendLine("    }");
        source.AppendLine("    if (Handle.IsNull())");
        source.AppendLine("    {");
        source.AppendLine("        OutObject = nullptr;");
        source.AppendLine("        return true;");
        source.AppendLine("    }");
        source.AppendLine("    FUnifiedScriptObjectHandleResult HandleResult;");
        source.AppendLine("    UObject* Object = Registry.ResolveObject(Handle, HandleResult);");
        source.AppendLine("    if (!HandleResult.IsSuccess())");
        source.AppendLine("    {");
        source.AppendLine("        OutResult.Error = EUnifiedScriptInvocationError::ObjectHandleFailure;");
        source.AppendLine("        OutResult.HandleError = HandleResult.Error;");
        source.AppendLine("        OutResult.Message = MoveTemp(HandleResult.Message);");
        source.AppendLine("        return false;");
        source.AppendLine("    }");
        source.AppendLine("    OutObject = Cast<TObjectType>(Object);");
        source.AppendLine("    return OutObject != nullptr || Fail(");
        source.AppendLine("        OutResult,");
        source.AppendLine("        EUnifiedScriptInvocationError::ValueTypeMismatch,");
        source.AppendLine("        TEXT(\"Generated native object argument has an incompatible object type.\"));");
        source.AppendLine("}");
        source.AppendLine();
        source.AppendLine("template <typename TObjectType>");
        source.AppendLine("bool DecodeFixedObject(");
        source.AppendLine("    FUnifiedScriptObjectRegistry& Registry,");
        source.AppendLine("    const FUnifiedScriptNativeValueV3& Value,");
        source.AppendLine("    TObjectType*& OutObject,");
        source.AppendLine("    FUnifiedScriptInvocationResult& OutResult)");
        source.AppendLine("{");
        source.AppendLine("    if (Value.Kind != EUnifiedScriptNativeValueKindV3::Object)");
        source.AppendLine("    {");
        source.AppendLine("        return Fail(OutResult, EUnifiedScriptInvocationError::ValueTypeMismatch,");
        source.AppendLine("            TEXT(\"Generated fixed object argument has an incompatible value kind.\"));");
        source.AppendLine("    }");
        source.AppendLine("    const FUnifiedScriptObjectHandle Handle{Value.Payload0};");
        source.AppendLine("    if (Handle.IsNull())");
        source.AppendLine("    {");
        source.AppendLine("        OutObject = nullptr;");
        source.AppendLine("        return true;");
        source.AppendLine("    }");
        source.AppendLine("    FUnifiedScriptObjectHandleResult HandleResult;");
        source.AppendLine("    UObject* Object = Registry.ResolveObject(Handle, HandleResult);");
        source.AppendLine("    if (!HandleResult.IsSuccess())");
        source.AppendLine("    {");
        source.AppendLine("        OutResult.Error = EUnifiedScriptInvocationError::ObjectHandleFailure;");
        source.AppendLine("        OutResult.HandleError = HandleResult.Error;");
        source.AppendLine("        OutResult.Message = MoveTemp(HandleResult.Message);");
        source.AppendLine("        return false;");
        source.AppendLine("    }");
        source.AppendLine("    OutObject = Cast<TObjectType>(Object);");
        source.AppendLine("    return OutObject != nullptr || Fail(OutResult,");
        source.AppendLine("        EUnifiedScriptInvocationError::ValueTypeMismatch,");
        source.AppendLine("        TEXT(\"Generated fixed object argument has an incompatible object type.\"));");
        source.AppendLine("}");
        source.AppendLine();
        source.AppendLine("bool EncodeFixedObject(");
        source.AppendLine("    FUnifiedScriptObjectRegistry& Registry,");
        source.AppendLine("    UObject* Object,");
        source.AppendLine("    FUnifiedScriptNativeValueV3& OutValue,");
        source.AppendLine("    FUnifiedScriptInvocationResult& OutResult)");
        source.AppendLine("{");
        source.AppendLine("    OutValue = {};");
        source.AppendLine("    OutValue.Kind = EUnifiedScriptNativeValueKindV3::Object;");
        source.AppendLine("    if (Object == nullptr)");
        source.AppendLine("    {");
        source.AppendLine("        return true;");
        source.AppendLine("    }");
        source.AppendLine("    FUnifiedScriptObjectHandleResult HandleResult;");
        source.AppendLine("    const FUnifiedScriptObjectHandle Handle = Registry.RegisterObject(Object, HandleResult);");
        source.AppendLine("    if (!HandleResult.IsSuccess())");
        source.AppendLine("    {");
        source.AppendLine("        OutResult.Error = EUnifiedScriptInvocationError::ObjectHandleFailure;");
        source.AppendLine("        OutResult.HandleError = HandleResult.Error;");
        source.AppendLine("        OutResult.Message = MoveTemp(HandleResult.Message);");
        source.AppendLine("        return false;");
        source.AppendLine("    }");
        source.AppendLine("    OutValue.Payload0 = Handle.Value;");
        source.AppendLine("    return true;");
        source.AppendLine("}");
        source.AppendLine("}");
        source.AppendLine();
    }

    private static void EmitStructCodecs(
        StringBuilder source,
        IReadOnlyList<UnrealReflectedType> structTypes,
        string cppNamespace)
    {
        if (structTypes.Count == 0)
        {
            return;
        }
        source.Append("namespace ").Append(cppNamespace).AppendLine("::Private");
        source.AppendLine("{");
        foreach (UnrealReflectedType type in structTypes)
        {
            string suffix = StructCodecSuffix(type.Path);
            source.Append("bool EncodeStruct_").Append(suffix).Append("(const ")
                .Append(type.NativeName).AppendLine("& Value,");
            source.AppendLine("    FUnifiedScriptObjectRegistry& Registry,");
            source.AppendLine("    FUnifiedScriptValue& OutValue,");
            source.AppendLine("    FUnifiedScriptInvocationResult& OutResult);");
            source.Append("bool DecodeStruct_").Append(suffix).AppendLine("(");
            source.AppendLine("    const FUnifiedScriptValue& Value,");
            source.AppendLine("    FUnifiedScriptObjectRegistry& Registry,");
            source.Append("    ").Append(type.NativeName).AppendLine("& OutValue,");
            source.AppendLine("    FUnifiedScriptInvocationResult& OutResult);");
        }
        source.AppendLine();
        foreach (UnrealReflectedType type in structTypes)
        {
            string suffix = StructCodecSuffix(type.Path);
            source.Append("bool EncodeStruct_").Append(suffix).Append("(const ")
                .Append(type.NativeName).AppendLine("& Value,");
            source.AppendLine("    FUnifiedScriptObjectRegistry& Registry,");
            source.AppendLine("    FUnifiedScriptValue& OutValue,");
            source.AppendLine("    FUnifiedScriptInvocationResult& OutResult)");
            source.AppendLine("{");
            source.AppendLine("    TArray<FUnifiedScriptValue> Fields;");
            source.Append("    Fields.Reserve(").Append(type.Properties.Count).AppendLine(");");
            for (int index = 0; index < type.Properties.Count; index++)
            {
                UnrealReflectedProperty field = type.Properties[index];
                source.Append("    FUnifiedScriptValue Field").Append(index).AppendLine(";");
                EmitValueWrite(source, field.Type, $"Value.{field.Name}", $"Field{index}", 4);
                source.Append("    Fields.Add(MoveTemp(Field").Append(index).AppendLine("));");
            }
            source.Append("    OutValue = FUnifiedScriptValue::FromStruct(MoveTemp(Fields), ")
                .Append(TypeIdLiteral(type.Path)).AppendLine(");");
            source.AppendLine("    return true;");
            source.AppendLine("}");
            source.AppendLine();

            source.Append("bool DecodeStruct_").Append(suffix).AppendLine("(");
            source.AppendLine("    const FUnifiedScriptValue& Value,");
            source.AppendLine("    FUnifiedScriptObjectRegistry& Registry,");
            source.Append("    ").Append(type.NativeName).AppendLine("& OutValue,");
            source.AppendLine("    FUnifiedScriptInvocationResult& OutResult)");
            source.AppendLine("{");
            source.AppendLine("    TConstArrayView<FUnifiedScriptValue> Fields;");
            source.Append("    if (!Value.TryGetStruct(").Append(TypeIdLiteral(type.Path))
                .Append(", Fields) || Fields.Num() != ").Append(type.Properties.Count).AppendLine(")");
            source.AppendLine("    {");
            source.AppendLine("        return Fail(OutResult, EUnifiedScriptInvocationError::ValueTypeMismatch,");
            source.Append("            TEXT(").Append(CppStringLiteral(
                    $"Generated struct '{type.Path}' received an incompatible field layout."))
                .AppendLine("));");
            source.AppendLine("    }");
            for (int index = 0; index < type.Properties.Count; index++)
            {
                UnrealReflectedProperty field = type.Properties[index];
                EmitValueRead(
                    source,
                    field.Type,
                    $"Fields[{index}]",
                    $"DecodedField{index}",
                    4,
                    field.Path);
                source.Append("    OutValue.").Append(field.Name).Append(" = ");
                if (field.Type.Kind is UnrealValueKind.String or UnrealValueKind.Struct or
                    UnrealValueKind.Array or UnrealValueKind.Set or UnrealValueKind.Map)
                {
                    source.Append("MoveTemp(DecodedField").Append(index).Append(')');
                }
                else
                {
                    source.Append("DecodedField").Append(index);
                }
                source.AppendLine(";");
            }
            source.AppendLine("    return true;");
            source.AppendLine("}");
            source.AppendLine();
        }
        source.AppendLine("}");
        source.AppendLine();
    }

    private static void EmitResultDescriptors(
        StringBuilder source,
        IReadOnlyList<(UnrealReflectedType Type, UnrealReflectedProperty Property)> properties,
        IReadOnlyList<(UnrealReflectedType Type, UnrealReflectedFunction Function)> functions,
        string cppNamespace)
    {
        source.Append("bool ").Append(cppNamespace).AppendLine("::DescribeReadResult(");
        source.AppendLine("    uint64 MemberId,");
        source.AppendLine("    EUnifiedScriptValueKind& OutKind,");
        source.AppendLine("    uint64& OutTypeId)");
        source.AppendLine("{");
        EmitMemberSwitchStart(source, properties.Count);
        foreach ((_, UnrealReflectedProperty property) in properties)
        {
            source.Append("    case ").Append(MemberIdLiteral(property.Path)).AppendLine(":");
            EmitResultDescriptorAssignment(source, property.Type, 8);
        }
        EmitMemberSwitchEnd(source, properties.Count, "false");
        source.AppendLine("}");
        source.AppendLine();

        source.Append("bool ").Append(cppNamespace).AppendLine("::DescribeInvokeResultCount(");
        source.AppendLine("    uint64 MemberId,");
        source.AppendLine("    uint32& OutResultCount)");
        source.AppendLine("{");
        EmitMemberSwitchStart(source, functions.Count);
        foreach ((_, UnrealReflectedFunction function) in functions)
        {
            int count = (function.ReturnType.Kind == UnrealValueKind.Void ? 0 : 1) +
                function.Parameters.Count(static parameter =>
                    parameter.Direction != UnrealParameterDirection.Input);
            source.Append("    case ").Append(MemberIdLiteral(function.Path)).AppendLine(":");
            source.Append("        OutResultCount = ").Append(count).AppendLine(";");
            source.AppendLine("        return true;");
        }
        EmitMemberSwitchEnd(source, functions.Count, "false");
        source.AppendLine("}");
        source.AppendLine();

        source.Append("bool ").Append(cppNamespace).AppendLine("::DescribeInvokeResult(");
        source.AppendLine("    uint64 MemberId,");
        source.AppendLine("    uint32 ResultIndex,");
        source.AppendLine("    EUnifiedScriptValueKind& OutKind,");
        source.AppendLine("    uint64& OutTypeId)");
        source.AppendLine("{");
        EmitMemberSwitchStart(source, functions.Count);
        foreach ((_, UnrealReflectedFunction function) in functions)
        {
            var resultTypes = new List<UnrealTypeReference>();
            if (function.ReturnType.Kind != UnrealValueKind.Void)
            {
                resultTypes.Add(function.ReturnType);
            }
            resultTypes.AddRange(function.Parameters
                .Where(static parameter => parameter.Direction != UnrealParameterDirection.Input)
                .Select(static parameter => parameter.Type));
            source.Append("    case ").Append(MemberIdLiteral(function.Path)).AppendLine(":");
            if (resultTypes.Count == 0)
            {
                source.AppendLine("        return false;");
                continue;
            }
            source.AppendLine("        switch (ResultIndex)");
            source.AppendLine("        {");
            for (int index = 0; index < resultTypes.Count; index++)
            {
                source.Append("        case ").Append(index).AppendLine(":");
                EmitResultDescriptorAssignment(source, resultTypes[index], 12);
            }
            source.AppendLine("        default:");
            source.AppendLine("            return false;");
            source.AppendLine("        }");
        }
        EmitMemberSwitchEnd(source, functions.Count, "false");
        source.AppendLine("}");
        source.AppendLine();
    }

    // MSVC treats default-only switches as C4065 under Unreal's warnings-as-errors.
    private static void EmitMemberSwitchStart(StringBuilder source, int count)
    {
        if (count == 0) return;
        source.AppendLine("    switch (MemberId)");
        source.AppendLine("    {");
    }

    private static void EmitMemberSwitchEnd(StringBuilder source, int count, string fallback)
    {
        if (count > 0) source.AppendLine("    default:");
        source.Append(count > 0 ? "        return " : "    return ").Append(fallback).AppendLine(";");
        if (count > 0) source.AppendLine("    }");
    }

    private static void EmitResultDescriptorAssignment(
        StringBuilder source,
        UnrealTypeReference type,
        int indent)
    {
        string padding = new(' ', indent);
        source.Append(padding).Append("OutKind = EUnifiedScriptValueKind::")
            .Append(NativeValueKind(type)).AppendLine(";");
        source.Append(padding).Append("OutTypeId = ").Append(type.Kind switch
        {
            UnrealValueKind.Enum => TypeIdLiteral(type.TypePath!),
            UnrealValueKind.Struct when !IsCoreStruct(type.TypePath) => TypeIdLiteral(type.TypePath!),
            UnrealValueKind.Array or UnrealValueKind.Set or UnrealValueKind.Map =>
                TypeIdLiteral(type.NativeName),
            _ => "0ull",
        }).AppendLine(";");
        source.Append(padding).AppendLine("return true;");
    }

    private static void EmitReadDispatcher(
        StringBuilder source,
        IReadOnlyList<(UnrealReflectedType Type, UnrealReflectedProperty Property)> properties,
        string cppNamespace)
    {
        source.Append("bool ").Append(cppNamespace).AppendLine("::ReadProperty(");
        source.AppendLine("    UObject* Target,");
        source.AppendLine("    FUnifiedScriptObjectRegistry& Registry,");
        source.AppendLine("    uint64 MemberId,");
        source.AppendLine("    FUnifiedScriptValue& OutValue,");
        source.AppendLine("    FUnifiedScriptInvocationResult& OutResult)");
        source.AppendLine("{");
        source.Append("    using namespace ").Append(cppNamespace).AppendLine("::Private;");
        EmitMemberSwitchStart(source, properties.Count);
        foreach ((UnrealReflectedType type, UnrealReflectedProperty property) in properties)
        {
            source.Append("    case ").Append(MemberIdLiteral(property.Path)).AppendLine(":");
            source.AppendLine("    {");
            if (!property.Access.Readable)
            {
                EmitFailure(source, "PropertyNotReadable", "Generated native property is not readable.", 8);
                source.AppendLine("    }");
                continue;
            }
            EmitResolveTarget(source, type, 8);
            EmitNativeFieldTypeCheck(source, property);
            EmitValueWrite(
                source,
                property.Type,
                $"TypedTarget->{property.Name}",
                "OutValue",
                8);
            source.AppendLine("        Succeed(OutResult);");
            source.AppendLine("        return true;");
            source.AppendLine("    }");
        }
        EmitMemberSwitchEnd(source, properties.Count, "FailUnknownMember(OutResult, MemberId)");
        source.AppendLine("}");
        source.AppendLine();
    }

    private static void EmitWriteDispatcher(
        StringBuilder source,
        IReadOnlyList<(UnrealReflectedType Type, UnrealReflectedProperty Property)> properties,
        string cppNamespace)
    {
        source.Append("bool ").Append(cppNamespace).AppendLine("::WriteProperty(");
        source.AppendLine("    UObject* Target,");
        source.AppendLine("    FUnifiedScriptObjectRegistry& Registry,");
        source.AppendLine("    uint64 MemberId,");
        source.AppendLine("    const FUnifiedScriptValue& Value,");
        source.AppendLine("    FUnifiedScriptInvocationResult& OutResult)");
        source.AppendLine("{");
        source.Append("    using namespace ").Append(cppNamespace).AppendLine("::Private;");
        EmitMemberSwitchStart(source, properties.Count);
        foreach ((UnrealReflectedType type, UnrealReflectedProperty property) in properties)
        {
            source.Append("    case ").Append(MemberIdLiteral(property.Path)).AppendLine(":");
            source.AppendLine("    {");
            if (!property.Access.Writable)
            {
                EmitFailure(source, "PropertyNotWritable", "Generated native property is read-only.", 8);
                source.AppendLine("    }");
                continue;
            }
            EmitResolveTarget(source, type, 8);
            EmitNativeFieldTypeCheck(source, property);
            EmitValueRead(source, property.Type, "Value", "NativeValue", 8, property.Path);
            source.Append("        TypedTarget->").Append(property.Name).Append(" = ");
            if (property.Type.Kind is UnrealValueKind.String or
                UnrealValueKind.Array or UnrealValueKind.Set or UnrealValueKind.Map)
            {
                source.Append("MoveTemp(NativeValue)");
            }
            else
            {
                source.Append("NativeValue");
            }
            source.AppendLine(";");
            source.AppendLine("        Succeed(OutResult);");
            source.AppendLine("        return true;");
            source.AppendLine("    }");
        }
        EmitMemberSwitchEnd(source, properties.Count, "FailUnknownMember(OutResult, MemberId)");
        source.AppendLine("}");
        source.AppendLine();
    }

    private static void EmitNativeFieldTypeCheck(StringBuilder source, UnrealReflectedProperty property)
    {
        if (property.NativeExtension is null) return;
        source.Append("        static_assert(std::is_same_v<std::remove_cv_t<decltype(TypedTarget->")
            .Append(property.Name).Append(")>, ").Append(CppType(property.Type))
            .AppendLine(">, \"Native extension field type does not match its contract.\");");
    }

    private static void EmitInvokeDispatcher(
        StringBuilder source,
        IReadOnlyList<(UnrealReflectedType Type, UnrealReflectedFunction Function)> functions,
        string cppNamespace)
    {
        source.Append("bool ").Append(cppNamespace).AppendLine("::Invoke(");
        source.AppendLine("    UObject* Target,");
        source.AppendLine("    FUnifiedScriptObjectRegistry& Registry,");
        source.AppendLine("    uint64 MemberId,");
        source.AppendLine("    TConstArrayView<FUnifiedScriptValue> Arguments,");
        source.AppendLine("    FUnifiedScriptValue& OutReturnValue,");
        source.AppendLine("    TArray<FUnifiedScriptValue>& OutOutputValues,");
        source.AppendLine("    FUnifiedScriptInvocationResult& OutResult)");
        source.AppendLine("{");
        source.Append("    using namespace ").Append(cppNamespace).AppendLine("::Private;");
        EmitMemberSwitchStart(source, functions.Count);
        foreach ((UnrealReflectedType type, UnrealReflectedFunction function) in functions)
        {
            int inputCount = function.Parameters.Count(static parameter =>
                parameter.Direction != UnrealParameterDirection.Output);
            int outputCount = function.Parameters.Count(static parameter =>
                parameter.Direction != UnrealParameterDirection.Input);
            source.Append("    case ").Append(MemberIdLiteral(function.Path)).AppendLine(":");
            source.AppendLine("    {");
            source.Append("        if (!RequireArgumentCount(Arguments, ")
                .Append(inputCount).AppendLine(", OutResult))");
            source.AppendLine("        {");
            source.AppendLine("            return false;");
            source.AppendLine("        }");
            if (!function.Traits.Static)
            {
                EmitResolveFunctionTarget(source, type, 8);
            }
            source.AppendLine("        OutOutputValues.Reset();");
            if (outputCount > 0)
            {
                source.Append("        OutOutputValues.Reserve(").Append(outputCount).AppendLine(");");
            }
            int inputIndex = 0;
            for (int index = 0; index < function.Parameters.Count; index++)
            {
                UnrealReflectedParameter parameter = function.Parameters[index];
                if (parameter.Direction == UnrealParameterDirection.Output)
                {
                    source.Append("        ").Append(CppType(parameter.Type)).Append(" Argument")
                        .Append(index).Append(" = ").Append(CppDefault(parameter.Type)).AppendLine(";");
                }
                else
                {
                    EmitValueRead(
                        source,
                        parameter.Type,
                        $"Arguments[{inputIndex}]",
                        $"Argument{index}",
                        8,
                        function.Path);
                    inputIndex++;
                }
            }

            string arguments = string.Join(", ", Enumerable.Range(0, function.Parameters.Count)
                .Select(static index => $"Argument{index}"));
            string invocation = FunctionInvocation(type, function, arguments);
            if (function.ReturnType.Kind == UnrealValueKind.Void)
            {
                source.Append("        ").Append(invocation).AppendLine(";");
                source.AppendLine("        OutReturnValue = {};");
            }
            else
            {
                source.Append("        ").Append(CppType(function.ReturnType))
                    .Append(" ReturnValue = ").Append(invocation).AppendLine(";");
                EmitValueWrite(source, function.ReturnType, "ReturnValue", "OutReturnValue", 8);
            }
            int outputIndex = 0;
            for (int index = 0; index < function.Parameters.Count; index++)
            {
                UnrealReflectedParameter parameter = function.Parameters[index];
                if (parameter.Direction == UnrealParameterDirection.Input)
                {
                    continue;
                }
                source.Append("        FUnifiedScriptValue EncodedOutput")
                    .Append(outputIndex).AppendLine(";");
                EmitValueWrite(
                    source,
                    parameter.Type,
                    $"Argument{index}",
                    $"EncodedOutput{outputIndex}",
                    8);
                source.Append("        OutOutputValues.Add(MoveTemp(EncodedOutput")
                    .Append(outputIndex).AppendLine("));");
                outputIndex++;
            }
            source.AppendLine("        Succeed(OutResult);");
            source.AppendLine("        return true;");
            source.AppendLine("    }");
        }
        EmitMemberSwitchEnd(source, functions.Count, "FailUnknownMember(OutResult, MemberId)");
        source.AppendLine("}");
    }

    private static void EmitInvokeInt32x2Dispatcher(
        StringBuilder source,
        IReadOnlyList<(UnrealReflectedType Type, UnrealReflectedFunction Function)> functions,
        string cppNamespace)
    {
        source.AppendLine();
        source.Append("bool ").Append(cppNamespace).AppendLine("::InvokeInt32x2(");
        source.AppendLine("    UObject* Target,");
        source.AppendLine("    uint64 MemberId,");
        source.AppendLine("    int32 Argument0,");
        source.AppendLine("    int32 Argument1,");
        source.AppendLine("    int32& OutReturnValue,");
        source.AppendLine("    FUnifiedScriptInvocationResult& OutResult)");
        source.AppendLine("{");
        source.Append("    using namespace ").Append(cppNamespace).AppendLine("::Private;");
        var scalarFunctions = functions
            .Where(static item => IsInt32x2Signature(item.Function))
            .ToArray();
        if (scalarFunctions.Length == 0)
        {
            source.AppendLine("    return FailUnknownMember(OutResult, MemberId);");
            source.AppendLine("}");
            return;
        }
        source.AppendLine("    switch (MemberId)");
        source.AppendLine("    {");
        foreach ((UnrealReflectedType type, UnrealReflectedFunction function) in
                 scalarFunctions)
        {
            source.Append("    case ").Append(MemberIdLiteral(function.Path)).AppendLine(":");
            source.AppendLine("    {");
            if (!function.Traits.Static)
            {
                EmitResolveFunctionTarget(source, type, 8);
            }
            source.Append("        OutReturnValue = ")
                .Append(FunctionInvocation(type, function, "Argument0, Argument1"))
                .AppendLine(";");
            source.AppendLine("        Succeed(OutResult);");
            source.AppendLine("        return true;");
            source.AppendLine("    }");
        }
        source.AppendLine("    default:");
        source.AppendLine("        return FailUnknownMember(OutResult, MemberId);");
        source.AppendLine("    }");
        source.AppendLine("}");
    }

    private static bool IsInt32x2Signature(UnrealReflectedFunction function) =>
        function.ReturnType.Kind == UnrealValueKind.Int32 &&
        function.Parameters.Count == 2 &&
        function.Parameters.All(static parameter =>
            parameter.Direction == UnrealParameterDirection.Input &&
            parameter.Type.Kind == UnrealValueKind.Int32);

    private static void EmitInvokeFixedDispatcher(
        StringBuilder source,
        IReadOnlyList<(UnrealReflectedType Type, UnrealReflectedFunction Function)> functions,
        string cppNamespace)
    {
        source.AppendLine();
        source.Append("bool ").Append(cppNamespace).AppendLine("::InvokeFixed(");
        source.AppendLine("    UObject* Target,");
        source.AppendLine("    FUnifiedScriptObjectRegistry& Registry,");
        source.AppendLine("    uint64 MemberId,");
        source.AppendLine("    TConstArrayView<FUnifiedScriptNativeValueV3> Arguments,");
        source.AppendLine("    FUnifiedScriptNativeValueV3* OutReturnValue,");
        source.AppendLine("    FUnifiedScriptInvocationResult& OutResult)");
        source.AppendLine("{");
        source.Append("    using namespace ").Append(cppNamespace).AppendLine("::Private;");
        var fixedFunctions = functions
            .Where(static item => IsFixedSignature(item.Function))
            .ToArray();
        if (fixedFunctions.Length == 0)
        {
            source.AppendLine("    return FailUnknownMember(OutResult, MemberId);");
            source.AppendLine("}");
            return;
        }
        source.AppendLine("    switch (MemberId)");
        source.AppendLine("    {");
        foreach ((UnrealReflectedType type, UnrealReflectedFunction function) in
                 fixedFunctions)
        {
            source.Append("    case ").Append(MemberIdLiteral(function.Path)).AppendLine(":");
            source.AppendLine("    {");
            source.Append("        if (Arguments.Num() != ").Append(function.Parameters.Count)
                .AppendLine(")");
            source.AppendLine("        {");
            source.AppendLine("            return Fail(OutResult, EUnifiedScriptInvocationError::ArgumentCountMismatch,");
            source.Append("                TEXT(\"Generated fixed thunk requires ")
                .Append(function.Parameters.Count).AppendLine(" arguments.\"));");
            source.AppendLine("        }");
            if (!function.Traits.Static)
            {
                EmitResolveFunctionTarget(source, type, 8);
            }
            EmitFixedResultValidation(source, function.ReturnType);
            for (int index = 0; index < function.Parameters.Count; index++)
            {
                EmitFixedValueRead(
                    source,
                    function.Parameters[index].Type,
                    $"Arguments[{index}]",
                    $"Argument{index}",
                    8);
            }
            string arguments = string.Join(", ", Enumerable.Range(0, function.Parameters.Count)
                .Select(static index => $"Argument{index}"));
            string invocation = FunctionInvocation(type, function, arguments);
            if (function.ReturnType.Kind == UnrealValueKind.Void)
            {
                source.Append("        ").Append(invocation).AppendLine(";");
            }
            else
            {
                source.Append("        ").Append(CppType(function.ReturnType))
                    .Append(" ReturnValue = ").Append(invocation).AppendLine(";");
                EmitFixedValueWrite(
                    source,
                    function.ReturnType,
                    "ReturnValue",
                    "(*OutReturnValue)",
                    8);
            }
            source.AppendLine("        Succeed(OutResult);");
            source.AppendLine("        return true;");
            source.AppendLine("    }");
        }
        source.AppendLine("    default:");
        source.AppendLine("        return FailUnknownMember(OutResult, MemberId);");
        source.AppendLine("    }");
        source.AppendLine("}");
    }

    private static bool IsFixedSignature(UnrealReflectedFunction function) =>
        function.Parameters.Count <= 4 &&
        IsFixedValueType(function.ReturnType, allowVoid: true) &&
        function.Parameters.All(static parameter =>
            parameter.Direction == UnrealParameterDirection.Input &&
            IsFixedValueType(parameter.Type, allowVoid: false));

    private static bool IsFixedValueType(UnrealTypeReference type, bool allowVoid) =>
        (allowVoid && type.Kind == UnrealValueKind.Void) ||
        type.Kind is UnrealValueKind.Boolean or UnrealValueKind.Int32 or
            UnrealValueKind.Float32 or UnrealValueKind.Object ||
        type.Kind == UnrealValueKind.Struct &&
            type.TypePath == "/Script/CoreUObject.Vector";

    private static void EmitFixedResultValidation(
        StringBuilder source,
        UnrealTypeReference type)
    {
        if (type.Kind == UnrealValueKind.Void)
        {
            source.AppendLine("        if (OutReturnValue != nullptr)");
        }
        else
        {
            source.Append("        if (OutReturnValue == nullptr || OutReturnValue->Kind != ")
                .Append(FixedNativeKind(type)).AppendLine(")");
        }
        source.AppendLine("        {");
        source.AppendLine("            return Fail(OutResult, EUnifiedScriptInvocationError::ValueTypeMismatch,");
        source.AppendLine("                TEXT(\"Generated fixed thunk result descriptor is incompatible.\"));");
        source.AppendLine("        }");
    }

    private static void EmitFixedValueRead(
        StringBuilder source,
        UnrealTypeReference type,
        string expression,
        string variable,
        int indent)
    {
        string padding = new(' ', indent);
        source.Append(padding).Append("if (").Append(expression).Append(".Kind != ")
            .Append(FixedNativeKind(type)).AppendLine(")");
        source.Append(padding).AppendLine("{");
        source.Append(padding).AppendLine("    return Fail(OutResult, EUnifiedScriptInvocationError::ValueTypeMismatch,");
        source.Append(padding).AppendLine("        TEXT(\"Generated fixed argument has an incompatible value kind.\"));");
        source.Append(padding).AppendLine("}");
        switch (type.Kind)
        {
            case UnrealValueKind.Boolean:
                source.Append(padding).Append("bool ").Append(variable).Append(" = ")
                    .Append(expression).AppendLine(".Payload0 != 0;");
                break;
            case UnrealValueKind.Int32:
                source.Append(padding).Append("int32 ").Append(variable)
                    .Append(" = static_cast<int32>(").Append(expression).AppendLine(".Payload0);");
                break;
            case UnrealValueKind.Float32:
                source.Append(padding).Append("const uint32 ").Append(variable).Append("Bits = static_cast<uint32>(")
                    .Append(expression).AppendLine(".Payload0);");
                source.Append(padding).Append("float ").Append(variable).AppendLine(" = 0.0f;");
                source.Append(padding).Append("FMemory::Memcpy(&").Append(variable).Append(", &")
                    .Append(variable).AppendLine("Bits, sizeof(float));");
                break;
            case UnrealValueKind.Object:
                source.Append(padding).Append(CppType(type)).Append(' ').Append(variable)
                    .AppendLine(" = nullptr;");
                source.Append(padding).Append("if (!DecodeFixedObject(Registry, ").Append(expression)
                    .Append(", ").Append(variable).AppendLine(", OutResult))");
                source.Append(padding).AppendLine("{");
                source.Append(padding).AppendLine("    return false;");
                source.Append(padding).AppendLine("}");
                break;
            default:
                for (int component = 0; component < 3; component++)
                {
                    source.Append(padding).Append("double ").Append(variable).Append("Component")
                        .Append(component).AppendLine(" = 0.0;");
                    source.Append(padding).Append("FMemory::Memcpy(&").Append(variable).Append("Component")
                        .Append(component).Append(", &").Append(expression).Append(".Payload")
                        .Append(component).AppendLine(", sizeof(double));");
                }
                source.Append(padding).Append("FVector ").Append(variable).Append("(")
                    .Append(variable).Append("Component0, ").Append(variable).Append("Component1, ")
                    .Append(variable).AppendLine("Component2);");
                break;
        }
    }

    private static void EmitFixedValueWrite(
        StringBuilder source,
        UnrealTypeReference type,
        string expression,
        string destination,
        int indent)
    {
        string padding = new(' ', indent);
        if (type.Kind == UnrealValueKind.Object)
        {
            source.Append(padding).Append("if (!EncodeFixedObject(Registry, GetObjectPointer(")
                .Append(expression).Append("), ").Append(destination).AppendLine(", OutResult))");
            source.Append(padding).AppendLine("{");
            source.Append(padding).AppendLine("    return false;");
            source.Append(padding).AppendLine("}");
            return;
        }
        source.Append(padding).Append(destination).AppendLine(" = {};");
        source.Append(padding).Append(destination).Append(".Kind = ")
            .Append(FixedNativeKind(type)).AppendLine(";");
        if (type.Kind == UnrealValueKind.Boolean)
        {
            source.Append(padding).Append(destination).Append(".Payload0 = ")
                .Append(expression).AppendLine(" ? 1 : 0;");
        }
        else if (type.Kind == UnrealValueKind.Int32)
        {
            source.Append(padding).Append(destination).Append(".Payload0 = static_cast<uint64>(")
                .Append(expression).AppendLine(");");
        }
        else if (type.Kind == UnrealValueKind.Float32)
        {
            source.Append(padding).Append("uint32 ReturnBits = 0;").AppendLine();
            source.Append(padding).Append("FMemory::Memcpy(&ReturnBits, &").Append(expression)
                .AppendLine(", sizeof(float));");
            source.Append(padding).Append(destination).AppendLine(".Payload0 = ReturnBits;");
        }
        else
        {
            string[] components = ["X", "Y", "Z"];
            for (int component = 0; component < components.Length; component++)
            {
                source.Append(padding).Append("const double ReturnComponent").Append(component)
                    .Append(" = ").Append(expression).Append('.').Append(components[component]).AppendLine(";");
                source.Append(padding).Append("FMemory::Memcpy(&").Append(destination).Append(".Payload")
                    .Append(component).Append(", &ReturnComponent").Append(component)
                    .AppendLine(", sizeof(double));");
            }
        }
    }

    private static string FixedNativeKind(UnrealTypeReference type) => type.Kind switch
    {
        UnrealValueKind.Boolean => "EUnifiedScriptNativeValueKindV3::Boolean",
        UnrealValueKind.Int32 => "EUnifiedScriptNativeValueKindV3::Int32",
        UnrealValueKind.Float32 => "EUnifiedScriptNativeValueKindV3::Float32",
        UnrealValueKind.Object => "EUnifiedScriptNativeValueKindV3::Object",
        UnrealValueKind.Struct when type.TypePath == "/Script/CoreUObject.Vector" =>
            "EUnifiedScriptNativeValueKindV3::Vector3",
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    private static void EmitResolveTarget(StringBuilder source, UnrealReflectedType type, int indent)
    {
        string padding = new(' ', indent);
        source.Append(padding).Append(type.NativeName).Append("* TypedTarget = ResolveTarget<")
            .Append(type.NativeName).Append(">(Target, TEXT(")
            .Append(CppStringLiteral(type.Path)).AppendLine("), OutResult);");
        source.Append(padding).AppendLine("if (TypedTarget == nullptr)");
        source.Append(padding).AppendLine("{");
        source.Append(padding).AppendLine("    return false;");
        source.Append(padding).AppendLine("}");
    }

    private static void EmitResolveFunctionTarget(
        StringBuilder source,
        UnrealReflectedType type,
        int indent)
    {
        if (!type.Traits.Interface)
        {
            EmitResolveTarget(source, type, indent);
            return;
        }

        string padding = new(' ', indent);
        source.Append(padding).Append("if (Target == nullptr || !Target->GetClass()->ImplementsInterface(")
            .Append(type.NativeName).AppendLine("::StaticClass()))");
        source.Append(padding).AppendLine("{");
        source.Append(padding).AppendLine("    return Fail(");
        source.Append(padding).AppendLine("        OutResult,");
        source.Append(padding).AppendLine("        EUnifiedScriptInvocationError::TargetTypeMismatch,");
        source.Append(padding).Append("        FString::Printf(TEXT(\"Object '%s' does not implement generated thunk interface '%s'.\"), *GetNameSafe(Target), TEXT(")
            .Append(CppStringLiteral(type.Path)).AppendLine(")));");
        source.Append(padding).AppendLine("}");
    }

    private static string FunctionInvocation(
        UnrealReflectedType type,
        UnrealReflectedFunction function,
        string arguments)
    {
        if (function.NativeBinding is { } binding)
        {
            string parameters = string.Join(", ", function.Parameters.Select((parameter, index) =>
                binding.Parameters[index] switch
                {
                    UnrealNativeParameterPassing.ConstReference => $"{CppType(parameter.Type)} const&",
                    UnrealNativeParameterPassing.Reference => $"{CppType(parameter.Type)}&",
                    _ => CppType(parameter.Type),
                }));
            string pointer = function.Traits.Static
                ? $"{CppType(function.ReturnType)} (*)({parameters})"
                : $"{CppType(function.ReturnType)} ({type.NativeName}::*)({parameters}){(binding.Const ? " const" : "")}";
            string method = $"static_cast<{pointer}>(&{type.NativeName}::{function.Name})";
            return function.Traits.Static
                ? $"({method})({arguments})"
                : $"(TypedTarget->*{method})({arguments})";
        }
        if (function.Traits.Static)
        {
            return $"{type.NativeName}::{function.Name}({arguments})";
        }
        if (!type.Traits.Interface)
        {
            return $"TypedTarget->{function.Name}({arguments})";
        }

        string interfaceName = type.NativeName.Length > 1 && type.NativeName[0] == 'U'
            ? $"I{type.NativeName[1..]}"
            : $"I{type.NativeName}";
        string forwardedArguments = string.IsNullOrEmpty(arguments)
            ? "Target"
            : $"Target, {arguments}";
        return $"{interfaceName}::Execute_{function.Name}({forwardedArguments})";
    }

    private static void EmitValueRead(
        StringBuilder source,
        UnrealTypeReference type,
        string expression,
        string variable,
        int indent,
        string memberPath)
    {
        string padding = new(' ', indent);
        if (type.Kind == UnrealValueKind.Callback)
        {
            source.Append(padding).Append("FScriptDelegate ").Append(variable).AppendLine("Delegate;");
            source.Append(padding).Append("if (!UEUnifiedScript_ResolveCallback(Registry, ").Append(expression)
                .Append(", ").Append(TypeIdLiteral(type.TypePath!)).Append(", ").Append(variable).AppendLine("Delegate, OutResult)) return false;");
            source.Append(padding).Append(CppType(type)).Append(' ').Append(variable).AppendLine(";");
            source.Append(padding).Append(variable).Append(".BindUFunction(").Append(variable).Append("Delegate.GetUObject(), ")
                .Append(variable).AppendLine("Delegate.GetFunctionName());");
            return;
        }
        if (IsContainer(type))
        {
            EmitContainerRead(source, type, expression, variable, indent, memberPath);
            return;
        }
        if (type.Kind == UnrealValueKind.Struct && !IsCoreStruct(type.TypePath))
        {
            source.Append(padding).Append(CppType(type)).Append(' ').Append(variable)
                .Append(" = ").Append(CppDefault(type)).AppendLine(";");
            source.Append(padding).Append("if (!DecodeStruct_")
                .Append(StructCodecSuffix(type.TypePath!)).Append('(')
                .Append(expression).Append(", Registry, ").Append(variable)
                .AppendLine(", OutResult))");
            source.Append(padding).AppendLine("{");
            source.Append(padding).AppendLine("    return false;");
            source.Append(padding).AppendLine("}");
            return;
        }
        if (type.Kind is UnrealValueKind.SoftObject or UnrealValueKind.SoftClass)
        {
            source.Append(padding).Append("FString ").Append(variable).AppendLine("Path;");
            source.Append(padding).Append("if (!").Append(expression).Append(".TryGetString(")
                .Append(variable).AppendLine("Path))");
            source.Append(padding).AppendLine("{");
            source.Append(padding).AppendLine("    return Fail(");
            source.Append(padding).AppendLine("        OutResult,");
            source.Append(padding).AppendLine("        EUnifiedScriptInvocationError::ValueTypeMismatch,");
            source.Append(padding).Append("        TEXT(")
                .Append(CppStringLiteral($"Generated native member '{memberPath}' received an incompatible soft path."))
                .AppendLine("));");
            source.Append(padding).AppendLine("}");
            source.Append(padding).Append(CppType(type)).Append(' ').Append(variable)
                .Append("{FSoftObjectPath(").Append(variable).AppendLine("Path)};");
            return;
        }
        if (type.Kind == UnrealValueKind.Enum)
        {
            source.Append(padding).Append("int64 ").Append(variable).AppendLine("Raw = 0;");
            source.Append(padding).Append("if (!").Append(expression)
                .Append(".TryGetEnumById(").Append(variable).Append("Raw, ")
                .Append(TypeIdLiteral(type.TypePath!)).AppendLine("))");
        }
        else if (IsObject(type))
        {
            source.Append(padding).Append(CppType(type)).Append(' ').Append(variable)
                .AppendLine(" = nullptr;");
            source.Append(padding).Append("if (!DecodeObject(Registry, ").Append(expression)
                .Append(", ").Append(type.Kind == UnrealValueKind.Class ? "true" : "false")
                .Append(", ").Append(variable).AppendLine(", OutResult))");
            source.Append(padding).AppendLine("{");
            source.Append(padding).AppendLine("    return false;");
            source.Append(padding).AppendLine("}");
            return;
        }
        else
        {
            source.Append(padding).Append(CppType(type)).Append(' ').Append(variable)
                .Append(" = ").Append(CppDefault(type)).AppendLine(";");
            source.Append(padding).Append("if (!").Append(expression).Append('.')
                .Append(TryGetMethod(type)).Append('(').Append(variable).AppendLine("))");
        }
        source.Append(padding).AppendLine("{");
        source.Append(padding).AppendLine("    return Fail(");
        source.Append(padding).AppendLine("        OutResult,");
        source.Append(padding).AppendLine("        EUnifiedScriptInvocationError::ValueTypeMismatch,");
        source.Append(padding).Append("        TEXT(")
            .Append(CppStringLiteral($"Generated native member '{memberPath}' received an incompatible value."))
            .AppendLine("));");
        source.Append(padding).AppendLine("}");
        if (type.Kind == UnrealValueKind.Enum)
        {
            string castType = CppType(type);
            if (castType.StartsWith("TEnumAsByte<", StringComparison.Ordinal) && castType.EndsWith('>'))
            {
                castType = castType[12..^1].Trim();
                source.Append(padding).Append("if (").Append(variable).Append("Raw < 0 || ")
                    .Append(variable).AppendLine("Raw > 255)");
                source.Append(padding).AppendLine("{");
                EmitFailure(source, "ValueTypeMismatch", "TEnumAsByte value is outside its byte range.", indent + 4);
                source.Append(padding).AppendLine("}");
            }
            source.Append(padding).Append(CppType(type)).Append(' ').Append(variable)
                .Append(" = static_cast<").Append(castType).Append(">(")
                .Append(variable).AppendLine("Raw);");
        }
    }

    private static void EmitContainerRead(
        StringBuilder source,
        UnrealTypeReference type,
        string expression,
        string variable,
        int indent,
        string memberPath)
    {
        string padding = new(' ', indent);
        string values = variable + "Values";
        source.Append(padding).Append("TConstArrayView<FUnifiedScriptValue> ")
            .Append(values).AppendLine(";");
        source.Append(padding).Append("if (!").Append(expression).Append('.')
            .Append(type.Kind switch
            {
                UnrealValueKind.Array => "TryGetArray",
                UnrealValueKind.Set => "TryGetSet",
                UnrealValueKind.Map => "TryGetMap",
                _ => throw new ArgumentOutOfRangeException(nameof(type)),
            })
            .Append('(').Append(TypeIdLiteral(type.NativeName)).Append(", ")
            .Append(values).AppendLine("))");
        source.Append(padding).AppendLine("{");
        source.Append(padding).AppendLine("    return Fail(");
        source.Append(padding).AppendLine("        OutResult,");
        source.Append(padding).AppendLine("        EUnifiedScriptInvocationError::ValueTypeMismatch,");
        source.Append(padding).Append("        TEXT(")
            .Append(CppStringLiteral($"Generated native member '{memberPath}' received an incompatible container value."))
            .AppendLine("));");
        source.Append(padding).AppendLine("}");
        source.Append(padding).Append(CppType(type)).Append(' ').Append(variable).AppendLine(";");
        if (type.Kind == UnrealValueKind.Array)
        {
            source.Append(padding).Append(variable).Append(".Reserve(").Append(values).AppendLine(".Num());");
            source.Append(padding).Append("for (const FUnifiedScriptValue& ")
                .Append(variable).Append("Cell : ").Append(values).AppendLine(")");
            source.Append(padding).AppendLine("{");
            EmitValueRead(
                source,
                type.ElementType!,
                variable + "Cell",
                variable + "Element",
                indent + 4,
                memberPath);
            source.Append(padding).Append("    ").Append(variable).Append(".Add(MoveTemp(")
                .Append(variable).AppendLine("Element));");
            source.Append(padding).AppendLine("}");
            return;
        }
        if (type.Kind == UnrealValueKind.Set)
        {
            source.Append(padding).Append(variable).Append(".Reserve(").Append(values).AppendLine(".Num());");
            source.Append(padding).Append("for (const FUnifiedScriptValue& ")
                .Append(variable).Append("Cell : ").Append(values).AppendLine(")");
            source.Append(padding).AppendLine("{");
            EmitValueRead(
                source,
                type.ElementType!,
                variable + "Cell",
                variable + "Element",
                indent + 4,
                memberPath);
            source.Append(padding).Append("    ").Append(variable).Append(".Add(MoveTemp(")
                .Append(variable).AppendLine("Element));");
            source.Append(padding).AppendLine("}");
            return;
        }

        source.Append(padding).Append(variable).Append(".Reserve(").Append(values)
            .AppendLine(".Num() / 2);");
        source.Append(padding).Append("for (int32 ").Append(variable).Append("Index = 0; ")
            .Append(variable).Append("Index < ").Append(values).Append(".Num(); ")
            .Append(variable).AppendLine("Index += 2)");
        source.Append(padding).AppendLine("{");
        EmitValueRead(
            source,
            type.KeyType!,
            $"{values}[{variable}Index]",
            variable + "Key",
            indent + 4,
            memberPath);
        EmitValueRead(
            source,
            type.ValueType!,
            $"{values}[{variable}Index + 1]",
            variable + "MappedValue",
            indent + 4,
            memberPath);
        source.Append(padding).Append("    ").Append(variable).Append(".Add(MoveTemp(")
            .Append(variable).Append("Key), MoveTemp(").Append(variable).AppendLine("MappedValue));");
        source.Append(padding).AppendLine("}");
    }

    private static void EmitValueWrite(
        StringBuilder source,
        UnrealTypeReference type,
        string expression,
        string output,
        int indent)
    {
        string padding = new(' ', indent);
        if (IsContainer(type))
        {
            EmitContainerWrite(source, type, expression, output, indent);
            return;
        }
        if (type.Kind == UnrealValueKind.Struct && !IsCoreStruct(type.TypePath))
        {
            source.Append(padding).Append("if (!EncodeStruct_")
                .Append(StructCodecSuffix(type.TypePath!)).Append('(')
                .Append(expression).Append(", Registry, ").Append(output)
                .AppendLine(", OutResult))");
            source.Append(padding).AppendLine("{");
            source.Append(padding).AppendLine("    return false;");
            source.Append(padding).AppendLine("}");
            return;
        }
        if (IsObject(type))
        {
            source.Append(padding).Append("if (!EncodeObject(Registry, GetObjectPointer(")
                .Append(expression).Append(')')
                .Append(", ").Append(type.Kind == UnrealValueKind.Class ? "true" : "false")
                .Append(", ").Append(output).AppendLine(", OutResult))");
            source.Append(padding).AppendLine("{");
            source.Append(padding).AppendLine("    return false;");
            source.Append(padding).AppendLine("}");
            return;
        }
        if (type.Kind is UnrealValueKind.SoftObject or UnrealValueKind.SoftClass)
        {
            source.Append(padding).Append(output).Append(" = FUnifiedScriptValue::FromString(")
                .Append(expression).AppendLine(".ToSoftObjectPath().ToString());");
            return;
        }
        source.Append(padding).Append(output).Append(" = ")
            .Append(FromNative(type, expression)).AppendLine(";");
    }

    private static void EmitContainerWrite(
        StringBuilder source,
        UnrealTypeReference type,
        string expression,
        string output,
        int indent)
    {
        string padding = new(' ', indent);
        string cells = output + "Cells";
        source.Append(padding).Append("TArray<FUnifiedScriptValue> ").Append(cells).AppendLine(";");
        source.Append(padding).Append(cells).Append(".Reserve(").Append(expression).Append(".Num()")
            .AppendLine(type.Kind == UnrealValueKind.Map ? " * 2);" : ");");
        if (type.Kind is UnrealValueKind.Array or UnrealValueKind.Set)
        {
            source.Append(padding).Append("for (const auto& ").Append(output).Append("Element : ")
                .Append(expression).AppendLine(")");
            source.Append(padding).AppendLine("{");
            source.Append(padding).AppendLine("    FUnifiedScriptValue EncodedElement;");
            EmitValueWrite(
                source,
                type.ElementType!,
                output + "Element",
                "EncodedElement",
                indent + 4);
            source.Append(padding).Append("    ").Append(cells)
                .AppendLine(".Add(MoveTemp(EncodedElement));");
            source.Append(padding).AppendLine("}");
        }
        else
        {
            source.Append(padding).Append("for (const auto& ").Append(output).Append("Entry : ")
                .Append(expression).AppendLine(")");
            source.Append(padding).AppendLine("{");
            source.Append(padding).AppendLine("    FUnifiedScriptValue EncodedKey;");
            EmitValueWrite(source, type.KeyType!, output + "Entry.Key", "EncodedKey", indent + 4);
            source.Append(padding).AppendLine("    FUnifiedScriptValue EncodedMappedValue;");
            EmitValueWrite(
                source,
                type.ValueType!,
                output + "Entry.Value",
                "EncodedMappedValue",
                indent + 4);
            source.Append(padding).Append("    ").Append(cells).AppendLine(".Add(MoveTemp(EncodedKey));");
            source.Append(padding).Append("    ").Append(cells)
                .AppendLine(".Add(MoveTemp(EncodedMappedValue));");
            source.Append(padding).AppendLine("}");
        }
        source.Append(padding).Append(output).Append(" = FUnifiedScriptValue::From")
            .Append(type.Kind).Append("(MoveTemp(").Append(cells).Append("), ")
            .Append(TypeIdLiteral(type.NativeName)).AppendLine(");");
    }

    private static void EmitFailure(
        StringBuilder source,
        string error,
        string message,
        int indent)
    {
        string padding = new(' ', indent);
        source.Append(padding).AppendLine("return Fail(");
        source.Append(padding).AppendLine("    OutResult,");
        source.Append(padding).Append("    EUnifiedScriptInvocationError::")
            .Append(error).AppendLine(",");
        source.Append(padding).Append("    TEXT(").Append(CppStringLiteral(message)).AppendLine("));");
    }

    private static void EnsureUniqueMemberIds(
        IEnumerable<(UnrealReflectedType Type, UnrealReflectedProperty Property)> properties,
        IEnumerable<(UnrealReflectedType Type, UnrealReflectedFunction Function)> functions)
    {
        var ids = new Dictionary<ulong, string>();
        foreach (string path in properties.Select(static item => item.Property.Path)
                     .Concat(functions.Select(static item => item.Function.Path)))
        {
            ulong id = UnrealMemberId.FromPath(path);
            if (ids.TryGetValue(id, out string? previous))
            {
                throw new InvalidOperationException(
                    $"Native member ID collision between '{previous}' and '{path}'.");
            }
            ids.Add(id, path);
        }
    }

    private static HashSet<string> GetSupportedStructPaths(
        IReadOnlyList<UnrealReflectedType> structTypes)
    {
        var supported = new HashSet<string>(StringComparer.Ordinal);
        bool changed;
        do
        {
            changed = false;
            foreach (UnrealReflectedType type in structTypes)
            {
                if (supported.Contains(type.Path) ||
                    type.Properties.Count == 0 ||
                    type.Properties.Any(field =>
                        field.Access.NativeAccess != UnrealNativeAccess.Public ||
                        !IsNativeValueType(field.Type, allowVoid: false, supported)))
                {
                    continue;
                }
                supported.Add(type.Path);
                changed = true;
            }
        }
        while (changed);
        return supported;
    }

    private static bool IsNativeValueType(
        UnrealTypeReference type,
        bool allowVoid,
        IReadOnlySet<string> supportedStructPaths) =>
        type.Kind is
            UnrealValueKind.Boolean or
            UnrealValueKind.Int8 or UnrealValueKind.UInt8 or
            UnrealValueKind.Int16 or UnrealValueKind.UInt16 or
            UnrealValueKind.Int32 or UnrealValueKind.UInt32 or
            UnrealValueKind.Int64 or UnrealValueKind.UInt64 or
            UnrealValueKind.Float32 or UnrealValueKind.Float64 or
            UnrealValueKind.String or UnrealValueKind.Name or UnrealValueKind.Text or
            UnrealValueKind.Enum or UnrealValueKind.Object or UnrealValueKind.Class or
            UnrealValueKind.SoftObject or UnrealValueKind.SoftClass or UnrealValueKind.ByteBuffer ||
        type.Kind == UnrealValueKind.Callback && type.CallbackSignature is { } signature &&
            IsNativeValueType(signature.ReturnType, true, supportedStructPaths) &&
            signature.Parameters.All(p => IsNativeValueType(p.Type, false, supportedStructPaths)) ||
        type.Kind == UnrealValueKind.Struct &&
            (IsCoreStruct(type.TypePath) ||
             type.TypePath is not null && supportedStructPaths.Contains(type.TypePath)) ||
        type.Kind is UnrealValueKind.Array or UnrealValueKind.Set &&
            type.ElementType is not null &&
            IsNativeValueType(type.ElementType, allowVoid: false, supportedStructPaths) ||
        type.Kind == UnrealValueKind.Map &&
            type.KeyType is not null && type.ValueType is not null &&
            IsNativeValueType(type.KeyType, allowVoid: false, supportedStructPaths) &&
            IsNativeValueType(type.ValueType, allowVoid: false, supportedStructPaths) ||
        allowVoid && type.Kind == UnrealValueKind.Void;

    private static bool IsCoreStruct(string? typePath) =>
        typePath is
            "/Script/CoreUObject.Vector" or
            "/Script/CoreUObject.Rotator" or
            "/Script/CoreUObject.Transform";

    private static bool IsObject(UnrealTypeReference type) =>
        type.Kind is UnrealValueKind.Object or UnrealValueKind.Class;

    private static bool IsContainer(UnrealTypeReference type) =>
        type.Kind is UnrealValueKind.Array or UnrealValueKind.Set or UnrealValueKind.Map;

    private static string NativeValueKind(UnrealTypeReference type) => type.Kind switch
    {
        UnrealValueKind.Void => "Void",
        UnrealValueKind.Boolean => "Boolean",
        UnrealValueKind.Int8 => "Int8",
        UnrealValueKind.UInt8 => "UInt8",
        UnrealValueKind.Int16 => "Int16",
        UnrealValueKind.UInt16 => "UInt16",
        UnrealValueKind.Int32 => "Int32",
        UnrealValueKind.UInt32 => "UInt32",
        UnrealValueKind.Int64 => "Int64",
        UnrealValueKind.UInt64 => "UInt64",
        UnrealValueKind.Float32 => "Float32",
        UnrealValueKind.Float64 => "Float64",
        UnrealValueKind.String => "String",
        UnrealValueKind.Name => "Name",
        UnrealValueKind.Text => "Text",
        UnrealValueKind.Enum => "Enum",
        UnrealValueKind.Object => "Object",
        UnrealValueKind.Class => "Class",
        UnrealValueKind.SoftObject or UnrealValueKind.SoftClass => "String",
        UnrealValueKind.Struct when type.TypePath == "/Script/CoreUObject.Vector" => "Vector",
        UnrealValueKind.Struct when type.TypePath == "/Script/CoreUObject.Rotator" => "Rotator",
        UnrealValueKind.Struct when type.TypePath == "/Script/CoreUObject.Transform" => "Transform",
        UnrealValueKind.Struct => "Struct",
        UnrealValueKind.Array => "Array",
        UnrealValueKind.Set => "Set",
        UnrealValueKind.Map => "Map",
        UnrealValueKind.ByteBuffer => "ByteBuffer",
        UnrealValueKind.Callback => "Callback",
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    private static string HeaderFor(UnrealReflectedType type)
    {
        if (!string.IsNullOrWhiteSpace(type.HeaderPath))
        {
            return type.HeaderPath!;
        }

        string name = type.NativeName;
        if (name.Length > 1 && name[0] is 'U' or 'A' or 'F')
        {
            name = name[1..];
        }
        return $"{name}.h";
    }

    private static IEnumerable<UnrealTypeReference> EnumerateTypeReferences(
        UnrealTypeReference type)
    {
        yield return type;
        if (type.ElementType is not null)
        {
            foreach (UnrealTypeReference nested in EnumerateTypeReferences(type.ElementType))
            {
                yield return nested;
            }
        }
        if (type.KeyType is not null)
        {
            foreach (UnrealTypeReference nested in EnumerateTypeReferences(type.KeyType))
            {
                yield return nested;
            }
        }
        if (type.ValueType is not null)
        {
            foreach (UnrealTypeReference nested in EnumerateTypeReferences(type.ValueType))
            {
                yield return nested;
            }
        }
        if (type.CallbackSignature is { } signature)
        {
            foreach (var nested in EnumerateTypeReferences(signature.ReturnType)) yield return nested;
            foreach (var parameter in signature.Parameters)
                foreach (var nested in EnumerateTypeReferences(parameter.Type)) yield return nested;
        }
    }

    private static bool IsHeaderPath(string value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.EndsWith(".h", StringComparison.OrdinalIgnoreCase) &&
        !Path.IsPathRooted(value) &&
        !value.Replace('\\', '/').Split('/').Any(static item => item is "" or "." or "..");

    private static bool IsCppNamespace(string value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Split("::", StringSplitOptions.None).All(static component =>
            component.Length > 0 &&
            (char.IsAsciiLetter(component[0]) || component[0] == '_') &&
            component.Skip(1).All(static character =>
                char.IsAsciiLetterOrDigit(character) || character == '_'));

    private static string FromNative(UnrealTypeReference type, string expression) => type.Kind switch
    {
        UnrealValueKind.ByteBuffer => $"FUnifiedScriptValue::FromByteBuffer({expression})",
        UnrealValueKind.Boolean => $"FUnifiedScriptValue::FromBoolean({expression})",
        UnrealValueKind.Int8 => $"FUnifiedScriptValue::FromInt8({expression})",
        UnrealValueKind.UInt8 => $"FUnifiedScriptValue::FromUInt8({expression})",
        UnrealValueKind.Int16 => $"FUnifiedScriptValue::FromInt16({expression})",
        UnrealValueKind.UInt16 => $"FUnifiedScriptValue::FromUInt16({expression})",
        UnrealValueKind.Int32 => $"FUnifiedScriptValue::FromInt32({expression})",
        UnrealValueKind.UInt32 => $"FUnifiedScriptValue::FromUInt32({expression})",
        UnrealValueKind.Int64 => $"FUnifiedScriptValue::FromInt64({expression})",
        UnrealValueKind.UInt64 => $"FUnifiedScriptValue::FromUInt64({expression})",
        UnrealValueKind.Float32 => $"FUnifiedScriptValue::FromFloat32({expression})",
        UnrealValueKind.Float64 => $"FUnifiedScriptValue::FromFloat64({expression})",
        UnrealValueKind.String => $"FUnifiedScriptValue::FromString({expression})",
        UnrealValueKind.Name => $"FUnifiedScriptValue::FromName({expression})",
        UnrealValueKind.Text => $"FUnifiedScriptValue::FromText({expression})",
        UnrealValueKind.SoftObject or UnrealValueKind.SoftClass =>
            $"FUnifiedScriptValue::FromString({expression}.ToSoftObjectPath().ToString())",
        UnrealValueKind.Enum =>
            $"FUnifiedScriptValue::FromEnumTypeId(static_cast<int64>({expression}), {TypeIdLiteral(type.TypePath!)})",
        UnrealValueKind.Struct when type.TypePath == "/Script/CoreUObject.Vector" =>
            $"FUnifiedScriptValue::FromVector({expression})",
        UnrealValueKind.Struct when type.TypePath == "/Script/CoreUObject.Rotator" =>
            $"FUnifiedScriptValue::FromRotator({expression})",
        UnrealValueKind.Struct when type.TypePath == "/Script/CoreUObject.Transform" =>
            $"FUnifiedScriptValue::FromTransform({expression})",
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    private static string CppType(UnrealTypeReference type) => type.Kind switch
    {
        UnrealValueKind.ByteBuffer => "FUnifiedScriptByteBuffer",
        UnrealValueKind.Void => "void",
        UnrealValueKind.Boolean => "bool",
        UnrealValueKind.Int8 => "int8",
        UnrealValueKind.UInt8 => "uint8",
        UnrealValueKind.Int16 => "int16",
        UnrealValueKind.UInt16 => "uint16",
        UnrealValueKind.Int32 => "int32",
        UnrealValueKind.UInt32 => "uint32",
        UnrealValueKind.Int64 => "int64",
        UnrealValueKind.UInt64 => "uint64",
        UnrealValueKind.Float32 => "float",
        UnrealValueKind.Float64 => "double",
        UnrealValueKind.String => "FString",
        UnrealValueKind.Name => "FName",
        UnrealValueKind.Text => "FText",
        UnrealValueKind.Enum or UnrealValueKind.Struct or UnrealValueKind.Callback => type.NativeName,
        UnrealValueKind.Object or UnrealValueKind.Class => CppObjectType(type.NativeName),
        UnrealValueKind.SoftObject or UnrealValueKind.SoftClass => type.NativeName,
        UnrealValueKind.Array or UnrealValueKind.Set or UnrealValueKind.Map => type.NativeName,
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    private static string CppDefault(UnrealTypeReference type) => type.Kind switch
    {
        UnrealValueKind.ByteBuffer => "{}",
        UnrealValueKind.Boolean => "false",
        UnrealValueKind.Int8 or UnrealValueKind.UInt8 or
        UnrealValueKind.Int16 or UnrealValueKind.UInt16 or
        UnrealValueKind.Int32 or UnrealValueKind.UInt32 or
        UnrealValueKind.Int64 or UnrealValueKind.UInt64 => "0",
        UnrealValueKind.Float32 => "0.0f",
        UnrealValueKind.Float64 => "0.0",
        UnrealValueKind.String or UnrealValueKind.Name or UnrealValueKind.Text or
        UnrealValueKind.Struct or UnrealValueKind.Array or UnrealValueKind.Set or UnrealValueKind.Map => "{}",
        UnrealValueKind.SoftObject or UnrealValueKind.SoftClass or UnrealValueKind.Callback => "{}",
        UnrealValueKind.Object or UnrealValueKind.Class => "nullptr",
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    private static string CppObjectType(string nativeName)
    {
        if (nativeName.StartsWith("TSubclassOf<", StringComparison.Ordinal) &&
            nativeName.EndsWith('>'))
        {
            return "UClass*";
        }
        const string objectPtrPrefix = "TObjectPtr<";
        if (nativeName.StartsWith(objectPtrPrefix, StringComparison.Ordinal) &&
            nativeName.EndsWith('>'))
        {
            return nativeName[objectPtrPrefix.Length..^1] + "*";
        }
        return nativeName.EndsWith('*') ? nativeName : nativeName + "*";
    }

    private static string TryGetMethod(UnrealTypeReference type) => type.Kind switch
    {
        UnrealValueKind.ByteBuffer => "TryGetByteBuffer",
        UnrealValueKind.Boolean => "TryGetBoolean",
        UnrealValueKind.Int8 => "TryGetInt8",
        UnrealValueKind.UInt8 => "TryGetUInt8",
        UnrealValueKind.Int16 => "TryGetInt16",
        UnrealValueKind.UInt16 => "TryGetUInt16",
        UnrealValueKind.Int32 => "TryGetInt32",
        UnrealValueKind.UInt32 => "TryGetUInt32",
        UnrealValueKind.Int64 => "TryGetInt64",
        UnrealValueKind.UInt64 => "TryGetUInt64",
        UnrealValueKind.Float32 => "TryGetFloat32",
        UnrealValueKind.Float64 => "TryGetFloat64",
        UnrealValueKind.String => "TryGetString",
        UnrealValueKind.Name => "TryGetName",
        UnrealValueKind.Text => "TryGetText",
        UnrealValueKind.Struct when type.TypePath == "/Script/CoreUObject.Vector" => "TryGetVector",
        UnrealValueKind.Struct when type.TypePath == "/Script/CoreUObject.Rotator" => "TryGetRotator",
        UnrealValueKind.Struct when type.TypePath == "/Script/CoreUObject.Transform" => "TryGetTransform",
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    private static string MemberIdLiteral(string memberPath) =>
        $"0x{UnrealMemberId.FromPath(memberPath):X16}ull";

    private static string TypeIdLiteral(string typePath) =>
        $"0x{UnrealTypeIdV3.FromPath(typePath):X16}ull";

    private static string StructCodecSuffix(string typePath) =>
        $"{UnrealTypeIdV3.FromPath(typePath):X16}";

    private static string CppStringLiteral(string value) =>
        $"\"{value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)}\"";
}
