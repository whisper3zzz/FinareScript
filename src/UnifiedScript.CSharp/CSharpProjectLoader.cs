using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using UnifiedScript.Semantics;

namespace UnifiedScript.CSharp;

public enum CSharpProjectReferenceKind
{
    Framework,
    Package,
    Project,
    Assembly,
}

public sealed record CSharpProjectReference(
    string Path,
    CSharpProjectReferenceKind Kind,
    string? PackageId,
    string? PackageVersion,
    string? SourceProjectPath);

public sealed record CSharpProjectLoadOptions(
    string Configuration = "Debug",
    string? TargetFramework = null,
    bool Restore = true,
    bool Build = true,
    string? DotNetPath = null,
    TimeSpan? Timeout = null);

public sealed record CSharpProjectLoadResult(
    string ProjectPath,
    string Configuration,
    string TargetFramework,
    CSharpCompilationRequest Request,
    IReadOnlyList<CSharpProjectReference> References);

public sealed class CSharpProjectLoadException : Exception
{
    public CSharpProjectLoadException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// Loads an SDK-style C# project through the installed MSBuild rather than
/// approximating imports, item globs, NuGet assets, or project references.
/// </summary>
public sealed class CSharpProjectLoader
{
    private static readonly string[] QueryProperties =
    [
        "AssemblyName",
        "TargetFramework",
        "DefineConstants",
        "LangVersion",
        "Nullable",
        "AllowUnsafeBlocks",
        "CheckForOverflowUnderflow",
        "Optimize",
    ];

    private static readonly string[] QueryItems =
    [
        "Compile",
        "ReferencePath",
        "ReferenceCopyLocalPaths",
    ];

    public CSharpProjectLoadResult Load(
        string projectPath,
        MethodEffectCatalog? effectCatalog = null,
        CSharpProjectLoadOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);
        options ??= new CSharpProjectLoadOptions();
        if (string.IsNullOrWhiteSpace(options.Configuration))
        {
            throw new ArgumentException("An MSBuild configuration is required.", nameof(options));
        }

        string fullProjectPath = Path.GetFullPath(projectPath);
        if (!File.Exists(fullProjectPath))
        {
            throw new FileNotFoundException("C# project does not exist.", fullProjectPath);
        }
        if (!string.Equals(Path.GetExtension(fullProjectPath), ".csproj", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Project loading requires a .csproj file.", nameof(projectPath));
        }

        string dotnetPath = ResolveDotNetPath(options.DotNetPath);
        TimeSpan timeout = options.Timeout ?? TimeSpan.FromMinutes(2);
        string targetFramework = SelectTargetFramework(
            fullProjectPath,
            dotnetPath,
            options,
            timeout);

        if (options.Restore)
        {
            RunDotNet(
                dotnetPath,
                ["restore", fullProjectPath, "--nologo", "--verbosity", "quiet"],
                Path.GetDirectoryName(fullProjectPath)!,
                timeout,
                "restore");
        }
        if (options.Build)
        {
            RunDotNet(
                dotnetPath,
                [
                    "build",
                    fullProjectPath,
                    "--nologo",
                    "--verbosity",
                    "quiet",
                    "--no-restore",
                    "--configuration",
                    options.Configuration,
                    "--framework",
                    targetFramework,
                ],
                Path.GetDirectoryName(fullProjectPath)!,
                timeout,
                "build");
        }

        var arguments = new List<string>
        {
            "msbuild",
            fullProjectPath,
            "-nologo",
            "-verbosity:quiet",
            "-target:ResolveReferences",
            $"-property:Configuration={options.Configuration}",
            $"-property:TargetFramework={targetFramework}",
            "-property:BuildProjectReferences=false",
        };
        arguments.AddRange(QueryProperties.Select(static property => $"-getProperty:{property}"));
        arguments.AddRange(QueryItems.Select(static item => $"-getItem:{item}"));
        string queryOutput = RunDotNet(
            dotnetPath,
            arguments,
            Path.GetDirectoryName(fullProjectPath)!,
            timeout,
            "MSBuild evaluation");

        using JsonDocument query = ParseQuery(queryOutput, fullProjectPath);
        JsonElement properties = query.RootElement.GetProperty("Properties");
        JsonElement items = query.RootElement.GetProperty("Items");
        string assemblyName = RequiredProperty(properties, "AssemblyName", fullProjectPath);
        string projectDirectory = Path.GetDirectoryName(fullProjectPath)!;
        CSharpSourceDocument[] sources = ReadSources(items, projectDirectory, fullProjectPath);
        CSharpProjectReference[] references = ReadReferences(items, fullProjectPath);
        string[] referencePaths = references
            .Select(static reference => reference.Path)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.Ordinal)
            .ToArray();
        string[] runtimeReferencePaths = ReadRuntimeReferences(items)
            .Concat(references
                .Where(static reference => reference.Kind is
                    CSharpProjectReferenceKind.Project or
                    CSharpProjectReferenceKind.Assembly)
                .Select(static reference => reference.Path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.Ordinal)
            .ToArray();
        CSharpCompilationSettings settings = ReadSettings(properties, targetFramework, fullProjectPath);
        var request = new CSharpCompilationRequest(
            assemblyName,
            sources,
            referencePaths,
            effectCatalog,
            settings,
            runtimeReferencePaths,
            includeDefaultReferences: false);
        return new CSharpProjectLoadResult(
            fullProjectPath,
            options.Configuration,
            targetFramework,
            request,
            references);
    }

    private static string SelectTargetFramework(
        string projectPath,
        string dotnetPath,
        CSharpProjectLoadOptions options,
        TimeSpan timeout)
    {
        string output = RunDotNet(
            dotnetPath,
            [
                "msbuild",
                projectPath,
                "-nologo",
                "-verbosity:quiet",
                $"-property:Configuration={options.Configuration}",
                "-getProperty:TargetFramework",
                "-getProperty:TargetFrameworks",
            ],
            Path.GetDirectoryName(projectPath)!,
            timeout,
            "target-framework evaluation");
        using JsonDocument query = ParseQuery(output, projectPath);
        JsonElement properties = query.RootElement.GetProperty("Properties");
        string single = OptionalProperty(properties, "TargetFramework");
        string multiple = OptionalProperty(properties, "TargetFrameworks");
        string[] frameworks = (string.IsNullOrWhiteSpace(multiple) ? single : multiple)
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (frameworks.Length == 0)
        {
            throw new CSharpProjectLoadException(
                $"Project '{projectPath}' does not declare TargetFramework or TargetFrameworks.");
        }
        if (options.TargetFramework is null)
        {
            if (frameworks.Length > 1)
            {
                throw new CSharpProjectLoadException(
                    $"Project '{projectPath}' targets {string.Join(", ", frameworks)}; " +
                    "select one target framework explicitly.");
            }
            return frameworks[0];
        }
        string? selected = frameworks.FirstOrDefault(framework =>
            string.Equals(framework, options.TargetFramework, StringComparison.OrdinalIgnoreCase));
        return selected ?? throw new CSharpProjectLoadException(
            $"Target framework '{options.TargetFramework}' is not declared by '{projectPath}'.");
    }

    private static CSharpSourceDocument[] ReadSources(
        JsonElement items,
        string projectDirectory,
        string projectPath)
    {
        if (!items.TryGetProperty("Compile", out JsonElement compileItems))
        {
            throw new CSharpProjectLoadException($"MSBuild returned no Compile items for '{projectPath}'.");
        }
        return compileItems.EnumerateArray()
            .Select(item =>
            {
                string fullPath = RequiredItemMetadata(item, "FullPath", projectPath);
                if (!File.Exists(fullPath))
                {
                    throw new CSharpProjectLoadException(
                        $"MSBuild Compile item '{fullPath}' does not exist after project evaluation.");
                }
                string link = OptionalItemMetadata(item, "Link");
                string logicalPath = string.IsNullOrWhiteSpace(link)
                    ? Path.GetRelativePath(projectDirectory, fullPath)
                    : link;
                return new CSharpSourceDocument(
                    logicalPath.Replace(Path.DirectorySeparatorChar, '/'),
                    File.ReadAllText(fullPath));
            })
            .OrderBy(static source => source.LogicalPath, StringComparer.Ordinal)
            .ToArray();
    }

    private static CSharpProjectReference[] ReadReferences(JsonElement items, string projectPath)
    {
        if (!items.TryGetProperty("ReferencePath", out JsonElement referenceItems))
        {
            throw new CSharpProjectLoadException($"MSBuild returned no ReferencePath items for '{projectPath}'.");
        }
        return referenceItems.EnumerateArray()
            .Select(item =>
            {
                string path = RequiredItemMetadata(item, "FullPath", projectPath);
                if (!File.Exists(path))
                {
                    throw new CSharpProjectLoadException(
                        $"Resolved metadata reference '{path}' does not exist. Build project references first.");
                }
                string packageId = OptionalItemMetadata(item, "NuGetPackageId");
                string frameworkName = OptionalItemMetadata(item, "FrameworkReferenceName");
                string sourceProject = OptionalItemMetadata(item, "MSBuildSourceProjectFile");
                CSharpProjectReferenceKind kind = !string.IsNullOrWhiteSpace(sourceProject)
                    ? CSharpProjectReferenceKind.Project
                    : !string.IsNullOrWhiteSpace(frameworkName)
                        ? CSharpProjectReferenceKind.Framework
                        : !string.IsNullOrWhiteSpace(packageId)
                            ? CSharpProjectReferenceKind.Package
                            : CSharpProjectReferenceKind.Assembly;
                return new CSharpProjectReference(
                    Path.GetFullPath(path),
                    kind,
                    string.IsNullOrWhiteSpace(packageId) ? null : packageId,
                    NullIfWhiteSpace(OptionalItemMetadata(item, "NuGetPackageVersion")),
                    NullIfWhiteSpace(sourceProject));
            })
            .GroupBy(static reference => reference.Path, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group
                .OrderBy(static reference => ReferencePriority(reference.Kind))
                .First())
            .OrderBy(static reference => reference.Path, StringComparer.Ordinal)
            .ToArray();
    }

    private static string[] ReadRuntimeReferences(JsonElement items)
    {
        if (!items.TryGetProperty("ReferenceCopyLocalPaths", out JsonElement referenceItems))
        {
            return [];
        }
        return referenceItems.EnumerateArray()
            .Select(static item => OptionalItemMetadata(item, "FullPath"))
            .Where(static path => !string.IsNullOrWhiteSpace(path) && IsManagedAssembly(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static CSharpCompilationSettings ReadSettings(
        JsonElement properties,
        string targetFramework,
        string projectPath)
    {
        string languageText = OptionalProperty(properties, "LangVersion");
        LanguageVersion languageVersion;
        if (string.IsNullOrWhiteSpace(languageText))
        {
            languageVersion = LanguageVersion.Default;
        }
        else if (!LanguageVersionFacts.TryParse(languageText, out languageVersion))
        {
            throw new CSharpProjectLoadException(
                $"Project '{projectPath}' uses unsupported LangVersion '{languageText}'.");
        }
        string[] symbols = OptionalProperty(properties, "DefineConstants")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return new CSharpCompilationSettings(
            targetFramework,
            languageVersion,
            symbols,
            ParseNullable(OptionalProperty(properties, "Nullable"), projectPath),
            ParseBoolean(OptionalProperty(properties, "AllowUnsafeBlocks")),
            ParseBoolean(OptionalProperty(properties, "CheckForOverflowUnderflow")),
            ParseBoolean(OptionalProperty(properties, "Optimize"))
                ? OptimizationLevel.Release
                : OptimizationLevel.Debug);
    }

    private static NullableContextOptions ParseNullable(string value, string projectPath) =>
        value.Trim().ToLowerInvariant() switch
        {
            "" or "disable" => NullableContextOptions.Disable,
            "enable" => NullableContextOptions.Enable,
            "warnings" => NullableContextOptions.Warnings,
            "annotations" => NullableContextOptions.Annotations,
            _ => throw new CSharpProjectLoadException(
                $"Project '{projectPath}' uses unsupported Nullable value '{value}'."),
        };

    private static bool ParseBoolean(string value) =>
        bool.TryParse(value, out bool parsed) && parsed;

    private static JsonDocument ParseQuery(string output, string projectPath)
    {
        int start = output.IndexOf('{');
        int end = output.LastIndexOf('}');
        if (start < 0 || end < start)
        {
            throw new CSharpProjectLoadException(
                $"MSBuild did not return a JSON project query for '{projectPath}'.");
        }
        try
        {
            return JsonDocument.Parse(output[start..(end + 1)]);
        }
        catch (JsonException exception)
        {
            throw new CSharpProjectLoadException(
                $"MSBuild returned invalid project query JSON for '{projectPath}': {exception.Message}");
        }
    }

    private static string RequiredProperty(
        JsonElement properties,
        string name,
        string projectPath)
    {
        string value = OptionalProperty(properties, name);
        return string.IsNullOrWhiteSpace(value)
            ? throw new CSharpProjectLoadException(
                $"MSBuild property '{name}' is empty for '{projectPath}'.")
            : value;
    }

    private static string OptionalProperty(JsonElement properties, string name) =>
        properties.TryGetProperty(name, out JsonElement value)
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static string RequiredItemMetadata(
        JsonElement item,
        string name,
        string projectPath)
    {
        string value = OptionalItemMetadata(item, name);
        return string.IsNullOrWhiteSpace(value)
            ? throw new CSharpProjectLoadException(
                $"MSBuild item metadata '{name}' is empty for '{projectPath}'.")
            : value;
    }

    private static string OptionalItemMetadata(JsonElement item, string name) =>
        item.TryGetProperty(name, out JsonElement value)
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static string? NullIfWhiteSpace(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private static bool IsManagedAssembly(string path)
    {
        string extension = Path.GetExtension(path);
        if (!File.Exists(path) ||
            !string.Equals(extension, ".dll", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(extension, ".exe", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        try
        {
            _ = AssemblyName.GetAssemblyName(path);
            return true;
        }
        catch (BadImageFormatException)
        {
            return false;
        }
        catch (FileLoadException)
        {
            return false;
        }
    }

    private static int ReferencePriority(CSharpProjectReferenceKind kind) => kind switch
    {
        CSharpProjectReferenceKind.Project => 0,
        CSharpProjectReferenceKind.Package => 1,
        CSharpProjectReferenceKind.Framework => 2,
        CSharpProjectReferenceKind.Assembly => 3,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static string ResolveDotNetPath(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }
        string runtimeHost = Path.GetFullPath(Path.Combine(
            RuntimeEnvironment.GetRuntimeDirectory(),
            "..",
            "..",
            "..",
            OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"));
        if (File.Exists(runtimeHost))
        {
            return runtimeHost;
        }
        string? processPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(processPath) &&
            string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            return processPath;
        }
        string? hostPath = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (!string.IsNullOrWhiteSpace(hostPath) && File.Exists(hostPath))
        {
            return hostPath;
        }
        return "dotnet";
    }

    private static string RunDotNet(
        string dotnetPath,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        TimeSpan timeout,
        string operation)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = dotnetPath,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                throw new CSharpProjectLoadException($"Unable to start dotnet for project {operation}.");
            }
        }
        catch (Exception exception) when (exception is not CSharpProjectLoadException)
        {
            throw new CSharpProjectLoadException(
                $"Unable to start '{dotnetPath}' for project {operation}: {exception.Message}");
        }
        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
        Task<string> stderrTask = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(checked((int)Math.Min(timeout.TotalMilliseconds, int.MaxValue))))
        {
            process.Kill(entireProcessTree: true);
            throw new CSharpProjectLoadException(
                $"dotnet project {operation} exceeded the {timeout.TotalSeconds:0}-second timeout.");
        }
        Task.WaitAll(stdoutTask, stderrTask);
        string stdout = stdoutTask.Result;
        string stderr = stderrTask.Result;
        if (process.ExitCode != 0)
        {
            string detail = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
            if (detail.Length > 8_000)
            {
                detail = detail[^8_000..];
            }
            throw new CSharpProjectLoadException(
                $"dotnet project {operation} failed with exit code {process.ExitCode}:\n{detail.Trim()}");
        }
        return stdout;
    }
}
