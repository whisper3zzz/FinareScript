using Microsoft.CodeAnalysis.CSharp;
using Finare;

namespace Finare.ProjectLoadingDemo;

public static class ProjectLoadingDemo
{
    private static LanguageVersion PackageVersion => LanguageVersion.CSharp14;

    [Visual]
    public static int Resolve(int value)
    {
#if FINARE_PROJECT_DEMO
        return value + 14;
#else
        return value - 1;
#endif
    }
}
