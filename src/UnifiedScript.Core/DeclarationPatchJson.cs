using System.Text.Json;
using System.Text.Json.Serialization;

namespace UnifiedScript.Core;

public static class DeclarationPatchJson
{
    public static string Serialize(DeclarationPatchDocument document, bool writeIndented = true)
    {
        DeclarationPatchDocument normalized = NormalizeAndValidate(document);
        return JsonSerializer.Serialize(normalized, CreateOptions(writeIndented));
    }

    public static DeclarationPatchDocument Deserialize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        DeclarationPatchDocument? document = JsonSerializer.Deserialize<DeclarationPatchDocument>(
            json,
            CreateOptions(writeIndented: true));
        if (document is null)
        {
            throw new JsonException("The declaration patch document was empty.");
        }

        return NormalizeAndValidate(document);
    }

    public static void Validate(DeclarationPatchDocument document) =>
        _ = NormalizeAndValidate(document);

    public static DeclarationPatchDocument NormalizeAndValidate(DeclarationPatchDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.SchemaVersion != DeclarationPatchDocument.CurrentSchemaVersion &&
            document.SchemaVersion != DeclarationPatchDocument.EventSchemaVersion &&
            document.SchemaVersion != DeclarationPatchDocument.LegacySchemaVersion)
        {
            throw new JsonException(
                $"Unsupported declaration patch schema '{document.SchemaVersion}'. " +
                $"Expected '{DeclarationPatchDocument.CurrentSchemaVersion}'.");
        }

        if (document.SourceCompilationId.IsEmpty || document.SemanticConfigurationId.IsEmpty ||
            document.Operations is null || document.Operations.Count == 0)
        {
            throw new JsonException(
                "A declaration patch requires source/configuration IDs and at least one operation.");
        }

        var assetTargets = new HashSet<SemanticId>();
        var stateTargets = new HashSet<SemanticId>();
        var functionTargets = new HashSet<SemanticId>();
        var normalized = new List<DeclarationPatchOperation>(document.Operations.Count);
        foreach (DeclarationPatchOperation? operation in document.Operations)
        {
            switch (operation)
            {
                case AddScriptAssetPatchOperation addAsset:
                    if (string.IsNullOrWhiteSpace(addAsset.TargetSourcePath))
                    {
                        throw new JsonException(
                            "An add-asset operation requires a target C# source path.");
                    }
                    normalized.Add(addAsset with
                    {
                        TargetSourcePath = addAsset.TargetSourcePath.Replace('\\', '/'),
                        Asset = NormalizeAssetSpec(addAsset.Asset),
                    });
                    break;
                case UpdateScriptAssetPatchOperation updateAsset:
                    ValidateUniqueTarget(
                        updateAsset.TargetAssetId,
                        assetTargets,
                        "update-asset",
                        "asset");
                    ScriptAssetPatchPrecondition expectedAsset = NormalizeAssetExpected(
                        updateAsset.Expected,
                        "updated asset precondition");
                    string replacementAssetPath = NormalizeAssetPath(
                        updateAsset.ReplacementAssetPath,
                        "updated asset path");
                    if (expectedAsset.AssetPath == replacementAssetPath)
                    {
                        throw new JsonException(
                            "An update-asset operation must change the asset path.");
                    }
                    normalized.Add(updateAsset with
                    {
                        Expected = expectedAsset,
                        ReplacementAssetPath = replacementAssetPath,
                    });
                    break;
                case RemoveScriptAssetPatchOperation removeAsset:
                    ValidateUniqueTarget(
                        removeAsset.TargetAssetId,
                        assetTargets,
                        "remove-asset",
                        "asset");
                    normalized.Add(removeAsset with
                    {
                        Expected = NormalizeAssetExpected(
                            removeAsset.Expected,
                            "removed asset precondition"),
                    });
                    break;
                case AddScriptStatePatchOperation add:
                    if (add.TargetAssetId.IsEmpty)
                    {
                        throw new JsonException("An add-state operation requires a target asset ID.");
                    }

                    normalized.Add(add with { State = NormalizeSpec(add.State, "added state") });
                    break;
                case UpdateScriptStatePatchOperation update:
                    ValidateUniqueStateTarget(update.TargetStateId, stateTargets, "update-state");
                    ScriptStatePatchPrecondition expected = NormalizeExpected(
                        update.Expected,
                        "updated state precondition");
                    ScriptStatePatchSpec replacement = NormalizeSpec(
                        update.Replacement,
                        "updated state replacement");
                    if (IsNoOp(expected, replacement))
                    {
                        throw new JsonException("An update-state operation must change the state contract.");
                    }

                    normalized.Add(update with
                    {
                        Expected = expected,
                        Replacement = replacement,
                    });
                    break;
                case RemoveScriptStatePatchOperation remove:
                    ValidateUniqueStateTarget(remove.TargetStateId, stateTargets, "remove-state");
                    normalized.Add(remove with
                    {
                        Expected = NormalizeExpected(remove.Expected, "removed state precondition"),
                    });
                    break;
                case AddScriptFunctionPatchOperation addFunction:
                    if (addFunction.TargetAssetId.IsEmpty)
                    {
                        throw new JsonException(
                            "An add-function operation requires a target asset ID.");
                    }
                    normalized.Add(addFunction with
                    {
                        Function = NormalizeFunctionSpec(addFunction.Function, "added function"),
                    });
                    break;
                case UpdateScriptFunctionPatchOperation updateFunction:
                    ValidateUniqueTarget(
                        updateFunction.TargetFunctionId,
                        functionTargets,
                        "update-function",
                        "function");
                    ScriptFunctionPatchPrecondition expectedFunction = NormalizeFunctionExpected(
                        updateFunction.Expected,
                        "updated function precondition");
                    ScriptFunctionPatchSpec replacementFunction = NormalizeFunctionSpec(
                        updateFunction.Replacement,
                        "updated function replacement");
                    if (IsNoOp(expectedFunction, replacementFunction))
                    {
                        throw new JsonException(
                            "An update-function operation must change the function contract.");
                    }
                    normalized.Add(updateFunction with
                    {
                        Expected = expectedFunction,
                        Replacement = replacementFunction,
                    });
                    break;
                case RemoveScriptFunctionPatchOperation removeFunction:
                    ValidateUniqueTarget(
                        removeFunction.TargetFunctionId,
                        functionTargets,
                        "remove-function",
                        "function");
                    normalized.Add(removeFunction with
                    {
                        Expected = NormalizeFunctionExpected(
                            removeFunction.Expected,
                            "removed function precondition"),
                    });
                    break;
                case AddUnrealLifecycleHookPatchOperation addLifecycle:
                    if (addLifecycle.TargetAssetId.IsEmpty ||
                        addLifecycle.Hook is not ("beginPlay" or "tick" or "endPlay"))
                    {
                        throw new JsonException(
                            "An Unreal lifecycle hook requires a target asset ID and " +
                            "beginPlay, tick, or endPlay hook.");
                    }
                    normalized.Add(addLifecycle);
                    break;
                case AddUnrealEventHandlerPatchOperation addEvent:
                    if (addEvent.TargetAssetId.IsEmpty)
                    {
                        throw new JsonException(
                            "An add-Unreal-event-handler operation requires a target asset ID.");
                    }
                    normalized.Add(addEvent with
                    {
                        Handler = NormalizeEventHandler(addEvent.Handler),
                    });
                    break;
                case null:
                    throw new JsonException("Declaration patch operations cannot be null.");
                default:
                    throw new JsonException(
                        $"Unsupported declaration patch operation '{operation.GetType().Name}'.");
            }
        }

        return document with
        {
            SchemaVersion = DeclarationPatchDocument.CurrentSchemaVersion,
            Operations = normalized,
        };
    }

    private static ScriptAssetPatchPrecondition NormalizeAssetExpected(
        ScriptAssetPatchPrecondition? expected,
        string role)
    {
        if (expected is null || string.IsNullOrWhiteSpace(expected.QualifiedName))
        {
            throw new JsonException($"The {role} has invalid required fields.");
        }
        return expected with
        {
            QualifiedName = expected.QualifiedName.Trim(),
            AssetPath = NormalizeAssetPath(expected.AssetPath, role),
        };
    }

    private static ScriptAssetPatchSpec NormalizeAssetSpec(ScriptAssetPatchSpec? spec)
    {
        if (spec is null || string.IsNullOrWhiteSpace(spec.Name))
        {
            throw new JsonException("An added Script Asset requires a C# type name.");
        }
        if (spec.BaseTypeName is not null && string.IsNullOrWhiteSpace(spec.BaseTypeName))
        {
            throw new JsonException("An added Script Asset base type cannot be blank.");
        }
        return spec with
        {
            Namespace = spec.Namespace?.Trim() ?? string.Empty,
            Name = spec.Name.Trim(),
            AssetPath = NormalizeAssetPath(spec.AssetPath, "added asset"),
            BaseTypeName = spec.BaseTypeName?.Trim(),
        };
    }

    private static string NormalizeAssetPath(string? path, string role)
    {
        string normalized = path?.Replace('\\', '/').Trim('/') ?? string.Empty;
        if (normalized.Length == 0 || normalized.Split('/').Any(
                static segment => segment.Length == 0 || segment is "." or ".."))
        {
            throw new JsonException(
                $"The {role} requires a non-empty relative path without '.' or '..' segments.");
        }
        return normalized;
    }

    private static ScriptFunctionPatchPrecondition NormalizeFunctionExpected(
        ScriptFunctionPatchPrecondition? expected,
        string role)
    {
        if (expected is null || string.IsNullOrWhiteSpace(expected.Name) ||
            string.IsNullOrWhiteSpace(expected.ReturnTypeMetadataName) ||
            expected.Parameters is null)
        {
            throw new JsonException($"The {role} has invalid required fields.");
        }
        var names = new HashSet<string>(StringComparer.Ordinal);
        ScriptFunctionParameterPatchPrecondition[] parameters = expected.Parameters
            .Select(parameter => parameter is null || string.IsNullOrWhiteSpace(parameter.Name) ||
                string.IsNullOrWhiteSpace(parameter.TypeMetadataName) || !names.Add(parameter.Name)
                    ? throw new JsonException(
                        $"The {role} parameters require unique names and metadata type names.")
                    : parameter with
                    {
                        Name = parameter.Name.Trim(),
                        TypeMetadataName = parameter.TypeMetadataName.Trim(),
                    })
            .ToArray();
        return expected with
        {
            Name = expected.Name.Trim(),
            ReturnTypeMetadataName = expected.ReturnTypeMetadataName.Trim(),
            Parameters = parameters,
        };
    }

    private static ScriptFunctionPatchSpec NormalizeFunctionSpec(
        ScriptFunctionPatchSpec? spec,
        string role)
    {
        if (spec is null || string.IsNullOrWhiteSpace(spec.Name) ||
            string.IsNullOrWhiteSpace(spec.ReturnTypeName) || spec.Parameters is null)
        {
            throw new JsonException($"The {role} has invalid required fields.");
        }
        var names = new HashSet<string>(StringComparer.Ordinal);
        ScriptFunctionParameterPatchSpec[] parameters = spec.Parameters
            .Select(parameter => parameter is null || string.IsNullOrWhiteSpace(parameter.Name) ||
                string.IsNullOrWhiteSpace(parameter.TypeName) || !names.Add(parameter.Name)
                    ? throw new JsonException(
                        $"The {role} parameters require unique names and C# type names.")
                    : parameter with
                    {
                        Name = parameter.Name.Trim(),
                        TypeName = parameter.TypeName.Trim(),
                    })
            .ToArray();
        return spec with
        {
            Name = spec.Name.Trim(),
            ReturnTypeName = spec.ReturnTypeName.Trim(),
            Parameters = parameters,
        };
    }

    private static UnrealEventHandlerPatchSpec NormalizeEventHandler(
        UnrealEventHandlerPatchSpec? handler)
    {
        if (handler is null || string.IsNullOrWhiteSpace(handler.Name) ||
            string.IsNullOrWhiteSpace(handler.SourceTypeName) ||
            string.IsNullOrWhiteSpace(handler.EventPath) ||
            handler.Source is not ("target" or "host") || handler.Parameters is null)
        {
            throw new JsonException(
                "An Unreal Event Entry requires a name, source wrapper, canonical event path, " +
                "target/host source, and parameters.");
        }
        if (handler.SourceMember is not null && string.IsNullOrWhiteSpace(handler.SourceMember))
        {
            throw new JsonException("An Unreal Event Entry source member cannot be blank.");
        }
        var parameterNames = new HashSet<string>(StringComparer.Ordinal);
        UnrealEventHandlerParameterPatchSpec[] parameters = handler.Parameters
            .Select(parameter => parameter is null || string.IsNullOrWhiteSpace(parameter.Name) ||
                string.IsNullOrWhiteSpace(parameter.TypeName) || !parameterNames.Add(parameter.Name)
                    ? throw new JsonException(
                        "Unreal Event Entry parameters require unique names and type names.")
                    : parameter)
            .ToArray();
        return handler with { Parameters = parameters };
    }

    private static ScriptStatePatchPrecondition NormalizeExpected(
        ScriptStatePatchPrecondition? expected,
        string role)
    {
        if (expected is null || string.IsNullOrWhiteSpace(expected.Name) ||
            string.IsNullOrWhiteSpace(expected.TypeMetadataName) || expected.Exposure is null)
        {
            throw new JsonException($"The {role} has invalid required fields.");
        }

        ValidateStateValue(expected.TypeKind, expected.DefaultValue, role);
        ValidateExposure(expected.Exposure, role);
        return expected with { DefaultValue = expected.DefaultValue.Clone() };
    }

    private static ScriptStatePatchSpec NormalizeSpec(ScriptStatePatchSpec? spec, string role)
    {
        if (spec is null || string.IsNullOrWhiteSpace(spec.Name) ||
            string.IsNullOrWhiteSpace(spec.TypeName) || spec.Exposure is null)
        {
            throw new JsonException($"The {role} has invalid required fields.");
        }

        ValidateStateValue(spec.TypeKind, spec.DefaultValue, role);
        ValidateExposure(spec.Exposure, role);
        return spec with { DefaultValue = spec.DefaultValue.Clone() };
    }

    private static void ValidateStateValue(
        ScriptStateTypeKind typeKind,
        JsonElement value,
        string role)
    {
        if (!Enum.IsDefined(typeKind) || value.ValueKind == JsonValueKind.Undefined)
        {
            throw new JsonException($"The {role} has an invalid type kind or default value.");
        }

        bool valid = typeKind switch
        {
            ScriptStateTypeKind.Boolean => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            ScriptStateTypeKind.Integer => value.ValueKind == JsonValueKind.Number &&
                (value.TryGetInt64(out _) || value.TryGetUInt64(out _)),
            ScriptStateTypeKind.FloatingPoint => value.ValueKind == JsonValueKind.Number &&
                value.TryGetDouble(out double number) && double.IsFinite(number),
            ScriptStateTypeKind.String => value.ValueKind is JsonValueKind.String or JsonValueKind.Null,
            ScriptStateTypeKind.Enum => value.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(value.GetString()),
            ScriptStateTypeKind.Name or ScriptStateTypeKind.Text =>
                value.ValueKind == JsonValueKind.String,
            ScriptStateTypeKind.Object or ScriptStateTypeKind.Class =>
                value.ValueKind == JsonValueKind.Null,
            ScriptStateTypeKind.Struct => IsFiniteVectorValue(value),
            _ => false,
        };
        if (!valid)
        {
            throw new JsonException(
                $"The {role} default is incompatible with state kind '{typeKind}'.");
        }
    }

    private static bool IsFiniteVectorValue(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object ||
            value.EnumerateObject().Count() != 3)
        {
            return false;
        }
        return TryGetFiniteNumber(value, "x") &&
            TryGetFiniteNumber(value, "y") &&
            TryGetFiniteNumber(value, "z");
    }

    private static bool TryGetFiniteNumber(JsonElement value, string name) =>
        value.TryGetProperty(name, out JsonElement component) &&
        component.ValueKind == JsonValueKind.Number &&
        component.TryGetDouble(out double number) && double.IsFinite(number);

    private static void ValidateExposure(ScriptExposure exposure, string role)
    {
        if (exposure.Category is not null && string.IsNullOrWhiteSpace(exposure.Category))
        {
            throw new JsonException($"The {role} exposure category cannot be blank.");
        }
    }

    private static void ValidateUniqueStateTarget(
        SemanticId target,
        ISet<SemanticId> targets,
        string operation) =>
        ValidateUniqueTarget(target, targets, operation, "state");

    private static void ValidateUniqueTarget(
        SemanticId target,
        ISet<SemanticId> targets,
        string operation,
        string targetKind)
    {
        if (target.IsEmpty || !targets.Add(target))
        {
            throw new JsonException(
                $"A {operation} operation requires a unique non-empty target {targetKind} ID.");
        }
    }

    private static bool IsNoOp(
        ScriptStatePatchPrecondition expected,
        ScriptStatePatchSpec replacement) =>
        string.Equals(expected.Name, replacement.Name, StringComparison.Ordinal) &&
        IsSameType(expected.TypeMetadataName, replacement.TypeName) &&
        expected.TypeKind == replacement.TypeKind &&
        JsonElement.DeepEquals(expected.DefaultValue, replacement.DefaultValue) &&
        expected.Exposure == replacement.Exposure;

    private static bool IsNoOp(
        ScriptFunctionPatchPrecondition expected,
        ScriptFunctionPatchSpec replacement) =>
        string.Equals(expected.Name, replacement.Name, StringComparison.Ordinal) &&
        IsSameType(expected.ReturnTypeMetadataName, replacement.ReturnTypeName) &&
        expected.IsStatic == replacement.IsStatic &&
        expected.Parameters.Count == replacement.Parameters.Count &&
        expected.Parameters.Zip(replacement.Parameters).All(pair =>
            pair.First.Name == pair.Second.Name &&
            IsSameType(pair.First.TypeMetadataName, pair.Second.TypeName));

    private static bool IsSameType(string metadataName, string sourceName)
    {
        IReadOnlyDictionary<string, string> aliases = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["bool"] = "System.Boolean",
            ["byte"] = "System.Byte",
            ["sbyte"] = "System.SByte",
            ["short"] = "System.Int16",
            ["ushort"] = "System.UInt16",
            ["int"] = "System.Int32",
            ["uint"] = "System.UInt32",
            ["long"] = "System.Int64",
            ["ulong"] = "System.UInt64",
            ["float"] = "System.Single",
            ["double"] = "System.Double",
            ["decimal"] = "System.Decimal",
            ["string"] = "System.String",
        };
        string normalized = aliases.GetValueOrDefault(sourceName, sourceName);
        return string.Equals(metadataName, normalized, StringComparison.Ordinal);
    }

    private static JsonSerializerOptions CreateOptions(bool writeIndented)
    {
        JsonSerializerOptions options = UnifiedJson.CreateOptions(writeIndented);
        options.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
        return options;
    }
}
