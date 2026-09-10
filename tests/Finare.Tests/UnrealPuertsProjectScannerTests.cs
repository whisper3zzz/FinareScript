using Finare.Unreal;

namespace Finare.Tests;

public sealed class UnrealPuertsProjectScannerTests
{
    [Fact]
    public void ScansCustomRootsAndClassifiesMigrationRisksDeterministically()
    {
        using var fixture = new PuertsScanFixture();
        fixture.Write(
            "ScanGame.uproject",
            """
            {
              "Plugins": [ { "Name": "Puerts", "Enabled": true } ]
            }
            """);
        fixture.Write(
            "package.json",
            """
            { "dependencies": { "puerts": "^1.0.0" } }
            """);
        fixture.Write(
            "GameCode/Player.mts",
            """
            import * as RuntimeUE from 'ue';
            import * as PuertsRuntime from 'puerts';

            class PlayerScript extends RuntimeUE.Character
            {
                FpsCamera: RuntimeUE.CameraComponent;

                async ReceiveBeginPlay(): Promise<void> {
                    RuntimeUE.KismetSystemLibrary.LineTraceSingle(this);
                    RuntimeUE.StaticMesh.Load('/Game/Test/SM_Test');
                    this.AddMovementInput(this.GetActorForwardVector(), 1, false);
                    this.FpsCamera.bUsePawnControlRotation = true;
                    this.FpsCamera.GetForwardVector();
                    this.OnDamaged.Add(() => {});
                    const reflected = RuntimeUE[typeName];
                    const module = require(moduleName);
                    const proxy = new Proxy(this, {});
                    eval(source);
                    await new Promise(resolve => setTimeout(resolve, 1));
                    PuertsRuntime.makeUClass(PlayerScript);
                }
            }
            """);
        fixture.Write(
            "NativeCode/ScanBridge.hpp",
            """
            UCLASS()
            class SCANGAME_API AScanBridge : public AActor
            {
                GENERATED_BODY()
            public:
                UFUNCTION(BlueprintCallable)
                void LegacyFacade();
            };
            """);

        var options = new UnrealPuertsProjectScanOptions(
            fixture.Root,
            ScriptRoots: ["GameCode"],
            NativeRoots: ["NativeCode"]);
        var scanner = new UnrealPuertsProjectScanner();
        UnrealPuertsProjectScan first = scanner.Scan(options);
        UnrealPuertsProjectScan second = scanner.Scan(options);

        Assert.True(first.Complete);
        Assert.Equal("ScanGame", first.ProjectName);
        Assert.Equal(first.SourceFingerprint, second.SourceFingerprint);
        Assert.Equal(
            UnrealPuertsProjectScanJson.Serialize(first),
            UnrealPuertsProjectScanJson.Serialize(second));
        Assert.Equal(1, first.Summary.ScriptFileCount);
        Assert.Equal(1, first.Summary.NativeHeaderCount);
        Assert.Equal(2, first.Summary.ConfigurationFileCount);
        Assert.True(first.Summary.BlockerCount >= 4);
        AssertFinding(first, "module", "ue");
        AssertFinding(first, "module", "puerts");
        AssertFinding(first, "scriptType", "PlayerScript extends RuntimeUE.Character");
        AssertFinding(first, "lifecycleHook", "PlayerScript.ReceiveBeginPlay");
        AssertFinding(first, "unrealApi", "UE.KismetSystemLibrary.LineTraceSingle");
        AssertFinding(first, "unrealApi", "UE.Character.AddMovementInput");
        AssertFinding(first, "unrealApi", "UE.Character.GetActorForwardVector");
        AssertFinding(first, "unrealApi", "UE.CameraComponent.GetForwardVector");
        AssertFinding(first, "unrealApi", "UE.CameraComponent.bUsePawnControlRotation:set");
        Assert.DoesNotContain(
            first.Findings,
            static finding => finding.Category == "unrealApi" &&
                finding.Key == "UE.Character.OnDamaged");
        AssertFinding(first, "puertsApi", "puerts.makeUClass");
        AssertFinding(first, "assetReference", "/Game/Test/SM_Test");
        AssertFinding(first, "delegatePattern", "this.OnDamaged.Add");
        AssertFinding(first, "asyncPattern", "timer:setTimeout");
        AssertFinding(first, "unsupportedDynamicBehavior", "computed-unreal-member");
        AssertFinding(first, "unsupportedDynamicBehavior", "dynamic-require");
        AssertFinding(first, "unsupportedDynamicBehavior", "proxy");
        AssertFinding(first, "unsupportedDynamicBehavior", "eval");
        AssertFinding(first, "nativeSurface", "AScanBridge.LegacyFacade");
        AssertFinding(first, "configuration", "puerts-reference:ScanGame.uproject");

        UnrealPuertsProjectScan roundTripped = UnrealPuertsProjectScanJson.Deserialize(
            UnrealPuertsProjectScanJson.Serialize(first));
        Assert.Equal(first.SourceFingerprint, roundTripped.SourceFingerprint);
        Assert.Equal(first.Summary, roundTripped.Summary);
    }

    [Fact]
    public void AutoDiscoversTsConfigAndCommonRootsWhileIgnoringDependencies()
    {
        using var fixture = new PuertsScanFixture();
        fixture.Write("tsconfig.json", "{ \"compilerOptions\": {} }");
        fixture.Write(
            "Content/JavaScript/Player.js",
            """
            const GameUE = require('ue');
            class Player extends GameUE.Actor {
                ReceiveTick() { GameUE.GameplayStatics.GetGameTimeInSeconds(this); }
            }
            """);
        fixture.Write(
            "Content/JavaScript/Types.d.ts",
            "class IgnoredDeclaration extends UE.Actor {}");
        fixture.Write(
            "node_modules/dependency/index.js",
            "class IgnoredDependency extends UE.Actor {}");

        UnrealPuertsProjectScan scan = new UnrealPuertsProjectScanner().Scan(
            new UnrealPuertsProjectScanOptions(fixture.Root));

        Assert.True(scan.Complete);
        Assert.Equal(["Content/JavaScript"], scan.ScriptRoots);
        Assert.Equal(1, scan.Summary.ScriptFileCount);
        AssertFinding(scan, "scriptType", "Player extends GameUE.Actor");
        AssertFinding(scan, "lifecycleHook", "Player.ReceiveTick");
        AssertFinding(scan, "unrealApi", "UE.GameplayStatics.GetGameTimeInSeconds");
        Assert.DoesNotContain(
            scan.Findings.SelectMany(static finding => finding.Locations),
            static location => location.SourcePath.Contains("node_modules", StringComparison.Ordinal));
    }

    [Fact]
    public void AutoDiscoveryPrefersAuthoredSourcesOverDeployedJavaScript()
    {
        using var fixture = new PuertsScanFixture();
        fixture.Write("tsconfig.json", "{ \"compilerOptions\": { \"outDir\": \"Content/JavaScript\" } }");
        fixture.Write(
            "TypeScript/Player.ts",
            "class AuthoredPlayer extends UE.Actor { ReceiveBeginPlay() {} }");
        fixture.Write(
            "Content/JavaScript/Player.js",
            "class DeployedPlayer extends UE.Actor { ReceiveBeginPlay() {} }");

        UnrealPuertsProjectScan scan = new UnrealPuertsProjectScanner().Scan(
            new UnrealPuertsProjectScanOptions(fixture.Root));

        Assert.True(scan.Complete);
        Assert.Equal(["TypeScript"], scan.ScriptRoots);
        Assert.Equal(1, scan.Summary.ScriptFileCount);
        AssertFinding(scan, "scriptType", "AuthoredPlayer extends UE.Actor");
        Assert.DoesNotContain(
            scan.Findings,
            static finding => finding.Key.Contains("DeployedPlayer", StringComparison.Ordinal));
    }

    [Fact]
    public void ReportsIncompleteBoundedScanInsteadOfSilentlyDroppingSources()
    {
        using var fixture = new PuertsScanFixture();
        fixture.Write(
            "Scripts/Large.ts",
            "class Large extends UE.Actor { ReceiveBeginPlay() {} }");

        UnrealPuertsProjectScan scan = new UnrealPuertsProjectScanner().Scan(
            new UnrealPuertsProjectScanOptions(
                fixture.Root,
                MaximumFileBytes: 8));

        Assert.False(scan.Complete);
        Assert.Equal(0, scan.Summary.ScriptFileCount);
        Assert.True(scan.Summary.BlockerCount > 0);
        Assert.Contains(
            scan.Findings,
            static finding => finding.Category == "scanLimit" &&
                finding.Key.StartsWith("file-bytes:", StringComparison.Ordinal));
        Assert.Contains(
            scan.Diagnostics,
            static diagnostic => diagnostic.Contains("per-file limit", StringComparison.Ordinal));
    }

    [Fact]
    public void CliWritesScanAndCanFailAutomationOnBlockers()
    {
        using var fixture = new PuertsScanFixture();
        fixture.Write(
            "Runtime/Entry.ts",
            """
            // eval(commentedSource);
            const diagnostic = "eval(stringSource)";
            const multiline = `diagnostic
            eval(templateSource)`;
            class Entry extends UE.Actor { ReceiveBeginPlay() {} }
            """);
        string outputPath = Path.Combine(fixture.Root, "scan.json");

        int success = Finare.Cli.Program.Main(
        [
            "ue-puerts-scan",
            fixture.Root,
            "--script-root",
            "Runtime",
            "--out",
            outputPath,
            "--fail-on-blockers",
        ]);

        Assert.Equal(0, success);
        Assert.True(File.Exists(outputPath));
        UnrealPuertsProjectScan clean = UnrealPuertsProjectScanJson.Deserialize(
            File.ReadAllText(outputPath));
        Assert.Equal(0, clean.Summary.BlockerCount);

        fixture.Write(
            "Runtime/Entry.ts",
            "class Entry extends UE.Actor { ReceiveBeginPlay() { eval(source); } }");
        int blocked = Finare.Cli.Program.Main(
        [
            "ue-puerts-scan",
            fixture.Root,
            "--script-root",
            "Runtime",
            "--out",
            outputPath,
            "--fail-on-blockers",
        ]);

        Assert.Equal(65, blocked);
        UnrealPuertsProjectScan risky = UnrealPuertsProjectScanJson.Deserialize(
            File.ReadAllText(outputPath));
        Assert.Equal(1, risky.Summary.BlockerCount);
    }

    private static void AssertFinding(
        UnrealPuertsProjectScan scan,
        string category,
        string key) =>
        Assert.Contains(
            scan.Findings,
            finding => finding.Category == category && finding.Key == key);

    private sealed class PuertsScanFixture : IDisposable
    {
        public PuertsScanFixture()
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                "FinarePuertsScan-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public void Write(string relativePath, string text)
        {
            string path = Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
