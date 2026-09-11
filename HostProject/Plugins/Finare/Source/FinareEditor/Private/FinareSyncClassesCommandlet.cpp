#include "FinareSyncClassesCommandlet.h"

#include "Dom/JsonObject.h"
#include "Misc/FileHelper.h"
#include "Misc/PackageName.h"
#include "Misc/Parse.h"
#include "Misc/Paths.h"
#include "Serialization/JsonReader.h"
#include "Serialization/JsonSerializer.h"
#include "FinareAsset.h"
#include "FinareBlueprintClassMaterializer.h"

namespace UE::Finare::SyncClasses::Private
{
	constexpr TCHAR SchemaVersion[] = TEXT("unifiedscript.unreal-script-deployment/v0.1");

	bool ParseManifest(
		const FString& InputPath,
		TArray<TObjectPtr<UFinareAsset>>& OutAssets,
		FString& OutError)
	{
		FString Json;
		if (!FFileHelper::LoadFileToString(Json, *InputPath))
		{
			OutError = FString::Printf(TEXT("Cannot read deployment manifest '%s'."), *InputPath);
			return false;
		}

		TSharedPtr<FJsonObject> Root;
		const TSharedRef<TJsonReader<>> Reader = TJsonReaderFactory<>::Create(Json);
		const TArray<TSharedPtr<FJsonValue>>* Scripts = nullptr;
		FString ParsedSchema;
		if (!FJsonSerializer::Deserialize(Reader, Root) || !Root.IsValid() ||
			!Root->TryGetStringField(TEXT("schemaVersion"), ParsedSchema) ||
			ParsedSchema != SchemaVersion ||
			!Root->TryGetArrayField(TEXT("scripts"), Scripts) || Scripts == nullptr ||
			Scripts->IsEmpty())
		{
			OutError = FString::Printf(
				TEXT("Script Class synchronization requires schema '%s' and a non-empty scripts array."),
				SchemaVersion);
			return false;
		}

		TSet<FString> AssetPaths;
		for (int32 Index = 0; Index < Scripts->Num(); ++Index)
		{
			const TSharedPtr<FJsonObject> Entry = (*Scripts)[Index].IsValid()
				                                      ? (*Scripts)[Index]->AsObject()
				                                      : nullptr;
			FString AssetPath;
			if (!Entry.IsValid() || !Entry->TryGetStringField(TEXT("assetPath"), AssetPath))
			{
				OutError = FString::Printf(
					TEXT("Deployment scripts[%d] requires an assetPath string."),
					Index);
				return false;
			}
			AssetPath.TrimStartAndEndInline();
			if (!AssetPath.StartsWith(TEXT("/Game/")) ||
				!FPackageName::IsValidLongPackageName(AssetPath) ||
				AssetPaths.Contains(AssetPath))
			{
				OutError = FString::Printf(
					TEXT("Deployment assetPath '%s' is invalid or duplicated."),
					*AssetPath);
				return false;
			}
			AssetPaths.Add(AssetPath);

			const FString AssetName = FPackageName::GetLongPackageAssetName(AssetPath);
			const FString ObjectPath = AssetPath + TEXT(".") + AssetName;
			UFinareAsset* Asset = LoadObject<UFinareAsset>(
				nullptr,
				*ObjectPath,
				nullptr,
				LOAD_NoWarn);
			if (Asset == nullptr)
			{
				OutError = FString::Printf(
					TEXT("Script Class synchronization requires existing Finare Asset '%s'."),
					*ObjectPath);
				return false;
			}
			if (Asset->ScriptClassKind != EFinareClassKind::None)
			{
				OutAssets.Add(Asset);
			}
		}
		return true;
	}

	void Accumulate(
		const FFinareBlueprintClassSyncResult& Result,
		FFinareBlueprintClassSyncResult& Total)
	{
		Total.DeclaredStateCount += Result.DeclaredStateCount;
		Total.AddedPropertyCount += Result.AddedPropertyCount;
		Total.UpdatedPropertyCount += Result.UpdatedPropertyCount;
		Total.RenamedPropertyCount += Result.RenamedPropertyCount;
		Total.RemovedPropertyCount += Result.RemovedPropertyCount;
		Total.PreservedDefaultOverrideCount += Result.PreservedDefaultOverrideCount;
		Total.DeclaredFunctionCount += Result.DeclaredFunctionCount;
		Total.AddedFunctionCount += Result.AddedFunctionCount;
		Total.UpdatedFunctionCount += Result.UpdatedFunctionCount;
		Total.RemovedFunctionCount += Result.RemovedFunctionCount;
		Total.DeclaredLifecycleHookCount += Result.DeclaredLifecycleHookCount;
		Total.UpdatedLifecycleHookCount += Result.UpdatedLifecycleHookCount;
		Total.DeclaredInputBindingCount += Result.DeclaredInputBindingCount;
		Total.UpdatedInputBindingCount += Result.UpdatedInputBindingCount;
		Total.DeclaredDelegateBindingCount += Result.DeclaredDelegateBindingCount;
		Total.UpdatedDelegateBindingCount += Result.UpdatedDelegateBindingCount;
		Total.DeclaredInterfaceBindingCount += Result.DeclaredInterfaceBindingCount;
		Total.UpdatedInterfaceBindingCount += Result.UpdatedInterfaceBindingCount;
		Total.bChanged |= Result.bChanged;
	}
} // namespace UE::Finare::SyncClasses::Private

UFinareSyncClassesCommandlet::UFinareSyncClassesCommandlet()
{
	IsClient = false;
	IsEditor = true;
	LogToConsole = true;
	ShowErrorCount = true;
}

int32 UFinareSyncClassesCommandlet::Main(const FString& Params)
{
	using namespace UE::Finare::SyncClasses::Private;

	FString InputPath;
	if (!FParse::Value(*Params, TEXT("Input="), InputPath))
	{
		UE_LOG(LogTemp, Error, TEXT("FinareSyncClasses requires -Input=<manifest.json>."));
		return 1;
	}
	InputPath = FPaths::ConvertRelativePathToFull(InputPath);

	TArray<TObjectPtr<UFinareAsset>> Assets;
	FString Error;
	if (!ParseManifest(InputPath, Assets, Error))
	{
		UE_LOG(LogTemp, Error, TEXT("Finare class proxy preflight failed: %s"), *Error);
		return 2;
	}

	FFinareBlueprintClassSyncResult PreflightTotal;
	for (UFinareAsset* Asset : Assets)
	{
		FFinareBlueprintClassSyncResult Result;
		if (!FFinareBlueprintClassMaterializer::Synchronize(
			*Asset,
			true,
			Result,
			Error))
		{
			UE_LOG(
				LogTemp,
				Error,
				TEXT("Finare class proxy preflight failed for '%s': %s"),
				*Asset->GetPathName(),
				*Error);
			return 3;
		}
		Accumulate(Result, PreflightTotal);
	}

	const bool bValidateOnly = FParse::Param(*Params, TEXT("ValidateOnly"));
	if (bValidateOnly)
	{
		if (PreflightTotal.bChanged)
		{
			UE_LOG(
				LogTemp,
				Error,
				TEXT(
					"Finare class proxy validation found stale output: Classes=%d, States=%d, Added=%d, Updated=%d, Renamed=%d, Removed=%d."
				),
				Assets.Num(),
				PreflightTotal.DeclaredStateCount,
				PreflightTotal.AddedPropertyCount,
				PreflightTotal.UpdatedPropertyCount,
				PreflightTotal.RenamedPropertyCount,
				PreflightTotal.RemovedPropertyCount);
			return 4;
		}
		UE_LOG(
			LogTemp,
			Display,
			TEXT("Finare class proxies validated: Classes=%d, States=%d, Manifest=%s"),
			Assets.Num(),
			PreflightTotal.DeclaredStateCount,
			*InputPath);
		return 0;
	}

	FFinareBlueprintClassSyncResult AppliedTotal;
	for (UFinareAsset* Asset : Assets)
	{
		FFinareBlueprintClassSyncResult Result;
		if (!FFinareBlueprintClassMaterializer::Synchronize(
			*Asset,
			false,
			Result,
			Error))
		{
			UE_LOG(
				LogTemp,
				Error,
				TEXT("Finare class proxy synchronization failed for '%s': %s"),
				*Asset->GetPathName(),
				*Error);
			return 5;
		}
		Accumulate(Result, AppliedTotal);
	}

	UE_LOG(
		LogTemp,
		Display,
		TEXT(
			"Finare class proxies synchronized: Classes=%d, States=%d, Added=%d, Updated=%d, Renamed=%d, Removed=%d, PreservedOverrides=%d, Manifest=%s"
		),
		Assets.Num(),
		AppliedTotal.DeclaredStateCount,
		AppliedTotal.AddedPropertyCount,
		AppliedTotal.UpdatedPropertyCount,
		AppliedTotal.RenamedPropertyCount,
		AppliedTotal.RemovedPropertyCount,
		AppliedTotal.PreservedDefaultOverrideCount,
		*InputPath);
	return 0;
}
