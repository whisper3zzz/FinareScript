#pragma once

#include "CoreMinimal.h"

struct FUnifiedScriptReflectionExportOptions
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

struct FUnifiedScriptReflectionExportResult
{
    int32 TypeCount = 0;
    int32 PropertyCount = 0;
    int32 FunctionCount = 0;
    int32 DelegateCount = 0;
    int32 ExcludedTypeCount = 0;
    int32 DiscoveredClassCount = 0;
    int32 DiscoveredBlueprintClassCount = 0;
};

class UEUNIFIEDSCRIPTEDITOR_API FUnifiedScriptReflectionExporter
{
public:
    static bool Export(
        const FUnifiedScriptReflectionExportOptions& Options,
        FUnifiedScriptReflectionExportResult& OutResult,
        FText& OutError);
};
