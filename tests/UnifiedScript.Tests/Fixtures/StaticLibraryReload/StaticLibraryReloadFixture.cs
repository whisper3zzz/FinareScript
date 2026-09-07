using UnifiedScript;
using UnifiedScript.Unreal;

namespace UnifiedScript.Tests.Fixtures.StaticLibraryReload;

[UnrealScriptClass(UnrealScriptClassKind.BlueprintFunctionLibrary)]
public static class StaticLibraryReloadFixture
{
    [Visual]
#if INCOMPATIBLE
    public static IReadOnlyList<string> ExchangeArray(
        IReadOnlyList<string> replacement,
        ref IReadOnlyList<string> value,
        out IReadOnlyList<string> previous)
#else
    public static IReadOnlyList<int> ExchangeArray(
        IReadOnlyList<int> replacement,
        ref IReadOnlyList<int> value,
        out IReadOnlyList<int> previous)
#endif
    {
#if INCOMPATIBLE
        throw new InvalidOperationException("The incompatible fixture must never execute.");
#elif V2
        previous = replacement;
        value = replacement;
        return replacement;
#else
        previous = value;
        value = replacement;
        return previous;
#endif
    }
}
