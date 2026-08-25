using System.Text.Json;
using System.Text.Json.Serialization;

namespace UnifiedScript.Unreal;

public enum UnrealBindingDisposition
{
    NativeThunk,
    ReflectionFallback,
    Rejected,
    DeferredType,
}

public sealed record UnrealBindingCoverageEntry(
    string Path,
    string OwnerPath,
    string Kind,
    UnrealBindingDisposition Disposition,
    IReadOnlyList<string> DiagnosticCodes,
    string Message);

public sealed record UnrealBindingCoverageSummary(
    int TypeCount,
    int MemberCount,
    int NativeThunkCount,
    int ReflectionFallbackCount,
    int RejectedCount,
    int DeferredTypeCount);

public sealed record UnrealBindingCoverageReport(
    string SchemaVersion,
    UnrealEngineIdentity Engine,
    UnrealBindingCoverageSummary Summary,
    IReadOnlyList<UnrealBindingCoverageEntry> Entries)
{
    public const string CurrentSchemaVersion = "unifiedscript.unreal-binding-coverage/v0.1";

    public static UnrealBindingCoverageReport Create(
        UnrealReflectionManifest manifest,
        UnrealBindingGenerationResult managed,
        UnrealNativeThunkGenerationResult native)
    {
        UnrealReflectionManifest normalized = UnrealReflectionJson.NormalizeAndValidate(manifest);
        ArgumentNullException.ThrowIfNull(managed);
        ArgumentNullException.ThrowIfNull(native);

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
            if (type.Kind == UnrealReflectedTypeKind.Struct)
            {
                foreach (UnrealReflectedProperty field in type.Properties)
                {
                    AddMember(
                        entries,
                        type.Path,
                        "structField",
                        field.Path,
                        supportsReflectionFallback: false,
                        "Legacy reflection fallback cannot marshal generated complex structs.",
                        managedDiagnostics,
                        nativeDiagnostics);
                }
                continue;
            }
            if (type.Kind != UnrealReflectedTypeKind.Class)
            {
                AddEntry(
                    entries,
                    type.Path,
                    type.Path,
                    "type",
                    UnrealBindingDisposition.DeferredType,
                    managedDiagnostics.GetValueOrDefault(type.Path) ?? [],
                    nativeDiagnostics.GetValueOrDefault(type.Path) ?? []);
                continue;
            }

            foreach (UnrealReflectedProperty property in type.Properties)
            {
                AddMember(
                    entries,
                    type.Path,
                    "property",
                    property.Path,
                    SupportsReflectionType(property.Type, allowVoid: false),
                    "Legacy reflection fallback cannot marshal this property type.",
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
                    !hasOutputs && reflectionTypesSupported,
                    hasOutputs
                        ? "Legacy reflection fallback cannot preserve output or reference parameters."
                        : "Legacy reflection fallback cannot marshal one or more function value types.",
                    managedDiagnostics,
                    nativeDiagnostics);
            }
        }

        UnrealBindingCoverageEntry[] ordered = entries
            .OrderBy(static item => item.Path, StringComparer.Ordinal)
            .ThenBy(static item => item.Kind, StringComparer.Ordinal)
            .ToArray();
        return new UnrealBindingCoverageReport(
            CurrentSchemaVersion,
            normalized.Engine,
            new UnrealBindingCoverageSummary(
                normalized.Types.Count,
                ordered.Count(static item => item.Kind != "type"),
                ordered.Count(static item => item.Disposition == UnrealBindingDisposition.NativeThunk),
                ordered.Count(static item => item.Disposition == UnrealBindingDisposition.ReflectionFallback),
                ordered.Count(static item => item.Disposition == UnrealBindingDisposition.Rejected),
                ordered.Count(static item => item.Disposition == UnrealBindingDisposition.DeferredType)),
            ordered);
    }

    private static void AddMember(
        ICollection<UnrealBindingCoverageEntry> entries,
        string ownerPath,
        string kind,
        string path,
        bool supportsReflectionFallback,
        string fallbackLimitation,
        IReadOnlyDictionary<string, UnrealBindingDiagnostic[]> managedDiagnostics,
        IReadOnlyDictionary<string, UnrealNativeThunkDiagnostic[]> nativeDiagnostics)
    {
        UnrealBindingDiagnostic[] managed = managedDiagnostics.GetValueOrDefault(path) ?? [];
        UnrealNativeThunkDiagnostic[] native = nativeDiagnostics.GetValueOrDefault(path) ?? [];
        UnrealBindingDisposition disposition = managed.Length > 0
            ? UnrealBindingDisposition.Rejected
            : native.Length > 0
                ? supportsReflectionFallback
                    ? UnrealBindingDisposition.ReflectionFallback
                    : UnrealBindingDisposition.Rejected
                : UnrealBindingDisposition.NativeThunk;
        bool reportFallbackLimitation =
            managed.Length == 0 && native.Length > 0 && !supportsReflectionFallback;
        AddEntry(
            entries,
            path,
            ownerPath,
            kind,
            disposition,
            managed,
            native,
            reportFallbackLimitation ? "USUE4001" : null,
            reportFallbackLimitation ? fallbackLimitation : null);
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

    private static bool SupportsReflectionType(UnrealTypeReference type, bool allowVoid) =>
        type.Kind is
            UnrealValueKind.Boolean or
            UnrealValueKind.Int8 or UnrealValueKind.UInt8 or
            UnrealValueKind.Int16 or UnrealValueKind.UInt16 or
            UnrealValueKind.Int32 or UnrealValueKind.UInt32 or
            UnrealValueKind.Int64 or UnrealValueKind.UInt64 or
            UnrealValueKind.Float32 or UnrealValueKind.Float64 or
            UnrealValueKind.String or UnrealValueKind.Name or UnrealValueKind.Text or
            UnrealValueKind.Enum or UnrealValueKind.Object or UnrealValueKind.Class ||
        type.Kind == UnrealValueKind.Struct && type.TypePath is
            "/Script/CoreUObject.Vector" or
            "/Script/CoreUObject.Rotator" or
            "/Script/CoreUObject.Transform" ||
        allowVoid && type.Kind == UnrealValueKind.Void;
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
