#if WITH_DEV_AUTOMATION_TESTS

#include "FinareBlueprintClassMaterializer.h"

#include "Dom/JsonObject.h"
#include "Engine/Blueprint.h"
#include "Engine/SCS_Node.h"
#include "Engine/SimpleConstructionScript.h"
#include "GameFramework/Actor.h"
#include "Kismet2/KismetEditorUtilities.h"
#include "Misc/AutomationTest.h"
#include "Misc/FileHelper.h"
#include "Misc/Guid.h"
#include "Misc/PackageName.h"
#include "Misc/Paths.h"
#include "Serialization/JsonReader.h"
#include "Serialization/JsonSerializer.h"
#include "UObject/SavePackage.h"
#include "PackageTools.h"
#include "FinareActor.h"
#include "FinareAsset.h"
#include "FinareBlueprint.h"
#include "FinareComponent.h"
#include "FinareEditorModule.h"
#include "FinareSourceWatcher.h"
#include "FinareWorldSubsystem.h"
#include "Widgets/SFinareAuthoringSurface.h"
#include "Engine/World.h"
#include "UObject/Package.h"
#include "UObject/UnrealType.h"
#include "Components/SceneComponent.h"
#include "HAL/PlatformTime.h"

namespace UE::Finare::OrdinaryActorCompositionTests::Private
{
	int32 ReadExposedInt(const UObject* Object, const TCHAR* Name)
	{
		const FIntProperty* Property = Object != nullptr
			? FindFProperty<FIntProperty>(Object->GetClass(), Name) : nullptr;
		return Property != nullptr ? Property->GetPropertyValue_InContainer(Object) : -1;
	}

	bool WriteExposedInt(UObject* Object, const TCHAR* Name, int32 Value)
	{
		const FIntProperty* Property = Object != nullptr
			? FindFProperty<FIntProperty>(Object->GetClass(), Name) : nullptr;
		if (Property == nullptr) return false;
		Object->Modify();
		Property->SetPropertyValue_InContainer(Object, Value);
		return true;
	}

	UObject* ReadPeer(const UObject* Object)
	{
		const FObjectPropertyBase* Property = Object != nullptr
			? FindFProperty<FObjectPropertyBase>(Object->GetClass(), TEXT("Peer")) : nullptr;
		return Property != nullptr ? Property->GetObjectPropertyValue_InContainer(Object) : nullptr;
	}
	struct FFixtureAsset
	{
		FString Id;
		FString TypeName;
		FString DeclarationAssetPath;
	};

	bool ReadFixtureScript(
		const TSharedPtr<FJsonObject>& Root,
		const TCHAR* TypeSuffix,
		FFixtureAsset& OutAsset)
	{
		const TArray<TSharedPtr<FJsonValue>>* Scripts = nullptr;
		if (!Root.IsValid() || !Root->TryGetArrayField(TEXT("scripts"), Scripts) ||
			Scripts == nullptr)
		{
			return false;
		}
		bool bFound = false;
		for (const TSharedPtr<FJsonValue>& Value : *Scripts)
		{
			const TSharedPtr<FJsonObject> Script = Value.IsValid() ? Value->AsObject() : nullptr;
			FString TypeName;
			if (!Script.IsValid() ||
				!Script->TryGetStringField(TEXT("scriptTypeName"), TypeName) ||
				!TypeName.EndsWith(TypeSuffix, ESearchCase::CaseSensitive))
			{
				continue;
			}
			if (bFound ||
				!Script->TryGetStringField(TEXT("scriptClassId"), OutAsset.Id) ||
				!Script->TryGetStringField(TEXT("scriptTypeName"), OutAsset.TypeName) ||
				!Script->TryGetStringField(
					TEXT("declarationAssetPath"), OutAsset.DeclarationAssetPath))
			{
				return false;
			}
			bFound = true;
		}
		return bFound;
	}

	bool ParseFixtureId(const FString& Id, FGuid& OutId)
	{
		FString Digits = Id;
		Digits.RemoveFromStart(TEXT("us_"));
		return FGuid::ParseExact(Digits, EGuidFormats::Digits, OutId);
	}

	void ConfigureFixtureAsset(
		UFinareAsset& Asset,
		const FFixtureAsset& Fixture,
		const FString& AssemblyPath,
		const FString& DeclarationPath,
		const FString& ChunkPath,
		EFinareClassKind Kind)
	{
		ParseFixtureId(Fixture.Id, Asset.ScriptClassId);
		Asset.ScriptClassKind = Kind;
		Asset.ScriptAssemblyPath = AssemblyPath;
		Asset.CompiledChunkPath = ChunkPath;
		Asset.ScriptTypeName = Fixture.TypeName;
		Asset.DeclarationPath = DeclarationPath;
		Asset.DeclarationAssetPath = Fixture.DeclarationAssetPath;
	}

	FString LoadDeclaration()
	{
		const FString Path = FPaths::ConvertRelativePathToFull(FPaths::Combine(
			FPaths::ProjectDir(),
			TEXT("../artifacts/m13-3c-4-class-composition/class-binding/class-binding-fixture.asset.json")));
		FString Declaration;
		FFileHelper::LoadFileToString(Declaration, *Path);
		return Declaration;
	}

	FString WithBaseClassPath(const FString& Source, const FString& ParentPath)
	{
		TSharedPtr<FJsonObject> Root;
		const TSharedRef<TJsonReader<>> Reader = TJsonReaderFactory<>::Create(Source);
		if (!FJsonSerializer::Deserialize(Reader, Root) || !Root.IsValid())
		{
			return FString();
		}
		const TArray<TSharedPtr<FJsonValue>>* Assets = nullptr;
		if (!Root->TryGetArrayField(TEXT("assets"), Assets) || Assets == nullptr ||
			Assets->Num() != 1)
		{
			return FString();
		}
		const TSharedPtr<FJsonObject> Asset = (*Assets)[0].IsValid()
			? (*Assets)[0]->AsObject()
			: nullptr;
		const TSharedPtr<FJsonValue>* BindingValue = Asset.IsValid()
			? Asset->Values.Find(TEXT("classBinding"))
			: nullptr;
		const TSharedPtr<FJsonObject> Binding = BindingValue != nullptr &&
			BindingValue->IsValid()
			? (*BindingValue)->AsObject()
			: nullptr;
		if (!Binding.IsValid())
		{
			return FString();
		}
		Binding->SetStringField(TEXT("baseClassPath"), ParentPath);
		FString Result;
		const TSharedRef<TJsonWriter<>> Writer = TJsonWriterFactory<>::Create(&Result);
		return FJsonSerializer::Serialize(Root.ToSharedRef(), Writer) ? Result : FString();
	}

	void ConfigureDemoAsset(
		UFinareAsset& Asset,
		const FString& TypeName = TEXT("Finare.Unreal.Demo.DemoScript"))
	{
		// DemoScript is a component-hosted Script Asset, not a generated Script Class.
		Asset.ScriptClassKind = EFinareClassKind::None;
		Asset.ScriptAssemblyPath =
			TEXT("{PluginDir}/Binaries/Managed/Demo/Finare.Unreal.Demo.dll");
		Asset.ScriptTypeName = TypeName;
		Asset.DeclarationPath =
			TEXT("{PluginDir}/Binaries/Managed/Demo/Finare.Unreal.Demo.asset.json");
		Asset.DeclarationAssetPath = TEXT("Unreal/Demo");
		Asset.AuthoringSurfacePath =
			TEXT("{PluginDir}/Binaries/Managed/Demo/Finare.Unreal.Demo.surface.json");
	}
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
	FFinareOrdinaryActorCompositionTest,
	"Finare.Unreal.ScriptClass.ParentChange.OrdinaryActorComposition",
	EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FFinareOrdinaryActorCompositionTest::RunTest(const FString& Parameters)
{
	using namespace UE::Finare::OrdinaryActorCompositionTests::Private;
	(void)Parameters;
	const FString Declaration = LoadDeclaration();
	if (!TestTrue(TEXT("Loads the class binding declaration fixture"), !Declaration.IsEmpty()))
	{
		return false;
	}
	TSharedPtr<FJsonObject> Root;
	const TSharedRef<TJsonReader<>> Reader = TJsonReaderFactory<>::Create(Declaration);
	const TArray<TSharedPtr<FJsonValue>>* Assets = nullptr;
	if (!TestTrue(TEXT("Declaration fixture contains one Script Asset"),
		FJsonSerializer::Deserialize(Reader, Root) && Root.IsValid() &&
		Root->TryGetArrayField(TEXT("assets"), Assets) && Assets != nullptr &&
		Assets->Num() == 1))
	{
		return false;
	}
	const TSharedPtr<FJsonObject> AssetObject = (*Assets)[0]->AsObject();
	FString IdText;
	FString QualifiedName;
	FString AssetPath;
	if (!TestTrue(TEXT("Declaration exposes its owner identity"),
		AssetObject.IsValid() && AssetObject->TryGetStringField(TEXT("id"), IdText) &&
		AssetObject->TryGetStringField(TEXT("qualifiedName"), QualifiedName) &&
		AssetObject->TryGetStringField(TEXT("assetPath"), AssetPath)))
	{
		return false;
	}
	IdText.RemoveFromStart(TEXT("us_"));
	FGuid ClassId;
	if (!TestTrue(TEXT("Declaration owner identity parses"),
		FGuid::ParseExact(IdText, EGuidFormats::Digits, ClassId)))
	{
		return false;
	}
	const FString Name = TEXT("OrdinaryActorComposition_") +
		FGuid::NewGuid().ToString(EGuidFormats::Digits);
	UPackage* Package = CreatePackage(*(TEXT("/Engine/Transient/") + Name));
	UFinareBlueprint* Blueprint = Package != nullptr
		? Cast<UFinareBlueprint>(FKismetEditorUtilities::CreateBlueprint(
			AFinareActor::StaticClass(), Package, FName(*Name), BPTYPE_Normal,
			UFinareBlueprint::StaticClass(), UFinareBlueprintGeneratedClass::StaticClass(),
			FName(TEXT("FinareOrdinaryActorComposition"))))
		: nullptr;
	UFinareAsset* ScriptAsset = Package != nullptr
		? NewObject<UFinareAsset>(Package, TEXT("ScriptAsset"), RF_Transactional)
		: nullptr;
	if (!TestNotNull(TEXT("Creates the managed Actor Blueprint"), Blueprint) ||
		!TestNotNull(TEXT("Creates the Script Asset"), ScriptAsset) ||
		!TestNotNull(TEXT("Managed Blueprint has a generated class"),
			Blueprint != nullptr ? Blueprint->GeneratedClass.Get() : nullptr))
	{
		return false;
	}
	ScriptAsset->ScriptClassId = ClassId;
	ScriptAsset->ScriptClassKind = EFinareClassKind::Actor;
	ScriptAsset->GeneratedClassPath = FSoftClassPath(Blueprint->GeneratedClass);
	ScriptAsset->ScriptTypeName = QualifiedName;
	ScriptAsset->DeclarationAssetPath = AssetPath;
	ScriptAsset->RequiredTargetClass = AFinareActor::StaticClass();
	ScriptAsset->DeclarationPath = TEXT("unused");
	Blueprint->ScriptAsset = ScriptAsset;
	AFinareActor* InitialDefaults = Cast<AFinareActor>(
		Blueprint->GeneratedClass->GetDefaultObject());
	if (!TestNotNull(TEXT("Initial Actor defaults expose the native carrier"), InitialDefaults) ||
		!TestNotNull(TEXT("Initial Actor defaults expose its ScriptComponent"),
			InitialDefaults != nullptr ? InitialDefaults->ScriptComponent.Get() : nullptr))
	{
		return false;
	}
	InitialDefaults->ScriptComponent->ScriptAsset = ScriptAsset;
	const bool bInitialState =
		InitialDefaults->ScriptComponent->ScriptState.AddProperty(
			TEXT("Count"), EPropertyBagPropertyType::Int32) == EPropertyBagAlterationResult::Success &&
		InitialDefaults->ScriptComponent->SetScriptStateInt32(TEXT("Count"), 3);
	TestTrue(TEXT("Native carrier keeps its authored state"), bInitialState &&
		InitialDefaults->ScriptComponent->IsScriptStateOverridden(TEXT("Count")));
	USCS_Node* UserNode = Blueprint->SimpleConstructionScript != nullptr
		? Blueprint->SimpleConstructionScript->CreateNode(USceneComponent::StaticClass(), TEXT("UserComponent"))
		: nullptr;
	if (!TestNotNull(TEXT("Creates an unrelated user SCS component"), UserNode) ||
		UserNode->GetVariableName() != FName(TEXT("UserComponent")))
	{
		return false;
	}
	Blueprint->SimpleConstructionScript->AddNode(UserNode);
	FFinareBlueprintClassSyncResult SyncResult;
	FString Error;
	const FString OrdinaryDeclaration = WithBaseClassPath(Declaration,
		AActor::StaticClass()->GetPathName());
	if (!TestTrue(TEXT("Synchronizes an Actor Script Asset onto ordinary AActor"),
		FFinareBlueprintClassMaterializer::SynchronizeJson(
			*ScriptAsset, *Blueprint, OrdinaryDeclaration, false, SyncResult, Error)))
	{
		AddError(Error);
		return false;
	}
	USCS_Node* CarrierNode = Blueprint->SimpleConstructionScript->FindSCSNode(TEXT("UnifiedScript"));
	UFinareComponent* CarrierTemplate = CarrierNode != nullptr
		? Cast<UFinareComponent>(CarrierNode->ComponentTemplate) : nullptr;
	TestTrue(TEXT("Ordinary Actor uses the exact managed SCS carrier"),
		Blueprint->ParentClass == AActor::StaticClass() && CarrierNode != nullptr &&
		CarrierNode->GetVariableName() == FName(TEXT("UnifiedScript")) &&
		CarrierTemplate != nullptr && CarrierTemplate->ScriptAsset == ScriptAsset &&
		CarrierTemplate->GetScriptStateInt32(TEXT("Count"), -1) == 3 &&
		CarrierTemplate->IsScriptStateOverridden(TEXT("Count")) &&
		ScriptAsset->RequiredTargetClass.Get() == AActor::StaticClass());
	TestTrue(TEXT("Ordinary Actor preserves the unrelated user SCS component"),
		Blueprint->SimpleConstructionScript->FindSCSNode(TEXT("UserComponent")) == UserNode);
	Error.Reset();
	const FString LegacyDeclaration = WithBaseClassPath(Declaration,
		AFinareActor::StaticClass()->GetPathName());
	if (!TestTrue(TEXT("Returns to the legacy Finare carrier"),
		FFinareBlueprintClassMaterializer::SynchronizeJson(
			*ScriptAsset, *Blueprint, LegacyDeclaration, false, SyncResult, Error)))
	{
		AddError(Error);
		return false;
	}
	AFinareActor* RestoredDefaults = Blueprint->GeneratedClass != nullptr
		? Cast<AFinareActor>(Blueprint->GeneratedClass->GetDefaultObject()) : nullptr;
	TestTrue(TEXT("Leaving ordinary Actor restores the native carrier"),
		Blueprint->ParentClass == AFinareActor::StaticClass() && RestoredDefaults != nullptr &&
		RestoredDefaults->ScriptComponent != nullptr &&
		RestoredDefaults->ScriptComponent->ScriptAsset == ScriptAsset &&
		RestoredDefaults->ScriptComponent->GetScriptStateInt32(TEXT("Count"), -1) == 3 &&
		RestoredDefaults->ScriptComponent->IsScriptStateOverridden(TEXT("Count")) &&
		ScriptAsset->RequiredTargetClass.Get() == AFinareActor::StaticClass());
	TestTrue(TEXT("Legacy transition preserves the unrelated user SCS component"),
		Blueprint->SimpleConstructionScript->FindSCSNode(TEXT("UserComponent")) == UserNode);
	return !HasAnyErrors();
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
	FFinareOrdinaryActorMultiAssetReloadTest,
	"Finare.Unreal.ScriptClass.ParentChange.OrdinaryActorMultiAssetReload",
	EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FFinareOrdinaryActorMultiAssetReloadTest::RunTest(const FString& Parameters)
{
	using namespace UE::Finare::OrdinaryActorCompositionTests::Private;
	(void)Parameters;
	UWorld* World = UWorld::CreateWorld(EWorldType::Game, false);
	if (!TestNotNull(TEXT("Creates the composed Actor test World"), World))
	{
		return false;
	}
	World->AddToRoot();
	AActor* Owner = World->SpawnActor<AActor>();
	UFinareComponent* Health = Owner != nullptr ? NewObject<UFinareComponent>(Owner) : nullptr;
	UFinareComponent* Mover = Owner != nullptr ? NewObject<UFinareComponent>(Owner) : nullptr;
	UFinareAsset* HealthAsset = NewObject<UFinareAsset>(World);
	UFinareAsset* MoverAsset = NewObject<UFinareAsset>(World);
	UFinareAsset* BrokenAsset = NewObject<UFinareAsset>(World);
	if (!TestNotNull(TEXT("Creates the composed Actor owner"), Owner) ||
		!TestNotNull(TEXT("Creates the Health component instance"), Health) ||
		!TestNotNull(TEXT("Creates the Mover component instance"), Mover) ||
		!TestNotNull(TEXT("Creates the Health Script Asset"), HealthAsset) ||
		!TestNotNull(TEXT("Creates the Mover Script Asset"), MoverAsset) ||
		!TestNotNull(TEXT("Creates a deliberately broken candidate"), BrokenAsset))
	{
		World->RemoveFromRoot();
		World->DestroyWorld(false);
		return false;
	}
	ConfigureDemoAsset(*HealthAsset);
	ConfigureDemoAsset(*MoverAsset);
	ConfigureDemoAsset(*BrokenAsset, TEXT("Finare.Unreal.Demo.MissingReloadComponent"));
	Health->ScriptAsset = HealthAsset;
	Mover->ScriptAsset = MoverAsset;
	Health->LifecycleOwnershipKey = TEXT("CompositionHealth");
	Mover->LifecycleOwnershipKey = TEXT("CompositionMover");
	Owner->AddInstanceComponent(Health);
	Owner->AddInstanceComponent(Mover);
	Health->RegisterComponentWithWorld(World);
	Mover->RegisterComponentWithWorld(World);
	const bool bPrepared = Health->RefreshScriptState() && Mover->RefreshScriptState() &&
		Health->SetScriptStateInt32(TEXT("InitialValue"), 17) &&
		Mover->SetScriptStateInt32(TEXT("InitialValue"), 29);
	Health->BeginPlay();
	Mover->BeginPlay();
	TestTrue(TEXT("Two component assets materialize their C# state"), bPrepared);
	TestTrue(TEXT("Both component assets run on the same ordinary Actor"),
		Health->bScriptRunning && Mover->bScriptRunning &&
		Health->GetOwner() == Owner && Mover->GetOwner() == Owner);
	TestEqual(TEXT("Health starts in generation one"), Health->ActiveGeneration, FString(TEXT("1")));
	TestEqual(TEXT("Mover starts in generation one"), Mover->ActiveGeneration, FString(TEXT("1")));
	const FString Publication = TEXT(
		"{\"schemaVersion\":\"unifiedscript.reload-transaction/v0.1\","
		"\"transactionId\":\"ordinary-actor-multi-asset\","
		"\"source\":\"dailyBuild\",\"state\":\"published\",\"phases\":[]}");
	const FFinareReloadBatchResult InitialReload =
		SFinareAuthoringSurface::ReloadAllScriptClassComponents(Publication);
	TestEqual(TEXT("Daily Build discovers both distinct component assets"), InitialReload.Matched, 2);
	TestEqual(TEXT("Daily Build advances both component assets together"), InitialReload.Reloaded, 2);
	TestEqual(TEXT("Daily Build reports no failure for the two valid assets"), InitialReload.Failed, 0);
	TestEqual(TEXT("Health advances with its own asset"), Health->ActiveGeneration, FString(TEXT("2")));
	TestEqual(TEXT("Mover advances with its own asset"), Mover->ActiveGeneration, FString(TEXT("2")));
	Mover->ScriptAsset = BrokenAsset;
	AddExpectedError(TEXT("MissingReloadComponent"), EAutomationExpectedErrorFlags::Contains, 1);
	const FFinareReloadBatchResult Rejected =
		SFinareAuthoringSurface::ReloadAllScriptClassComponents(Publication);
	TestEqual(TEXT("A broken candidate still matches the whole batch"), Rejected.Matched, 2);
	TestEqual(TEXT("A broken candidate prevents every component commit"), Rejected.Reloaded, 0);
	TestEqual(TEXT("A broken candidate fails the complete batch"), Rejected.Failed, 2);
	TestEqual(TEXT("Health keeps its committed generation after rejection"),
		Health->ActiveGeneration, FString(TEXT("2")));
	TestEqual(TEXT("Mover keeps its committed generation after rejection"),
		Mover->ActiveGeneration, FString(TEXT("2")));
	Mover->ScriptAsset = MoverAsset;
	const FFinareReloadBatchResult FinalReload =
		SFinareAuthoringSurface::ReloadAllScriptClassComponents(Publication);
	TestEqual(TEXT("Restored component assets commit together"), FinalReload.Reloaded, 2);
	TestEqual(TEXT("Health reaches the next shared generation"), Health->ActiveGeneration, FString(TEXT("3")));
	TestEqual(TEXT("Mover reaches the next shared generation"), Mover->ActiveGeneration, FString(TEXT("3")));
	Health->StopScript();
	Mover->StopScript();
	Health->UnregisterComponent();
	Mover->UnregisterComponent();
	World->RemoveFromRoot();
	World->DestroyWorld(false);
	return !HasAnyErrors();
}

namespace UE::Finare::OrdinaryActorCompositionTests::Private
{
	struct FCompositionWorkflowState
	{
		FAutomationTestBase* Test = nullptr;
		FString ConfigPath;
		FString SourcePath;
		FString WatcherConfigPath;
		FString RevisionBuildScript;
		FString ReportPath;
		FString OriginalSource;
		FString V2Source;
		FString BadSource;
		TArray<uint8> OriginalLiveAssembly;
		TArray<uint8> OriginalLiveDeclaration;
		TArray<uint8> OriginalReport;
		bool bHadLiveAssembly = false;
		bool bHadLiveDeclaration = false;
		bool bHadReport = false;
		TUniquePtr<FFinareSourceWatcher> Watcher;
		FDelegateHandle CompletionHandle;
		FFinareDailyBuildResult LastResult;
		int32 CompletionCount = 0;
		double BadBuildStartedAt = 0.0;
		bool bWorkflowFailed = false;
		UWorld* World = nullptr;
		UPackage* Package = nullptr;
		UPackage* HealthPackage = nullptr;
		UPackage* MoverPackage = nullptr;
		FString PackageName;
		FString HealthPackageName;
		FString MoverPackageName;
		FString PackageFilename;
		FString HealthPackageFilename;
		FString MoverPackageFilename;
		TArray<uint8> OriginalPackage;
		TArray<uint8> OriginalHealthPackage;
		TArray<uint8> OriginalMoverPackage;
		bool bHadPackage = false;
		bool bHadHealthPackage = false;
		bool bHadMoverPackage = false;
		UFinareBlueprint* ActorBlueprint = nullptr;
		UFinareBlueprint* HealthBlueprint = nullptr;
		UFinareBlueprint* MoverBlueprint = nullptr;
		USCS_Node* CarrierNode = nullptr;
		USCS_Node* HealthNode = nullptr;
		USCS_Node* MoverNode = nullptr;
		UFinareComponent* CarrierTemplate = nullptr;
		UFinareComponent* HealthTemplate = nullptr;
		UFinareComponent* MoverTemplate = nullptr;
		AActor* Owner = nullptr;
		UFinareComponent* Carrier = nullptr;
		UFinareComponent* Health = nullptr;
		UFinareComponent* Mover = nullptr;
		UFinareAsset* CarrierAsset = nullptr;
		UFinareAsset* HealthAsset = nullptr;
		UFinareAsset* MoverAsset = nullptr;
		bool bCleaned = false;

		void OnDailyBuildCompleted(const FFinareDailyBuildResult& Result)
		{
			++CompletionCount;
			LastResult = Result;
		}

		FString LiveAssemblyPath;
		FString LiveDeclarationPath;

		void Cleanup()
		{
			if (bCleaned)
			{
				return;
			}
			bCleaned = true;
			if (CompletionHandle.IsValid())
			{
				IFinareEditorModule::Get().OnDailyBuildCompleted().Remove(CompletionHandle);
				CompletionHandle.Reset();
			}
			Watcher.Reset();
			IFinareEditorModule::Get().ClearDailyBuildCommandOverride();
			if (Carrier != nullptr)
			{
				Carrier->StopScript();
				Carrier->UnregisterComponent();
			}
			if (Health != nullptr)
			{
				Health->StopScript();
				Health->UnregisterComponent();
			}
			if (Mover != nullptr)
			{
				Mover->StopScript();
				Mover->UnregisterComponent();
			}
			if (Owner != nullptr && !Owner->IsActorBeingDestroyed())
			{
				Owner->Destroy();
				Owner = nullptr;
			}
			if (World != nullptr)
			{
				World->RemoveFromRoot();
				World->DestroyWorld(false);
				World = nullptr;
			}
			const auto UnloadPackage = [](UPackage*& PackageToUnload)
			{
				if (PackageToUnload == nullptr)
				{
					return;
				}
				PackageToUnload->SetDirtyFlag(false);
				if (PackageToUnload->IsRooted())
				{
					PackageToUnload->RemoveFromRoot();
				}
				TArray<UPackage*> Packages{PackageToUnload};
				UPackageTools::UnloadPackages(Packages);
				PackageToUnload = nullptr;
			};
			UnloadPackage(Package);
			UnloadPackage(HealthPackage);
			UnloadPackage(MoverPackage);
			CollectGarbage(RF_NoFlags);
			ActorBlueprint = nullptr;
			HealthBlueprint = nullptr;
			MoverBlueprint = nullptr;
			CarrierNode = nullptr;
			HealthNode = nullptr;
			MoverNode = nullptr;
			CarrierTemplate = nullptr;
			HealthTemplate = nullptr;
			MoverTemplate = nullptr;
			CarrierAsset = nullptr;
			HealthAsset = nullptr;
			MoverAsset = nullptr;
			const auto RestorePackage = [](const FString& Filename, bool bHadOriginal,
				const TArray<uint8>& OriginalBytes)
			{
				if (Filename.IsEmpty())
				{
					return;
				}
				if (bHadOriginal)
				{
					FFileHelper::SaveArrayToFile(OriginalBytes, *Filename);
				}
				else if (IFileManager::Get().FileExists(*Filename))
				{
					IFileManager::Get().Delete(*Filename, false, true, true);
				}
			};
			RestorePackage(PackageFilename, bHadPackage, OriginalPackage);
			RestorePackage(HealthPackageFilename, bHadHealthPackage, OriginalHealthPackage);
			RestorePackage(MoverPackageFilename, bHadMoverPackage, OriginalMoverPackage);
			if (!SourcePath.IsEmpty() && !OriginalSource.IsEmpty())
			{
				FFileHelper::SaveStringToFile(OriginalSource, *SourcePath);
			}
			if (!LiveAssemblyPath.IsEmpty())
			{
				if (bHadLiveAssembly)
				{
					FFileHelper::SaveArrayToFile(OriginalLiveAssembly, *LiveAssemblyPath);
				}
				else
				{
					IFileManager::Get().Delete(*LiveAssemblyPath, false, true, true);
				}
			}
			if (!LiveDeclarationPath.IsEmpty())
			{
				if (bHadLiveDeclaration)
				{
					FFileHelper::SaveArrayToFile(OriginalLiveDeclaration, *LiveDeclarationPath);
				}
				else
				{
					IFileManager::Get().Delete(*LiveDeclarationPath, false, true, true);
				}
			}
			if (!ReportPath.IsEmpty())
			{
				if (bHadReport)
				{
					FFileHelper::SaveArrayToFile(OriginalReport, *ReportPath);
				}
				else
				{
					IFileManager::Get().Delete(*ReportPath, false, true, true);
				}
			}
			IFinareEditorModule::Get().ClearDailyBuildCommandOverride();
		}

		~FCompositionWorkflowState()
		{
			Cleanup();
		}
	};

	bool ReadWorkflowString(
		const TSharedPtr<FJsonObject>& Root,
		const TCHAR* Field,
		FString& OutValue)
	{
		return Root.IsValid() && Root->TryGetStringField(Field, OutValue) &&
			!OutValue.IsEmpty();
	}

	bool MakeWorkflowSourceVariants(
		const FString& Original,
		FString& OutV2,
		FString& OutBad)
	{
		OutV2 = Original;
		OutV2.ReplaceInline(TEXT("=> \"v1\""), TEXT("=> \"v2\""));
		OutBad = OutV2;
		OutBad.ReplaceInline(TEXT("=> \"v2\""), TEXT("=> \"bad\""));
		OutBad.ReplaceInline(TEXT("=> false"), TEXT("=> true"));
		return OutV2 != Original && OutBad != OutV2;
	}
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
	FFinarePureCSharpWorkflowTest,
	"Finare.Unreal.ScriptClass.Composition.PureCSharpWorkflow",
	EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FFinarePureCSharpWorkflowTest::RunTest(const FString& Parameters)
{
	using namespace UE::Finare::OrdinaryActorCompositionTests::Private;
	(void)Parameters;
	TSharedPtr<FCompositionWorkflowState> State = MakeShared<FCompositionWorkflowState>();
	State->Test = this;
	State->ConfigPath = FPaths::ConvertRelativePathToFull(FPaths::Combine(
		FPaths::ProjectDir(), TEXT("../artifacts/class-composition/fixture.json")));
	FString ConfigJson;
	TSharedPtr<FJsonObject> Config;
	if (!TestTrue(
		TEXT("Pure C# workflow loads its prepared fixture configuration"),
		FFileHelper::LoadFileToString(ConfigJson, *State->ConfigPath)))
	{
		return false;
	}
	const TSharedRef<TJsonReader<>> Reader = TJsonReaderFactory<>::Create(ConfigJson);
	if (!TestTrue(
		TEXT("Pure C# workflow fixture is valid JSON"),
		FJsonSerializer::Deserialize(Reader, Config) && Config.IsValid()))
	{
		return false;
	}
	FString PackageDirectory;
	if (!TestTrue(
		TEXT("Pure C# workflow exposes its watcher and revision build inputs"),
		ReadWorkflowString(Config, TEXT("watchSourcePath"), State->SourcePath) &&
		ReadWorkflowString(Config, TEXT("watcherConfigPath"), State->WatcherConfigPath) &&
		ReadWorkflowString(Config, TEXT("revisionBuildScript"), State->RevisionBuildScript) &&
		ReadWorkflowString(Config, TEXT("dailyBuildReportPath"), State->ReportPath) &&
		ReadWorkflowString(Config, TEXT("liveAssemblyPath"), State->LiveAssemblyPath) &&
		ReadWorkflowString(Config, TEXT("liveDeclarationPath"), State->LiveDeclarationPath) &&
		ReadWorkflowString(Config, TEXT("packageDirectory"), PackageDirectory) &&
		FFileHelper::LoadFileToString(State->OriginalSource, *State->SourcePath) &&
		MakeWorkflowSourceVariants(State->OriginalSource, State->V2Source, State->BadSource)))
	{
		return false;
	}
	State->bHadLiveAssembly = IFileManager::Get().FileExists(*State->LiveAssemblyPath) &&
		FFileHelper::LoadFileToArray(State->OriginalLiveAssembly, *State->LiveAssemblyPath);
	State->bHadLiveDeclaration = IFileManager::Get().FileExists(*State->LiveDeclarationPath) &&
		FFileHelper::LoadFileToArray(State->OriginalLiveDeclaration, *State->LiveDeclarationPath);
	State->bHadReport = IFileManager::Get().FileExists(*State->ReportPath) &&
		FFileHelper::LoadFileToArray(State->OriginalReport, *State->ReportPath);
	FFixtureAsset ActorFixture;
	FFixtureAsset HealthFixture;
	FFixtureAsset MoverFixture;
	if (!TestTrue(
		TEXT("Pure C# workflow resolves all three Script Asset identities"),
		ReadFixtureScript(Config, TEXT(".CompositionActor"), ActorFixture) &&
		ReadFixtureScript(Config, TEXT(".CompositionHealth"), HealthFixture) &&
		ReadFixtureScript(Config, TEXT(".CompositionMover"), MoverFixture)))
	{
		return false;
	}

	const auto PackageNameFromClassPath = [](const FString& ClassPath)
	{
		const int32 Dot = ClassPath.Find(TEXT("."), ESearchCase::CaseSensitive,
			ESearchDir::FromEnd);
		return Dot == INDEX_NONE ? FString() : ClassPath.Left(Dot);
	};
	const auto ClassAssetName = [](const FString& ClassPath)
	{
		const int32 Dot = ClassPath.Find(TEXT("."), ESearchCase::CaseSensitive,
			ESearchDir::FromEnd);
		const int32 Slash = ClassPath.Find(TEXT("/"), ESearchCase::CaseSensitive,
			ESearchDir::FromEnd);
		return Dot == INDEX_NONE || Slash == INDEX_NONE || Dot <= Slash
			? FString()
			: ClassPath.Mid(Slash + 1, Dot - Slash - 1);
	};
	const FString ActorClassPath = Config->GetStringField(TEXT("actorClassPath"));
	const FString HealthClassPath = Config->GetStringField(TEXT("healthClassPath"));
	const FString MoverClassPath = Config->GetStringField(TEXT("moverClassPath"));
	State->PackageName = PackageNameFromClassPath(ActorClassPath);
	State->HealthPackageName = PackageNameFromClassPath(HealthClassPath);
	State->MoverPackageName = PackageNameFromClassPath(MoverClassPath);
	const FString ActorName = ClassAssetName(ActorClassPath);
	const FString HealthName = ClassAssetName(HealthClassPath);
	const FString MoverName = ClassAssetName(MoverClassPath);
	if (!TestTrue(TEXT("Pure C# workflow uses the fixture's independent generated-class packages"),
		!State->PackageName.IsEmpty() && !State->HealthPackageName.IsEmpty() &&
		!State->MoverPackageName.IsEmpty() &&
		State->PackageName.StartsWith(PackageDirectory + TEXT("/")) &&
		State->HealthPackageName.StartsWith(PackageDirectory + TEXT("/")) &&
		State->MoverPackageName.StartsWith(PackageDirectory + TEXT("/")) &&
		!ActorName.IsEmpty() && !HealthName.IsEmpty() && !MoverName.IsEmpty()))
	{
		State->Cleanup();
		return false;
	}
	const auto SnapshotPackage = [&](const FString& Name, FString& Filename,
		bool& bHadOriginal, TArray<uint8>& OriginalBytes)
	{
		Filename = FPackageName::LongPackageNameToFilename(
			Name, FPackageName::GetAssetPackageExtension());
		bHadOriginal = IFileManager::Get().FileExists(*Filename);
		return !bHadOriginal || FFileHelper::LoadFileToArray(OriginalBytes, *Filename);
	};
	if (!TestTrue(TEXT("Pure C# workflow snapshots its controlled fixture packages"),
		SnapshotPackage(State->PackageName, State->PackageFilename,
			State->bHadPackage, State->OriginalPackage) &&
		SnapshotPackage(State->HealthPackageName, State->HealthPackageFilename,
			State->bHadHealthPackage, State->OriginalHealthPackage) &&
		SnapshotPackage(State->MoverPackageName, State->MoverPackageFilename,
			State->bHadMoverPackage, State->OriginalMoverPackage)))
	{
		State->Cleanup();
		return false;
	}
	const auto PreparePackage = [](const FString& Name)
	{
		if (UPackage* Existing = FindPackage(nullptr, *Name))
		{
			Existing->SetDirtyFlag(false);
			if (Existing->IsRooted())
			{
				Existing->RemoveFromRoot();
			}
			TArray<UPackage*> ExistingPackages{Existing};
			UPackageTools::UnloadPackages(ExistingPackages);
			CollectGarbage(RF_NoFlags);
		}
		UPackage* Result = CreatePackage(*Name);
		if (Result != nullptr)
		{
			Result->AddToRoot();
		}
		return Result;
	};
	State->Package = PreparePackage(State->PackageName);
	State->HealthPackage = PreparePackage(State->HealthPackageName);
	State->MoverPackage = PreparePackage(State->MoverPackageName);
	State->ActorBlueprint = State->Package != nullptr
		? Cast<UFinareBlueprint>(FKismetEditorUtilities::CreateBlueprint(
			AFinareActor::StaticClass(), State->Package, FName(*ActorName), BPTYPE_Normal,
			UFinareBlueprint::StaticClass(), UFinareBlueprintGeneratedClass::StaticClass(),
			FName(TEXT("FinarePureCSharpWorkflow"))))
		: nullptr;
	State->HealthBlueprint = State->HealthPackage != nullptr
		? Cast<UFinareBlueprint>(FKismetEditorUtilities::CreateBlueprint(
			UFinareComponent::StaticClass(), State->HealthPackage, FName(*HealthName),
			BPTYPE_Normal, UFinareBlueprint::StaticClass(),
			UFinareBlueprintGeneratedClass::StaticClass(), FName(TEXT("FinarePureCSharpWorkflow"))))
		: nullptr;
	State->MoverBlueprint = State->MoverPackage != nullptr
		? Cast<UFinareBlueprint>(FKismetEditorUtilities::CreateBlueprint(
			UFinareComponent::StaticClass(), State->MoverPackage, FName(*MoverName),
			BPTYPE_Normal, UFinareBlueprint::StaticClass(),
			UFinareBlueprintGeneratedClass::StaticClass(), FName(TEXT("FinarePureCSharpWorkflow"))))
		: nullptr;
	State->CarrierAsset = State->Package != nullptr
		? NewObject<UFinareAsset>(State->Package, TEXT("CompositionActorScriptAsset"),
			RF_Public | RF_Standalone | RF_Transactional) : nullptr;
	State->HealthAsset = State->HealthPackage != nullptr
		? NewObject<UFinareAsset>(State->HealthPackage, TEXT("CompositionHealthScriptAsset"),
			RF_Public | RF_Standalone | RF_Transactional) : nullptr;
	State->MoverAsset = State->MoverPackage != nullptr
		? NewObject<UFinareAsset>(State->MoverPackage, TEXT("CompositionMoverScriptAsset"),
			RF_Public | RF_Standalone | RF_Transactional) : nullptr;
	if (!TestNotNull(TEXT("Pure C# workflow creates its Actor package"), State->Package) ||
		!TestNotNull(TEXT("Pure C# workflow creates its Health package"), State->HealthPackage) ||
		!TestNotNull(TEXT("Pure C# workflow creates its Mover package"), State->MoverPackage) ||
		!TestNotNull(TEXT("Pure C# workflow creates the Actor Blueprint"), State->ActorBlueprint) ||
		!TestNotNull(TEXT("Pure C# workflow creates the Health Blueprint"), State->HealthBlueprint) ||
		!TestNotNull(TEXT("Pure C# workflow creates the Mover Blueprint"), State->MoverBlueprint) ||
		!TestNotNull(TEXT("Pure C# workflow creates the Actor Script Asset"), State->CarrierAsset) ||
		!TestNotNull(TEXT("Pure C# workflow creates the Health Script Asset"), State->HealthAsset) ||
		!TestNotNull(TEXT("Pure C# workflow creates the Mover Script Asset"), State->MoverAsset))
	{
		State->Cleanup();
		return false;
	}
	State->CarrierAsset->GeneratedClassPath = FSoftClassPath(State->ActorBlueprint->GeneratedClass);
	State->HealthAsset->GeneratedClassPath = FSoftClassPath(State->HealthBlueprint->GeneratedClass);
	State->MoverAsset->GeneratedClassPath = FSoftClassPath(State->MoverBlueprint->GeneratedClass);
	TestEqual(TEXT("Actor generated class path follows the fixture contract"),
		State->CarrierAsset->GeneratedClassPath.ToString(), Config->GetStringField(TEXT("actorClassPath")));
	TestEqual(TEXT("Health generated class path follows the fixture contract"),
		State->HealthAsset->GeneratedClassPath.ToString(), Config->GetStringField(TEXT("healthClassPath")));
	TestEqual(TEXT("Mover generated class path follows the fixture contract"),
		State->MoverAsset->GeneratedClassPath.ToString(), Config->GetStringField(TEXT("moverClassPath")));
	// Start with the same native carrier the normal factory creates.  The
	// emitted Actor class binding below then drives the real native-to-SCS
	// transition through the materializer.
	State->CarrierAsset->RequiredTargetClass = AFinareActor::StaticClass();
	State->HealthAsset->RequiredTargetClass = AActor::StaticClass();
	State->MoverAsset->RequiredTargetClass = AActor::StaticClass();
	// Persist source assembly/declaration paths and keep chunk selection empty;
	// this makes the watcher build and live reload the actual managed outputs.
	ConfigureFixtureAsset(*State->CarrierAsset, ActorFixture, State->LiveAssemblyPath,
		State->LiveDeclarationPath, FString(), EFinareClassKind::Actor);
	ConfigureFixtureAsset(*State->HealthAsset, HealthFixture, State->LiveAssemblyPath,
		State->LiveDeclarationPath, FString(), EFinareClassKind::ActorComponent);
	ConfigureFixtureAsset(*State->MoverAsset, MoverFixture, State->LiveAssemblyPath,
		State->LiveDeclarationPath, FString(), EFinareClassKind::ActorComponent);
	State->ActorBlueprint->ScriptAsset = State->CarrierAsset;
	State->HealthBlueprint->ScriptAsset = State->HealthAsset;
	State->MoverBlueprint->ScriptAsset = State->MoverAsset;
	Cast<UFinareBlueprintGeneratedClass>(State->ActorBlueprint->GeneratedClass)->ManagedScriptAsset =
		State->CarrierAsset;
	Cast<UFinareBlueprintGeneratedClass>(State->HealthBlueprint->GeneratedClass)->ManagedScriptAsset =
		State->HealthAsset;
	Cast<UFinareBlueprintGeneratedClass>(State->MoverBlueprint->GeneratedClass)->ManagedScriptAsset =
		State->MoverAsset;
	Cast<UFinareComponent>(State->HealthBlueprint->GeneratedClass->GetDefaultObject())->ScriptAsset =
		State->HealthAsset;
	Cast<UFinareComponent>(State->MoverBlueprint->GeneratedClass->GetDefaultObject())->ScriptAsset =
		State->MoverAsset;
	AFinareActor* LegacyActorDefaults = Cast<AFinareActor>(
		State->ActorBlueprint->GeneratedClass->GetDefaultObject());
	if (!TestNotNull(TEXT("Pure C# workflow starts from the factory native Actor carrier"),
		LegacyActorDefaults) ||
		!TestNotNull(TEXT("Pure C# workflow initializes the native Actor ScriptAsset"),
		LegacyActorDefaults != nullptr ? LegacyActorDefaults->ScriptComponent.Get() : nullptr))
	{
		State->Cleanup();
		return false;
	}
	LegacyActorDefaults->ScriptComponent->ScriptAsset = State->CarrierAsset;
	FString DeclarationJson;
	if (!TestTrue(TEXT("Pure C# workflow loads the emitted declaration for materialization"),
		FFileHelper::LoadFileToString(DeclarationJson, *State->LiveDeclarationPath)))
	{
		State->Cleanup();
		return false;
	}
	FFinareBlueprintClassSyncResult ActorSync;
	FFinareBlueprintClassSyncResult HealthSync;
	FFinareBlueprintClassSyncResult MoverSync;
	FString SyncError;
	const bool bMaterialized =
		FFinareBlueprintClassMaterializer::SynchronizeJson(
			*State->CarrierAsset, *State->ActorBlueprint, DeclarationJson,
			false, ActorSync, SyncError) &&
		FFinareBlueprintClassMaterializer::SynchronizeJson(
			*State->HealthAsset, *State->HealthBlueprint, DeclarationJson,
			false, HealthSync, SyncError) &&
		FFinareBlueprintClassMaterializer::SynchronizeJson(
			*State->MoverAsset, *State->MoverBlueprint, DeclarationJson,
			false, MoverSync, SyncError);
	if (!TestTrue(TEXT("All three emitted declarations materialize through the editor path"),
		bMaterialized))
	{
		AddError(SyncError);
		State->Cleanup();
		return false;
	}
	State->CarrierNode = State->ActorBlueprint->SimpleConstructionScript->FindSCSNode(
		TEXT("UnifiedScript"));
	State->HealthNode = State->ActorBlueprint->SimpleConstructionScript->CreateNode(
		State->HealthBlueprint->GeneratedClass, TEXT("HealthComponent"));
	State->MoverNode = State->ActorBlueprint->SimpleConstructionScript->CreateNode(
		State->MoverBlueprint->GeneratedClass, TEXT("MoverComponent"));
	State->CarrierTemplate = State->CarrierNode != nullptr
		? Cast<UFinareComponent>(State->CarrierNode->ComponentTemplate) : nullptr;
	State->HealthTemplate = State->HealthNode != nullptr
		? Cast<UFinareComponent>(State->HealthNode->ComponentTemplate) : nullptr;
	State->MoverTemplate = State->MoverNode != nullptr
		? Cast<UFinareComponent>(State->MoverNode->ComponentTemplate) : nullptr;
	if (!TestTrue(TEXT("Pure C# workflow switches the factory Actor to ordinary AActor"),
		State->ActorBlueprint->ParentClass == AActor::StaticClass() &&
		State->CarrierAsset->RequiredTargetClass.Get() == AActor::StaticClass()) ||
		!TestNotNull(TEXT("Pure C# workflow creates the Actor SCS carrier"), State->CarrierTemplate) ||
		!TestNotNull(TEXT("Pure C# workflow creates the Health SCS carrier"), State->HealthTemplate) ||
		!TestNotNull(TEXT("Pure C# workflow creates the Mover SCS carrier"), State->MoverTemplate))
	{
		State->Cleanup();
		return false;
	}
	State->ActorBlueprint->SimpleConstructionScript->AddNode(State->HealthNode);
	State->ActorBlueprint->SimpleConstructionScript->AddNode(State->MoverNode);
	State->HealthTemplate->ScriptAsset = State->HealthAsset;
	State->MoverTemplate->ScriptAsset = State->MoverAsset;
	const bool bActorDefaultsReady = State->CarrierTemplate->RefreshScriptState();
	TestTrue(TEXT("Ordinary Actor materializes its source default before override"),
		bActorDefaultsReady && State->CarrierTemplate->GetScriptStateInt32(TEXT("Seed"), -1) == 11);
	const bool bComponentDefaultsReady =
		State->HealthTemplate->RefreshScriptState() && State->MoverTemplate->RefreshScriptState();
	TestTrue(TEXT("Component declarations retain their source defaults before override"),
		bComponentDefaultsReady &&
		State->HealthTemplate->GetScriptStateInt32(TEXT("HitPoints"), -1) == 100 &&
		State->MoverTemplate->GetScriptStateInt32(TEXT("Speed"), -1) == 3);
	const bool bMaterializedState =
		bActorDefaultsReady && State->CarrierTemplate->SetScriptStateInt32(TEXT("Seed"), 23) &&
		bComponentDefaultsReady &&
		State->HealthTemplate->SetScriptStateInt32(TEXT("HitPoints"), 135) &&
		State->MoverTemplate->SetScriptStateInt32(TEXT("Speed"), 8);
	if (!TestTrue(TEXT("Materialized SCS templates retain defaults and authored overrides"),
		bMaterializedState))
	{
		State->Cleanup();
		return false;
	}
	// Script Class gameplay state is exposed as real UE properties. Author the
	// CDO/SCS properties used by Details and managed hydration before saving.
	UObject* ActorDefaults = State->ActorBlueprint->GeneratedClass->GetDefaultObject();
	TestTrue(TEXT("Generated UE properties contain the C# defaults"),
		ReadExposedInt(ActorDefaults, TEXT("Seed")) == 11 &&
		ReadExposedInt(State->HealthTemplate, TEXT("HitPoints")) == 100 &&
		ReadExposedInt(State->MoverTemplate, TEXT("Speed")) == 3);
	const FObjectPropertyBase* PeerProperty = FindFProperty<FObjectPropertyBase>(
		State->MoverTemplate->GetClass(), TEXT("Peer"));
	if (!TestTrue(TEXT("Authors exposed Actor and component overrides"),
		WriteExposedInt(ActorDefaults, TEXT("Seed"), 23) &&
		WriteExposedInt(State->HealthTemplate, TEXT("HitPoints"), 135) &&
		WriteExposedInt(State->MoverTemplate, TEXT("Speed"), 8) && PeerProperty != nullptr))
	{
		State->Cleanup();
		return false;
	}
	PeerProperty->SetObjectPropertyValue_InContainer(State->MoverTemplate, State->HealthTemplate);
	FKismetEditorUtilities::CompileBlueprint(State->HealthBlueprint);
	FKismetEditorUtilities::CompileBlueprint(State->MoverBlueprint);
	FKismetEditorUtilities::CompileBlueprint(State->ActorBlueprint);
	IFileManager::Get().MakeDirectory(*FPaths::GetPath(State->PackageFilename), true);
	IFileManager::Get().MakeDirectory(*FPaths::GetPath(State->HealthPackageFilename), true);
	IFileManager::Get().MakeDirectory(*FPaths::GetPath(State->MoverPackageFilename), true);
	FSavePackageArgs SaveArguments;
	SaveArguments.TopLevelFlags = RF_Public | RF_Standalone;
	SaveArguments.SaveFlags = SAVE_NoError;
	const bool bSaved =
		UPackage::SavePackage(State->HealthPackage, State->HealthBlueprint,
			*State->HealthPackageFilename, SaveArguments) &&
		UPackage::SavePackage(State->MoverPackage, State->MoverBlueprint,
			*State->MoverPackageFilename, SaveArguments) &&
		UPackage::SavePackage(State->Package, State->ActorBlueprint,
			*State->PackageFilename, SaveArguments);
	if (!TestTrue(TEXT("Persistent Actor and component packages save"), bSaved))
	{
		State->Cleanup();
		return false;
	}
	TWeakObjectPtr<UFinareBlueprint> SavedActorBlueprint(State->ActorBlueprint);
	TWeakObjectPtr<UFinareBlueprint> SavedHealthBlueprint(State->HealthBlueprint);
	TWeakObjectPtr<UFinareBlueprint> SavedMoverBlueprint(State->MoverBlueprint);
	State->ActorBlueprint = nullptr;
	State->HealthBlueprint = nullptr;
	State->MoverBlueprint = nullptr;
	State->CarrierAsset = nullptr;
	State->HealthAsset = nullptr;
	State->MoverAsset = nullptr;
	State->CarrierNode = nullptr;
	State->HealthNode = nullptr;
	State->MoverNode = nullptr;
	State->CarrierTemplate = nullptr;
	State->HealthTemplate = nullptr;
	State->MoverTemplate = nullptr;
	State->Package->SetDirtyFlag(false);
	State->HealthPackage->SetDirtyFlag(false);
	State->MoverPackage->SetDirtyFlag(false);
	if (State->Package->IsRooted()) State->Package->RemoveFromRoot();
	if (State->HealthPackage->IsRooted()) State->HealthPackage->RemoveFromRoot();
	if (State->MoverPackage->IsRooted()) State->MoverPackage->RemoveFromRoot();
	TArray<UPackage*> PackagesToUnload{State->Package, State->HealthPackage, State->MoverPackage};
	const bool bUnloaded = UPackageTools::UnloadPackages(PackagesToUnload);
	State->Package = nullptr;
	State->HealthPackage = nullptr;
	State->MoverPackage = nullptr;
	CollectGarbage(RF_NoFlags);
	TestTrue(TEXT("Persistent composition packages unload for save/reopen"), bUnloaded);
	TestFalse(TEXT("Saved Actor Blueprint is released before reopen"), SavedActorBlueprint.IsValid());
	TestFalse(TEXT("Saved Health Blueprint is released before reopen"), SavedHealthBlueprint.IsValid());
	TestFalse(TEXT("Saved Mover Blueprint is released before reopen"), SavedMoverBlueprint.IsValid());
	State->Package = LoadPackage(nullptr, *State->PackageName, LOAD_None);
	State->HealthPackage = LoadPackage(nullptr, *State->HealthPackageName, LOAD_None);
	State->MoverPackage = LoadPackage(nullptr, *State->MoverPackageName, LOAD_None);
	if (State->Package != nullptr) State->Package->AddToRoot();
	if (State->HealthPackage != nullptr) State->HealthPackage->AddToRoot();
	if (State->MoverPackage != nullptr) State->MoverPackage->AddToRoot();
	State->ActorBlueprint = State->Package != nullptr
		? FindObject<UFinareBlueprint>(State->Package, *ActorName) : nullptr;
	State->HealthBlueprint = State->HealthPackage != nullptr
		? FindObject<UFinareBlueprint>(State->HealthPackage, *HealthName) : nullptr;
	State->MoverBlueprint = State->MoverPackage != nullptr
		? FindObject<UFinareBlueprint>(State->MoverPackage, *MoverName) : nullptr;
	State->CarrierAsset = State->Package != nullptr
		? FindObject<UFinareAsset>(State->Package, TEXT("CompositionActorScriptAsset")) : nullptr;
	State->HealthAsset = State->HealthPackage != nullptr
		? FindObject<UFinareAsset>(State->HealthPackage, TEXT("CompositionHealthScriptAsset")) : nullptr;
	State->MoverAsset = State->MoverPackage != nullptr
		? FindObject<UFinareAsset>(State->MoverPackage, TEXT("CompositionMoverScriptAsset")) : nullptr;
	if (!TestNotNull(TEXT("Reopens the persistent Actor Blueprint at its exact path"), State->ActorBlueprint) ||
		!TestNotNull(TEXT("Reopens the persistent Health Blueprint at its exact path"), State->HealthBlueprint) ||
		!TestNotNull(TEXT("Reopens the persistent Mover Blueprint at its exact path"), State->MoverBlueprint) ||
		!TestNotNull(TEXT("Reopens the persistent Actor Script Asset"), State->CarrierAsset) ||
		!TestNotNull(TEXT("Reopens the persistent Health Script Asset"), State->HealthAsset) ||
		!TestNotNull(TEXT("Reopens the persistent Mover Script Asset"), State->MoverAsset))
	{
		State->Cleanup();
		return false;
	}
	TestEqual(TEXT("Reopened Actor generated class remains fixture exact"),
		FSoftClassPath(State->ActorBlueprint->GeneratedClass).ToString(),
		Config->GetStringField(TEXT("actorClassPath")));
	TestEqual(TEXT("Reopened Health generated class remains fixture exact"),
		FSoftClassPath(State->HealthBlueprint->GeneratedClass).ToString(),
		Config->GetStringField(TEXT("healthClassPath")));
	TestEqual(TEXT("Reopened Mover generated class remains fixture exact"),
		FSoftClassPath(State->MoverBlueprint->GeneratedClass).ToString(),
		Config->GetStringField(TEXT("moverClassPath")));
	State->CarrierNode = State->ActorBlueprint->SimpleConstructionScript->FindSCSNode(TEXT("UnifiedScript"));
	State->HealthNode = State->ActorBlueprint->SimpleConstructionScript->FindSCSNode(TEXT("HealthComponent"));
	State->MoverNode = State->ActorBlueprint->SimpleConstructionScript->FindSCSNode(TEXT("MoverComponent"));
	State->CarrierTemplate = State->CarrierNode != nullptr
		? Cast<UFinareComponent>(State->CarrierNode->ComponentTemplate) : nullptr;
	State->HealthTemplate = State->HealthNode != nullptr
		? Cast<UFinareComponent>(State->HealthNode->ComponentTemplate) : nullptr;
	State->MoverTemplate = State->MoverNode != nullptr
		? Cast<UFinareComponent>(State->MoverNode->ComponentTemplate) : nullptr;
	if (!TestTrue(TEXT("Save/reopen preserves the three SCS carriers and authored values"),
		State->CarrierTemplate != nullptr && State->HealthTemplate != nullptr &&
		State->MoverTemplate != nullptr && State->CarrierTemplate->ScriptAsset == State->CarrierAsset &&
		State->HealthTemplate->ScriptAsset == State->HealthAsset &&
		State->MoverTemplate->ScriptAsset == State->MoverAsset &&
		State->CarrierTemplate->GetScriptStateInt32(TEXT("Seed"), -1) == 23 &&
		State->HealthTemplate->GetScriptStateInt32(TEXT("HitPoints"), -1) == 135 &&
		State->MoverTemplate->GetScriptStateInt32(TEXT("Speed"), -1) == 8))
	{
		State->Cleanup();
		return false;
	}
	TestTrue(TEXT("Save/reopen preserves the Health SCS template reference"),
		ReadPeer(State->MoverTemplate) == State->HealthTemplate);
	TestTrue(TEXT("Save/reopen preserves the exposed property overrides"),
		ReadExposedInt(State->ActorBlueprint->GeneratedClass->GetDefaultObject(), TEXT("Seed")) == 23 &&
		ReadExposedInt(State->HealthTemplate, TEXT("HitPoints")) == 135 &&
		ReadExposedInt(State->MoverTemplate, TEXT("Speed")) == 8);

	State->World = UWorld::CreateWorld(EWorldType::Game, false);
	if (!TestNotNull(TEXT("Pure C# workflow creates a runtime World"), State->World))
	{
		State->Cleanup();
		return false;
	}
	State->World->AddToRoot();
	State->World->InitializeActorsForPlay(FURL());
	State->Owner = State->World->SpawnActor<AActor>(State->ActorBlueprint->GeneratedClass);
	TArray<UFinareComponent*> RuntimeComponents;
	if (State->Owner != nullptr)
	{
		State->Owner->GetComponents<UFinareComponent>(RuntimeComponents);
	}
	for (UFinareComponent* Component : RuntimeComponents)
	{
		if (Component == nullptr)
		{
			continue;
		}
		if (Component->ScriptAsset == State->HealthAsset)
		{
			State->Health = Component;
		}
		else if (Component->ScriptAsset == State->MoverAsset)
		{
			State->Mover = Component;
		}
		else if (Component->ScriptAsset == State->CarrierAsset)
		{
			State->Carrier = Component;
		}
	}
	if (!TestNotNull(TEXT("Pure C# workflow spawns the Actor composition"), State->Owner) ||
		!TestNotNull(TEXT("Pure C# workflow finds the Actor carrier instance"), State->Carrier) ||
		!TestNotNull(TEXT("Pure C# workflow finds the Health instance"), State->Health) ||
		!TestNotNull(TEXT("Pure C# workflow finds the Mover instance"), State->Mover))
	{
		State->Cleanup();
		return false;
	}
	if (!TestTrue(
		TEXT("Pure C# workflow carries saved defaults and overrides into runtime instances"),
		State->Carrier->GetScriptStateInt32(TEXT("Seed"), -1) == 23 &&
		State->Health->GetScriptStateInt32(TEXT("HitPoints"), -1) == 135 &&
		State->Mover->GetScriptStateInt32(TEXT("Speed"), -1) == 8))
	{
		State->Cleanup();
		return false;
	}
	UObject* RuntimePeer = ReadPeer(State->Mover);
	TestEqual(TEXT("Runtime reference identifies the Health instance"),
		RuntimePeer, static_cast<UObject*>(State->Health));
	if (!TestTrue(
		TEXT("Pure C# workflow remaps the cross-component reference to this Actor"),
		RuntimePeer == State->Health && RuntimePeer != State->HealthTemplate &&
		State->Health->GetOwner() == State->Owner))
	{
		State->Cleanup();
		return false;
	}
	// A second Actor must get its own mapping. An explicitly assigned live
	// reference to the first Actor must then survive component registration.
	AActor* OtherActor = State->World->SpawnActor<AActor>(State->ActorBlueprint->GeneratedClass);
	UFinareComponent* OtherHealth = nullptr;
	UFinareComponent* OtherMover = nullptr;
	if (OtherActor != nullptr)
	{
		TArray<UFinareComponent*> OtherComponents;
		OtherActor->GetComponents(OtherComponents);
		for (UFinareComponent* Component : OtherComponents)
		{
			if (Component->ScriptAsset == State->HealthAsset) OtherHealth = Component;
			if (Component->ScriptAsset == State->MoverAsset) OtherMover = Component;
		}
	}
	const bool bOtherMapped = OtherHealth != nullptr && OtherMover != nullptr &&
		ReadPeer(OtherMover) == OtherHealth && OtherHealth != State->Health;
	TestTrue(TEXT("Each Actor resolves its own component template reference"), bOtherMapped);
	if (bOtherMapped)
	{
		FindFProperty<FObjectPropertyBase>(OtherMover->GetClass(), TEXT("Peer"))
			->SetObjectPropertyValue_InContainer(OtherMover, State->Health);
		OtherMover->ReregisterComponent();
		TestTrue(TEXT("Registration preserves intentional live references and original templates"),
			ReadPeer(OtherMover) == State->Health && ReadPeer(State->MoverTemplate) == State->HealthTemplate);
	}
	if (OtherActor != nullptr) OtherActor->Destroy();
	if (HasAnyErrors())
	{
		State->Cleanup();
		return false;
	}
	State->Carrier->BeginPlay();
	State->Health->BeginPlay();
	State->Mover->BeginPlay();
	if (!TestTrue(
		TEXT("Pure C# workflow starts the three v1 scripts"),
		State->Carrier->bScriptRunning && State->Health->bScriptRunning &&
		State->Mover->bScriptRunning))
	{
		State->Cleanup();
		return false;
	}
	State->Mover->TickComponent(0.016f, LEVELTICK_All, nullptr);
	TestTrue(TEXT("All three scripts start at generation one"),
		State->Carrier->ActiveGeneration == TEXT("1") && State->Health->ActiveGeneration == TEXT("1") &&
		State->Mover->ActiveGeneration == TEXT("1"));
	if (!TestTrue(TEXT("C# v1 reads the spawned Health through its persisted reference"),
		State->Carrier->ManagedState == TEXT("Actor:v1:23") &&
		State->Health->ManagedState == TEXT("Health:v1:135") &&
		State->Mover->ManagedState == TEXT("Mover:v1:8:Peer=Health:v1:135")))
	{
		State->Cleanup();
		return false;
	}

	FFinareDailyBuildCommandOverride Override;
	Override.Executable = TEXT("powershell.exe");
	Override.Arguments = FString::Printf(
		TEXT("-NoProfile -ExecutionPolicy Bypass -File \"%s\" -Config \"%s\" -Report \"%s\""),
		*State->RevisionBuildScript,
		*State->ConfigPath,
		*State->ReportPath);
	Override.ReportPath = State->ReportPath;
	if (!TestTrue(
		TEXT("Pure C# workflow installs only its Automation Daily Build command override"),
		IFinareEditorModule::Get().SetDailyBuildCommandOverride(Override)))
	{
		State->Cleanup();
		return false;
	}
	State->CompletionHandle = IFinareEditorModule::Get().OnDailyBuildCompleted().AddRaw(
		State.Get(), &FCompositionWorkflowState::OnDailyBuildCompleted);
	State->Watcher = MakeUnique<FFinareSourceWatcher>(
		State->WatcherConfigPath,
		[WeakState = TWeakPtr<FCompositionWorkflowState>(State)]()
		{
			if (TSharedPtr<FCompositionWorkflowState> Pinned = WeakState.Pin())
			{
				IFinareEditorModule::Get().RequestDailyBuild(
					EFinareDailyBuildReason::SourceSave,
					true);
			}
		});
	if (!TestTrue(
		TEXT("Saving the editable revision requests the watcher-backed v2 build"),
		FFileHelper::SaveStringToFile(State->V2Source, *State->SourcePath)))
	{
		State->Cleanup();
		return false;
	}

	const double StartedAt = FPlatformTime::Seconds();
	ADD_LATENT_AUTOMATION_COMMAND(FFunctionLatentCommand([this, State, StartedAt]()
	{
		if (State->CompletionCount == 0)
		{
			if (FPlatformTime::Seconds() - StartedAt < 90.0)
			{
				return false;
			}
			AddError(TEXT("Timed out waiting for the watcher-backed v2 Daily Build receipt."));
			State->bWorkflowFailed = true;
			return true;
		}
		TestEqual(TEXT("Watcher-backed v2 build publishes one receipt"), State->CompletionCount, 1);
		TestTrue(TEXT("Watcher-backed v2 build succeeds"), State->LastResult.IsSuccess());
		TestEqual(TEXT("Watcher-backed v2 build reloads all three scripts"),
			State->LastResult.ReloadedComponents, 3);
		State->Mover->TickComponent(0.016f, LEVELTICK_All, nullptr);
		TestTrue(TEXT("The successful save advances all three generation identities"),
			State->Carrier->ActiveGeneration == TEXT("2") && State->Health->ActiveGeneration == TEXT("2") &&
			State->Mover->ActiveGeneration == TEXT("2"));
		TestTrue(TEXT("Watcher-backed v2 generation is observable on all components"),
			State->Carrier->ManagedState == TEXT("Actor:v2:23") &&
			State->Health->ManagedState == TEXT("Health:v2:135") &&
			State->Mover->ManagedState == TEXT("Mover:v2:8:Peer=Health:v2:135"));
		State->bWorkflowFailed = HasAnyErrors();
		return true;
	}));

	ADD_LATENT_AUTOMATION_COMMAND(FFunctionLatentCommand([this, State]()
	{
		if (State->bWorkflowFailed) return true;
		AddExpectedError(TEXT("Composition mover candidate rejected"),
			EAutomationExpectedErrorFlags::Contains, 1);
		State->BadBuildStartedAt = FPlatformTime::Seconds();
		if (!TestTrue(TEXT("Saves the failing C# revision"),
			FFileHelper::SaveStringToFile(State->BadSource, *State->SourcePath)))
		{
			State->bWorkflowFailed = true;
		}
		return true;
	}));

	ADD_LATENT_AUTOMATION_COMMAND(FFunctionLatentCommand([this, State]()
	{
		if (State->bWorkflowFailed) return true;
		if (State->CompletionCount < 2)
		{
			if (FPlatformTime::Seconds() - State->BadBuildStartedAt < 90.0)
			{
				return false;
			}
			AddError(TEXT("Timed out waiting for the watcher-backed bad Daily Build receipt."));
			return true;
		}
		TestEqual(TEXT("Bad watcher-backed build still matches all three scripts"),
			State->LastResult.MatchedComponents, 3);
		TestFalse(TEXT("Bad watcher-backed build fails the shared reload barrier"),
			State->LastResult.IsSuccess());
		TestEqual(TEXT("Bad watcher-backed build commits no component"),
			State->LastResult.ReloadedComponents, 0);
		TestTrue(TEXT("The rejected save retains every previous generation identity"),
			State->Carrier->ActiveGeneration == TEXT("2") && State->Health->ActiveGeneration == TEXT("2") &&
			State->Mover->ActiveGeneration == TEXT("2"));
		State->Mover->TickComponent(0.016f, LEVELTICK_All, nullptr);
		TestTrue(TEXT("Bad watcher-backed build retains the v2 component state"),
			State->Carrier->ManagedState == TEXT("Actor:v2:23") &&
			State->Health->ManagedState == TEXT("Health:v2:135") &&
			State->Mover->ManagedState == TEXT("Mover:v2:8:Peer=Health:v2:135") &&
			State->Carrier->bScriptRunning && State->Health->bScriptRunning && State->Mover->bScriptRunning);
		return true;
	}));

	ADD_LATENT_AUTOMATION_COMMAND(FFunctionLatentCommand([State]()
	{
		State->Cleanup();
		return true;
	}));
	return true;
}

#endif
