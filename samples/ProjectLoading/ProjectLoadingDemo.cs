using Microsoft.CodeAnalysis.CSharp;
using UnifiedScript;

namespace UnifiedScript.ProjectLoadingDemo;

public static class ProjectLoadingDemo
{
    private static LanguageVersion PackageVersion => LanguageVersion.CSharp14;

    [Visual]
    public static int Resolve(int value)
    {
#if UNIFIEDSCRIPT_PROJECT_DEMO
        return value + 14;
#else
        return value - 1;
#endif
    }
}
