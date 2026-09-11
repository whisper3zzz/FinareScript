#if WITH_DEV_AUTOMATION_TESTS

#include "FinareAuthoringSurface.h"
#include "Widgets/SFinareAuthoringSurface.h"

#include "Dom/JsonObject.h"
#include "HAL/FileManager.h"
#include "Misc/AutomationTest.h"
#include "Misc/CommandLine.h"
#include "Misc/FileHelper.h"
#include "Misc/Guid.h"
#include "Misc/Parse.h"
#include "Misc/Paths.h"
#include "Misc/ScopeExit.h"
#include "Serialization/JsonReader.h"
#include "Serialization/JsonSerializer.h"
#include "Serialization/JsonWriter.h"

namespace UE::Finare::AuthoringSurface::Tests
{
	namespace
	{
		bool LoadRawJson(
			const FString& Path,
			TSharedPtr<FJsonObject>& OutRoot,
			FString& OutError)
		{
			FString Json;
			if (!FFileHelper::LoadFileToString(Json, *Path))
			{
				OutError = FString::Printf(TEXT("Could not read '%s'."), *Path);
				return false;
			}

			const TSharedRef<TJsonReader<>> Reader = TJsonReaderFactory<>::Create(Json);
			if (!FJsonSerializer::Deserialize(Reader, OutRoot) || !OutRoot.IsValid())
			{
				OutError = FString::Printf(TEXT("Could not parse '%s': %s"), *Path, *Reader->GetErrorMessage());
				return false;
			}
			return true;
		}

		bool SaveRawJson(
			const FString& Path,
			const TSharedPtr<FJsonObject>& Root,
			FString& OutError)
		{
			FString Json;
			const TSharedRef<TJsonWriter<>> Writer = TJsonWriterFactory<>::Create(&Json);
			if (!Root.IsValid() || !FJsonSerializer::Serialize(Root.ToSharedRef(), Writer))
			{
				OutError = FString::Printf(TEXT("Could not serialize '%s'."), *Path);
				return false;
			}
			if (!FFileHelper::SaveStringToFile(Json, *Path))
			{
				OutError = FString::Printf(TEXT("Could not write '%s'."), *Path);
				return false;
			}
			return true;
		}

		int32 CountStateRows(
			const TArray<FFinareSurfaceTreeItemPtr>& Items,
			const FString& StateId)
		{
			int32 Count = 0;
			for (const FFinareSurfaceTreeItemPtr& Item : Items)
			{
				if (!Item.IsValid())
				{
					continue;
				}
				if (Item->Kind == EFinareSurfaceTreeItemKind::State &&
					Item->SemanticId == StateId)
				{
					++Count;
				}
				Count += CountStateRows(Item->Children, StateId);
			}
			return Count;
		}

		FFinareSurfaceTreeItemPtr FindStateRow(
			const TArray<FFinareSurfaceTreeItemPtr>& Items,
			const FString& StateId)
		{
			for (const FFinareSurfaceTreeItemPtr& Item : Items)
			{
				if (!Item.IsValid())
				{
					continue;
				}
				if (Item->Kind == EFinareSurfaceTreeItemKind::State &&
					Item->SemanticId == StateId)
				{
					return Item;
				}
				if (FFinareSurfaceTreeItemPtr Match = FindStateRow(Item->Children, StateId))
				{
					return Match;
				}
			}
			return nullptr;
		}

		FFinareSurfaceTreeItemPtr FindTreeItem(
			const TArray<FFinareSurfaceTreeItemPtr>& Items,
			const FString& SemanticId)
		{
			for (const FFinareSurfaceTreeItemPtr& Item : Items)
			{
				if (!Item.IsValid())
				{
					continue;
				}
				if (Item->SemanticId == SemanticId)
				{
					return Item;
				}
				if (FFinareSurfaceTreeItemPtr Match = FindTreeItem(Item->Children, SemanticId))
				{
					return Match;
				}
			}
			return nullptr;
		}

		FFinareSurfaceTreeItemPtr FindAssetStateRow(
			const TArray<FFinareSurfaceTreeItemPtr>& Items,
			const FString& AssetPath,
			const FString& StateId)
		{
			for (const FFinareSurfaceTreeItemPtr& Item : Items)
			{
				if (!Item.IsValid())
				{
					continue;
				}
				if (Item->Kind == EFinareSurfaceTreeItemKind::Asset &&
					Item->Label == AssetPath)
				{
					return FindStateRow(Item->Children, StateId);
				}
			}
			return nullptr;
		}

		FFinareSurfaceTreeItemPtr FindAssetRow(
			const TArray<FFinareSurfaceTreeItemPtr>& Items,
			const FString& AssetPath)
		{
			for (const FFinareSurfaceTreeItemPtr& Item : Items)
			{
				if (Item.IsValid() && Item->Kind == EFinareSurfaceTreeItemKind::Asset &&
					Item->Label == AssetPath)
				{
					return Item;
				}
			}
			return nullptr;
		}

		const FFinareAuthoringAsset* FindAsset(
			const FFinareAuthoringSurfaceDocument& Document,
			const TCHAR* AssetPath)
		{
			return Document.Assets.FindByPredicate([AssetPath](const FFinareAuthoringAsset& Asset)
			{
				return Asset.AssetPath == AssetPath;
			});
		}

		const FFinareAuthoringState* FindState(
			const FFinareAuthoringAsset* Asset,
			const TCHAR* Name)
		{
			return Asset == nullptr
				       ? nullptr
				       : Asset->States.FindByPredicate([Name](const FFinareAuthoringState& State)
				       {
					       return State.Name == Name;
				       });
		}

		const FFinareAuthoringFunction* FindFunction(
			const FFinareAuthoringAsset* Asset,
			const TCHAR* Name)
		{
			return Asset == nullptr
				       ? nullptr
				       : Asset->Functions.FindByPredicate([Name](const FFinareAuthoringFunction& Function)
				       {
					       return Function.Name == Name;
				       });
		}

		FString MakeTempPath(const FString& TempDirectory, const TCHAR* Filename)
		{
			return FPaths::Combine(TempDirectory, Filename);
		}
	}
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
	FFinareInheritedAuthoringSurfaceTest,
	"Finare.Unreal.Editor.AuthoringSurface.InheritedOwnership",
	EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FFinareInheritedAuthoringSurfaceTest::RunTest(const FString& Parameters)
{
	using namespace UE::Finare::AuthoringSurface::Tests;

	FString SurfacePath;
	if (!FParse::Value(FCommandLine::Get(), TEXT("FinareInheritedAuthoringSurface="), SurfacePath))
	{
		SurfacePath = FPaths::Combine(
			FPaths::ProjectDir(),
			TEXT("../tests/Finare.Tests/Fixtures/AuthoringInheritance/InheritedAuthoring.surface.json"));
	}
	SurfacePath = FPaths::ConvertRelativePathToFull(SurfacePath);
	FPaths::NormalizeFilename(SurfacePath);

	const TSharedRef<SFinareAuthoringSurface> Widget =
		SNew(SFinareAuthoringSurface).InitialSurfacePath(SurfacePath);
	FString Error;
	if (!TestTrue(
		*FString::Printf(TEXT("Generated inherited authoring Surface loads: %s"), *SurfacePath),
		Widget->LoadSurface(SurfacePath)))
	{
		return false;
	}

	const FFinareAuthoringSurfaceDocument& Document = Widget->Document;
	const FFinareAuthoringAsset* Base = FindAsset(Document, TEXT("Game/ZBase"));
	const FFinareAuthoringAsset* Override = FindAsset(Document, TEXT("Game/AOverride"));
	const FFinareAuthoringAsset* Follower = FindAsset(Document, TEXT("Game/BFollower"));
	const FFinareAuthoringState* BaseSeed = FindState(Base, TEXT("Seed"));
	const FFinareAuthoringState* OverrideSeed = FindState(Override, TEXT("Seed"));
	const FFinareAuthoringState* FollowerSeed = FindState(Follower, TEXT("Seed"));
	const FFinareAuthoringFunction* BaseReadSeed = FindFunction(Base, TEXT("ReadSeed"));
	const FFinareAuthoringFunction* BaseForward = FindFunction(Base, TEXT("Forward"));
	if (!TestNotNull(TEXT("Explicit base asset is present"), Base) ||
		!TestNotNull(TEXT("Override asset is present"), Override) ||
		!TestNotNull(TEXT("Follower asset is present"), Follower) ||
		!TestNotNull(TEXT("Base Seed projection is present"), BaseSeed) ||
		!TestNotNull(TEXT("Override Seed projection is present"), OverrideSeed) ||
		!TestNotNull(TEXT("Follower Seed projection is present"), FollowerSeed) ||
		!TestNotNull(TEXT("Base ReadSeed projection is present"), BaseReadSeed) ||
		!TestNotNull(TEXT("Base Forward projection is present"), BaseForward))
	{
		return false;
	}

	TestEqual(TEXT("Fresh CLI surface is schema v0.5"), Document.SchemaVersion,
	          FString(TEXT("unifiedscript.authoring-surface/v0.5")));
	TestEqual(TEXT("All four inherited declarations have explicit owners"),
	          Document.DeclarationOwners.Num(), 4);
	TestEqual(TEXT("Derived override is ordered before the explicit base owner"),
	          Document.Assets[0].AssetPath, FString(TEXT("Game/AOverride")));
	TestEqual(TEXT("Base default is 3"), BaseSeed->DefaultValue, FString(TEXT("3")));
	TestEqual(TEXT("Override default is 77"), OverrideSeed->DefaultValue, FString(TEXT("77")));
	TestEqual(TEXT("Follower default is 3"), FollowerSeed->DefaultValue, FString(TEXT("3")));
	TestEqual(TEXT("Inherited Values retain their complete defaults"), Base->States[1].DefaultValue,
	          FString(TEXT("[2,4]")));
	TestTrue(TEXT("Shared Seed owner is the explicit base asset"),
	         Document.FindDeclarationOwnerAsset(BaseSeed->Id) == Base);
	TestTrue(TEXT("Shared ReadSeed owner is the explicit base asset"),
	         Document.FindDeclarationOwnerAsset(BaseReadSeed->Id) == Base);
	TestTrue(TEXT("Graph projection keeps the inherited caller body"),
	         BaseForward->Actions.Num() > 0 && BaseForward->Edges.Num() > 0);

	TestEqual(TEXT("Widget keeps every effective Seed projection in the tree"),
	          CountStateRows(Widget->RootItems, BaseSeed->Id), 3);
	const FFinareSurfaceTreeItemPtr OverrideRow = FindAssetStateRow(
		Widget->RootItems, TEXT("Game/AOverride"), BaseSeed->Id);
	const FFinareSurfaceTreeItemPtr BaseRow = FindAssetStateRow(
		Widget->RootItems, TEXT("Game/ZBase"), BaseSeed->Id);
	TestTrue(TEXT("Override row displays its effective default"),
	         OverrideRow.IsValid() && OverrideRow->Detail.Contains(TEXT("= 77")));
	TestTrue(TEXT("Base row displays the source default"),
	         BaseRow.IsValid() && BaseRow->Detail.Contains(TEXT("= 3")));
	const FFinareSurfaceTreeItemPtr* IndexedSeed = Widget->SurfaceItemsById.Find(BaseSeed->Id);
	TestTrue(TEXT("Only the source-owner state row is indexed for editing"),
	         IndexedSeed != nullptr && IndexedSeed->IsValid() && *IndexedSeed == BaseRow);
	TestTrue(TEXT("Semantic navigation resolves the inherited state to its source owner"),
	         Widget->NavigateToSemanticId(BaseSeed->Id));
	TestTrue(TEXT("Widget FindState resolves the explicit source owner"),
	         Widget->FindState(BaseSeed->Id) == BaseSeed);
	TestTrue(TEXT("Widget FindFunction resolves the explicit source owner"),
	         Widget->FindFunction(BaseReadSeed->Id) == BaseReadSeed);
	const FFinareSurfaceTreeItemPtr BaseAssetRow = FindAssetRow(
		Widget->RootItems, TEXT("Game/ZBase"));
	const FFinareSurfaceTreeItemPtr BaseForwardRow = BaseAssetRow.IsValid()
		                                                 ? FindTreeItem(BaseAssetRow->Children, BaseForward->Id)
		                                                 : nullptr;
	const FFinareSurfaceTreeItemPtr* IndexedForward =
		Widget->SurfaceItemsById.Find(BaseForward->Id);
	TestTrue(TEXT("Inherited function is indexed on the source-owner tree"),
	         BaseForwardRow.IsValid() && IndexedForward != nullptr &&
	         IndexedForward->IsValid() && *IndexedForward == BaseForwardRow);

	const FFinareSurfaceTreeItemPtr* IndexedRootBefore =
		Widget->SurfaceItemsById.Find(BaseForward->RootRegionId);
	const FString ActionId = BaseForward->Actions[0].Id;
	const FFinareSurfaceTreeItemPtr* IndexedActionBefore =
		Widget->SurfaceItemsById.Find(ActionId);
	const FFinareAuthoringFunction* OverrideForward = FindFunction(Override, TEXT("Forward"));
	const FFinareSurfaceTreeItemPtr OverrideAssetRow = FindAssetRow(
		Widget->RootItems, TEXT("Game/AOverride"));
	const FFinareSurfaceTreeItemPtr OverrideForwardRow = OverrideAssetRow.IsValid() &&
	                                                     OverrideForward != nullptr
		                                                     ? FindTreeItem(
			                                                     OverrideAssetRow->Children, OverrideForward->Id)
		                                                     : nullptr;
	const FFinareAuthoringRegion* OverrideRoot = OverrideForward == nullptr
		                                             ? nullptr
		                                             : OverrideForward->FindRegion(OverrideForward->RootRegionId);
	TestTrue(TEXT("Inherited function graph source index exists before derived projection"),
	         IndexedRootBefore != nullptr && IndexedRootBefore->IsValid() &&
	         IndexedActionBefore != nullptr && IndexedActionBefore->IsValid());
	if (OverrideForward != nullptr && OverrideRoot != nullptr && OverrideForwardRow.IsValid())
	{
		Widget->AddRegionTree(*OverrideForward, *OverrideRoot, OverrideForwardRow, false);
	}
	const FFinareSurfaceTreeItemPtr* IndexedRootAfter =
		Widget->SurfaceItemsById.Find(BaseForward->RootRegionId);
	const FFinareSurfaceTreeItemPtr* IndexedActionAfter =
		Widget->SurfaceItemsById.Find(ActionId);
	TestTrue(TEXT("Derived graph projection keeps source-owner descendant indices"),
	         IndexedRootBefore != nullptr && IndexedRootAfter != nullptr &&
	         IndexedRootAfter->IsValid() && *IndexedRootAfter == *IndexedRootBefore &&
	         IndexedActionBefore != nullptr && IndexedActionAfter != nullptr &&
	         IndexedActionAfter->IsValid() && *IndexedActionAfter == *IndexedActionBefore);
	TestTrue(TEXT("Graph descendant navigation resolves through its source function"),
	         Widget->NavigateToSemanticId(BaseForward->RootRegionId));
	TestTrue(TEXT("Owner navigation reaches the inherited base graph"),
	         Widget->SelectFunction(BaseForward->Id));
	TestEqual(TEXT("Inherited graph navigation selects the source function"),
	          Widget->GetActiveFunctionId(), BaseForward->Id);

	FString PatchJson;
	if (!TestTrue(
		TEXT("Leaf projection builds an owner-preconditioned state patch"),
		FFinareAuthoringSurfaceReader::BuildStateUpdatePatch(
			Document,
			*OverrideSeed,
			TEXT("RenamedSeed"),
			OverrideSeed->TypeDisplayName,
			OverrideSeed->TypeKind,
			TEXT("11"),
			OverrideSeed->Exposure,
			PatchJson,
			Error)))
	{
		AddError(Error);
		return false;
	}
	TSharedPtr<FJsonObject> PatchRoot;
	const TSharedRef<TJsonReader<>> PatchReader = TJsonReaderFactory<>::Create(PatchJson);
	const bool bPatchParsed = FJsonSerializer::Deserialize(PatchReader, PatchRoot) &&
		PatchRoot.IsValid();
	TestTrue(TEXT("Leaf state patch is valid JSON"), bPatchParsed);

	const TArray<TSharedPtr<FJsonValue>>* PatchOperations = nullptr;
	const TSharedPtr<FJsonObject>* PatchOperation = nullptr;
	const TSharedPtr<FJsonObject>* PatchExpected = nullptr;
	const TSharedPtr<FJsonObject>* PatchReplacement = nullptr;
	double ExpectedDefault = 0.0;
	double ReplacementDefault = 0.0;
	const bool bPatchShape = bPatchParsed &&
		PatchRoot->TryGetArrayField(TEXT("operations"), PatchOperations) &&
		PatchOperations != nullptr && PatchOperations->Num() == 1 &&
		(*PatchOperations)[0].IsValid() &&
		(*PatchOperations)[0]->TryGetObject(PatchOperation) &&
		PatchOperation != nullptr &&
		PatchOperation->IsValid() &&
		(*PatchOperation)->TryGetObjectField(TEXT("expected"), PatchExpected) &&
		PatchExpected != nullptr &&
		PatchExpected->IsValid() &&
		(*PatchExpected)->TryGetNumberField(TEXT("defaultValue"), ExpectedDefault) &&
		(*PatchOperation)->TryGetObjectField(TEXT("replacement"), PatchReplacement) &&
		PatchReplacement != nullptr &&
		PatchReplacement->IsValid() &&
		(*PatchReplacement)->TryGetNumberField(TEXT("defaultValue"), ReplacementDefault);
	TestTrue(TEXT("Leaf state patch contains structured expected/replacement defaults"), bPatchShape);
	if (bPatchShape)
	{
		TestTrue(TEXT("Leaf patch retains the source owner's expected default"),
		         FMath::IsNearlyEqual(ExpectedDefault, 3.0));
		TestTrue(TEXT("Leaf patch carries the requested owner replacement"),
		         FMath::IsNearlyEqual(ReplacementDefault, 11.0));
	}

	FString PatchPath;
	if (FParse::Value(FCommandLine::Get(), TEXT("FinareInheritedAuthoringPatch="), PatchPath))
	{
		PatchPath = FPaths::ConvertRelativePathToFull(PatchPath);
		FPaths::NormalizeFilename(PatchPath);
		TestTrue(TEXT("Optional inherited authoring patch output directory is available"),
		         IFileManager::Get().MakeDirectory(*FPaths::GetPath(PatchPath), true));
		TestTrue(TEXT("Optional inherited authoring patch is written"),
		         FFileHelper::SaveStringToFile(PatchJson, *PatchPath));
	}

	TSharedPtr<FJsonObject> RawSurface;
	if (!TestTrue(TEXT("Generated Surface JSON is available for owner negative cases"),
	              LoadRawJson(SurfacePath, RawSurface, Error)))
	{
		AddError(Error);
		return false;
	}

	const int32 OriginalStateCount = Widget->Document.GetStateCount();
	FString TempDirectory = FPaths::Combine(
		FPaths::ProjectSavedDir(),
		TEXT("Automation/InheritedAuthoring"),
		FGuid::NewGuid().ToString(EGuidFormats::Digits));
	FPaths::NormalizeFilename(TempDirectory);
	if (!TestTrue(TEXT("Inherited authoring negative fixture directory is available"),
	              IFileManager::Get().MakeDirectory(*TempDirectory, true)))
	{
		return false;
	}
	const FString MissingOwnerPath = MakeTempPath(TempDirectory, TEXT("missing-owner.json"));
	const FString LegacyPath = MakeTempPath(TempDirectory, TEXT("legacy-v04.json"));
	const FString GraphMismatchPath = MakeTempPath(TempDirectory, TEXT("graph-mismatch.json"));
	ON_SCOPE_EXIT
	{
		IFileManager& FileManager = IFileManager::Get();
		FileManager.Delete(*MissingOwnerPath);
		FileManager.Delete(*LegacyPath);
		FileManager.Delete(*GraphMismatchPath);
		FileManager.DeleteDirectory(*TempDirectory, false, false);
	};

	RawSurface->RemoveField(TEXT("declarationOwners"));
	if (!TestTrue(TEXT("Missing v0.5 owner fixture is written"),
	              SaveRawJson(MissingOwnerPath, RawSurface, Error)))
	{
		AddError(Error);
		return false;
	}
	TestFalse(TEXT("Invalid owner metadata cannot replace the loaded document"),
	          Widget->LoadSurface(MissingOwnerPath));
	TestEqual(TEXT("Rejected owner metadata leaves the old document intact"),
	          Widget->Document.GetStateCount(), OriginalStateCount);
	IFileManager::Get().Delete(*MissingOwnerPath);

	if (!TestTrue(TEXT("Fresh Surface JSON can be reloaded after owner rejection"),
	              Widget->LoadSurface(SurfacePath)))
	{
		return false;
	}

	TSharedPtr<FJsonObject> LegacySurface;
	if (!TestTrue(TEXT("Legacy Surface negative fixture parses"),
	              LoadRawJson(SurfacePath, LegacySurface, Error)))
	{
		AddError(Error);
		return false;
	}
	LegacySurface->SetStringField(TEXT("schemaVersion"), TEXT("unifiedscript.authoring-surface/v0.4"));
	LegacySurface->RemoveField(TEXT("declarationOwners"));
	const TArray<TSharedPtr<FJsonValue>>* LegacyAssets = nullptr;
	if (!TestTrue(TEXT("Legacy fixture contains assets"),
	              LegacySurface->TryGetArrayField(TEXT("assets"), LegacyAssets) && LegacyAssets != nullptr))
	{
		return false;
	}
	for (const TSharedPtr<FJsonValue>& Value : *LegacyAssets)
	{
		const TSharedPtr<FJsonObject> Asset = Value.IsValid() ? Value->AsObject() : nullptr;
		if (Asset.IsValid())
		{
			TArray<TSharedPtr<FJsonValue>> EmptyFunctions;
			Asset->SetArrayField(TEXT("functions"), MoveTemp(EmptyFunctions));
		}
	}
	if (!TestTrue(TEXT("Legacy v0.4 fixture is written"), SaveRawJson(LegacyPath, LegacySurface, Error)) ||
		!TestTrue(TEXT("Legacy v0.4 shared states remain readable"), Widget->LoadSurface(LegacyPath)))
	{
		AddError(Error);
		return false;
	}
	const FFinareAuthoringAsset* LegacyOverride = FindAsset(Widget->Document, TEXT("Game/AOverride"));
	const FFinareAuthoringState* LegacyState = FindState(LegacyOverride, TEXT("Seed"));
	TestNotNull(TEXT("Legacy shared state projection remains present"), LegacyState);
	TestNull(TEXT("Legacy ambiguous shared state has no editable owner"),
	         LegacyState != nullptr ? Widget->FindState(LegacyState->Id) : nullptr);
	FString LegacyPatch;
	Error.Reset();
	TestFalse(TEXT("Legacy ambiguous state cannot build an owner patch"),
	          LegacyState != nullptr && FFinareAuthoringSurfaceReader::BuildStateUpdatePatch(
		          Widget->Document,
		          *LegacyState,
		          TEXT("Seed"),
		          LegacyState->TypeDisplayName,
		          LegacyState->TypeKind,
		          TEXT("11"),
		          LegacyState->Exposure,
		          LegacyPatch,
		          Error));
	IFileManager::Get().Delete(*LegacyPath);

	if (!TestTrue(TEXT("v0.5 Surface is restored after legacy read-only check"),
	              Widget->LoadSurface(SurfacePath)))
	{
		return false;
	}

	TSharedPtr<FJsonObject> GraphMismatchSurface;
	if (!TestTrue(TEXT("Graph mismatch fixture parses"),
	              LoadRawJson(SurfacePath, GraphMismatchSurface, Error)))
	{
		AddError(Error);
		return false;
	}
	const TArray<TSharedPtr<FJsonValue>>* GraphAssets = nullptr;
	bool bChangedGraph = false;
	if (GraphMismatchSurface->TryGetArrayField(TEXT("assets"), GraphAssets) && GraphAssets != nullptr)
	{
		for (const TSharedPtr<FJsonValue>& Value : *GraphAssets)
		{
			const TSharedPtr<FJsonObject> Asset = Value.IsValid() ? Value->AsObject() : nullptr;
			FString AssetPath;
			if (!Asset.IsValid() || !Asset->TryGetStringField(TEXT("assetPath"), AssetPath) ||
				AssetPath != TEXT("Game/BFollower"))
			{
				continue;
			}
			const TArray<TSharedPtr<FJsonValue>>* Functions = nullptr;
			if (Asset->TryGetArrayField(TEXT("functions"), Functions) && Functions != nullptr &&
				!Functions->IsEmpty())
			{
				const TSharedPtr<FJsonObject> Function = (*Functions)[0].IsValid()
					                                         ? (*Functions)[0]->AsObject()
					                                         : nullptr;
				const TArray<TSharedPtr<FJsonValue>>* Actions = nullptr;
				if (Function.IsValid() && Function->TryGetArrayField(TEXT("actions"), Actions) &&
					Actions != nullptr && !Actions->IsEmpty())
				{
					const TSharedPtr<FJsonObject> Action = (*Actions)[0].IsValid()
						                                       ? (*Actions)[0]->AsObject()
						                                       : nullptr;
					if (Action.IsValid())
					{
						Action->SetStringField(TEXT("summary"), TEXT("changed inherited graph"));
						bChangedGraph = true;
					}
				}
			}
		}
	}
	TestTrue(TEXT("Graph mismatch fixture changes a real inherited action"), bChangedGraph);
	if (!TestTrue(TEXT("Graph mismatch fixture is written"),
	              SaveRawJson(GraphMismatchPath, GraphMismatchSurface, Error)))
	{
		AddError(Error);
		return false;
	}
	TestFalse(TEXT("Shared function graph mismatch is rejected"),
	          Widget->LoadSurface(GraphMismatchPath));
	TestEqual(TEXT("Rejected graph mismatch leaves the loaded document intact"),
	          Widget->Document.GetStateCount(), OriginalStateCount);
	IFileManager::Get().Delete(*GraphMismatchPath);

	return !HasAnyErrors();
}

#endif
