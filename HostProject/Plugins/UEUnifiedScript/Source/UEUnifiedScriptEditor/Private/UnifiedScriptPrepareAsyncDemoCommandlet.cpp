#include "UnifiedScriptPrepareAsyncDemoCommandlet.h"

#include "UnifiedScriptActor.h"
#include "UnifiedScriptAsset.h"
#include "UnifiedScriptBlueprint.h"
#include "UnifiedScriptBlueprintClassMaterializer.h"
#include "UnifiedScriptComponent.h"
#include "Camera/CameraActor.h"
#include "Camera/CameraComponent.h"
#include "Components/DirectionalLightComponent.h"
#include "Components/SkyLightComponent.h"
#include "Editor.h"
#include "Engine/DirectionalLight.h"
#include "Engine/SkyLight.h"
#include "Engine/World.h"
#include "EngineUtils.h"
#include "FileHelpers.h"
#include "GameFramework/GameModeBase.h"
#include "GameFramework/WorldSettings.h"
#include "Kismet2/KismetEditorUtilities.h"
#include "Misc/FileHelper.h"
#include "Misc/PackageName.h"
#include "UObject/SavePackage.h"

UUnifiedScriptPrepareAsyncDemoCommandlet::UUnifiedScriptPrepareAsyncDemoCommandlet()
{
    IsClient = false;
    IsServer = false;
    IsEditor = true;
    LogToConsole = true;
}

int32 UUnifiedScriptPrepareAsyncDemoCommandlet::Main(const FString& Params)
{
    const FString ClassPackage = TEXT("/Game/UnifiedScriptDemo/BP_AsyncUiDemo");
    const FString ClassPath = ClassPackage + TEXT(".BP_AsyncUiDemo_C");
    const FString MapName = TEXT("/Game/UnifiedScriptDemo/L_AsyncUiDemo");
    const FName MapOwner(TEXT("UnifiedScript.AsyncUiDemo.v1"));
    FGuid ClassId;
    FGuid::ParseExact(TEXT("762043152e714d68b05a31426591ee03"), EGuidFormats::Digits, ClassId);
    auto Fail = [](const FString& Message)
    {
        UE_LOG(LogTemp, Error, TEXT("Async UI Demo: %s"), *Message);
        return 1;
    };
    UClass* GameMode = LoadClass<AGameModeBase>(nullptr,
        TEXT("/Script/UEUnifiedScriptHost.UnifiedScriptAsyncDemoGameMode"));
    if (!GEditor || !GameMode) return Fail(TEXT("Run in the repository UEUnifiedScriptHost project."));

    const FString DeclarationPath = FPaths::ProjectPluginsDir() /
        TEXT("UEUnifiedScript/Binaries/Managed/Demo/UnifiedScript.Unreal.Demo.asset.json");
    FString Declaration;
    if (!FFileHelper::LoadFileToString(Declaration, *DeclarationPath))
        return Fail(TEXT("Emit the Release Demo chunk and declaration first."));

    // Resolve both destinations before changing either. A similarly named user
    // asset is not evidence of ownership and must never be adopted implicitly.
    UWorld* Map = nullptr;
    if (FPackageName::DoesPackageExist(MapName))
    {
        Map = LoadObject<UWorld>(nullptr, *(MapName + TEXT(".L_AsyncUiDemo")));
        if (!Map || !Map->GetWorldSettings()->ActorHasTag(MapOwner))
            return Fail(TEXT("Existing Demo map has no matching ownership marker; it was not changed."));
    }
    UUnifiedScriptBlueprint* Blueprint = nullptr;
    if (FPackageName::DoesPackageExist(ClassPackage))
    {
        Blueprint = LoadObject<UUnifiedScriptBlueprint>(nullptr, *(ClassPackage + TEXT(".BP_AsyncUiDemo")));
        if (!Blueprint || !Blueprint->ScriptAsset || Blueprint->ScriptAsset->ScriptClassId != ClassId ||
            Blueprint->ScriptAsset->ScriptTypeName != TEXT("UnifiedScript.Unreal.Demo.AsyncUiDemoScript") ||
            Blueprint->ScriptAsset->GeneratedClassPath.ToString() != ClassPath ||
            Blueprint->ScriptAsset->ScriptClassKind != EUnifiedScriptClassKind::Actor)
            return Fail(TEXT("Existing Demo class has a different identity; it was not changed."));
    }
    else
    {
        if (Map) return Fail(TEXT("Existing Demo scene is missing its Script Class; repair its reference explicitly."));
        UPackage* Package = CreatePackage(*ClassPackage);
        Blueprint = Cast<UUnifiedScriptBlueprint>(FKismetEditorUtilities::CreateBlueprint(
            AUnifiedScriptActor::StaticClass(), Package, TEXT("BP_AsyncUiDemo"), BPTYPE_Normal,
            UUnifiedScriptBlueprint::StaticClass(), UUnifiedScriptBlueprintGeneratedClass::StaticClass()));
        if (!Blueprint) return Fail(TEXT("Could not create the Demo Script Class."));
        auto* Asset = NewObject<UUnifiedScriptAsset>(Package, TEXT("AsyncUiScript"), RF_Public | RF_Standalone);
        Asset->ScriptClassId = ClassId;
        Asset->ScriptClassKind = EUnifiedScriptClassKind::Actor;
        Asset->GeneratedClassPath = FSoftClassPath(ClassPath);
        Asset->ScriptTypeName = TEXT("UnifiedScript.Unreal.Demo.AsyncUiDemoScript");
        Asset->RequiredTargetClass = AUnifiedScriptActor::StaticClass();
        Asset->DeclarationAssetPath = TEXT("Unreal/AsyncUiDemo");
        Asset->DeclarationPath = TEXT("{PluginDir}/Binaries/Managed/Demo/UnifiedScript.Unreal.Demo.asset.json");
        Asset->CompiledChunkPath = TEXT("{PluginDir}/Binaries/Managed/Demo/UnifiedScript.Unreal.Demo.uschunk");
        Asset->ScriptAssemblyPath = TEXT("{PluginDir}/Binaries/Managed/Demo/UnifiedScript.Unreal.Demo.dll");
        Asset->SourcePath = TEXT("{ProjectDir}/../samples/Unreal/UnifiedScript.Unreal.Demo/AsyncUiDemoScript.cs");
        Asset->ApiNodeCatalogPath = TEXT("{ProjectDir}/../samples/Unreal/UMG.api-nodes.json");
        Blueprint->ScriptAsset = Asset;
    }

    auto* Asset = Blueprint->ScriptAsset.Get();
    const FString SurfacePath = TEXT("{PluginDir}/Binaries/Managed/Demo/UnifiedScript.Unreal.Demo.surface.json");
    // Upgrade the owned sample's authoring association without touching scene/default edits.
    const bool bSetSurfacePath = Asset->AuthoringSurfacePath.IsEmpty();
    if (bSetSurfacePath) Asset->AuthoringSurfacePath = SurfacePath;
    FUnifiedScriptBlueprintClassSyncResult Result;
    FString Error;
    if (!FUnifiedScriptBlueprintClassMaterializer::SynchronizeJson(*Asset, *Blueprint, Declaration, false, Result, Error))
        return Fail(Error);
    auto* CDO = Cast<AUnifiedScriptActor>(Blueprint->GeneratedClass->GetDefaultObject());
    if (!CDO) return Fail(TEXT("Generated Demo class is not an Actor."));
    if (bSetSurfacePath || CDO->GetUnifiedScriptComponent()->ScriptAsset != Asset)
    {
        CDO->GetUnifiedScriptComponent()->ScriptAsset = Asset;
        FSavePackageArgs Save;
        Save.TopLevelFlags = RF_Public | RF_Standalone;
        Save.SaveFlags = SAVE_NoError;
        if (!UPackage::SavePackage(Blueprint->GetOutermost(), Blueprint,
            *FPackageName::LongPackageNameToFilename(ClassPackage, FPackageName::GetAssetPackageExtension()), Save))
            return Fail(TEXT("Could not save the Demo host association."));
    }

    if (!Map)
    {
        Map = GEditor->NewMap();
        if (!Map) return Fail(TEXT("Could not create the Demo scene."));
        Map->GetWorldSettings()->DefaultGameMode = GameMode;
        Map->GetWorldSettings()->Tags.Add(MapOwner);
        Map->SpawnActor<AUnifiedScriptActor>(Blueprint->GeneratedClass)->SetActorLabel(TEXT("Async UI Gameplay (C#)"));
        const FVector CameraPosition(-350, 450, 320);
        auto* Camera = Map->SpawnActor<ACameraActor>(CameraPosition,
            (FVector(200, -110, 100) - CameraPosition).Rotation());
        Camera->Tags.Add(TEXT("UnifiedScript.AsyncUiCamera"));
        Camera->GetCameraComponent()->SetFieldOfView(55);
        auto* Light = Map->SpawnActor<ADirectionalLight>(FVector::ZeroVector, FRotator(-45, -30, 0));
        Light->GetLightComponent()->SetMobility(EComponentMobility::Movable);
        Light->GetLightComponent()->SetIntensity(5);
        auto* Sky = Map->SpawnActor<ASkyLight>();
        Sky->GetLightComponent()->SetMobility(EComponentMobility::Movable);
        Sky->GetLightComponent()->SetIntensity(1);
        if (!UEditorLoadingAndSavingUtils::SaveMap(Map, MapName)) return Fail(TEXT("Could not save the Demo scene."));
    }
    UE_LOG(LogTemp, Display, TEXT("UNIFIEDSCRIPT_ASYNC_UI_PREPARED %s (existing scenes are preserved)"), *MapName);
    return 0;
}
