#include "FinareExportCommandlet.h"

#include "Misc/CommandLine.h"
#include "Misc/Parse.h"
#include "FinareReflectionExporter.h"

UFinareExportCommandlet::UFinareExportCommandlet()
{
    IsClient = false;
    IsEditor = true;
    IsServer = false;
    LogToConsole = true;
    ShowErrorCount = true;
}

int32 UFinareExportCommandlet::Main(const FString& Params)
{
    FFinareReflectionExportOptions Options;
    if (!FParse::Value(*Params, TEXT("Output="), Options.OutputPath) || Options.OutputPath.IsEmpty())
    {
        UE_LOG(LogTemp, Error, TEXT("FinareExport requires -Output=<absolute manifest path>."));
        return 64;
    }

    FString ModuleList;
    if (FParse::Value(*Params, TEXT("Modules="), ModuleList, false))
    {
        ModuleList.ParseIntoArray(Options.Modules, TEXT(","), true);
    }
    if (Options.Modules.IsEmpty())
    {
        Options.Modules.Add(TEXT("FinareRuntime"));
    }

    FString ClassList;
    if (FParse::Value(*Params, TEXT("Classes="), ClassList, false))
    {
        ClassList.ParseIntoArray(Options.ClassPaths, TEXT(","), true);
    }

    FString TypeNameList;
    if (FParse::Value(*Params, TEXT("TypeNames="), TypeNameList, false))
    {
        TypeNameList.ParseIntoArray(Options.TypeNames, TEXT(","), true);
    }

    FString BlueprintAssetList;
    if (FParse::Value(*Params, TEXT("BlueprintAssets="), BlueprintAssetList, false))
    {
        BlueprintAssetList.ParseIntoArray(Options.BlueprintAssetPaths, TEXT(","), true);
    }

    FFinareReflectionExportResult Result;
    FText Error;
    if (!FFinareReflectionExporter::Export(Options, Result, Error))
    {
        UE_LOG(LogTemp, Error, TEXT("Finare reflection export failed: %s"), *Error.ToString());
        return 1;
    }

    UE_LOG(
        LogTemp,
        Display,
        TEXT("Finare reflection export wrote %d types, %d properties, %d functions, and %d delegates to %s (%d source-discovered classes, %d Blueprint classes, %d internal types excluded)"),
        Result.TypeCount,
        Result.PropertyCount,
        Result.FunctionCount,
        Result.DelegateCount,
        *Options.OutputPath,
        Result.DiscoveredClassCount,
        Result.DiscoveredBlueprintClassCount,
        Result.ExcludedTypeCount);
    return 0;
}
