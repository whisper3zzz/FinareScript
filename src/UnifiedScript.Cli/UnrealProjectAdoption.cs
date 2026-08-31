using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace UnifiedScript.Cli;

internal sealed record UnrealProjectAdoptionOptions(
    string ProjectPath,
    string? SdkRoot,
    string TemplateId,
    string ScriptName,
    string? ManagedNamespace);

internal sealed record UnrealProjectAdoptionSummary(
    int Total,
    int Create,
    int Update,
    int Unchanged,
    int Conflicts)
{
    public int Changed => Create + Update;
}

internal sealed record UnrealProjectAdoptionPlanEntry(
    string Path,
    string Kind,
    string Action,
    string? CurrentSha256,
    string DesiredSha256,
    string Reason);

internal sealed record UnrealProjectAdoptionPlanDocument(
    string SchemaVersion,
    string Mode,
    string Status,
    string Project,
    string SdkRoot,
    string Template,
    string ScriptName,
    string ManagedNamespace,
    string Fingerprint,
    UnrealProjectAdoptionSummary Summary,
    IReadOnlyList<UnrealProjectAdoptionPlanEntry> Files);

internal sealed record UnrealProjectAdoptionApplyResult(
    string Status,
    string? TransactionId,
    string? ReportPath,
    UnrealProjectAdoptionSummary Summary);

internal sealed record UnrealProjectDoctorCheck(
    string Id,
    string Status,
    string Message,
    string? Remediation = null);

internal sealed record UnrealProjectDoctorSummary(
    int Passed,
    int Warnings,
    int Failed);

internal sealed record UnrealProjectDoctorReport(
    string SchemaVersion,
    string Status,
    string Project,
    string? EngineRoot,
    UnrealProjectDoctorSummary Summary,
    IReadOnlyList<UnrealProjectDoctorCheck> Checks);

internal sealed class UnrealProjectAdoptionPlan
{
    public required UnrealProjectAdoptionPlanDocument Document { get; init; }

    public required string ProjectRoot { get; init; }

    public required IReadOnlyList<UnrealProjectAdoptionDesiredFile> DesiredFiles { get; init; }

    public required string? PreviousTransactionId { get; init; }
}

internal sealed record UnrealProjectAdoptionDesiredFile(
    string RelativePath,
    string FullPath,
    string Kind,
    string Action,
    string? CurrentSha256,
    string DesiredSha256,
    byte[] Content,
    string Reason);

internal sealed record UnrealProjectAdoptionTransactionEntry(
    string Path,
    string Kind,
    string Action,
    bool HadPrevious,
    string? PreviousSha256,
    string DesiredSha256);

internal sealed record UnrealProjectAdoptionTransaction(
    string SchemaVersion,
    string Status,
    string TransactionId,
    string Project,
    string SdkRoot,
    string Template,
    string ScriptName,
    string ManagedNamespace,
    string Fingerprint,
    string? PreviousTransactionId,
    IReadOnlyList<UnrealProjectAdoptionTransactionEntry> Files);

internal sealed record UnrealProjectAdoptionCurrentState(
    string SchemaVersion,
    string TransactionId);

internal static partial class UnrealProjectAdoptionService
{
    public const string PlanSchemaVersion = "unifiedscript.unreal-project-adoption-plan/v0.1";
    public const string TransactionSchemaVersion =
        "unifiedscript.unreal-project-adoption-transaction/v0.1";
    public const string CurrentStateSchemaVersion =
        "unifiedscript.unreal-project-adoption-current/v0.1";
    public const string DoctorSchemaVersion = "unifiedscript.unreal-project-doctor/v0.1";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private static readonly string[] RequiredRuntimeFiles =
    [
        "UnifiedScript.Core.dll",
        "UnifiedScript.Unreal.dll",
        "UnifiedScript.Unreal.deps.json",
    ];

    public static UnrealProjectAdoptionPlan CreatePlan(UnrealProjectAdoptionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        string projectPath = Path.GetFullPath(options.ProjectPath);
        if (!File.Exists(projectPath) ||
            !string.Equals(Path.GetExtension(projectPath), ".uproject", StringComparison.OrdinalIgnoreCase))
        {
            throw new FileNotFoundException($"Unreal project does not exist: {projectPath}", projectPath);
        }

        ValidateIdentifier(options.ScriptName, "Script name");
        string projectRoot = Path.GetDirectoryName(projectPath) ??
            throw new InvalidDataException("The Unreal project has no parent directory.");
        string projectName = Path.GetFileNameWithoutExtension(projectPath);
        ValidateIdentifier(projectName, "Project name");

        AdoptionSdkLayout layout = ResolveSdkLayout(options.SdkRoot);
        UnrealProjectAdoptionTransaction? previous = ReadCurrentTransaction(projectRoot);
        bool maintainProductNativeHost = previous?.Files.Any(static file =>
            file.Kind == "native-host") == true;
        ProjectMetadata metadata = ReadProjectMetadata(
            projectPath,
            projectName,
            maintainProductNativeHost);
        string managedNamespace = string.IsNullOrWhiteSpace(options.ManagedNamespace)
            ? $"{projectName}.UnifiedScript"
            : options.ManagedNamespace;
        ValidateNamespace(managedNamespace);
        AdoptionTemplate selectedTemplate = ReadTemplate(layout.TemplateRoot, options.TemplateId);
        string assemblyName = $"{projectName}.UnifiedScript";

        var desired = new Dictionary<string, DesiredFileBuilder>(PathComparer);
        AddDesired(
            desired,
            projectRoot,
            GetRelativePath(projectRoot, projectPath),
            "project-merge",
            MergeProductIntoProject(
                projectPath,
                metadata.RequiresProductNativeHost,
                metadata.ProjectModule));

        var replacements = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["PROJECT_NAME"] = projectName,
            ["PROJECT_IDENTIFIER"] = projectName,
            ["PROJECT_MODULE"] = metadata.ProjectModule,
            ["EDITOR_TARGET"] = metadata.EditorTarget,
            ["GAME_TARGET"] = metadata.GameTarget,
            ["SCRIPT_NAME"] = options.ScriptName,
            ["MANAGED_NAMESPACE"] = managedNamespace,
            ["ASSEMBLY_NAME"] = assemblyName,
        };
        AddRenderedTemplate(
            desired,
            projectRoot,
            Path.Combine(layout.TemplateRoot, "Config", "UnifiedScript.onboarding.json.template"),
            Path.Combine("Config", "UnifiedScript.onboarding.json"),
            "project-policy",
            replacements);
        AddRenderedTemplate(
            desired,
            projectRoot,
            Path.Combine(layout.TemplateRoot, "Contracts", "Project.unreal-api-contract.json.template"),
            Path.Combine("Contracts", $"{projectName}.unreal-api-contract.json"),
            "project-policy",
            replacements);
        AddRenderedTemplate(
            desired,
            projectRoot,
            Path.Combine(layout.TemplateRoot, "Deployment", "Project.unifiedscript-deployment.json.template"),
            Path.Combine("Deployment", $"{projectName}.unifiedscript-deployment.json"),
            "project-policy",
            replacements);
        AddRenderedTemplate(
            desired,
            projectRoot,
            Path.Combine(layout.TemplateRoot, "Managed", "Project.UnifiedScript.csproj.template"),
            Path.Combine("Managed", assemblyName, $"{assemblyName}.csproj"),
            "managed-project",
            replacements);
        AddRenderedTemplate(
            desired,
            projectRoot,
            Path.Combine(layout.TemplateRoot, "Managed", "Project.UnifiedScript.slnx.template"),
            Path.Combine("Managed", assemblyName, $"{assemblyName}.slnx"),
            "ide",
            replacements);
        AddRenderedTemplate(
            desired,
            projectRoot,
            Path.Combine(layout.TemplateRoot, "IDE", "Project.UnifiedScript.code-workspace.template"),
            $"{projectName}.UnifiedScript.code-workspace",
            "ide",
            replacements);
        AddRenderedTemplate(
            desired,
            projectRoot,
            Path.Combine(layout.TemplateRoot, "IDE", "UnifiedScript.ide.json.template"),
            Path.Combine("Managed", "UnifiedScript.ide.json"),
            "ide",
            replacements);
        AddRenderedTemplate(
            desired,
            projectRoot,
            Path.Combine(layout.TemplateRoot, selectedTemplate.ScriptTemplate),
            Path.Combine("Managed", assemblyName, $"{options.ScriptName}.cs"),
            "managed-source",
            replacements);
        if (metadata.RequiresProductNativeHost)
        {
            AddRenderedTemplate(
                desired,
                projectRoot,
                Path.Combine(layout.TemplateRoot, "Source", "Project.Build.cs.template"),
                Path.Combine("Source", projectName, $"{projectName}.Build.cs"),
                "native-host",
                replacements);
            AddRenderedTemplate(
                desired,
                projectRoot,
                Path.Combine(layout.TemplateRoot, "Source", "Project.cpp.template"),
                Path.Combine("Source", projectName, $"{projectName}.cpp"),
                "native-host",
                replacements);
            AddRenderedTemplate(
                desired,
                projectRoot,
                Path.Combine(layout.TemplateRoot, "Source", "Project.Target.cs.template"),
                Path.Combine("Source", $"{projectName}.Target.cs"),
                "native-host",
                replacements);
            AddRenderedTemplate(
                desired,
                projectRoot,
                Path.Combine(layout.TemplateRoot, "Source", "ProjectEditor.Target.cs.template"),
                Path.Combine("Source", $"{projectName}Editor.Target.cs"),
                "native-host",
                replacements);
        }

        foreach (string sourcePath in EnumeratePluginPayload(layout.PluginRoot))
        {
            string relative = GetRelativePath(layout.PluginRoot, sourcePath);
            string installedRelative = Path.Combine("Plugins", "UEUnifiedScript", relative);
            string normalizedInstalledRelative = NormalizeRelative(installedRelative);
            bool isGeneratedSource = IsMutableGeneratedPluginSource(relative);
            string installedPath = ResolveContainedPath(projectRoot, normalizedInstalledRelative);
            bool preserveGeneratedSource = isGeneratedSource &&
                previous?.Files.Any(entry =>
                    entry.Kind == "generated-source" &&
                    string.Equals(
                        entry.Path,
                        normalizedInstalledRelative,
                        PathComparison)) == true &&
                File.Exists(installedPath);
            AddDesired(
                desired,
                projectRoot,
                installedRelative,
                isGeneratedSource ? "generated-source" : "plugin",
                File.ReadAllBytes(preserveGeneratedSource ? installedPath : sourcePath));
        }
        foreach (string sourcePath in Directory.EnumerateFiles(layout.RuntimeRoot, "*", SearchOption.TopDirectoryOnly)
                     .OrderBy(static path => path, PathComparer))
        {
            AddDesired(
                desired,
                projectRoot,
                Path.Combine("Plugins", "UEUnifiedScript", "Binaries", "Managed", Path.GetFileName(sourcePath)),
                "runtime",
                File.ReadAllBytes(sourcePath));
        }
        foreach (string sourcePath in Directory.EnumerateFiles(
                     layout.CliRoot,
                     "*",
                     SearchOption.AllDirectories).OrderBy(static path => path, PathComparer))
        {
            AddDesired(
                desired,
                projectRoot,
                Path.Combine(
                    "Plugins",
                    "UEUnifiedScript",
                    "Tools",
                    "UnifiedScript.Cli",
                    GetRelativePath(layout.CliRoot, sourcePath)),
                "tool",
                File.ReadAllBytes(sourcePath));
        }
        foreach (string sourcePath in Directory.EnumerateFiles(
                     layout.TemplateRoot,
                     "*",
                     SearchOption.AllDirectories).OrderBy(static path => path, PathComparer))
        {
            AddDesired(
                desired,
                projectRoot,
                Path.Combine(
                    "Plugins",
                    "UEUnifiedScript",
                    "Tools",
                    "Templates",
                    "UnrealProject",
                    GetRelativePath(layout.TemplateRoot, sourcePath)),
                "tool",
                File.ReadAllBytes(sourcePath));
        }
        foreach (string sourcePath in Directory.EnumerateFiles(
                     layout.DocumentationRoot,
                     "*",
                     SearchOption.AllDirectories).OrderBy(static path => path, PathComparer))
        {
            AddDesired(
                desired,
                projectRoot,
                Path.Combine(
                    "Plugins",
                    "UEUnifiedScript",
                    "Tools",
                    "Documentation",
                    GetRelativePath(layout.DocumentationRoot, sourcePath)),
                "documentation",
                File.ReadAllBytes(sourcePath));
        }
        AddDesired(
            desired,
            projectRoot,
            Path.Combine("Plugins", "UEUnifiedScript", "Tools", "unifiedscript.cmd"),
            "tool",
            File.ReadAllBytes(layout.CliWrapperPath));
        AddDesired(
            desired,
            projectRoot,
            Path.Combine("Plugins", "UEUnifiedScript", "Tools", "setup-unreal-project.ps1"),
            "tool",
            File.ReadAllBytes(layout.OnboardingScriptPath));
        AddDesired(
            desired,
            projectRoot,
            Path.Combine("Plugins", "UEUnifiedScript", "Tools", "UnifiedScript.BuildGraph.psm1"),
            "tool",
            File.ReadAllBytes(layout.BuildGraphModulePath));
        AddDesired(
            desired,
            projectRoot,
            Path.Combine(
                "Plugins",
                "UEUnifiedScript",
                "Tools",
                "run-unifiedscript-onboarding-after-process.ps1"),
            "tool",
            File.ReadAllBytes(layout.DeferredOnboardingScriptPath));
        AddDesired(
            desired,
            projectRoot,
            Path.Combine(
                "Plugins",
                "UEUnifiedScript",
                "Tools",
                "start-unifiedscript-authoring.ps1"),
            "tool",
            File.ReadAllBytes(layout.AuthoringScriptPath));
        foreach (string sourcePath in Directory.EnumerateFiles(
                     layout.AuthoringWorkbenchRoot,
                     "*",
                     SearchOption.AllDirectories).OrderBy(static path => path, PathComparer))
        {
            AddDesired(
                desired,
                projectRoot,
                Path.Combine(
                    "Plugins",
                    "UEUnifiedScript",
                    "Authoring",
                    "Workbench",
                    GetRelativePath(layout.AuthoringWorkbenchRoot, sourcePath)),
                "authoring",
                File.ReadAllBytes(sourcePath));
        }

        foreach (string required in RequiredRuntimeFiles)
        {
            if (!desired.ContainsKey(NormalizeRelative(Path.Combine(
                    "Plugins", "UEUnifiedScript", "Binaries", "Managed", required))))
            {
                throw new InvalidDataException($"UnifiedScript SDK runtime is incomplete: {required}");
            }
        }

        var files = new List<UnrealProjectAdoptionDesiredFile>(desired.Count);
        foreach (DesiredFileBuilder item in desired.Values.OrderBy(static value => value.RelativePath, StringComparer.Ordinal))
        {
            byte[]? current = File.Exists(item.FullPath) ? File.ReadAllBytes(item.FullPath) : null;
            string? currentHash = current is null ? null : Hash(current);
            string desiredHash = Hash(item.Content);
            string action;
            string reason;
            if (current is null)
            {
                action = "create";
                reason = "The required product file does not exist.";
            }
            else if (current.AsSpan().SequenceEqual(item.Content))
            {
                action = "unchanged";
                reason = "The current file already matches the desired content.";
            }
            else if (item.Kind == "project-merge")
            {
                action = "update";
                reason = "The UEUnifiedScript plugin entry will be merged while preserving other project fields.";
            }
            else if (IsOwnedAndUnmodified(previous, item.RelativePath, currentHash))
            {
                action = "update";
                reason = "The file matches the last adopted version and can be upgraded safely.";
            }
            else
            {
                action = "conflict";
                reason = "The existing file is not owned by the current adoption transaction or was modified afterward.";
            }

            files.Add(new UnrealProjectAdoptionDesiredFile(
                item.RelativePath,
                item.FullPath,
                item.Kind,
                action,
                currentHash,
                desiredHash,
                item.Content,
                reason));
        }

        UnrealProjectAdoptionSummary summary = Summarize(files);
        string fingerprint = Hash(Encoding.UTF8.GetBytes(string.Join(
            "\n",
            files.Select(static file =>
                $"{file.RelativePath}\0{file.Kind}\0{file.Action}\0{file.CurrentSha256}\0{file.DesiredSha256}"))));
        var document = new UnrealProjectAdoptionPlanDocument(
            PlanSchemaVersion,
            "preview",
            summary.Conflicts == 0 ? "ready" : "blocked",
            projectPath,
            layout.SdkRoot,
            selectedTemplate.Id,
            options.ScriptName,
            managedNamespace,
            fingerprint,
            summary,
            files.Select(static file => new UnrealProjectAdoptionPlanEntry(
                file.RelativePath,
                file.Kind,
                file.Action,
                file.CurrentSha256,
                file.DesiredSha256,
                file.Reason)).ToArray());
        return new UnrealProjectAdoptionPlan
        {
            Document = document,
            ProjectRoot = projectRoot,
            DesiredFiles = files,
            PreviousTransactionId = previous?.TransactionId,
        };
    }

    public static UnrealProjectAdoptionApplyResult Apply(
        UnrealProjectAdoptionPlan plan,
        Action<int>? beforeCommitEntry = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.Document.Summary.Conflicts != 0)
        {
            throw new InvalidDataException(
                $"Adoption is blocked by {plan.Document.Summary.Conflicts} existing-file conflict(s). Run --preview and preserve or move those files.");
        }

        UnrealProjectAdoptionDesiredFile[] changed = plan.DesiredFiles
            .Where(static file => file.Action is "create" or "update")
            .ToArray();
        ValidateCurrentPlanInputs(plan.DesiredFiles);
        if (changed.Length == 0)
        {
            return new UnrealProjectAdoptionApplyResult("unchanged", null, null, plan.Document.Summary);
        }

        string transactionId = $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}"[..24];
        string adoptionRoot = GetAdoptionRoot(plan.ProjectRoot);
        string transactionRoot = Path.Combine(adoptionRoot, "transactions", transactionId);
        string stagingRoot = Path.Combine(transactionRoot, "staging");
        string backupRoot = Path.Combine(transactionRoot, "backup");
        Directory.CreateDirectory(stagingRoot);
        Directory.CreateDirectory(backupRoot);

        var entries = new List<UnrealProjectAdoptionTransactionEntry>(changed.Length);
        var committed = new List<CommittedFile>(changed.Length);
        try
        {
            foreach (UnrealProjectAdoptionDesiredFile file in changed)
            {
                string stagePath = ResolveContainedPath(stagingRoot, file.RelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(stagePath)!);
                File.WriteAllBytes(stagePath, file.Content);
                if (!string.Equals(Hash(File.ReadAllBytes(stagePath)), file.DesiredSha256, StringComparison.Ordinal))
                {
                    throw new IOException($"Staged adoption file failed integrity verification: {file.RelativePath}");
                }

                bool hadPrevious = File.Exists(file.FullPath);
                if (hadPrevious)
                {
                    string backupPath = ResolveContainedPath(backupRoot, file.RelativePath);
                    Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
                    File.Copy(file.FullPath, backupPath, overwrite: false);
                }
                entries.Add(new UnrealProjectAdoptionTransactionEntry(
                    file.RelativePath,
                    file.Kind,
                    file.Action,
                    hadPrevious,
                    file.CurrentSha256,
                    file.DesiredSha256));
            }

            for (int index = 0; index < changed.Length; index++)
            {
                beforeCommitEntry?.Invoke(index);
                UnrealProjectAdoptionDesiredFile file = changed[index];
                string stagePath = ResolveContainedPath(stagingRoot, file.RelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(file.FullPath)!);
                string displacedPath = Path.Combine(
                    transactionRoot,
                    "displaced",
                    index.ToString("D6", System.Globalization.CultureInfo.InvariantCulture));
                bool hadPrevious = File.Exists(file.FullPath);
                if (hadPrevious)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(displacedPath)!);
                    File.Move(file.FullPath, displacedPath);
                }
                try
                {
                    File.Move(stagePath, file.FullPath);
                }
                catch
                {
                    if (hadPrevious && File.Exists(displacedPath))
                    {
                        File.Move(displacedPath, file.FullPath);
                    }
                    throw;
                }
                committed.Add(new CommittedFile(file.FullPath, displacedPath, hadPrevious));
            }

            var transaction = new UnrealProjectAdoptionTransaction(
                TransactionSchemaVersion,
                "applied",
                transactionId,
                plan.Document.Project,
                plan.Document.SdkRoot,
                plan.Document.Template,
                plan.Document.ScriptName,
                plan.Document.ManagedNamespace,
                plan.Document.Fingerprint,
                plan.PreviousTransactionId,
                entries);
            string reportPath = Path.Combine(transactionRoot, "transaction.json");
            WriteJsonAtomically(reportPath, transaction);
            WriteJsonAtomically(
                Path.Combine(adoptionRoot, "current.json"),
                new UnrealProjectAdoptionCurrentState(CurrentStateSchemaVersion, transactionId));

            foreach (CommittedFile file in committed)
            {
                TryDeleteFile(file.DisplacedPath);
            }
            TryDeleteDirectory(stagingRoot);
            TryDeleteDirectory(Path.Combine(transactionRoot, "displaced"));
            return new UnrealProjectAdoptionApplyResult(
                "applied",
                transactionId,
                reportPath,
                plan.Document.Summary);
        }
        catch
        {
            RestoreCommittedFiles(committed);
            TryDeleteDirectory(transactionRoot);
            throw;
        }
    }

    public static UnrealProjectAdoptionApplyResult Rollback(
        string projectPath,
        string? requestedTransactionId = null)
    {
        string fullProjectPath = Path.GetFullPath(projectPath);
        if (!File.Exists(fullProjectPath))
        {
            throw new FileNotFoundException($"Unreal project does not exist: {fullProjectPath}", fullProjectPath);
        }
        string projectRoot = Path.GetDirectoryName(fullProjectPath)!;
        string adoptionRoot = GetAdoptionRoot(projectRoot);
        UnrealProjectAdoptionCurrentState current = ReadJson<UnrealProjectAdoptionCurrentState>(
            Path.Combine(adoptionRoot, "current.json"));
        ValidateSchema(current.SchemaVersion, CurrentStateSchemaVersion, "adoption current state");
        string transactionId = requestedTransactionId ?? current.TransactionId;
        if (!string.Equals(current.TransactionId, transactionId, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Only the current adoption transaction can be rolled back. Current: {current.TransactionId}");
        }

        string transactionRoot = ResolveContainedPath(
            Path.Combine(adoptionRoot, "transactions"),
            transactionId);
        string transactionPath = Path.Combine(transactionRoot, "transaction.json");
        UnrealProjectAdoptionTransaction transaction =
            ReadJson<UnrealProjectAdoptionTransaction>(transactionPath);
        ValidateSchema(transaction.SchemaVersion, TransactionSchemaVersion, "adoption transaction");
        if (transaction.Status != "applied")
        {
            throw new InvalidDataException(
                $"Adoption transaction '{transactionId}' is not applied: {transaction.Status}");
        }

        foreach (UnrealProjectAdoptionTransactionEntry entry in transaction.Files)
        {
            string target = ResolveContainedPath(projectRoot, entry.Path);
            if (!File.Exists(target) ||
                entry.Kind != "generated-source" &&
                !string.Equals(Hash(File.ReadAllBytes(target)), entry.DesiredSha256, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Rollback would overwrite post-adoption changes: {entry.Path}");
            }
            if (entry.HadPrevious)
            {
                string backup = ResolveContainedPath(Path.Combine(transactionRoot, "backup"), entry.Path);
                if (!File.Exists(backup) ||
                    !string.Equals(Hash(File.ReadAllBytes(backup)), entry.PreviousSha256, StringComparison.Ordinal))
                {
                    throw new InvalidDataException($"Rollback backup is missing or corrupt: {entry.Path}");
                }
            }
        }

        string rollbackRoot = Path.Combine(transactionRoot, $"rollback-{Guid.NewGuid():N}");
        string restoreRoot = Path.Combine(rollbackRoot, "restore");
        string currentRoot = Path.Combine(rollbackRoot, "current");
        var restored = new List<RollbackFile>(transaction.Files.Count);
        try
        {
            foreach (UnrealProjectAdoptionTransactionEntry entry in transaction.Files)
            {
                if (!entry.HadPrevious)
                {
                    continue;
                }
                string source = ResolveContainedPath(Path.Combine(transactionRoot, "backup"), entry.Path);
                string stage = ResolveContainedPath(restoreRoot, entry.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(stage)!);
                File.Copy(source, stage, overwrite: false);
            }

            foreach (UnrealProjectAdoptionTransactionEntry entry in transaction.Files.Reverse())
            {
                string target = ResolveContainedPath(projectRoot, entry.Path);
                string savedCurrent = ResolveContainedPath(currentRoot, entry.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(savedCurrent)!);
                File.Move(target, savedCurrent);
                if (entry.HadPrevious)
                {
                    string stage = ResolveContainedPath(restoreRoot, entry.Path);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Move(stage, target);
                }
                restored.Add(new RollbackFile(target, savedCurrent, entry.HadPrevious));
            }

            string currentPath = Path.Combine(adoptionRoot, "current.json");
            CommitRollbackMetadata(
                transactionPath,
                transaction with { Status = "rolledBack" },
                currentPath,
                transaction.PreviousTransactionId);
            TryDeleteDirectory(rollbackRoot);
            RemoveEmptyProductDirectories(projectRoot, transaction.Files);
            return new UnrealProjectAdoptionApplyResult(
                "rolledBack",
                transactionId,
                transactionPath,
                new UnrealProjectAdoptionSummary(
                    transaction.Files.Count,
                    0,
                    transaction.Files.Count,
                    0,
                    0));
        }
        catch
        {
            foreach (RollbackFile file in restored.AsEnumerable().Reverse())
            {
                TryDeleteFile(file.TargetPath);
                if (File.Exists(file.SavedCurrentPath))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(file.TargetPath)!);
                    File.Move(file.SavedCurrentPath, file.TargetPath);
                }
            }
            TryDeleteDirectory(rollbackRoot);
            throw;
        }
    }

    public static UnrealProjectDoctorReport Diagnose(string projectPath, string? engineRoot)
    {
        string fullProjectPath = Path.GetFullPath(projectPath);
        var checks = new List<UnrealProjectDoctorCheck>();
        if (!File.Exists(fullProjectPath) ||
            !string.Equals(Path.GetExtension(fullProjectPath), ".uproject", StringComparison.OrdinalIgnoreCase))
        {
            checks.Add(new UnrealProjectDoctorCheck(
                "project",
                "failed",
                $"Unreal project does not exist: {fullProjectPath}",
                "Pass an existing .uproject path."));
            return CreateDoctorReport(fullProjectPath, null, checks);
        }

        string projectRoot = Path.GetDirectoryName(fullProjectPath)!;
        JsonObject? project = ParseProject(fullProjectPath);
        bool pluginEnabled = FindPlugin(project, "UEUnifiedScript") is JsonObject plugin &&
            plugin["Enabled"]?.GetValue<bool>() == true;
        checks.Add(pluginEnabled
            ? new UnrealProjectDoctorCheck("plugin-enabled", "passed", "UEUnifiedScript is enabled in the project.")
            : new UnrealProjectDoctorCheck(
                "plugin-enabled",
                "failed",
                "UEUnifiedScript is not explicitly enabled in the project.",
                "Run unifiedscript adopt <Game.uproject>."));

        string pluginRoot = Path.Combine(projectRoot, "Plugins", "UEUnifiedScript");
        string descriptor = Path.Combine(pluginRoot, "UEUnifiedScript.uplugin");
        checks.Add(File.Exists(descriptor)
            ? new UnrealProjectDoctorCheck("plugin-payload", "passed", "The source plugin payload is installed.")
            : new UnrealProjectDoctorCheck(
                "plugin-payload",
                "failed",
                $"The installed plugin descriptor is missing: {descriptor}",
                "Run unifiedscript adopt from a complete SDK."));

        string managedRuntime = Path.Combine(pluginRoot, "Binaries", "Managed");
        string[] missingRuntime = RequiredRuntimeFiles
            .Where(file => !File.Exists(Path.Combine(managedRuntime, file)))
            .ToArray();
        checks.Add(missingRuntime.Length == 0
            ? new UnrealProjectDoctorCheck("managed-runtime", "passed", "Required managed runtime files are installed.")
            : new UnrealProjectDoctorCheck(
                "managed-runtime",
                "failed",
                $"Managed runtime closure is missing: {string.Join(", ", missingRuntime)}",
                "Re-run unifiedscript adopt from the intended SDK."));

        string projectCli = Path.Combine(
            pluginRoot,
            "Tools",
            "UnifiedScript.Cli",
            "UnifiedScript.Cli.dll");
        string projectCliWrapper = Path.Combine(pluginRoot, "Tools", "unifiedscript.cmd");
        string projectOnboarding = Path.Combine(
            pluginRoot,
            "Tools",
            "setup-unreal-project.ps1");
        string projectBuildGraph = Path.Combine(
            pluginRoot,
            "Tools",
            "UnifiedScript.BuildGraph.psm1");
        string projectDeferredOnboarding = Path.Combine(
            pluginRoot,
            "Tools",
            "run-unifiedscript-onboarding-after-process.ps1");
        string projectAuthoring = Path.Combine(
            pluginRoot,
            "Tools",
            "start-unifiedscript-authoring.ps1");
        string projectAuthoringWorkbench = Path.Combine(
            pluginRoot,
            "Authoring",
            "Workbench",
            "index.html");
        checks.Add(File.Exists(projectCli) &&
                   File.Exists(projectCliWrapper) &&
                   File.Exists(projectOnboarding) &&
                   File.Exists(projectBuildGraph) &&
                   File.Exists(projectDeferredOnboarding) &&
                   File.Exists(projectAuthoring) &&
                   File.Exists(projectAuthoringWorkbench)
            ? new UnrealProjectDoctorCheck(
                "project-tools",
                "passed",
                "Project-local adopt/doctor/build/authoring tools are installed for editor use.")
            : new UnrealProjectDoctorCheck(
                "project-tools",
                "failed",
                "The project-local UnifiedScript CLI, dependency-aware build graph, onboarding tool, authoring launcher, or Workbench is missing.",
                "Re-run unifiedscript adopt from the intended SDK."));

        string configPath = Path.Combine(projectRoot, "Config", "UnifiedScript.onboarding.json");
        checks.Add(ReadSchemaCheck(
            "onboarding-config",
            configPath,
            "unifiedscript.unreal-project-onboarding/v0.1",
            "Run unifiedscript adopt to create the project-owned onboarding configuration."));
        string projectName = Path.GetFileNameWithoutExtension(fullProjectPath);
        string deploymentPath = Path.Combine(
            projectRoot,
            "Deployment",
            $"{projectName}.unifiedscript-deployment.json");
        checks.Add(ReadSchemaCheck(
            "deployment-manifest",
            deploymentPath,
            "unifiedscript.unreal-script-deployment/v0.1",
            "Run unifiedscript adopt to create the project-owned deployment manifest."));
        string[] managedProjects = Directory.Exists(Path.Combine(projectRoot, "Managed"))
            ? Directory.GetFiles(Path.Combine(projectRoot, "Managed"), "*.csproj", SearchOption.AllDirectories)
            : [];
        checks.Add(managedProjects.Length > 0
            ? new UnrealProjectDoctorCheck("managed-project", "passed", $"Found {managedProjects.Length} managed project(s).")
            : new UnrealProjectDoctorCheck(
                "managed-project",
                "failed",
                "No UnifiedScript managed project was found.",
                "Run unifiedscript adopt to create the first managed project and script."));

        string ideManifestPath = Path.Combine(projectRoot, "Managed", "UnifiedScript.ide.json");
        checks.Add(ReadSchemaCheck(
            "ide-manifest",
            ideManifestPath,
            "unifiedscript.ide-workspace/v0.1",
            "Run unifiedscript adopt to regenerate the project-local IDE manifest."));
        string solutionPath = Path.Combine(
            projectRoot,
            "Managed",
            $"{projectName}.UnifiedScript",
            $"{projectName}.UnifiedScript.slnx");
        string workspacePath = Path.Combine(
            projectRoot,
            $"{projectName}.UnifiedScript.code-workspace");
        string documentationRoot = Path.Combine(
            pluginRoot,
            "Tools",
            "Documentation");
        checks.Add(File.Exists(solutionPath) && File.Exists(workspacePath) &&
                   Directory.Exists(documentationRoot) &&
                   Directory.EnumerateFiles(documentationRoot, "*", SearchOption.AllDirectories).Any()
            ? new UnrealProjectDoctorCheck(
                "ide-workspace",
                "passed",
                "Project-local solution, editor workspace, debugger configuration, and documentation are installed.")
            : new UnrealProjectDoctorCheck(
                "ide-workspace",
                "failed",
                "The UnifiedScript solution, editor workspace, debugger configuration, or installed documentation is missing.",
                "Run unifiedscript adopt to regenerate the complete IDE workspace."));

        AddAdoptionStateChecks(projectRoot, checks);
        string? resolvedEngine = ResolveEngineRoot(engineRoot, project?["EngineAssociation"]?.GetValue<string>());
        if (resolvedEngine is null)
        {
            checks.Add(new UnrealProjectDoctorCheck(
                "engine",
                "warning",
                "No Unreal Engine root could be resolved.",
                "Pass --engine-root, set UNIFIEDSCRIPT_UE_ROOT, or register EngineAssociation."));
        }
        else
        {
            string[] engineInputs =
            [
                Path.Combine(resolvedEngine, "Engine", "Build", "BatchFiles", "Build.bat"),
                Path.Combine(resolvedEngine, "Engine", "Binaries", "Win64", "UnrealEditor-Cmd.exe"),
                Path.Combine(resolvedEngine, "Engine", "Build", "Build.version"),
            ];
            string[] missingEngine = engineInputs.Where(static path => !File.Exists(path)).ToArray();
            if (missingEngine.Length != 0)
            {
                checks.Add(new UnrealProjectDoctorCheck(
                    "engine",
                    "failed",
                    $"Resolved Unreal Engine is incomplete: {string.Join(", ", missingEngine)}",
                    "Select a complete supported Unreal Engine installation."));
            }
            else
            {
                string buildVersionPath = Path.Combine(
                    resolvedEngine,
                    "Engine",
                    "Build",
                    "Build.version");
                try
                {
                    using JsonDocument version = JsonDocument.Parse(File.ReadAllText(buildVersionPath));
                    int major = version.RootElement.GetProperty("MajorVersion").GetInt32();
                    int minor = version.RootElement.GetProperty("MinorVersion").GetInt32();
                    int patch = version.RootElement.GetProperty("PatchVersion").GetInt32();
                    checks.Add(major == 5 && minor == 8
                        ? new UnrealProjectDoctorCheck(
                            "engine",
                            "passed",
                            $"Unreal Engine {major}.{minor}.{patch} is usable at {resolvedEngine}.")
                        : new UnrealProjectDoctorCheck(
                            "engine",
                            "failed",
                            $"Resolved Unreal Engine {major}.{minor}.{patch} is outside the supported 5.8 family.",
                            "Use a validated UE 5.8 installation or wait for an explicit compatibility row."));
                }
                catch (Exception exception) when (exception is
                    JsonException or KeyNotFoundException or InvalidOperationException)
                {
                    checks.Add(new UnrealProjectDoctorCheck(
                        "engine",
                        "failed",
                        $"Unreal Build.version is invalid: {exception.Message}",
                        "Repair or select a complete Unreal Engine installation."));
                }
            }
        }

        string generatedRoot = Path.Combine(projectRoot, "Generated");
        string? coveragePath = Directory.Exists(generatedRoot)
            ? Directory.EnumerateFiles(
                    generatedRoot,
                    "*.binding-coverage.json",
                    SearchOption.TopDirectoryOnly)
                .OrderBy(static path => path, PathComparer)
                .FirstOrDefault()
            : null;
        checks.Add(coveragePath is not null
            ? ReadSchemaCheck(
                "generated-surface",
                coveragePath,
                "unifiedscript.unreal-binding-coverage/v0.2",
                "Regenerate bindings from the adopted API Contract.")
            : new UnrealProjectDoctorCheck(
                "generated-surface",
                "warning",
                "Adoption is complete, but the first reflection/binding onboarding run has not produced coverage yet.",
                "Run the normal UnifiedScript project build/onboarding action before packaging."));

        string managedAssembly = Path.Combine(
            projectRoot,
            "Plugins",
            "UEUnifiedScript",
            "Binaries",
            "Managed",
            "Scripts",
            $"{projectName}.UnifiedScript.dll");
        checks.Add(File.Exists(managedAssembly)
            ? new UnrealProjectDoctorCheck(
                "cook-closure",
                "passed",
                "The first project managed assembly is staged for cook/package.")
            : new UnrealProjectDoctorCheck(
                "cook-closure",
                "warning",
                "The adopted managed project has not been built into the plugin staging directory yet.",
                "Run the normal UnifiedScript project build/onboarding action before packaging."));

        return CreateDoctorReport(fullProjectPath, resolvedEngine, checks);
    }

    public static string SerializePlan(UnrealProjectAdoptionPlanDocument plan) => Serialize(plan);

    public static string SerializeDoctor(UnrealProjectDoctorReport report) => Serialize(report);

    public static void WriteReport(string path, string content)
    {
        string fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath) ?? Environment.CurrentDirectory);
        string temporary = fullPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporary, content, new UTF8Encoding(false));
            File.Move(temporary, fullPath, overwrite: true);
        }
        finally
        {
            TryDeleteFile(temporary);
        }
    }

    private static UnrealProjectDoctorReport CreateDoctorReport(
        string projectPath,
        string? engineRoot,
        IReadOnlyList<UnrealProjectDoctorCheck> checks)
    {
        int passed = checks.Count(static check => check.Status == "passed");
        int warnings = checks.Count(static check => check.Status == "warning");
        int failed = checks.Count(static check => check.Status == "failed");
        return new UnrealProjectDoctorReport(
            DoctorSchemaVersion,
            failed == 0 ? "passed" : "failed",
            projectPath,
            engineRoot,
            new UnrealProjectDoctorSummary(passed, warnings, failed),
            checks);
    }

    private static void AddAdoptionStateChecks(
        string projectRoot,
        ICollection<UnrealProjectDoctorCheck> checks)
    {
        UnrealProjectAdoptionTransaction? transaction;
        try
        {
            transaction = ReadCurrentTransaction(projectRoot);
        }
        catch (Exception exception) when (exception is IOException or JsonException or InvalidDataException)
        {
            checks.Add(new UnrealProjectDoctorCheck(
                "adoption-state",
                "failed",
                exception.Message,
                "Restore the Saved/UnifiedScript/Adoption state or re-adopt after preserving project files."));
            return;
        }
        if (transaction is null)
        {
            checks.Add(new UnrealProjectDoctorCheck(
                "adoption-state",
                "warning",
                "No current adoption transaction is recorded.",
                "Run unifiedscript adopt to enable deterministic upgrades and rollback."));
            return;
        }

        var drift = new List<string>();
        foreach (UnrealProjectAdoptionTransactionEntry entry in transaction.Files
                     .Where(static entry => entry.Kind is "plugin" or "runtime" or "tool"))
        {
            string target = ResolveContainedPath(projectRoot, entry.Path);
            if (!File.Exists(target) ||
                !string.Equals(Hash(File.ReadAllBytes(target)), entry.DesiredSha256, StringComparison.Ordinal))
            {
                drift.Add(entry.Path);
            }
        }
        checks.Add(drift.Count == 0
            ? new UnrealProjectDoctorCheck(
                "adoption-state",
                "passed",
                $"Adoption transaction {transaction.TransactionId} owns an intact product payload.")
            : new UnrealProjectDoctorCheck(
                "adoption-state",
                "failed",
                $"Installed product payload drifted after adoption: {string.Join(", ", drift)}",
                "Preserve intentional edits elsewhere, then re-run unifiedscript adopt from the intended SDK."));
    }

    private static UnrealProjectDoctorCheck ReadSchemaCheck(
        string id,
        string path,
        string expectedSchema,
        string remediation)
    {
        if (!File.Exists(path))
        {
            return new UnrealProjectDoctorCheck(id, "failed", $"Required file is missing: {path}", remediation);
        }
        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
            string? schema = document.RootElement.GetProperty("schemaVersion").GetString();
            return schema == expectedSchema
                ? new UnrealProjectDoctorCheck(id, "passed", $"{Path.GetFileName(path)} uses {expectedSchema}.")
                : new UnrealProjectDoctorCheck(
                    id,
                    "failed",
                    $"{Path.GetFileName(path)} uses unsupported schema '{schema}'.",
                    remediation);
        }
        catch (Exception exception) when (exception is
            JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return new UnrealProjectDoctorCheck(id, "failed", $"Invalid JSON in {path}: {exception.Message}", remediation);
        }
    }

    private static string? ResolveEngineRoot(string? requestedRoot, string? association)
    {
        string? candidate = requestedRoot;
        if (string.IsNullOrWhiteSpace(candidate))
        {
            candidate = Environment.GetEnvironmentVariable("UNIFIEDSCRIPT_UE_ROOT");
        }
        if (!string.IsNullOrWhiteSpace(candidate))
        {
            return Path.GetFullPath(candidate);
        }
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(association))
        {
            return null;
        }

        using (RegistryKey? customBuilds = Registry.CurrentUser.OpenSubKey(
                   @"Software\Epic Games\Unreal Engine\Builds"))
        {
            if (customBuilds?.GetValue(association) is string customPath &&
                !string.IsNullOrWhiteSpace(customPath))
            {
                return Path.GetFullPath(customPath);
            }
        }
        foreach (RegistryKey root in new[] { Registry.LocalMachine })
        {
            foreach (string keyPath in new[]
                     {
                         $@"SOFTWARE\EpicGames\Unreal Engine\{association}",
                         $@"SOFTWARE\WOW6432Node\EpicGames\Unreal Engine\{association}",
                     })
            {
                using RegistryKey? key = root.OpenSubKey(keyPath);
                if (key?.GetValue("InstalledDirectory") is string installed &&
                    !string.IsNullOrWhiteSpace(installed))
                {
                    return Path.GetFullPath(installed);
                }
            }
        }
        return null;
    }

    private static void RemoveEmptyProductDirectories(
        string projectRoot,
        IReadOnlyList<UnrealProjectAdoptionTransactionEntry> entries)
    {
        try
        {
            foreach (string directory in entries
                         .Select(entry => Path.GetDirectoryName(ResolveContainedPath(projectRoot, entry.Path)))
                         .Where(static path => path is not null)
                         .Cast<string>()
                         .Distinct(PathComparer)
                         .OrderByDescending(static path => path.Length))
            {
                string current = directory;
                while (IsContained(projectRoot, current) &&
                       !string.Equals(current, projectRoot, PathComparison) &&
                       Directory.Exists(current) &&
                       !Directory.EnumerateFileSystemEntries(current).Any())
                {
                    Directory.Delete(current);
                    current = Path.GetDirectoryName(current) ?? projectRoot;
                }
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void RestoreCommittedFiles(IReadOnlyList<CommittedFile> committed)
    {
        foreach (CommittedFile file in committed.AsEnumerable().Reverse())
        {
            TryDeleteFile(file.TargetPath);
            if (file.HadPrevious && File.Exists(file.DisplacedPath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file.TargetPath)!);
                File.Move(file.DisplacedPath, file.TargetPath);
            }
        }
    }

    private static void CommitRollbackMetadata(
        string transactionPath,
        UnrealProjectAdoptionTransaction transaction,
        string currentPath,
        string? previousTransactionId)
    {
        string nonce = Guid.NewGuid().ToString("N");
        string transactionStage = transactionPath + $".{nonce}.tmp";
        string transactionBackup = transactionPath + $".{nonce}.bak";
        string currentStage = currentPath + $".{nonce}.tmp";
        string currentBackup = currentPath + $".{nonce}.bak";
        bool transactionMoved = false;
        bool currentMoved = false;
        bool transactionInstalled = false;
        bool currentInstalled = false;
        try
        {
            File.WriteAllText(
                transactionStage,
                Serialize(transaction),
                new UTF8Encoding(false));
            if (previousTransactionId is not null)
            {
                File.WriteAllText(
                    currentStage,
                    Serialize(new UnrealProjectAdoptionCurrentState(
                        CurrentStateSchemaVersion,
                        previousTransactionId)),
                    new UTF8Encoding(false));
            }
            File.Move(transactionPath, transactionBackup);
            transactionMoved = true;
            File.Move(currentPath, currentBackup);
            currentMoved = true;
            File.Move(transactionStage, transactionPath);
            transactionInstalled = true;
            if (previousTransactionId is not null)
            {
                File.Move(currentStage, currentPath);
                currentInstalled = true;
            }
            TryDeleteFile(transactionBackup);
            TryDeleteFile(currentBackup);
        }
        catch
        {
            if (currentInstalled)
            {
                TryDeleteFile(currentPath);
            }
            if (transactionInstalled)
            {
                TryDeleteFile(transactionPath);
            }
            if (currentMoved && File.Exists(currentBackup))
            {
                File.Move(currentBackup, currentPath, overwrite: true);
            }
            if (transactionMoved && File.Exists(transactionBackup))
            {
                File.Move(transactionBackup, transactionPath, overwrite: true);
            }
            throw;
        }
        finally
        {
            TryDeleteFile(transactionStage);
            TryDeleteFile(currentStage);
            TryDeleteFile(transactionBackup);
            TryDeleteFile(currentBackup);
        }
    }

    private static void ValidateCurrentPlanInputs(IReadOnlyList<UnrealProjectAdoptionDesiredFile> files)
    {
        foreach (UnrealProjectAdoptionDesiredFile file in files)
        {
            string? actual = File.Exists(file.FullPath) ? Hash(File.ReadAllBytes(file.FullPath)) : null;
            if (!string.Equals(actual, file.CurrentSha256, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Adoption input changed after preview; regenerate the plan: {file.RelativePath}");
            }
        }
    }

    private static UnrealProjectAdoptionSummary Summarize(
        IReadOnlyList<UnrealProjectAdoptionDesiredFile> files) => new(
        files.Count,
        files.Count(static file => file.Action == "create"),
        files.Count(static file => file.Action == "update"),
        files.Count(static file => file.Action == "unchanged"),
        files.Count(static file => file.Action == "conflict"));

    private static bool IsOwnedAndUnmodified(
        UnrealProjectAdoptionTransaction? previous,
        string relativePath,
        string? currentHash)
    {
        if (previous is null || currentHash is null)
        {
            return false;
        }
        UnrealProjectAdoptionTransactionEntry? entry = previous.Files.FirstOrDefault(
            candidate => string.Equals(candidate.Path, relativePath, PathComparison));
        return entry is not null &&
            string.Equals(entry.DesiredSha256, currentHash, StringComparison.Ordinal);
    }

    private static UnrealProjectAdoptionTransaction? ReadCurrentTransaction(string projectRoot)
    {
        string adoptionRoot = GetAdoptionRoot(projectRoot);
        string currentPath = Path.Combine(adoptionRoot, "current.json");
        if (!File.Exists(currentPath))
        {
            return null;
        }
        UnrealProjectAdoptionCurrentState state = ReadJson<UnrealProjectAdoptionCurrentState>(currentPath);
        ValidateSchema(state.SchemaVersion, CurrentStateSchemaVersion, "adoption current state");
        string transactionPath = Path.Combine(
            ResolveContainedPath(Path.Combine(adoptionRoot, "transactions"), state.TransactionId),
            "transaction.json");
        UnrealProjectAdoptionTransaction transaction =
            ReadJson<UnrealProjectAdoptionTransaction>(transactionPath);
        ValidateSchema(transaction.SchemaVersion, TransactionSchemaVersion, "adoption transaction");
        if (transaction.Status != "applied" || transaction.TransactionId != state.TransactionId)
        {
            throw new InvalidDataException("The current adoption transaction is inconsistent.");
        }
        return transaction;
    }

    private static void ValidateSchema(string actual, string expected, string label)
    {
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Unsupported {label} schema '{actual}'. Expected '{expected}'.");
        }
    }

    private static T ReadJson<T>(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Required adoption state does not exist: {path}", path);
        }
        return JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOptions) ??
            throw new JsonException($"JSON document '{path}' deserialized to null.");
    }

    private static void WriteJsonAtomically<T>(string path, T value) =>
        WriteReport(path, Serialize(value));

    private static string Serialize<T>(T value) =>
        JsonSerializer.Serialize(value, JsonOptions).Replace("\r\n", "\n", StringComparison.Ordinal) + "\n";

    private static byte[] MergeProductIntoProject(
        string projectPath,
        bool addNativeHost,
        string projectModule)
    {
        JsonObject project = ParseProject(projectPath);
        JsonObject? existing = FindPlugin(project, "UEUnifiedScript");
        bool changed = false;
        if (existing is null)
        {
            JsonArray plugins;
            if (project["Plugins"] is null)
            {
                plugins = [];
                project["Plugins"] = plugins;
            }
            else
            {
                plugins = project["Plugins"] as JsonArray ??
                    throw new InvalidDataException("The .uproject Plugins field must be an array.");
            }
            plugins.Add(new JsonObject
            {
                ["Name"] = "UEUnifiedScript",
                ["Enabled"] = true,
            });
            changed = true;
        }
        else if (existing["Enabled"]?.GetValue<bool>() != true)
        {
            existing["Enabled"] = true;
            changed = true;
        }
        if (addNativeHost)
        {
            JsonArray modules;
            if (project["Modules"] is null)
            {
                modules = [];
                project["Modules"] = modules;
            }
            else
            {
                modules = project["Modules"] as JsonArray ??
                    throw new InvalidDataException("The .uproject Modules field must be an array.");
            }
            bool moduleExists = modules.OfType<JsonObject>().Any(module => string.Equals(
                module["Name"]?.GetValue<string>(),
                projectModule,
                StringComparison.OrdinalIgnoreCase));
            if (!moduleExists)
            {
                modules.Add(new JsonObject
                {
                    ["Name"] = projectModule,
                    ["Type"] = "Runtime",
                    ["LoadingPhase"] = "Default",
                });
                changed = true;
            }
        }
        if (!changed)
        {
            return File.ReadAllBytes(projectPath);
        }
        return Encoding.UTF8.GetBytes(
            project.ToJsonString(new JsonSerializerOptions { WriteIndented = true })
                .Replace("\r\n", "\n", StringComparison.Ordinal) + "\n");
    }

    private static JsonObject ParseProject(string projectPath)
    {
        try
        {
            return JsonNode.Parse(File.ReadAllText(projectPath), documentOptions: new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
            }) as JsonObject ?? throw new InvalidDataException("The .uproject root must be a JSON object.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"The .uproject file is invalid JSON: {exception.Message}", exception);
        }
    }

    private static JsonObject? FindPlugin(JsonObject project, string name)
    {
        if (project["Plugins"] is null)
        {
            return null;
        }
        JsonArray plugins = project["Plugins"] as JsonArray ??
            throw new InvalidDataException("The .uproject Plugins field must be an array.");
        JsonObject[] matches = plugins
            .OfType<JsonObject>()
            .Where(plugin => string.Equals(
                plugin["Name"]?.GetValue<string>(),
                name,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (matches.Length > 1)
        {
            throw new InvalidDataException($"The .uproject contains duplicate plugin entries for '{name}'.");
        }
        return matches.SingleOrDefault();
    }

    private static ProjectMetadata ReadProjectMetadata(
        string projectPath,
        string projectName,
        bool maintainProductNativeHost)
    {
        JsonObject project = ParseProject(projectPath);
        JsonArray? modules = project["Modules"] as JsonArray;
        bool hasNativeModule = modules is not null &&
            modules.OfType<JsonObject>().Any(module =>
                !string.IsNullOrWhiteSpace(module["Name"]?.GetValue<string>()));
        string projectModule = hasNativeModule
            ? modules!.OfType<JsonObject>()
                .Select(module => module["Name"]?.GetValue<string>())
                .FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value)) ?? projectName
            : projectName;
        ValidateIdentifier(projectModule, "Project module");
        string projectRoot = Path.GetDirectoryName(projectPath)!;
        string sourceRoot = Path.Combine(projectRoot, "Source");
        string? editorTargetFile = Directory.Exists(sourceRoot)
            ? Directory.EnumerateFiles(
                    sourceRoot,
                    "*Editor.Target.cs",
                    SearchOption.AllDirectories)
                .OrderBy(static path => path, PathComparer)
                .FirstOrDefault()
            : null;
        bool requiresProductNativeHost = !hasNativeModule || maintainProductNativeHost;
        string editorTarget = editorTargetFile is null
            ? requiresProductNativeHost ? $"{projectName}Editor" : "UnrealEditor"
            : Path.GetFileName(editorTargetFile)[..^".Target.cs".Length];
        ValidateIdentifier(editorTarget, "Editor target");
        string? gameTargetFile = Directory.Exists(sourceRoot)
            ? Directory.EnumerateFiles(
                    sourceRoot,
                    "*.Target.cs",
                    SearchOption.AllDirectories)
                .Where(static path => !Path.GetFileName(path).EndsWith(
                    "Editor.Target.cs",
                    StringComparison.OrdinalIgnoreCase))
                .OrderBy(static path => path, PathComparer)
                .FirstOrDefault()
            : null;
        string gameTarget = gameTargetFile is null
            ? requiresProductNativeHost ? projectName : "UnrealGame"
            : Path.GetFileName(gameTargetFile)[..^".Target.cs".Length];
        ValidateIdentifier(gameTarget, "Game target");
        return new ProjectMetadata(
            projectModule,
            editorTarget,
            gameTarget,
            requiresProductNativeHost);
    }

    private static AdoptionTemplate ReadTemplate(string templateRoot, string templateId)
    {
        string catalogPath = Path.Combine(templateRoot, "unifiedscript.project-templates.json");
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(catalogPath));
        JsonElement root = document.RootElement;
        if (root.GetProperty("schemaVersion").GetString() !=
            "unifiedscript.project-template-catalog/v0.1")
        {
            throw new InvalidDataException("The UnifiedScript project template catalog has an unsupported schema.");
        }
        JsonElement[] matches = root.GetProperty("templates")
            .EnumerateArray()
            .Where(template => template.GetProperty("id").GetString() == templateId)
            .ToArray();
        if (matches.Length != 1)
        {
            throw new InvalidDataException($"Unknown or duplicate UnifiedScript project template '{templateId}'.");
        }
        string scriptTemplate = matches[0].GetProperty("scriptTemplate").GetString() ??
            throw new InvalidDataException($"Project template '{templateId}' has no script template.");
        scriptTemplate = scriptTemplate.Replace('/', Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(scriptTemplate) ||
            scriptTemplate.Split(Path.DirectorySeparatorChar).Contains("..", StringComparer.Ordinal) ||
            !scriptTemplate.EndsWith(".template", StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Project template '{templateId}' has an unsafe script path.");
        }
        return new AdoptionTemplate(templateId, scriptTemplate);
    }

    private static AdoptionSdkLayout ResolveSdkLayout(string? requestedRoot)
    {
        IEnumerable<string> candidates;
        if (!string.IsNullOrWhiteSpace(requestedRoot))
        {
            candidates = [Path.GetFullPath(requestedRoot)];
        }
        else
        {
            var discovered = new List<string>();
            DirectoryInfo? current = new(AppContext.BaseDirectory);
            while (current is not null)
            {
                discovered.Add(current.FullName);
                current = current.Parent;
            }
            candidates = discovered;
        }

        foreach (string root in candidates.Distinct(PathComparer))
        {
            string packagedTemplates = Path.Combine(root, "Templates", "UnrealProject");
            string repositoryTemplates = Path.Combine(root, "templates", "UnrealProject");
            string embeddedTemplates = Path.Combine(root, "Tools", "Templates", "UnrealProject");
            string? templateRoot = Directory.Exists(packagedTemplates)
                ? packagedTemplates
                : Directory.Exists(repositoryTemplates)
                    ? repositoryTemplates
                    : Directory.Exists(embeddedTemplates) ? embeddedTemplates : null;
            string packagedPlugin = Path.Combine(root, "Plugins", "UEUnifiedScript");
            string repositoryPlugin = Path.Combine(root, "HostProject", "Plugins", "UEUnifiedScript");
            string? pluginRoot = File.Exists(Path.Combine(packagedPlugin, "UEUnifiedScript.uplugin"))
                ? packagedPlugin
                : File.Exists(Path.Combine(repositoryPlugin, "UEUnifiedScript.uplugin"))
                    ? repositoryPlugin
                    : File.Exists(Path.Combine(root, "UEUnifiedScript.uplugin")) ? root : null;
            string packagedRuntime = Path.Combine(root, "Managed", "Runtime");
            string repositoryRuntime = pluginRoot is null
                ? string.Empty
                : Path.Combine(pluginRoot, "Binaries", "Managed");
            string? runtimeRoot = Directory.Exists(packagedRuntime)
                ? packagedRuntime
                : Directory.Exists(repositoryRuntime) ? repositoryRuntime : null;
            string packagedCli = Path.Combine(root, "Tools", "UnifiedScript.Cli");
            string repositoryCli = Path.Combine(
                root,
                "src",
                "UnifiedScript.Cli",
                "bin",
                "Release",
                "net10.0");
            string? cliRoot = File.Exists(Path.Combine(packagedCli, "UnifiedScript.Cli.dll"))
                ? packagedCli
                : File.Exists(Path.Combine(repositoryCli, "UnifiedScript.Cli.dll"))
                    ? repositoryCli
                    : null;
            string packagedWrapper = Path.Combine(root, "Tools", "unifiedscript.cmd");
            string repositoryWrapper = Path.Combine(root, "tools", "unifiedscript.cmd");
            string? wrapper = File.Exists(packagedWrapper)
                ? packagedWrapper
                : File.Exists(repositoryWrapper) ? repositoryWrapper : null;
            string packagedOnboarding = Path.Combine(root, "Tools", "setup-unreal-project.ps1");
            string repositoryOnboarding = Path.Combine(root, "tools", "setup-unreal-project.ps1");
            string? onboarding = File.Exists(packagedOnboarding)
                ? packagedOnboarding
                : File.Exists(repositoryOnboarding) ? repositoryOnboarding : null;
            string packagedBuildGraph = Path.Combine(
                root,
                "Tools",
                "UnifiedScript.BuildGraph.psm1");
            string repositoryBuildGraph = Path.Combine(
                root,
                "tools",
                "UnifiedScript.BuildGraph.psm1");
            string? buildGraph = File.Exists(packagedBuildGraph)
                ? packagedBuildGraph
                : File.Exists(repositoryBuildGraph) ? repositoryBuildGraph : null;
            string packagedDeferredOnboarding = Path.Combine(
                root,
                "Tools",
                "run-unifiedscript-onboarding-after-process.ps1");
            string repositoryDeferredOnboarding = Path.Combine(
                root,
                "tools",
                "run-unifiedscript-onboarding-after-process.ps1");
            string? deferredOnboarding = File.Exists(packagedDeferredOnboarding)
                ? packagedDeferredOnboarding
                : File.Exists(repositoryDeferredOnboarding) ? repositoryDeferredOnboarding : null;
            string packagedAuthoring = Path.Combine(
                root,
                "Tools",
                "start-unifiedscript-authoring.ps1");
            string repositoryAuthoring = Path.Combine(
                root,
                "tools",
                "start-unifiedscript-authoring.ps1");
            string? authoring = File.Exists(packagedAuthoring)
                ? packagedAuthoring
                : File.Exists(repositoryAuthoring) ? repositoryAuthoring : null;
            string packagedAuthoringWorkbench = Path.Combine(root, "Authoring", "Workbench");
            string repositoryAuthoringWorkbench = Path.Combine(root, "prototype", "visual-editor");
            string? authoringWorkbench = File.Exists(Path.Combine(
                    packagedAuthoringWorkbench,
                    "index.html"))
                ? packagedAuthoringWorkbench
                : File.Exists(Path.Combine(repositoryAuthoringWorkbench, "index.html"))
                    ? repositoryAuthoringWorkbench
                    : null;
            string packagedDocumentation = Path.Combine(root, "Documentation");
            string repositoryDocumentation = Path.Combine(root, "docs");
            string embeddedDocumentation = Path.Combine(root, "Tools", "Documentation");
            string? documentationRoot = Directory.Exists(packagedDocumentation)
                ? packagedDocumentation
                : Directory.Exists(repositoryDocumentation)
                    ? repositoryDocumentation
                    : Directory.Exists(embeddedDocumentation) ? embeddedDocumentation : null;
            if (templateRoot is not null && pluginRoot is not null && runtimeRoot is not null &&
                cliRoot is not null && wrapper is not null && onboarding is not null &&
                buildGraph is not null &&
                deferredOnboarding is not null && authoring is not null &&
                authoringWorkbench is not null && documentationRoot is not null)
            {
                string catalog = Path.Combine(templateRoot, "unifiedscript.project-templates.json");
                if (File.Exists(catalog) && RequiredRuntimeFiles.All(file =>
                        File.Exists(Path.Combine(runtimeRoot, file))))
                {
                    return new AdoptionSdkLayout(
                        Path.GetFullPath(root),
                        Path.GetFullPath(templateRoot),
                        Path.GetFullPath(pluginRoot),
                        Path.GetFullPath(runtimeRoot),
                        Path.GetFullPath(cliRoot),
                        Path.GetFullPath(wrapper),
                        Path.GetFullPath(onboarding),
                        Path.GetFullPath(buildGraph),
                        Path.GetFullPath(deferredOnboarding),
                        Path.GetFullPath(authoring),
                        Path.GetFullPath(authoringWorkbench),
                        Path.GetFullPath(documentationRoot));
                }
            }
        }
        throw new DirectoryNotFoundException(
            "A complete UnifiedScript SDK/repository root could not be resolved. Pass --sdk-root.");
    }

    private static IEnumerable<string> EnumeratePluginPayload(string pluginRoot)
    {
        string descriptor = Path.Combine(pluginRoot, "UEUnifiedScript.uplugin");
        yield return descriptor;
        foreach (string path in Directory.EnumerateFiles(pluginRoot, "*", SearchOption.AllDirectories)
                     .OrderBy(static path => path, PathComparer))
        {
            string relative = NormalizeRelative(GetRelativePath(pluginRoot, path));
            string firstSegment = relative.Split('/')[0];
            if (firstSegment is "Authoring" or "Binaries" or "Intermediate" or "Saved" or "DerivedDataCache" or "Tools" ||
                string.Equals(path, descriptor, PathComparison))
            {
                continue;
            }
            yield return path;
        }
    }

    private static bool IsMutableGeneratedPluginSource(string relativePath)
    {
        string normalized = NormalizeRelative(relativePath);
        return normalized.StartsWith("Source/", StringComparison.OrdinalIgnoreCase) &&
            normalized.Contains("/Private/Generated/", StringComparison.OrdinalIgnoreCase) &&
            normalized.EndsWith(".generated.cpp", StringComparison.OrdinalIgnoreCase);
    }

    private static void AddRenderedTemplate(
        IDictionary<string, DesiredFileBuilder> desired,
        string projectRoot,
        string templatePath,
        string relativePath,
        string kind,
        IReadOnlyDictionary<string, string> replacements)
    {
        if (!File.Exists(templatePath))
        {
            throw new FileNotFoundException($"Required adoption template does not exist: {templatePath}", templatePath);
        }
        string text = File.ReadAllText(templatePath);
        foreach ((string key, string value) in replacements)
        {
            text = text.Replace($"__{key}__", value, StringComparison.Ordinal);
        }
        Match unresolved = TemplatePlaceholderRegex().Match(text);
        if (unresolved.Success)
        {
            throw new InvalidDataException(
                $"Adoption template '{templatePath}' has unresolved placeholder '{unresolved.Value}'.");
        }
        text = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        if (!text.EndsWith('\n'))
        {
            text += "\n";
        }
        AddDesired(desired, projectRoot, relativePath, kind, Encoding.UTF8.GetBytes(text));
    }

    private static void AddDesired(
        IDictionary<string, DesiredFileBuilder> desired,
        string projectRoot,
        string relativePath,
        string kind,
        byte[] content)
    {
        string normalized = NormalizeRelative(relativePath);
        string fullPath = ResolveContainedPath(projectRoot, normalized);
        if (!desired.TryAdd(normalized, new DesiredFileBuilder(normalized, fullPath, kind, content)))
        {
            throw new InvalidDataException($"Adoption plan contains duplicate target '{normalized}'.");
        }
    }

    private static string GetAdoptionRoot(string projectRoot) =>
        Path.Combine(projectRoot, "Saved", "UnifiedScript", "Adoption");

    private static string ResolveContainedPath(string root, string relativePath)
    {
        if (Path.IsPathRooted(relativePath))
        {
            throw new InvalidDataException($"Adoption path must be relative: {relativePath}");
        }
        string fullRoot = Path.GetFullPath(root);
        string target = Path.GetFullPath(Path.Combine(
            fullRoot,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsContained(fullRoot, target))
        {
            throw new InvalidDataException($"Adoption path escapes its root: {relativePath}");
        }
        return target;
    }

    private static bool IsContained(string root, string target)
    {
        string prefix = Path.GetFullPath(root).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(target).StartsWith(prefix, PathComparison);
    }

    private static string GetRelativePath(string root, string path) =>
        NormalizeRelative(Path.GetRelativePath(root, path));

    private static string NormalizeRelative(string path) =>
        path.Replace('\\', '/');

    private static string Hash(byte[] content) =>
        Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

    private static void ValidateIdentifier(string value, string label)
    {
        if (!IdentifierRegex().IsMatch(value))
        {
            throw new InvalidDataException($"{label} must be a C#/Unreal-safe identifier: {value}");
        }
    }

    private static void ValidateNamespace(string value)
    {
        if (!NamespaceRegex().IsMatch(value))
        {
            throw new InvalidDataException($"Managed namespace is invalid: {value}");
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierRegex();

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*(?:\\.[A-Za-z_][A-Za-z0-9_]*)*$", RegexOptions.CultureInvariant)]
    private static partial Regex NamespaceRegex();

    [GeneratedRegex("__[A-Z0-9_]+__", RegexOptions.CultureInvariant)]
    private static partial Regex TemplatePlaceholderRegex();

    private sealed record AdoptionSdkLayout(
        string SdkRoot,
        string TemplateRoot,
        string PluginRoot,
        string RuntimeRoot,
        string CliRoot,
        string CliWrapperPath,
        string OnboardingScriptPath,
        string BuildGraphModulePath,
        string DeferredOnboardingScriptPath,
        string AuthoringScriptPath,
        string AuthoringWorkbenchRoot,
        string DocumentationRoot);

    private sealed record AdoptionTemplate(string Id, string ScriptTemplate);

    private sealed record ProjectMetadata(
        string ProjectModule,
        string EditorTarget,
        string GameTarget,
        bool RequiresProductNativeHost);

    private sealed record DesiredFileBuilder(
        string RelativePath,
        string FullPath,
        string Kind,
        byte[] Content);

    private sealed record CommittedFile(string TargetPath, string DisplacedPath, bool HadPrevious);

    private sealed record RollbackFile(string TargetPath, string SavedCurrentPath, bool HadPrevious);
}
