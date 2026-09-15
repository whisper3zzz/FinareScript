#if WITH_DEV_AUTOMATION_TESTS

#include "FinareActor.h"
#include "FinareAsset.h"
#include "FinareBlueprint.h"
#include "FinareBlueprintClassMaterializer.h"
#include "FinareComponent.h"
#include "Dom/JsonObject.h"
#include "Engine/SCS_Node.h"
#include "Engine/SimpleConstructionScript.h"
#include "HAL/FileManager.h"
#include "Kismet2/KismetEditorUtilities.h"
#include "Misc/AutomationTest.h"
#include "Misc/FileHelper.h"
#include "Misc/PackageName.h"
#include "Misc/Paths.h"
#include "Misc/ScopeExit.h"
#include "PackageTools.h"
#include "Serialization/JsonReader.h"
#include "Serialization/JsonSerializer.h"
#include "UObject/Package.h"
#include "UObject/SavePackage.h"

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FFinareCompositionPublicationTest,
	"Finare.Unreal.ScriptClass.Composition.PublicationRecovery",
	EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FFinareCompositionPublicationTest::RunTest(const FString& Parameters)
{
	const FString Root = FPaths::ConvertRelativePathToFull(FPaths::Combine(
		FPaths::ProjectDir(), TEXT("../artifacts/class-composition")));
	const auto ReadJson = [](const FString& Path, TSharedPtr<FJsonObject>& Json)
	{
		FString Text;
		return FFileHelper::LoadFileToString(Text, *Path) &&
			FJsonSerializer::Deserialize(TJsonReaderFactory<>::Create(Text), Json) && Json.IsValid();
	};
	const auto Serialize = [](const TSharedPtr<FJsonObject>& Json)
	{
		FString Text;
		FJsonSerializer::Serialize(Json.ToSharedRef(), TJsonWriterFactory<>::Create(&Text));
		return Text;
	};
	TSharedPtr<FJsonObject> Config;
	TSharedPtr<FJsonObject> Declaration;
	if (!TestTrue(TEXT("Publication test reads the emitted C# fixture"),
		ReadJson(FPaths::Combine(Root, TEXT("fixture.json")), Config) &&
		ReadJson(FPaths::Combine(Root, TEXT("v1/declaration/ClassCompositionFixture.asset.json")), Declaration)))
	{
		return false;
	}
	const FString ClassPath = Config->GetStringField(TEXT("actorClassPath"));
	const FString BlueprintPackageName = FPackageName::ObjectPathToPackageName(ClassPath);
	const FString ScriptPackageName = Config->GetStringField(TEXT("packageDirectory")) + TEXT("/PublicationActorAsset");
	const FString ChildPackageName = Config->GetStringField(TEXT("packageDirectory")) + TEXT("/PublicationChild");
	const FString OnboardingPath = FPaths::Combine(FPaths::ProjectConfigDir(), TEXT("Finare.onboarding.json"));
	const FString ManifestPath = FPaths::Combine(Root, TEXT("publication-deployment.json"));
	const FString BlueprintFilename = FPackageName::LongPackageNameToFilename(BlueprintPackageName, FPackageName::GetAssetPackageExtension());
	const FString ScriptFilename = FPackageName::LongPackageNameToFilename(ScriptPackageName, FPackageName::GetAssetPackageExtension());
	TMap<FString, TArray<uint8>> Preimages;
	TSet<FString> AbsentFiles;
	for (const FString& Path : {OnboardingPath, ManifestPath, BlueprintFilename, ScriptFilename})
	{
		if (IFileManager::Get().FileExists(*Path))
		{
			if (!FFileHelper::LoadFileToArray(Preimages.Add(Path), *Path))
			{
				AddError(TEXT("Cannot capture publication test input: ") + Path);
				return false;
			}
		}
		else
		{
			AbsentFiles.Add(Path);
		}
	}
	TArray<UPackage*> Packages;
	ON_SCOPE_EXIT
	{
		for (UPackage* Package : Packages)
		{
			Package->SetDirtyFlag(false);
		}
		UPackageTools::UnloadPackages(Packages);
		for (const auto& Pair : Preimages)
		{
			TestTrue(TEXT("Restores publication test input"), FFileHelper::SaveArrayToFile(Pair.Value, *Pair.Key));
		}
		for (const FString& Path : AbsentFiles)
		{
			IFileManager::Get().Delete(*Path, false, true);
		}
	};
	UPackage* BlueprintPackage = CreatePackage(*BlueprintPackageName);
	UPackage* ScriptPackage = CreatePackage(*ScriptPackageName);
	UPackage* ChildPackage = CreatePackage(*ChildPackageName);
	Packages = {ChildPackage, BlueprintPackage, ScriptPackage};
	UFinareBlueprint* Blueprint = Cast<UFinareBlueprint>(FKismetEditorUtilities::CreateBlueprint(
		AFinareActor::StaticClass(), BlueprintPackage, TEXT("CompositionActor"), BPTYPE_Normal,
		UFinareBlueprint::StaticClass(), UFinareBlueprintGeneratedClass::StaticClass(), TEXT("CompositionPublication")));
	UFinareAsset* Asset = NewObject<UFinareAsset>(ScriptPackage, TEXT("PublicationActorAsset"),
		RF_Public | RF_Standalone | RF_Transactional);
	if (!TestNotNull(TEXT("Creates publication Blueprint"), Blueprint) || !TestNotNull(TEXT("Creates publication Script Asset"), Asset))
	{
		return false;
	}
	TSharedPtr<FJsonObject> ActorDeclaration;
	for (const TSharedPtr<FJsonValue>& Value : Declaration->GetArrayField(TEXT("assets")))
	{
		if (Value->AsObject()->GetStringField(TEXT("qualifiedName")).EndsWith(TEXT(".CompositionActor")))
		{
			ActorDeclaration = Value->AsObject();
		}
	}
	if (!TestTrue(TEXT("Finds the actual emitted Actor declaration"), ActorDeclaration.IsValid()))
	{
		return false;
	}
	FString ClassId = ActorDeclaration->GetStringField(TEXT("id"));
	ClassId.RemoveFromStart(TEXT("us_"));
	FGuid::ParseExact(ClassId, EGuidFormats::Digits, Asset->ScriptClassId);
	Asset->ScriptClassKind = EFinareClassKind::Actor;
	Asset->GeneratedClassPath = FSoftClassPath(ClassPath);
	Asset->ScriptTypeName = ActorDeclaration->GetStringField(TEXT("qualifiedName"));
	Asset->DeclarationAssetPath = ActorDeclaration->GetStringField(TEXT("assetPath"));
	Asset->RequiredTargetClass = AFinareActor::StaticClass();
	Blueprint->ScriptAsset = Asset;
	CastChecked<AFinareActor>(Blueprint->GeneratedClass->GetDefaultObject())->ScriptComponent->ScriptAsset = Asset;
	UBlueprint* Child = FKismetEditorUtilities::CreateBlueprint(Blueprint->GeneratedClass,
		ChildPackage, TEXT("PublicationChild"), BPTYPE_Normal, UBlueprint::StaticClass(),
		UBlueprintGeneratedClass::StaticClass(), TEXT("CompositionPublication"));
	if (!TestNotNull(TEXT("Creates an existing child with its own carrier defaults"), Child))
	{
		return false;
	}
	CastChecked<AFinareActor>(Child->GeneratedClass->GetDefaultObject())->ScriptComponent->bTickAutomatically = false;
	const auto Save = [](UPackage* Package, UObject* Object, const FString& Filename)
	{
		IFileManager::Get().MakeDirectory(*FPaths::GetPath(Filename), true);
		FSavePackageArgs Args;
		Args.TopLevelFlags = RF_Public | RF_Standalone;
		Args.SaveFlags = SAVE_NoError;
		return UPackage::SavePackage(Package, Object, *Filename, Args);
	};
	if (!TestTrue(TEXT("Saves the original separate Blueprint and Script Asset packages"),
		Save(BlueprintPackage, Blueprint, BlueprintFilename) && Save(ScriptPackage, Asset, ScriptFilename)))
	{
		return false;
	}
	TSharedPtr<FJsonObject> Entry = MakeShared<FJsonObject>();
	Entry->SetStringField(TEXT("assetPath"), ScriptPackageName);
	Entry->SetStringField(TEXT("typeName"), Asset->ScriptTypeName);
	Entry->SetStringField(TEXT("declarationAssetPath"), Asset->DeclarationAssetPath);
	Entry->SetStringField(TEXT("requiredTargetClass"), AFinareActor::StaticClass()->GetPathName());
	TSharedPtr<FJsonObject> Manifest = MakeShared<FJsonObject>();
	Manifest->SetStringField(TEXT("schemaVersion"), TEXT("unifiedscript.unreal-script-deployment/v0.1"));
	Manifest->SetArrayField(TEXT("scripts"), {MakeShared<FJsonValueObject>(Entry)});
	TSharedPtr<FJsonObject> Onboarding = MakeShared<FJsonObject>();
	TSharedPtr<FJsonObject> Deployment = MakeShared<FJsonObject>();
	Deployment->SetStringField(TEXT("manifest"), ManifestPath);
	Onboarding->SetObjectField(TEXT("deployment"), Deployment);
	if (!TestTrue(TEXT("Sets up a scoped deployment manifest"),
		FFileHelper::SaveStringToFile(Serialize(Manifest), *ManifestPath) &&
		FFileHelper::SaveStringToFile(Serialize(Onboarding), *OnboardingPath)))
	{
		return false;
	}
	TArray<uint8> BaselineBlueprint, BaselineAsset, BaselineManifest;
	FFileHelper::LoadFileToArray(BaselineBlueprint, *BlueprintFilename);
	FFileHelper::LoadFileToArray(BaselineAsset, *ScriptFilename);
	FFileHelper::LoadFileToArray(BaselineManifest, *ManifestPath);
	const auto Matches = [](const FString& Path, const TArray<uint8>& Expected)
	{
		TArray<uint8> Actual;
		return FFileHelper::LoadFileToArray(Actual, *Path) && Actual == Expected;
	};
	Manifest->SetStringField(TEXT("editorNote"), TEXT("retain this prepared change"));
	FFinareBlueprintClassMaterializerOptions Options;
	FFinareExternalPublicationFile& Candidate = Options.ExternalPublicationFiles.AddDefaulted_GetRef();
	Candidate.Path = ManifestPath;
	const FString CandidateJson = Serialize(Manifest);
	FTCHARToUTF8 CandidateUtf8(*CandidateJson);
	Candidate.Content.Append(reinterpret_cast<const uint8*>(CandidateUtf8.Get()), CandidateUtf8.Length());
	FFinareBlueprintClassSyncResult Result;
	FString Error;
	const FString DeclarationJson = Serialize(Declaration);
	TestTrue(TEXT("Validate-only accepts the direct C# publication"),
		FFinareBlueprintClassMaterializer::SynchronizeJson(*Asset, *Blueprint, DeclarationJson, true, Result, Error, Options));
	TestTrue(TEXT("Validate-only does not mutate parent, target or deployment"),
		Blueprint->ParentClass == AFinareActor::StaticClass() && Asset->RequiredTargetClass.Get() == AFinareActor::StaticClass() &&
		Matches(ManifestPath, BaselineManifest) && Matches(BlueprintFilename, BaselineBlueprint) && Matches(ScriptFilename, BaselineAsset));
	Options.FailAfterExternalPublicationWrite = 1;
	TestFalse(TEXT("Failure after publication triggers the shared journal rollback"),
		FFinareBlueprintClassMaterializer::SynchronizeJson(*Asset, *Blueprint, DeclarationJson, false, Result, Error, Options));
	TestTrue(TEXT("Failure is the requested publication injection"), Error.Contains(TEXT("Injected external publication failure")));
	TestTrue(TEXT("Rollback restores in-memory target and all file preimages"),
		Blueprint->ParentClass == AFinareActor::StaticClass() && Asset->RequiredTargetClass.Get() == AFinareActor::StaticClass() &&
		Matches(ManifestPath, BaselineManifest) && Matches(BlueprintFilename, BaselineBlueprint) && Matches(ScriptFilename, BaselineAsset));
	TestFalse(TEXT("Rollback preserves the Script Asset clean package flag"), ScriptPackage->IsDirty());
	Options.FailAfterExternalPublicationWrite = INDEX_NONE;
	if (!TestTrue(TEXT("Direct C# publication succeeds through the common entry point"),
		FFinareBlueprintClassMaterializer::SynchronizeJson(*Asset, *Blueprint, DeclarationJson, false, Result, Error, Options)))
	{
		AddError(Error);
		return false;
	}
	TSharedPtr<FJsonObject> Published;
	TestTrue(TEXT("Publication retains prepared edits and updates the target family"),
		ReadJson(ManifestPath, Published) && Published->GetStringField(TEXT("editorNote")) == TEXT("retain this prepared change") &&
		Published->GetArrayField(TEXT("scripts"))[0]->AsObject()->GetStringField(TEXT("requiredTargetClass")) == AActor::StaticClass()->GetPathName() &&
		Asset->RequiredTargetClass.Get() == AActor::StaticClass());
	USCS_Node* Node = Blueprint->SimpleConstructionScript->FindSCSNode(TEXT("UnifiedScript"));
	UFinareComponent* ParentCarrier = Node != nullptr ? Cast<UFinareComponent>(Node->ComponentTemplate) : nullptr;
	UFinareComponent* ChildCarrier = Node != nullptr ? Cast<UFinareComponent>(Node->GetActualComponentTemplate(CastChecked<UBlueprintGeneratedClass>(Child->GeneratedClass))) : nullptr;
	TestTrue(TEXT("Child overrides belong to the child and leave parent defaults intact"),
		ParentCarrier != nullptr && ChildCarrier != nullptr && ParentCarrier != ChildCarrier &&
		ParentCarrier->bTickAutomatically && !ChildCarrier->bTickAutomatically && ChildCarrier->GetOuter() == Child->GeneratedClass);
	// Simulate an older deployment applying its old carrier restriction after
	// the physical parent has already changed. Same-parent sync must repair it.
	Asset->RequiredTargetClass = AFinareActor::StaticClass();
	TestTrue(TEXT("Same-parent synchronization repairs a stale deployment target"),
		FFinareBlueprintClassMaterializer::SynchronizeJson(*Asset, *Blueprint, DeclarationJson, false, Result, Error) &&
		Asset->RequiredTargetClass.Get() == AActor::StaticClass());
	return !HasAnyErrors();
}

#endif
