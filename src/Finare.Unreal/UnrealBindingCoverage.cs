using System.Text.Json;
using System.Text.Json.Serialization;

namespace Finare.Unreal;

public enum UnrealBindingDisposition
{
    DirectThunk,
    CallFrame,
    DataPlaneOperation,
    ReflectionFallback,
    Rejected,
}

public sealed record UnrealBindingCoverageEntry(
    string Path,
    string OwnerPath,
    string Kind,
    UnrealBindingDisposition Disposition,
    IReadOnlyList<string> DiagnosticCodes,
    string Message)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? NativeExtension { get; init; }
}

public sealed record UnrealBindingCoverageSummary(
    int TypeCount,
    int MemberCount,
    int DirectThunkCount,
    int CallFrameCount,
    int DataPlaneOperationCount,
    int ReflectionFallbackCount,
    int RejectedCount)
{
    public int NativeThunkCount => DirectThunkCount + CallFrameCount;

    public int DeferredTypeCount => 0;
}

public sealed record UnrealBindingCoverageGate(
    bool Passed,
    IReadOnlyList<string> Diagnostics);

public sealed record UnrealBindingCoverageReport(
    string SchemaVersion,
    UnrealEngineIdentity Engine,
    UnrealBindingCoverageSummary Summary,
    UnrealBindingCoverageBaseline? Baseline,
    UnrealBindingCoverageGate Gate,
    IReadOnlyList<UnrealBindingCoverageEntry> Entries)
{
    public const string CurrentSchemaVersion = "unifiedscript.unreal-binding-coverage/v0.2";

    public static UnrealBindingCoverageReport Create(
        UnrealReflectionManifest manifest,
        UnrealBindingGenerationResult managed,
        UnrealNativeThunkGenerationResult native) =>
        Create(
            UnrealReflectionJson.NormalizeAndValidate(manifest),
            managed,
            native,
            UnrealApiPolicyEvaluator.Evaluate(manifest, UnrealApiPolicy.AllowSelected),
            baseline: null);

    public static UnrealBindingCoverageReport Create(
        UnrealApiContractProjection projection,
        UnrealBindingGenerationResult managed,
        UnrealNativeThunkGenerationResult native)
    {
        ArgumentNullException.ThrowIfNull(projection);
        return Create(
            projection.SelectedReflection,
            managed,
            native,
            projection.PolicyEvaluation,
            projection.Contract.CoverageBaseline);
    }

    private static UnrealBindingCoverageReport Create(
        UnrealReflectionManifest selectedManifest,
        UnrealBindingGenerationResult managed,
        UnrealNativeThunkGenerationResult native,
        UnrealApiPolicyEvaluation policy,
        UnrealBindingCoverageBaseline? baseline)
    {
        UnrealReflectionManifest normalized = UnrealReflectionJson.NormalizeAndValidate(selectedManifest);
        ArgumentNullException.ThrowIfNull(managed);
        ArgumentNullException.ThrowIfNull(native);
        ArgumentNullException.ThrowIfNull(policy);

        IReadOnlyDictionary<string, UnrealBindingDiagnostic[]> managedDiagnostics = managed.Diagnostics
            .GroupBy(static item => item.MemberPath, StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => group.OrderBy(static item => item.Code, StringComparer.Ordinal).ToArray(),
                StringComparer.Ordinal);
        IReadOnlyDictionary<string, UnrealNativeThunkDiagnostic[]> nativeDiagnostics = native.Diagnostics
            .GroupBy(static item => item.MemberPath, StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => group.OrderBy(static item => item.Code, StringComparer.Ordinal).ToArray(),
                StringComparer.Ordinal);

        var entries = new List<UnrealBindingCoverageEntry>();
        foreach (UnrealReflectedType type in normalized.Types)
        {
            if (type.Kind == UnrealReflectedTypeKind.NativeObject)
            {
                foreach (var member in type.Properties.Select(p => (p.Path, Kind: "property"))
                             .Concat(type.Functions.Select(f => (f.Path, Kind: "function"))))
                {
                    var decision = policy.For(member.Path);
                    var managedErrors = managedDiagnostics.GetValueOrDefault(member.Path) ?? [];
                    var nativeErrors = nativeDiagnostics.GetValueOrDefault(member.Path) ?? [];
                    bool allowed = decision.Action == UnrealApiPolicyAction.Allow && managedErrors.Length == 0 && nativeErrors.Length == 0 &&
                        decision.Route is UnrealBindingRoute.Auto or UnrealBindingRoute.DirectThunk;
                    AddEntry(entries, member.Path, type.Path, member.Kind,
                        allowed ? UnrealBindingDisposition.DirectThunk : UnrealBindingDisposition.Rejected,
                        managedErrors, nativeErrors, allowed ? null : decision.DiagnosticCode ?? "USUE5410",
                        allowed ? "Generated Native Object ABI v1 dispatch; no UObject/reflection fallback."
                            : decision.Reason ?? "Native object member requires a supported generated ownership/value route.");
                }
                continue;
            }
            foreach (UnrealReflectedProperty property in type.Properties)
            {
                AddMember(
                    entries,
                    type.Path,
                    type.Kind == UnrealReflectedTypeKind.Struct ? "structField" : "property",
                    property.Path,
                    property.Type,
                    function: null,
                    supportsReflectionFallback: type.Kind == UnrealReflectedTypeKind.Class &&
                        !type.Traits.Interface &&
                        property.NativeExtension is null &&
                        SupportsReflectionType(property.Type, allowVoid: false),
                    property.NativeExtension is null ? "Safe reflection fallback cannot marshal this property type."
                        : "Native-only properties have no reflected fallback.",
                    policy.For(property.Path),
                    managedDiagnostics,
                    nativeDiagnostics);
            }
            foreach (UnrealReflectedFunction function in type.Functions)
            {
                bool hasOutputs = function.Parameters.Any(static parameter =>
                    parameter.Direction != UnrealParameterDirection.Input);
                bool reflectionTypesSupported =
                    SupportsReflectionType(function.ReturnType, allowVoid: true) &&
                    function.Parameters.All(static parameter =>
                        SupportsReflectionType(parameter.Type, allowVoid: false));
                AddMember(
                    entries,
                    type.Path,
                    "function",
                    function.Path,
                    function.ReturnType,
                    function,
                    function.NativeBinding is null && !type.Traits.Interface &&
                        !hasOutputs && reflectionTypesSupported,
                    function.NativeBinding is not null ? "Native-only functions have no reflected fallback." : hasOutputs
                        ? "Safe reflection fallback cannot preserve output or reference parameters."
                        : "Safe reflection fallback cannot marshal one or more function value types.",
                    policy.For(function.Path),
                    managedDiagnostics,
                    nativeDiagnostics);
            }
            foreach (UnrealReflectedDelegate reflectedDelegate in type.Delegates)
            {
                UnrealApiPolicyDecision decision = policy.For(reflectedDelegate.Path);
                UnrealBindingDiagnostic[] delegateDiagnostics =
                    managedDiagnostics.GetValueOrDefault(reflectedDelegate.Path) ?? [];
                AddEntry(
                    entries,
                    reflectedDelegate.Path,
                    type.Path,
                    "delegate",
                    decision.Action == UnrealApiPolicyAction.Deny || delegateDiagnostics.Length > 0
                        ? UnrealBindingDisposition.Rejected
                        : UnrealBindingDisposition.ReflectionFallback,
                    delegateDiagnostics,
                    [],
                    decision.Action == UnrealApiPolicyAction.Deny
                        ? decision.DiagnosticCode
                        : "USUE4101",
                    decision.Action == UnrealApiPolicyAction.Deny
                        ? decision.Reason
                        : "Delegate delivery uses the generation-safe native event proxy and reflected signature.");
            }
        }

        Dictionary<string, string?> origins = normalized.Types.SelectMany(static type =>
            type.Properties.Select(static field => (field.Path, Origin: field.NativeExtension))
                .Concat(type.Functions.Select(static method => (method.Path, Origin: method.NativeBinding?.Extension))))
            .ToDictionary(static item => item.Path, static item => item.Origin, StringComparer.Ordinal);
        UnrealBindingCoverageEntry[] ordered = entries
            .Select(entry => entry with { NativeExtension = origins.GetValueOrDefault(entry.Path) })
            .OrderBy(static item => item.Path, StringComparer.Ordinal)
            .ThenBy(static item => item.Kind, StringComparer.Ordinal)
            .ToArray();
        var summary = new UnrealBindingCoverageSummary(
            normalized.Types.Count,
            ordered.Length,
            ordered.Count(static item => item.Disposition == UnrealBindingDisposition.DirectThunk),
            ordered.Count(static item => item.Disposition == UnrealBindingDisposition.CallFrame),
            ordered.Count(static item => item.Disposition == UnrealBindingDisposition.DataPlaneOperation),
            ordered.Count(static item => item.Disposition == UnrealBindingDisposition.ReflectionFallback),
            ordered.Count(static item => item.Disposition == UnrealBindingDisposition.Rejected));
        return new UnrealBindingCoverageReport(
            CurrentSchemaVersion,
            normalized.Engine,
            summary,
            baseline,
            EvaluateGate(summary, baseline),
            ordered);
    }

    private static void AddMember(
        ICollection<UnrealBindingCoverageEntry> entries,
        string ownerPath,
        string kind,
        string path,
        UnrealTypeReference valueType,
        UnrealReflectedFunction? function,
        bool supportsReflectionFallback,
        string fallbackLimitation,
        UnrealApiPolicyDecision policy,
        IReadOnlyDictionary<string, UnrealBindingDiagnostic[]> managedDiagnostics,
        IReadOnlyDictionary<string, UnrealNativeThunkDiagnostic[]> nativeDiagnostics)
    {
        UnrealBindingDiagnostic[] managed = managedDiagnostics.GetValueOrDefault(path) ?? [];
        UnrealNativeThunkDiagnostic[] native = nativeDiagnostics.GetValueOrDefault(path) ?? [];
        if (policy.Action == UnrealApiPolicyAction.Deny)
        {
            AddEntry(entries, path, ownerPath, kind, UnrealBindingDisposition.Rejected, [], [],
                policy.DiagnosticCode, policy.Reason);
            return;
        }
        if (policy.Route == UnrealBindingRoute.DataPlaneOperation)
        {
            AddEntry(entries, path, ownerPath, kind, UnrealBindingDisposition.DataPlaneOperation,
                [], [], "USUE4102", "The selected member is served by the generated gameplay data plane.");
            return;
        }
        bool hasBlockingManagedDiagnostic = managed.Any(static diagnostic => diagnostic.Code != "USUE2008");
        if (hasBlockingManagedDiagnostic)
        {
            AddEntry(entries, path, ownerPath, kind, UnrealBindingDisposition.Rejected, managed, native);
            return;
        }
        if (policy.Route == UnrealBindingRoute.ReflectionFallback)
        {
            AddEntry(
                entries,
                path,
                ownerPath,
                kind,
                supportsReflectionFallback
                    ? UnrealBindingDisposition.ReflectionFallback
                    : UnrealBindingDisposition.Rejected,
                managed,
                native,
                supportsReflectionFallback ? "USUE4103" : "USUE4001",
                supportsReflectionFallback
                    ? "The project API policy explicitly selects safe reflection fallback."
                    : fallbackLimitation);
            return;
        }
        if (native.Length > 0)
        {
            AddEntry(
                entries,
                path,
                ownerPath,
                kind,
                supportsReflectionFallback
                    ? UnrealBindingDisposition.ReflectionFallback
                    : UnrealBindingDisposition.Rejected,
                managed,
                native,
                supportsReflectionFallback ? "USUE4104" : "USUE4001",
                supportsReflectionFallback
                    ? "The native thunk is unavailable; the member is safe on reflected fallback."
                    : fallbackLimitation);
            return;
        }

        bool direct = function is not null && IsDirectThunk(function);
        if (policy.Route == UnrealBindingRoute.DirectThunk && !direct)
        {
            AddEntry(entries, path, ownerPath, kind, UnrealBindingDisposition.Rejected, [], [],
                "USUE4002", "The policy requires a direct thunk but the signature requires CallFrame.");
            return;
        }
        UnrealBindingDisposition disposition = policy.Route switch
        {
            UnrealBindingRoute.CallFrame => UnrealBindingDisposition.CallFrame,
            UnrealBindingRoute.DirectThunk => UnrealBindingDisposition.DirectThunk,
            _ => direct ? UnrealBindingDisposition.DirectThunk : UnrealBindingDisposition.CallFrame,
        };
        AddEntry(entries, path, ownerPath, kind, disposition, managed, native);
    }

    private static void AddEntry(
        ICollection<UnrealBindingCoverageEntry> entries,
        string path,
        string ownerPath,
        string kind,
        UnrealBindingDisposition disposition,
        IReadOnlyList<UnrealBindingDiagnostic> managed,
        IReadOnlyList<UnrealNativeThunkDiagnostic> native,
        string? coverageCode = null,
        string? coverageMessage = null)
    {
        string[] codes = managed.Select(static item => item.Code)
            .Concat(native.Select(static item => item.Code))
            .Concat(coverageCode is null ? [] : [coverageCode])
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        string message = string.Join(
            " ",
            managed.Select(static item => item.Message)
                .Concat(native.Select(static item => item.Message))
                .Concat(coverageMessage is null ? [] : [coverageMessage])
                .Distinct(StringComparer.Ordinal));
        entries.Add(new UnrealBindingCoverageEntry(
            path,
            ownerPath,
            kind,
            disposition,
            codes,
            message));
    }

    private static bool IsDirectThunk(UnrealReflectedFunction function) =>
        function.Parameters.Count <= 4 &&
        function.Parameters.All(static parameter =>
            parameter.Direction == UnrealParameterDirection.Input && IsFixedType(parameter.Type, allowVoid: false)) &&
        IsFixedType(function.ReturnType, allowVoid: true);

    private static bool IsFixedType(UnrealTypeReference type, bool allowVoid) =>
        type.Kind is UnrealValueKind.Boolean or UnrealValueKind.Int32 or UnrealValueKind.Float32 or
            UnrealValueKind.Object ||
        type.Kind == UnrealValueKind.Struct && type.TypePath == "/Script/CoreUObject.Vector" ||
        allowVoid && type.Kind == UnrealValueKind.Void;

    private static bool SupportsReflectionType(UnrealTypeReference type, bool allowVoid) =>
        type.Kind is
            UnrealValueKind.Boolean or
            UnrealValueKind.Int8 or UnrealValueKind.UInt8 or
            UnrealValueKind.Int16 or UnrealValueKind.UInt16 or
            UnrealValueKind.Int32 or UnrealValueKind.UInt32 or
            UnrealValueKind.Int64 or UnrealValueKind.UInt64 or
            UnrealValueKind.Float32 or UnrealValueKind.Float64 or
            UnrealValueKind.String or UnrealValueKind.Name or UnrealValueKind.Text or
            UnrealValueKind.Enum or UnrealValueKind.Object or UnrealValueKind.Class or UnrealValueKind.Interface or
            UnrealValueKind.SoftObject or UnrealValueKind.SoftClass ||
        type.Kind == UnrealValueKind.Struct && type.TypePath is
            "/Script/CoreUObject.Vector" or
            "/Script/CoreUObject.Rotator" or
            "/Script/CoreUObject.Transform" ||
        allowVoid && type.Kind == UnrealValueKind.Void;

    private static UnrealBindingCoverageGate EvaluateGate(
        UnrealBindingCoverageSummary summary,
        UnrealBindingCoverageBaseline? baseline)
    {
        if (baseline is null)
        {
            return new UnrealBindingCoverageGate(true, []);
        }
        var diagnostics = new List<string>();
        RequireMinimum("USUE5101", "selected members", summary.MemberCount, baseline.MinimumMemberCount, diagnostics);
        RequireMinimum("USUE5102", "direct thunks", summary.DirectThunkCount, baseline.MinimumDirectThunkCount, diagnostics);
        RequireMinimum("USUE5103", "CallFrame members", summary.CallFrameCount, baseline.MinimumCallFrameCount, diagnostics);
        RequireMinimum("USUE5104", "data-plane operations", summary.DataPlaneOperationCount, baseline.MinimumDataPlaneOperationCount, diagnostics);
        RequireMinimum("USUE5105", "reflection fallbacks", summary.ReflectionFallbackCount, baseline.MinimumReflectionFallbackCount, diagnostics);
        if (summary.RejectedCount > baseline.MaximumRejectedCount)
        {
            diagnostics.Add(
                $"USUE5106: rejected members regressed to {summary.RejectedCount}; maximum is {baseline.MaximumRejectedCount}.");
        }
        return new UnrealBindingCoverageGate(diagnostics.Count == 0, diagnostics);
    }

    private static void RequireMinimum(
        string code,
        string label,
        int actual,
        int minimum,
        ICollection<string> diagnostics)
    {
        if (actual < minimum)
        {
            diagnostics.Add($"{code}: {label} regressed to {actual}; minimum is {minimum}.");
        }
    }
}

public static class UnrealBindingCoverageJson
{
    public static string Serialize(UnrealBindingCoverageReport report, bool writeIndented = true)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (report.SchemaVersion != UnrealBindingCoverageReport.CurrentSchemaVersion)
        {
            throw new ArgumentException("Unsupported Unreal binding coverage schema.", nameof(report));
        }
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = writeIndented,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return JsonSerializer.Serialize(report, options).ReplaceLineEndings("\n") + "\n";
    }
}
