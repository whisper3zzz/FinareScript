#if WITH_DEV_AUTOMATION_TESTS

#include "CoreMinimal.h"

#include "Engine/Blueprint.h"
#include "Engine/BlueprintGeneratedClass.h"
#include "Engine/Engine.h"
#include "Engine/World.h"
#include "FinareActor.h"
#include "FinareAsset.h"
#include "FinareAuthoringWorkbench.h"
#include "FinareBlueprint.h"
#include "FinareBlueprintClassMaterializer.h"
#include "FinareComponent.h"
#include "FinareCoreClrHost.h"
#include "FinareWorldSubsystem.h"
#include "GameFramework/Actor.h"
#include "Kismet2/KismetEditorUtilities.h"
#include "Misc/AutomationTest.h"
#include "Misc/FileHelper.h"
#include "Misc/Paths.h"
#include "Misc/ScopeExit.h"
#include "UObject/ObjectKey.h"
#include "UObject/Package.h"
#include "UObject/SavePackage.h"
#include "UObject/StrongObjectPtr.h"
#include "UObject/UnrealType.h"

namespace UE::Finare::NativePublicationWorldTests
{
	struct FFixtureAsset
	{
		FString Id;
		FString QualifiedName;
		FString AssetPath;
	};

	struct FWorldFixture
	{
		UWorld* World = nullptr;
		FWorldContext* Context = nullptr;
		AFinareActor* Actor = nullptr;
		UFinareComponent* Component = nullptr;
		UFinareWorldSubsystem* Host = nullptr;
	};

	bool LoadFixture(
		FString& OutDeclaration,
		FString& OutChunkPath,
		TArray<FFixtureAsset>& OutAssets,
		FString& OutError)
	{
		const FString FixtureRoot = FPaths::ConvertRelativePathToFull(FPaths::Combine(
			FPaths::ProjectDir(),
			TEXT("../artifacts/finare-usability/c4-publication-world-fixture")));
		const FString DeclarationPath = FPaths::Combine(
			FixtureRoot,
			TEXT("c4-publication-world-fixture.asset.json"));
		OutChunkPath = FPaths::Combine(
			FixtureRoot,
			TEXT("c4-publication-world-fixture.uschunk"));
		if (!FFileHelper::LoadFileToString(OutDeclaration, *DeclarationPath) ||
			OutDeclaration.IsEmpty() ||
			!IFileManager::Get().FileExists(*OutChunkPath))
		{
			OutError = FString::Printf(
				TEXT("C4 publication fixture is missing declaration or chunk: %s."),
				*FixtureRoot);
			return false;
		}

		TSharedPtr<FJsonObject> Root;
		const TSharedRef<TJsonReader<>> Reader = TJsonReaderFactory<>::Create(OutDeclaration);
		const TArray<TSharedPtr<FJsonValue>>* Assets = nullptr;
		if (!FJsonSerializer::Deserialize(Reader, Root) || !Root.IsValid() ||
			!Root->TryGetArrayField(TEXT("assets"), Assets) || Assets == nullptr)
		{
			OutError = TEXT("C4 publication fixture declaration is invalid JSON.");
			return false;
		}
		for (const TSharedPtr<FJsonValue>& Value : *Assets)
		{
			const TSharedPtr<FJsonObject> Asset = Value.IsValid() ? Value->AsObject() : nullptr;
			FFixtureAsset Parsed;
			if (Asset.IsValid() &&
				Asset->TryGetStringField(TEXT("id"), Parsed.Id) &&
				Asset->TryGetStringField(TEXT("qualifiedName"), Parsed.QualifiedName) &&
				Asset->TryGetStringField(TEXT("assetPath"), Parsed.AssetPath))
			{
				OutAssets.Add(MoveTemp(Parsed));
			}
		}
		if (OutAssets.Num() != 2)
		{
			OutError = FString::Printf(
				TEXT("C4 publication fixture expected two independent managed assets, got %d."),
				OutAssets.Num());
			return false;
		}
		return true;
	}

	bool MakeWorld(FWorldFixture& Out, FString& OutError)
	{
		Out.World = UWorld::CreateWorld(EWorldType::Game, false);
		if (Out.World == nullptr || GEngine == nullptr)
		{
			OutError = TEXT("Could not create C4 publication World.");
			return false;
		}
		Out.World->AddToRoot();
		Out.Context = &GEngine->CreateNewWorldContext(EWorldType::Game);
		Out.Context->SetCurrentWorld(Out.World);
		Out.World->InitializeActorsForPlay(FURL());
		Out.Host = Out.World->GetSubsystem<UFinareWorldSubsystem>();
		if (Out.Host == nullptr)
		{
			OutError = TEXT("C4 publication World has no managed host subsystem.");
			return false;
		}
		return true;
	}

	void DestroyWorld(FWorldFixture& Fixture)
	{
		if (Fixture.Actor != nullptr && !Fixture.Actor->IsActorBeingDestroyed())
		{
			if (Fixture.Actor->HasActorBegunPlay())
			{
				Fixture.Actor->RouteEndPlay(EEndPlayReason::Destroyed);
			}
			Fixture.Actor->Destroy();
		}
		Fixture.Actor = nullptr;
		Fixture.Component = nullptr;
		Fixture.Host = nullptr;
		if (Fixture.Context != nullptr && GEngine != nullptr)
		{
			GEngine->DestroyWorldContext(Fixture.World);
			Fixture.Context = nullptr;
		}
		if (Fixture.World != nullptr)
		{
			Fixture.World->RemoveFromRoot();
			Fixture.World->DestroyWorld(false);
			Fixture.World = nullptr;
		}
	}

	UFinareBlueprint* CreateManagedBlueprint(
		UClass* Parent,
		const FString& PackageName,
		const FString& Name,
		UPackage*& OutPackage)
	{
		OutPackage = CreatePackage(*PackageName);
		return OutPackage != nullptr
			       ? Cast<UFinareBlueprint>(FKismetEditorUtilities::CreateBlueprint(
				       Parent,
				       OutPackage,
				       FName(*Name),
				       BPTYPE_Normal,
				       UFinareBlueprint::StaticClass(),
				       UFinareBlueprintGeneratedClass::StaticClass(),
				       FName(TEXT("FinareNativePublicationWorlds"))))
			       : nullptr;
	}

	UFinareAsset* ConfigureScriptAsset(
		UFinareBlueprint& Blueprint,
		UPackage& Package,
		const FFixtureAsset& FixtureAsset,
		const FString& ChunkPath,
		const FString& DeclarationPath)
	{
		UFinareAsset* ScriptAsset = NewObject<UFinareAsset>(
			&Package,
			TEXT("ScriptAsset"),
			RF_Public | RF_Standalone);
		if (ScriptAsset == nullptr || Blueprint.GeneratedClass == nullptr)
		{
			return nullptr;
		}
		FString ClassIdText = FixtureAsset.Id;
		ClassIdText.RemoveFromStart(TEXT("us_"));
		if (!FGuid::ParseExact(ClassIdText, EGuidFormats::Digits, ScriptAsset->ScriptClassId))
		{
			return nullptr;
		}
		ScriptAsset->ScriptClassKind = EFinareClassKind::Actor;
		ScriptAsset->GeneratedClassPath = FSoftClassPath(Blueprint.GeneratedClass);
		ScriptAsset->CompiledChunkPath = ChunkPath;
		ScriptAsset->ScriptTypeName = FixtureAsset.QualifiedName;
		ScriptAsset->DeclarationPath = DeclarationPath;
		ScriptAsset->DeclarationAssetPath = FixtureAsset.AssetPath;
		Blueprint.ScriptAsset = ScriptAsset;
		return ScriptAsset;
	}

	bool SaveObjectPackage(UObject& Object, const FString& Filename, FString& OutError)
	{
		FSavePackageArgs Args;
		Args.TopLevelFlags = RF_Public | RF_Standalone;
		Args.SaveFlags = SAVE_NoError;
		if (!UPackage::SavePackage(Object.GetOutermost(), &Object, *Filename, Args))
		{
			OutError = FString::Printf(TEXT("Could not save C4 publication package '%s'."), *Filename);
			return false;
		}
		return true;
	}

	bool ReadBytes(const FString& Path, TArray<uint8>& OutBytes)
	{
		return FFileHelper::LoadFileToArray(OutBytes, *Path);
	}

	FString BuildCandidate(const FString& Declaration, int32 CountDefault)
	{
		FString Candidate = Declaration;
		Candidate.ReplaceInline(
			TEXT("\"defaultValue\": 1"),
			*FString::Printf(TEXT("\"defaultValue\": %d"), CountDefault),
			ESearchCase::CaseSensitive);
		return Candidate;
	}

	FFinareNativePublicationPrepareResult BuildPrepare(
		const FString& Declaration,
		const FFixtureAsset& Owner,
		const FString& TransactionId,
		const FString& ExternalPath,
		int32 CountDefault)
	{
		FFinareNativePublicationPrepareResult Prepare;
		Prepare.SchemaVersion = TEXT("unifiedscript.native-publication-prepare/v0.1");
		Prepare.TransactionId = TransactionId;
		Prepare.PreviewToken = TEXT("c4-publication-world-preview-token");
		Prepare.BaselineRevision = 1;
		Prepare.CandidateRevision = 2;
		Prepare.bPublishesRuntime = true;
		Prepare.HistoryEntryTransactionId = TransactionId;
		Prepare.HistoryDirection = TEXT("commit");
		Prepare.ScriptAssetsJson = BuildCandidate(Declaration, CountDefault);

		FFinareNativeBindingChange Binding;
		Binding.AssetPath = Owner.AssetPath;
		Binding.AssetId = Owner.Id;
		FFinareClassBindingDeclaration Before;
		Before.Kind = TEXT("Actor");
		FFinareClassBindingDeclaration After = Before;
		Binding.Before = Before;
		Binding.After = After;
		Prepare.BindingChanges.Add(MoveTemp(Binding));

		FFinareNativePublicationOutput Output;
		Output.Path = ExternalPath;
		Output.bBaselineExists = false;
		Output.BaselineDigest = TEXT("sha256:e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855");
		Output.CandidateDigest = Output.BaselineDigest;
		Prepare.Outputs.Add(MoveTemp(Output));
		return Prepare;
	}

	IMPLEMENT_SIMPLE_AUTOMATION_TEST(
		FFinareNativePublicationWorldsTest,
		"Finare.Unreal.Editor.Authoring.NativePublicationWorlds",
		EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

	bool FFinareNativePublicationWorldsTest::RunTest(const FString& Parameters)
	{
		(void)Parameters;
		FString Declaration;
		FString ChunkPath;
		TArray<FFixtureAsset> Assets;
		FString Error;
		if (!TestTrue(
			TEXT("Loads the generated C4 publication World fixture"),
			LoadFixture(Declaration, ChunkPath, Assets, Error)))
		{
			AddError(Error);
			return false;
		}
		const FFixtureAsset* OwnerFixture = Assets.FindByPredicate(
			[](const FFixtureAsset& Asset)
			{
				return Asset.AssetPath == TEXT("Game/C4PublicationOwner");
			});
		const FFixtureAsset* DescendantFixture = Assets.FindByPredicate(
			[](const FFixtureAsset& Asset)
			{
				return Asset.AssetPath == TEXT("Game/C4PublicationDescendant");
			});
		if (!TestNotNull(TEXT("Finds the emitted owner asset by its stable asset path"), OwnerFixture) ||
			!TestNotNull(TEXT("Finds the emitted descendant asset by its stable asset path"), DescendantFixture))
		{
			return false;
		}
		const FString DeclarationPath = FPaths::ConvertRelativePathToFull(FPaths::Combine(
			FPaths::ProjectDir(),
			TEXT(
				"../artifacts/finare-usability/c4-publication-world-fixture/c4-publication-world-fixture.asset.json")));
		TArray<uint8> DeclarationBytesBefore;
		TArray<uint8> ChunkBytesBefore;
		if (!TestTrue(
				TEXT("Snapshots the fixture declaration bytes"), ReadBytes(DeclarationPath, DeclarationBytesBefore)) ||
			!TestTrue(TEXT("Snapshots the fixture chunk bytes"), ReadBytes(ChunkPath, ChunkBytesBefore)))
		{
			return false;
		}

		// These paths are part of the emitted fixture's state contract. The
		// synchronizer intentionally ignores state when GeneratedClassPath is
		// omitted, so the native Blueprint packages must use the same stable
		// generated class paths as the C# fixture.
		const FString Name = TEXT("C4PublicationOwner");
		const FString RootPackageName = TEXT("/Game/Temp/C4PublicationOwner");
		const FString DescendantPackageName = TEXT("/Game/Temp/C4PublicationDescendant");
		UPackage* RootPackage = nullptr;
		UPackage* DescendantPackage = nullptr;
		UFinareBlueprint* OwnerBlueprint = CreateManagedBlueprint(
			AFinareActor::StaticClass(), RootPackageName, Name, RootPackage);
		UFinareBlueprint* DescendantBlueprint = OwnerBlueprint != nullptr
			                                        ? CreateManagedBlueprint(
				                                        OwnerBlueprint->GeneratedClass,
				                                        DescendantPackageName,
				                                        TEXT("C4PublicationDescendant"),
				                                        DescendantPackage)
			                                        : nullptr;
		UFinareAsset* OwnerAsset = OwnerBlueprint != nullptr && RootPackage != nullptr
			                           ? ConfigureScriptAsset(*OwnerBlueprint, *RootPackage, *OwnerFixture, ChunkPath,
			                                                  DeclarationPath)
			                           : nullptr;
		UFinareAsset* DescendantAsset = DescendantBlueprint != nullptr && DescendantPackage != nullptr
			                                ? ConfigureScriptAsset(*DescendantBlueprint, *DescendantPackage,
			                                                       *DescendantFixture, ChunkPath, DeclarationPath)
			                                : nullptr;
		if (!TestNotNull(TEXT("Creates the managed owner Blueprint"), OwnerBlueprint) ||
			!TestNotNull(TEXT("Creates the independent managed descendant Blueprint"), DescendantBlueprint) ||
			!TestNotNull(TEXT("Creates the owner ScriptAsset"), OwnerAsset) ||
			!TestNotNull(TEXT("Creates the independent descendant ScriptAsset"), DescendantAsset))
		{
			return false;
		}
		TestTrue(
			TEXT("Descendant uses the owner generated class as its UE parent"),
			DescendantBlueprint->ParentClass == OwnerBlueprint->GeneratedClass);
		TestTrue(
			TEXT("Descendant has its own generated class and ScriptClassId"),
			DescendantBlueprint->GeneratedClass != OwnerBlueprint->GeneratedClass &&
			DescendantAsset->ScriptClassId != OwnerAsset->ScriptClassId);

		FFinareBlueprintClassSyncResult SyncResult;
		if (!TestTrue(
				TEXT("Materializes the owner fixture declaration"),
				FFinareBlueprintClassMaterializer::SynchronizeJson(
					*OwnerAsset, *OwnerBlueprint, Declaration, false, SyncResult, Error)) ||
			!TestTrue(
				TEXT("Materializes the independent descendant fixture declaration"),
				FFinareBlueprintClassMaterializer::SynchronizeJson(
					*DescendantAsset, *DescendantBlueprint, Declaration, false, SyncResult, Error)))
		{
			AddError(Error);
			return false;
		}
		AFinareActor* OwnerCdo = OwnerBlueprint->GeneratedClass->GetDefaultObject<AFinareActor>();
		AFinareActor* DescendantCdo = DescendantBlueprint->GeneratedClass->GetDefaultObject<AFinareActor>();
		if (!TestNotNull(TEXT("Owner CDO exists after materialization"), OwnerCdo) ||
			!TestNotNull(TEXT("Descendant CDO exists after materialization"), DescendantCdo) ||
			!TestNotNull(TEXT("Owner CDO keeps its ScriptAsset"), OwnerCdo->GetFinareComponent()) ||
			!TestNotNull(TEXT("Descendant CDO keeps its ScriptAsset"), DescendantCdo->GetFinareComponent()))
		{
			return false;
		}
		OwnerCdo->GetFinareComponent()->bStartAutomatically = true;
		OwnerCdo->GetFinareComponent()->bTickAutomatically = true;
		OwnerCdo->GetFinareComponent()->ScriptAsset = OwnerAsset;
		DescendantCdo->GetFinareComponent()->bStartAutomatically = true;
		DescendantCdo->GetFinareComponent()->bTickAutomatically = true;
		DescendantCdo->GetFinareComponent()->ScriptAsset = DescendantAsset;
		TestTrue(
			TEXT("Materializer records exact owner metadata on both generated classes"),
			Cast<UFinareBlueprintGeneratedClass>(OwnerBlueprint->GeneratedClass)->ManagedScriptAsset == OwnerAsset &&
			Cast<UFinareBlueprintGeneratedClass>(DescendantBlueprint->GeneratedClass)->ManagedScriptAsset ==
			DescendantAsset);

		const FString OwnerPackageFilename = FPackageName::LongPackageNameToFilename(
			RootPackageName, FPackageName::GetAssetPackageExtension());
		const FString DescendantPackageFilename = FPackageName::LongPackageNameToFilename(
			DescendantPackageName, FPackageName::GetAssetPackageExtension());
		if (!TestTrue(
				TEXT("Saves the owner package baseline"),
				SaveObjectPackage(*OwnerBlueprint, OwnerPackageFilename, Error)) ||
			!TestTrue(TEXT("Saves the descendant package baseline"), SaveObjectPackage(*DescendantBlueprint,
				          DescendantPackageFilename, Error)))
		{
			AddError(Error);
			return false;
		}
		TArray<uint8> OwnerPackageBytesBeforeReject;
		TArray<uint8> DescendantPackageBytesBeforeReject;
		ReadBytes(OwnerPackageFilename, OwnerPackageBytesBeforeReject);
		ReadBytes(DescendantPackageFilename, DescendantPackageBytesBeforeReject);

		const int32 InitialWorldCount = FFinareCoreClrHost::Get().GetManagedWorldCount();
		FWorldFixture OwnerWorld;
		FWorldFixture DescendantWorld;
		FString WorldError;
		ON_SCOPE_EXIT
		{
			DestroyWorld(OwnerWorld);
			DestroyWorld(DescendantWorld);
			if (IFileManager::Get().FileExists(*OwnerPackageFilename))
			{
				IFileManager::Get().Delete(*OwnerPackageFilename, false, true, true);
			}
			if (IFileManager::Get().FileExists(*DescendantPackageFilename))
			{
				IFileManager::Get().Delete(*DescendantPackageFilename, false, true, true);
			}
			CollectGarbage(RF_NoFlags);
			TestEqual(
				TEXT("C4 publication World teardown restores managed World count"),
				FFinareCoreClrHost::Get().GetManagedWorldCount(),
				InitialWorldCount);
		};
		if (!TestTrue(TEXT("Creates the owner World"), MakeWorld(OwnerWorld, WorldError)) ||
			!TestTrue(TEXT("Creates the descendant World"), MakeWorld(DescendantWorld, WorldError)))
		{
			AddError(WorldError);
			return false;
		}
		OwnerWorld.Actor = OwnerWorld.World->SpawnActor<AFinareActor>(OwnerBlueprint->GeneratedClass);
		DescendantWorld.Actor = DescendantWorld.World->SpawnActor<AFinareActor>(DescendantBlueprint->GeneratedClass);
		if (!TestNotNull(TEXT("Spawns the owner actor"), OwnerWorld.Actor) ||
			!TestNotNull(TEXT("Spawns the descendant actor"), DescendantWorld.Actor))
		{
			return false;
		}
		OwnerWorld.Component = OwnerWorld.Actor->GetFinareComponent();
		DescendantWorld.Component = DescendantWorld.Actor->GetFinareComponent();
		if (!TestNotNull(TEXT("Owner actor has its managed component"), OwnerWorld.Component) ||
			!TestNotNull(TEXT("Descendant actor has its managed component"), DescendantWorld.Component))
		{
			return false;
		}
		OwnerWorld.Actor->DispatchBeginPlay();
		DescendantWorld.Actor->DispatchBeginPlay();
		if (!TestTrue(TEXT("Owner World starts the real managed script"), OwnerWorld.Component->bScriptRunning) ||
			!TestTrue(
				TEXT("Descendant World starts the real independent managed script"),
				DescendantWorld.Component->bScriptRunning))
		{
			return false;
		}
		FIntProperty* OwnerCount = FindFProperty<FIntProperty>(OwnerWorld.Actor->GetClass(), TEXT("Count"));
		FIntProperty* DescendantCount = FindFProperty<FIntProperty>(DescendantWorld.Actor->GetClass(),
		                                                            TEXT("DescCount"));
		if (!TestNotNull(TEXT("Owner actor consumes its Count state"), OwnerCount) ||
			!TestNotNull(TEXT("Descendant actor consumes its independent DescCount state"), DescendantCount))
		{
			return false;
		}
		TestEqual(TEXT("Owner OnBeginPlay changes Count"), OwnerCount->GetPropertyValue_InContainer(OwnerWorld.Actor),
		          101);
		TestEqual(
			TEXT("Descendant OnBeginPlay changes DescCount"),
			DescendantCount->GetPropertyValue_InContainer(DescendantWorld.Actor), 202);
		OwnerWorld.Host->BeginManagedFrame();
		OwnerWorld.Component->TickComponent(0.016f, LEVELTICK_All, nullptr);
		OwnerWorld.Host->EndManagedFrame();
		DescendantWorld.Host->BeginManagedFrame();
		DescendantWorld.Component->TickComponent(0.016f, LEVELTICK_All, nullptr);
		DescendantWorld.Host->EndManagedFrame();
		TestEqual(TEXT("Owner OnTick changes Count"), OwnerCount->GetPropertyValue_InContainer(OwnerWorld.Actor), 102);
		TestEqual(
			TEXT("Descendant OnTick changes DescCount"),
			DescendantCount->GetPropertyValue_InContainer(DescendantWorld.Actor), 204);
		// Seed an explicit inherited owner-state override on the descendant
		// actor. This is a native typed-state preservation check, independent of
		// the managed lifecycle values above.
		FIntProperty* DescendantInheritedCount = FindFProperty<FIntProperty>(
			DescendantWorld.Actor->GetClass(),
			TEXT("Count"));
		if (!TestNotNull(TEXT("Descendant actor exposes inherited Count state"), DescendantInheritedCount))
		{
			return false;
		}
		DescendantInheritedCount->SetPropertyValue_InContainer(DescendantWorld.Actor, 150);
		TestEqual(
			TEXT("Descendant keeps its explicit inherited Count override before publication"),
			DescendantInheritedCount->GetPropertyValue_InContainer(DescendantWorld.Actor),
			150);

		FFinareRuntimeDebugContext OwnerContextBeforeReject;
		FFinareRuntimeDebugContext DescendantContextBeforeReject;
		TestTrue(
			TEXT("Owner exposes a runtime identity before rejection"),
			OwnerWorld.Component->GetRuntimeDebugContext(OwnerContextBeforeReject));
		TestTrue(
			TEXT("Descendant exposes a runtime identity before rejection"),
			DescendantWorld.Component->GetRuntimeDebugContext(DescendantContextBeforeReject));

		const FString ExternalRoot = FPaths::ConvertRelativePathToFull(FPaths::Combine(
			FPaths::ProjectSavedDir(), TEXT("Finare/C4PublicationWorlds")));
		IFileManager::Get().MakeDirectory(*ExternalRoot, true);
		const FString PreviewToken = TEXT("c4-publication-world-preview-token");
		FFinareAuthoringWorkbench::FNativePublicationContext Context;
		Context.ScriptAsset = OwnerAsset;
		Context.Blueprint = OwnerBlueprint;
		Context.GeneratedClass = OwnerBlueprint->GeneratedClass;
		Context.ScriptAssetObjectPath = OwnerAsset->GetPathName();
		Context.ScriptClassId = OwnerAsset->ScriptClassId;
		Context.ScriptTypeName = OwnerAsset->ScriptTypeName;
		Context.GeneratedClassPath = OwnerAsset->GeneratedClassPath.ToString();
		Context.GeneratedOwnerPath = OwnerBlueprint->GetPathName();
		Context.NativeParentClassPath = OwnerBlueprint->ParentClass->GetPathName();
		Context.bHasExpectedPreviewRevisions = true;
		Context.ExpectedBaselineRevision = 1;
		Context.ExpectedCandidateRevision = 2;

		TMap<FObjectKey, FObjectKey> ReinstancedKeys;
		TMap<FObjectKey, TStrongObjectPtr<UObject>> ReinstancedTargets;
		const FDelegateHandle ReinstancedHandle = FCoreUObjectDelegates::OnObjectsReinstanced.AddLambda(
			[&ReinstancedKeys, &ReinstancedTargets](const FCoreUObjectDelegates::FReplacementObjectMap& ReplacementMap)
			{
				for (const TPair<UObject*, UObject*>& Pair : ReplacementMap)
				{
					if (Pair.Key == nullptr)
					{
						continue;
					}
					const FObjectKey OldKey(Pair.Key);
					ReinstancedKeys.Add(OldKey, Pair.Value != nullptr ? FObjectKey(Pair.Value) : FObjectKey());
					if (Pair.Value != nullptr)
					{
						ReinstancedTargets.Add(OldKey, TStrongObjectPtr<UObject>(Pair.Value));
					}
				}
			});
		ON_SCOPE_EXIT
		{
			FCoreUObjectDelegates::OnObjectsReinstanced.Remove(ReinstancedHandle);
		};
		const FObjectKey OwnerActorKey(OwnerWorld.Actor);
		const FObjectKey DescendantActorKey(DescendantWorld.Actor);
		auto ResolveReinstanced = [&](const FObjectKey& OriginalKey) -> UObject*
		{
			TSet<FObjectKey> Visited;
			FObjectKey Current = OriginalKey;
			while (Current != FObjectKey() && !Visited.Contains(Current))
			{
				Visited.Add(Current);
				const FObjectKey* Next = ReinstancedKeys.Find(Current);
				if (Next == nullptr)
				{
					UObject* Result = Current.ResolveObjectPtr();
					return IsValid(Result) ? Result : nullptr;
				}
				if (*Next == FObjectKey())
				{
					return nullptr;
				}
				Current = *Next;
			}
			return nullptr;
		};
		auto CurrentOwnerActor = [&]() -> AFinareActor*
		{
			return Cast<AFinareActor>(ResolveReinstanced(OwnerActorKey));
		};
		auto CurrentDescendantActor = [&]() -> AFinareActor*
		{
			return Cast<AFinareActor>(ResolveReinstanced(DescendantActorKey));
		};
		auto CheckRejectedState = [&](const TCHAR* Label, const FString& RejectionError, bool bOwnerActive) -> bool
		{
			bool bStable = TestFalse(
				*FString::Printf(TEXT("%s rejects the native publication"), Label),
				RejectionError.IsEmpty());
			bStable &= TestTrue(
				*FString::Printf(TEXT("%s reports the active managed instance"), Label),
				RejectionError.Contains(TEXT("while a managed instance is active")));
			bStable &= TestTrue(
				*FString::Printf(TEXT("%s leaves the fixture declaration unchanged"), Label),
				[&]()
				{
					TArray<uint8> Current;
					return ReadBytes(DeclarationPath, Current) && Current == DeclarationBytesBefore;
				}());
			bStable &= TestTrue(
				*FString::Printf(TEXT("%s leaves the fixture chunk unchanged"), Label),
				[&]()
				{
					TArray<uint8> Current;
					return ReadBytes(ChunkPath, Current) && Current == ChunkBytesBefore;
				}());
			bStable &= TestTrue(
				*FString::Printf(TEXT("%s leaves owner and descendant packages unchanged"), Label),
				[&]()
				{
					TArray<uint8> CurrentOwner;
					TArray<uint8> CurrentDescendant;
					return ReadBytes(OwnerPackageFilename, CurrentOwner) &&
						ReadBytes(DescendantPackageFilename, CurrentDescendant) &&
						CurrentOwner == OwnerPackageBytesBeforeReject &&
						CurrentDescendant == DescendantPackageBytesBeforeReject;
				}());
			AFinareActor* CurrentOwner = CurrentOwnerActor();
			AFinareActor* CurrentDescendant = CurrentDescendantActor();
			bStable &= TestNotNull(*FString::Printf(TEXT("%s preserves owner actor"), Label), CurrentOwner);
			bStable &= TestNotNull(*FString::Printf(TEXT("%s preserves descendant actor"), Label), CurrentDescendant);
			if (CurrentOwner != nullptr && CurrentDescendant != nullptr)
			{
				UFinareComponent* CurrentOwnerComponent = CurrentOwner->GetFinareComponent();
				UFinareComponent* CurrentDescendantComponent = CurrentDescendant->GetFinareComponent();
				bStable &= TestTrue(
					*FString::Printf(TEXT("%s preserves owner runtime state"), Label),
					CurrentOwnerComponent != nullptr && CurrentOwnerComponent->bScriptRunning == bOwnerActive);
				bStable &= TestTrue(
					*FString::Printf(TEXT("%s preserves descendant runtime state"), Label),
					CurrentDescendantComponent != nullptr && CurrentDescendantComponent->bScriptRunning);
				bStable &= TestTrue(
					*FString::Printf(TEXT("%s preserves owner and descendant ScriptAsset references"), Label),
					CurrentOwnerComponent != nullptr && CurrentOwnerComponent->ScriptAsset == OwnerAsset &&
					CurrentDescendantComponent != nullptr && CurrentDescendantComponent->ScriptAsset ==
					DescendantAsset);
			}
			return bStable;
		};

		const FString RejectBothId = FGuid::NewGuid().ToString(EGuidFormats::DigitsWithHyphensLower);
		const FString RejectBothExternal = FPaths::Combine(ExternalRoot, RejectBothId + TEXT(".bin"));
		FFinareNativePublicationPrepareResult RejectBoth = BuildPrepare(
			Declaration, *OwnerFixture, RejectBothId, RejectBothExternal, 41);
		FString ApplyError;
		TestFalse(
			TEXT("ApplyNativePublication rejects the owner while owner and managed descendant Worlds are active"),
			FFinareAuthoringWorkbench::ApplyNativePublication(Context, RejectBoth, PreviewToken, ApplyError));
		TestTrue(
			TEXT("Both-active rejection leaves no candidate output"),
			!IFileManager::Get().FileExists(*RejectBothExternal));
		TestTrue(TEXT("Both-active rejection leaves no history"), !IFileManager::Get().FileExists(*FPaths::Combine(
			         FPaths::ProjectSavedDir(), TEXT("Finare/ClassOperations/NativeHistory"),
			         RejectBothId + TEXT(".json"))));
		TestTrue(TEXT("Both-active rejection leaves no completion marker"), !IFileManager::Get().FileExists(
			         *FPaths::Combine(
				         FPaths::ProjectSavedDir(), TEXT("Finare/ClassOperations/NativeHistory"),
				         RejectBothId + TEXT(".completed.json"))));
		CheckRejectedState(TEXT("Both-active native publication"), ApplyError, true);
		FFinareRuntimeDebugContext OwnerContextAfterReject;
		FFinareRuntimeDebugContext DescendantContextAfterReject;
		TestTrue(
			TEXT("Both-active rejection preserves owner runtime identity"),
			OwnerWorld.Component->GetRuntimeDebugContext(OwnerContextAfterReject));
		TestTrue(
			TEXT("Both-active rejection preserves descendant runtime identity"),
			DescendantWorld.Component->GetRuntimeDebugContext(DescendantContextAfterReject));
		TestEqual(TEXT("Both-active rejection preserves owner generation"), OwnerContextAfterReject.Generation,
		          OwnerContextBeforeReject.Generation);
		TestEqual(
			TEXT("Both-active rejection preserves descendant generation"), DescendantContextAfterReject.Generation,
			DescendantContextBeforeReject.Generation);

		OwnerWorld.Component->StopScript();
		TestFalse(TEXT("Stopping owner leaves owner managed script stopped"), OwnerWorld.Component->bScriptRunning);
		DescendantWorld.Host->BeginManagedFrame();
		DescendantWorld.Component->TickComponent(0.016f, LEVELTICK_All, nullptr);
		DescendantWorld.Host->EndManagedFrame();
		TestEqual(TEXT("Descendant remains consumable after owner stops"),
		          FindFProperty<FIntProperty>(DescendantWorld.Actor->GetClass(), TEXT("DescCount"))->
		          GetPropertyValue_InContainer(DescendantWorld.Actor),
		          206);

		const FString RejectDescendantId = FGuid::NewGuid().ToString(EGuidFormats::DigitsWithHyphensLower);
		const FString RejectDescendantExternal = FPaths::Combine(ExternalRoot, RejectDescendantId + TEXT(".bin"));
		FFinareNativePublicationPrepareResult RejectDescendant = BuildPrepare(
			Declaration, *OwnerFixture, RejectDescendantId, RejectDescendantExternal, 43);
		ApplyError.Reset();
		TestFalse(
			TEXT("ApplyNativePublication rejects the owner while only the managed descendant World is active"),
			FFinareAuthoringWorkbench::ApplyNativePublication(Context, RejectDescendant, PreviewToken, ApplyError));
		TestTrue(
			TEXT("Descendant-active rejection leaves no candidate output"),
			!IFileManager::Get().FileExists(*RejectDescendantExternal));
		CheckRejectedState(TEXT("Descendant-active native publication"), ApplyError, false);

		DescendantWorld.Component->StopScript();
		TestFalse(
			TEXT("Stopping descendant leaves descendant managed script stopped"),
			DescendantWorld.Component->bScriptRunning);
		const FString SuccessId = FGuid::NewGuid().ToString(EGuidFormats::DigitsWithHyphensLower);
		const FString SuccessExternal = FPaths::Combine(ExternalRoot, SuccessId + TEXT(".bin"));
		FFinareNativePublicationPrepareResult SuccessPrepare = BuildPrepare(
			Declaration, *OwnerFixture, SuccessId, SuccessExternal, 41);
		ApplyError.Reset();
		TestTrue(
			TEXT("ApplyNativePublication commits a same-parent native publication once all Worlds are stopped"),
			FFinareAuthoringWorkbench::ApplyNativePublication(Context, SuccessPrepare, PreviewToken, ApplyError));
		const FString HistoryDirectory = FPaths::Combine(
			FPaths::ConvertRelativePathToFull(
				FPaths::Combine(FPaths::ProjectSavedDir(), TEXT("Finare/ClassOperations/NativeHistory"))));
		const FString SuccessHistory = FPaths::Combine(HistoryDirectory, SuccessId + TEXT(".json"));
		const FString SuccessMarker = FPaths::Combine(HistoryDirectory, SuccessId + TEXT(".completed.json"));
		TestTrue(
			TEXT("Successful same-parent publication writes its external output"),
			IFileManager::Get().FileExists(*SuccessExternal));
		TestTrue(
			TEXT("Successful same-parent publication writes history"), IFileManager::Get().FileExists(*SuccessHistory));
		TestTrue(
			TEXT("Successful same-parent publication writes completion marker"),
			IFileManager::Get().FileExists(*SuccessMarker));
		TestTrue(
			TEXT("Successful same-parent publication reports the owner actor replacement"),
			ReinstancedKeys.Contains(OwnerActorKey) &&
			CurrentOwnerActor() != nullptr &&
			CurrentOwnerActor() != OwnerWorld.Actor);
		OwnerCdo = OwnerBlueprint->GeneratedClass->GetDefaultObject<AFinareActor>();
		DescendantCdo = DescendantBlueprint->GeneratedClass->GetDefaultObject<AFinareActor>();
		FIntProperty* OwnerCdoCount = OwnerCdo != nullptr
			                              ? FindFProperty<FIntProperty>(OwnerCdo->GetClass(), TEXT("Count"))
			                              : nullptr;
		FIntProperty* DescendantCdoCount = DescendantCdo != nullptr
			                                   ? FindFProperty<FIntProperty>(
				                                   DescendantCdo->GetClass(), TEXT("DescCount"))
			                                   : nullptr;
		TestEqual(
			TEXT("Same-parent publication applies the candidate owner default"),
			OwnerCdoCount->GetPropertyValue_InContainer(OwnerCdo), 41);
		TestEqual(
			TEXT("Same-parent publication preserves descendant default"),
			DescendantCdoCount->GetPropertyValue_InContainer(DescendantCdo), 2);
		OwnerWorld.Actor = CurrentOwnerActor();
		DescendantWorld.Actor = CurrentDescendantActor();
		OwnerWorld.Component = OwnerWorld.Actor != nullptr ? OwnerWorld.Actor->GetFinareComponent() : nullptr;
		DescendantWorld.Component = DescendantWorld.Actor != nullptr
			                            ? DescendantWorld.Actor->GetFinareComponent()
			                            : nullptr;
		if (!TestNotNull(TEXT("Successful publication resolves the owner actor"), OwnerWorld.Actor) ||
			!TestNotNull(TEXT("Successful publication resolves the descendant actor"), DescendantWorld.Actor) ||
			!TestNotNull(TEXT("Successful publication resolves the owner component"), OwnerWorld.Component) ||
			!TestNotNull(TEXT("Successful publication resolves the descendant component"), DescendantWorld.Component))
		{
			return false;
		}
		TestFalse(
			TEXT("Successful same-parent publication keeps the stopped owner stopped"),
			OwnerWorld.Component->bScriptRunning);
		TestFalse(
			TEXT("Successful same-parent publication keeps the stopped descendant stopped"),
			DescendantWorld.Component->bScriptRunning);
		TestTrue(TEXT("Successful same-parent publication preserves owner component outer and archetype"),
		         OwnerWorld.Component->GetOuter() == OwnerWorld.Actor &&
		         OwnerCdo->GetFinareComponent() != nullptr &&
		         OwnerWorld.Component->GetArchetype() == OwnerCdo->GetFinareComponent());
		TestTrue(TEXT("Successful same-parent publication preserves descendant component outer and archetype"),
		         DescendantWorld.Component->GetOuter() == DescendantWorld.Actor &&
		         DescendantCdo->GetFinareComponent() != nullptr &&
		         DescendantWorld.Component->GetArchetype() == DescendantCdo->GetFinareComponent());
		TestTrue(TEXT("Successful same-parent publication preserves ScriptAsset references"),
		         OwnerWorld.Component->ScriptAsset == OwnerAsset && DescendantWorld.Component->ScriptAsset ==
		         DescendantAsset);
		TestEqual(TEXT("Successful same-parent publication preserves stopped owner value"),
		          FindFProperty<FIntProperty>(OwnerWorld.Actor->GetClass(), TEXT("Count"))->
		          GetPropertyValue_InContainer(OwnerWorld.Actor), 102);
		TestEqual(TEXT("Successful same-parent publication preserves stopped descendant value"),
		          FindFProperty<FIntProperty>(DescendantWorld.Actor->GetClass(), TEXT("DescCount"))->
		          GetPropertyValue_InContainer(DescendantWorld.Actor), 206);
		TestEqual(TEXT("Successful same-parent publication preserves descendant inherited Count override"),
		          FindFProperty<FIntProperty>(DescendantWorld.Actor->GetClass(), TEXT("Count"))->
		          GetPropertyValue_InContainer(DescendantWorld.Actor), 150);
		Context.GeneratedClass = OwnerBlueprint->GeneratedClass;

		IFileManager::Get().Delete(*SuccessExternal, false, true, true);
		IFileManager::Get().Delete(*SuccessHistory, false, true, true);
		IFileManager::Get().Delete(*SuccessMarker, false, true, true);
		TArray<uint8> OwnerPackageBytesBeforeFailure;
		TestTrue(
			TEXT("Captures the successful same-parent package bytes before injected failure"),
			ReadBytes(OwnerPackageFilename, OwnerPackageBytesBeforeFailure));
		const FString FailureId = FGuid::NewGuid().ToString(EGuidFormats::DigitsWithHyphensLower);
		const FString FailureExternal = FPaths::Combine(ExternalRoot, FailureId + TEXT(".bin"));
		FFinareNativePublicationPrepareResult FailurePrepare = BuildPrepare(
			Declaration, *OwnerFixture, FailureId, FailureExternal, 77);
		Context.FailAfterExternalPublicationWriteForTesting = 2;
		ApplyError.Reset();
		TestFalse(
			TEXT("ApplyNativePublication rejects injected external-write failure after same-parent materialization"),
			FFinareAuthoringWorkbench::ApplyNativePublication(Context, FailurePrepare, PreviewToken, ApplyError));
		TestEqual(TEXT("Injected same-parent failure reports the external-write seam"), ApplyError,
		          FString(TEXT("Injected external publication failure after 2 external file writes.")));
		TestFalse(
			TEXT("Injected same-parent failure removes external output"),
			IFileManager::Get().FileExists(*FailureExternal));
		TestFalse(TEXT("Injected same-parent failure leaves no history"), IFileManager::Get().FileExists(
			          *FPaths::Combine(HistoryDirectory, FailureId + TEXT(".json"))));
		TestFalse(TEXT("Injected same-parent failure leaves no completion marker"), IFileManager::Get().FileExists(
			          *FPaths::Combine(HistoryDirectory, FailureId + TEXT(".completed.json"))));
		TArray<uint8> OwnerPackageBytesAfterFailure;
		TestTrue(TEXT("Injected same-parent failure restores owner package bytes"),
		         ReadBytes(OwnerPackageFilename, OwnerPackageBytesAfterFailure) && OwnerPackageBytesAfterFailure ==
		         OwnerPackageBytesBeforeFailure);
		OwnerWorld.Actor = CurrentOwnerActor();
		DescendantWorld.Actor = CurrentDescendantActor();
		OwnerWorld.Component = OwnerWorld.Actor != nullptr ? OwnerWorld.Actor->GetFinareComponent() : nullptr;
		DescendantWorld.Component = DescendantWorld.Actor != nullptr
			                            ? DescendantWorld.Actor->GetFinareComponent()
			                            : nullptr;
		if (!TestNotNull(TEXT("Failed publication resolves the owner actor"), OwnerWorld.Actor) ||
			!TestNotNull(TEXT("Failed publication resolves the descendant actor"), DescendantWorld.Actor) ||
			!TestNotNull(TEXT("Failed publication resolves the owner component"), OwnerWorld.Component) ||
			!TestNotNull(TEXT("Failed publication resolves the descendant component"), DescendantWorld.Component))
		{
			return false;
		}
		OwnerCdo = OwnerBlueprint->GeneratedClass->GetDefaultObject<AFinareActor>();
		DescendantCdo = DescendantBlueprint->GeneratedClass->GetDefaultObject<AFinareActor>();
		TestFalse(TEXT("Injected same-parent failure keeps the owner stopped"), OwnerWorld.Component->bScriptRunning);
		TestFalse(
			TEXT("Injected same-parent failure keeps the descendant stopped"),
			DescendantWorld.Component->bScriptRunning);
		TestEqual(TEXT("Injected same-parent failure restores owner value"),
		          FindFProperty<FIntProperty>(OwnerWorld.Actor->GetClass(), TEXT("Count"))->
		          GetPropertyValue_InContainer(OwnerWorld.Actor), 102);
		TestEqual(TEXT("Injected same-parent failure restores descendant value"),
		          FindFProperty<FIntProperty>(DescendantWorld.Actor->GetClass(), TEXT("DescCount"))->
		          GetPropertyValue_InContainer(DescendantWorld.Actor), 206);
		TestEqual(TEXT("Injected same-parent failure restores descendant inherited Count override"),
		          FindFProperty<FIntProperty>(DescendantWorld.Actor->GetClass(), TEXT("Count"))->
		          GetPropertyValue_InContainer(DescendantWorld.Actor), 150);
		TestTrue(TEXT("Injected same-parent failure restores owner component identity"),
		         OwnerWorld.Component->GetOuter() == OwnerWorld.Actor && OwnerWorld.Component->ScriptAsset == OwnerAsset
		         &&
		         OwnerCdo->GetFinareComponent() != nullptr && OwnerWorld.Component->GetArchetype() == OwnerCdo->
		         GetFinareComponent());
		TestTrue(TEXT("Injected same-parent failure restores descendant component identity"),
		         DescendantWorld.Component->GetOuter() == DescendantWorld.Actor && DescendantWorld.Component->
		         ScriptAsset == DescendantAsset &&
		         DescendantCdo->GetFinareComponent() != nullptr && DescendantWorld.Component->GetArchetype() ==
		         DescendantCdo->GetFinareComponent());
		if (!TestTrue(TEXT("Owner explicit Start is released after rollback"), OwnerWorld.Component->StartScript()) ||
			!TestTrue(
				TEXT("Descendant explicit Start is released after rollback"),
				DescendantWorld.Component->StartScript()) ||
			!TestTrue(TEXT("Owner runs after rollback explicit Start"), OwnerWorld.Component->bScriptRunning) ||
			!TestTrue(TEXT("Descendant runs after rollback explicit Start"), DescendantWorld.Component->bScriptRunning))
		{
			return false;
		}
		OwnerWorld.Host->BeginManagedFrame();
		OwnerWorld.Component->TickComponent(0.016f, LEVELTICK_All, nullptr);
		OwnerWorld.Host->EndManagedFrame();
		DescendantWorld.Host->BeginManagedFrame();
		DescendantWorld.Component->TickComponent(0.016f, LEVELTICK_All, nullptr);
		DescendantWorld.Host->EndManagedFrame();
		TestEqual(TEXT("Owner ticks after rollback explicit Start"),
		          FindFProperty<FIntProperty>(OwnerWorld.Actor->GetClass(), TEXT("Count"))->
		          GetPropertyValue_InContainer(OwnerWorld.Actor), 203);
		TestEqual(TEXT("Descendant ticks after rollback explicit Start"),
		          FindFProperty<FIntProperty>(DescendantWorld.Actor->GetClass(), TEXT("DescCount"))->
		          GetPropertyValue_InContainer(DescendantWorld.Actor), 408);
		TestEqual(TEXT("Explicit restart preserves descendant inherited Count override"),
		          FindFProperty<FIntProperty>(DescendantWorld.Actor->GetClass(), TEXT("Count"))->
		          GetPropertyValue_InContainer(DescendantWorld.Actor), 150);
		OwnerWorld.Component->StopScript();
		DescendantWorld.Component->StopScript();
		Context.FailAfterExternalPublicationWriteForTesting = INDEX_NONE;
		return true;
	}
}

#endif
