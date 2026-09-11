using System.Diagnostics;
using System.Text.Json;
using Finare.Authoring;
using Finare.CSharp;
using Finare.Core;
using Finare.Graph;
using Finare.Runtime;
using Finare.Semantics;
using Finare.Unreal;

namespace Finare.Cli;

internal static class Program
{
    public static int Main(string[] args)
    {
        try
        {
            return Run(args);
        }
        catch (FileNotFoundException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 66;
        }
        catch (Exception exception) when (exception is
            JsonException or ArgumentException or InvalidDataException)
        {
            Console.Error.WriteLine(exception.Message);
            return 65;
        }
        catch (IOException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 74;
        }
    }

    private static int Run(IReadOnlyList<string> args)
    {
        if (args.Count > 0 && args[0] == "identity-init")
        {
            return RunIdentityInit(args);
        }

        if (args.Count > 0 && args[0] == "script-class-reference-plan")
        {
            return RunScriptClassReferencePlan(args);
        }

        if (args.Count > 0 && args[0] == "script-class-identity-plan")
        {
            return ScriptClassIdentityPlanCommand.Run(args);
        }

        if (args.Count > 0 && args[0] == "layout-init")
        {
            return RunLayout(args, synchronize: false);
        }

        if (args.Count > 0 && args[0] == "layout-sync")
        {
            return RunLayout(args, synchronize: true);
        }

        if (args.Count > 0 && args[0] == "semantic-diff")
        {
            return RunSemanticDiff(args);
        }

        if (args.Count > 0 && args[0] == "semantic-merge")
        {
            return RunSemanticMerge(args);
        }

        if (args.Count > 0 && args[0] == "ai-proposal-pack")
        {
            return RunAiProposalPack(args);
        }

        if (args.Count > 0 && args[0] == "apply-patch")
        {
            return RunApply(args, isPlan: false);
        }

        if (args.Count > 0 && args[0] == "apply-plan")
        {
            return RunApply(args, isPlan: true);
        }

        if (args.Count > 0 && args[0] is "apply-project-patch" or "apply-project-plan")
        {
            return RunProjectApply(args, isPlan: args[0] == "apply-project-plan");
        }

        if (args.Count > 0 && args[0] == "apply-declaration-patch")
        {
            return RunDeclarationApply(args);
        }

        if (args.Count > 0 && args[0] == "execute")
        {
            return RunExecute(args);
        }

        if (args.Count > 0 && args[0] == "chunk-pack")
        {
            return RunChunkPack(args);
        }

        if (args.Count > 0 && args[0] == "chunk-inspect")
        {
            return RunChunkInspect(args);
        }

        if (args.Count > 0 && args[0] == "chunk-profile")
        {
            return RunChunkProfile(args);
        }

        if (args.Count > 0 && args[0] == "chunk-first-tick-profile")
        {
            return RunChunkFirstTickProfile(args);
        }

        if (args.Count > 0 && args[0] is "upgrade-plan" or "upgrade-apply")
        {
            return RunUpgrade(args, isPlan: args[0] == "upgrade-plan");
        }

        if (args.Count > 0 && args[0] == "diagnostics-bundle")
        {
            return RunDiagnosticsBundle(args);
        }

        if (args.Count > 0 && args[0] == "adopt")
        {
            return RunUnrealProjectAdoption(args);
        }

        if (args.Count > 0 && args[0] == "doctor")
        {
            return RunUnrealProjectDoctor(args);
        }

        if (args.Count > 0 && args[0] == "guide")
        {
            return RunProductGuidance(args);
        }

        if (args.Count > 0 && args[0] == "debug-serve")
        {
            return RunDebugServe(args);
        }

        if (args.Count > 0 && args[0] == "debug-frame-serve")
        {
            return RunDebugFrameServe(args);
        }

        if (args.Count > 0 && args[0] == "ue-bindings")
        {
            return RunUnrealBindings(args);
        }

        if (args.Count > 0 && args[0] == "ue-contract-propose")
        {
            return RunUnrealContractProposal(args);
        }

        if (args.Count > 0 && args[0] == "ue-surface-expand")
        {
            return RunUnrealDevelopmentSurface(args);
        }

        if (args.Count > 0 && args[0] == "ue-reflection-hints")
        {
            return RunUnrealReflectionHints(args);
        }

        if (args.Count > 0 && args[0] == "ue-data-plane")
        {
            return RunUnrealDataPlane(args);
        }

        if (args.Count > 0 && args[0] == "ue-data-plane-extension")
        {
            return RunUnrealDataPlaneExtension(args);
        }

        if (args.Count > 0 && args[0] == "ue-puerts-inventory")
        {
            return RunUnrealPuertsInventory(args);
        }

        if (args.Count > 0 && args[0] == "ue-puerts-scan")
        {
            return RunUnrealPuertsScan(args);
        }

        if (args.Count > 0 && args[0] == "ue-puerts-plan")
        {
            return RunUnrealPuertsPlan(args);
        }

        if (args.Count > 0 && args[0] == "ue-puerts-coexist")
        {
            return RunUnrealPuertsCoexist(args);
        }

        if (args.Count > 0 && args[0] == "ue-puerts-verify")
        {
            return RunUnrealPuertsVerify(args);
        }

        if (args.Count > 0 && args[0] == "ue-profile-diagnose")
        {
            return RunUnrealProfileDiagnosis(args);
        }

        if (args.Count > 0 && args[0] == "ue-workload-compare")
        {
            return RunUnrealWorkloadComparison(args);
        }

        if (args.Count > 0 && args[0] == "ue-workload-budget")
        {
            return RunUnrealWorkloadBudget(args);
        }

        if (args.Count > 0 && args[0] == "ue-usability-budget")
        {
            return RunUnrealUsabilityBudget(args);
        }

        if (args.Count > 0 && args[0] == "ue-workload-assemble")
        {
            return RunUnrealWorkloadAssembly(args);
        }

        if (args.Count > 0 && args[0] == "ue-workload-capture")
        {
            return RunUnrealWorkloadCapture(args);
        }

        if (args.Count > 0 && args[0] == "verify-roundtrip")
        {
            return RunRoundTripVerification(args);
        }

        if (args.Count > 0 && args[0] is "emit" or "emit-files")
        {
            return RunEmit(args);
        }

        return RunAnalyze(args);
    }

    private static int RunProductGuidance(IReadOnlyList<string> args)
    {
        if (args.Count < 2 || args.Count > 5 ||
            args[1].StartsWith("--", StringComparison.Ordinal))
        {
            PrintUsage();
            return 64;
        }

        string topic = args[1];
        string? sdkRoot = null;
        bool writeJson = false;
        int index = 2;
        while (index < args.Count)
        {
            string option = args[index++];
            if (option == "--json" && !writeJson)
            {
                writeJson = true;
                continue;
            }
            if (option == "--sdk-root" && sdkRoot is null && index < args.Count &&
                !args[index].StartsWith("--", StringComparison.Ordinal))
            {
                sdkRoot = args[index++];
                continue;
            }
            PrintUsage();
            return 64;
        }

        ProductGuidanceDocument document = ProductGuidanceCatalog.Resolve(topic, sdkRoot);
        Console.WriteLine(writeJson
            ? ProductGuidanceCatalog.Serialize(document)
            : document.Path);
        return 0;
    }

    private static int RunSemanticDiff(IReadOnlyList<string> args)
    {
        if (!TryParseSemanticDiffArguments(args, out SemanticDiffOptions? options))
        {
            PrintUsage();
            return 64;
        }

        options = options ?? throw new InvalidOperationException(
            "Argument parsing succeeded without semantic diff options.");
        foreach (string input in new[] { options.BaselinePath, options.TargetPath })
        {
            if (!File.Exists(input))
            {
                Console.Error.WriteLine($"Semantic graph input does not exist: {input}");
                return 66;
            }
        }

        string outputPath = Path.GetFullPath(options.OutputPath);
        EnsureDifferentPath(
            options.BaselinePath,
            outputPath,
            "Semantic diff output cannot overwrite the baseline graph.");
        EnsureDifferentPath(
            options.TargetPath,
            outputPath,
            "Semantic diff output cannot overwrite the target graph.");
        SemanticGraphDiffDocument diff = new SemanticGraphDiffer().Compare(
            GraphJson.Deserialize(File.ReadAllText(options.BaselinePath)),
            GraphJson.Deserialize(File.ReadAllText(options.TargetPath)));
        WriteAtomically(outputPath, SemanticGraphReviewJson.SerializeDiff(diff) + "\n");
        Console.WriteLine(outputPath);
        Console.WriteLine(
            $"semantic changes={diff.Summary.ChangeCount}; " +
            $"functions +{diff.Summary.AddedFunctions} " +
            $"-{diff.Summary.RemovedFunctions} ~{diff.Summary.ModifiedFunctions}");
        return options.FailOnChange && diff.Summary.ChangeCount > 0 ? 3 : 0;
    }

    private static int RunSemanticMerge(IReadOnlyList<string> args)
    {
        if (!TryParseSemanticMergeArguments(args, out SemanticMergeOptions? options))
        {
            PrintUsage();
            return 64;
        }

        options = options ?? throw new InvalidOperationException(
            "Argument parsing succeeded without semantic merge options.");
        foreach (string input in new[]
                 {
                     options.BaselinePath,
                     options.OursPath,
                     options.TheirsPath,
                 })
        {
            if (!File.Exists(input))
            {
                Console.Error.WriteLine($"Semantic graph input does not exist: {input}");
                return 66;
            }
        }

        string reportPath = Path.GetFullPath(options.ReportPath);
        string outputPath = Path.GetFullPath(options.OutputPath);
        foreach (string input in new[]
                 {
                     options.BaselinePath,
                     options.OursPath,
                     options.TheirsPath,
                 })
        {
            EnsureDifferentPath(
                input,
                reportPath,
                "Semantic merge report cannot overwrite an input graph.");
            EnsureDifferentPath(
                input,
                outputPath,
                "Semantic merge output cannot overwrite an input graph.");
        }
        EnsureDifferentPath(
            reportPath,
            outputPath,
            "Semantic merge report and graph output require different paths.");

        SemanticGraphMergeResult result = new SemanticGraphMerger().Merge(
            GraphJson.Deserialize(File.ReadAllText(options.BaselinePath)),
            GraphJson.Deserialize(File.ReadAllText(options.OursPath)),
            GraphJson.Deserialize(File.ReadAllText(options.TheirsPath)));
        string report = SemanticGraphReviewJson.SerializeMerge(result.Document) + "\n";
        if (!result.Succeeded || result.MergedGraph is null)
        {
            WriteAtomically(reportPath, report);
            Console.WriteLine(reportPath);
            Console.Error.WriteLine(
                $"Semantic merge has {result.Document.Summary.ConflictCount} conflict(s); " +
                "no merged graph was written.");
            return 3;
        }

        WriteTransactionally(
        [
            new OutputFile(reportPath, report),
            new OutputFile(outputPath, GraphJson.Serialize(result.MergedGraph) + "\n"),
        ]);
        Console.WriteLine(reportPath);
        Console.WriteLine(outputPath);
        Console.WriteLine(
            $"semantic merge clean; ours={result.Document.Summary.OursResolutions}; " +
            $"theirs={result.Document.Summary.TheirsResolutions}; " +
            $"identical={result.Document.Summary.IdenticalResolutions}; " +
            $"removed={result.Document.Summary.RemovedFunctions}");
        return 0;
    }

    private static bool TryParseSemanticDiffArguments(
        IReadOnlyList<string> args,
        out SemanticDiffOptions? options)
    {
        options = null;
        if (args.Count < 5 || args[0] != "semantic-diff" ||
            args[1].StartsWith("--", StringComparison.Ordinal) ||
            args[2].StartsWith("--", StringComparison.Ordinal))
        {
            return false;
        }

        string? output = null;
        bool failOnChange = false;
        for (int index = 3; index < args.Count; index++)
        {
            if (args[index] == "--fail-on-change")
            {
                failOnChange = true;
                continue;
            }
            if (args[index] == "--out" && index + 1 < args.Count)
            {
                output = args[++index];
                continue;
            }
            return false;
        }

        if (string.IsNullOrWhiteSpace(output))
        {
            return false;
        }
        options = new SemanticDiffOptions(args[1], args[2], output, failOnChange);
        return true;
    }

    private static int RunAiProposalPack(IReadOnlyList<string> args)
    {
        if (!TryParseAiProposalPackArguments(args, out AiProposalPackOptions? options))
        {
            PrintUsage();
            return 64;
        }

        options = options ?? throw new InvalidOperationException(
            "Argument parsing succeeded without AI proposal options.");
        string planPath = Path.GetFullPath(options.PlanPath);
        if (!File.Exists(planPath))
        {
            Console.Error.WriteLine($"Semantic patch plan does not exist: {planPath}");
            return 66;
        }
        string outputPath = Path.GetFullPath(options.OutputPath);
        EnsureDifferentPath(planPath, outputPath, "AI proposal output");
        SemanticPatchPlanDocument plan = SemanticPatchPlanJson.Deserialize(
            File.ReadAllText(planPath));
        AiSemanticPatchProposalDocument proposal = AiSemanticPatchProposalDocument.Create(
            AiSemanticPatchProposalDraft.Create(
                options.Title,
                options.Intent,
                new AiSemanticPatchProposalProducer(
                    options.Producer,
                    options.ProducerVersion,
                    options.Model),
                plan));
        WriteAtomically(outputPath, AiSemanticPatchProposalJson.Serialize(proposal) + "\n");
        Console.WriteLine(outputPath);
        Console.WriteLine(proposal.ProposalId);
        Console.WriteLine($"typedSteps={proposal.Plan.Steps.Count}");
        return 0;
    }

    private static bool TryParseAiProposalPackArguments(
        IReadOnlyList<string> args,
        out AiProposalPackOptions? options)
    {
        options = null;
        if (args.Count < 12 || args[0] != "ai-proposal-pack" ||
            args[1].StartsWith("--", StringComparison.Ordinal))
        {
            return false;
        }

        string? title = null;
        string? intent = null;
        string? producer = null;
        string? producerVersion = null;
        string? model = null;
        string? output = null;
        for (int index = 2; index < args.Count; index++)
        {
            string option = args[index];
            if (index + 1 >= args.Count)
            {
                return false;
            }
            string value = args[++index];
            switch (option)
            {
                case "--title" when title is null:
                    title = value;
                    break;
                case "--intent" when intent is null:
                    intent = value;
                    break;
                case "--producer" when producer is null:
                    producer = value;
                    break;
                case "--producer-version" when producerVersion is null:
                    producerVersion = value;
                    break;
                case "--model" when model is null:
                    model = value;
                    break;
                case "--out" when output is null:
                    output = value;
                    break;
                default:
                    return false;
            }
        }

        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(intent) ||
            string.IsNullOrWhiteSpace(producer) || string.IsNullOrWhiteSpace(producerVersion) ||
            string.IsNullOrWhiteSpace(output))
        {
            return false;
        }
        options = new AiProposalPackOptions(
            args[1], title, intent, producer, producerVersion, model, output);
        return true;
    }

    private static bool TryParseSemanticMergeArguments(
        IReadOnlyList<string> args,
        out SemanticMergeOptions? options)
    {
        options = null;
        if (args.Count < 8 || args[0] != "semantic-merge" ||
            args.Skip(1).Take(3).Any(static value => value.StartsWith("--", StringComparison.Ordinal)))
        {
            return false;
        }

        string? report = null;
        string? output = null;
        for (int index = 4; index < args.Count; index++)
        {
            if (args[index] == "--report" && index + 1 < args.Count)
            {
                report = args[++index];
                continue;
            }
            if (args[index] == "--out" && index + 1 < args.Count)
            {
                output = args[++index];
                continue;
            }
            return false;
        }

        if (string.IsNullOrWhiteSpace(report) || string.IsNullOrWhiteSpace(output))
        {
            return false;
        }
        options = new SemanticMergeOptions(args[1], args[2], args[3], report, output);
        return true;
    }

    private static int RunDiagnosticsBundle(IReadOnlyList<string> args)
    {
        if (!TryParseDiagnosticsBundleArguments(
                args,
                out DiagnosticsBundleOptions? options))
        {
            PrintUsage();
            return 64;
        }

        options = options ?? throw new InvalidOperationException(
            "Argument parsing succeeded without diagnostics bundle options.");
        string inputDirectory = Path.GetFullPath(options.InputDirectory);
        if (!Directory.Exists(inputDirectory))
        {
            Console.Error.WriteLine(
                $"Diagnostics input directory does not exist: {inputDirectory}");
            return 66;
        }
        string policyPath = Path.GetFullPath(options.PolicyPath ?? Path.Combine(
            AppContext.BaseDirectory,
            "finare.diagnostics-policy.json"));
        if (!File.Exists(policyPath))
        {
            Console.Error.WriteLine($"Diagnostics policy does not exist: {policyPath}");
            return 66;
        }

        FinareDiagnosticsPolicy policy =
            FinareDiagnosticsPolicyJson.Deserialize(File.ReadAllText(policyPath));
        FinareDiagnosticsBundleResult result =
            new FinareDiagnosticsBundleBuilder().Build(inputDirectory, policy);
        string outputPath = Path.GetFullPath(options.OutputPath);
        WriteTransactionally([new OutputFile(outputPath, result.Archive)]);
        Console.WriteLine(outputPath);
        Console.WriteLine(result.Sha256);
        Console.WriteLine(
            $"reports={result.Manifest.ReportCount}; " +
            $"redactions={result.Manifest.Reports.Sum(static report => report.RedactionCount)}; " +
            $"privacy={result.Manifest.PrivacyMode}");
        return 0;
    }

    private static int RunUnrealProjectAdoption(IReadOnlyList<string> args)
    {
        if (!TryParseUnrealProjectAdoptionArguments(
                args,
                out UnrealProjectAdoptionCliOptions? options))
        {
            PrintUsage();
            return 64;
        }

        options = options ?? throw new InvalidOperationException(
            "Argument parsing succeeded without Unreal project adoption options.");
        if (options.Rollback)
        {
            UnrealProjectAdoptionApplyResult rollback =
                UnrealProjectAdoptionService.Rollback(
                    options.ProjectPath,
                    options.TransactionId);
            Console.WriteLine(
                $"adoption status={rollback.Status}; transaction={rollback.TransactionId}; " +
                $"restored={rollback.Summary.Update}");
            if (rollback.ReportPath is not null)
            {
                Console.WriteLine(rollback.ReportPath);
            }
            return 0;
        }

        UnrealProjectAdoptionPlan plan = UnrealProjectAdoptionService.CreatePlan(
            new UnrealProjectAdoptionOptions(
                options.ProjectPath,
                options.SdkRoot,
                options.TemplateId,
                options.ScriptName,
                options.ManagedNamespace));
        string serializedPlan = UnrealProjectAdoptionService.SerializePlan(plan.Document);
        if (options.ReportPath is not null)
        {
            UnrealProjectAdoptionService.WriteReport(options.ReportPath, serializedPlan);
            Console.WriteLine(Path.GetFullPath(options.ReportPath));
        }
        Console.WriteLine(
            $"adoption create={plan.Document.Summary.Create}; " +
            $"update={plan.Document.Summary.Update}; " +
            $"unchanged={plan.Document.Summary.Unchanged}; " +
            $"conflicts={plan.Document.Summary.Conflicts}");
        foreach (UnrealProjectAdoptionPlanEntry conflict in plan.Document.Files.Where(
                     static file => file.Action == "conflict"))
        {
            Console.Error.WriteLine($"conflict: {conflict.Path}: {conflict.Reason}");
        }
        if (options.Preview)
        {
            return plan.Document.Summary.Conflicts == 0 ? 0 : 3;
        }
        if (plan.Document.Summary.Conflicts != 0)
        {
            return 65;
        }

        UnrealProjectAdoptionApplyResult result = UnrealProjectAdoptionService.Apply(plan);
        Console.WriteLine($"adoption status={result.Status}; transaction={result.TransactionId ?? "none"}");
        if (result.ReportPath is not null)
        {
            Console.WriteLine(result.ReportPath);
        }
        return 0;
    }

    private static int RunUnrealProjectDoctor(IReadOnlyList<string> args)
    {
        if (!TryParseUnrealProjectDoctorArguments(
                args,
                out UnrealProjectDoctorCliOptions? options))
        {
            PrintUsage();
            return 64;
        }

        options = options ?? throw new InvalidOperationException(
            "Argument parsing succeeded without Unreal project doctor options.");
        UnrealProjectDoctorReport report = UnrealProjectAdoptionService.Diagnose(
            options.ProjectPath,
            options.EngineRoot);
        string reportPath = Path.GetFullPath(options.ReportPath ?? Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(options.ProjectPath)) ?? Environment.CurrentDirectory,
            "Saved",
            "Finare",
            "Adoption",
            "doctor-report.json"));
        UnrealProjectAdoptionService.WriteReport(
            reportPath,
            UnrealProjectAdoptionService.SerializeDoctor(report));
        Console.WriteLine(
            $"doctor passed={report.Summary.Passed}; warnings={report.Summary.Warnings}; " +
            $"failed={report.Summary.Failed}");
        foreach (UnrealProjectDoctorCheck check in report.Checks.Where(
                     static check => check.Status != "passed"))
        {
            Console.WriteLine($"{check.Status}: {check.Id}: {check.Message}");
        }
        Console.WriteLine(reportPath);
        return report.Summary.Failed == 0 ? 0 : 2;
    }

    private static int RunUpgrade(IReadOnlyList<string> args, bool isPlan)
    {
        if (!TryParseUpgradeArguments(args, isPlan, out UpgradeOptions? options))
        {
            PrintUsage();
            return 64;
        }

        options = options ?? throw new InvalidOperationException(
            "Argument parsing succeeded without compatibility upgrade options.");
        string inputPath = Path.GetFullPath(options.InputPath);
        if (!File.Exists(inputPath) && !Directory.Exists(inputPath))
        {
            Console.Error.WriteLine($"Upgrade input does not exist: {inputPath}");
            return 66;
        }

        string policyPath = Path.GetFullPath(options.PolicyPath ?? Path.Combine(
            AppContext.BaseDirectory,
            "finare.compatibility-policy.json"));
        if (!File.Exists(policyPath))
        {
            Console.Error.WriteLine($"Compatibility policy does not exist: {policyPath}");
            return 66;
        }

        FinareCompatibilityPolicy policy =
            FinareCompatibilityJson.DeserializePolicy(File.ReadAllText(policyPath));
        FinareUpgradePlan plan = FinareUpgradePlanner.CreatePlan(policy, inputPath);
        if (isPlan)
        {
            string outputPath = Path.GetFullPath(options.OutputPath);
            EnsureDifferentPath(
                inputPath,
                outputPath,
                "Upgrade plan output cannot overwrite its input.");
            WriteAtomically(outputPath, FinareCompatibilityJson.SerializePlan(plan));
            Console.WriteLine(outputPath);
            PrintUpgradeSummary(plan);
            return options.FailOnBlocked && plan.Summary.Blocked > 0 ? 65 : 0;
        }

        string outputDirectory = Path.GetFullPath(options.OutputPath);
        string reportPath = Path.GetFullPath(options.ReportPath ??
            Path.Combine(outputDirectory, "upgrade-report.json"));
        if (File.Exists(inputPath))
        {
            EnsureDifferentPath(
                inputPath,
                reportPath,
                "Upgrade report cannot overwrite its input.");
        }
        else
        {
            foreach (FinareUpgradePlanItem item in plan.Items)
            {
                EnsureDifferentPath(
                    Path.Combine(inputPath, item.Path.Replace('/', Path.DirectorySeparatorChar)),
                    reportPath,
                    "Upgrade report cannot overwrite a scanned input artifact.");
            }
        }
        var outputs = new List<OutputFile>
        {
            new(reportPath, FinareCompatibilityJson.SerializePlan(plan)),
        };
        FinareUpgradePlanItem[] nonRewritable = plan.Items
            .Where(static item => item.Disposition is
                FinareUpgradeDisposition.Regenerate or
                FinareUpgradeDisposition.Rebuild or
                FinareUpgradeDisposition.Blocked)
            .ToArray();
        if (nonRewritable.Length == 0)
        {
            bool inputIsFile = File.Exists(inputPath);
            foreach (FinareUpgradePlanItem item in plan.Items.Where(
                static item => item.Disposition == FinareUpgradeDisposition.Rewrite))
            {
                string sourcePath = inputIsFile
                    ? inputPath
                    : Path.Combine(inputPath, item.Path.Replace('/', Path.DirectorySeparatorChar));
                string targetPath = inputIsFile
                    ? Path.Combine(outputDirectory, Path.GetFileName(inputPath))
                    : ResolveOutputPath(outputDirectory, item.Path);
                EnsureDifferentPath(
                    sourcePath,
                    targetPath,
                    "Compatibility upgrades must be written to a separate path.");
                outputs.Add(new OutputFile(
                    targetPath,
                    FinareUpgradePlanner.Rewrite(
                        item.FamilyId,
                        File.ReadAllText(sourcePath))));
            }
        }

        WriteTransactionally(outputs);
        foreach (OutputFile output in outputs)
        {
            Console.WriteLine(Path.GetFullPath(output.Path));
        }
        PrintUpgradeSummary(plan);
        if (nonRewritable.Length > 0)
        {
            Console.Error.WriteLine(
                "Upgrade apply made no artifact changes because the plan contains " +
                "regenerate, rebuild, or blocked actions. Follow the report first.");
            return 65;
        }
        return 0;
    }

    private static void PrintUpgradeSummary(FinareUpgradePlan plan) => Console.WriteLine(
        $"scanned={plan.Summary.Scanned}; current={plan.Summary.Current}; " +
        $"rewrite={plan.Summary.Rewrite}; regenerate={plan.Summary.Regenerate}; " +
        $"rebuild={plan.Summary.Rebuild}; blocked={plan.Summary.Blocked}");

    private static int RunRoundTripVerification(IReadOnlyList<string> args)
    {
        if (!TryParseRoundTripArguments(args, out RoundTripOptions? options))
        {
            PrintUsage();
            return 64;
        }

        options = options ?? throw new InvalidOperationException(
            "Argument parsing succeeded without round-trip options.");
        var inputPaths = new List<string>
        {
            options.VisualSourcePath,
            options.CodeSourcePath,
            options.RequirementPath,
        };
        inputPaths.AddRange(options.ReferencePaths);
        if (options.VisualIdentityPath is not null)
        {
            inputPaths.Add(options.VisualIdentityPath);
        }
        if (options.CodeIdentityPath is not null)
        {
            inputPaths.Add(options.CodeIdentityPath);
        }
        foreach (string path in inputPaths)
        {
            if (!File.Exists(path))
            {
                Console.Error.WriteLine($"Round-trip input does not exist: {path}");
                return 66;
            }
        }

        IdentitySidecarDocument? visualIdentity = options.VisualIdentityPath is null
            ? null
            : LoadIdentitySidecar(options.VisualIdentityPath);
        IdentitySidecarDocument? codeIdentity = options.CodeIdentityPath is null
            ? null
            : LoadIdentitySidecar(options.CodeIdentityPath);
        string logicalPath = visualIdentity?.SourcePath ?? codeIdentity?.SourcePath ??
            Path.GetFileName(options.VisualSourcePath);
        if (visualIdentity is not null && codeIdentity is not null &&
            !string.Equals(
                visualIdentity.SourcePath,
                codeIdentity.SourcePath,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Visual and C# identity sidecars must describe the same logical source path.");
        }

        string[] references = options.ReferencePaths
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var visualRequest = new CSharpCompilationRequest(
            options.AssemblyName,
            [new CSharpSourceDocument(
                logicalPath,
                File.ReadAllText(options.VisualSourcePath),
                visualIdentity)],
            references);
        var codeRequest = new CSharpCompilationRequest(
            options.AssemblyName,
            [new CSharpSourceDocument(
                logicalPath,
                File.ReadAllText(options.CodeSourcePath),
                codeIdentity)],
            references);
        RoundTripRequirementDocument requirements = RoundTripRequirementJson.Deserialize(
            File.ReadAllText(options.RequirementPath));
        try
        {
            RoundTripEquivalenceReport report = new RoundTripEquivalenceVerifier().Verify(
                visualRequest,
                codeRequest,
                requirements.ToRequirements());
            string fullReportPath = Path.GetFullPath(options.ReportPath);
            WriteAtomically(
                fullReportPath,
                RoundTripEquivalenceJson.Serialize(report) + "\n");
            Console.WriteLine(fullReportPath);
            Console.WriteLine(
                $"Round-trip equivalence: {(report.Passed ? "PASS" : "FAIL")} " +
                $"(methods={report.Methods.Count}, assets={report.AssetEquivalent})");
            return report.Passed ? 0 : 3;
        }
        catch (ExecutionCompilationException exception)
        {
            foreach (UnifiedDiagnostic diagnostic in exception.Diagnostics)
            {
                Console.Error.WriteLine(FormatDiagnostic(diagnostic));
            }
            Console.Error.WriteLine(exception.Message);
            return 2;
        }
    }

    private static int RunProjectApply(IReadOnlyList<string> args, bool isPlan)
    {
        if (!TryParseProjectPatchArguments(args, isPlan, out ProjectPatchOptions? options))
        {
            PrintUsage();
            return 64;
        }

        options = options ?? throw new InvalidOperationException(
            "Argument parsing succeeded without project patch options.");
        if (!File.Exists(options.ProjectPath))
        {
            Console.Error.WriteLine($"C# project does not exist: {options.ProjectPath}");
            return 66;
        }
        if (!File.Exists(options.EditDocumentPath))
        {
            Console.Error.WriteLine(
                $"{(isPlan ? "Patch plan" : "Patch")} does not exist: " +
                options.EditDocumentPath);
            return 66;
        }

        CSharpCompilationRequest request;
        try
        {
            request = LoadAnalysisRequest(new AnalyzeOptions(
                [options.ProjectPath],
                null,
                options.EffectCatalogPath,
                options.AssemblyName,
                options.ReferencePaths,
                options.IdentityPaths,
                options.Configuration,
                options.TargetFramework,
                options.Restore,
                options.Build));
        }
        catch (CSharpProjectLoadException exception)
        {
            Console.Error.WriteLine($"error USCS3001: {exception.Message}");
            return 2;
        }

        string editJson = File.ReadAllText(options.EditDocumentPath);
        CSharpPatchResult result = isPlan
            ? new CSharpPatchPlanApplier().Apply(
                request,
                SemanticPatchPlanJson.Deserialize(editJson))
            : new CSharpSourcePatchApplier().Apply(
                request,
                SemanticPatchJson.Deserialize(editJson));
        foreach (UnifiedDiagnostic diagnostic in result.Diagnostics)
        {
            Console.Error.WriteLine(FormatDiagnostic(diagnostic));
        }
        if (!result.Succeeded || result.PatchedRequest is null || result.Analysis is null)
        {
            Console.Error.WriteLine(
                $"Project {(isPlan ? "patch plan" : "patch")} failed; no source was written.");
            return 3;
        }

        string fullOutputDirectory = Path.GetFullPath(options.OutputDirectory);
        var originalSources = request.Sources.ToDictionary(
            static source => source.LogicalPath,
            StringComparer.Ordinal);
        CSharpSourceDocument[] changedSources = result.PatchedRequest.Sources
            .Where(source => !originalSources.TryGetValue(
                    source.LogicalPath,
                    out CSharpSourceDocument? original) ||
                !string.Equals(source.Source, original.Source, StringComparison.Ordinal))
            .OrderBy(static source => source.LogicalPath, StringComparer.Ordinal)
            .ToArray();
        if (changedSources.Length == 0)
        {
            throw new InvalidOperationException("A successful project patch changed no source documents.");
        }

        Dictionary<string, IdentitySidecarDocument> updatedIdentities = result.Analysis.IdentitySidecars
            .ToDictionary(static sidecar => sidecar.SourcePath, StringComparer.Ordinal);
        var outputs = new List<OutputFile>();
        foreach (CSharpSourceDocument source in changedSources)
        {
            string sourceOutputPath = ResolveOutputPath(fullOutputDirectory, source.LogicalPath);
            outputs.Add(new OutputFile(sourceOutputPath, source.Source));
            if (updatedIdentities.TryGetValue(
                    source.LogicalPath,
                    out IdentitySidecarDocument? identity))
            {
                outputs.Add(new OutputFile(
                    sourceOutputPath + ".usids",
                    IdentitySidecarJson.Serialize(identity) + "\n"));
            }
        }
        WriteTransactionally(outputs);
        foreach (OutputFile output in outputs)
        {
            Console.WriteLine(Path.GetFullPath(output.Path));
        }
        return 0;
    }

    private static int RunUnrealBindings(IReadOnlyList<string> args)
    {
        if (!TryParseUnrealBindingArguments(args, out UnrealBindingOptions? options))
        {
            PrintUsage();
            return 64;
        }

        options = options ?? throw new InvalidOperationException(
            "Argument parsing succeeded without Unreal binding options.");
        if (!File.Exists(options.ManifestPath))
        {
            Console.Error.WriteLine($"Unreal reflection manifest does not exist: {options.ManifestPath}");
            return 66;
        }

        if (options.ContractPath is not null && !File.Exists(options.ContractPath))
        {
            Console.Error.WriteLine($"Unreal API contract does not exist: {options.ContractPath}");
            return 66;
        }
        if (options.NodeCatalogOutputPath is not null && options.ContractPath is null)
        {
            Console.Error.WriteLine("--nodes-out requires --contract so visual nodes share the shipped API allowlist.");
            return 64;
        }
        if (options.BrowserOutputPath is not null && options.ContractPath is null)
        {
            Console.Error.WriteLine("--browser-out requires --contract so the browser shares the shipped API allowlist.");
            return 64;
        }

        string fullManifestPath = Path.GetFullPath(options.ManifestPath);
        string fullOutputPath = Path.GetFullPath(options.OutputPath);
        string? fullNativeOutputPath = options.NativeOutputPath is null
            ? null
            : Path.GetFullPath(options.NativeOutputPath);
        string? fullReportPath = options.ReportPath is null
            ? null
            : Path.GetFullPath(options.ReportPath);
        string? fullContractPath = options.ContractPath is null
            ? null
            : Path.GetFullPath(options.ContractPath);
        string? fullNodeCatalogOutputPath = options.NodeCatalogOutputPath is null
            ? null
            : Path.GetFullPath(options.NodeCatalogOutputPath);
        string? fullBrowserOutputPath = options.BrowserOutputPath is null
            ? null
            : Path.GetFullPath(options.BrowserOutputPath);
        string[] inputPaths = fullContractPath is null
            ? [fullManifestPath]
            : [fullManifestPath, fullContractPath];
        string[] outputPaths = new[]
        {
            fullOutputPath,
            fullNativeOutputPath,
            fullReportPath,
            fullNodeCatalogOutputPath,
            fullBrowserOutputPath,
        }.Where(static path => path is not null).Cast<string>().ToArray();
        if (outputPaths.Distinct(StringComparer.OrdinalIgnoreCase).Count() != outputPaths.Length)
        {
            throw new ArgumentException("Unreal contract generation output paths must be distinct.");
        }
        foreach (string inputPath in inputPaths)
        {
            foreach (string outputPath in outputPaths)
            {
                EnsureDifferentPath(
                    inputPath,
                    outputPath,
                    "Unreal contract generation cannot overwrite its reflection or contract input.");
            }
        }

        UnrealReflectionManifest manifest = UnrealReflectionJson.Deserialize(
            File.ReadAllText(fullManifestPath));
        UnrealApiContractProjection? projection = null;
        string managedNamespace = options.Namespace;
        string nativeHeader = options.NativeHeader;
        string nativeNamespace = options.NativeNamespace;
        if (fullContractPath is not null)
        {
            UnrealApiContract contract = UnrealApiContractJson.Deserialize(
                File.ReadAllText(fullContractPath));
            projection = UnrealApiContractProjector.Project(manifest, contract);
            manifest = projection.Reflection;
            managedNamespace = projection.Contract.ManagedNamespace;
            nativeHeader = projection.Contract.NativeHeader;
            nativeNamespace = projection.Contract.NativeNamespace;
        }

        UnrealBindingGenerationResult result = new UnrealBindingGenerator().Generate(
            manifest,
            new UnrealBindingGeneratorOptions(managedNamespace));
        foreach (UnrealBindingDiagnostic diagnostic in result.Diagnostics)
        {
            Console.Error.WriteLine(
                $"warning {diagnostic.Code}: {diagnostic.Message} [{diagnostic.MemberPath}]");
        }

        UnrealNativeThunkGenerationResult? nativeResult = null;
        if (fullNativeOutputPath is not null || fullReportPath is not null ||
            fullBrowserOutputPath is not null || options.FailOnRejected)
        {
            nativeResult = new UnrealNativeThunkGenerator().Generate(
                projection?.NativeReflection ?? manifest,
                new UnrealNativeThunkGeneratorOptions(
                    nativeHeader,
                    nativeNamespace));
            foreach (UnrealNativeThunkDiagnostic diagnostic in nativeResult.Diagnostics)
            {
                Console.Error.WriteLine(
                    $"warning {diagnostic.Code}: {diagnostic.Message} [{diagnostic.MemberPath}]");
            }
        }
        UnrealBindingCoverageReport? coverage = null;
        if (fullReportPath is not null || fullBrowserOutputPath is not null ||
            options.FailOnRejected)
        {
            coverage = projection is null
                ? UnrealBindingCoverageReport.Create(manifest, result, nativeResult!)
                : UnrealBindingCoverageReport.Create(projection, result, nativeResult!);
        }
        if (options.FailOnRejected && coverage!.Summary.RejectedCount > 0)
        {
            Console.Error.WriteLine(
                $"Unreal binding generation rejected {coverage.Summary.RejectedCount} member(s). " +
                "See diagnostics or the coverage report for details.");
            return 65;
        }
        if (coverage is { Gate.Passed: false })
        {
            foreach (string diagnostic in coverage.Gate.Diagnostics)
            {
                Console.Error.WriteLine(diagnostic);
            }
            Console.Error.WriteLine("Unreal binding coverage regressed below the API Contract baseline.");
            return 65;
        }

        var outputs = new List<OutputFile>
        {
            new(fullOutputPath, result.Source),
        };
        if (fullNativeOutputPath is not null)
        {
            outputs.Add(new OutputFile(fullNativeOutputPath, nativeResult!.Source));
        }
        if (fullReportPath is not null)
        {
            outputs.Add(new OutputFile(fullReportPath, UnrealBindingCoverageJson.Serialize(coverage!)));
        }
        UnrealApiNodeCatalog? nodeCatalog =
            fullNodeCatalogOutputPath is not null || fullBrowserOutputPath is not null
                ? UnrealApiNodeCatalogGenerator.Generate(projection!)
                : null;
        if (fullNodeCatalogOutputPath is not null)
        {
            outputs.Add(new OutputFile(
                fullNodeCatalogOutputPath,
                UnrealApiNodeCatalogGenerator.Serialize(nodeCatalog!)));
        }
        if (fullBrowserOutputPath is not null)
        {
            UnrealApiBrowserDocument browser = UnrealApiBrowserGenerator.Generate(
                projection!,
                nodeCatalog!,
                coverage!);
            outputs.Add(new OutputFile(
                fullBrowserOutputPath,
                UnrealApiBrowserJson.Serialize(browser)));
        }
        WriteTransactionally(outputs);
        foreach (OutputFile output in outputs)
        {
            Console.WriteLine(Path.GetFullPath(output.Path));
        }
        return 0;
    }

    private static int RunUnrealContractProposal(IReadOnlyList<string> args)
    {
        if (!TryParseUnrealContractProposalArguments(
                args,
                out UnrealContractProposalOptions? options))
        {
            PrintUsage();
            return 64;
        }

        options = options ?? throw new InvalidOperationException(
            "Argument parsing succeeded without Unreal Contract proposal options.");
        string[] inputPaths =
        [
            Path.GetFullPath(options.ReflectionPath),
            Path.GetFullPath(options.RequestPath),
            Path.GetFullPath(options.BaseContractPath),
        ];
        foreach (string inputPath in inputPaths)
        {
            if (!File.Exists(inputPath))
            {
                Console.Error.WriteLine($"Unreal Contract proposal input does not exist: {inputPath}");
                return 66;
            }
        }

        string fullOutputPath = Path.GetFullPath(options.OutputPath);
        foreach (string inputPath in inputPaths)
        {
            EnsureDifferentPath(
                inputPath,
                fullOutputPath,
                "Unreal Contract proposal cannot overwrite an input artifact.");
        }

        UnrealReflectionManifest reflection = UnrealReflectionJson.Deserialize(
            File.ReadAllText(inputPaths[0]));
        UnrealPuertsContractRequestSet requests =
            UnrealPuertsMigrationPackageJson.DeserializeContractRequests(
                File.ReadAllText(inputPaths[1]));
        UnrealApiContract contract = UnrealApiContractJson.Deserialize(
            File.ReadAllText(inputPaths[2]));
        UnrealApiContractProposal proposal = new UnrealApiContractProposalGenerator().Generate(
            reflection,
            requests,
            contract);
        WriteTransactionally(
        [
            new OutputFile(fullOutputPath, UnrealApiContractProposalJson.Serialize(proposal)),
        ]);
        Console.WriteLine(fullOutputPath);
        Console.WriteLine(
            $"requests={proposal.Summary.RequestCount}; " +
            $"selected={proposal.Summary.SelectedCount}; " +
            $"already-selected={proposal.Summary.AlreadySelectedCount}; " +
            $"open={proposal.Summary.OpenDecisionCount}; review-required=true");
        if (options.FailOnOpenDecisions && proposal.Summary.OpenDecisionCount > 0)
        {
            Console.Error.WriteLine(
                "Unreal API Contract proposal has open decisions. Review the proposal before changing the shipped Contract.");
            return 65;
        }
        return 0;
    }

    private static int RunUnrealDevelopmentSurface(IReadOnlyList<string> args)
    {
        if (!TryParseUnrealDevelopmentSurfaceArguments(
                args,
                out UnrealDevelopmentSurfaceOptions? options))
        {
            PrintUsage();
            return 64;
        }

        options = options ?? throw new InvalidOperationException(
            "Argument parsing succeeded without Unreal development surface options.");
        string reflectionPath = Path.GetFullPath(options.ReflectionPath);
        string baseContractPath = Path.GetFullPath(options.BaseContractPath);
        string[] projectPaths = options.ProjectPaths.Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.Ordinal)
            .ToArray();
        foreach (string inputPath in new[] { reflectionPath, baseContractPath }.Concat(projectPaths))
        {
            if (!File.Exists(inputPath))
            {
                Console.Error.WriteLine($"Unreal development surface input does not exist: {inputPath}");
                return 66;
            }
        }

        string requestOutputPath = Path.GetFullPath(options.RequestOutputPath);
        string overlayOutputPath = Path.GetFullPath(options.OverlayOutputPath);
        string reportOutputPath = Path.GetFullPath(options.ReportOutputPath);
        string? lockOutputPath = options.LockOutputPath is null
            ? null
            : Path.GetFullPath(options.LockOutputPath);
        string? verifyLockPath = options.VerifyLockPath is null
            ? null
            : Path.GetFullPath(options.VerifyLockPath);
        if (verifyLockPath is not null && !File.Exists(verifyLockPath))
        {
            Console.Error.WriteLine($"Unreal generated surface lock does not exist: {verifyLockPath}");
            return 66;
        }
        string[] outputPaths = new[] { requestOutputPath, overlayOutputPath, reportOutputPath }
            .Concat(lockOutputPath is null ? [] : [lockOutputPath])
            .ToArray();
        if (outputPaths.Distinct(StringComparer.OrdinalIgnoreCase).Count() != outputPaths.Length)
        {
            throw new ArgumentException("Unreal development surface outputs must be distinct.");
        }
        foreach (string inputPath in new[] { reflectionPath, baseContractPath }.Concat(projectPaths))
        {
            foreach (string outputPath in outputPaths)
            {
                EnsureDifferentPath(
                    inputPath,
                    outputPath,
                    "Unreal development surface cannot overwrite an input artifact.");
            }
        }

        UnrealReflectionManifest reflection = UnrealReflectionJson.Deserialize(
            File.ReadAllText(reflectionPath));
        UnrealApiContract contract = UnrealApiContractJson.Deserialize(
            File.ReadAllText(baseContractPath));
        var sources = new List<CSharpSourceDocument>();
        var loadedRequests = new List<CSharpCompilationRequest>();
        foreach (string projectPath in projectPaths)
        {
            CSharpProjectLoadResult loaded;
            try
            {
                loaded = new CSharpProjectLoader().Load(
                    projectPath,
                    options: new CSharpProjectLoadOptions(
                        options.Configuration,
                        Restore: true,
                        Build: false,
                        DotNetPath: options.DotNetPath));
            }
            catch (CSharpProjectLoadException exception)
            {
                Console.Error.WriteLine(exception.Message);
                return 65;
            }
            loadedRequests.Add(loaded.Request);
            string prefix = Path.GetFileNameWithoutExtension(projectPath);
            sources.AddRange(loaded.Request.Sources.Select(source => new CSharpSourceDocument(
                $"{prefix}/{source.LogicalPath}",
                source.Source)));
        }

        string[] configurationKeys = loadedRequests
            .Select(static request => request.Settings.ConfigurationKey)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        bool[] defaultReferenceModes = loadedRequests
            .Select(static request => request.IncludeDefaultReferences)
            .Distinct()
            .ToArray();
        if (configurationKeys.Length != 1 || defaultReferenceModes.Length != 1)
        {
            Console.Error.WriteLine(
                "Managed projects in one Unreal surface expansion must use compatible C# " +
                "parse/compilation settings and default-reference policy.");
            return 65;
        }
        string[] referencePaths = loadedRequests
            .SelectMany(static request => request.ReferencePaths)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var scanRequest = new CSharpCompilationRequest(
            "Finare.ApiSurfaceScan",
            sources,
            referencePaths,
            settings: loadedRequests[0].Settings,
            runtimeReferencePaths: referencePaths,
            includeDefaultReferences: defaultReferenceModes[0]);

        UnrealApiDependencyRequestSet requests = new UnrealApiSourceDependencyScanner().Scan(
            contract.ContractName,
            scanRequest,
            reflection,
            contract);
        UnrealApiDevelopmentSurfaceResult result =
            new UnrealApiDevelopmentSurfacePlanner().Plan(reflection, requests, contract);
        if (verifyLockPath is not null && !result.Applied)
        {
            Console.Error.WriteLine(
                "USUE3202: generated surface lock cannot be verified while API decisions are open.");
            return 65;
        }
        UnrealApiGeneratedSurfaceLock? surfaceLock = result.Applied &&
            (lockOutputPath is not null || verifyLockPath is not null)
                ? UnrealApiGeneratedSurfaceLockJson.Create(
                    result.ProjectName,
                    reflection,
                    result)
                : null;
        if (verifyLockPath is not null && surfaceLock is not null)
        {
            UnrealApiGeneratedSurfaceLock expected = UnrealApiGeneratedSurfaceLockJson.Deserialize(
                File.ReadAllText(verifyLockPath));
            IReadOnlyList<string> differences = UnrealApiGeneratedSurfaceLockJson.Diff(
                expected,
                surfaceLock);
            if (differences.Count > 0)
            {
                Console.Error.WriteLine(
                    "USUE3201: generated surface lock is stale: " +
                    string.Join(", ", differences));
                return 65;
            }
        }
        var outputs = new List<OutputFile>
        {
            new(requestOutputPath, UnrealApiDependencyRequestJson.Serialize(requests)),
            new(reportOutputPath, UnrealApiDevelopmentSurfaceJson.Serialize(result)),
        };
        if (result.Applied)
        {
            outputs.Add(new OutputFile(
                overlayOutputPath,
                UnrealApiContractJson.Serialize(result.EffectiveContract)));
            if (lockOutputPath is not null && surfaceLock is not null)
            {
                outputs.Add(new OutputFile(
                    lockOutputPath,
                    UnrealApiGeneratedSurfaceLockJson.Serialize(surfaceLock)));
            }
        }
        WriteTransactionally(outputs);

        Console.WriteLine(requestOutputPath);
        if (result.Applied)
        {
            Console.WriteLine(overlayOutputPath);
        }
        Console.WriteLine(reportOutputPath);
        if (lockOutputPath is not null && surfaceLock is not null)
        {
            Console.WriteLine(lockOutputPath);
        }
        Console.WriteLine(
            $"requests={result.Summary.RequestCount}; " +
            $"selected={result.Summary.SelectedCount}; " +
            $"already-selected={result.Summary.AlreadySelectedCount}; " +
            $"open={result.Summary.OpenDecisionCount}; " +
            $"applied={result.Applied.ToString().ToLowerInvariant()}");
        if (options.FailOnOpenDecisions && !result.Applied)
        {
            Console.Error.WriteLine(
                "Unreal API development surface has open decisions. " +
                "No overlay Contract was written; inspect the source-linked report.");
            return 65;
        }
        return 0;
    }

    private static int RunUnrealReflectionHints(IReadOnlyList<string> args)
    {
        if (!TryParseUnrealReflectionHintArguments(args, out UnrealReflectionHintOptions? options))
        {
            PrintUsage();
            return 64;
        }

        options = options ?? throw new InvalidOperationException(
            "Argument parsing succeeded without Unreal reflection-hint options.");
        string baseContractPath = Path.GetFullPath(options.BaseContractPath);
        string outputPath = Path.GetFullPath(options.OutputPath);
        string[] projectPaths = options.ProjectPaths.Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.Ordinal)
            .ToArray();
        foreach (string inputPath in new[] { baseContractPath }.Concat(projectPaths))
        {
            if (!File.Exists(inputPath))
            {
                Console.Error.WriteLine($"Unreal reflection-hint input does not exist: {inputPath}");
                return 66;
            }
            EnsureDifferentPath(
                inputPath,
                outputPath,
                "Unreal reflection hints cannot overwrite an input artifact.");
        }

        UnrealApiContract contract = UnrealApiContractJson.Deserialize(
            File.ReadAllText(baseContractPath));
        var sources = new List<CSharpSourceDocument>();
        var loadedRequests = new List<CSharpCompilationRequest>();
        foreach (string projectPath in projectPaths)
        {
            CSharpProjectLoadResult loaded;
            try
            {
                loaded = new CSharpProjectLoader().Load(
                    projectPath,
                    options: new CSharpProjectLoadOptions(
                        options.Configuration,
                        Restore: true,
                        Build: false,
                        DotNetPath: options.DotNetPath));
            }
            catch (CSharpProjectLoadException exception)
            {
                Console.Error.WriteLine(exception.Message);
                return 65;
            }
            loadedRequests.Add(loaded.Request);
            string prefix = Path.GetFileNameWithoutExtension(projectPath);
            sources.AddRange(loaded.Request.Sources.Select(source => new CSharpSourceDocument(
                $"{prefix}/{source.LogicalPath}",
                source.Source)));
        }

        string[] configurationKeys = loadedRequests
            .Select(static request => request.Settings.ConfigurationKey)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        bool[] defaultReferenceModes = loadedRequests
            .Select(static request => request.IncludeDefaultReferences)
            .Distinct()
            .ToArray();
        if (configurationKeys.Length != 1 || defaultReferenceModes.Length != 1)
        {
            Console.Error.WriteLine(
                "Managed projects in one Unreal reflection-hint scan must use compatible C# " +
                "parse/compilation settings and default-reference policy.");
            return 65;
        }
        string[] referencePaths = loadedRequests
            .SelectMany(static request => request.ReferencePaths)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var scanRequest = new CSharpCompilationRequest(
            "Finare.ReflectionHintScan",
            sources,
            referencePaths,
            settings: loadedRequests[0].Settings,
            runtimeReferencePaths: referencePaths,
            includeDefaultReferences: defaultReferenceModes[0]);
        UnrealApiSourceTypeHintSet hints = new UnrealApiSourceTypeHintScanner().Scan(
            contract.ContractName,
            scanRequest,
            contract);
        WriteTransactionally(
            [new OutputFile(outputPath, UnrealApiSourceTypeHintJson.Serialize(hints))]);
        Console.WriteLine(outputPath);
        Console.WriteLine($"type-hints={hints.Hints.Count}; source={hints.SourceFingerprint}");
        return 0;
    }

    private static int RunUnrealDataPlane(IReadOnlyList<string> args)
    {
        if (!TryParseUnrealDataPlaneArguments(
                args,
                out UnrealDataPlaneOptions? options))
        {
            PrintUsage();
            return 64;
        }

        options = options ?? throw new InvalidOperationException(
            "Argument parsing succeeded without Unreal data-plane options.");
        if (!File.Exists(options.SchemaPath))
        {
            Console.Error.WriteLine(
                $"Unreal data-plane schema does not exist: {options.SchemaPath}");
            return 66;
        }

        string fullSchemaPath = Path.GetFullPath(options.SchemaPath);
        string fullManagedOutputPath = Path.GetFullPath(options.ManagedOutputPath);
        string fullNativeOutputPath = Path.GetFullPath(options.NativeOutputPath);
        EnsureDifferentPath(
            fullSchemaPath,
            fullManagedOutputPath,
            "Unreal data-plane generation cannot overwrite its schema input.");
        EnsureDifferentPath(
            fullSchemaPath,
            fullNativeOutputPath,
            "Unreal data-plane generation cannot overwrite its schema input.");
        EnsureDifferentPath(
            fullManagedOutputPath,
            fullNativeOutputPath,
            "Unreal data-plane managed and native outputs must be distinct.");

        UnrealDataPlaneLayoutSchema schema = UnrealDataPlaneLayoutJson.Deserialize(
            File.ReadAllText(fullSchemaPath));
        UnrealDataPlaneLayoutGenerationResult result =
            new UnrealDataPlaneLayoutGenerator().Generate(schema);
        var outputs = new List<OutputFile>
        {
            new(fullManagedOutputPath, result.ManagedSource),
            new(fullNativeOutputPath, result.NativeSource),
        };
        WriteTransactionally(outputs);
        Console.WriteLine(fullManagedOutputPath);
        Console.WriteLine(fullNativeOutputPath);
        Console.WriteLine($"schema-fingerprint=0x{result.SchemaFingerprint:X16}");
        return 0;
    }

    private static int RunUnrealDataPlaneExtension(IReadOnlyList<string> args)
    {
        if (!TryParseUnrealDataPlaneExtensionArguments(
                args,
                out UnrealDataPlaneExtensionOptions? options))
        {
            PrintUsage();
            return 64;
        }
        options = options ?? throw new InvalidOperationException(
            "Argument parsing succeeded without Unreal data-plane extension options.");
        if (!File.Exists(options.SchemaPath))
        {
            Console.Error.WriteLine(
                $"Unreal data-plane extension schema does not exist: {options.SchemaPath}");
            return 66;
        }

        string fullSchemaPath = Path.GetFullPath(options.SchemaPath);
        string fullManagedOutputPath = Path.GetFullPath(options.ManagedOutputPath);
        string fullNativeOutputPath = Path.GetFullPath(options.NativeOutputPath);
        string fullCatalogOutputPath = Path.GetFullPath(options.CatalogOutputPath);
        string[] paths =
        [
            fullSchemaPath,
            fullManagedOutputPath,
            fullNativeOutputPath,
            fullCatalogOutputPath,
        ];
        if (paths.Distinct(StringComparer.OrdinalIgnoreCase).Count() != paths.Length)
        {
            throw new ArgumentException(
                "Unreal data-plane extension inputs and outputs must use distinct paths.");
        }

        UnrealDataPlaneExtensionSchema schema = UnrealDataPlaneExtensionJson.Deserialize(
            File.ReadAllText(fullSchemaPath));
        UnrealDataPlaneExtensionGenerationResult result =
            new UnrealDataPlaneExtensionGenerator().Generate(schema);
        WriteTransactionally(
        [
            new(fullManagedOutputPath, result.ManagedSource),
            new(fullNativeOutputPath, result.NativeSource),
            new(fullCatalogOutputPath, result.CatalogJson),
        ]);
        Console.WriteLine(fullManagedOutputPath);
        Console.WriteLine(fullNativeOutputPath);
        Console.WriteLine(fullCatalogOutputPath);
        Console.WriteLine($"extension-fingerprint=0x{result.SchemaFingerprint:X16}");
        return 0;
    }

    private static int RunUnrealPuertsInventory(IReadOnlyList<string> args)
    {
        if (!TryParseUnrealPuertsInventoryArguments(
                args,
                out UnrealPuertsInventoryOptions? options))
        {
            PrintUsage();
            return 64;
        }

        options = options ?? throw new InvalidOperationException(
            "Argument parsing succeeded without Puerts inventory options.");
        if (!Directory.Exists(options.ProjectRoot))
        {
            Console.Error.WriteLine(
                $"Puerts project root does not exist: {options.ProjectRoot}");
            return 66;
        }
        if (!Directory.Exists(options.NativeProjectRoot))
        {
            Console.Error.WriteLine(
                $"Migration target project root does not exist: {options.NativeProjectRoot}");
            return 66;
        }
        if (options.CoveragePath is not null && !File.Exists(options.CoveragePath))
        {
            Console.Error.WriteLine(
                $"Puerts migration coverage does not exist: {options.CoveragePath}");
            return 66;
        }

        string fullOutputPath = Path.GetFullPath(options.OutputPath);
        string? fullCoveragePath = options.CoveragePath is null
            ? null
            : Path.GetFullPath(options.CoveragePath);
        string? fullReportPath = options.ReportPath is null
            ? null
            : Path.GetFullPath(options.ReportPath);
        if (fullCoveragePath is not null)
        {
            EnsureDifferentPath(
                fullCoveragePath,
                fullOutputPath,
                "Puerts inventory output cannot overwrite its coverage input.");
        }
        if (fullReportPath is not null)
        {
            EnsureDifferentPath(
                fullOutputPath,
                fullReportPath,
                "Puerts inventory and coverage report outputs must be distinct.");
            if (fullCoveragePath is not null)
            {
                EnsureDifferentPath(
                    fullCoveragePath,
                    fullReportPath,
                    "Puerts coverage report cannot overwrite its coverage input.");
            }
        }

        UnrealPuertsMigrationInventory inventory =
            new UnrealPuertsMigrationInventoryBuilder().Build(
                options.ProjectRoot,
                options.NativeProjectRoot,
                options.ProjectName);
        var outputs = new List<OutputFile>
        {
            new(fullOutputPath, UnrealPuertsMigrationJson.Serialize(inventory)),
        };
        UnrealPuertsMigrationCoverageReport? report = null;
        if (fullCoveragePath is not null)
        {
            UnrealPuertsMigrationCoverage coverage =
                UnrealPuertsMigrationJson.DeserializeCoverage(
                    File.ReadAllText(fullCoveragePath));
            report = UnrealPuertsMigrationCoverageValidator.Validate(inventory, coverage);
            foreach (string diagnostic in report.Diagnostics)
            {
                Console.Error.WriteLine(diagnostic);
            }
            if (fullReportPath is not null)
            {
                outputs.Add(new OutputFile(
                    fullReportPath,
                    UnrealPuertsMigrationJson.Serialize(report)));
            }
        }
        WriteTransactionally(outputs);
        foreach (OutputFile output in outputs)
        {
            Console.WriteLine(Path.GetFullPath(output.Path));
        }
        Console.WriteLine(
            $"inventory-fingerprint={inventory.SourceFingerprint}; " +
            $"items={inventory.Summary.ItemCount}");
        if (options.FailOnUncovered && report is { Passed: false })
        {
            return 65;
        }
        return 0;
    }

    private static int RunUnrealPuertsScan(IReadOnlyList<string> args)
    {
        if (!TryParseUnrealPuertsScanArguments(args, out UnrealPuertsScanOptions? options))
        {
            PrintUsage();
            return 64;
        }

        options = options ?? throw new InvalidOperationException(
            "Argument parsing succeeded without Puerts scan options.");
        if (!Directory.Exists(options.ProjectRoot))
        {
            Console.Error.WriteLine(
                $"Puerts project root does not exist: {options.ProjectRoot}");
            return 66;
        }
        if (!Directory.Exists(options.NativeProjectRoot))
        {
            Console.Error.WriteLine(
                $"Puerts native project root does not exist: {options.NativeProjectRoot}");
            return 66;
        }

        UnrealPuertsProjectScan scan = new UnrealPuertsProjectScanner().Scan(
            new UnrealPuertsProjectScanOptions(
                options.ProjectRoot,
                options.NativeProjectRoot,
                options.ProjectName,
                options.ScriptRoots,
                options.NativeRoots));
        string fullOutputPath = Path.GetFullPath(options.OutputPath);
        WriteTransactionally(
        [
            new OutputFile(fullOutputPath, UnrealPuertsProjectScanJson.Serialize(scan)),
        ]);
        Console.WriteLine(fullOutputPath);
        Console.WriteLine(
            $"scan-fingerprint={scan.SourceFingerprint}; " +
            $"scripts={scan.Summary.ScriptFileCount}; " +
            $"findings={scan.Summary.FindingCount}; " +
            $"review={scan.Summary.ReviewCount}; " +
            $"blockers={scan.Summary.BlockerCount}; " +
            $"complete={scan.Complete.ToString().ToLowerInvariant()}");
        foreach (string diagnostic in scan.Diagnostics)
        {
            Console.Error.WriteLine(diagnostic);
        }
        return options.FailOnBlockers && (!scan.Complete || scan.Summary.BlockerCount > 0)
            ? 65
            : 0;
    }

    private static int RunUnrealPuertsPlan(IReadOnlyList<string> args)
    {
        if (!TryParseUnrealPuertsPlanArguments(args, out UnrealPuertsPlanOptions? options))
        {
            PrintUsage();
            return 64;
        }

        options = options ?? throw new InvalidOperationException(
            "Argument parsing succeeded without Puerts migration plan options.");
        if (!File.Exists(options.ScanPath))
        {
            Console.Error.WriteLine($"Puerts project scan does not exist: {options.ScanPath}");
            return 66;
        }
        if (options.ContractPath is not null && !File.Exists(options.ContractPath))
        {
            Console.Error.WriteLine($"Unreal API Contract does not exist: {options.ContractPath}");
            return 66;
        }

        string fullScanPath = Path.GetFullPath(options.ScanPath);
        string? fullContractPath = options.ContractPath is null
            ? null
            : Path.GetFullPath(options.ContractPath);
        UnrealPuertsProjectScan scan = UnrealPuertsProjectScanJson.Deserialize(
            File.ReadAllText(fullScanPath));
        UnrealApiContract? contract = fullContractPath is null
            ? null
            : UnrealApiContractJson.Deserialize(File.ReadAllText(fullContractPath));
        UnrealPuertsMigrationPackage package = new UnrealPuertsMigrationPlanner().Generate(
            scan,
            contract,
            options.ManagedNamespace);
        string fullOutputDirectory = Path.GetFullPath(options.OutputDirectory);
        OutputFile[] outputs = package.Files.Select(file => new OutputFile(
                ResolveOutputPath(fullOutputDirectory, file.RelativePath),
                file.Content))
            .ToArray();
        foreach (OutputFile output in outputs)
        {
            EnsureDifferentPath(
                fullScanPath,
                output.Path,
                "Puerts migration package cannot overwrite its scan input.");
            if (fullContractPath is not null)
            {
                EnsureDifferentPath(
                    fullContractPath,
                    output.Path,
                    "Puerts migration package cannot overwrite its Contract input.");
            }
        }
        WriteTransactionally(outputs);
        foreach (OutputFile output in outputs)
        {
            Console.WriteLine(Path.GetFullPath(output.Path));
        }
        Console.WriteLine(
            $"plan={package.Plan.PlanId}; " +
            $"work-items={package.Plan.Summary.WorkItemCount}; " +
            $"api-covered={package.ApiCoverage.Summary.CoveredCount + package.ApiCoverage.Summary.TypeCoveredCount}; " +
            $"api-missing={package.ApiCoverage.Summary.MissingCount}; " +
            $"contract-requests={package.ContractRequests.Requests.Count}; " +
            $"verification-cases={package.VerificationHarness.Cases.Count}; " +
            $"blocked={package.Plan.Blocked.ToString().ToLowerInvariant()}");
        return options.FailOnBlockers && package.Plan.Blocked ? 65 : 0;
    }

    private static int RunUnrealPuertsVerify(IReadOnlyList<string> args)
    {
        if (!TryParseUnrealPuertsVerifyArguments(args, out UnrealPuertsVerifyOptions? options))
        {
            PrintUsage();
            return 64;
        }

        options = options ?? throw new InvalidOperationException(
            "Argument parsing succeeded without Puerts verification options.");
        foreach ((string description, string path) in new[]
                 {
                     ("Puerts verification harness", options.HarnessPath),
                     ("Puerts runtime evidence", options.PuertsEvidencePath),
                     ("Finare runtime evidence", options.FinareEvidencePath),
                 })
        {
            if (!File.Exists(path))
            {
                Console.Error.WriteLine($"{description} does not exist: {path}");
                return 66;
            }
        }

        string fullHarnessPath = Path.GetFullPath(options.HarnessPath);
        string fullPuertsPath = Path.GetFullPath(options.PuertsEvidencePath);
        string fullUnifiedPath = Path.GetFullPath(options.FinareEvidencePath);
        string fullOutputPath = Path.GetFullPath(options.OutputPath);
        foreach (string inputPath in new[] { fullHarnessPath, fullPuertsPath, fullUnifiedPath })
        {
            EnsureDifferentPath(
                inputPath,
                fullOutputPath,
                "Puerts verification report cannot overwrite an input artifact.");
        }
        UnrealPuertsVerificationHarness harness =
            UnrealPuertsMigrationPackageJson.DeserializeHarness(
                File.ReadAllText(fullHarnessPath));
        UnrealPuertsVerificationEvidence puerts =
            UnrealPuertsMigrationPackageJson.DeserializeEvidence(
                File.ReadAllText(fullPuertsPath));
        UnrealPuertsVerificationEvidence unified =
            UnrealPuertsMigrationPackageJson.DeserializeEvidence(
                File.ReadAllText(fullUnifiedPath));
        UnrealPuertsVerificationReport report = UnrealPuertsVerificationHarnessRunner.Compare(
            harness,
            puerts,
            unified);
        WriteTransactionally(
        [
            new OutputFile(fullOutputPath, UnrealPuertsVerificationReportJson.Serialize(report)),
        ]);
        Console.WriteLine(fullOutputPath);
        foreach (string diagnostic in report.Diagnostics)
        {
            Console.Error.WriteLine(diagnostic);
        }
        foreach (UnrealPuertsVerificationIssue issue in report.Issues)
        {
            Console.Error.WriteLine($"{issue.CaseId} {issue.Kind}: {issue.Message}");
        }
        Console.WriteLine(
            $"verification-passed={report.Passed.ToString().ToLowerInvariant()}; " +
            $"cases={report.Summary.RequiredCaseCount}; " +
            $"matched={report.Summary.PassedCaseCount}; " +
            $"issues={report.Summary.IssueCount}");
        return options.FailOnMismatch && !report.Passed ? 65 : 0;
    }

    private static int RunUnrealPuertsCoexist(IReadOnlyList<string> args)
    {
        if (!TryParseUnrealPuertsCoexistArguments(
                args,
                out UnrealPuertsCoexistOptions? options))
        {
            PrintUsage();
            return 64;
        }

        options = options ?? throw new InvalidOperationException(
            "Argument parsing succeeded without Puerts coexistence options.");
        if (!File.Exists(options.PlanPath))
        {
            Console.Error.WriteLine($"Puerts migration plan does not exist: {options.PlanPath}");
            return 66;
        }
        string fullPlanPath = Path.GetFullPath(options.PlanPath);
        UnrealPuertsMigrationPlan plan = UnrealPuertsMigrationPackageJson.DeserializePlan(
            File.ReadAllText(fullPlanPath));
        UnrealPuertsCoexistencePackage package =
            new UnrealPuertsCoexistencePlanner().Generate(
                plan,
                options.UnifiedManagedTypes);
        string fullOutputDirectory = Path.GetFullPath(options.OutputDirectory);
        OutputFile[] outputs = package.Files.Select(file => new OutputFile(
                ResolveOutputPath(fullOutputDirectory, file.RelativePath),
                file.Content))
            .ToArray();
        foreach (OutputFile output in outputs)
        {
            EnsureDifferentPath(
                fullPlanPath,
                output.Path,
                "Puerts coexistence output cannot overwrite its migration plan input.");
        }
        WriteTransactionally(outputs);
        foreach (OutputFile output in outputs)
        {
            Console.WriteLine(Path.GetFullPath(output.Path));
        }
        int unifiedCount = package.Manifest.Routes.Count(
            static route => route.Owner == UnrealPuertsRuntimeOwner.Finare);
        Console.WriteLine(
            $"manifest={package.Manifest.ManifestId}; " +
            $"routes={package.Manifest.Routes.Count}; " +
            $"puerts={package.Manifest.Routes.Count - unifiedCount}; " +
            $"unifiedScript={unifiedCount}; " +
            $"blocked={plan.Blocked.ToString().ToLowerInvariant()}");
        return options.FailOnBlockers && plan.Blocked ? 65 : 0;
    }

    private static int RunEmit(IReadOnlyList<string> args)
    {
        if (!TryParseCompilationArguments(args, out AnalyzeOptions? options) ||
            string.IsNullOrWhiteSpace(options?.OutputDirectory))
        {
            PrintUsage();
            return 64;
        }

        foreach (string inputPath in options.InputPaths)
        {
            if (!File.Exists(inputPath))
            {
                Console.Error.WriteLine($"Input file does not exist: {inputPath}");
                return 66;
            }
        }

        CSharpCompilationRequest request;
        try
        {
            request = LoadAnalysisRequest(options);
        }
        catch (CSharpProjectLoadException exception)
        {
            Console.Error.WriteLine($"error USCS3001: {exception.Message}");
            return 2;
        }
        CSharpProgramEmitResult result = new CSharpProgramEmitter().Emit(request);
        foreach (UnifiedDiagnostic diagnostic in result.Diagnostics)
        {
            Console.Error.WriteLine(FormatDiagnostic(diagnostic));
        }

        if (!result.Succeeded || result.Image is null || result.Image.Graph is null)
        {
            Console.Error.WriteLine(
                "Emission failed; PE, PDB, source-map, and declaration artifacts were not emitted.");
            return 2;
        }

        string outputDirectory = Path.GetFullPath(options.OutputDirectory);
        Directory.CreateDirectory(outputDirectory);
        string baseName = GetOutputBaseName(options);
        string assemblyPath = Path.Combine(outputDirectory, $"{baseName}.dll");
        string pdbPath = Path.Combine(outputDirectory, $"{baseName}.pdb");
        string sourceMapPath = Path.Combine(outputDirectory, $"{baseName}.sourcemap.json");
        string assetPath = Path.Combine(outputDirectory, $"{baseName}.asset.json");
        string surfacePath = Path.Combine(outputDirectory, $"{baseName}.surface.json");
        string chunkPath = Path.Combine(outputDirectory, $"{baseName}.uschunk");
        string authoringSurfaceJson = AuthoringSurfaceJson.Serialize(
            new AuthoringSurfaceProjector().Project(
                result.Image.ScriptAssets,
                result.Image.Graph,
                result.Image.DeclarationOwners));
        CSharpProgramChunkEmitResult chunk = new CSharpProgramChunkCompiler().Compile(
            result.Image,
            new CSharpProgramChunkOptions(
                RuntimeReferencePaths: request.RuntimeReferencePaths,
                IncludePortablePdb: !string.Equals(
                    options.Configuration,
                    "Release",
                    StringComparison.OrdinalIgnoreCase)));
        var outputs = new List<OutputFile>
        {
            new OutputFile(assemblyPath, result.Image.PortableExecutable),
            new OutputFile(pdbPath, result.Image.PortablePdb),
            new OutputFile(
                sourceMapPath,
                CSharpProgramSourceMapJson.Serialize(result.Image.SourceMap) + "\n"),
            new OutputFile(
                assetPath,
                ScriptAssetJson.Serialize(result.Image.ScriptAssets) + "\n"),
            new OutputFile(
                surfacePath,
                authoringSurfaceJson + "\n"),
            new OutputFile(chunkPath, chunk.Content),
        };
        foreach (IdentitySidecarDocument sidecar in result.Image.IdentitySidecars)
        {
            outputs.Add(new OutputFile(
                Path.Combine(outputDirectory, GetIdentityArtifactName(sidecar.SourcePath)),
                IdentitySidecarJson.Serialize(sidecar) + "\n"));
        }

        WriteTransactionally(outputs);
        foreach (OutputFile output in outputs)
        {
            Console.WriteLine(Path.GetFullPath(output.Path));
        }

        return 0;
    }

    private static int RunChunkPack(IReadOnlyList<string> args)
    {
        if (!TryParseChunkPackArguments(args, out ChunkPackOptions? options))
        {
            PrintUsage();
            return 64;
        }

        options = options ?? throw new InvalidOperationException(
            "Argument parsing succeeded without chunk-pack options.");
        var sections = new List<FinareChunkSection>
        {
            ReadChunkSection(
                FinareChunkSectionKind.RuntimeManifest,
                FinareChunkSectionFlags.RuntimeRequired,
                options.RuntimeManifestPath),
            ReadChunkSection(
                FinareChunkSectionKind.ManagedAssembly,
                FinareChunkSectionFlags.RuntimeRequired,
                options.AssemblyPath),
            ReadChunkSection(
                FinareChunkSectionKind.DeclarationData,
                FinareChunkSectionFlags.RuntimeRequired,
                options.DeclarationPath),
            ReadChunkSection(
                FinareChunkSectionKind.BindingManifest,
                FinareChunkSectionFlags.RuntimeRequired,
                options.BindingManifestPath),
            ReadChunkSection(
                FinareChunkSectionKind.DependencyManifest,
                FinareChunkSectionFlags.RuntimeRequired,
                options.DependencyManifestPath),
        };
        AddOptionalChunkSection(
            sections,
            FinareChunkSectionKind.PortablePdb,
            FinareChunkSectionFlags.Debug,
            options.PortablePdbPath);
        AddOptionalChunkSection(
            sections,
            FinareChunkSectionKind.SourceMap,
            FinareChunkSectionFlags.Debug,
            options.SourceMapPath);
        AddOptionalChunkSection(
            sections,
            FinareChunkSectionKind.AuthoringSurface,
            FinareChunkSectionFlags.Editor,
            options.AuthoringSurfacePath);

        byte[] content = FinareChunkCodec.Write(sections);
        FinareChunk chunk = FinareProgramChunk.Read(content);
        string outputPath = Path.GetFullPath(options.OutputPath);
        WriteTransactionally([new OutputFile(outputPath, content)]);
        Console.WriteLine(outputPath);
        Console.WriteLine(chunk.ContentDigest);
        return 0;
    }

    private static int RunChunkInspect(IReadOnlyList<string> args)
    {
        if (args.Count != 2 || args[0] != "chunk-inspect" ||
            args[1].StartsWith("--", StringComparison.Ordinal))
        {
            PrintUsage();
            return 64;
        }

        string inputPath = Path.GetFullPath(args[1]);
        if (!File.Exists(inputPath))
        {
            Console.Error.WriteLine($"Chunk input does not exist: {inputPath}");
            return 66;
        }

        using FileStream stream = File.OpenRead(inputPath);
        FinareChunk chunk = FinareProgramChunk.Read(stream);
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            schemaVersion = "unifiedscript.chunk-inspection/v0.1",
            path = inputPath,
            formatVersion = chunk.FormatVersion,
            byteLength = chunk.ByteLength,
            contentDigest = chunk.ContentDigest,
            sections = chunk.Sections.Select(static section => new
            {
                kind = section.Kind.ToString(),
                kindId = $"0x{(uint)section.Kind:x8}",
                flags = section.Flags.ToString(),
                byteLength = section.Data.Length,
            }),
        }, UnifiedJson.CreateOptions()));
        return 0;
    }

    private static int RunChunkProfile(IReadOnlyList<string> args)
    {
        if (!TryParseChunkProfileArguments(args, out ChunkProfileOptions? options))
        {
            PrintUsage();
            return 64;
        }

        options = options ?? throw new InvalidOperationException(
            "Argument parsing succeeded without chunk profile options.");
        string inputPath = Path.GetFullPath(options.InputPath);
        if (!File.Exists(inputPath))
        {
            Console.Error.WriteLine($"Chunk input does not exist: {inputPath}");
            return 66;
        }
        string outputPath = Path.GetFullPath(options.OutputPath);
        EnsureDifferentPath(
            inputPath,
            outputPath,
            "Chunk profiling cannot overwrite its compiled input.");
        FinareCompiledChunkLoadProfileReport report =
            new FinareCompiledChunkLoadProfiler().Profile(
                inputPath,
                options.ReferencePaths,
                options.TrialCount,
                options.PrepareEntryMethods);
        WriteTransactionally([
            new OutputFile(
                outputPath,
                FinareCompiledChunkLoadProfileJson.Serialize(report)),
        ]);
        long[] totalLoads = report.Trials
            .Select(static trial => trial.TotalLoadNanoseconds)
            .Order()
            .ToArray();
        int middle = totalLoads.Length / 2;
        long medianLoad = totalLoads.Length % 2 == 1
            ? totalLoads[middle]
            : checked((totalLoads[middle - 1] + totalLoads[middle]) / 2);
        Console.WriteLine(outputPath);
        Console.WriteLine(report.ContentDigest);
        Console.WriteLine(
            $"trials={report.TrialCount}; " +
            $"medianLoadNs={medianLoad}; " +
            $"firstLoadNs={report.Trials[0].TotalLoadNanoseconds}; " +
            $"jitPrepare={report.EntryMethodJitPreparation}");
        return 0;
    }

    private static int RunChunkFirstTickProfile(IReadOnlyList<string> args)
    {
        if (!TryParseChunkFirstTickProfileArguments(
                args,
                out ChunkFirstTickProfileOptions? options))
        {
            PrintUsage();
            return 64;
        }

        options = options ?? throw new InvalidOperationException(
            "Argument parsing succeeded without first-Tick profile options.");
        string inputPath = Path.GetFullPath(options.InputPath);
        if (!File.Exists(inputPath))
        {
            Console.Error.WriteLine($"Chunk input does not exist: {inputPath}");
            return 66;
        }
        string outputPath = Path.GetFullPath(options.OutputPath);
        EnsureDifferentPath(
            inputPath,
            outputPath,
            "First-Tick profiling cannot overwrite its compiled input.");
        string[] references = options.ReferencePaths
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        using var stream = new FileStream(
            inputPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 64 * 1024,
            FileOptions.SequentialScan);
        CoreClrCompiledChunkSession session =
            new CoreClrCompiledChunkHost().Load(stream, references);
        CoreClrGenerationPrewarmOperation? prewarm = null;
        try
        {
            object instance = session.CreateEntryInstance(options.TypeName);
            if (instance is not IUnrealManagedScript script)
            {
                throw new InvalidDataException(
                    $"First-Tick profile type '{options.TypeName}' does not implement " +
                    $"{nameof(IUnrealManagedScript)}.");
            }
            prewarm = CoreClrGenerationPrewarmOperation.Create(
                instance.GetType(),
                options.DisablePrewarm
                    ? CoreClrGenerationPrewarmOptions.Disabled
                    : CoreClrGenerationPrewarmOptions.Default);

            long scheduleStarted = Stopwatch.GetTimestamp();
            bool scheduled = prewarm.Start();
            long scheduleNanoseconds = ElapsedNanoseconds(scheduleStarted);
            if (options.FrameGapMilliseconds > 0)
            {
                Thread.Sleep(options.FrameGapMilliseconds);
            }

            CoreClrGenerationPrewarmSnapshot atFirstTick = prewarm.ObserveFirstTick();
            long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            long firstTickStarted = Stopwatch.GetTimestamp();
            script.OnTick(1.0f / 60.0f);
            long firstTickNanoseconds = ElapsedNanoseconds(firstTickStarted);
            long firstTickAllocatedBytes = Math.Max(
                0,
                GC.GetAllocatedBytesForCurrentThread() - allocatedBefore);

            long teardownStarted = Stopwatch.GetTimestamp();
            prewarm.Cancel();
            session.Dispose();
            long teardownNanoseconds = ElapsedNanoseconds(teardownStarted);
            if (!prewarm.Completion.Wait(TimeSpan.FromSeconds(5)))
            {
                throw new InvalidOperationException(
                    "Background generation prewarm did not stop within five seconds.");
            }
            CoreClrGenerationPrewarmSnapshot finalPrewarm = prewarm.GetSnapshot();

            string json = JsonSerializer.Serialize(new
            {
                schemaVersion = "unifiedscript.generation-prewarm-trial/v0.1",
                chunkFormatVersion = session.Chunk.FormatVersion,
                contentDigest = session.Chunk.ContentDigest,
                runtimeVersion = Environment.Version.ToString(),
                typeName = options.TypeName,
                variant = options.DisablePrewarm ? "disabled" : "background",
                frameGapMilliseconds = options.FrameGapMilliseconds,
                scheduled,
                scheduleNanoseconds,
                firstTickNanoseconds,
                firstTickAllocatedBytes,
                teardownNanoseconds,
                prewarmAtFirstTick = atFirstTick,
                finalPrewarm,
            }, UnifiedJson.CreateOptions(writeIndented: true));
            WriteTransactionally([new OutputFile(outputPath, json)]);
            Console.WriteLine(outputPath);
            Console.WriteLine(
                $"variant={(options.DisablePrewarm ? "disabled" : "background")}; " +
                $"firstTickNs={firstTickNanoseconds}; scheduleNs={scheduleNanoseconds}; " +
                $"teardownNs={teardownNanoseconds}; " +
                $"preparedBeforeFirstTick={atFirstTick.PreparedBeforeFirstTickCount}");
            return 0;
        }
        finally
        {
            prewarm?.Cancel();
            session.Dispose();
        }
    }

    private static FinareChunkSection ReadChunkSection(
        FinareChunkSectionKind kind,
        FinareChunkSectionFlags flags,
        string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Chunk section '{kind}' does not exist.", path);
        }

        return new FinareChunkSection(kind, flags, File.ReadAllBytes(path));
    }

    private static long ElapsedNanoseconds(long startedTimestamp) => checked(
        (long)Math.Ceiling(
            Math.Max(0, Stopwatch.GetTimestamp() - startedTimestamp) *
            1_000_000_000d /
            Stopwatch.Frequency));

    private static void AddOptionalChunkSection(
        ICollection<FinareChunkSection> sections,
        FinareChunkSectionKind kind,
        FinareChunkSectionFlags flags,
        string? path)
    {
        if (path is not null)
        {
            sections.Add(ReadChunkSection(kind, flags, path));
        }
    }

    private static int RunExecute(IReadOnlyList<string> args)
    {
        if (!TryParseExecuteArguments(args, out ExecuteOptions? options))
        {
            PrintUsage();
            return 64;
        }

        options = options ?? throw new InvalidOperationException(
            "Argument parsing succeeded without execution options.");
        if (!File.Exists(options.InputPath))
        {
            Console.Error.WriteLine($"Input file does not exist: {options.InputPath}");
            return 66;
        }

        MethodEffectCatalog effectCatalog = LoadEffectCatalog(options.EffectCatalogPath);
        CSharpSourceDocument[] sources = LoadSources(
            [options.InputPath],
            options.IdentityPath is null ? [] : [options.IdentityPath]);
        var request = new CSharpCompilationRequest(
            options.AssemblyName,
            sources,
            options.ReferencePaths,
            effectCatalog);
        try
        {
            using CoreClrExecutionSession session = new CoreClrExecutionHost().CompileAndLoad(request);
            SemanticId methodId = options.MethodId ??
                session.ResolveExportedMethodId(options.QualifiedMethodName!);
            ExecutionArgument[] arguments = ParseExecutionArguments(options.ArgumentsJson);
            ScriptAssetReceiver? receiver = options.Receiver is null
                ? null
                : ResolveExecutionReceiver(session, options.Receiver);
            ExecutionResult result = session.Execute(new ExecutionInvocation(methodId, arguments, receiver));
            Console.WriteLine(ExecutionJson.Serialize(result));
            return result.Outcome == ExecutionOutcome.Returned ? 0 : 3;
        }
        catch (ExecutionCompilationException exception)
        {
            foreach (UnifiedDiagnostic diagnostic in exception.Diagnostics)
            {
                Console.Error.WriteLine(FormatDiagnostic(diagnostic));
            }

            Console.Error.WriteLine(exception.Message);
            return 2;
        }
        catch (ExecutionInvocationException exception)
        {
            Console.Error.WriteLine($"error {exception.Code}: {exception.Message}");
            return 3;
        }
    }

    private static ScriptAssetReceiver ResolveExecutionReceiver(
        CoreClrExecutionSession session,
        string selector)
    {
        ScriptAssetDeclaration[] matches = session.ScriptAssets.Assets
            .Where(asset => string.Equals(asset.Id.Value, selector, StringComparison.Ordinal) ||
                string.Equals(asset.QualifiedName, selector, StringComparison.Ordinal))
            .ToArray();
        return matches.Length switch
        {
            1 => new ScriptAssetReceiver(matches[0].Id),
            0 => throw new ExecutionInvocationException(
                "UVR1008",
                $"Script Asset receiver '{selector}' does not exist in the loaded program."),
            _ => throw new ExecutionInvocationException(
                "UVR1008",
                $"Script Asset receiver selector '{selector}' is ambiguous; use its semantic ID."),
        };
    }

    private static int RunDebugServe(IReadOnlyList<string> args)
    {
        if (!TryParseDebugServeArguments(args, out DebugServeOptions? options))
        {
            PrintUsage();
            return 64;
        }

        options = options ?? throw new InvalidOperationException(
            "Argument parsing succeeded without debug server options.");
        if (!File.Exists(options.InputPath))
        {
            Console.Error.WriteLine($"Input file does not exist: {options.InputPath}");
            return 66;
        }

        if (!Directory.Exists(options.ContentRoot))
        {
            Console.Error.WriteLine($"Visual workbench root does not exist: {options.ContentRoot}");
            return 66;
        }
        if (options.ApiBrowserPath is not null && !File.Exists(options.ApiBrowserPath))
        {
            Console.Error.WriteLine($"Unreal API browser does not exist: {options.ApiBrowserPath}");
            return 66;
        }

        string fullInputPath = Path.GetFullPath(options.InputPath);
        string identityPath = options.IdentityPath ?? options.InputPath + ".usids";
        string historyPath = options.HistoryPath ?? options.InputPath + ".ushistory.json";
        CSharpCompilationRequest request;
        Dictionary<string, string> sourceFiles;
        IReadOnlyDictionary<string, string> identityFiles;
        IReadOnlySet<string>? readOnlySources = null;
        if (options.ProjectPath is null)
        {
            string logicalPath = GetLogicalInputPath(fullInputPath);
            IdentitySidecarDocument identitySidecar = File.Exists(identityPath)
                ? LoadIdentitySidecar(identityPath)
                : IdentitySidecarDocument.CreateEmpty(logicalPath);
            string[] additionalSourcePaths = options.AdditionalSourcePaths
                .Select(Path.GetFullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            string? missingSource = additionalSourcePaths.FirstOrDefault(path => !File.Exists(path));
            if (missingSource is not null)
            {
                Console.Error.WriteLine($"Additional source file does not exist: {missingSource}");
                return 66;
            }
            CSharpSourceDocument[] sources =
            [
                new CSharpSourceDocument(logicalPath, File.ReadAllText(fullInputPath), identitySidecar),
                .. additionalSourcePaths
                    .Where(path => !string.Equals(path, fullInputPath, StringComparison.OrdinalIgnoreCase))
                    .Select(path => new CSharpSourceDocument(
                        GetLogicalInputPath(path), File.ReadAllText(path),
                        IdentitySidecarDocument.CreateEmpty(GetLogicalInputPath(path)))),
            ];
            sourceFiles = new Dictionary<string, string>(StringComparer.Ordinal) { [logicalPath] = fullInputPath };
            foreach (string path in additionalSourcePaths.Where(path =>
                !string.Equals(path, fullInputPath, StringComparison.OrdinalIgnoreCase)))
                sourceFiles.Add(GetLogicalInputPath(path), path);
            identityFiles = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [logicalPath] = Path.GetFullPath(identityPath),
            };
            request = new CSharpCompilationRequest(options.AssemblyName, sources,
                options.ReferencePaths, LoadEffectCatalog(options.EffectCatalogPath));
        }
        else
        {
            try
            {
                ProjectAuthoringInput project = ProjectAuthoringInput.Load(
                    options.ProjectPath, fullInputPath, options.IdentityPath,
                    new CSharpProjectLoadOptions(options.Configuration, options.TargetFramework,
                        options.Restore, options.Build), LoadEffectCatalog(options.EffectCatalogPath));
                request = project.Request;
                sourceFiles = new Dictionary<string, string>(project.SourceFiles, StringComparer.Ordinal);
                identityPath = project.IdentityPath;
                readOnlySources = project.ReadOnlySources;
                identityFiles = project.IdentityFiles;
            }
            catch (CSharpProjectLoadException exception)
            {
                Console.Error.WriteLine($"error USCS3001: {exception.Message}");
                return 2;
            }
        }
        UnrealApiBrowserDocument? apiBrowser = options.ApiBrowserPath is null
            ? null
            : UnrealApiBrowserJson.Deserialize(File.ReadAllText(options.ApiBrowserPath));
        try
        {
            using var server = new CoreClrDebugHttpServer(
                request,
                fullInputPath,
                identityPath,
                options.ContentRoot,
                options.Port,
                new AuthoringArtifactOutputs(
                    options.DeclarationOutputPath,
                    options.SurfaceOutputPath,
                    options.ManagedAssemblyOutputPath,
                    options.PortablePdbOutputPath,
                    historyPath,
                    options.GraphLayoutPath,
                    options.CompiledChunkOutputPath),
                sourceFiles,
                apiBrowser,
                readOnlySources,
                options.PublishInitial,
                options.ProjectPath is null ? null : () => ProjectAuthoringInput.Load(
                    options.ProjectPath, fullInputPath, options.IdentityPath,
                    new CSharpProjectLoadOptions(options.Configuration, options.TargetFramework, Restore: false, Build: false),
                    LoadEffectCatalog(options.EffectCatalogPath)),
                identityFiles);
            using var cancellation = new CancellationTokenSource();
            ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                cancellation.Cancel();
            };
            Console.CancelKeyPress += cancelHandler;
            try
            {
                Console.WriteLine(
                    $"Finare visual debug server ready at http://127.0.0.1:{options.Port}/");
                server.RunAsync(cancellation.Token).GetAwaiter().GetResult();
            }
            finally
            {
                Console.CancelKeyPress -= cancelHandler;
            }

            return 0;
        }
        catch (ExecutionCompilationException exception)
        {
            foreach (UnifiedDiagnostic diagnostic in exception.Diagnostics)
            {
                Console.Error.WriteLine(FormatDiagnostic(diagnostic));
            }

            Console.Error.WriteLine(exception.Message);
            return 2;
        }
    }

    private static int RunDebugFrameServe(IReadOnlyList<string> args)
    {
        if (!TryParseDebugFrameServeArguments(args, out DebugFrameServeOptions? options))
        {
            PrintUsage();
            return 64;
        }

        options = options ?? throw new InvalidOperationException(
            "Argument parsing succeeded without remote debug frame server options.");
        if (!File.Exists(options.InputPath))
        {
            Console.Error.WriteLine($"Input file does not exist: {options.InputPath}");
            return 66;
        }

        string fullInputPath = Path.GetFullPath(options.InputPath);
        string logicalPath = GetLogicalInputPath(fullInputPath);
        string identityPath = options.IdentityPath ?? options.InputPath + ".usids";
        IdentitySidecarDocument identitySidecar = File.Exists(identityPath)
            ? LoadIdentitySidecar(identityPath)
            : IdentitySidecarDocument.CreateEmpty(logicalPath);
        string[] additionalSourcePaths = options.AdditionalSourcePaths
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        string? missingSource = additionalSourcePaths.FirstOrDefault(path => !File.Exists(path));
        if (missingSource is not null)
        {
            Console.Error.WriteLine($"Additional source file does not exist: {missingSource}");
            return 66;
        }

        CSharpSourceDocument[] sources =
        [
            new CSharpSourceDocument(
                logicalPath,
                File.ReadAllText(fullInputPath),
                identitySidecar),
            .. additionalSourcePaths
                .Where(path => !string.Equals(path, fullInputPath, StringComparison.OrdinalIgnoreCase))
                .Select(path => new CSharpSourceDocument(
                    GetLogicalInputPath(path),
                    File.ReadAllText(path),
                    IdentitySidecarDocument.CreateEmpty(GetLogicalInputPath(path)))),
        ];
        var request = new CSharpCompilationRequest(
            options.AssemblyName,
            sources,
            options.ReferencePaths,
            LoadEffectCatalog(options.EffectCatalogPath));
        try
        {
            using var transport = new CoreClrDebugLoopbackTransport();
            CoreClrDebugReloadResult reload = transport.Reload(request);
            var server = new CoreClrDebugTcpServer(
                transport,
                options.Port,
                options.MaximumConnections);
            try
            {
                server.Start();
                using var cancellation = new CancellationTokenSource();
                ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
                {
                    eventArgs.Cancel = true;
                    cancellation.Cancel();
                };
                Console.CancelKeyPress += cancelHandler;
                try
                {
                    var ready = new DebugFrameServerReady(
                        DebugFrameServerReady.CurrentSchemaVersion,
                        $"tcp://127.0.0.1:{server.LocalEndpoint.Port}",
                        reload.Generation,
                        CoreClrDebugFrameCodec.ProtocolVersion,
                        CoreClrDebugFrameCodec.HeaderSize,
                        CoreClrDebugFrameCodec.MaximumPayloadBytes,
                        server.MaximumConnections);
                    Console.WriteLine(JsonSerializer.Serialize(
                        ready,
                        UnifiedJson.CreateOptions(writeIndented: false)));
                    server.RunAsync(cancellation.Token).GetAwaiter().GetResult();
                }
                finally
                {
                    Console.CancelKeyPress -= cancelHandler;
                }
            }
            finally
            {
                server.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }

            return 0;
        }
        catch (ExecutionCompilationException exception)
        {
            foreach (UnifiedDiagnostic diagnostic in exception.Diagnostics)
            {
                Console.Error.WriteLine(FormatDiagnostic(diagnostic));
            }

            Console.Error.WriteLine(exception.Message);
            return 2;
        }
    }

    private static int RunUnrealProfileDiagnosis(IReadOnlyList<string> args)
    {
        const long MaximumDiagnosisInputBytes = 16L * 1024 * 1024;
        if (!TryParseUnrealProfileDiagnosisArguments(
                args,
                out UnrealProfileDiagnosisOptions? options))
        {
            PrintUsage();
            return 64;
        }
        options = options ?? throw new InvalidOperationException(
            "Argument parsing succeeded without gameplay diagnosis options.");
        foreach (string input in new[]
        {
            options.BaselinePath,
            options.CurrentPath,
            options.EvidencePath,
        })
        {
            if (!File.Exists(input))
            {
                Console.Error.WriteLine($"Gameplay diagnosis input does not exist: {input}");
                return 66;
            }
            if (new FileInfo(input).Length > MaximumDiagnosisInputBytes)
            {
                Console.Error.WriteLine(
                    $"Gameplay diagnosis input exceeds 16 MiB: {input}");
                return 65;
            }
        }
        try
        {
            UnrealGameplayProfileTrace baseline =
                UnrealGameplayDiagnosisJson.DeserializeTrace(
                    File.ReadAllText(options.BaselinePath));
            UnrealGameplayProfileTrace current =
                UnrealGameplayDiagnosisJson.DeserializeTrace(
                    File.ReadAllText(options.CurrentPath));
            UnrealGameplayDiagnosisEvidence evidence =
                UnrealGameplayDiagnosisJson.DeserializeEvidence(
                    File.ReadAllText(options.EvidencePath));
            UnrealGameplayDiagnosisReport report = UnrealGameplayDiagnosis.Analyze(
                baseline,
                current,
                evidence.DebugObservation,
                evidence.NodeLocations,
                evidence.Options);
            string outputPath = Path.GetFullPath(options.OutputPath);
            WriteAtomically(
                outputPath,
                UnrealGameplayDiagnosisJson.Serialize(report) + "\n");
            Console.WriteLine(outputPath);
            Console.WriteLine(
                $"Gameplay diagnosis: " +
                $"{(report.AcceptanceSatisfied ? "LOCATED" : "INCOMPLETE")} " +
                $"(defect={report.LocatedGameplayDefect}, " +
                $"regression={report.LocatedPerformanceRegression}, " +
                $"findings={report.Findings.Count})");
            return report.AcceptanceSatisfied ? 0 : 3;
        }
        catch (InvalidDataException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 65;
        }
    }

    private static int RunUnrealWorkloadComparison(IReadOnlyList<string> args)
    {
        const long MaximumPerformanceInputBytes = 32L * 1024 * 1024;
        if (!TryParseUnrealWorkloadComparisonArguments(
                args,
                out UnrealWorkloadComparisonOptions? options))
        {
            PrintUsage();
            return 64;
        }
        options = options ?? throw new InvalidOperationException(
            "Argument parsing succeeded without workload comparison options.");
        foreach (string input in new[]
        {
            options.ManifestPath,
            options.FinareResultPath,
            options.PuertsResultPath,
        })
        {
            if (!File.Exists(input))
            {
                Console.Error.WriteLine($"Performance comparison input does not exist: {input}");
                return 66;
            }
            if (new FileInfo(input).Length > MaximumPerformanceInputBytes)
            {
                Console.Error.WriteLine(
                    $"Performance comparison input exceeds 32 MiB: {input}");
                return 65;
            }
        }
        try
        {
            UnrealPerformanceSuiteManifest manifest =
                UnrealPerformanceSuiteJson.DeserializeManifest(
                    File.ReadAllText(options.ManifestPath));
            UnrealPerformanceEngineResult unifiedScript =
                UnrealPerformanceSuiteJson.DeserializeResult(
                    File.ReadAllText(options.FinareResultPath));
            UnrealPerformanceEngineResult puerts =
                UnrealPerformanceSuiteJson.DeserializeResult(
                    File.ReadAllText(options.PuertsResultPath));
            UnrealPerformanceComparisonReport report =
                UnrealPerformanceWorkloadSuite.Compare(
                    manifest,
                    unifiedScript,
                    puerts);
            string outputPath = Path.GetFullPath(options.OutputPath);
            WriteAtomically(
                outputPath,
                UnrealPerformanceSuiteJson.SerializeReport(report) + "\n");
            Console.WriteLine(outputPath);
            Console.WriteLine(
                $"Performance comparison: MATCHED " +
                $"(workloads={report.Workloads.Count}, " +
                $"trials={manifest.TrialCount}, schedule={report.Schedule.Count})");
            return 0;
        }
        catch (InvalidDataException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 65;
        }
    }

    private static int RunUnrealWorkloadBudget(IReadOnlyList<string> args)
    {
        const long MaximumPerformanceInputBytes = 32L * 1024 * 1024;
        if (!TryParseUnrealWorkloadBudgetArguments(
                args,
                out UnrealWorkloadBudgetOptions? options))
        {
            PrintUsage();
            return 64;
        }
        options = options ?? throw new InvalidOperationException(
            "Argument parsing succeeded without workload budget options.");
        foreach (string input in new[]
        {
            options.ManifestPath,
            options.ComparisonPath,
            options.BudgetPath,
        })
        {
            if (!File.Exists(input))
            {
                Console.Error.WriteLine($"Performance budget input does not exist: {input}");
                return 66;
            }
            if (new FileInfo(input).Length > MaximumPerformanceInputBytes)
            {
                Console.Error.WriteLine(
                    $"Performance budget input exceeds 32 MiB: {input}");
                return 65;
            }
        }
        try
        {
            UnrealPerformanceSuiteManifest manifest =
                UnrealPerformanceSuiteJson.DeserializeManifest(
                    File.ReadAllText(options.ManifestPath));
            UnrealPerformanceComparisonReport comparison =
                UnrealPerformanceSuiteJson.DeserializeReport(
                    File.ReadAllText(options.ComparisonPath));
            UnrealPerformanceBudgetManifest budgets =
                UnrealPerformanceBudgetJson.DeserializeManifest(
                    File.ReadAllText(options.BudgetPath));
            UnrealPerformanceBudgetReport report = UnrealPerformanceBudgets.Evaluate(
                manifest,
                comparison,
                budgets);
            string outputPath = Path.GetFullPath(options.OutputPath);
            WriteAtomically(
                outputPath,
                UnrealPerformanceBudgetJson.SerializeReport(report) + "\n");
            Console.WriteLine(outputPath);
            Console.WriteLine(
                $"Performance budget: {report.Status.ToUpperInvariant()} " +
                $"(weightedRatio={report.WeightedRatio:F6}, " +
                $"maximum={report.MaximumWeightedRatio:F6}, " +
                $"regressions={(report.RegressionGatesPassed ? "passed" : "failed")})");
            return 0;
        }
        catch (InvalidDataException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 65;
        }
    }

    private static int RunUnrealUsabilityBudget(IReadOnlyList<string> args)
    {
        const long MaximumUsabilityInputBytes = 64L * 1024 * 1024;
        if (!TryParseUnrealUsabilityBudgetArguments(
                args,
                out UnrealUsabilityBudgetOptions? options))
        {
            PrintUsage();
            return 64;
        }
        options = options ?? throw new InvalidOperationException(
            "Argument parsing succeeded without usability budget options.");
        foreach (string input in new[]
        {
            options.PackagedFirstPlayPath,
            options.DailyReloadPath,
            options.AutomationEvidencePath,
            options.BudgetPath,
        })
        {
            if (!File.Exists(input))
            {
                Console.Error.WriteLine($"Usability budget input does not exist: {input}");
                return 66;
            }
            if (new FileInfo(input).Length > MaximumUsabilityInputBytes)
            {
                Console.Error.WriteLine(
                    $"Usability budget input exceeds 64 MiB: {input}");
                return 65;
            }
        }

        UnrealUsabilityBudgetManifest budgets =
            UnrealUsabilityRegressionJson.DeserializeManifest(
                File.ReadAllText(options.BudgetPath));
        UnrealUsabilityRegressionReport report = UnrealUsabilityRegression.Evaluate(
            budgets,
            options.PackagedFirstPlayPath,
            options.DailyReloadPath,
            options.AutomationEvidencePath);
        string outputPath = Path.GetFullPath(options.OutputPath);
        WriteAtomically(
            outputPath,
            UnrealUsabilityRegressionJson.SerializeReport(report) + "\n");
        Console.WriteLine(outputPath);
        Console.WriteLine(
            $"Unreal usability budget: {report.Status.ToUpperInvariant()} " +
            $"(scenarios={report.Scenarios.Count}, violations={report.Violations.Count})");
        return options.FailOnRegression && report.Status != "passed" ? 65 : 0;
    }

    private static int RunUnrealWorkloadAssembly(IReadOnlyList<string> args)
    {
        const long MaximumCaptureBytes = 4L * 1024 * 1024;
        if (!TryParseUnrealWorkloadAssemblyArguments(
                args,
                out UnrealWorkloadAssemblyOptions? options))
        {
            PrintUsage();
            return 64;
        }
        options = options ?? throw new InvalidOperationException(
            "Argument parsing succeeded without workload assembly options.");
        if (!File.Exists(options.ManifestPath))
        {
            Console.Error.WriteLine(
                $"Performance manifest does not exist: {options.ManifestPath}");
            return 66;
        }
        if (!Directory.Exists(options.CaptureDirectory))
        {
            Console.Error.WriteLine(
                $"Performance capture directory does not exist: {options.CaptureDirectory}");
            return 66;
        }
        string[] capturePaths = Directory.GetFiles(
            options.CaptureDirectory,
            "*.json",
            SearchOption.TopDirectoryOnly);
        if (capturePaths.Length == 0 || capturePaths.Length > 4096)
        {
            Console.Error.WriteLine(
                "Performance capture directory must contain 1-4096 JSON files.");
            return 65;
        }
        try
        {
            UnrealPerformanceSuiteManifest manifest =
                UnrealPerformanceSuiteJson.DeserializeManifest(
                    File.ReadAllText(options.ManifestPath));
            var captures = new List<UnrealPerformanceTrialCapture>(capturePaths.Length);
            foreach (string path in capturePaths.Order(StringComparer.Ordinal))
            {
                if (new FileInfo(path).Length > MaximumCaptureBytes)
                {
                    throw new InvalidDataException(
                        $"Performance trial capture exceeds 4 MiB: {path}");
                }
                captures.Add(UnrealPerformanceSuiteJson.DeserializeCapture(
                    File.ReadAllText(path)));
            }
            UnrealPerformanceEngineResult result =
                UnrealPerformanceWorkloadSuite.AssembleResult(
                    manifest,
                    options.EngineId,
                    captures);
            string outputPath = Path.GetFullPath(options.OutputPath);
            WriteAtomically(
                outputPath,
                UnrealPerformanceSuiteJson.SerializeResult(result) + "\n");
            Console.WriteLine(outputPath);
            Console.WriteLine(
                $"Performance result: ASSEMBLED " +
                $"(engine={result.EngineId}, trials={result.Trials.Count})");
            return 0;
        }
        catch (InvalidDataException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 65;
        }
    }

    private static int RunUnrealWorkloadCapture(IReadOnlyList<string> args)
    {
        const long MaximumMeasurementBytes = 4L * 1024 * 1024;
        if (!TryParseUnrealWorkloadCaptureArguments(
                args,
                out UnrealWorkloadCaptureOptions? options))
        {
            PrintUsage();
            return 64;
        }
        options = options ?? throw new InvalidOperationException(
            "Argument parsing succeeded without workload capture options.");
        foreach (string input in new[] { options.ManifestPath, options.MeasurementPath })
        {
            if (!File.Exists(input))
            {
                Console.Error.WriteLine(
                    $"Performance capture input does not exist: {input}");
                return 66;
            }
            if (new FileInfo(input).Length > MaximumMeasurementBytes)
            {
                Console.Error.WriteLine(
                    $"Performance capture input exceeds 4 MiB: {input}");
                return 65;
            }
        }
        try
        {
            UnrealPerformanceSuiteManifest manifest =
                UnrealPerformanceSuiteJson.DeserializeManifest(
                    File.ReadAllText(options.ManifestPath));
            UnrealPerformanceRawMeasurement measurement =
                UnrealPerformanceSuiteJson.DeserializeRawMeasurement(
                    File.ReadAllText(options.MeasurementPath));
            UnrealPerformanceTrialCapture capture =
                UnrealPerformanceWorkloadSuite.CreateCapture(manifest, measurement);
            string outputPath = Path.GetFullPath(options.OutputPath);
            WriteAtomically(
                outputPath,
                UnrealPerformanceSuiteJson.SerializeCapture(capture) + "\n");
            Console.WriteLine(outputPath);
            Console.WriteLine(
                $"Performance trial: CAPTURED " +
                $"(engine={capture.EngineId}, " +
                $"workload={capture.Trial.WorkloadId}, " +
                $"trial={capture.Trial.Trial}, " +
                $"ordinal={capture.Trial.ExecutionOrdinal})");
            return 0;
        }
        catch (InvalidDataException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 65;
        }
    }

    private static int RunLayout(IReadOnlyList<string> args, bool synchronize)
    {
        if (!TryParseLayoutArguments(args, synchronize, out LayoutOptions? options))
        {
            PrintUsage();
            return 64;
        }

        options = options ?? throw new InvalidOperationException("Argument parsing succeeded without layout options.");
        if (!File.Exists(options.GraphPath))
        {
            Console.Error.WriteLine($"Graph document does not exist: {options.GraphPath}");
            return 66;
        }

        if (options.LayoutPath is not null && !File.Exists(options.LayoutPath))
        {
            Console.Error.WriteLine($"Graph layout sidecar does not exist: {options.LayoutPath}");
            return 66;
        }

        string fullGraphPath = Path.GetFullPath(options.GraphPath);
        string fullOutputPath = Path.GetFullPath(options.OutputPath);
        EnsureDifferentPath(
            fullGraphPath,
            fullOutputPath,
            "Graph layout output cannot overwrite the semantic graph document.");
        GraphDocument graph = GraphJson.Deserialize(File.ReadAllText(fullGraphPath));
        var layoutSynchronizer = new GraphLayoutSynchronizer();
        GraphLayoutDocument result;
        if (options.LayoutPath is null)
        {
            result = layoutSynchronizer.Create(graph);
        }
        else
        {
            string fullLayoutPath = Path.GetFullPath(options.LayoutPath);
            EnsureDifferentPath(
                fullLayoutPath,
                fullOutputPath,
                "In-place graph layout synchronization is not supported; choose a different --out path.");
            result = layoutSynchronizer.Synchronize(
                graph,
                GraphLayoutJson.Deserialize(File.ReadAllText(fullLayoutPath)));
        }

        WriteAtomically(fullOutputPath, GraphLayoutJson.Serialize(result) + "\n");
        Console.WriteLine(fullOutputPath);
        return 0;
    }

    private static int RunAnalyze(IReadOnlyList<string> args)
    {
        if (!TryParseCompilationArguments(args, out AnalyzeOptions? options))
        {
            PrintUsage();
            return 64;
        }

        options = options ?? throw new InvalidOperationException("Argument parsing succeeded without options.");
        foreach (string inputPath in options.InputPaths)
        {
            if (!File.Exists(inputPath))
            {
                Console.Error.WriteLine($"Input file does not exist: {inputPath}");
                return 66;
            }
        }

        CSharpCompilationRequest request;
        try
        {
            request = LoadAnalysisRequest(options);
        }
        catch (CSharpProjectLoadException exception)
        {
            Console.Error.WriteLine($"error USCS3001: {exception.Message}");
            return 2;
        }
        CSharpAnalysisResult result = new CSharpSemanticPipeline().Analyze(request);
        foreach (UnifiedDiagnostic diagnostic in result.Diagnostics)
        {
            Console.Error.WriteLine(FormatDiagnostic(diagnostic));
        }

        string? fullOutputDirectory = options.OutputDirectory is null
            ? null
            : Path.GetFullPath(options.OutputDirectory);
        string? baseName = fullOutputDirectory is null ? null : GetOutputBaseName(options);
        if (fullOutputDirectory is not null)
        {
            Directory.CreateDirectory(fullOutputDirectory);
            string profilePath = Path.Combine(fullOutputDirectory, $"{baseName}.profile.json");
            WriteAtomically(profilePath, VisualProfileJson.Serialize(result.Profile) + "\n");
            Console.WriteLine(profilePath);
        }

        if (!result.Succeeded || result.Hir is null || result.Semantics is null ||
            result.ScriptAssets is null)
        {
            Console.Error.WriteLine(
                "Analysis failed; the profile report was emitted when --out was supplied, but " +
                "declaration, HIR, semantics, and graph artifacts were not emitted.");
            return 2;
        }

        GraphDocument graph = new HirGraphProjector().Project(
            result.Hir,
            result.Semantics,
            result.DeclarationReferences);
        string hirJson = HirJson.Serialize(HirDocument.Create(result.Hir));
        string semanticJson = SemanticJson.Serialize(result.Semantics);
        string graphJson = GraphJson.Serialize(graph);
        string assetJson = ScriptAssetJson.Serialize(result.ScriptAssets);
        string authoringSurfaceJson = AuthoringSurfaceJson.Serialize(
            new AuthoringSurfaceProjector().Project(
                result.ScriptAssets,
                graph,
                result.DeclarationOwners));
        if (options.OutputDirectory is null)
        {
            Console.WriteLine(graphJson);
            return 0;
        }

        fullOutputDirectory ??= Path.GetFullPath(options.OutputDirectory);
        baseName ??= GetOutputBaseName(options);
        string hirPath = Path.Combine(fullOutputDirectory, $"{baseName}.hir.json");
        string semanticPath = Path.Combine(fullOutputDirectory, $"{baseName}.semantics.json");
        string graphPath = Path.Combine(fullOutputDirectory, $"{baseName}.graph.json");
        string assetPath = Path.Combine(fullOutputDirectory, $"{baseName}.asset.json");
        string authoringSurfacePath = Path.Combine(
            fullOutputDirectory,
            $"{baseName}.surface.json");
        File.WriteAllText(hirPath, hirJson + "\n");
        File.WriteAllText(semanticPath, semanticJson + "\n");
        File.WriteAllText(graphPath, graphJson + "\n");
        File.WriteAllText(assetPath, assetJson + "\n");
        File.WriteAllText(authoringSurfacePath, authoringSurfaceJson + "\n");
        Console.WriteLine(hirPath);
        Console.WriteLine(semanticPath);
        Console.WriteLine(graphPath);
        Console.WriteLine(assetPath);
        Console.WriteLine(authoringSurfacePath);
        foreach (IdentitySidecarDocument sidecar in result.IdentitySidecars)
        {
            string identityPath = Path.Combine(
                fullOutputDirectory,
                GetIdentityArtifactName(sidecar.SourcePath));
            WriteAtomically(identityPath, IdentitySidecarJson.Serialize(sidecar) + "\n");
            Console.WriteLine(identityPath);
        }

        return 0;
    }

    private static int RunIdentityInit(IReadOnlyList<string> args)
    {
        if (!TryParseIdentityArguments(args, out IdentityOptions? options))
        {
            PrintUsage();
            return 64;
        }

        options = options ?? throw new InvalidOperationException("Argument parsing succeeded without identity options.");
        if (!File.Exists(options.InputPath))
        {
            Console.Error.WriteLine($"Input file does not exist: {options.InputPath}");
            return 66;
        }

        string fullInputPath = Path.GetFullPath(options.InputPath);
        if (options.ProjectPath is not null)
        {
            return RunProjectIdentityInit(options, fullInputPath);
        }

        string logicalPath = GetLogicalInputPath(fullInputPath);
        MethodEffectCatalog effectCatalog = LoadEffectCatalog(options.EffectCatalogPath);
        var request = new CSharpCompilationRequest(
            options.AssemblyName ?? "Finare.Analysis",
            [new CSharpSourceDocument(
                logicalPath,
                File.ReadAllText(fullInputPath),
                IdentitySidecarDocument.CreateEmpty(logicalPath))],
            options.ReferencePaths,
            effectCatalog);
        CSharpAnalysisResult result = new CSharpSemanticPipeline().Analyze(request);
        foreach (UnifiedDiagnostic diagnostic in result.Diagnostics)
        {
            Console.Error.WriteLine(FormatDiagnostic(diagnostic));
        }

        if (!result.Succeeded || result.IdentitySidecars.Count != 1)
        {
            Console.Error.WriteLine("Identity initialization failed; no sidecar was written.");
            return 2;
        }

        string fullOutputPath = Path.GetFullPath(options.OutputPath);
        EnsureDifferentPath(fullInputPath, fullOutputPath, "Identity output cannot overwrite the input source.");
        WriteAtomically(
            fullOutputPath,
            IdentitySidecarJson.Serialize(result.IdentitySidecars[0]) + "\n");
        Console.WriteLine(fullOutputPath);
        return 0;
    }

    private static int RunProjectIdentityInit(IdentityOptions options, string fullInputPath)
    {
        string projectPath = Path.GetFullPath(options.ProjectPath!);
        MethodEffectCatalog effectCatalog = LoadEffectCatalog(options.EffectCatalogPath);
        CSharpProjectLoadResult project = new CSharpProjectLoader().Load(
            projectPath,
            effectCatalog,
            new CSharpProjectLoadOptions(
                options.Configuration,
                options.TargetFramework,
                options.Restore,
                options.Build,
                DotNetPath: options.DotNetPath));

        CSharpProjectSource[] matches = project.Sources
            .Where(source => string.Equals(
                Path.GetFullPath(source.Path),
                fullInputPath,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (matches.Length != 1)
        {
            throw new ArgumentException(
                $"Identity input '{fullInputPath}' must match exactly one Compile source in '{projectPath}' " +
                $"(found {matches.Length}).");
        }

        CSharpProjectSource selectedSource = matches[0];
        string fullOutputPath = Path.GetFullPath(options.OutputPath);
        ValidateProjectIdentityOutputPath(fullOutputPath, projectPath, project.Sources, selectedSource);
        CSharpSourceDocument[] sources = AttachIdentitySidecarsForInit(project, selectedSource);
        string[] explicitReferences = options.ReferencePaths
            .Select(Path.GetFullPath)
            .ToArray();
        string[] compileReferences = project.Request.ReferencePaths
            .Concat(explicitReferences)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        string[] runtimeReferences = project.Request.RuntimeReferencePaths
            .Concat(explicitReferences)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var request = new CSharpCompilationRequest(
            options.AssemblyName ?? project.Request.AssemblyName,
            sources,
            compileReferences,
            effectCatalog,
            project.Request.Settings,
            runtimeReferences,
            project.Request.IncludeDefaultReferences);
        CSharpAnalysisResult result = new CSharpSemanticPipeline().Analyze(request);
        foreach (UnifiedDiagnostic diagnostic in result.Diagnostics)
        {
            Console.Error.WriteLine(FormatDiagnostic(diagnostic));
        }

        IdentitySidecarDocument? selectedIdentity = result.IdentitySidecars
            .SingleOrDefault(sidecar => string.Equals(
                sidecar.SourcePath,
                selectedSource.LogicalPath,
                StringComparison.Ordinal));
        if (!result.Succeeded || selectedIdentity is null)
        {
            Console.Error.WriteLine("Identity initialization failed; no sidecar was written.");
            return 2;
        }

        WriteAtomically(
            fullOutputPath,
            IdentitySidecarJson.Serialize(selectedIdentity) + "\n");
        Console.WriteLine(fullOutputPath);
        return 0;
    }

    private static void ValidateProjectIdentityOutputPath(
        string outputPath,
        string projectPath,
        IReadOnlyList<CSharpProjectSource> projectSources,
        CSharpProjectSource selectedSource)
    {
        if (string.Equals(outputPath, projectPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Identity output cannot overwrite the project file.");
        }

        string selectedSidecarPath = Path.GetFullPath(selectedSource.Path + ".usids");
        foreach (CSharpProjectSource source in projectSources)
        {
            string sourcePath = Path.GetFullPath(source.Path);
            string sidecarPath = Path.GetFullPath(sourcePath + ".usids");
            if (string.Equals(outputPath, sourcePath, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(
                    $"Identity output cannot overwrite project source '{sourcePath}'.");
            }
            if (string.Equals(outputPath, sidecarPath, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(outputPath, selectedSidecarPath, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(
                    $"Identity output cannot overwrite another project source sidecar '{sidecarPath}'.");
            }
        }
    }

    private static CSharpSourceDocument[] AttachIdentitySidecarsForInit(
        CSharpProjectLoadResult project,
        CSharpProjectSource selectedSource)
    {
        Dictionary<string, CSharpProjectSource> sourcesByLogicalPath = project.Sources
            .ToDictionary(source => source.LogicalPath, StringComparer.Ordinal);
        var attached = new List<CSharpSourceDocument>(project.Request.Sources.Count);
        foreach (CSharpSourceDocument source in project.Request.Sources)
        {
            if (!sourcesByLogicalPath.TryGetValue(source.LogicalPath, out CSharpProjectSource? projectSource))
            {
                throw new ArgumentException(
                    $"Project source '{source.LogicalPath}' was not present in the physical source map.");
            }

            if (string.Equals(
                    Path.GetFullPath(projectSource.Path),
                    Path.GetFullPath(selectedSource.Path),
                    StringComparison.OrdinalIgnoreCase))
            {
                attached.Add(new CSharpSourceDocument(
                    source.LogicalPath,
                    source.Source,
                    IdentitySidecarDocument.CreateEmpty(source.LogicalPath),
                    source.IdentityPins));
                continue;
            }

            string adjacentPath = projectSource.Path + ".usids";
            if (!File.Exists(adjacentPath))
            {
                attached.Add(new CSharpSourceDocument(
                    source.LogicalPath,
                    source.Source,
                    identityPins: source.IdentityPins));
                continue;
            }

            IdentitySidecarDocument sidecar = LoadIdentitySidecar(adjacentPath);
            if (!IdentityBelongsToProjectSource(
                    new ProjectIdentityInput(Path.GetFullPath(adjacentPath), sidecar),
                    projectSource.Path))
            {
                throw new ArgumentException(
                    $"Identity sidecar '{adjacentPath}' does not belong to project source '{projectSource.Path}'.");
            }

            attached.Add(new CSharpSourceDocument(
                sidecar.SourcePath,
                source.Source,
                sidecar,
                source.IdentityPins));
        }

        return attached.ToArray();
    }

    private static int RunApply(IReadOnlyList<string> args, bool isPlan)
    {
        if (!TryParsePatchArguments(args, isPlan, out PatchOptions? options))
        {
            PrintUsage();
            return 64;
        }

        options = options ?? throw new InvalidOperationException("Argument parsing succeeded without patch options.");
        if (!File.Exists(options.InputPath))
        {
            Console.Error.WriteLine($"Input file does not exist: {options.InputPath}");
            return 66;
        }

        if (!File.Exists(options.EditDocumentPath))
        {
            Console.Error.WriteLine(
                $"{(isPlan ? "Patch plan" : "Patch")} file does not exist: " +
                options.EditDocumentPath);
            return 66;
        }

        string fullInputPath = Path.GetFullPath(options.InputPath);
        string fullOutputPath = Path.GetFullPath(options.OutputPath);
        StringComparison pathComparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (string.Equals(fullInputPath, fullOutputPath, pathComparison))
        {
            throw new ArgumentException(
                "In-place source patching is not supported yet; choose a different --out path.");
        }

        EnsureDifferentPath(
            Path.GetFullPath(options.EditDocumentPath),
            fullOutputPath,
            "Source output cannot overwrite the input edit document.");

        MethodEffectCatalog effectCatalog = LoadEffectCatalog(options.EffectCatalogPath);
        IdentitySidecarDocument? identitySidecar = options.IdentityPath is null
            ? null
            : LoadIdentitySidecar(options.IdentityPath);
        var request = new CSharpCompilationRequest(
            options.AssemblyName,
            [new CSharpSourceDocument(
                GetLogicalInputPath(fullInputPath),
                File.ReadAllText(fullInputPath),
                identitySidecar)],
            options.ReferencePaths,
            effectCatalog);
        string editJson = File.ReadAllText(options.EditDocumentPath);
        CSharpPatchResult result = isPlan
            ? new CSharpPatchPlanApplier().Apply(
                request,
                SemanticPatchPlanJson.Deserialize(editJson))
            : new CSharpSourcePatchApplier().Apply(
                request,
                SemanticPatchJson.Deserialize(editJson));
        foreach (UnifiedDiagnostic diagnostic in result.Diagnostics)
        {
            Console.Error.WriteLine(FormatDiagnostic(diagnostic));
        }

        if (!result.Succeeded || result.PatchedRequest is null)
        {
            Console.Error.WriteLine(
                $"{(isPlan ? "Patch plan" : "Patch")} failed; no source file was written.");
            return 3;
        }

        string patchedSource = AssertSinglePatchedSource(result.PatchedRequest);
        var outputs = new List<OutputFile>
        {
            new(fullOutputPath, patchedSource),
        };
        if (identitySidecar is not null)
        {
            string fullIdentityInputPath = Path.GetFullPath(options.IdentityPath!);
            string fullIdentityOutputPath = Path.GetFullPath(options.IdentityOutputPath!);
            EnsureDifferentPath(
                fullIdentityInputPath,
                fullIdentityOutputPath,
                "Identity output cannot overwrite the input sidecar.");
            EnsureDifferentPath(
                fullIdentityInputPath,
                fullOutputPath,
                "Source output cannot overwrite the input identity sidecar.");
            EnsureDifferentPath(
                fullInputPath,
                fullIdentityOutputPath,
                "Identity output cannot overwrite the input source.");
            EnsureDifferentPath(
                Path.GetFullPath(options.EditDocumentPath),
                fullIdentityOutputPath,
                "Identity output cannot overwrite the input edit document.");
            IdentitySidecarDocument updatedSidecar = AssertSingleIdentitySidecar(result.Analysis) with
            {
                SourcePath = GetLogicalInputPath(fullOutputPath),
            };
            outputs.Add(new OutputFile(
                fullIdentityOutputPath,
                IdentitySidecarJson.Serialize(updatedSidecar) + "\n"));
        }

        WriteTransactionally(outputs);
        Console.WriteLine(fullOutputPath);
        if (options.IdentityOutputPath is not null)
        {
            Console.WriteLine(Path.GetFullPath(options.IdentityOutputPath));
        }

        return 0;
    }

    private static int RunDeclarationApply(IReadOnlyList<string> args)
    {
        if (!TryParseDeclarationPatchArguments(args, out DeclarationPatchOptions? options))
        {
            PrintUsage();
            return 64;
        }

        options = options ?? throw new InvalidOperationException(
            "Argument parsing succeeded without declaration patch options.");
        if (!File.Exists(options.InputPath))
        {
            Console.Error.WriteLine($"Input file does not exist: {options.InputPath}");
            return 66;
        }

        if (!File.Exists(options.PatchPath))
        {
            Console.Error.WriteLine($"Declaration patch file does not exist: {options.PatchPath}");
            return 66;
        }

        string fullInputPath = Path.GetFullPath(options.InputPath);
        string fullPatchPath = Path.GetFullPath(options.PatchPath);
        string fullOutputPath = Path.GetFullPath(options.OutputPath);
        string fullAssetOutputPath = Path.GetFullPath(options.AssetOutputPath);
        var inputPaths = new List<string> { fullInputPath, fullPatchPath };
        if (options.IdentityPath is not null)
        {
            inputPaths.Add(Path.GetFullPath(options.IdentityPath));
        }

        var outputPaths = new List<string> { fullOutputPath, fullAssetOutputPath };
        if (options.IdentityOutputPath is not null)
        {
            outputPaths.Add(Path.GetFullPath(options.IdentityOutputPath));
        }

        foreach (string inputPath in inputPaths)
        {
            foreach (string outputPath in outputPaths)
            {
                EnsureDifferentPath(
                    inputPath,
                    outputPath,
                    "Declaration patch outputs cannot overwrite source, patch, or identity inputs.");
            }
        }

        IdentitySidecarDocument? identitySidecar = options.IdentityPath is null
            ? null
            : LoadIdentitySidecar(options.IdentityPath);
        MethodEffectCatalog effectCatalog = LoadEffectCatalog(options.EffectCatalogPath);
        var request = new CSharpCompilationRequest(
            options.AssemblyName,
            [new CSharpSourceDocument(
                GetLogicalInputPath(fullInputPath),
                File.ReadAllText(fullInputPath),
                identitySidecar)],
            options.ReferencePaths,
            effectCatalog);
        DeclarationPatchDocument patch = DeclarationPatchJson.Deserialize(File.ReadAllText(fullPatchPath));
        CSharpPatchResult result = new CSharpDeclarationPatchApplier().Apply(request, patch);
        foreach (UnifiedDiagnostic diagnostic in result.Diagnostics)
        {
            Console.Error.WriteLine(FormatDiagnostic(diagnostic));
        }

        if (!result.Succeeded || result.PatchedRequest is null || result.Analysis?.ScriptAssets is null)
        {
            Console.Error.WriteLine(
                "Declaration patch failed; source, identity, and asset outputs were not written.");
            return 3;
        }

        string patchedSource = AssertSinglePatchedSource(result.PatchedRequest);
        string outputLogicalPath = GetLogicalInputPath(fullOutputPath);
        IdentitySidecarDocument? updatedSidecar = identitySidecar is null
            ? null
            : AssertSingleIdentitySidecar(result.Analysis) with { SourcePath = outputLogicalPath };
        var outputRequest = new CSharpCompilationRequest(
            options.AssemblyName,
            [new CSharpSourceDocument(outputLogicalPath, patchedSource, updatedSidecar)],
            options.ReferencePaths,
            effectCatalog);
        CSharpAnalysisResult outputAnalysis = new CSharpSemanticPipeline().Analyze(outputRequest);
        foreach (UnifiedDiagnostic diagnostic in outputAnalysis.Diagnostics)
        {
            Console.Error.WriteLine(FormatDiagnostic(diagnostic));
        }

        if (!outputAnalysis.Succeeded || outputAnalysis.ScriptAssets is null)
        {
            Console.Error.WriteLine(
                "Declaration patch output validation failed; no output files were written.");
            return 3;
        }

        var outputs = new List<OutputFile>
        {
            new(fullOutputPath, patchedSource),
            new(fullAssetOutputPath, ScriptAssetJson.Serialize(outputAnalysis.ScriptAssets) + "\n"),
        };
        if (updatedSidecar is not null)
        {
            IdentitySidecarDocument outputSidecar = AssertSingleIdentitySidecar(outputAnalysis);
            outputs.Add(new OutputFile(
                Path.GetFullPath(options.IdentityOutputPath!),
                IdentitySidecarJson.Serialize(outputSidecar) + "\n"));
        }

        WriteTransactionally(outputs);
        foreach (OutputFile output in outputs)
        {
            Console.WriteLine(Path.GetFullPath(output.Path));
        }

        return 0;
    }

    private static CSharpSourceDocument[] LoadSources(
        IReadOnlyList<string> inputPaths,
        IReadOnlyList<string> identityPaths)
    {
        var identities = new Dictionary<string, IdentitySidecarDocument>(StringComparer.Ordinal);
        foreach (string identityPath in identityPaths)
        {
            IdentitySidecarDocument sidecar = LoadIdentitySidecar(identityPath);
            if (!identities.TryAdd(sidecar.SourcePath, sidecar))
            {
                throw new ArgumentException(
                    $"More than one identity sidecar was supplied for '{sidecar.SourcePath}'.");
            }
        }

        CSharpSourceDocument[] sources = inputPaths
            .Select(Path.GetFullPath)
            .Select(fullPath =>
            {
                string logicalPath = GetLogicalInputPath(fullPath);
                identities.Remove(logicalPath, out IdentitySidecarDocument? sidecar);
                return new CSharpSourceDocument(
                    logicalPath,
                    File.ReadAllText(fullPath),
                    sidecar);
            })
            .ToArray();
        if (identities.Count > 0)
        {
            throw new ArgumentException(
                $"Identity sidecar '{identities.Values.First().SourcePath}' does not match any input source.");
        }

        return sources;
    }

    private static CSharpCompilationRequest LoadAnalysisRequest(AnalyzeOptions options)
    {
        MethodEffectCatalog effectCatalog = LoadEffectCatalog(options.EffectCatalogPath);
        bool projectInput = options.InputPaths.Count == 1 &&
            string.Equals(
                Path.GetExtension(options.InputPaths[0]),
                ".csproj",
                StringComparison.OrdinalIgnoreCase);
        if (!projectInput && options.InputPaths.Any(path =>
                string.Equals(
                    Path.GetExtension(path),
                    ".csproj",
                    StringComparison.OrdinalIgnoreCase)))
        {
            throw new CSharpProjectLoadException(
                "A .csproj must be the command's only input; MSBuild owns its complete Compile item set.");
        }
        if (!projectInput)
        {
            if (options.Configuration != "Debug" || options.TargetFramework is not null ||
                !options.Restore || !options.Build)
            {
                throw new CSharpProjectLoadException(
                    "--configuration, --framework, --no-restore, and --no-build require a .csproj input.");
            }
            CSharpSourceDocument[] sources = LoadSources(options.InputPaths, options.IdentityPaths);
            return new CSharpCompilationRequest(
                options.AssemblyName ?? "Finare.Analysis",
                sources,
                options.ReferencePaths,
                effectCatalog);
        }

        CSharpProjectLoadResult loaded = new CSharpProjectLoader().Load(
            options.InputPaths[0],
            effectCatalog,
            new CSharpProjectLoadOptions(
                options.Configuration,
                options.TargetFramework,
                options.Restore,
                options.Build));
        CSharpSourceDocument[] projectSources = AttachIdentitySidecars(
            loaded.Request.Sources,
            loaded.Sources,
            options.IdentityPaths);
        string[] compileReferences = loaded.Request.ReferencePaths
            .Concat(options.ReferencePaths.Select(Path.GetFullPath))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        string[] runtimeReferences = loaded.Request.RuntimeReferencePaths
            .Concat(options.ReferencePaths.Select(Path.GetFullPath))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new CSharpCompilationRequest(
            options.AssemblyName ?? loaded.Request.AssemblyName,
            projectSources,
            compileReferences,
            effectCatalog,
            loaded.Request.Settings,
            runtimeReferences,
            loaded.Request.IncludeDefaultReferences);
    }

    internal static CSharpSourceDocument[] AttachIdentitySidecars(
        IReadOnlyList<CSharpSourceDocument> sources,
        IReadOnlyList<CSharpProjectSource> projectSources,
        IReadOnlyList<string> identityPaths)
    {
        // Ordinary builds and authoring must consume the same persistent IDs.
        // Explicit paths still support existing portable/non-adjacent sidecars.
        identityPaths = identityPaths.Select(Path.GetFullPath)
            .Concat(projectSources.Select(source => source.Path + ".usids").Where(File.Exists))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var identities = new List<ProjectIdentityInput>(identityPaths.Count);
        var identitySourcePaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (string identityPath in identityPaths)
        {
            IdentitySidecarDocument sidecar = LoadIdentitySidecar(identityPath);
            if (!identitySourcePaths.Add(sidecar.SourcePath))
            {
                throw new ArgumentException(
                    $"More than one identity sidecar was supplied for '{sidecar.SourcePath}'.");
            }

            identities.Add(new ProjectIdentityInput(Path.GetFullPath(identityPath), sidecar));
        }

        Dictionary<string, CSharpProjectSource> physicalSources = projectSources
            .ToDictionary(static source => source.LogicalPath, StringComparer.Ordinal);
        var assigned = new Dictionary<string, ProjectIdentityInput>(StringComparer.Ordinal);
        foreach (ProjectIdentityInput identity in identities)
        {
            CSharpSourceDocument[] exactMatches = sources
                .Where(source => string.Equals(
                    source.LogicalPath,
                    identity.Sidecar.SourcePath,
                    StringComparison.Ordinal))
                .ToArray();
            CSharpSourceDocument[] matches = exactMatches.Length > 0
                ? exactMatches
                : sources
                    .Where(source =>
                        physicalSources.TryGetValue(
                            source.LogicalPath,
                            out CSharpProjectSource? projectSource) &&
                        IdentityBelongsToProjectSource(identity, projectSource.Path))
                    .ToArray();
            if (matches.Length != 1)
            {
                string reason = matches.Length == 0 ? "does not match" : "ambiguously matches";
                throw new ArgumentException(
                    $"Identity sidecar '{identity.Sidecar.SourcePath}' {reason} a project source.");
            }
            if (!assigned.TryAdd(matches[0].LogicalPath, identity))
            {
                throw new ArgumentException(
                    $"More than one identity sidecar matched project source '{matches[0].LogicalPath}'.");
            }
        }

        CSharpSourceDocument[] attached = sources
            .Select(source =>
            {
                assigned.TryGetValue(source.LogicalPath, out ProjectIdentityInput? identity);
                return new CSharpSourceDocument(
                    identity?.Sidecar.SourcePath ?? source.LogicalPath,
                    source.Source,
                    identity?.Sidecar,
                    source.IdentityPins);
            })
            .ToArray();

        return attached;
    }

    private static bool IdentityBelongsToProjectSource(
        ProjectIdentityInput identity,
        string physicalSourcePath)
    {
        string fullSourcePath = Path.GetFullPath(physicalSourcePath);
        if (identity.Path.EndsWith(".usids", StringComparison.OrdinalIgnoreCase))
        {
            string adjacentSourcePath = Path.GetFullPath(
                identity.Path[..^".usids".Length]);
            if (string.Equals(
                adjacentSourcePath,
                fullSourcePath,
                StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        string sidecarSourcePath = identity.Sidecar.SourcePath.Replace('\\', '/');
        if (Path.IsPathRooted(identity.Sidecar.SourcePath))
        {
            return string.Equals(
                Path.GetFullPath(identity.Sidecar.SourcePath),
                fullSourcePath,
                StringComparison.OrdinalIgnoreCase);
        }

        string normalizedSourcePath = fullSourcePath.Replace('\\', '/');
        string normalizedSuffix = "/" + sidecarSourcePath.TrimStart('/');
        return normalizedSourcePath.EndsWith(normalizedSuffix, StringComparison.OrdinalIgnoreCase);
    }

    private sealed record ProjectIdentityInput(
        string Path,
        IdentitySidecarDocument Sidecar);

    private static IdentitySidecarDocument LoadIdentitySidecar(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Identity sidecar does not exist.", path);
        }

        return IdentitySidecarJson.Deserialize(File.ReadAllText(path));
    }

    private static MethodEffectCatalog LoadEffectCatalog(string? path)
    {
        MethodEffectCatalog catalog = MethodEffectCatalog.CreateDefault();
        if (path is null)
        {
            return catalog;
        }

        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Effect catalog does not exist.", path);
        }

        MethodEffectCatalogDocument document = MethodEffectCatalog.ReadDocument(File.ReadAllText(path));
        return catalog.WithOverrides(document.Rules);
    }

    private static bool TryParseCompilationArguments(
        IReadOnlyList<string> args,
        out AnalyzeOptions? options)
    {
        options = null;
        if (args.Count < 2 ||
            args[0] is not ("analyze" or "analyze-files" or "emit" or "emit-files"))
        {
            return false;
        }

        string command = args[0];
        var inputPaths = new List<string>();
        int index = 1;
        while (index < args.Count && !args[index].StartsWith("--", StringComparison.Ordinal))
        {
            inputPaths.Add(args[index++]);
        }

        if (inputPaths.Count == 0 ||
            command is "analyze" or "emit" && inputPaths.Count != 1)
        {
            return false;
        }

        string? outputDirectory = null;
        string? effectCatalogPath = null;
        string? assemblyName = null;
        string configuration = "Debug";
        string? targetFramework = null;
        bool restore = true;
        bool build = true;
        var referencePaths = new List<string>();
        var identityPaths = new List<string>();
        while (index < args.Count)
        {
            string option = args[index++];
            if (option == "--no-restore" && restore)
            {
                restore = false;
                continue;
            }
            if (option == "--no-build" && build)
            {
                build = false;
                continue;
            }
            if (index >= args.Count)
            {
                return false;
            }

            string value = args[index++];
            switch (option)
            {
                case "--out" when outputDirectory is null:
                    outputDirectory = value;
                    break;
                case "--effects" when effectCatalogPath is null:
                    effectCatalogPath = value;
                    break;
                case "--assembly" when assemblyName is null:
                    assemblyName = value;
                    break;
                case "--configuration" when configuration == "Debug":
                    configuration = value;
                    break;
                case "--framework" when targetFramework is null:
                    targetFramework = value;
                    break;
                case "--reference":
                    referencePaths.Add(value);
                    break;
                case "--identity":
                    identityPaths.Add(value);
                    break;
                default:
                    return false;
            }
        }

        options = new AnalyzeOptions(
            inputPaths,
            outputDirectory,
            effectCatalogPath,
            assemblyName,
            referencePaths,
            identityPaths,
            configuration,
            targetFramework,
            restore,
            build);
        return true;
    }

    private static bool TryParseChunkPackArguments(
        IReadOnlyList<string> args,
        out ChunkPackOptions? options)
    {
        options = null;
        if (args.Count < 13 || args[0] != "chunk-pack")
        {
            return false;
        }

        string? outputPath = null;
        string? runtimeManifestPath = null;
        string? assemblyPath = null;
        string? declarationPath = null;
        string? bindingManifestPath = null;
        string? dependencyManifestPath = null;
        string? portablePdbPath = null;
        string? sourceMapPath = null;
        string? authoringSurfacePath = null;
        int index = 1;
        while (index < args.Count)
        {
            string option = args[index++];
            if (index >= args.Count)
            {
                return false;
            }

            string value = args[index++];
            switch (option)
            {
                case "--out" when outputPath is null:
                    outputPath = value;
                    break;
                case "--runtime-manifest" when runtimeManifestPath is null:
                    runtimeManifestPath = value;
                    break;
                case "--assembly" when assemblyPath is null:
                    assemblyPath = value;
                    break;
                case "--declaration" when declarationPath is null:
                    declarationPath = value;
                    break;
                case "--bindings" when bindingManifestPath is null:
                    bindingManifestPath = value;
                    break;
                case "--dependencies" when dependencyManifestPath is null:
                    dependencyManifestPath = value;
                    break;
                case "--pdb" when portablePdbPath is null:
                    portablePdbPath = value;
                    break;
                case "--source-map" when sourceMapPath is null:
                    sourceMapPath = value;
                    break;
                case "--authoring-surface" when authoringSurfacePath is null:
                    authoringSurfacePath = value;
                    break;
                default:
                    return false;
            }
        }

        if (outputPath is null || runtimeManifestPath is null || assemblyPath is null ||
            declarationPath is null || bindingManifestPath is null ||
            dependencyManifestPath is null)
        {
            return false;
        }

        options = new ChunkPackOptions(
            outputPath,
            runtimeManifestPath,
            assemblyPath,
            declarationPath,
            bindingManifestPath,
            dependencyManifestPath,
            portablePdbPath,
            sourceMapPath,
            authoringSurfacePath);
        return true;
    }

    private static bool TryParseChunkProfileArguments(
        IReadOnlyList<string> args,
        out ChunkProfileOptions? options)
    {
        options = null;
        if (args.Count < 4 || args[0] != "chunk-profile" ||
            args[1].StartsWith("--", StringComparison.Ordinal))
        {
            return false;
        }

        string inputPath = args[1];
        string? outputPath = null;
        int trialCount = 3;
        bool trialCountSet = false;
        bool prepareEntryMethods = true;
        var referencePaths = new List<string>();
        int index = 2;
        while (index < args.Count)
        {
            string option = args[index++];
            if (option == "--no-jit-prepare" && prepareEntryMethods)
            {
                prepareEntryMethods = false;
                continue;
            }
            if (index >= args.Count)
            {
                return false;
            }
            string value = args[index++];
            switch (option)
            {
                case "--out" when outputPath is null:
                    outputPath = value;
                    break;
                case "--trials" when int.TryParse(value, out int parsedTrials) &&
                    parsedTrials is >= 1 and <= 50 && !trialCountSet:
                    trialCount = parsedTrials;
                    trialCountSet = true;
                    break;
                case "--reference":
                    referencePaths.Add(value);
                    break;
                default:
                    return false;
            }
        }
        if (outputPath is null)
        {
            return false;
        }
        options = new ChunkProfileOptions(
            inputPath,
            outputPath,
            trialCount,
            prepareEntryMethods,
            referencePaths);
        return true;
    }

    private static bool TryParseChunkFirstTickProfileArguments(
        IReadOnlyList<string> args,
        out ChunkFirstTickProfileOptions? options)
    {
        options = null;
        if (args.Count < 6 || args[0] != "chunk-first-tick-profile" ||
            args[1].StartsWith("--", StringComparison.Ordinal))
        {
            return false;
        }

        string inputPath = args[1];
        string? typeName = null;
        string? outputPath = null;
        int frameGapMilliseconds = 16;
        bool frameGapSet = false;
        bool disablePrewarm = false;
        var referencePaths = new List<string>();
        int index = 2;
        while (index < args.Count)
        {
            string option = args[index++];
            if (option == "--disable-prewarm" && !disablePrewarm)
            {
                disablePrewarm = true;
                continue;
            }
            if (index >= args.Count)
            {
                return false;
            }
            string value = args[index++];
            switch (option)
            {
                case "--type" when typeName is null && !string.IsNullOrWhiteSpace(value):
                    typeName = value;
                    break;
                case "--out" when outputPath is null:
                    outputPath = value;
                    break;
                case "--frame-gap-ms" when int.TryParse(value, out int parsedGap) &&
                    parsedGap is >= 0 and <= 100 && !frameGapSet:
                    frameGapMilliseconds = parsedGap;
                    frameGapSet = true;
                    break;
                case "--reference":
                    referencePaths.Add(value);
                    break;
                default:
                    return false;
            }
        }
        if (typeName is null || outputPath is null)
        {
            return false;
        }
        options = new ChunkFirstTickProfileOptions(
            inputPath,
            outputPath,
            typeName,
            frameGapMilliseconds,
            disablePrewarm,
            referencePaths);
        return true;
    }

    private static bool TryParseUpgradeArguments(
        IReadOnlyList<string> args,
        bool isPlan,
        out UpgradeOptions? options)
    {
        options = null;
        string expectedCommand = isPlan ? "upgrade-plan" : "upgrade-apply";
        if (args.Count < 4 || args[0] != expectedCommand ||
            args[1].StartsWith("--", StringComparison.Ordinal))
        {
            return false;
        }

        string inputPath = args[1];
        string? outputPath = null;
        string? policyPath = null;
        string? reportPath = null;
        bool failOnBlocked = false;
        int index = 2;
        while (index < args.Count)
        {
            string option = args[index++];
            if (option == "--fail-on-blocked" && !failOnBlocked)
            {
                failOnBlocked = true;
                continue;
            }
            if (index >= args.Count)
            {
                return false;
            }

            string value = args[index++];
            switch (option)
            {
                case "--out" when outputPath is null:
                    outputPath = value;
                    break;
                case "--policy" when policyPath is null:
                    policyPath = value;
                    break;
                case "--report" when !isPlan && reportPath is null:
                    reportPath = value;
                    break;
                default:
                    return false;
            }
        }

        if (outputPath is null || isPlan && reportPath is not null)
        {
            return false;
        }
        options = new UpgradeOptions(
            inputPath,
            outputPath,
            policyPath,
            reportPath,
            failOnBlocked);
        return true;
    }

    private static bool TryParseDiagnosticsBundleArguments(
        IReadOnlyList<string> args,
        out DiagnosticsBundleOptions? options)
    {
        options = null;
        if (args.Count < 4 || args[0] != "diagnostics-bundle" ||
            args[1].StartsWith("--", StringComparison.Ordinal))
        {
            return false;
        }

        string inputDirectory = args[1];
        string? outputPath = null;
        string? policyPath = null;
        int index = 2;
        while (index < args.Count)
        {
            string option = args[index++];
            if (index >= args.Count)
            {
                return false;
            }
            string value = args[index++];
            switch (option)
            {
                case "--out" when outputPath is null:
                    outputPath = value;
                    break;
                case "--policy" when policyPath is null:
                    policyPath = value;
                    break;
                default:
                    return false;
            }
        }
        if (outputPath is null)
        {
            return false;
        }
        options = new DiagnosticsBundleOptions(inputDirectory, outputPath, policyPath);
        return true;
    }

    private static bool TryParseUnrealProjectAdoptionArguments(
        IReadOnlyList<string> args,
        out UnrealProjectAdoptionCliOptions? options)
    {
        options = null;
        if (args.Count < 2 || args[0] != "adopt" ||
            args[1].StartsWith("--", StringComparison.Ordinal))
        {
            return false;
        }

        string projectPath = args[1];
        string? sdkRoot = null;
        string templateId = "minimal";
        string scriptName = "FirstScript";
        string? managedNamespace = null;
        string? reportPath = null;
        string? transactionId = null;
        bool templateSet = false;
        bool scriptNameSet = false;
        bool preview = false;
        bool apply = false;
        bool rollback = false;
        int index = 2;
        while (index < args.Count)
        {
            string option = args[index++];
            if (option == "--preview" && !preview)
            {
                preview = true;
                continue;
            }
            if (option == "--apply" && !apply)
            {
                apply = true;
                continue;
            }
            if (option == "--rollback" && !rollback)
            {
                rollback = true;
                continue;
            }
            if (index >= args.Count)
            {
                return false;
            }
            string value = args[index++];
            switch (option)
            {
                case "--sdk-root" when sdkRoot is null:
                    sdkRoot = value;
                    break;
                case "--template" when !templateSet:
                    templateId = value;
                    templateSet = true;
                    break;
                case "--script-name" when !scriptNameSet:
                    scriptName = value;
                    scriptNameSet = true;
                    break;
                case "--namespace" when managedNamespace is null:
                    managedNamespace = value;
                    break;
                case "--report" when reportPath is null:
                    reportPath = value;
                    break;
                case "--transaction" when transactionId is null:
                    transactionId = value;
                    break;
                default:
                    return false;
            }
        }
        if (preview && apply || rollback && (preview || apply) ||
            rollback && (sdkRoot is not null || managedNamespace is not null ||
                reportPath is not null || templateSet || scriptNameSet) ||
            !rollback && transactionId is not null)
        {
            return false;
        }
        options = new UnrealProjectAdoptionCliOptions(
            projectPath,
            sdkRoot,
            templateId,
            scriptName,
            managedNamespace,
            reportPath,
            preview,
            rollback,
            transactionId);
        return true;
    }

    private static bool TryParseUnrealProjectDoctorArguments(
        IReadOnlyList<string> args,
        out UnrealProjectDoctorCliOptions? options)
    {
        options = null;
        if (args.Count < 2 || args[0] != "doctor" ||
            args[1].StartsWith("--", StringComparison.Ordinal))
        {
            return false;
        }
        string? engineRoot = null;
        string? reportPath = null;
        int index = 2;
        while (index < args.Count)
        {
            string option = args[index++];
            if (index >= args.Count)
            {
                return false;
            }
            string value = args[index++];
            switch (option)
            {
                case "--engine-root" when engineRoot is null:
                    engineRoot = value;
                    break;
                case "--report" when reportPath is null:
                    reportPath = value;
                    break;
                default:
                    return false;
            }
        }
        options = new UnrealProjectDoctorCliOptions(args[1], engineRoot, reportPath);
        return true;
    }

    private static bool TryParseUnrealBindingArguments(
        IReadOnlyList<string> args,
        out UnrealBindingOptions? options)
    {
        options = null;
        if (args.Count < 4 || args[0] != "ue-bindings" ||
            args[1].StartsWith("--", StringComparison.Ordinal))
        {
            return false;
        }

        string manifestPath = args[1];
        string? outputPath = null;
        string? nativeOutputPath = null;
        string? reportPath = null;
        string? bindingNamespace = null;
        string? nativeHeader = null;
        string? nativeNamespace = null;
        string? contractPath = null;
        string? nodeCatalogOutputPath = null;
        string? browserOutputPath = null;
        bool failOnRejected = false;
        int index = 2;
        while (index < args.Count)
        {
            string option = args[index++];
            if (option == "--fail-on-rejected" && !failOnRejected)
            {
                failOnRejected = true;
                continue;
            }
            if (index >= args.Count)
            {
                return false;
            }

            string value = args[index++];
            switch (option)
            {
                case "--out" when outputPath is null:
                    outputPath = value;
                    break;
                case "--namespace" when bindingNamespace is null:
                    bindingNamespace = value;
                    break;
                case "--native-out" when nativeOutputPath is null:
                    nativeOutputPath = value;
                    break;
                case "--native-header" when nativeHeader is null:
                    nativeHeader = value;
                    break;
                case "--native-namespace" when nativeNamespace is null:
                    nativeNamespace = value;
                    break;
                case "--report" when reportPath is null:
                    reportPath = value;
                    break;
                case "--contract" when contractPath is null:
                    contractPath = value;
                    break;
                case "--nodes-out" when nodeCatalogOutputPath is null:
                    nodeCatalogOutputPath = value;
                    break;
                case "--browser-out" when browserOutputPath is null:
                    browserOutputPath = value;
                    break;
                default:
                    return false;
            }
        }

        if (outputPath is null)
        {
            return false;
        }

        options = new UnrealBindingOptions(
            manifestPath,
            outputPath,
            bindingNamespace ?? "Finare.Unreal.Generated",
            nativeOutputPath,
            nativeHeader ?? "FinareGeneratedThunks.h",
            nativeNamespace ?? "UE::Finare::Generated",
            reportPath,
            contractPath,
            nodeCatalogOutputPath,
            browserOutputPath,
            failOnRejected);
        return true;
    }

    private static bool TryParseUnrealContractProposalArguments(
        IReadOnlyList<string> args,
        out UnrealContractProposalOptions? options)
    {
        options = null;
        if (args.Count < 8 || args[0] != "ue-contract-propose" ||
            args[1].StartsWith("--", StringComparison.Ordinal))
        {
            return false;
        }

        string reflectionPath = args[1];
        string? requestPath = null;
        string? baseContractPath = null;
        string? outputPath = null;
        bool failOnOpenDecisions = false;
        int index = 2;
        while (index < args.Count)
        {
            string option = args[index++];
            if (option == "--fail-on-open-decisions" && !failOnOpenDecisions)
            {
                failOnOpenDecisions = true;
                continue;
            }
            if (index >= args.Count)
            {
                return false;
            }
            string value = args[index++];
            switch (option)
            {
                case "--requests" when requestPath is null:
                    requestPath = value;
                    break;
                case "--base" when baseContractPath is null:
                    baseContractPath = value;
                    break;
                case "--out" when outputPath is null:
                    outputPath = value;
                    break;
                default:
                    return false;
            }
        }
        if (requestPath is null || baseContractPath is null || outputPath is null)
        {
            return false;
        }
        options = new UnrealContractProposalOptions(
            reflectionPath,
            requestPath,
            baseContractPath,
            outputPath,
            failOnOpenDecisions);
        return true;
    }

    private static bool TryParseUnrealDevelopmentSurfaceArguments(
        IReadOnlyList<string> args,
        out UnrealDevelopmentSurfaceOptions? options)
    {
        options = null;
        if (args.Count < 10 || args[0] != "ue-surface-expand" ||
            args[1].StartsWith("--", StringComparison.Ordinal))
        {
            return false;
        }

        string reflectionPath = args[1];
        var projectPaths = new List<string>();
        string? baseContractPath = null;
        string? requestOutputPath = null;
        string? overlayOutputPath = null;
        string? reportOutputPath = null;
        string? lockOutputPath = null;
        string? verifyLockPath = null;
        string configuration = "Release";
        string? dotNetPath = null;
        bool failOnOpenDecisions = false;
        int index = 2;
        while (index < args.Count)
        {
            string option = args[index++];
            if (option == "--fail-on-open-decisions" && !failOnOpenDecisions)
            {
                failOnOpenDecisions = true;
                continue;
            }
            if (index >= args.Count)
            {
                return false;
            }
            string value = args[index++];
            switch (option)
            {
                case "--project":
                    projectPaths.Add(value);
                    break;
                case "--base" when baseContractPath is null:
                    baseContractPath = value;
                    break;
                case "--requests-out" when requestOutputPath is null:
                    requestOutputPath = value;
                    break;
                case "--overlay-out" when overlayOutputPath is null:
                    overlayOutputPath = value;
                    break;
                case "--report" when reportOutputPath is null:
                    reportOutputPath = value;
                    break;
                case "--lock-out" when lockOutputPath is null:
                    lockOutputPath = value;
                    break;
                case "--verify-lock" when verifyLockPath is null:
                    verifyLockPath = value;
                    break;
                case "--configuration" when configuration == "Release":
                    configuration = value;
                    break;
                case "--dotnet" when dotNetPath is null:
                    dotNetPath = value;
                    break;
                default:
                    return false;
            }
        }
        if (projectPaths.Count == 0 || baseContractPath is null ||
            requestOutputPath is null || overlayOutputPath is null ||
            reportOutputPath is null || string.IsNullOrWhiteSpace(configuration))
        {
            return false;
        }
        options = new UnrealDevelopmentSurfaceOptions(
            reflectionPath,
            projectPaths,
            baseContractPath,
            requestOutputPath,
            overlayOutputPath,
            reportOutputPath,
            lockOutputPath,
            verifyLockPath,
            configuration,
            dotNetPath,
            failOnOpenDecisions);
        return true;
    }

    private static bool TryParseUnrealReflectionHintArguments(
        IReadOnlyList<string> args,
        out UnrealReflectionHintOptions? options)
    {
        options = null;
        if (args.Count < 7 || args[0] != "ue-reflection-hints")
        {
            return false;
        }

        var projectPaths = new List<string>();
        string? baseContractPath = null;
        string? outputPath = null;
        string configuration = "Release";
        string? dotNetPath = null;
        int index = 1;
        while (index < args.Count)
        {
            string option = args[index++];
            if (index >= args.Count)
            {
                return false;
            }
            string value = args[index++];
            switch (option)
            {
                case "--project":
                    projectPaths.Add(value);
                    break;
                case "--base" when baseContractPath is null:
                    baseContractPath = value;
                    break;
                case "--out" when outputPath is null:
                    outputPath = value;
                    break;
                case "--configuration" when configuration == "Release":
                    configuration = value;
                    break;
                case "--dotnet" when dotNetPath is null:
                    dotNetPath = value;
                    break;
                default:
                    return false;
            }
        }
        if (projectPaths.Count == 0 || baseContractPath is null || outputPath is null ||
            string.IsNullOrWhiteSpace(configuration))
        {
            return false;
        }
        options = new UnrealReflectionHintOptions(
            projectPaths,
            baseContractPath,
            outputPath,
            configuration,
            dotNetPath);
        return true;
    }

    private static bool TryParseUnrealDataPlaneArguments(
        IReadOnlyList<string> args,
        out UnrealDataPlaneOptions? options)
    {
        options = null;
        if (args.Count < 6 || args[0] != "ue-data-plane" ||
            args[1].StartsWith("--", StringComparison.Ordinal))
        {
            return false;
        }

        string schemaPath = args[1];
        string? managedOutputPath = null;
        string? nativeOutputPath = null;
        int index = 2;
        while (index < args.Count)
        {
            string option = args[index++];
            if (index >= args.Count)
            {
                return false;
            }
            string value = args[index++];
            switch (option)
            {
                case "--managed-out" when managedOutputPath is null:
                    managedOutputPath = value;
                    break;
                case "--native-out" when nativeOutputPath is null:
                    nativeOutputPath = value;
                    break;
                default:
                    return false;
            }
        }

        if (managedOutputPath is null || nativeOutputPath is null)
        {
            return false;
        }
        options = new UnrealDataPlaneOptions(
            schemaPath,
            managedOutputPath,
            nativeOutputPath);
        return true;
    }

    private static bool TryParseUnrealDataPlaneExtensionArguments(
        IReadOnlyList<string> args,
        out UnrealDataPlaneExtensionOptions? options)
    {
        options = null;
        if (args.Count < 8 || args[0] != "ue-data-plane-extension" ||
            args[1].StartsWith("--", StringComparison.Ordinal))
        {
            return false;
        }

        string schemaPath = args[1];
        string? managedOutputPath = null;
        string? nativeOutputPath = null;
        string? catalogOutputPath = null;
        int index = 2;
        while (index < args.Count)
        {
            string option = args[index++];
            if (index >= args.Count)
            {
                return false;
            }
            string value = args[index++];
            switch (option)
            {
                case "--managed-out" when managedOutputPath is null:
                    managedOutputPath = value;
                    break;
                case "--native-out" when nativeOutputPath is null:
                    nativeOutputPath = value;
                    break;
                case "--catalog-out" when catalogOutputPath is null:
                    catalogOutputPath = value;
                    break;
                default:
                    return false;
            }
        }
        if (managedOutputPath is null || nativeOutputPath is null ||
            catalogOutputPath is null)
        {
            return false;
        }
        options = new UnrealDataPlaneExtensionOptions(
            schemaPath,
            managedOutputPath,
            nativeOutputPath,
            catalogOutputPath);
        return true;
    }

    private static bool TryParseUnrealPuertsInventoryArguments(
        IReadOnlyList<string> args,
        out UnrealPuertsInventoryOptions? options)
    {
        options = null;
        if (args.Count < 4 || args[0] != "ue-puerts-inventory" ||
            args[1].StartsWith("--", StringComparison.Ordinal))
        {
            return false;
        }

        string projectRoot = args[1];
        string? outputPath = null;
        string? coveragePath = null;
        string? reportPath = null;
        string? nativeProjectRoot = null;
        string? projectName = null;
        bool failOnUncovered = false;
        int index = 2;
        while (index < args.Count)
        {
            string option = args[index++];
            if (option == "--fail-on-uncovered" && !failOnUncovered)
            {
                failOnUncovered = true;
                continue;
            }
            if (index >= args.Count)
            {
                return false;
            }
            string value = args[index++];
            switch (option)
            {
                case "--out" when outputPath is null:
                    outputPath = value;
                    break;
                case "--coverage" when coveragePath is null:
                    coveragePath = value;
                    break;
                case "--report" when reportPath is null:
                    reportPath = value;
                    break;
                case "--native-project" when nativeProjectRoot is null:
                    nativeProjectRoot = value;
                    break;
                case "--project-name" when projectName is null:
                    projectName = value;
                    break;
                default:
                    return false;
            }
        }

        if (outputPath is null ||
            failOnUncovered && coveragePath is null ||
            reportPath is not null && coveragePath is null)
        {
            return false;
        }
        options = new UnrealPuertsInventoryOptions(
            projectRoot,
            nativeProjectRoot ?? projectRoot,
            projectName,
            outputPath,
            coveragePath,
            reportPath,
            failOnUncovered);
        return true;
    }

    private static bool TryParseUnrealPuertsScanArguments(
        IReadOnlyList<string> args,
        out UnrealPuertsScanOptions? options)
    {
        options = null;
        if (args.Count < 4 || args[0] != "ue-puerts-scan" ||
            args[1].StartsWith("--", StringComparison.Ordinal))
        {
            return false;
        }

        string projectRoot = args[1];
        string? outputPath = null;
        string? nativeProjectRoot = null;
        string? projectName = null;
        var scriptRoots = new List<string>();
        var nativeRoots = new List<string>();
        bool failOnBlockers = false;
        int index = 2;
        while (index < args.Count)
        {
            string option = args[index++];
            if (option == "--fail-on-blockers" && !failOnBlockers)
            {
                failOnBlockers = true;
                continue;
            }
            if (index >= args.Count)
            {
                return false;
            }
            string value = args[index++];
            switch (option)
            {
                case "--out" when outputPath is null:
                    outputPath = value;
                    break;
                case "--native-project" when nativeProjectRoot is null:
                    nativeProjectRoot = value;
                    break;
                case "--project-name" when projectName is null:
                    projectName = value;
                    break;
                case "--script-root":
                    scriptRoots.Add(value);
                    break;
                case "--native-root":
                    nativeRoots.Add(value);
                    break;
                default:
                    return false;
            }
        }
        if (outputPath is null)
        {
            return false;
        }
        options = new UnrealPuertsScanOptions(
            projectRoot,
            nativeProjectRoot ?? projectRoot,
            projectName,
            outputPath,
            scriptRoots,
            nativeRoots,
            failOnBlockers);
        return true;
    }

    private static bool TryParseUnrealPuertsPlanArguments(
        IReadOnlyList<string> args,
        out UnrealPuertsPlanOptions? options)
    {
        options = null;
        if (args.Count < 4 || args[0] != "ue-puerts-plan" ||
            args[1].StartsWith("--", StringComparison.Ordinal))
        {
            return false;
        }

        string scanPath = args[1];
        string? outputDirectory = null;
        string? contractPath = null;
        string? managedNamespace = null;
        bool failOnBlockers = false;
        int index = 2;
        while (index < args.Count)
        {
            string option = args[index++];
            if (option == "--fail-on-blockers" && !failOnBlockers)
            {
                failOnBlockers = true;
                continue;
            }
            if (index >= args.Count)
            {
                return false;
            }
            string value = args[index++];
            switch (option)
            {
                case "--out" when outputDirectory is null:
                    outputDirectory = value;
                    break;
                case "--contract" when contractPath is null:
                    contractPath = value;
                    break;
                case "--namespace" when managedNamespace is null:
                    managedNamespace = value;
                    break;
                default:
                    return false;
            }
        }
        if (outputDirectory is null)
        {
            return false;
        }
        options = new UnrealPuertsPlanOptions(
            scanPath,
            outputDirectory,
            contractPath,
            managedNamespace,
            failOnBlockers);
        return true;
    }

    private static bool TryParseUnrealPuertsVerifyArguments(
        IReadOnlyList<string> args,
        out UnrealPuertsVerifyOptions? options)
    {
        options = null;
        if (args.Count < 8 || args[0] != "ue-puerts-verify" ||
            args[1].StartsWith("--", StringComparison.Ordinal))
        {
            return false;
        }

        string harnessPath = args[1];
        string? puertsEvidencePath = null;
        string? unifiedScriptEvidencePath = null;
        string? outputPath = null;
        bool failOnMismatch = false;
        int index = 2;
        while (index < args.Count)
        {
            string option = args[index++];
            if (option == "--fail-on-mismatch" && !failOnMismatch)
            {
                failOnMismatch = true;
                continue;
            }
            if (index >= args.Count)
            {
                return false;
            }
            string value = args[index++];
            switch (option)
            {
                case "--puerts" when puertsEvidencePath is null:
                    puertsEvidencePath = value;
                    break;
                case "--unified" when unifiedScriptEvidencePath is null:
                    unifiedScriptEvidencePath = value;
                    break;
                case "--out" when outputPath is null:
                    outputPath = value;
                    break;
                default:
                    return false;
            }
        }
        if (puertsEvidencePath is null || unifiedScriptEvidencePath is null ||
            outputPath is null)
        {
            return false;
        }
        options = new UnrealPuertsVerifyOptions(
            harnessPath,
            puertsEvidencePath,
            unifiedScriptEvidencePath,
            outputPath,
            failOnMismatch);
        return true;
    }

    private static bool TryParseUnrealPuertsCoexistArguments(
        IReadOnlyList<string> args,
        out UnrealPuertsCoexistOptions? options)
    {
        options = null;
        if (args.Count < 4 || args[0] != "ue-puerts-coexist" ||
            args[1].StartsWith("--", StringComparison.Ordinal))
        {
            return false;
        }

        string planPath = args[1];
        string? outputDirectory = null;
        var unifiedManagedTypes = new Dictionary<string, string>(StringComparer.Ordinal);
        bool failOnBlockers = false;
        int index = 2;
        while (index < args.Count)
        {
            string option = args[index++];
            if (option == "--fail-on-blockers" && !failOnBlockers)
            {
                failOnBlockers = true;
                continue;
            }
            if (index >= args.Count)
            {
                return false;
            }
            string value = args[index++];
            if (option == "--out" && outputDirectory is null)
            {
                outputDirectory = value;
                continue;
            }
            if (option != "--unified")
            {
                return false;
            }
            int separator = value.IndexOf('=');
            if (separator <= 0 || separator == value.Length - 1 ||
                !unifiedManagedTypes.TryAdd(value[..separator], value[(separator + 1)..]))
            {
                return false;
            }
        }
        if (outputDirectory is null)
        {
            return false;
        }
        options = new UnrealPuertsCoexistOptions(
            planPath,
            outputDirectory,
            unifiedManagedTypes,
            failOnBlockers);
        return true;
    }

    private static bool TryParseExecuteArguments(
        IReadOnlyList<string> args,
        out ExecuteOptions? options)
    {
        options = null;
        if (args.Count < 4 || args[0] != "execute" || args[1].StartsWith("--", StringComparison.Ordinal))
        {
            return false;
        }

        string inputPath = args[1];
        string? qualifiedMethodName = null;
        SemanticId? methodId = null;
        string argumentsJson = "[]";
        bool hasArguments = false;
        string? effectCatalogPath = null;
        string? assemblyName = null;
        string? identityPath = null;
        string? receiver = null;
        var referencePaths = new List<string>();
        int index = 2;
        while (index < args.Count)
        {
            string option = args[index++];
            if (index >= args.Count)
            {
                return false;
            }

            string value = args[index++];
            switch (option)
            {
                case "--method" when qualifiedMethodName is null:
                    qualifiedMethodName = value;
                    break;
                case "--method-id" when methodId is null && SemanticId.TryParse(value, out SemanticId parsed):
                    methodId = parsed;
                    break;
                case "--args" when !hasArguments:
                    argumentsJson = value;
                    hasArguments = true;
                    break;
                case "--effects" when effectCatalogPath is null:
                    effectCatalogPath = value;
                    break;
                case "--assembly" when assemblyName is null:
                    assemblyName = value;
                    break;
                case "--reference":
                    referencePaths.Add(value);
                    break;
                case "--identity" when identityPath is null:
                    identityPath = value;
                    break;
                case "--receiver" when receiver is null:
                    receiver = value;
                    break;
                default:
                    return false;
            }
        }

        if ((qualifiedMethodName is null) == (methodId is null))
        {
            return false;
        }

        options = new ExecuteOptions(
            inputPath,
            qualifiedMethodName,
            methodId,
            argumentsJson,
            effectCatalogPath,
            assemblyName ?? "Finare.Execution",
            referencePaths,
            identityPath,
            receiver);
        return true;
    }

    private static bool TryParseDebugServeArguments(
        IReadOnlyList<string> args,
        out DebugServeOptions? options)
    {
        options = null;
        if (args.Count < 4 || args[0] != "debug-serve" || args[1].StartsWith("--", StringComparison.Ordinal))
        {
            return false;
        }

        string inputPath = args[1];
        string? contentRoot = null;
        string? effectCatalogPath = null;
        string? assemblyName = null;
        string? identityPath = null;
        string? declarationOutputPath = null;
        string? surfaceOutputPath = null;
        string? managedAssemblyOutputPath = null;
        string? portablePdbOutputPath = null;
        string? historyPath = null;
        string? graphLayoutPath = null;
        string? apiBrowserPath = null;
        string? projectPath = null;
        string configuration = "Debug";
        string? targetFramework = null;
        string? compiledChunkOutputPath = null;
        bool hasConfiguration = false;
        bool restore = true;
        bool build = true;
        bool publishInitial = false;
        int port = 4173;
        bool hasPort = false;
        var referencePaths = new List<string>();
        var additionalSourcePaths = new List<string>();
        int index = 2;
        while (index < args.Count)
        {
            string option = args[index++];
            if (option == "--no-restore" && restore) { restore = false; continue; }
            if (option == "--no-build" && build) { build = false; continue; }
            if (option == "--publish-initial" && !publishInitial) { publishInitial = true; continue; }
            if (index >= args.Count)
            {
                return false;
            }

            string value = args[index++];
            switch (option)
            {
                case "--root" when contentRoot is null:
                    contentRoot = value;
                    break;
                case "--port" when !hasPort && int.TryParse(value, out int parsedPort):
                    port = parsedPort;
                    hasPort = true;
                    break;
                case "--effects" when effectCatalogPath is null:
                    effectCatalogPath = value;
                    break;
                case "--assembly" when assemblyName is null:
                    assemblyName = value;
                    break;
                case "--reference":
                    referencePaths.Add(value);
                    break;
                case "--source":
                    additionalSourcePaths.Add(value);
                    break;
                case "--identity" when identityPath is null:
                    identityPath = value;
                    break;
                case "--asset-out" when declarationOutputPath is null:
                    declarationOutputPath = value;
                    break;
                case "--surface-out" when surfaceOutputPath is null:
                    surfaceOutputPath = value;
                    break;
                case "--managed-out" when managedAssemblyOutputPath is null:
                    managedAssemblyOutputPath = value;
                    break;
                case "--pdb-out" when portablePdbOutputPath is null:
                    portablePdbOutputPath = value;
                    break;
                case "--history" when historyPath is null:
                    historyPath = value;
                    break;
                case "--layout-out" when graphLayoutPath is null:
                    graphLayoutPath = value;
                    break;
                case "--api-browser" when apiBrowserPath is null:
                    apiBrowserPath = value;
                    break;
                case "--project" when projectPath is null:
                    projectPath = value;
                    break;
                case "--configuration" when !hasConfiguration:
                    configuration = value;
                    hasConfiguration = true;
                    break;
                case "--framework" when targetFramework is null:
                    targetFramework = value;
                    break;
                case "--chunk-out" when compiledChunkOutputPath is null:
                    compiledChunkOutputPath = value;
                    break;
                default:
                    return false;
            }
        }

        if (contentRoot is null || port is < 1024 or > 65535)
        {
            return false;
        }
        if (projectPath is null && (hasConfiguration || targetFramework is not null || !restore || !build))
            return false;
        if (projectPath is not null && (assemblyName is not null || referencePaths.Count != 0 || additionalSourcePaths.Count != 0))
            return false; // MSBuild, not command-line source guesses, owns the project compilation.

        options = new DebugServeOptions(
            inputPath,
            contentRoot,
            port,
            effectCatalogPath,
            assemblyName ?? "Finare.Analysis",
            referencePaths,
            identityPath,
            additionalSourcePaths,
            declarationOutputPath,
            surfaceOutputPath,
            managedAssemblyOutputPath,
            portablePdbOutputPath,
            historyPath,
            graphLayoutPath,
            apiBrowserPath,
            projectPath, configuration, targetFramework, restore, build, compiledChunkOutputPath, publishInitial);
        return true;
    }

    private static bool TryParseDebugFrameServeArguments(
        IReadOnlyList<string> args,
        out DebugFrameServeOptions? options)
    {
        options = null;
        if (args.Count < 2 || args[0] != "debug-frame-serve" ||
            args[1].StartsWith("--", StringComparison.Ordinal))
        {
            return false;
        }

        string inputPath = args[1];
        string? effectCatalogPath = null;
        string? assemblyName = null;
        string? identityPath = null;
        int port = 4174;
        int maximumConnections = 32;
        bool hasPort = false;
        bool hasMaximumConnections = false;
        var referencePaths = new List<string>();
        var additionalSourcePaths = new List<string>();
        int index = 2;
        while (index < args.Count)
        {
            string option = args[index++];
            if (index >= args.Count)
            {
                return false;
            }

            string value = args[index++];
            switch (option)
            {
                case "--port" when !hasPort && int.TryParse(value, out int parsedPort):
                    port = parsedPort;
                    hasPort = true;
                    break;
                case "--max-connections" when !hasMaximumConnections &&
                    int.TryParse(value, out int parsedMaximumConnections):
                    maximumConnections = parsedMaximumConnections;
                    hasMaximumConnections = true;
                    break;
                case "--effects" when effectCatalogPath is null:
                    effectCatalogPath = value;
                    break;
                case "--assembly" when assemblyName is null:
                    assemblyName = value;
                    break;
                case "--reference":
                    referencePaths.Add(value);
                    break;
                case "--source":
                    additionalSourcePaths.Add(value);
                    break;
                case "--identity" when identityPath is null:
                    identityPath = value;
                    break;
                default:
                    return false;
            }
        }

        if (port is > 0 and < 1024 or > 65535 ||
            maximumConnections is < 1 or > 256)
        {
            return false;
        }

        options = new DebugFrameServeOptions(
            inputPath,
            port,
            maximumConnections,
            effectCatalogPath,
            assemblyName ?? "Finare.RemoteDebug",
            referencePaths,
            identityPath,
            additionalSourcePaths);
        return true;
    }

    private static bool TryParseUnrealProfileDiagnosisArguments(
        IReadOnlyList<string> args,
        out UnrealProfileDiagnosisOptions? options)
    {
        options = null;
        if (args.Count != 9 || args[0] != "ue-profile-diagnose")
        {
            return false;
        }
        string? baselinePath = null;
        string? currentPath = null;
        string? evidencePath = null;
        string? outputPath = null;
        int index = 1;
        while (index < args.Count)
        {
            string option = args[index++];
            string value = args[index++];
            switch (option)
            {
                case "--baseline" when baselinePath is null:
                    baselinePath = value;
                    break;
                case "--current" when currentPath is null:
                    currentPath = value;
                    break;
                case "--evidence" when evidencePath is null:
                    evidencePath = value;
                    break;
                case "--out" when outputPath is null:
                    outputPath = value;
                    break;
                default:
                    return false;
            }
        }
        if (baselinePath is null || currentPath is null ||
            evidencePath is null || outputPath is null)
        {
            return false;
        }
        options = new UnrealProfileDiagnosisOptions(
            baselinePath,
            currentPath,
            evidencePath,
            outputPath);
        return true;
    }

    private static bool TryParseUnrealWorkloadComparisonArguments(
        IReadOnlyList<string> args,
        out UnrealWorkloadComparisonOptions? options)
    {
        options = null;
        if (args.Count != 9 || args[0] != "ue-workload-compare")
        {
            return false;
        }
        string? manifestPath = null;
        string? unifiedScriptResultPath = null;
        string? puertsResultPath = null;
        string? outputPath = null;
        int index = 1;
        while (index < args.Count)
        {
            string option = args[index++];
            string value = args[index++];
            switch (option)
            {
                case "--manifest" when manifestPath is null:
                    manifestPath = value;
                    break;
                case "--unified" when unifiedScriptResultPath is null:
                    unifiedScriptResultPath = value;
                    break;
                case "--puerts" when puertsResultPath is null:
                    puertsResultPath = value;
                    break;
                case "--out" when outputPath is null:
                    outputPath = value;
                    break;
                default:
                    return false;
            }
        }
        if (manifestPath is null || unifiedScriptResultPath is null ||
            puertsResultPath is null || outputPath is null)
        {
            return false;
        }
        options = new UnrealWorkloadComparisonOptions(
            manifestPath,
            unifiedScriptResultPath,
            puertsResultPath,
            outputPath);
        return true;
    }

    private static bool TryParseUnrealWorkloadBudgetArguments(
        IReadOnlyList<string> args,
        out UnrealWorkloadBudgetOptions? options)
    {
        options = null;
        if (args.Count != 9 || args[0] != "ue-workload-budget")
        {
            return false;
        }
        string? manifestPath = null;
        string? comparisonPath = null;
        string? budgetPath = null;
        string? outputPath = null;
        int index = 1;
        while (index < args.Count)
        {
            string option = args[index++];
            string value = args[index++];
            switch (option)
            {
                case "--manifest" when manifestPath is null:
                    manifestPath = value;
                    break;
                case "--comparison" when comparisonPath is null:
                    comparisonPath = value;
                    break;
                case "--budgets" when budgetPath is null:
                    budgetPath = value;
                    break;
                case "--out" when outputPath is null:
                    outputPath = value;
                    break;
                default:
                    return false;
            }
        }
        if (manifestPath is null || comparisonPath is null ||
            budgetPath is null || outputPath is null)
        {
            return false;
        }
        options = new UnrealWorkloadBudgetOptions(
            manifestPath,
            comparisonPath,
            budgetPath,
            outputPath);
        return true;
    }

    private static bool TryParseUnrealUsabilityBudgetArguments(
        IReadOnlyList<string> args,
        out UnrealUsabilityBudgetOptions? options)
    {
        options = null;
        if (args.Count is < 11 or > 12 || args[0] != "ue-usability-budget")
        {
            return false;
        }
        string? packagedFirstPlayPath = null;
        string? dailyReloadPath = null;
        string? automationEvidencePath = null;
        string? budgetPath = null;
        string? outputPath = null;
        bool failOnRegression = false;
        int index = 1;
        while (index < args.Count)
        {
            string option = args[index++];
            if (option == "--fail-on-regression" && !failOnRegression)
            {
                failOnRegression = true;
                continue;
            }
            if (index >= args.Count)
            {
                return false;
            }
            string value = args[index++];
            switch (option)
            {
                case "--packaged-first-play" when packagedFirstPlayPath is null:
                    packagedFirstPlayPath = value;
                    break;
                case "--daily-reload" when dailyReloadPath is null:
                    dailyReloadPath = value;
                    break;
                case "--automation-evidence" when automationEvidencePath is null:
                    automationEvidencePath = value;
                    break;
                case "--budgets" when budgetPath is null:
                    budgetPath = value;
                    break;
                case "--out" when outputPath is null:
                    outputPath = value;
                    break;
                default:
                    return false;
            }
        }
        if (packagedFirstPlayPath is null || dailyReloadPath is null ||
            automationEvidencePath is null || budgetPath is null || outputPath is null)
        {
            return false;
        }
        options = new UnrealUsabilityBudgetOptions(
            packagedFirstPlayPath,
            dailyReloadPath,
            automationEvidencePath,
            budgetPath,
            outputPath,
            failOnRegression);
        return true;
    }

    private static bool TryParseUnrealWorkloadAssemblyArguments(
        IReadOnlyList<string> args,
        out UnrealWorkloadAssemblyOptions? options)
    {
        options = null;
        if (args.Count != 9 || args[0] != "ue-workload-assemble")
        {
            return false;
        }
        string? manifestPath = null;
        string? captureDirectory = null;
        string? engineId = null;
        string? outputPath = null;
        int index = 1;
        while (index < args.Count)
        {
            string option = args[index++];
            string value = args[index++];
            switch (option)
            {
                case "--manifest" when manifestPath is null:
                    manifestPath = value;
                    break;
                case "--captures" when captureDirectory is null:
                    captureDirectory = value;
                    break;
                case "--engine" when engineId is null:
                    engineId = value;
                    break;
                case "--out" when outputPath is null:
                    outputPath = value;
                    break;
                default:
                    return false;
            }
        }
        if (manifestPath is null || captureDirectory is null ||
            engineId is null || outputPath is null)
        {
            return false;
        }
        options = new UnrealWorkloadAssemblyOptions(
            manifestPath,
            captureDirectory,
            engineId,
            outputPath);
        return true;
    }

    private static bool TryParseUnrealWorkloadCaptureArguments(
        IReadOnlyList<string> args,
        out UnrealWorkloadCaptureOptions? options)
    {
        options = null;
        if (args.Count != 7 || args[0] != "ue-workload-capture")
        {
            return false;
        }
        string? manifestPath = null;
        string? measurementPath = null;
        string? outputPath = null;
        int index = 1;
        while (index < args.Count)
        {
            string option = args[index++];
            string value = args[index++];
            switch (option)
            {
                case "--manifest" when manifestPath is null:
                    manifestPath = value;
                    break;
                case "--measurement" when measurementPath is null:
                    measurementPath = value;
                    break;
                case "--out" when outputPath is null:
                    outputPath = value;
                    break;
                default:
                    return false;
            }
        }
        if (manifestPath is null || measurementPath is null || outputPath is null)
        {
            return false;
        }
        options = new UnrealWorkloadCaptureOptions(
            manifestPath,
            measurementPath,
            outputPath);
        return true;
    }

    private static ExecutionArgument[] ParseExecutionArguments(string argumentsJson)
    {
        using JsonDocument document = JsonDocument.Parse(argumentsJson);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new ExecutionInvocationException(
                "UVR1005",
                "--args must contain one JSON array whose elements match the method parameters.");
        }

        return document.RootElement
            .EnumerateArray()
            .Select(static element => new ExecutionArgument(element.GetRawText()))
            .ToArray();
    }

    private static bool TryParseIdentityArguments(IReadOnlyList<string> args, out IdentityOptions? options)
    {
        options = null;
        if (args.Count < 4 || args[0] != "identity-init" || args[1].StartsWith("--", StringComparison.Ordinal))
        {
            return false;
        }

        string inputPath = args[1];
        string? outputPath = null;
        string? projectPath = null;
        string? effectCatalogPath = null;
        string? assemblyName = null;
        string configuration = "Debug";
        bool configurationSpecified = false;
        string? targetFramework = null;
        string? dotNetPath = null;
        var referencePaths = new List<string>();
        bool restore = true;
        bool build = true;
        int index = 2;
        while (index < args.Count)
        {
            string option = args[index++];
            if (option is "--no-restore" or "--no-build")
            {
                if (option == "--no-restore")
                {
                    restore = false;
                }
                else
                {
                    build = false;
                }
                continue;
            }
            if (index >= args.Count)
            {
                return false;
            }

            string value = args[index++];
            switch (option)
            {
                case "--out" when outputPath is null:
                    outputPath = value;
                    break;
                case "--project" when projectPath is null:
                    projectPath = value;
                    break;
                case "--effects" when effectCatalogPath is null:
                    effectCatalogPath = value;
                    break;
                case "--assembly" when assemblyName is null:
                    assemblyName = value;
                    break;
                case "--configuration" when !configurationSpecified:
                    configuration = value;
                    configurationSpecified = true;
                    break;
                case "--framework" when targetFramework is null:
                    targetFramework = value;
                    break;
                case "--dotnet" when dotNetPath is null:
                    dotNetPath = value;
                    break;
                case "--reference":
                    referencePaths.Add(value);
                    break;
                default:
                    return false;
            }
        }

        if (outputPath is null)
        {
            return false;
        }

        if (projectPath is null &&
            (targetFramework is not null ||
             dotNetPath is not null ||
             configurationSpecified ||
             !restore ||
             !build))
        {
            return false;
        }

        options = new IdentityOptions(
            inputPath,
            outputPath,
            projectPath,
            effectCatalogPath,
            assemblyName,
            referencePaths,
            configuration,
            targetFramework,
            dotNetPath,
            restore,
            build);
        return true;
    }

    private static bool TryParseRoundTripArguments(
        IReadOnlyList<string> args,
        out RoundTripOptions? options)
    {
        options = null;
        if (args.Count < 7 || args[0] != "verify-roundtrip" ||
            args[1].StartsWith("--", StringComparison.Ordinal) ||
            args[2].StartsWith("--", StringComparison.Ordinal))
        {
            return false;
        }

        string visualSourcePath = args[1];
        string codeSourcePath = args[2];
        string? requirementPath = null;
        string? reportPath = null;
        string? visualIdentityPath = null;
        string? codeIdentityPath = null;
        string? assemblyName = null;
        var referencePaths = new List<string>();
        int index = 3;
        while (index < args.Count)
        {
            string option = args[index++];
            if (index >= args.Count)
            {
                return false;
            }

            string value = args[index++];
            switch (option)
            {
                case "--requirements" when requirementPath is null:
                    requirementPath = value;
                    break;
                case "--report" when reportPath is null:
                    reportPath = value;
                    break;
                case "--visual-identity" when visualIdentityPath is null:
                    visualIdentityPath = value;
                    break;
                case "--code-identity" when codeIdentityPath is null:
                    codeIdentityPath = value;
                    break;
                case "--assembly" when assemblyName is null:
                    assemblyName = value;
                    break;
                case "--reference":
                    referencePaths.Add(value);
                    break;
                default:
                    return false;
            }
        }

        if (requirementPath is null || reportPath is null ||
            (visualIdentityPath is null) != (codeIdentityPath is null))
        {
            return false;
        }

        options = new RoundTripOptions(
            visualSourcePath,
            codeSourcePath,
            requirementPath,
            reportPath,
            visualIdentityPath,
            codeIdentityPath,
            assemblyName ?? "Finare.RoundTrip",
            referencePaths);
        return true;
    }

    private static bool TryParseProjectPatchArguments(
        IReadOnlyList<string> args,
        bool isPlan,
        out ProjectPatchOptions? options)
    {
        options = null;
        string command = isPlan ? "apply-project-plan" : "apply-project-patch";
        string editOption = isPlan ? "--plan" : "--patch";
        if (args.Count < 6 || args[0] != command ||
            args[1].StartsWith("--", StringComparison.Ordinal))
        {
            return false;
        }

        string projectPath = args[1];
        string? editDocumentPath = null;
        string? outputDirectory = null;
        string? effectCatalogPath = null;
        string? assemblyName = null;
        string configuration = "Debug";
        string? targetFramework = null;
        bool restore = true;
        bool build = true;
        var referencePaths = new List<string>();
        var identityPaths = new List<string>();
        int index = 2;
        while (index < args.Count)
        {
            string option = args[index++];
            if (option == "--no-restore" && restore)
            {
                restore = false;
                continue;
            }
            if (option == "--no-build" && build)
            {
                build = false;
                continue;
            }
            if (index >= args.Count)
            {
                return false;
            }

            string value = args[index++];
            if (option == editOption && editDocumentPath is null)
            {
                editDocumentPath = value;
                continue;
            }
            switch (option)
            {
                case "--out" when outputDirectory is null:
                    outputDirectory = value;
                    break;
                case "--effects" when effectCatalogPath is null:
                    effectCatalogPath = value;
                    break;
                case "--assembly" when assemblyName is null:
                    assemblyName = value;
                    break;
                case "--configuration" when configuration == "Debug":
                    configuration = value;
                    break;
                case "--framework" when targetFramework is null:
                    targetFramework = value;
                    break;
                case "--reference":
                    referencePaths.Add(value);
                    break;
                case "--identity":
                    identityPaths.Add(value);
                    break;
                default:
                    return false;
            }
        }

        if (editDocumentPath is null || outputDirectory is null)
        {
            return false;
        }
        options = new ProjectPatchOptions(
            projectPath,
            editDocumentPath,
            outputDirectory,
            effectCatalogPath,
            assemblyName,
            referencePaths,
            identityPaths,
            configuration,
            targetFramework,
            restore,
            build);
        return true;
    }

    private static bool TryParseLayoutArguments(
        IReadOnlyList<string> args,
        bool synchronize,
        out LayoutOptions? options)
    {
        options = null;
        string command = synchronize ? "layout-sync" : "layout-init";
        if (args.Count < 4 || args[0] != command || args[1].StartsWith("--", StringComparison.Ordinal))
        {
            return false;
        }

        string graphPath = args[1];
        string? layoutPath = null;
        string? outputPath = null;
        int index = 2;
        while (index < args.Count)
        {
            string option = args[index++];
            if (index >= args.Count)
            {
                return false;
            }

            string value = args[index++];
            switch (option)
            {
                case "--layout" when synchronize && layoutPath is null:
                    layoutPath = value;
                    break;
                case "--out" when outputPath is null:
                    outputPath = value;
                    break;
                default:
                    return false;
            }
        }

        if (outputPath is null || synchronize && layoutPath is null)
        {
            return false;
        }

        options = new LayoutOptions(graphPath, layoutPath, outputPath);
        return true;
    }

    private static bool TryParsePatchArguments(
        IReadOnlyList<string> args,
        bool isPlan,
        out PatchOptions? options)
    {
        options = null;
        string command = isPlan ? "apply-plan" : "apply-patch";
        string editOption = isPlan ? "--plan" : "--patch";
        if (args.Count < 6 || args[0] != command || args[1].StartsWith("--", StringComparison.Ordinal))
        {
            return false;
        }

        string inputPath = args[1];
        string? editDocumentPath = null;
        string? outputPath = null;
        string? effectCatalogPath = null;
        string? assemblyName = null;
        string? identityPath = null;
        string? identityOutputPath = null;
        var referencePaths = new List<string>();
        int index = 2;
        while (index < args.Count)
        {
            string option = args[index++];
            if (index >= args.Count)
            {
                return false;
            }

            string value = args[index++];
            if (option == editOption && editDocumentPath is null)
            {
                editDocumentPath = value;
                continue;
            }

            switch (option)
            {
                case "--out" when outputPath is null:
                    outputPath = value;
                    break;
                case "--effects" when effectCatalogPath is null:
                    effectCatalogPath = value;
                    break;
                case "--assembly" when assemblyName is null:
                    assemblyName = value;
                    break;
                case "--reference":
                    referencePaths.Add(value);
                    break;
                case "--identity" when identityPath is null:
                    identityPath = value;
                    break;
                case "--out-identity" when identityOutputPath is null:
                    identityOutputPath = value;
                    break;
                default:
                    return false;
            }
        }

        if (editDocumentPath is null || outputPath is null ||
            (identityPath is null) != (identityOutputPath is null))
        {
            return false;
        }

        options = new PatchOptions(
            inputPath,
            editDocumentPath,
            outputPath,
            effectCatalogPath,
            assemblyName ?? "Finare.Analysis",
            referencePaths,
            identityPath,
            identityOutputPath);
        return true;
    }

    private static bool TryParseDeclarationPatchArguments(
        IReadOnlyList<string> args,
        out DeclarationPatchOptions? options)
    {
        options = null;
        if (args.Count < 8 || args[0] != "apply-declaration-patch" ||
            args[1].StartsWith("--", StringComparison.Ordinal))
        {
            return false;
        }

        string inputPath = args[1];
        string? patchPath = null;
        string? outputPath = null;
        string? assetOutputPath = null;
        string? effectCatalogPath = null;
        string? assemblyName = null;
        string? identityPath = null;
        string? identityOutputPath = null;
        var referencePaths = new List<string>();
        int index = 2;
        while (index < args.Count)
        {
            string option = args[index++];
            if (index >= args.Count)
            {
                return false;
            }

            string value = args[index++];
            switch (option)
            {
                case "--declaration-patch" when patchPath is null:
                    patchPath = value;
                    break;
                case "--out" when outputPath is null:
                    outputPath = value;
                    break;
                case "--out-asset" when assetOutputPath is null:
                    assetOutputPath = value;
                    break;
                case "--effects" when effectCatalogPath is null:
                    effectCatalogPath = value;
                    break;
                case "--assembly" when assemblyName is null:
                    assemblyName = value;
                    break;
                case "--reference":
                    referencePaths.Add(value);
                    break;
                case "--identity" when identityPath is null:
                    identityPath = value;
                    break;
                case "--out-identity" when identityOutputPath is null:
                    identityOutputPath = value;
                    break;
                default:
                    return false;
            }
        }

        if (patchPath is null || outputPath is null || assetOutputPath is null ||
            (identityPath is null) != (identityOutputPath is null))
        {
            return false;
        }

        options = new DeclarationPatchOptions(
            inputPath,
            patchPath,
            outputPath,
            assetOutputPath,
            effectCatalogPath,
            assemblyName ?? "Finare.Analysis",
            referencePaths,
            identityPath,
            identityOutputPath);
        return true;
    }

    private static string FormatDiagnostic(UnifiedDiagnostic diagnostic) =>
        $"{diagnostic.Span.FilePath}({diagnostic.Span.Start.Line},{diagnostic.Span.Start.Column}): " +
        $"{diagnostic.Severity.ToString().ToLowerInvariant()} {diagnostic.Code}: {diagnostic.Message}";

    private static int RunScriptClassReferencePlan(IReadOnlyList<string> args)
    {
        if (!TryParseScriptClassReferencePlanArguments(
                args,
                out ScriptClassReferencePlanOptions? options) ||
            options is null)
        {
            PrintUsage();
            return 64;
        }

        CSharpProjectLoadResult project = new CSharpProjectLoader().Load(
            options.ProjectPath,
            options: new CSharpProjectLoadOptions(
                options.Configuration,
                options.TargetFramework,
                options.Restore,
                options.Build,
                DotNetPath: options.DotNetPath));
        CSharpTypeReferencePlan plan = new CSharpTypeReferencePlanner().Plan(
            project,
            options.ManagedTypeName,
            options.ReplacementTypeName);
        string json = JsonSerializer.Serialize(
            plan,
            UnifiedJson.CreateOptions(writeIndented: true)) + "\n";
        WriteAtomically(options.OutputPath, json);
        Console.WriteLine(Path.GetFullPath(options.OutputPath));
        return 0;
    }

    private static bool TryParseScriptClassReferencePlanArguments(
        IReadOnlyList<string> args,
        out ScriptClassReferencePlanOptions? options)
    {
        options = null;
        if (args.Count < 2 || args[0] != "script-class-reference-plan")
        {
            return false;
        }
        string projectPath = args[1];
        string? managedTypeName = null;
        string? replacementTypeName = null;
        string? outputPath = null;
        string configuration = "Debug";
        string? targetFramework = null;
        string? dotNetPath = null;
        bool restore = false;
        bool build = false;
        for (int index = 2; index < args.Count; index++)
        {
            string option = args[index];
            if (option == "--restore")
            {
                restore = true;
                continue;
            }
            if (option == "--build")
            {
                build = true;
                continue;
            }
            if (++index >= args.Count)
            {
                return false;
            }
            string value = args[index];
            switch (option)
            {
                case "--type" when managedTypeName is null:
                    managedTypeName = value;
                    break;
                case "--new-name" when replacementTypeName is null:
                    replacementTypeName = value;
                    break;
                case "--out" when outputPath is null:
                    outputPath = value;
                    break;
                case "--configuration":
                    configuration = value;
                    break;
                case "--framework" when targetFramework is null:
                    targetFramework = value;
                    break;
                case "--dotnet" when dotNetPath is null:
                    dotNetPath = value;
                    break;
                default:
                    return false;
            }
        }
        if (string.IsNullOrWhiteSpace(projectPath) ||
            string.IsNullOrWhiteSpace(managedTypeName) ||
            string.IsNullOrWhiteSpace(outputPath) ||
            string.IsNullOrWhiteSpace(configuration))
        {
            return false;
        }
        options = new ScriptClassReferencePlanOptions(
            projectPath,
            managedTypeName,
            replacementTypeName,
            outputPath,
            configuration,
            targetFramework,
            restore,
            build,
            dotNetPath);
        return true;
    }

    private static string GetLogicalInputPath(string fullInputPath)
    {
        string relativePath = Path.GetRelativePath(Environment.CurrentDirectory, fullInputPath);
        bool escapesWorkingDirectory = relativePath == ".." ||
            relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal);
        string path = escapesWorkingDirectory ? fullInputPath : relativePath;
        return path.Replace(Path.DirectorySeparatorChar, '/');
    }

    private static string GetOutputBaseName(AnalyzeOptions options)
    {
        if (options.InputPaths.Count == 1)
        {
            return Path.GetFileNameWithoutExtension(options.InputPaths[0]);
        }

        char[] invalidCharacters = Path.GetInvalidFileNameChars();
        return new string((options.AssemblyName ?? "Finare.Analysis")
            .Select(character => invalidCharacters.Contains(character) ? '_' : character)
            .ToArray());
    }

    private static string GetIdentityArtifactName(string sourcePath)
    {
        char[] invalidCharacters = Path.GetInvalidFileNameChars();
        return new string(sourcePath
            .Select(character => character is '/' or '\\' || invalidCharacters.Contains(character)
                ? '_'
                : character)
            .ToArray()) + ".usids";
    }

    private static string AssertSinglePatchedSource(CSharpCompilationRequest request)
    {
        if (request.Sources.Count != 1)
        {
            throw new InvalidOperationException("The apply-patch CLI currently requires one source document.");
        }

        return request.Sources[0].Source;
    }

    private static IdentitySidecarDocument AssertSingleIdentitySidecar(CSharpAnalysisResult? analysis)
    {
        if (analysis?.IdentitySidecars.Count != 1)
        {
            throw new InvalidOperationException("A sidecar-enabled patch must produce one updated identity sidecar.");
        }

        return analysis.IdentitySidecars[0];
    }

    private static void EnsureDifferentPath(string first, string second, string message)
    {
        StringComparison pathComparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (string.Equals(first, second, pathComparison))
        {
            throw new ArgumentException(message);
        }
    }

    private static string ResolveOutputPath(string outputDirectory, string logicalPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(logicalPath);
        if (Path.IsPathRooted(logicalPath))
        {
            throw new ArgumentException(
                $"Project source logical path '{logicalPath}' must be relative.",
                nameof(logicalPath));
        }

        string fullRoot = Path.GetFullPath(outputDirectory);
        string candidate = Path.GetFullPath(Path.Combine(
            fullRoot,
            logicalPath.Replace('/', Path.DirectorySeparatorChar)));
        string rootPrefix = fullRoot.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        StringComparison comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!candidate.StartsWith(rootPrefix, comparison))
        {
            throw new ArgumentException(
                $"Project source logical path '{logicalPath}' escapes the output directory.",
                nameof(logicalPath));
        }
        return candidate;
    }

    internal static void WriteAtomically(string outputPath, string content)
        => WriteTransactionally([new OutputFile(outputPath, content)]);

    private static void WriteTransactionally(IReadOnlyList<OutputFile> outputs)
    {
        string[] fullPaths = outputs.Select(output => Path.GetFullPath(output.Path)).ToArray();
        if (fullPaths.Distinct(StringComparer.OrdinalIgnoreCase).Count() != fullPaths.Length)
        {
            throw new ArgumentException("Transactional output paths must be distinct.", nameof(outputs));
        }

        var pending = new List<PendingOutput>(outputs.Count);
        var movedTargets = new List<PendingOutput>(outputs.Count);
        bool committed = false;
        try
        {
            for (int index = 0; index < outputs.Count; index++)
            {
                string fullPath = fullPaths[index];
                string directory = Path.GetDirectoryName(fullPath) ?? Environment.CurrentDirectory;
                Directory.CreateDirectory(directory);
                string nonce = Guid.NewGuid().ToString("N");
                string temporaryPath = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{nonce}.tmp");
                string backupPath = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{nonce}.bak");
                File.WriteAllBytes(temporaryPath, outputs[index].Content);
                pending.Add(new PendingOutput(fullPath, temporaryPath, backupPath));
            }

            foreach (PendingOutput output in pending)
            {
                if (File.Exists(output.Path))
                {
                    File.Move(output.Path, output.BackupPath);
                }

                File.Move(output.TemporaryPath, output.Path);
                movedTargets.Add(output);
            }

            committed = true;
        }
        catch
        {
            foreach (PendingOutput output in movedTargets.AsEnumerable().Reverse())
            {
                TryDelete(output.Path);
            }

            foreach (PendingOutput output in pending.AsEnumerable().Reverse())
            {
                if (File.Exists(output.BackupPath))
                {
                    File.Move(output.BackupPath, output.Path, overwrite: true);
                }
            }

            throw;
        }
        finally
        {
            foreach (PendingOutput output in pending)
            {
                TryDelete(output.TemporaryPath);
                if (committed)
                {
                    TryDelete(output.BackupPath);
                }
            }
        }
    }

    private static void TryDelete(string path)
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

    private static void PrintUsage() => Console.Error.WriteLine(
        "Usage:\n" +
        "  finare script-class-reference-plan <project.csproj> --type <Namespace.Type> [--new-name <Type>] --out <plan.json> [--configuration <name>] [--framework <tfm>] [--dotnet <path>] [--restore] [--build]\n" +
        "  finare script-class-identity-plan <project.csproj> --request <request.json> --out <plan.json> [--configuration <name>] [--framework <tfm>] [--dotnet <path>]\n" +
        "  finare analyze <input.cs|project.csproj> [options]\n" +
        "  finare analyze-files <input.cs> <additional.cs>... [options]\n" +
        "  finare emit <input.cs|project.csproj> --out <directory> [options]\n" +
        "  finare emit-files <input.cs> <additional.cs>... --out <directory> [options]\n" +
        "  finare chunk-pack --out <program.uschunk> --runtime-manifest <file> --assembly <file.dll> --declaration <file> --bindings <file> --dependencies <file> [sidecars]\n" +
        "  finare chunk-inspect <program.uschunk>\n" +
        "  finare chunk-profile <program.uschunk> --out <profile.json> [--trials <1-50>] [--reference <assembly>] [--no-jit-prepare]\n" +
        "  finare chunk-first-tick-profile <program.uschunk> --type <entry-type> --out <profile.json> [--frame-gap-ms <0-100>] [--reference <assembly>] [--disable-prewarm]\n" +
        "  finare upgrade-plan <file|directory> --out <report.json> [--policy <policy.json>] [--fail-on-blocked]\n" +
        "  finare upgrade-apply <file|directory> --out <directory> [--report <report.json>] [--policy <policy.json>]\n" +
        "  finare diagnostics-bundle <directory> --out <bundle.zip> [--policy <policy.json>]\n" +
        "  finare adopt <Game.uproject> [--preview|--apply] [--sdk-root <directory>] [--template <id>] [--script-name <name>] [--namespace <name>] [--report <plan.json>]\n" +
        "  finare adopt <Game.uproject> --rollback [--transaction <id>]\n" +
        "  finare doctor <Game.uproject> [--engine-root <directory>] [--report <doctor.json>]\n" +
        "  finare guide <index|first-project|recipes> [--sdk-root <directory>] [--json]\n" +
        "  finare identity-init <input.cs> --out <identity.usids> [--project <project.csproj> --configuration <Debug|Release> --framework <tfm> --no-restore --no-build --dotnet <path>] [options]\n" +
        "  finare layout-init <graph.json> --out <layout.usgraph>\n" +
        "  finare layout-sync <graph.json> --layout <layout.usgraph> --out <updated.usgraph>\n" +
        "  finare semantic-diff <baseline.graph.json> <target.graph.json> --out <diff.json> [--fail-on-change]\n" +
        "  finare semantic-merge <baseline.graph.json> <ours.graph.json> <theirs.graph.json> --report <merge.json> --out <merged.graph.json>\n" +
        "  finare ai-proposal-pack <plan.json> --title <text> --intent <text> --producer <id> --producer-version <version> [--model <id>] --out <proposal.json>\n" +
        "  finare apply-patch <input.cs> --patch <patch.json> --out <output.cs> [options]\n" +
        "  finare apply-plan <input.cs> --plan <plan.json> --out <output.cs> [options]\n" +
        "  finare apply-project-patch <project.csproj> --patch <patch.json> --out <directory> [options]\n" +
        "  finare apply-project-plan <project.csproj> --plan <plan.json> --out <directory> [options]\n" +
        "  finare apply-declaration-patch <input.cs> --declaration-patch <patch.json> " +
        "--out <output.cs> --out-asset <asset.json> [options]\n" +
        "  finare execute <input.cs> (--method <name> | --method-id <us_...>) [options]\n" +
        "  finare debug-serve <input.cs> --root <visual-editor-directory> [--project <project.csproj>] [--port <number>] [--api-browser <api-browser.json>] [options]\n" +
        "  finare debug-frame-serve <input.cs> [--port <number>] [--max-connections <count>] [options]\n" +
        "  finare ue-bindings <reflection.json> --out <bindings.cs> [--contract <api-contract.json>] [--native-out <thunks.cpp>] [--nodes-out <nodes.json>] [--browser-out <api-browser.json>] [--report <coverage.json>] [--fail-on-rejected] [--namespace <name>]\n" +
        "  finare ue-contract-propose <reflection.json> --requests <contract-requests.json> --base <api-contract.json> --out <proposal.json> [--fail-on-open-decisions]\n" +
        "  finare ue-reflection-hints --project <managed.csproj> [--project <managed.csproj>...] --base <api-contract.json> --out <type-hints.json> [--configuration <name>] [--dotnet <path>]\n" +
        "  finare ue-surface-expand <reflection.json> --project <managed.csproj> [--project <managed.csproj>...] --base <api-contract.json> --requests-out <requests.json> --overlay-out <development-contract.json> --report <surface.json> [--lock-out <surface-lock.json>] [--verify-lock <surface-lock.json>] [--configuration <name>] [--dotnet <path>] [--fail-on-open-decisions]\n" +
        "  finare ue-data-plane <schema.json> --managed-out <layouts.g.cs> --native-out <layouts.gen.h>\n" +
        "  finare ue-data-plane-extension <schema.json> --managed-out <extension.g.cs> --native-out <extension.gen.h> --catalog-out <catalog.json>\n" +
        "  finare ue-puerts-inventory <puerts-project> --out <inventory.json> [--native-project <target-project>] [--project-name <name>] [--coverage <coverage.json> --report <report.json> --fail-on-uncovered]\n" +
        "  finare ue-puerts-scan <puerts-project> --out <scan.json> [--native-project <project>] [--project-name <name>] [--script-root <directory>]... [--native-root <directory>]... [--fail-on-blockers]\n" +
        "  finare ue-puerts-plan <scan.json> --out <directory> [--contract <api-contract.json>] [--namespace <name>] [--fail-on-blockers]\n" +
        "  finare ue-puerts-coexist <migration-plan.json> --out <directory> [--unified <SourceType=Managed.Type>]... [--fail-on-blockers]\n" +
        "  finare ue-puerts-verify <harness.json> --puerts <evidence.json> --unified <evidence.json> --out <report.json> [--fail-on-mismatch]\n" +
        "  finare ue-profile-diagnose --baseline <profile.json> --current <profile.json> --evidence <evidence.json> --out <report.json>\n" +
        "  finare ue-workload-capture --manifest <suite.json> --measurement <raw.json> --out <capture.json>\n" +
        "  finare ue-workload-assemble --manifest <suite.json> --captures <directory> --engine <unifiedScript|puerts> --out <result.json>\n" +
        "  finare ue-workload-compare --manifest <suite.json> --unified <result.json> --puerts <result.json> --out <report.json>\n" +
        "  finare ue-workload-budget --manifest <suite.json> --comparison <report.json> --budgets <budgets.json> --out <budget-report.json>\n" +
        "  finare ue-usability-budget --packaged-first-play <report.json> --daily-reload <report.json> --automation-evidence <log> --budgets <budgets.json> --out <report.json> [--fail-on-regression]\n" +
        "  finare verify-roundtrip <visual.cs> <code.cs> --requirements <requirements.json> --report <report.json> [options]\n" +
        "Options:\n" +
        "  --out <path>            Artifact directory, initialized sidecar, or patched source output.\n" +
        "  --patch <patch.json>    Semantic patch document required by apply-patch.\n" +
        "  --plan <plan.json>      Ordered semantic patch plan required by apply-plan.\n" +
        "  --declaration-patch <file> Typed declaration patch required by apply-declaration-patch.\n" +
        "  --out-asset <file>      Revalidated Declaration IR output.\n" +
        "  --identity <file.usids> Use a persistent identity sidecar; may repeat for *-files commands.\n" +
        "  --out-identity <file>   Updated sidecar output required by sidecar-enabled apply-patch.\n" +
        "  --method <name>         Fully qualified exported method required by execute.\n" +
        "  --method-id <us_...>    Exact semantic method ID alternative for overloaded methods.\n" +
        "  --receiver <asset-id-or-type>  Select an instance Script Asset receiver for execute.\n" +
        "  --args <json-array>     Execute arguments; defaults to an empty array.\n" +
        "  --layout <file.usgraph> Existing visual layout required by layout-sync.\n" +
        "  --root <directory>      Static visual workbench root required by debug-serve.\n" +
        "  --port <number>         Loopback server port; debug-serve defaults to 4173, frame server to 4174.\n" +
        "  --max-connections <n>   Remote debug frame connection cap; defaults to 32.\n" +
        "  --source <input.cs>     Add a debug compilation source; may repeat.\n" +
        "  --asset-out <path>      Transactional Declaration IR output for debug-serve.\n" +
        "  --surface-out <path>    Transactional Authoring Surface output for debug-serve.\n" +
        "  --managed-out <path>    Transactional managed assembly output for debug-serve.\n" +
        "  --chunk-out <path>      Publish the matching compiled chunk in the authoring transaction.\n" +
        "  --publish-initial       Publish initial authoring artifacts before accepting requests.\n" +
        "  --pdb-out <path>        Transactional portable PDB output for debug-serve.\n" +
        "  --history <path>        Persistent transactional Undo/Redo journal for debug-serve.\n" +
        "  --layout-out <path>     Transactional .usgraph layout synchronized by debug-serve.\n" +
        "  --namespace <name>      Namespace for generated Unreal managed bindings.\n" +
        "  --native-out <path>     Also emit ABI v3 generated fast-binding C++.\n" +
        "  --native-header <path>  Header included by generated native fast bindings.\n" +
        "  --native-namespace <name> C++ namespace for generated native fast bindings.\n" +
        "  --contract <path>       Select one product API surface for every generated artifact.\n" +
        "  --nodes-out <path>      Emit the visual API node catalog selected by --contract.\n" +
        "  --report <path>         Emit native/fallback/rejected UE API coverage JSON.\n" +
        "  --fail-on-rejected      Return data-error status when any UE member is rejected.\n" +
        "  --managed-out <path>    Emit schema-generated managed data-plane layouts.\n" +
        "  --coverage <path>       Validate every Puerts inventory item against an explicit migration route.\n" +
        "  --native-project <path> Scan remaining native facade functions in the migration target.\n" +
        "  --project-name <name>   Stable inventory name when source and target projects differ.\n" +
        "  --fail-on-uncovered     Return data-error status for drift, missing, or stale migration routes.\n" +
        "  --requirements <file>   Visual/C# round-trip methods and execution cases.\n" +
        "  --visual-identity <file> Persistent IDs for the Visual-authored source.\n" +
        "  --code-identity <file>  Reconciled persistent IDs for the C#-edited source.\n" +
        "  --assembly <name>        Set the analysis assembly name.\n" +
        "  --effects <catalog.json> Apply effect catalog overrides.\n" +
        "  --reference <assembly>   Add a metadata reference; may be repeated.\n" +
        "  --pdb <file>             Add an optional portable-PDB debug section.\n" +
        "  --source-map <file>      Add an optional source-map debug section.\n" +
        "  --authoring-surface <file> Add an optional editor-only authoring section.\n" +
        "  --trials <1-50>        Chunk load profiling trials; defaults to 3.\n" +
        "  --no-jit-prepare       Skip RuntimeHelpers entry-method JIT preparation.\n" +
        "  --policy <file>          Override the bundled compatibility policy.\n" +
        "  --fail-on-blocked       Return data-error status for unreadable known versions.\n" +
        "  --configuration <name>   Select the MSBuild configuration; defaults to Debug.\n" +
        "  --framework <tfm>        Select one framework from a multi-target .csproj.\n" +
        "  --no-restore             Do not restore the .csproj before loading.\n" +
        "  --no-build               Do not build the .csproj before loading.\n" +
        "Analyze writes graph JSON to stdout when --out is omitted; emit and apply commands require --out.");

    private sealed record AnalyzeOptions(
        IReadOnlyList<string> InputPaths,
        string? OutputDirectory,
        string? EffectCatalogPath,
        string? AssemblyName,
        IReadOnlyList<string> ReferencePaths,
        IReadOnlyList<string> IdentityPaths,
        string Configuration,
        string? TargetFramework,
        bool Restore,
        bool Build);

    private sealed record ExecuteOptions(
        string InputPath,
        string? QualifiedMethodName,
        SemanticId? MethodId,
        string ArgumentsJson,
        string? EffectCatalogPath,
        string AssemblyName,
        IReadOnlyList<string> ReferencePaths,
        string? IdentityPath,
        string? Receiver);

    private sealed record DebugServeOptions(
        string InputPath,
        string ContentRoot,
        int Port,
        string? EffectCatalogPath,
        string AssemblyName,
        IReadOnlyList<string> ReferencePaths,
        string? IdentityPath,
        IReadOnlyList<string> AdditionalSourcePaths,
        string? DeclarationOutputPath,
        string? SurfaceOutputPath,
        string? ManagedAssemblyOutputPath,
        string? PortablePdbOutputPath,
        string? HistoryPath,
        string? GraphLayoutPath,
        string? ApiBrowserPath,
        string? ProjectPath,
        string Configuration,
        string? TargetFramework,
        bool Restore,
        bool Build,
        string? CompiledChunkOutputPath,
        bool PublishInitial);

    private sealed record DebugFrameServeOptions(
        string InputPath,
        int Port,
        int MaximumConnections,
        string? EffectCatalogPath,
        string AssemblyName,
        IReadOnlyList<string> ReferencePaths,
        string? IdentityPath,
        IReadOnlyList<string> AdditionalSourcePaths);

    private sealed record DebugFrameServerReady(
        string SchemaVersion,
        string Endpoint,
        CoreClrProgramGeneration Generation,
        ushort ProtocolVersion,
        int HeaderBytes,
        int MaximumPayloadBytes,
        int MaximumConnections)
    {
        public const string CurrentSchemaVersion =
            "unifiedscript.debug-frame-server/v0.1";
    }

    private sealed record UnrealProfileDiagnosisOptions(
        string BaselinePath,
        string CurrentPath,
        string EvidencePath,
        string OutputPath);

    private sealed record UnrealWorkloadComparisonOptions(
        string ManifestPath,
        string FinareResultPath,
        string PuertsResultPath,
        string OutputPath);

    private sealed record UnrealWorkloadBudgetOptions(
        string ManifestPath,
        string ComparisonPath,
        string BudgetPath,
        string OutputPath);

    private sealed record UnrealUsabilityBudgetOptions(
        string PackagedFirstPlayPath,
        string DailyReloadPath,
        string AutomationEvidencePath,
        string BudgetPath,
        string OutputPath,
        bool FailOnRegression);

    private sealed record UnrealWorkloadAssemblyOptions(
        string ManifestPath,
        string CaptureDirectory,
        string EngineId,
        string OutputPath);

    private sealed record UnrealWorkloadCaptureOptions(
        string ManifestPath,
        string MeasurementPath,
        string OutputPath);

    private sealed record UnrealBindingOptions(
        string ManifestPath,
        string OutputPath,
        string Namespace,
        string? NativeOutputPath,
        string NativeHeader,
        string NativeNamespace,
        string? ReportPath,
        string? ContractPath,
        string? NodeCatalogOutputPath,
        string? BrowserOutputPath,
        bool FailOnRejected);

    private sealed record UnrealContractProposalOptions(
        string ReflectionPath,
        string RequestPath,
        string BaseContractPath,
        string OutputPath,
        bool FailOnOpenDecisions);

    private sealed record UnrealDevelopmentSurfaceOptions(
        string ReflectionPath,
        IReadOnlyList<string> ProjectPaths,
        string BaseContractPath,
        string RequestOutputPath,
        string OverlayOutputPath,
        string ReportOutputPath,
        string? LockOutputPath,
        string? VerifyLockPath,
        string Configuration,
        string? DotNetPath,
        bool FailOnOpenDecisions);

    private sealed record UnrealReflectionHintOptions(
        IReadOnlyList<string> ProjectPaths,
        string BaseContractPath,
        string OutputPath,
        string Configuration,
        string? DotNetPath);

    private sealed record UnrealDataPlaneOptions(
        string SchemaPath,
        string ManagedOutputPath,
        string NativeOutputPath);

    private sealed record UnrealDataPlaneExtensionOptions(
        string SchemaPath,
        string ManagedOutputPath,
        string NativeOutputPath,
        string CatalogOutputPath);

    private sealed record ChunkPackOptions(
        string OutputPath,
        string RuntimeManifestPath,
        string AssemblyPath,
        string DeclarationPath,
        string BindingManifestPath,
        string DependencyManifestPath,
        string? PortablePdbPath,
        string? SourceMapPath,
        string? AuthoringSurfacePath);

    private sealed record ChunkProfileOptions(
        string InputPath,
        string OutputPath,
        int TrialCount,
        bool PrepareEntryMethods,
        IReadOnlyList<string> ReferencePaths);

    private sealed record ChunkFirstTickProfileOptions(
        string InputPath,
        string OutputPath,
        string TypeName,
        int FrameGapMilliseconds,
        bool DisablePrewarm,
        IReadOnlyList<string> ReferencePaths);

    private sealed record UpgradeOptions(
        string InputPath,
        string OutputPath,
        string? PolicyPath,
        string? ReportPath,
        bool FailOnBlocked);

    private sealed record DiagnosticsBundleOptions(
        string InputDirectory,
        string OutputPath,
        string? PolicyPath);

    private sealed record UnrealProjectAdoptionCliOptions(
        string ProjectPath,
        string? SdkRoot,
        string TemplateId,
        string ScriptName,
        string? ManagedNamespace,
        string? ReportPath,
        bool Preview,
        bool Rollback,
        string? TransactionId);

    private sealed record UnrealProjectDoctorCliOptions(
        string ProjectPath,
        string? EngineRoot,
        string? ReportPath);

    private sealed record ScriptClassReferencePlanOptions(
        string ProjectPath,
        string ManagedTypeName,
        string? ReplacementTypeName,
        string OutputPath,
        string Configuration,
        string? TargetFramework,
        bool Restore,
        bool Build,
        string? DotNetPath);

    private sealed record UnrealPuertsInventoryOptions(
        string ProjectRoot,
        string NativeProjectRoot,
        string? ProjectName,
        string OutputPath,
        string? CoveragePath,
        string? ReportPath,
        bool FailOnUncovered);

    private sealed record UnrealPuertsScanOptions(
        string ProjectRoot,
        string NativeProjectRoot,
        string? ProjectName,
        string OutputPath,
        IReadOnlyList<string> ScriptRoots,
        IReadOnlyList<string> NativeRoots,
        bool FailOnBlockers);

    private sealed record UnrealPuertsPlanOptions(
        string ScanPath,
        string OutputDirectory,
        string? ContractPath,
        string? ManagedNamespace,
        bool FailOnBlockers);

    private sealed record UnrealPuertsVerifyOptions(
        string HarnessPath,
        string PuertsEvidencePath,
        string FinareEvidencePath,
        string OutputPath,
        bool FailOnMismatch);

    private sealed record UnrealPuertsCoexistOptions(
        string PlanPath,
        string OutputDirectory,
        IReadOnlyDictionary<string, string> UnifiedManagedTypes,
        bool FailOnBlockers);

    private sealed record IdentityOptions(
        string InputPath,
        string OutputPath,
        string? ProjectPath,
        string? EffectCatalogPath,
        string? AssemblyName,
        IReadOnlyList<string> ReferencePaths,
        string Configuration,
        string? TargetFramework,
        string? DotNetPath,
        bool Restore,
        bool Build);

    private sealed record RoundTripOptions(
        string VisualSourcePath,
        string CodeSourcePath,
        string RequirementPath,
        string ReportPath,
        string? VisualIdentityPath,
        string? CodeIdentityPath,
        string AssemblyName,
        IReadOnlyList<string> ReferencePaths);

    private sealed record ProjectPatchOptions(
        string ProjectPath,
        string EditDocumentPath,
        string OutputDirectory,
        string? EffectCatalogPath,
        string? AssemblyName,
        IReadOnlyList<string> ReferencePaths,
        IReadOnlyList<string> IdentityPaths,
        string Configuration,
        string? TargetFramework,
        bool Restore,
        bool Build);

    private sealed record LayoutOptions(
        string GraphPath,
        string? LayoutPath,
        string OutputPath);

    private sealed record SemanticDiffOptions(
        string BaselinePath,
        string TargetPath,
        string OutputPath,
        bool FailOnChange);

    private sealed record SemanticMergeOptions(
        string BaselinePath,
        string OursPath,
        string TheirsPath,
        string ReportPath,
        string OutputPath);

    private sealed record AiProposalPackOptions(
        string PlanPath,
        string Title,
        string Intent,
        string Producer,
        string ProducerVersion,
        string? Model,
        string OutputPath);

    private sealed record PatchOptions(
        string InputPath,
        string EditDocumentPath,
        string OutputPath,
        string? EffectCatalogPath,
        string AssemblyName,
        IReadOnlyList<string> ReferencePaths,
        string? IdentityPath,
        string? IdentityOutputPath);

    private sealed record DeclarationPatchOptions(
        string InputPath,
        string PatchPath,
        string OutputPath,
        string AssetOutputPath,
        string? EffectCatalogPath,
        string AssemblyName,
        IReadOnlyList<string> ReferencePaths,
        string? IdentityPath,
        string? IdentityOutputPath);

    private sealed record OutputFile(string Path, byte[] Content)
    {
        public OutputFile(string path, string content)
            : this(path, System.Text.Encoding.UTF8.GetBytes(content))
        {
        }
    }

    private sealed record PendingOutput(string Path, string TemporaryPath, string BackupPath);
}
