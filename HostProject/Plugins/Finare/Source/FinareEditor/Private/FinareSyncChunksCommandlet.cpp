#include "FinareSyncChunksCommandlet.h"

#include "Dom/JsonObject.h"
#include "Interfaces/IPluginManager.h"
#include "Misc/FileHelper.h"
#include "Misc/PackageName.h"
#include "Misc/Parse.h"
#include "Misc/Paths.h"
#include "Serialization/JsonReader.h"
#include "Serialization/JsonSerializer.h"
#include "FinareAsset.h"
#include "FinareCompiledChunk.h"
#include "UObject/Package.h"
#include "UObject/SavePackage.h"

namespace UE::Finare::SyncChunks::Private
{
	constexpr TCHAR SchemaVersion[] = TEXT("unifiedscript.unreal-chunk-deployment/v0.1");

	struct FChunkDeploymentEntry
	{
		FString AssetPath;
		FString ChunkPath;
		FString DeclarationAssetPath;
		TObjectPtr<UFinareAsset> Asset;
	};

	bool ReadRequiredString(
		const TSharedPtr<FJsonObject>& Object,
		const TCHAR* Field,
		FString& OutValue,
		FString& OutError)
	{
		if (!Object.IsValid() || !Object->TryGetStringField(Field, OutValue))
		{
			OutError = FString::Printf(
				TEXT("Chunk deployment entry requires string field '%s'."),
				Field);
			return false;
		}
		OutValue.TrimStartAndEndInline();
		if (OutValue.IsEmpty())
		{
			OutError = FString::Printf(
				TEXT("Chunk deployment field '%s' cannot be empty."),
				Field);
			return false;
		}
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

	FString ResolveChunkPath(const FString& ConfiguredPath)
	{
		FString Result = ConfiguredPath;
		Result.ReplaceInline(TEXT("{ProjectDir}"), *FPaths::ProjectDir());
		if (const TSharedPtr<IPlugin> Plugin =
			IPluginManager::Get().FindPlugin(TEXT("Finare")))
		{
			Result.ReplaceInline(TEXT("{PluginDir}"), *Plugin->GetBaseDir());
		}
		Result = FPaths::ConvertRelativePathToFull(Result);
		FPaths::NormalizeFilename(Result);
		return Result;
	}

	bool ValidateChunk(const FString& ChunkPath, FString& OutError)
	{
		static constexpr EFinareCompiledChunkSection RequiredSections[] = {
			EFinareCompiledChunkSection::RuntimeManifest,
			EFinareCompiledChunkSection::ManagedAssembly,
			EFinareCompiledChunkSection::DeclarationData,
			EFinareCompiledChunkSection::BindingManifest,
			EFinareCompiledChunkSection::DependencyManifest,
		};
		TArray<uint8> Payload;
		for (const EFinareCompiledChunkSection Section : RequiredSections)
		{
			if (!FFinareCompiledChunk::LoadSection(
				ChunkPath,
				Section,
				Payload,
				OutError))
			{
				return false;
			}
		}
		return true;
	}

	bool ParseManifest(
		const FString& InputPath,
		TArray<FChunkDeploymentEntry>& OutEntries,
		FString& OutError)
	{
		FString Json;
		if (!FFileHelper::LoadFileToString(Json, *InputPath))
		{
			OutError = FString::Printf(
				TEXT("Cannot read chunk deployment manifest '%s'."),
				*InputPath);
			return false;
		}

		TSharedPtr<FJsonObject> Root;
		const TSharedRef<TJsonReader<>> Reader = TJsonReaderFactory<>::Create(Json);
		if (!FJsonSerializer::Deserialize(Reader, Root) || !Root.IsValid())
		{
			OutError = TEXT("Chunk deployment manifest is not valid JSON.");
			return false;
		}
		const TCHAR* RootFields[] = {TEXT("schemaVersion"), TEXT("chunks")};
		FString ParsedSchema;
		const TArray<TSharedPtr<FJsonValue>>* Chunks = nullptr;
		if (!ValidateKnownFields(Root, RootFields, TEXT("Chunk deployment manifest"), OutError) ||
			!Root->TryGetStringField(TEXT("schemaVersion"), ParsedSchema) ||
			ParsedSchema != SchemaVersion ||
			!Root->TryGetArrayField(TEXT("chunks"), Chunks) || Chunks == nullptr ||
			Chunks->IsEmpty())
		{
			if (OutError.IsEmpty())
			{
				OutError = FString::Printf(
					TEXT("Chunk deployment requires schema '%s' and a non-empty chunks array."),
					SchemaVersion);
			}
			return false;
		}

		TSet<FString> AssetPaths;
		for (int32 Index = 0; Index < Chunks->Num(); ++Index)
		{
			const TSharedPtr<FJsonObject> Object = (*Chunks)[Index]->AsObject();
			const TCHAR* EntryFields[] = {
				TEXT("assetPath"),
				TEXT("chunkPath"),
				TEXT("declarationAssetPath"),
			};
			if (!Object.IsValid() ||
				!ValidateKnownFields(Object, EntryFields, TEXT("Chunk deployment entry"), OutError))
			{
				if (OutError.IsEmpty())
				{
					OutError = FString::Printf(TEXT("Chunk deployment chunks[%d] must be an object."), Index);
				}
				return false;
			}

			FChunkDeploymentEntry& Entry = OutEntries.AddDefaulted_GetRef();
			if (!ReadRequiredString(Object, TEXT("assetPath"), Entry.AssetPath, OutError) ||
				!ReadRequiredString(Object, TEXT("chunkPath"), Entry.ChunkPath, OutError) ||
				!ReadRequiredString(
					Object,
					TEXT("declarationAssetPath"),
					Entry.DeclarationAssetPath,
					OutError))
			{
				return false;
			}
			if (!Entry.AssetPath.StartsWith(TEXT("/Game/")) ||
				!FPackageName::IsValidLongPackageName(Entry.AssetPath) ||
				AssetPaths.Contains(Entry.AssetPath))
			{
				OutError = FString::Printf(
					TEXT("Chunk deployment assetPath '%s' is invalid or duplicated."),
					*Entry.AssetPath);
				return false;
			}
			AssetPaths.Add(Entry.AssetPath);

			const FString AssetName = FPackageName::GetLongPackageAssetName(Entry.AssetPath);
			const FString ObjectPath = Entry.AssetPath + TEXT(".") + AssetName;
			Entry.Asset = LoadObject<UFinareAsset>(nullptr, *ObjectPath, nullptr, LOAD_NoWarn);
			if (Entry.Asset == nullptr)
			{
				OutError = FString::Printf(
					TEXT("Chunk deployment requires existing Finare Asset '%s'."),
					*ObjectPath);
				return false;
			}

			const FString ResolvedChunkPath = ResolveChunkPath(Entry.ChunkPath);
			if (!ResolvedChunkPath.EndsWith(TEXT(".uschunk"), ESearchCase::IgnoreCase) ||
				!ValidateChunk(ResolvedChunkPath, OutError))
			{
				if (OutError.IsEmpty())
				{
					OutError = FString::Printf(
						TEXT("Chunk deployment path '%s' is not a verified .uschunk."),
						*ResolvedChunkPath);
				}
				return false;
			}
		}
		return true;
	}

	bool SaveEntry(FChunkDeploymentEntry& Entry, FString& OutError)
	{
		UFinareAsset* Asset = Entry.Asset.Get();
		UPackage* Package = Asset != nullptr ? Asset->GetOutermost() : nullptr;
		if (Asset == nullptr || Package == nullptr)
		{
			OutError = FString::Printf(TEXT("Chunk deployment asset '%s' was unloaded."), *Entry.AssetPath);
			return false;
		}
		Asset->Modify();
		Asset->CompiledChunkPath = Entry.ChunkPath;
		Asset->DeclarationAssetPath = Entry.DeclarationAssetPath;
		Asset->MarkPackageDirty();

		const FString Filename = FPackageName::LongPackageNameToFilename(
			Entry.AssetPath,
			FPackageName::GetAssetPackageExtension());
		FSavePackageArgs SaveArguments;
		SaveArguments.TopLevelFlags = RF_Public | RF_Standalone;
		SaveArguments.SaveFlags = SAVE_NoError;
		if (!UPackage::SavePackage(Package, Asset, *Filename, SaveArguments))
		{
			OutError = FString::Printf(
				TEXT("Failed to save chunk-enabled Script Asset '%s'."),
				*Entry.AssetPath);
			return false;
		}
		return true;
	}
} // namespace UE::Finare::SyncChunks::Private

UFinareSyncChunksCommandlet::UFinareSyncChunksCommandlet()
{
	IsClient = false;
	IsEditor = true;
	LogToConsole = true;
	ShowErrorCount = true;
}

int32 UFinareSyncChunksCommandlet::Main(const FString& Params)
{
	using namespace UE::Finare::SyncChunks::Private;

	FString InputPath;
	if (!FParse::Value(*Params, TEXT("Input="), InputPath))
	{
		UE_LOG(LogTemp, Error, TEXT("FinareSyncChunks requires -Input=<manifest.json>."));
		return 1;
	}
	InputPath = FPaths::ConvertRelativePathToFull(InputPath);

	TArray<FChunkDeploymentEntry> Entries;
	FString Error;
	if (!ParseManifest(InputPath, Entries, Error))
	{
		UE_LOG(LogTemp, Error, TEXT("Finare chunk deployment preflight failed: %s"), *Error);
		return 2;
	}
	if (FParse::Param(*Params, TEXT("ValidateOnly")))
	{
		UE_LOG(
			LogTemp,
			Display,
			TEXT("Finare chunk deployment validated: Chunks=%d, Manifest=%s"),
			Entries.Num(),
			*InputPath);
		return 0;
	}

	for (FChunkDeploymentEntry& Entry : Entries)
	{
		if (!SaveEntry(Entry, Error))
		{
			UE_LOG(LogTemp, Error, TEXT("Finare chunk deployment sync failed: %s"), *Error);
			return 3;
		}
	}
	UE_LOG(
		LogTemp,
		Display,
		TEXT("Finare chunk deployment synchronized: Chunks=%d, Manifest=%s"),
		Entries.Num(),
		*InputPath);
	return 0;
}
