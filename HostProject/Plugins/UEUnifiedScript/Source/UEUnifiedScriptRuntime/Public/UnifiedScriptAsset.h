#pragma once

#include "CoreMinimal.h"
#include "Engine/DataAsset.h"
#include "UnifiedScriptAsset.generated.h"

/**
 * Project asset describing one managed UnifiedScript entry point.
 *
 * The asset owns program identity only. Start/tick policy remains on each host
 * component so multiple instances can run the same script differently.
 */
UCLASS(BlueprintType)
class UEUNIFIEDSCRIPTRUNTIME_API UUnifiedScriptAsset : public UDataAsset
{
    GENERATED_BODY()

public:
    UPROPERTY(VisibleAnywhere, Category="UnifiedScript")
    int32 SchemaVersion = 1;

    UPROPERTY(EditAnywhere, Category="UnifiedScript|Runtime")
    FString ScriptAssemblyPath;

    UPROPERTY(EditAnywhere, Category="UnifiedScript|Runtime")
    FString ScriptTypeName;

    /** Optional UObject base class that every host target must satisfy before managed code starts. */
    UPROPERTY(EditAnywhere, Category="UnifiedScript|Runtime", meta=(AllowAbstract="true"))
    TSoftClassPtr<UObject> RequiredTargetClass;

    UPROPERTY(EditAnywhere, Category="UnifiedScript|Authoring")
    FString SourcePath;

    /** Declaration IR emitted from the C# [ScriptAsset] source. Supports {PluginDir} and {ProjectDir}. */
    UPROPERTY(EditAnywhere, Category="UnifiedScript|Authoring")
    FString DeclarationPath;

    /** Asset path inside a declaration document containing multiple script assets. */
    UPROPERTY(EditAnywhere, Category="UnifiedScript|Authoring")
    FString DeclarationAssetPath;

    /** Authoring Surface v0.3 consumed by the native Slate GraphEditor workspace. */
    UPROPERTY(EditAnywhere, Category="UnifiedScript|Authoring")
    FString AuthoringSurfacePath;

    /** Loopback Workbench endpoint used by the Slate Apply flow. */
    UPROPERTY(EditAnywhere, Category="UnifiedScript|Authoring")
    FString AuthoringServerUrl = TEXT("http://127.0.0.1:4173");

    bool ResolveConfiguration(
        FString& OutAssemblyPath,
        FString& OutTypeName,
        FString& OutError) const;

    /** Validates the concrete component target against RequiredTargetClass without starting CoreCLR. */
    bool ValidateTarget(const UObject* Target, FString& OutError) const;

    FString ResolveRequiredTargetClassPath() const;

    void ResolveDeclarationConfiguration(
        FString& OutDeclarationPath,
        FString& OutDeclarationAssetPath) const;

    FString ResolveAuthoringSurfacePath() const;

    FString ResolveAuthoringServerUrl() const;
};
