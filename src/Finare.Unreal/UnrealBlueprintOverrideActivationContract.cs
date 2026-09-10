using System.Buffers;
using System.Collections.ObjectModel;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Finare.Core;

namespace Finare.Unreal;

/// <summary>
/// The native-owned description of the Blueprint override surface expected by
/// one persisted Script Class. It intentionally contains strings and immutable
/// shape data only; no collectible Type or MethodInfo is retained here.
/// </summary>
internal sealed record UnrealBlueprintOverrideActivationShape(
    string Kind,
    string NativeName,
    string TypePath,
    IReadOnlyList<UnrealBlueprintOverrideActivationField>? Fields = null,
    UnrealBlueprintOverrideActivationShape? Element = null,
    UnrealBlueprintOverrideActivationShape? Key = null,
    UnrealBlueprintOverrideActivationShape? Value = null);

internal sealed record UnrealBlueprintOverrideActivationField(
    string NativeName,
    UnrealBlueprintOverrideActivationShape Type);

internal sealed record UnrealBlueprintOverrideActivationParameter(
    string Direction,
    UnrealBlueprintOverrideActivationShape Type);

internal sealed record UnrealBlueprintOverrideActivationRoute(
    string ManagedName,
    string DeclaringClassPath,
    string FunctionName,
    string SuperMode,
    UnrealBlueprintOverrideActivationShape ReturnType,
    IReadOnlyList<UnrealBlueprintOverrideActivationParameter> Parameters)
{
    internal string RouteKey => DeclaringClassPath + "\0" + FunctionName;
}

internal sealed class UnrealBlueprintOverrideActivationException : InvalidOperationException
{
    internal UnrealBlueprintOverrideActivationException(string message)
        : base(message)
    {
    }

    internal UnrealBlueprintOverrideActivationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Exact UTF-8 contract exchanged between the persisted generated Blueprint
/// class and the managed Activator. The parser is deliberately strict so a
/// native producer cannot silently omit a route or a nested type shape.
/// </summary>
internal sealed class UnrealBlueprintOverrideActivationContract
{
    internal const string Schema = "unifiedscript.blueprint-override-contract/v0.1";
    internal const int MaximumUtf8Bytes = 256 * 1024;

    internal static UnrealBlueprintOverrideActivationContract Empty { get; } =
        new(Array.Empty<UnrealBlueprintOverrideActivationRoute>());

    internal IReadOnlyList<UnrealBlueprintOverrideActivationRoute> Overrides { get; }

    private UnrealBlueprintOverrideActivationContract(
        IReadOnlyList<UnrealBlueprintOverrideActivationRoute> overrides)
    {
        Overrides = new ReadOnlyCollection<UnrealBlueprintOverrideActivationRoute>(
            overrides.ToArray());
    }

    internal static UnrealBlueprintOverrideActivationContract Create(Type scriptType)
    {
        ArgumentNullException.ThrowIfNull(scriptType);
        IReadOnlyDictionary<string, UnrealBlueprintOverrideRoute> routes;
        try
        {
            routes = UnrealBlueprintOverrideContract.Create(scriptType);
        }
        catch (UnrealBlueprintOverrideActivationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is InvalidOperationException)
        {
            throw new UnrealBlueprintOverrideActivationException(
                $"The managed type '{scriptType.FullName}' has an invalid Blueprint override declaration: " +
                exception.Message,
                exception);
        }

        var result = new List<UnrealBlueprintOverrideActivationRoute>(routes.Count);
        foreach (UnrealBlueprintOverrideRoute route in routes.Values
                     .OrderBy(static item => item.ManagedMethodIdentity, StringComparer.Ordinal)
                     .ThenBy(static item => item.RouteKey, StringComparer.Ordinal))
        {
            int separator = route.ManagedMethodIdentity.LastIndexOf('.');
            string managedName = separator >= 0
                ? route.ManagedMethodIdentity[(separator + 1)..]
                : route.ManagedMethodIdentity;
            UnrealBlueprintOverrideInvocationContract invocation;
            try
            {
                invocation = UnrealBlueprintOverrideContract.ResolveInvocation(
                    scriptType,
                    managedName);
            }
            catch (Exception exception) when (exception is InvalidOperationException)
            {
                throw new UnrealBlueprintOverrideActivationException(
                    $"Blueprint override '{managedName}' cannot be resolved for activation: " +
                    exception.Message,
                    exception);
            }

            if (!TryCreateShape(
                    invocation.Method.ReturnType,
                    $"Blueprint override '{managedName}' return type",
                    out UnrealBlueprintOverrideActivationShape returnType,
                    out string? shapeError))
            {
                throw new UnrealBlueprintOverrideActivationException(shapeError!);
            }

            var parameters = new List<UnrealBlueprintOverrideActivationParameter>(
                invocation.Parameters.Length);
            foreach (ParameterInfo parameter in invocation.Parameters)
            {
                Type parameterType = parameter.ParameterType;
                Type valueType = parameterType.IsByRef
                    ? parameterType.GetElementType() ?? typeof(void)
                    : parameterType;
                string direction = !parameterType.IsByRef
                    ? "in"
                    : parameter.IsOut
                        ? "out"
                        : "ref";
                if (!TryCreateShape(
                        valueType,
                        $"Blueprint override '{managedName}' parameter '{parameter.Name}'",
                        out UnrealBlueprintOverrideActivationShape parameterShape,
                        out shapeError))
                {
                    throw new UnrealBlueprintOverrideActivationException(shapeError!);
                }
                parameters.Add(new UnrealBlueprintOverrideActivationParameter(
                    direction,
                    parameterShape));
            }

            result.Add(new UnrealBlueprintOverrideActivationRoute(
                managedName,
                route.DeclaringClassPath,
                route.FunctionName,
                route.SuperMode == UnrealBlueprintEventSuperMode.Explicit
                    ? "explicit"
                    : route.SuperMode.ToString(),
                returnType,
                new ReadOnlyCollection<UnrealBlueprintOverrideActivationParameter>(
                    parameters.ToArray())));
        }

        return new UnrealBlueprintOverrideActivationContract(result);
    }

    internal static UnrealBlueprintOverrideActivationContract Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (Encoding.UTF8.GetByteCount(json) > MaximumUtf8Bytes)
        {
            throw new UnrealBlueprintOverrideActivationException(
                $"The Blueprint override activation contract exceeds the {MaximumUtf8Bytes}-byte UTF-8 limit.");
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            return Parse(document.RootElement);
        }
        catch (UnrealBlueprintOverrideActivationException)
        {
            throw;
        }
        catch (JsonException exception)
        {
            throw new UnrealBlueprintOverrideActivationException(
                $"The Blueprint override activation contract is invalid JSON: {exception.Message}",
                exception);
        }
    }

    internal string ToJson(bool writeIndented = false)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(
                   buffer,
                   new JsonWriterOptions { Indented = writeIndented }))
        {
            writer.WriteStartObject();
            writer.WriteString("schema", Schema);
            writer.WriteStartArray("overrides");
            foreach (UnrealBlueprintOverrideActivationRoute route in Overrides)
            {
                writer.WriteStartObject();
                writer.WriteString("managedName", route.ManagedName);
                writer.WriteString("declaringClassPath", route.DeclaringClassPath);
                writer.WriteString("functionName", route.FunctionName);
                writer.WriteString("superMode", route.SuperMode);
                WriteShape(writer, "returnType", route.ReturnType);
                writer.WriteStartArray("parameters");
                foreach (UnrealBlueprintOverrideActivationParameter parameter in route.Parameters)
                {
                    writer.WriteStartObject();
                    writer.WriteString("direction", parameter.Direction);
                    WriteShape(writer, "type", parameter.Type);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        if (buffer.WrittenCount > MaximumUtf8Bytes)
        {
            throw new UnrealBlueprintOverrideActivationException(
                $"The Blueprint override activation contract exceeds the {MaximumUtf8Bytes}-byte UTF-8 limit.");
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    internal void ValidateType(Type scriptType)
    {
        UnrealBlueprintOverrideActivationContract actual = Create(scriptType);
        var actualByRoute = actual.Overrides.ToDictionary(
            static route => route.RouteKey,
            StringComparer.OrdinalIgnoreCase);
        foreach (UnrealBlueprintOverrideActivationRoute expected in Overrides)
        {
            if (!actualByRoute.TryGetValue(expected.RouteKey, out UnrealBlueprintOverrideActivationRoute? candidate))
            {
                throw ContractMismatch(
                    expected,
                    $"route is missing (expected managedName '{expected.ManagedName}')",
                    scriptType);
            }

            if (!string.Equals(expected.ManagedName, candidate.ManagedName, StringComparison.Ordinal))
            {
                throw ContractMismatch(
                    expected,
                    $"managedName expected '{expected.ManagedName}', actual '{candidate.ManagedName}'",
                    scriptType);
            }
            if (!string.Equals(expected.DeclaringClassPath, candidate.DeclaringClassPath, StringComparison.Ordinal))
            {
                throw ContractMismatch(
                    expected,
                    $"declaringClassPath expected '{expected.DeclaringClassPath}', actual '{candidate.DeclaringClassPath}'",
                    scriptType);
            }
            if (!string.Equals(expected.FunctionName, candidate.FunctionName, StringComparison.Ordinal))
            {
                throw ContractMismatch(
                    expected,
                    $"functionName expected '{expected.FunctionName}', actual '{candidate.FunctionName}'",
                    scriptType);
            }
            if (!string.Equals(expected.SuperMode, candidate.SuperMode, StringComparison.Ordinal))
            {
                throw ContractMismatch(
                    expected,
                    $"superMode expected '{expected.SuperMode}', actual '{candidate.SuperMode}'",
                    scriptType);
            }

            string? returnDifference = FindShapeDifference(
                expected.ReturnType,
                candidate.ReturnType,
                "returnType");
            if (returnDifference is not null)
            {
                throw ContractMismatch(expected, returnDifference, scriptType);
            }
            if (expected.Parameters.Count != candidate.Parameters.Count)
            {
                throw ContractMismatch(
                    expected,
                    $"parameters.count expected {expected.Parameters.Count}, actual {candidate.Parameters.Count}",
                    scriptType);
            }
            for (int index = 0; index < expected.Parameters.Count; index++)
            {
                UnrealBlueprintOverrideActivationParameter expectedParameter = expected.Parameters[index];
                UnrealBlueprintOverrideActivationParameter actualParameter = candidate.Parameters[index];
                string parameterPath = $"parameters[{index}]";
                if (!string.Equals(expectedParameter.Direction, actualParameter.Direction, StringComparison.Ordinal))
                {
                    throw ContractMismatch(
                        expected,
                        $"{parameterPath}.direction expected '{expectedParameter.Direction}', " +
                        $"actual '{actualParameter.Direction}'",
                        scriptType);
                }
                string? parameterDifference = FindShapeDifference(
                    expectedParameter.Type,
                    actualParameter.Type,
                    $"{parameterPath}.type");
                if (parameterDifference is not null)
                {
                    throw ContractMismatch(expected, parameterDifference, scriptType);
                }
            }
        }

        var expectedRouteKeys = new HashSet<string>(
            Overrides.Select(static route => route.RouteKey),
            StringComparer.OrdinalIgnoreCase);
        foreach (UnrealBlueprintOverrideActivationRoute candidate in actual.Overrides)
        {
            if (!expectedRouteKeys.Contains(candidate.RouteKey))
            {
                throw ContractMismatch(
                    candidate,
                    $"unexpected route (actual managedName '{candidate.ManagedName}')",
                    scriptType);
            }
        }
    }

    private static UnrealBlueprintOverrideActivationException ContractMismatch(
        UnrealBlueprintOverrideActivationRoute route,
        string difference,
        Type scriptType) =>
        new(
            $"Blueprint override route '{route.DeclaringClassPath}:{route.FunctionName}' {difference} " +
            $"for managed type '{scriptType.FullName}'. " +
            "The generated Script Class must be synchronized before managed activation. " +
            "Stop the affected instance, synchronize the Script Class, and restart it to apply an override contract change.");

    private static string? FindShapeDifference(
        UnrealBlueprintOverrideActivationShape expected,
        UnrealBlueprintOverrideActivationShape actual,
        string path)
    {
        if (!string.Equals(expected.Kind, actual.Kind, StringComparison.Ordinal))
        {
            return $"{path}.kind expected '{expected.Kind}', actual '{actual.Kind}'";
        }

        if ((expected.Fields is null) != (actual.Fields is null))
        {
            return $"{path}.fields expected {ShapePresence(expected.Fields)}, actual {ShapePresence(actual.Fields)}";
        }
        if (expected.Fields is not null && actual.Fields is not null)
        {
            if (expected.Fields.Count != actual.Fields.Count)
            {
                return $"{path}.fields.count expected {expected.Fields.Count}, actual {actual.Fields.Count}";
            }
            for (int index = 0; index < expected.Fields.Count; index++)
            {
                UnrealBlueprintOverrideActivationField expectedField = expected.Fields[index];
                UnrealBlueprintOverrideActivationField actualField = actual.Fields[index];
                string fieldPath = $"{path}.fields[{index}]";
                if (!string.Equals(expectedField.NativeName, actualField.NativeName, StringComparison.OrdinalIgnoreCase))
                {
                    return $"{fieldPath}.nativeName expected '{expectedField.NativeName}', " +
                        $"actual '{actualField.NativeName}'";
                }
                string? fieldDifference = FindShapeDifference(
                    expectedField.Type,
                    actualField.Type,
                    $"{fieldPath}.type");
                if (fieldDifference is not null)
                {
                    return fieldDifference;
                }
            }
        }

        string? elementDifference = FindOptionalShapeDifference(expected.Element, actual.Element, $"{path}.element");
        if (elementDifference is not null) return elementDifference;
        string? keyDifference = FindOptionalShapeDifference(expected.Key, actual.Key, $"{path}.key");
        if (keyDifference is not null) return keyDifference;
        string? valueDifference = FindOptionalShapeDifference(expected.Value, actual.Value, $"{path}.value");
        if (valueDifference is not null) return valueDifference;
        if (!string.Equals(expected.TypePath, actual.TypePath, StringComparison.Ordinal))
        {
            return $"{path}.typePath expected '{expected.TypePath}', actual '{actual.TypePath}'";
        }
        if (!string.Equals(expected.NativeName, actual.NativeName, StringComparison.Ordinal))
        {
            return $"{path}.nativeName expected '{expected.NativeName}', actual '{actual.NativeName}'";
        }
        return null;
    }

    private static string? FindOptionalShapeDifference(
        UnrealBlueprintOverrideActivationShape? expected,
        UnrealBlueprintOverrideActivationShape? actual,
        string path)
    {
        if (expected is null && actual is null) return null;
        if (expected is null) return $"{path} expected absent, actual {DescribeShape(actual!)}";
        if (actual is null) return $"{path} expected {DescribeShape(expected)}, actual absent";
        return FindShapeDifference(expected, actual, path);
    }

    private static string ShapePresence(
        IReadOnlyList<UnrealBlueprintOverrideActivationField>? fields) =>
        fields is null ? "absent" : $"present ({fields.Count} fields)";

    private static string DescribeShape(UnrealBlueprintOverrideActivationShape shape) =>
        $"{shape.Kind}/{shape.NativeName}/{shape.TypePath}";

    private static UnrealBlueprintOverrideActivationContract Parse(JsonElement root)
    {
        RequireObject(root, "root");
        EnsureProperties(root, "root", "schema", "overrides");
        string schema = RequiredString(root, "schema", "root");
        if (!string.Equals(schema, Schema, StringComparison.Ordinal))
        {
            throw new UnrealBlueprintOverrideActivationException(
                $"Unsupported Blueprint override activation contract schema '{schema}'. Expected '{Schema}'.");
        }
        JsonElement overridesElement = RequiredProperty(root, "overrides", "root");
        if (overridesElement.ValueKind != JsonValueKind.Array)
        {
            throw new UnrealBlueprintOverrideActivationException(
                "Blueprint override activation contract property 'overrides' must be an array.");
        }

        var routes = new List<UnrealBlueprintOverrideActivationRoute>();
        var routeKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var managedNames = new HashSet<string>(StringComparer.Ordinal);
        int index = 0;
        foreach (JsonElement element in overridesElement.EnumerateArray())
        {
            string path = $"overrides[{index}]";
            RequireObject(element, path);
            EnsureProperties(
                element,
                path,
                "managedName",
                "declaringClassPath",
                "functionName",
                "superMode",
                "returnType",
                "parameters");
            string managedName = RequiredString(element, "managedName", path);
            if (!ScriptAssetJson.IsValidBlueprintOverrideFunctionName(managedName))
            {
                throw new UnrealBlueprintOverrideActivationException(
                    $"{path}.managedName '{managedName}' is not a valid managed method identifier.");
            }
            if (!managedNames.Add(managedName))
            {
                throw new UnrealBlueprintOverrideActivationException(
                    $"Duplicate managed Blueprint override method name '{managedName}'.");
            }
            string declaringClassPath = RequiredString(element, "declaringClassPath", path);
            if (!ScriptAssetJson.IsValidBlueprintOverrideClassPath(declaringClassPath))
            {
                throw new UnrealBlueprintOverrideActivationException(
                    $"{path}.declaringClassPath '{declaringClassPath}' is not a valid Unreal class object path.");
            }
            string functionName = RequiredString(element, "functionName", path);
            if (!ScriptAssetJson.IsValidBlueprintOverrideFunctionName(functionName))
            {
                throw new UnrealBlueprintOverrideActivationException(
                    $"{path}.functionName '{functionName}' is not a valid Unreal function identifier.");
            }
            string routeKey = declaringClassPath + "\0" + functionName;
            if (!routeKeys.Add(routeKey))
            {
                throw new UnrealBlueprintOverrideActivationException(
                    $"Duplicate Blueprint override route '{declaringClassPath}:{functionName}' (case-insensitive).");
            }
            string superMode = RequiredString(element, "superMode", path);
            if (!string.Equals(superMode, "explicit", StringComparison.Ordinal))
            {
                throw new UnrealBlueprintOverrideActivationException(
                    $"{path}.superMode must be 'explicit'.");
            }
            UnrealBlueprintOverrideActivationShape returnType = ParseShape(
                RequiredProperty(element, "returnType", path),
                $"{path}.returnType",
                depth: 0,
                allowVoid: true);
            JsonElement parametersElement = RequiredProperty(element, "parameters", path);
            if (parametersElement.ValueKind != JsonValueKind.Array)
            {
                throw new UnrealBlueprintOverrideActivationException(
                    $"{path}.parameters must be an array.");
            }
            if (parametersElement.GetArrayLength() > UnrealManagedFunctionDispatcherV2.MaximumArgumentCount)
            {
                throw new UnrealBlueprintOverrideActivationException(
                    $"{path}.parameters contains more than the supported " +
                    $"{UnrealManagedFunctionDispatcherV2.MaximumArgumentCount} parameters.");
            }
            var parameters = new List<UnrealBlueprintOverrideActivationParameter>();
            int parameterIndex = 0;
            foreach (JsonElement parameterElement in parametersElement.EnumerateArray())
            {
                string parameterPath = $"{path}.parameters[{parameterIndex}]";
                RequireObject(parameterElement, parameterPath);
                EnsureProperties(parameterElement, parameterPath, "direction", "type");
                string direction = RequiredString(parameterElement, "direction", parameterPath);
                if (direction is not ("in" or "ref" or "out"))
                {
                    throw new UnrealBlueprintOverrideActivationException(
                        $"{parameterPath}.direction must be 'in', 'ref', or 'out'.");
                }
                parameters.Add(new UnrealBlueprintOverrideActivationParameter(
                    direction,
                    ParseShape(
                        RequiredProperty(parameterElement, "type", parameterPath),
                        $"{parameterPath}.type",
                        depth: 0,
                        allowVoid: false)));
                parameterIndex++;
            }

            routes.Add(new UnrealBlueprintOverrideActivationRoute(
                managedName,
                declaringClassPath,
                functionName,
                superMode,
                returnType,
                new ReadOnlyCollection<UnrealBlueprintOverrideActivationParameter>(
                    parameters.ToArray())));
            index++;
        }
        return new UnrealBlueprintOverrideActivationContract(routes);
    }

    private static UnrealBlueprintOverrideActivationShape ParseShape(
        JsonElement element,
        string path,
        int depth,
        bool allowVoid)
    {
        if (depth > 16)
        {
            throw InvalidShape(path, "nested shape depth exceeds the ABI recursion depth of 16");
        }
        RequireObject(element, path);
        EnsureProperties(
            element,
            path,
            "kind",
            "nativeName",
            "typePath",
            "fields",
            "element",
            "key",
            "value");
        string kind = RequiredString(element, "kind", path);
        string nativeName = RequiredString(element, "nativeName", path);
        string typePath = RequiredString(element, "typePath", path);
        switch (kind)
        {
            case "void":
                if (!allowVoid)
                    throw InvalidShape(path, "void is only valid as a return type");
                RequireEmptyShapeMetadata(path, nativeName, typePath, element, "void");
                return new UnrealBlueprintOverrideActivationShape(kind, nativeName, typePath);
            case "value":
                if (nativeName.Length == 0 || typePath.Length != 0)
                    throw InvalidShape(path, "value requires nativeName and an empty typePath");
                RequireNoShapeChildren(element, path);
                return new UnrealBlueprintOverrideActivationShape(kind, nativeName, typePath);
            case "enum":
                if (nativeName.Length == 0 || typePath.Length == 0)
                    throw InvalidShape(path, "enum requires nativeName and typePath");
                RequireNoShapeChildren(element, path);
                return new UnrealBlueprintOverrideActivationShape(kind, nativeName, typePath);
            case "struct":
                if (nativeName.Length == 0 || typePath.Length == 0)
                    throw InvalidShape(path, "struct requires nativeName and typePath");
                JsonElement fieldsElement = RequiredProperty(element, "fields", path);
                if (fieldsElement.ValueKind != JsonValueKind.Array)
                    throw InvalidShape(path, "struct fields must be an array");
                if (fieldsElement.GetArrayLength() == 0)
                    throw InvalidShape(path, "struct fields must contain at least one field");
                var fields = new List<UnrealBlueprintOverrideActivationField>();
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                int fieldIndex = 0;
                foreach (JsonElement fieldElement in fieldsElement.EnumerateArray())
                {
                    string fieldPath = $"{path}.fields[{fieldIndex}]";
                    RequireObject(fieldElement, fieldPath);
                    EnsureProperties(fieldElement, fieldPath, "nativeName", "type");
                    string fieldName = RequiredString(fieldElement, "nativeName", fieldPath);
                    if (fieldName.Length == 0 || !names.Add(fieldName))
                        throw InvalidShape(fieldPath, "struct field nativeName must be unique and non-empty");
                    fields.Add(new UnrealBlueprintOverrideActivationField(
                        fieldName,
                        ParseShape(
                            RequiredProperty(fieldElement, "type", fieldPath),
                            $"{fieldPath}.type",
                            depth + 1,
                            allowVoid: false)));
                    fieldIndex++;
                }
                RequireAbsent(element, path, "element", "key", "value");
                return new UnrealBlueprintOverrideActivationShape(
                    kind,
                    nativeName,
                    typePath,
                    new ReadOnlyCollection<UnrealBlueprintOverrideActivationField>(fields.ToArray()));
            case "array":
            case "set":
                if (nativeName.Length == 0 || typePath.Length != 0)
                    throw InvalidShape(path, $"{kind} requires nativeName and an empty typePath");
                RequireAbsent(element, path, "fields", "key", "value");
                return new UnrealBlueprintOverrideActivationShape(
                    kind,
                    nativeName,
                    typePath,
                    Element: ParseShape(
                        RequiredProperty(element, "element", path),
                        $"{path}.element",
                        depth + 1,
                        allowVoid: false));
            case "map":
                if (nativeName.Length == 0 || typePath.Length != 0)
                    throw InvalidShape(path, "map requires nativeName and an empty typePath");
                RequireAbsent(element, path, "fields", "element");
                return new UnrealBlueprintOverrideActivationShape(
                    kind,
                    nativeName,
                    typePath,
                    Key: ParseShape(
                        RequiredProperty(element, "key", path),
                        $"{path}.key",
                        depth + 1,
                        allowVoid: false),
                    Value: ParseShape(
                        RequiredProperty(element, "value", path),
                        $"{path}.value",
                        depth + 1,
                        allowVoid: false));
            default:
                throw InvalidShape(path, $"unknown shape kind '{kind}'");
        }
    }

    private static bool TryCreateShape(
        Type type,
        string context,
        out UnrealBlueprintOverrideActivationShape shape,
        out string? error)
    {
        if (UnrealNativeValueCodecV3.TryCreateBlueprintOverrideShape(
                type,
                out shape,
                out error))
        {
            return true;
        }
        shape = null!;
        error = $"{context} has an unsupported Blueprint ABI shape: {error}";
        return false;
    }

    private static void WriteShape(
        Utf8JsonWriter writer,
        string propertyName,
        UnrealBlueprintOverrideActivationShape shape)
    {
        writer.WritePropertyName(propertyName);
        writer.WriteStartObject();
        writer.WriteString("kind", shape.Kind);
        writer.WriteString("nativeName", shape.NativeName);
        writer.WriteString("typePath", shape.TypePath);
        if (shape.Fields is not null)
        {
            writer.WriteStartArray("fields");
            foreach (UnrealBlueprintOverrideActivationField field in shape.Fields)
            {
                writer.WriteStartObject();
                writer.WriteString("nativeName", field.NativeName);
                WriteShape(writer, "type", field.Type);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
        if (shape.Element is not null) WriteShape(writer, "element", shape.Element);
        if (shape.Key is not null) WriteShape(writer, "key", shape.Key);
        if (shape.Value is not null) WriteShape(writer, "value", shape.Value);
        writer.WriteEndObject();
    }

    private static void EnsureProperties(JsonElement element, string path, params string[] allowed)
    {
        var allowedSet = new HashSet<string>(allowed, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (!allowedSet.Contains(property.Name))
                throw new UnrealBlueprintOverrideActivationException(
                    $"{path} contains unknown property '{property.Name}'.");
            if (!seen.Add(property.Name))
                throw new UnrealBlueprintOverrideActivationException(
                    $"{path} contains duplicate property '{property.Name}'.");
        }
    }

    private static string RequiredString(JsonElement element, string name, string path)
    {
        JsonElement value = RequiredProperty(element, name, path);
        if (value.ValueKind != JsonValueKind.String)
            throw new UnrealBlueprintOverrideActivationException(
                $"{path}.{name} must be a string.");
        return value.GetString() ?? string.Empty;
    }

    private static JsonElement RequiredProperty(JsonElement element, string name, string path)
    {
        if (!element.TryGetProperty(name, out JsonElement value))
            throw new UnrealBlueprintOverrideActivationException(
                $"{path}.{name} is required.");
        return value;
    }

    private static void RequireObject(JsonElement element, string path)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new UnrealBlueprintOverrideActivationException($"{path} must be an object.");
    }

    private static void RequireNoShapeChildren(JsonElement element, string path) =>
        RequireAbsent(element, path, "fields", "element", "key", "value");

    private static void RequireEmptyShapeMetadata(
        string path,
        string nativeName,
        string typePath,
        JsonElement element,
        string kind)
    {
        if (nativeName.Length != 0 || typePath.Length != 0)
            throw InvalidShape(path, $"{kind} requires empty nativeName and typePath");
        RequireNoShapeChildren(element, path);
    }

    private static void RequireAbsent(JsonElement element, string path, params string[] names)
    {
        foreach (string name in names)
        {
            if (element.TryGetProperty(name, out _))
                throw InvalidShape(path, $"property '{name}' is not valid for this shape kind");
        }
    }

    private static UnrealBlueprintOverrideActivationException InvalidShape(
        string path,
        string detail) =>
        new($"Invalid Blueprint override shape at {path}: {detail}.");
}
