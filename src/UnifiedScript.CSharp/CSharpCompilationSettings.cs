using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace UnifiedScript.CSharp;

public sealed record CSharpCompilationSettings
{
    public CSharpCompilationSettings(
        string targetFramework,
        LanguageVersion languageVersion,
        IEnumerable<string>? preprocessorSymbols = null,
        NullableContextOptions nullableContext = NullableContextOptions.Enable,
        bool allowUnsafe = false,
        bool checkOverflow = false,
        OptimizationLevel optimizationLevel = OptimizationLevel.Debug)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetFramework);
        TargetFramework = targetFramework;
        LanguageVersion = languageVersion;
        PreprocessorSymbols = (preprocessorSymbols ?? [])
            .Where(static symbol => !string.IsNullOrWhiteSpace(symbol))
            .Select(static symbol => symbol.Trim())
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        NullableContext = nullableContext;
        AllowUnsafe = allowUnsafe;
        CheckOverflow = checkOverflow;
        OptimizationLevel = optimizationLevel;
    }

    public string TargetFramework { get; }

    public LanguageVersion LanguageVersion { get; }

    public IReadOnlyList<string> PreprocessorSymbols { get; }

    public NullableContextOptions NullableContext { get; }

    public bool AllowUnsafe { get; }

    public bool CheckOverflow { get; }

    public OptimizationLevel OptimizationLevel { get; }

    public CSharpParseOptions ParseOptions => new(
        LanguageVersion,
        DocumentationMode.Parse,
        SourceCodeKind.Regular,
        PreprocessorSymbols);

    public string ConfigurationKey => string.Join(
        '|',
        TargetFramework,
        LanguageVersion.ToString(),
        NullableContext.ToString(),
        AllowUnsafe,
        CheckOverflow,
        OptimizationLevel.ToString(),
        string.Join(',', PreprocessorSymbols));

    public static CSharpCompilationSettings Default { get; } = new(
        "net10.0",
        LanguageVersion.Preview);
}
