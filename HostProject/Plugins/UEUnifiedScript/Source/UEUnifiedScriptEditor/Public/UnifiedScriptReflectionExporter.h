#pragma once

#include "CoreMinimal.h"

struct FUnifiedScriptReflectionExportOptions
{
    FString OutputPath;
    TArray<FString> Modules;
};

struct FUnifiedScriptReflectionExportResult
{
    int32 TypeCount = 0;
    int32 PropertyCount = 0;
    int32 FunctionCount = 0;
    int32 DelegateCount = 0;
    int32 ExcludedTypeCount = 0;
};

class UEUNIFIEDSCRIPTEDITOR_API FUnifiedScriptReflectionExporter
{
public:
    static bool Export(
        const FUnifiedScriptReflectionExportOptions& Options,
        FUnifiedScriptReflectionExportResult& OutResult,
        FText& OutError);
};
