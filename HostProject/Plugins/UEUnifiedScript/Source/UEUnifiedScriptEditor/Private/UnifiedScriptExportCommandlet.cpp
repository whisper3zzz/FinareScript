#include "UnifiedScriptExportCommandlet.h"

#include "Misc/CommandLine.h"
#include "Misc/Parse.h"
#include "UnifiedScriptReflectionExporter.h"

UUnifiedScriptExportCommandlet::UUnifiedScriptExportCommandlet()
{
    IsClient = false;
    IsEditor = true;
    IsServer = false;
    LogToConsole = true;
    ShowErrorCount = true;
}

int32 UUnifiedScriptExportCommandlet::Main(const FString& Params)
{
    FUnifiedScriptReflectionExportOptions Options;
    if (!FParse::Value(*Params, TEXT("Output="), Options.OutputPath) || Options.OutputPath.IsEmpty())
    {
        UE_LOG(LogTemp, Error, TEXT("UnifiedScriptExport requires -Output=<absolute manifest path>."));
        return 64;
    }

    FString ModuleList;
    if (FParse::Value(*Params, TEXT("Modules="), ModuleList))
    {
        ModuleList.ParseIntoArray(Options.Modules, TEXT(","), true);
    }
    if (Options.Modules.IsEmpty())
    {
        Options.Modules.Add(TEXT("UEUnifiedScriptRuntime"));
    }

    FUnifiedScriptReflectionExportResult Result;
    FText Error;
    if (!FUnifiedScriptReflectionExporter::Export(Options, Result, Error))
    {
        UE_LOG(LogTemp, Error, TEXT("UnifiedScript reflection export failed: %s"), *Error.ToString());
        return 1;
    }

    UE_LOG(
        LogTemp,
        Display,
        TEXT("UnifiedScript reflection export wrote %d types, %d properties, and %d functions to %s"),
        Result.TypeCount,
        Result.PropertyCount,
        Result.FunctionCount,
        *Options.OutputPath);
    return 0;
}
