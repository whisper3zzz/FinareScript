#pragma once

#include "CoreMinimal.h"
#include "Engine/DataAsset.h"
#include "FinareClassBinding.h"
#include "FinareAsset.generated.h"

UENUM(BlueprintType)
enum class EFinareClassKind : uint8
{
    None,
    Actor,
    ActorComponent,
    Object,
    BlueprintFunctionLibrary,
};

/**
 * Project asset describing one managed Finare entry point.
 *
 * The asset owns program identity only. Start/tick policy remains on each host
 * component so multiple instances can run the same script differently.
 */
UCLASS(BlueprintType)
class FINARERUNTIME_API UFinareAsset : public UDataAsset
{
    GENERATED_BODY()

public:
    UPROPERTY(VisibleAnywhere, Category="Finare")
    int32 SchemaVersion = 1;

    /** Stable semantic identity written into the managed [ScriptAsset] declaration. */
    UPROPERTY(VisibleAnywhere, Category="Finare|Class")
    FGuid ScriptClassId;

    /** Determines whether the generated Blueprint proxy is placeable or attachable. */
    UPROPERTY(VisibleAnywhere, Category="Finare|Class")
    EFinareClassKind ScriptClassKind = EFinareClassKind::None;

    /** Exact generated Blueprint class path; never resolved by short name. */
    UPROPERTY(VisibleAnywhere, Category="Finare|Class")
    FSoftClassPath GeneratedClassPath;

    /** Preferred verified .uschunk program. Falls back to ScriptAssemblyPath during migration. */
    UPROPERTY(EditAnywhere, Category="Finare|Runtime")
    FString CompiledChunkPath;

    UPROPERTY(EditAnywhere, Category="Finare|Runtime")
    FString ScriptAssemblyPath;

    UPROPERTY(EditAnywhere, Category="Finare|Runtime")
    FString ScriptTypeName;

    /** Optional UObject base class that every host target must satisfy before managed code starts. */
    UPROPERTY(EditAnywhere, Category="Finare|Runtime", meta=(AllowAbstract="true"))
    TSoftClassPtr<UObject> RequiredTargetClass;

    UPROPERTY(EditAnywhere, Category="Finare|Authoring")
    FString SourcePath;

    /** Declaration IR emitted from the C# [ScriptAsset] source. Supports {PluginDir} and {ProjectDir}. */
    UPROPERTY(EditAnywhere, Category="Finare|Authoring")
    FString DeclarationPath;

    /** Asset path inside a declaration document containing multiple script assets. */
    UPROPERTY(EditAnywhere, Category="Finare|Authoring")
    FString DeclarationAssetPath;

    /** Authoring Surface v0.3 consumed by the native Slate GraphEditor workspace. */
    UPROPERTY(EditAnywhere, Category="Finare|Authoring")
    FString AuthoringSurfacePath;

    /** Contract-generated Unreal API node catalog consumed by the Slate node palette. */
    UPROPERTY(EditAnywhere, Category="Finare|Authoring")
    FString ApiNodeCatalogPath;

    /** Loopback Workbench endpoint used by the Slate Apply flow. */
    UPROPERTY(EditAnywhere, Category="Finare|Authoring")
    FString AuthoringServerUrl = TEXT("http://127.0.0.1:4173");

    bool ResolveConfiguration(
        FString& OutAssemblyPath,
        FString& OutTypeName,
        FString& OutError) const;

    /** Resolves either the preferred chunk or the retained loose assembly explicitly. */
    bool ResolveConfiguration(
        bool bPreferCompiledChunk,
        FString& OutAssemblyPath,
        FString& OutTypeName,
        FString& OutError) const;

    /** Resolves the selected program to an absolute runtime path, expanding deployment placeholders. */
    bool ResolveRuntimeConfiguration(
        bool bPreferCompiledChunk,
        FString& OutAssemblyPath,
        FString& OutTypeName,
        FString& OutError) const;

    /** Validates the concrete component target against RequiredTargetClass without starting CoreCLR. */
    bool ValidateTarget(const UObject* Target, FString& OutError) const;

    /** Rejects stale or similarly named declaration output for a persistent Script Class. */
    bool ValidateDeclarationIdentity(
        const FGuid& DeclarationId,
        const FString& DeclarationQualifiedName,
        FString& OutError) const;

    /** Validates declaration class-composition metadata against this selected asset owner. */
    bool ValidateDeclarationClassBinding(
        const TOptional<FFinareClassBindingDeclaration>& Binding,
        FString& OutError) const;

    /** Rejects a host/target that is not an instance of the exact generated proxy class. */
    bool ValidateGeneratedClassIdentity(
        const UObject* Host,
        const UObject* Target,
        FString& OutError) const;

    FString ResolveRequiredTargetClassPath() const;

    FString ResolveCompiledChunkPath() const;

    void ResolveDeclarationConfiguration(
        FString& OutDeclarationPath,
        FString& OutDeclarationAssetPath) const;

    FString ResolveAuthoringSurfacePath() const;

    FString ResolveApiNodeCatalogPath() const;

    FString ResolveAuthoringServerUrl() const;
};
