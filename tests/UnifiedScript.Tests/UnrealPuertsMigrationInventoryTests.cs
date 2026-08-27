using UnifiedScript.Unreal;

namespace UnifiedScript.Tests;

public sealed class UnrealPuertsMigrationInventoryTests
{
    [Fact]
    public void BuilderInventoriesPuertsGameplayAndNativeFacadeDeterministically()
    {
        using var fixture = new PuertsProjectFixture();
        fixture.Write(
            "TypeScript/TS_Player.ts",
            """
            import * as UE from 'ue'
            import {$ref, $unref} from 'puerts'

            function delay(t: number) {
                return new Promise(resolve => setTimeout(resolve, t));
            }

            class TS_Player extends UE.Character {
                FpsCamera: UE.CameraComponent;

                Constructor() {}

                ReceiveBeginPlay(): void {
                    this.FpsCamera.bUsePawnControlRotation = true;
                    const hit = $ref<UE.HitResult>(undefined);
                    UE.KismetSystemLibrary.LineTraceSingle(this, new UE.Vector(0, 0, 0));
                    UE.StaticMesh.Load('/Game/Test/SM_Test');
                }

                async Shoot(): Promise<void> {
                    this.AddMovementInput(this.GetActorForwardVector(), 1, false);
                    const end = this.FpsCamera.GetForwardVector().op_Multiply(100);
                    await delay(0.1);
                }
            }

            declare module "ue" { interface Object { Added(): void } }
            UE.Object.prototype.Added = function Added() {};
            """);
        fixture.Write(
            "Source/Test/TestCharacter.h",
            """
            UCLASS()
            class TEST_API ATestCharacter final : public AActor
            {
                GENERATED_BODY()
            public:
                UFUNCTION(BlueprintCallable)
                void ManualGameplayFacade();
            };
            """);

        var builder = new UnrealPuertsMigrationInventoryBuilder();
        UnrealPuertsMigrationInventory first = builder.Build(fixture.Root);
        UnrealPuertsMigrationInventory second = builder.Build(fixture.Root);

        Assert.Equal(first.SourceFingerprint, second.SourceFingerprint);
        Assert.Equal(
            UnrealPuertsMigrationJson.Serialize(first),
            UnrealPuertsMigrationJson.Serialize(second));
        AssertItem(first, "script", "TypeScript/TS_Player.ts");
        AssertItem(first, "scriptType", "TS_Player extends UE.Character");
        AssertItem(first, "scriptMethod", "TS_Player.Shoot");
        AssertItem(first, "lifecycleHook", "TS_Player.ReceiveBeginPlay");
        AssertItem(first, "import", "puerts");
        AssertItem(first, "puertsApi", "$ref");
        AssertItem(first, "unrealApi", "UE.KismetSystemLibrary.LineTraceSingle");
        AssertItem(first, "unrealApi", "UE.CameraComponent.bUsePawnControlRotation:set");
        AssertItem(first, "unrealApi", "UE.Vector.op_Multiply");
        AssertItem(first, "resource", "/Game/Test/SM_Test");
        AssertItem(first, "globalPattern", "function:delay");
        AssertItem(first, "globalPattern", "module-augmentation:ue");
        AssertItem(
            first,
            "globalPattern",
            "prototype-mutation:UE.Object.prototype.Added");
        AssertItem(first, "asyncPattern", "async:TS_Player.Shoot");
        AssertItem(first, "asyncPattern", "timer:setTimeout");
        AssertItem(first, "nativeFacade", "ATestCharacter.ManualGameplayFacade");
        Assert.Equal(1, first.Summary.NativeFacadeCount);
    }

    [Fact]
    public void CoverageRequiresExactFingerprintAndEveryInventoryItem()
    {
        using var fixture = new PuertsProjectFixture();
        fixture.Write(
            "TypeScript/TS_Gun.ts",
            "class TS_Gun extends UE.Actor { ReceiveBeginPlay(): void {} }");
        UnrealPuertsMigrationInventory inventory =
            new UnrealPuertsMigrationInventoryBuilder().Build(fixture.Root);
        UnrealPuertsMigrationCoverageEntry[] entries = inventory.Items
            .Select(item => new UnrealPuertsMigrationCoverageEntry(
                item.Kind,
                item.Key,
                "generated-binding",
                "Managed/TS_Gun.cs"))
            .ToArray();
        var exact = new UnrealPuertsMigrationCoverage(
            "unifiedscript.puerts-migration-coverage/v0.1",
            inventory.ProjectName,
            inventory.SourceFingerprint,
            entries);

        UnrealPuertsMigrationCoverageReport passed =
            UnrealPuertsMigrationCoverageValidator.Validate(inventory, exact);
        Assert.True(passed.Passed);
        Assert.Equal(inventory.Items.Count, passed.CoveredItemCount);

        UnrealPuertsMigrationCoverageReport uncovered =
            UnrealPuertsMigrationCoverageValidator.Validate(
                inventory,
                exact with { Entries = entries.Skip(1).ToArray() });
        Assert.False(uncovered.Passed);
        Assert.Equal(1, uncovered.UncoveredItemCount);

        UnrealPuertsMigrationCoverageReport drifted =
            UnrealPuertsMigrationCoverageValidator.Validate(
                inventory,
                exact with { InventoryFingerprint = new string('0', 64) });
        Assert.False(drifted.Passed);
        Assert.False(drifted.FingerprintMatches);
    }

    [Fact]
    public void CoverageJsonRejectsUnknownProperties()
    {
        const string json =
            """
            {
              "schemaVersion": "unifiedscript.puerts-migration-coverage/v0.1",
              "projectName": "Example",
              "inventoryFingerprint": "abc",
              "entries": [],
              "unexpected": true
            }
            """;

        Assert.Throws<System.Text.Json.JsonException>(
            () => UnrealPuertsMigrationJson.DeserializeCoverage(json));
    }

    private static void AssertItem(
        UnrealPuertsMigrationInventory inventory,
        string kind,
        string key) =>
        Assert.Contains(inventory.Items, item => item.Kind == kind && item.Key == key);

    private sealed class PuertsProjectFixture : IDisposable
    {
        public PuertsProjectFixture()
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                "UnifiedScriptPuertsInventory-" + Guid.NewGuid().ToString("N"));
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
