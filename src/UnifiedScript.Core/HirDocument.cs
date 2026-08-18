using UnifiedScript.Core.Hir;

namespace UnifiedScript.Core;

public sealed record HirDocument(
    string SchemaVersion,
    HirCompilationUnit Compilation)
{
    public const string CurrentSchemaVersion = "unifiedscript.hir/v0.4";

    public static HirDocument Create(HirCompilationUnit compilation)
    {
        ArgumentNullException.ThrowIfNull(compilation);
        return new HirDocument(CurrentSchemaVersion, compilation);
    }
}
