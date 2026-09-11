#pragma once

#include "CoreMinimal.h"

struct FFinareReflectionExportOptions
{
	FString OutputPath;
	TArray<FString> Modules;

	/** Optional exact /Script/Module.Type allowlist. Empty exports every native class in Modules. */
	TArray<FString> ClassPaths;

	/** Optional generated C# short names resolved uniquely across native and bounded Blueprint classes. */
	TArray<FString> TypeNames;

	/** Optional exact Blueprint asset object paths whose generated classes must be loaded and exported. */
	TArray<FString> BlueprintAssetPaths;
};

struct FFinareReflectionExportResult
{
	int32 TypeCount = 0;
	int32 PropertyCount = 0;
	int32 FunctionCount = 0;
	int32 DelegateCount = 0;
	int32 ExcludedTypeCount = 0;
	int32 DiscoveredClassCount = 0;
	int32 DiscoveredBlueprintClassCount = 0;
};

class FINAREEDITOR_API FFinareReflectionExporter
{
public:
	static bool Export(
		const FFinareReflectionExportOptions& Options,
		FFinareReflectionExportResult& OutResult,
		FText& OutError);
};
