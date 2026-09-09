using System.Reflection;

namespace UnifiedScript.Unreal;

/// <summary>
/// Builds the internal member locator used by the Unreal adapter.  Generated
/// class paths are retained for handwritten and legacy declarations; adopted
/// Script Classes use their persistent class id so an asset rename does not
/// leave state/RPC bindings pointing at a dead UClass path.
/// </summary>
internal static class UnrealScriptClassIdentity
{
    internal const string StablePrefix = "unifiedscript-class/";

    internal static string MemberPath(Type scriptType, string generatedClassPath, string memberName)
    {
        ArgumentNullException.ThrowIfNull(scriptType);
        ArgumentException.ThrowIfNullOrWhiteSpace(generatedClassPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(memberName);

        ScriptAssetAttribute? asset = scriptType.GetCustomAttribute<ScriptAssetAttribute>(inherit: false);
        if (asset is null || asset.ClassId is null)
        {
            return $"{generatedClassPath}:{memberName}";
        }

        string classId = asset.ClassId;
        if (!TryParseClassId(classId, out string digits))
        {
            throw new InvalidOperationException(
                $"Script Class '{scriptType.FullName}' has invalid ClassId '{asset.ClassId}'. " +
                "Expected a non-zero us_<32 lowercase hexadecimal digits> identity.");
        }

        return $"{StablePrefix}us_{digits}:{memberName}";
    }

    internal static bool TryParseClassId(string value, out string digits)
    {
        digits = string.Empty;
        if (!value.StartsWith("us_", StringComparison.Ordinal) || value.Length != 35)
        {
            return false;
        }

        string candidate = value[3..];
        if (!candidate.All(static c => c is >= '0' and <= '9' or >= 'a' and <= 'f'))
        {
            return false;
        }

        if (!Guid.TryParseExact(candidate, "N", out Guid id) || id == Guid.Empty)
        {
            return false;
        }

        if (!string.Equals(candidate, candidate.ToLowerInvariant(), StringComparison.Ordinal))
        {
            return false;
        }

        digits = candidate;
        return true;
    }
}
