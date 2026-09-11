#include "FinareSyncAssetsCommandlet.h"

#include "AssetRegistry/AssetRegistryModule.h"
#include "Dom/JsonObject.h"
#include "HAL/FileManager.h"
#include "Misc/FileHelper.h"
#include "Misc/PackageName.h"
#include "Misc/Parse.h"
#include "Misc/Paths.h"
#include "Modules/ModuleManager.h"
#include "Serialization/JsonReader.h"
#include "Serialization/JsonSerializer.h"
#include "FinareAsset.h"
#include "UObject/Package.h"
#include "UObject/SavePackage.h"

namespace UE::Finare::SyncAssets::Private
{
	constexpr TCHAR SchemaVersion[] = TEXT("unifiedscript.unreal-script-deployment/v0.1");

	struct FDeploymentEntry
	{
		FString AssetPath;
		FString AssemblyPath;
		FString TypeName;
		FString RequiredTargetClassPath;
		FString SourcePath;
		FString DeclarationPath;
		FString DeclarationAssetPath;
		FString AuthoringSurfacePath;
		FString ApiNodeCatalogPath;
		FString AuthoringServerUrl;
		UClass* RequiredTargetClass = nullptr;
	};

	bool ReadRequiredString(
		const TSharedPtr<FJsonObject>& Object,
		const TCHAR* Field,
		FString& OutValue,
		FString& OutError)
	{
		if (!Object.IsValid() || !Object->TryGetStringField(Field, OutValue))
		{
			OutError = FString::Printf(TEXT("Deployment entry requires string field '%s'."), Field);
			return false;
		}
		OutValue.TrimStartAndEndInline();
		if (OutValue.IsEmpty())
		{
			OutError = FString::Printf(TEXT("Deployment field '%s' cannot be empty."), Field);
			return false;
		}
		return true;
	}

	bool ReadOptionalString(
		const TSharedPtr<FJsonObject>& Object,
		const TCHAR* Field,
		FString& OutValue,
		FString& OutError)
	{
		OutValue.Reset();
		if (!Object.IsValid() || !Object->HasField(Field))
		{
			return true;
		}
		if (!Object->TryGetStringField(Field, OutValue))
		{
			OutError = FString::Printf(TEXT("Optional deployment field '%s' must be a string."), Field);
			return false;
		}
		OutValue.TrimStartAndEndInline();
		return true;
	}

	bool ValidateKnownFields(
		const TSharedPtr<FJsonObject>& Object,
		TConstArrayView<const TCHAR*> AllowedFields,
		const TCHAR* Context,
		FString& OutError)
	{
		TSet<FString> Allowed;
		for (const TCHAR* Field : AllowedFields)
		{
			Allowed.Add(Field);
		}
		for (const TPair<FString, TSharedPtr<FJsonValue>>& Pair : Object->Values)
		{
			if (!Allowed.Contains(Pair.Key))
			{
				OutError = FString::Printf(
					TEXT("%s contains unknown field '%s'."),
					Context,
					*Pair.Key);
				return false;
			}
		}
		return true;
	}

	bool ParseManifest(
		const FString& InputPath,
		TArray<FDeploymentEntry>& OutEntries,
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
		if (!FJsonSerializer::Deserialize(Reader, Root) || !Root.IsValid())
		{
			OutError = FString::Printf(TEXT("Deployment manifest '%s' is not valid JSON."), *InputPath);
			return false;
		}
		const TCHAR* RootFields[] = {TEXT("schemaVersion"), TEXT("scripts")};
		if (!ValidateKnownFields(Root, RootFields, TEXT("Deployment manifest"), OutError))
		{
			return false;
		}

		FString ParsedSchema;
		if (!Root->TryGetStringField(TEXT("schemaVersion"), ParsedSchema) ||
			ParsedSchema != SchemaVersion)
		{
			OutError = FString::Printf(
				TEXT("Unsupported deployment schema '%s'; expected '%s'."),
				*ParsedSchema,
				SchemaVersion);
			return false;
		}

		const TArray<TSharedPtr<FJsonValue>>* Scripts = nullptr;
		if (!Root->TryGetArrayField(TEXT("scripts"), Scripts) || Scripts == nullptr || Scripts->IsEmpty())
		{
			OutError = TEXT("Deployment manifest requires a non-empty 'scripts' array.");
			return false;
		}

		TSet<FString> AssetPaths;
		OutEntries.Reset();
		for (int32 Index = 0; Index < Scripts->Num(); ++Index)
		{
			const TSharedPtr<FJsonObject> Object = (*Scripts)[Index]->AsObject();
			if (!Object.IsValid())
			{
				OutError = FString::Printf(TEXT("Deployment scripts[%d] must be an object."), Index);
				return false;
			}
			const TCHAR* EntryFields[] = {
				TEXT("assetPath"),
				TEXT("assemblyPath"),
				TEXT("typeName"),
				TEXT("requiredTargetClass"),
				TEXT("sourcePath"),
				TEXT("declarationPath"),
				TEXT("declarationAssetPath"),
				TEXT("authoringSurfacePath"),
				TEXT("apiNodeCatalogPath"),
				TEXT("authoringServerUrl"),
			};
			if (!ValidateKnownFields(Object, EntryFields, TEXT("Deployment entry"), OutError))
			{
				return false;
			}

			FDeploymentEntry& Entry = OutEntries.AddDefaulted_GetRef();
			if (!ReadRequiredString(Object, TEXT("assetPath"), Entry.AssetPath, OutError) ||
				!ReadRequiredString(Object, TEXT("assemblyPath"), Entry.AssemblyPath, OutError) ||
				!ReadRequiredString(Object, TEXT("typeName"), Entry.TypeName, OutError) ||
				!ReadRequiredString(
					Object,
					TEXT("requiredTargetClass"),
					Entry.RequiredTargetClassPath,
					OutError) ||
				!ReadOptionalString(Object, TEXT("sourcePath"), Entry.SourcePath, OutError) ||
				!ReadOptionalString(Object, TEXT("declarationPath"), Entry.DeclarationPath, OutError) ||
				!ReadOptionalString(
					Object,
					TEXT("declarationAssetPath"),
					Entry.DeclarationAssetPath,
					OutError) ||
				!ReadOptionalString(
					Object,
					TEXT("authoringSurfacePath"),
					Entry.AuthoringSurfacePath,
					OutError) ||
				!ReadOptionalString(
					Object,
					TEXT("apiNodeCatalogPath"),
					Entry.ApiNodeCatalogPath,
					OutError) ||
				!ReadOptionalString(
					Object,
					TEXT("authoringServerUrl"),
					Entry.AuthoringServerUrl,
					OutError))
			{
				return false;
			}

			if (!Entry.AssetPath.StartsWith(TEXT("/Game/")) ||
				!FPackageName::IsValidLongPackageName(Entry.AssetPath))
			{
				OutError = FString::Printf(
					TEXT("Deployment assetPath '%s' must be a valid /Game package path."),
					*Entry.AssetPath);
				return false;
			}
			if (AssetPaths.Contains(Entry.AssetPath))
			{
				OutError = FString::Printf(
					TEXT("Deployment assetPath '%s' is duplicated."),
					*Entry.AssetPath);
				return false;
			}
			AssetPaths.Add(Entry.AssetPath);

			Entry.RequiredTargetClass = LoadObject<UClass>(
				nullptr,
				*Entry.RequiredTargetClassPath);
			if (Entry.RequiredTargetClass == nullptr)
			{
				OutError = FString::Printf(
					TEXT("Deployment target class '%s' cannot be loaded."),
					*Entry.RequiredTargetClassPath);
				return false;
			}

			const FString AssetName = FPackageName::GetLongPackageAssetName(Entry.AssetPath);
			const FString ObjectPath = Entry.AssetPath + TEXT(".") + AssetName;
			if (UObject* Existing = StaticLoadObject(
				UObject::StaticClass(),
				nullptr,
				*ObjectPath,
				nullptr,
				LOAD_NoWarn))
			{
				if (!Existing->IsA<UFinareAsset>())
				{
					OutError = FString::Printf(
						TEXT("Deployment path '%s' already contains '%s', not a Finare Asset."),
						*ObjectPath,
						*Existing->GetClass()->GetPathName());
					return false;
				}
			}
		}
		return true;
	}

	bool SyncEntry(const FDeploymentEntry& Entry, bool& bOutCreated, FString& OutError)
	{
		const FString AssetName = FPackageName::GetLongPackageAssetName(Entry.AssetPath);
		const FString ObjectPath = Entry.AssetPath + TEXT(".") + AssetName;
		UFinareAsset* Asset = LoadObject<UFinareAsset>(
			nullptr,
			*ObjectPath,
			nullptr,
			LOAD_NoWarn);
		UPackage* Package = Asset != nullptr
			                    ? Asset->GetOutermost()
			                    : CreatePackage(*Entry.AssetPath);
		if (Package == nullptr)
		{
			OutError = FString::Printf(TEXT("Cannot create package '%s'."), *Entry.AssetPath);
			return false;
		}

		bOutCreated = Asset == nullptr;
		if (Asset == nullptr)
		{
			Asset = NewObject<UFinareAsset>(
				Package,
				*AssetName,
				RF_Public | RF_Standalone | RF_Transactional);
			FAssetRegistryModule::AssetCreated(Asset);
		}
		Asset->Modify();
		Asset->SchemaVersion = 1;
		Asset->ScriptAssemblyPath = Entry.AssemblyPath;
		Asset->ScriptTypeName = Entry.TypeName;
		Asset->RequiredTargetClass = TSoftClassPtr<UObject>(Entry.RequiredTargetClass);
		Asset->SourcePath = Entry.SourcePath;
		Asset->DeclarationPath = Entry.DeclarationPath;
		Asset->DeclarationAssetPath = Entry.DeclarationAssetPath;
		Asset->AuthoringSurfacePath = Entry.AuthoringSurfacePath;
		Asset->ApiNodeCatalogPath = Entry.ApiNodeCatalogPath;
		Asset->AuthoringServerUrl = Entry.AuthoringServerUrl.IsEmpty()
			                            ? TEXT("http://127.0.0.1:4173")
			                            : Entry.AuthoringServerUrl;
		Asset->MarkPackageDirty();

		const FString Filename = FPackageName::LongPackageNameToFilename(
			Entry.AssetPath,
			FPackageName::GetAssetPackageExtension());
		IFileManager::Get().MakeDirectory(*FPaths::GetPath(Filename), true);
		FSavePackageArgs SaveArguments;
		SaveArguments.TopLevelFlags = RF_Public | RF_Standalone;
		SaveArguments.SaveFlags = SAVE_NoError;
		if (!UPackage::SavePackage(Package, Asset, *Filename, SaveArguments))
		{
			OutError = FString::Printf(
				TEXT("Failed to save Finare Asset '%s' to '%s'."),
				*ObjectPath,
				*Filename);
			return false;
		}
		return true;
	}
} // namespace UE::Finare::SyncAssets::Private

UFinareSyncAssetsCommandlet::UFinareSyncAssetsCommandlet()
{
	IsClient = false;
	IsEditor = true;
	LogToConsole = true;
	ShowErrorCount = true;
}

int32 UFinareSyncAssetsCommandlet::Main(const FString& Params)
{
	using namespace UE::Finare::SyncAssets::Private;

	FString InputPath;
	if (!FParse::Value(*Params, TEXT("Input="), InputPath))
	{
		UE_LOG(LogTemp, Error, TEXT("FinareSyncAssets requires -Input=<manifest.json>."));
		return 1;
	}
	InputPath = FPaths::ConvertRelativePathToFull(InputPath);

	TArray<FDeploymentEntry> Entries;
	FString Error;
	if (!ParseManifest(InputPath, Entries, Error))
	{
		UE_LOG(LogTemp, Error, TEXT("Finare deployment preflight failed: %s"), *Error);
		return 2;
	}

	if (FParse::Param(*Params, TEXT("ValidateOnly")))
	{
		UE_LOG(
			LogTemp,
			Display,
			TEXT("Finare deployment validated: Scripts=%d, Manifest=%s"),
			Entries.Num(),
			*InputPath);
		return 0;
	}

	int32 Created = 0;
	int32 Updated = 0;
	for (const FDeploymentEntry& Entry : Entries)
	{
		bool bCreated = false;
		if (!SyncEntry(Entry, bCreated, Error))
		{
			UE_LOG(LogTemp, Error, TEXT("Finare deployment sync failed: %s"), *Error);
			return 3;
		}
		Created += bCreated ? 1 : 0;
		Updated += bCreated ? 0 : 1;
	}

	UE_LOG(
		LogTemp,
		Display,
		TEXT("Finare deployment synchronized: Scripts=%d, Created=%d, Updated=%d, Manifest=%s"),
		Entries.Num(),
		Created,
		Updated,
		*InputPath);
	return 0;
}
