#include "FinareBlueprintClassMaterializer.h"

#include "Engine/Blueprint.h"
#include "Engine/BlueprintGeneratedClass.h"
#include "EdGraph/EdGraph.h"
#include "Editor.h"
#include "Editor/Transactor.h"
#include "GameFramework/Actor.h"
#include "Kismet2/KismetEditorUtilities.h"
#include "HAL/FileManager.h"
#include "Misc/AutomationTest.h"
#include "Misc/FileHelper.h"
#include "Misc/ITransaction.h"
#include "Misc/PackageName.h"
#include "Misc/Paths.h"
#include "Misc/ScopeExit.h"
#include "Dom/JsonObject.h"
#include "Serialization/JsonReader.h"
#include "Serialization/JsonSerializer.h"
#include "FinareActor.h"
#include "FinareAsset.h"
#include "FinareBlueprint.h"
#include "FinareComponent.h"
#include "UObject/Package.h"
#include "UObject/StrongObjectPtr.h"
#include "UObject/UnrealType.h"
#include "UObject/WeakObjectPtr.h"

namespace UE::Finare::ClassParentTests::Private
{
FString FixtureDeclaration()
{
    const FString Path = FPaths::ConvertRelativePathToFull(FPaths::Combine(
        FPaths::ProjectDir(),
        TEXT("../artifacts/m13-3c-4-class-composition/class-binding/class-binding-fixture.asset.json")));
    FString Result;
    FFileHelper::LoadFileToString(Result, *Path);
    return Result;
}

FString WithBaseClassPath(
    const FString& Source,
    const FString& ParentPath,
    bool bClearBase = false)
{
    TSharedPtr<FJsonObject> Root;
    const TSharedRef<TJsonReader<>> Reader = TJsonReaderFactory<>::Create(Source);
    if (!FJsonSerializer::Deserialize(Reader, Root) || !Root.IsValid())
    {
        return FString();
    }
    const TArray<TSharedPtr<FJsonValue>>* Assets = nullptr;
    if (!Root->TryGetArrayField(TEXT("assets"), Assets) || Assets == nullptr || Assets->Num() != 1)
    {
        return FString();
    }
    const TSharedPtr<FJsonObject> Asset = (*Assets)[0]->AsObject();
    const TSharedPtr<FJsonValue> BindingValue = Asset.IsValid()
        ? Asset->TryGetField(TEXT("classBinding"))
        : nullptr;
    if (!BindingValue.IsValid())
    {
        return FString();
    }
    const TSharedPtr<FJsonObject> Binding = BindingValue->AsObject();
    if (!Binding.IsValid())
    {
        return FString();
    }
    if (bClearBase)
    {
        Binding->SetField(TEXT("baseClassPath"), MakeShared<FJsonValueNull>());
    }
    else
    {
        Binding->SetStringField(TEXT("baseClassPath"), ParentPath);
    }
    FString Result;
    const TSharedRef<TJsonWriter<>> Writer = TJsonWriterFactory<>::Create(&Result);
    return FJsonSerializer::Serialize(Root.ToSharedRef(), Writer) ? Result : FString();
}

FString WithBaseClassPathAndStateName(
    const FString& Source,
    const FString& ParentPath,
    const FString& StateName)
{
    const FString ResultWithParent = WithBaseClassPath(Source, ParentPath);
    TSharedPtr<FJsonObject> Root;
    const TSharedRef<TJsonReader<>> Reader = TJsonReaderFactory<>::Create(ResultWithParent);
    if (!FJsonSerializer::Deserialize(Reader, Root) || !Root.IsValid())
    {
        return FString();
    }
    const TArray<TSharedPtr<FJsonValue>>* Assets = nullptr;
    if (!Root->TryGetArrayField(TEXT("assets"), Assets) || Assets == nullptr || Assets->Num() != 1)
    {
        return FString();
    }
    const TSharedPtr<FJsonObject> Asset = (*Assets)[0]->AsObject();
    const TArray<TSharedPtr<FJsonValue>>* States = nullptr;
    if (!Asset.IsValid() || !Asset->TryGetArrayField(TEXT("states"), States) ||
        States == nullptr || States->Num() != 1 || !(*States)[0].IsValid() ||
        !(*States)[0]->AsObject().IsValid())
    {
        return FString();
    }
    (*States)[0]->AsObject()->SetStringField(TEXT("name"), StateName);
    FString Result;
    const TSharedRef<TJsonWriter<>> Writer = TJsonWriterFactory<>::Create(&Result);
    return FJsonSerializer::Serialize(Root.ToSharedRef(), Writer) ? Result : FString();
}

FString WithIncompatibleCountType(
    const FString& Source,
    const FString& ParentPath)
{
    const FString ResultWithParent = WithBaseClassPath(Source, ParentPath);
    TSharedPtr<FJsonObject> Root;
    const TSharedRef<TJsonReader<>> Reader = TJsonReaderFactory<>::Create(ResultWithParent);
    if (!FJsonSerializer::Deserialize(Reader, Root) || !Root.IsValid())
    {
        return FString();
    }
    const TArray<TSharedPtr<FJsonValue>>* Assets = nullptr;
    if (!Root->TryGetArrayField(TEXT("assets"), Assets) || Assets == nullptr || Assets->Num() != 1)
    {
        return FString();
    }
    const TSharedPtr<FJsonObject> Asset = (*Assets)[0]->AsObject();
    const TArray<TSharedPtr<FJsonValue>>* States = nullptr;
    if (!Asset.IsValid() || !Asset->TryGetArrayField(TEXT("states"), States) ||
        States == nullptr || States->Num() != 1 || !(*States)[0].IsValid() ||
        !(*States)[0]->AsObject().IsValid())
    {
        return FString();
    }
    const TSharedPtr<FJsonObject> State = (*States)[0]->AsObject();
    State->SetStringField(TEXT("typeDisplayName"), TEXT("bool"));
    State->SetStringField(TEXT("typeMetadataName"), TEXT("System.Boolean"));
    State->SetStringField(TEXT("typeKind"), TEXT("boolean"));
    State->SetBoolField(TEXT("defaultValue"), true);
    FString Result;
    const TSharedRef<TJsonWriter<>> Writer = TJsonWriterFactory<>::Create(&Result);
    return FJsonSerializer::Serialize(Root.ToSharedRef(), Writer) ? Result : FString();
}

UFinareBlueprint* CreateManagedBlueprint(const FString& Name, UPackage*& OutPackage)
{
    OutPackage = CreatePackage(*(TEXT("/Game/Temp/") + Name));
    return Cast<UFinareBlueprint>(FKismetEditorUtilities::CreateBlueprint(
        AFinareActor::StaticClass(),
        OutPackage,
        FName(*Name),
        BPTYPE_Normal,
        UFinareBlueprint::StaticClass(),
        UFinareBlueprintGeneratedClass::StaticClass(),
        FName(TEXT("FinareClassParentChange"))));
}

UBlueprint* CreateOrdinaryBlueprint(
    UClass* ParentClass,
    const FString& Name,
    UPackage*& OutPackage)
{
    OutPackage = CreatePackage(*(TEXT("/Engine/Transient/") + Name));
    return FKismetEditorUtilities::CreateBlueprint(
        ParentClass,
        OutPackage,
        FName(*Name),
        BPTYPE_Normal,
        UBlueprint::StaticClass(),
        UBlueprintGeneratedClass::StaticClass(),
        FName(TEXT("FinareClassParentChange")));
}
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
    FFinareClassParentChangeTest,
    "Finare.Unreal.ScriptClass.ParentChange.Primitive",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FFinareClassParentChangeTest::RunTest(const FString& Parameters)
{
    using namespace UE::Finare::ClassParentTests::Private;

    const auto GetScriptComponent = [](UBlueprint* Candidate) -> UFinareComponent*
    {
        if (Candidate == nullptr || Candidate->GeneratedClass == nullptr)
        {
            return nullptr;
        }
        if (AFinareActor* ActorDefaults =
                Cast<AFinareActor>(Candidate->GeneratedClass->GetDefaultObject()))
        {
            return ActorDefaults->ScriptComponent;
        }
        return Cast<UFinareComponent>(Candidate->GeneratedClass->GetDefaultObject());
    };

    const FString Declaration = FixtureDeclaration();
    if (!TestTrue(TEXT("Loads actual emitted class binding fixture"), !Declaration.IsEmpty()))
    {
        return false;
    }

    TSharedPtr<FJsonObject> Root;
    const TSharedRef<TJsonReader<>> Reader = TJsonReaderFactory<>::Create(Declaration);
    const TArray<TSharedPtr<FJsonValue>>* Assets = nullptr;
    if (!TestTrue(
            TEXT("Emitted class binding fixture has one asset identity"),
            FJsonSerializer::Deserialize(Reader, Root) && Root.IsValid() &&
            Root->TryGetArrayField(TEXT("assets"), Assets) && Assets != nullptr &&
            Assets->Num() == 1))
    {
        return false;
    }
    const TSharedPtr<FJsonObject> AssetObject = (*Assets)[0]->AsObject();
    FString AssetIdText;
    FString QualifiedName;
    FString DeclarationAssetPath;
    if (!TestTrue(
            TEXT("Emitted class binding fixture exposes identity and selector"),
            AssetObject.IsValid() &&
            AssetObject->TryGetStringField(TEXT("id"), AssetIdText) &&
            AssetObject->TryGetStringField(TEXT("qualifiedName"), QualifiedName) &&
            AssetObject->TryGetStringField(TEXT("assetPath"), DeclarationAssetPath)))
    {
        return false;
    }
    AssetIdText.RemoveFromStart(TEXT("us_"));
    FGuid FixtureClassId;
    if (!TestTrue(
            TEXT("Emitted class binding fixture identity parses"),
            FGuid::ParseExact(AssetIdText, EGuidFormats::Digits, FixtureClassId)))
    {
        return false;
    }

    const FString Name = TEXT("ParentChange_") +
        FGuid::NewGuid().ToString(EGuidFormats::Digits);
    UPackage* ManagedPackage = nullptr;
    UFinareBlueprint* ManagedBlueprint = CreateManagedBlueprint(Name, ManagedPackage);
    if (!TestNotNull(TEXT("Creates managed Blueprint proxy"), ManagedBlueprint) ||
        !TestNotNull(
            TEXT("Managed Blueprint has generated class"),
            ManagedBlueprint != nullptr ? ManagedBlueprint->GeneratedClass.Get() : nullptr))
    {
        return false;
    }

    UFinareAsset* ScriptAsset = NewObject<UFinareAsset>(ManagedPackage);
    ScriptAsset->SetFlags(RF_Public | RF_Standalone);
    const FString ManagedPackageFilename = FPackageName::LongPackageNameToFilename(
        ManagedPackage->GetName(),
        FPackageName::GetAssetPackageExtension());
    const FString ExternalProbeRoot = FPaths::ConvertRelativePathToFull(FPaths::Combine(
        FPaths::ProjectSavedDir(),
        TEXT("Finare/ParentChangeExternal_") + FGuid::NewGuid().ToString(EGuidFormats::Digits)));
    const FString ExistingExternalPath = FPaths::Combine(ExternalProbeRoot, TEXT("existing.output"));
    const FString CreatedExternalPath = FPaths::Combine(ExternalProbeRoot, TEXT("created.output"));
    if (!TestFalse(
            TEXT("External publication existing probe path is initially absent"),
            IFileManager::Get().FileExists(*ExistingExternalPath)) ||
        !TestFalse(
            TEXT("External publication created probe path is initially absent"),
            IFileManager::Get().FileExists(*CreatedExternalPath)))
    {
        return false;
    }
    ON_SCOPE_EXIT
    {
        IFileManager::Get().Delete(*ManagedPackageFilename, false, true, true);
        IFileManager::Get().Delete(*ExistingExternalPath, false, true, true);
        IFileManager::Get().Delete(*CreatedExternalPath, false, true, true);
        IFileManager::Get().DeleteDirectory(*ExternalProbeRoot, false, true);
    };
    ScriptAsset->ScriptClassId = FixtureClassId;
    ScriptAsset->ScriptClassKind = EFinareClassKind::Actor;
    ScriptAsset->GeneratedClassPath = FSoftClassPath(ManagedBlueprint->GeneratedClass);
    ScriptAsset->ScriptTypeName = QualifiedName;
    ScriptAsset->DeclarationAssetPath = DeclarationAssetPath;
    ManagedBlueprint->ScriptAsset = ScriptAsset;

    FFinareBlueprintClassSyncResult Result;
    FString Error;
    if (!TestTrue(
            TEXT("Initial emitted declaration synchronizes"),
            FFinareBlueprintClassMaterializer::SynchronizeJson(
                *ScriptAsset,
                *ManagedBlueprint,
                Declaration,
                false,
                Result,
                Error)))
    {
        return false;
    }

    // The factory configures the post-materialization CDO.  Set that owner at
    // the same lifecycle point before exercising a parent transition, then
    // prove the transition preserves it.
    AFinareActor* InitialDefaults =
        ManagedBlueprint->GeneratedClass->GetDefaultObject<AFinareActor>();
    if (InitialDefaults != nullptr && InitialDefaults->ScriptComponent != nullptr)
    {
        InitialDefaults->ScriptComponent->ScriptAsset = ScriptAsset;
    }
    TestTrue(
        TEXT("Post-materialization CDO owns the managed ScriptAsset before parent change"),
        InitialDefaults != nullptr &&
        InitialDefaults->ScriptComponent != nullptr &&
        InitialDefaults->ScriptComponent->ScriptAsset == ScriptAsset);

    UPackage* ChildPackage = nullptr;
    UBlueprint* ChildBlueprint = CreateOrdinaryBlueprint(
        ManagedBlueprint->GeneratedClass,
        Name + TEXT("_Child"),
        ChildPackage);
    UPackage* GrandchildPackage = nullptr;
    UBlueprint* GrandchildBlueprint = ChildBlueprint != nullptr
        ? CreateOrdinaryBlueprint(
            ChildBlueprint->GeneratedClass,
            Name + TEXT("_Grandchild"),
            GrandchildPackage)
        : nullptr;
    TestNotNull(TEXT("Creates ordinary child before parent change"), ChildBlueprint);
    TestNotNull(TEXT("Creates ordinary grandchild before parent change"), GrandchildBlueprint);
    TestTrue(
        TEXT("Pre-change ordinary child CDO owns the managed ScriptAsset"),
        ChildBlueprint != nullptr &&
        Cast<AFinareActor>(ChildBlueprint->GeneratedClass->GetDefaultObject()) != nullptr &&
        Cast<AFinareActor>(ChildBlueprint->GeneratedClass->GetDefaultObject())->ScriptComponent != nullptr &&
        Cast<AFinareActor>(ChildBlueprint->GeneratedClass->GetDefaultObject())->ScriptComponent->ScriptAsset == ScriptAsset);
    TestTrue(
        TEXT("Pre-change ordinary grandchild CDO owns the managed ScriptAsset"),
        GrandchildBlueprint != nullptr &&
        Cast<AFinareActor>(GrandchildBlueprint->GeneratedClass->GetDefaultObject()) != nullptr &&
        Cast<AFinareActor>(GrandchildBlueprint->GeneratedClass->GetDefaultObject())->ScriptComponent != nullptr &&
        Cast<AFinareActor>(GrandchildBlueprint->GeneratedClass->GetDefaultObject())->ScriptComponent->ScriptAsset == ScriptAsset);
    UFinareComponent* ComponentBeforeValidate =
        InitialDefaults != nullptr ? InitialDefaults->ScriptComponent : nullptr;

    const UClass* OriginalParent = ManagedBlueprint->ParentClass;
    const FString OriginalGeneratedPath = ManagedBlueprint->GeneratedClass->GetPathName();
    TArray<FGuid> OriginalStateGuids;
    TArray<FGuid> OriginalGraphGuids;
    auto CaptureGraphGuids = [&OriginalGraphGuids](const TArray<UEdGraph*>& Graphs)
    {
        for (UEdGraph* Graph : Graphs)
        {
            if (Graph != nullptr)
            {
                OriginalGraphGuids.Add(Graph->GraphGuid);
            }
        }
    };
    CaptureGraphGuids(ManagedBlueprint->FunctionGraphs);
    CaptureGraphGuids(ManagedBlueprint->UbergraphPages);
    CaptureGraphGuids(ManagedBlueprint->MacroGraphs);
    for (const FBPVariableDescription& Variable : ManagedBlueprint->NewVariables)
    {
        if (Variable.HasMetaData(TEXT("UnifiedScript.ManagedState")))
        {
            OriginalStateGuids.Add(Variable.VarGuid);
        }
    }

    UPackage* ParentPackage = nullptr;
    UBlueprint* OrdinaryParent = CreateOrdinaryBlueprint(
        AFinareActor::StaticClass(),
        Name + TEXT("_Parent"),
        ParentPackage);
    if (!TestNotNull(TEXT("Creates ordinary same-carrier parent"), OrdinaryParent) ||
        !TestNotNull(
            TEXT("Ordinary parent compiles"),
            OrdinaryParent != nullptr ? OrdinaryParent->GeneratedClass.Get() : nullptr))
    {
        return false;
    }
    AFinareActor* OrdinaryParentDefaults =
        OrdinaryParent->GeneratedClass->GetDefaultObject<AFinareActor>();
    FBoolProperty* DamageProperty = OrdinaryParentDefaults != nullptr
        ? FindFProperty<FBoolProperty>(OrdinaryParentDefaults->GetClass(), TEXT("bCanBeDamaged"))
        : nullptr;
    if (OrdinaryParentDefaults != nullptr)
    {
        OrdinaryParentDefaults->ManagedActorState = TEXT("ParentDefaultA");
        if (OrdinaryParentDefaults->ScriptComponent != nullptr)
        {
            OrdinaryParentDefaults->ScriptComponent->ScriptAsset = ScriptAsset;
            OrdinaryParentDefaults->ScriptComponent->bTickAutomatically = false;
            OrdinaryParentDefaults->ScriptComponent->ScriptAssemblyPath = TEXT("ParentComponentA");
        }
    }
    if (DamageProperty != nullptr)
    {
        DamageProperty->SetPropertyValue_InContainer(OrdinaryParentDefaults, false);
    }
    TestTrue(
        TEXT("First parent owns a distinct editable inherited default"),
        OrdinaryParentDefaults != nullptr &&
        OrdinaryParentDefaults->ManagedActorState == TEXT("ParentDefaultA"));

    UPackage* SecondParentPackage = nullptr;
    UBlueprint* SecondOrdinaryParent = CreateOrdinaryBlueprint(
        AFinareActor::StaticClass(),
        Name + TEXT("_SecondParent"),
        SecondParentPackage);
    AFinareActor* SecondParentDefaults = SecondOrdinaryParent != nullptr
        ? SecondOrdinaryParent->GeneratedClass->GetDefaultObject<AFinareActor>()
        : nullptr;
    if (SecondParentDefaults != nullptr)
    {
        SecondParentDefaults->ManagedActorState = TEXT("ParentDefaultB");
        if (SecondParentDefaults->ScriptComponent != nullptr)
        {
            SecondParentDefaults->ScriptComponent->bTickAutomatically = true;
            SecondParentDefaults->ScriptComponent->ScriptAssemblyPath = TEXT("ParentComponentB");
        }
    }
    FBoolProperty* SecondDamageProperty = SecondParentDefaults != nullptr
        ? FindFProperty<FBoolProperty>(SecondParentDefaults->GetClass(), TEXT("bCanBeDamaged"))
        : nullptr;
    if (SecondDamageProperty != nullptr)
    {
        SecondDamageProperty->SetPropertyValue_InContainer(SecondParentDefaults, false);
    }
    TestNotNull(TEXT("Creates second ordinary parent"), SecondOrdinaryParent);
    TestTrue(
        TEXT("Second parent owns a different editable inherited default"),
        SecondParentDefaults != nullptr &&
        SecondParentDefaults->ManagedActorState == TEXT("ParentDefaultB"));

    UFinareComponent* ChildComponentBeforeParentChange =
        GetScriptComponent(ChildBlueprint);
    TestTrue(
        TEXT("Child ScriptComponent has the original root component as its exact archetype"),
        ChildBlueprint != nullptr &&
        ChildBlueprint->GeneratedClass != nullptr &&
        ChildComponentBeforeParentChange != nullptr &&
        InitialDefaults != nullptr &&
        InitialDefaults->ScriptComponent != nullptr &&
        ChildComponentBeforeParentChange->GetOuter() ==
            ChildBlueprint->GeneratedClass->GetDefaultObject() &&
        ChildComponentBeforeParentChange->GetArchetype() ==
            InitialDefaults->ScriptComponent);
    if (ChildComponentBeforeParentChange != nullptr)
    {
        ChildComponentBeforeParentChange->ScriptAssemblyPath = TEXT("ChildComponentExplicit");
    }
    UFinareComponent* GrandchildComponentBeforeParentChange =
        GetScriptComponent(GrandchildBlueprint);
    if (ChildComponentBeforeParentChange != nullptr &&
        GrandchildComponentBeforeParentChange != nullptr)
    {
        GrandchildComponentBeforeParentChange->ScriptAssemblyPath =
            ChildComponentBeforeParentChange->ScriptAssemblyPath;
    }
    TestTrue(
        TEXT("Child ScriptComponent receives an explicit editable override before the caller transaction"),
        ChildComponentBeforeParentChange != nullptr &&
        ChildComponentBeforeParentChange->ScriptAssemblyPath == TEXT("ChildComponentExplicit"));
    TestTrue(
        TEXT("Grandchild ScriptComponent starts as a follower of the child component"),
        GrandchildBlueprint != nullptr &&
        GrandchildBlueprint->GeneratedClass != nullptr &&
        GrandchildComponentBeforeParentChange != nullptr &&
        GrandchildComponentBeforeParentChange->GetOuter() ==
            GrandchildBlueprint->GeneratedClass->GetDefaultObject() &&
        GrandchildComponentBeforeParentChange->GetArchetype() ==
            ChildComponentBeforeParentChange &&
        ChildComponentBeforeParentChange != nullptr &&
        GrandchildComponentBeforeParentChange->ScriptAssemblyPath ==
            ChildComponentBeforeParentChange->ScriptAssemblyPath);

    const FString ReparentedDeclaration = WithBaseClassPath(
        Declaration,
        OrdinaryParent->GeneratedClass->GetPathName());
    if (!TestTrue(TEXT("Reparent declaration updates the existing binding field"),
            !ReparentedDeclaration.IsEmpty()))
    {
        return false;
    }
    const int32 OriginalVariableCount = ManagedBlueprint->NewVariables.Num();
    UTransactor* OriginalEditorTransactor =
        GEditor != nullptr ? GEditor->Trans.Get() : nullptr;
    TStrongObjectPtr<UFinareAsset> CallerSentinel;
    bool bOwnsCallerTransaction = false;
    if (OriginalEditorTransactor != nullptr && GUndo == nullptr)
    {
        const int32 BeginResult = OriginalEditorTransactor->Begin(
            TEXT("FinareClassParentChangeTestCaller"),
            FText::FromString(TEXT("Caller sentinel")));
        TestEqual(
            TEXT("Creates an active caller transaction sentinel"),
            BeginResult,
            0);
        if (BeginResult == 0 && GUndo != nullptr)
        {
            bOwnsCallerTransaction = true;
            CallerSentinel = TStrongObjectPtr<UFinareAsset>(
                NewObject<UFinareAsset>(
                    GetTransientPackage(),
                    NAME_None,
                    RF_Transactional));
            if (CallerSentinel.IsValid())
            {
                CallerSentinel->Modify();
                CallerSentinel->ScriptTypeName = TEXT("CallerSentinel");
            }
        }
    }
    ITransaction* OriginalUndo = GUndo;
    const int32 OriginalUndoQueueLength = OriginalEditorTransactor != nullptr
        ? OriginalEditorTransactor->GetQueueLength()
        : INDEX_NONE;
    const int32 OriginalUndoCount = OriginalEditorTransactor != nullptr
        ? OriginalEditorTransactor->GetUndoCount()
        : INDEX_NONE;
    const int32 OriginalSentinelQueueIndex =
        bOwnsCallerTransaction && OriginalEditorTransactor != nullptr
            ? OriginalEditorTransactor->GetQueueLength() - 1
            : INDEX_NONE;
    const FTransaction* OriginalSentinelTransaction =
        OriginalSentinelQueueIndex != INDEX_NONE && OriginalEditorTransactor != nullptr
            ? OriginalEditorTransactor->GetTransaction(OriginalSentinelQueueIndex)
            : nullptr;
    const int32 OriginalSentinelRecordCount = OriginalSentinelTransaction != nullptr
        ? OriginalSentinelTransaction->GetRecordCount()
        : INDEX_NONE;
    const TWeakObjectPtr<UTransactor> OriginalEditorTransactorWeak(OriginalEditorTransactor);
    ON_SCOPE_EXIT
    {
        if (bOwnsCallerTransaction &&
            GEditor != nullptr &&
            GEditor->Trans.Get() == OriginalEditorTransactor &&
            GUndo == OriginalUndo)
        {
            OriginalEditorTransactor->Cancel();
        }
    };
    TestNotNull(TEXT("Parent change has the editor transaction host"), OriginalEditorTransactor);
    const auto AssertEditorTransactionState =
        [this,
            OriginalEditorTransactorWeak,
            OriginalUndo,
            OriginalUndoQueueLength,
            OriginalUndoCount,
            bOwnsCallerTransaction,
            OriginalSentinelQueueIndex,
            OriginalSentinelRecordCount](
            const TCHAR* Phase)
    {
        // Keep this assertion from masking the production bug: the test must
        // not root the original transactor while the materializer swaps it.
        const bool bOriginalTransactorAlive = OriginalEditorTransactorWeak.IsValid();
        UTransactor* LiveOriginalTransactor = bOriginalTransactorAlive
            ? OriginalEditorTransactorWeak.Get()
            : nullptr;
        const bool bRestoredIdentity =
            LiveOriginalTransactor != nullptr &&
            GEditor != nullptr && GEditor->Trans.Get() == LiveOriginalTransactor;
        TestTrue(
            FString::Printf(TEXT("%s preserves the original editor transactor identity"), Phase),
            bRestoredIdentity);
        TestTrue(
            FString::Printf(TEXT("%s restores the original GUndo transaction"), Phase),
            GUndo == OriginalUndo);
        TestTrue(
            FString::Printf(TEXT("%s preserves an active caller GUndo transaction"), Phase),
            OriginalUndo != nullptr && GUndo == OriginalUndo);
        TestTrue(
            FString::Printf(TEXT("%s preserves the original undo history"), Phase),
            bRestoredIdentity &&
            LiveOriginalTransactor->GetQueueLength() == OriginalUndoQueueLength &&
            LiveOriginalTransactor->GetUndoCount() == OriginalUndoCount);
        const FTransaction* LiveSentinelTransaction =
            bOwnsCallerTransaction && bOriginalTransactorAlive &&
                OriginalSentinelQueueIndex != INDEX_NONE
                ? LiveOriginalTransactor->GetTransaction(OriginalSentinelQueueIndex)
                : nullptr;
        TestTrue(
            FString::Printf(TEXT("%s preserves the caller transaction record count"), Phase),
            !bOwnsCallerTransaction ||
                (LiveSentinelTransaction != nullptr &&
                    LiveSentinelTransaction->GetRecordCount() == OriginalSentinelRecordCount));
    };
    Error.Reset();
    const bool bValidateOnlySucceeded =
        FFinareBlueprintClassMaterializer::SynchronizeJson(
            *ScriptAsset,
            *ManagedBlueprint,
            ReparentedDeclaration,
            true,
            Result,
            Error);
    if (!TestTrue(
            TEXT("Validate-only compiles prospective parent without mutating owner"),
            bValidateOnlySucceeded))
    {
        AddError(Error.IsEmpty()
            ? TEXT("Validate-only parent change failed without an error message.")
            : Error);
        return false;
    }
    TestTrue(TEXT("Validate-only leaves parent unchanged"),
        ManagedBlueprint->ParentClass == OriginalParent);
    TestTrue(TEXT("Validate-only leaves generated path unchanged"),
        ManagedBlueprint->GeneratedClass->GetPathName() == OriginalGeneratedPath);
    TestEqual(TEXT("Validate-only leaves generated state count unchanged"),
        ManagedBlueprint->NewVariables.Num(),
        OriginalVariableCount);
    TestTrue(
        TEXT("Validate-only preserves the original ScriptComponent owner"),
        ManagedBlueprint->GeneratedClass->GetDefaultObject<AFinareActor>() != nullptr &&
        ManagedBlueprint->GeneratedClass->GetDefaultObject<AFinareActor>()->ScriptComponent == ComponentBeforeValidate &&
        ManagedBlueprint->GeneratedClass->GetDefaultObject<AFinareActor>()->ScriptComponent->ScriptAsset == ScriptAsset);

    Error.Reset();
    const bool bAppliedFirstParent =
        FFinareBlueprintClassMaterializer::SynchronizeJson(
            *ScriptAsset,
            *ManagedBlueprint,
            ReparentedDeclaration,
            false,
            Result,
            Error);
    if (!TestTrue(
            TEXT("Applies exact same-carrier ordinary Blueprint parent"),
            bAppliedFirstParent))
    {
        AddError(Error.IsEmpty()
            ? TEXT("Same-carrier parent change failed without an error message.")
            : Error);
        return false;
    }
    TestTrue(TEXT("Managed Blueprint now uses requested parent"),
        ManagedBlueprint->ParentClass == OrdinaryParent->GeneratedClass);
    TestTrue(TEXT("Reparented generated class derives from requested parent"),
        ManagedBlueprint->GeneratedClass != nullptr &&
        ManagedBlueprint->GeneratedClass->GetSuperClass() == OrdinaryParent->GeneratedClass);
    bool bStateGuidsPreserved =
        OriginalStateGuids.Num() == ManagedBlueprint->NewVariables.Num();
    for (const FGuid& Id : OriginalStateGuids)
    {
        bStateGuidsPreserved &= ManagedBlueprint->NewVariables.ContainsByPredicate(
            [&Id](const FBPVariableDescription& Variable)
            {
                return Variable.VarGuid == Id;
            });
    }
    TestTrue(TEXT("Persistent state GUIDs survive parent change"), bStateGuidsPreserved);
    TestTrue(
        TEXT("Managed ScriptComponent ownership survives parent change"),
        ManagedBlueprint->GeneratedClass->GetDefaultObject<AFinareActor>() != nullptr &&
        ManagedBlueprint->GeneratedClass->GetDefaultObject<AFinareActor>()->ScriptComponent != nullptr &&
        ManagedBlueprint->GeneratedClass->GetDefaultObject<AFinareActor>()->ScriptComponent->ScriptAsset == ScriptAsset);
    TestTrue(
        TEXT("Root CDO follower adopts the first parent editable default"),
        ManagedBlueprint->GeneratedClass->GetDefaultObject<AFinareActor>() != nullptr &&
        ManagedBlueprint->GeneratedClass->GetDefaultObject<AFinareActor>()->ManagedActorState ==
            TEXT("ParentDefaultA"));
    DamageProperty = FindFProperty<FBoolProperty>(
        ManagedBlueprint->GeneratedClass,
        TEXT("bCanBeDamaged"));
    TestTrue(
        TEXT("Root CDO follower adopts the first native bitfield default"),
        DamageProperty != nullptr &&
        !DamageProperty->GetPropertyValue_InContainer(
            ManagedBlueprint->GeneratedClass->GetDefaultObject()));
    UFinareComponent* RootComponentAfterFirstParent =
        GetScriptComponent(ManagedBlueprint);
    UFinareComponent* ChildComponentAfterFirstParent =
        GetScriptComponent(ChildBlueprint);
    UFinareComponent* GrandchildComponentAfterFirstParent =
        GetScriptComponent(GrandchildBlueprint);
    TestTrue(
        TEXT("Root ScriptComponent follower adopts the first parent editable defaults"),
        RootComponentAfterFirstParent != nullptr &&
        OrdinaryParentDefaults != nullptr &&
        RootComponentAfterFirstParent->GetOuter() ==
            ManagedBlueprint->GeneratedClass->GetDefaultObject() &&
        RootComponentAfterFirstParent->GetArchetype() == OrdinaryParentDefaults->ScriptComponent &&
        !RootComponentAfterFirstParent->bTickAutomatically &&
        RootComponentAfterFirstParent->ScriptAssemblyPath == TEXT("ParentComponentA"));
    TestTrue(
        TEXT("Child ScriptComponent keeps its explicit editable override"),
        ChildComponentAfterFirstParent != nullptr &&
        ChildBlueprint != nullptr &&
        ChildComponentAfterFirstParent->GetOuter() ==
            ChildBlueprint->GeneratedClass->GetDefaultObject() &&
        ChildComponentAfterFirstParent->GetArchetype() == RootComponentAfterFirstParent &&
        ChildComponentAfterFirstParent->ScriptAssemblyPath == TEXT("ChildComponentExplicit"));
    TestTrue(
        TEXT("Grandchild ScriptComponent follows the child exact archetype"),
        GrandchildComponentAfterFirstParent != nullptr &&
        GrandchildBlueprint != nullptr &&
        GrandchildComponentAfterFirstParent->GetOuter() ==
            GrandchildBlueprint->GeneratedClass->GetDefaultObject() &&
        GrandchildComponentAfterFirstParent->GetArchetype() == ChildComponentAfterFirstParent &&
        GrandchildComponentAfterFirstParent->ScriptAssemblyPath == TEXT("ChildComponentExplicit"));
    AssertEditorTransactionState(TEXT("Successful parent change"));

    AFinareActor* ReparentedDefaults =
        ManagedBlueprint->GeneratedClass->GetDefaultObject<AFinareActor>();
    if (ReparentedDefaults != nullptr)
    {
        ReparentedDefaults->ManagedActorState = TEXT("ChildExplicitOverride");
    }
    DamageProperty = FindFProperty<FBoolProperty>(
        ManagedBlueprint->GeneratedClass,
        TEXT("bCanBeDamaged"));
    if (DamageProperty != nullptr)
    {
        DamageProperty->SetPropertyValue_InContainer(
            ManagedBlueprint->GeneratedClass->GetDefaultObject(),
            true);
    }
    const FString SecondParentDeclaration = WithBaseClassPath(
        Declaration,
        SecondOrdinaryParent != nullptr
            ? SecondOrdinaryParent->GeneratedClass->GetPathName()
            : FString());
    TestTrue(
        TEXT("Explicit root inherited value survives a second parent change"),
        SecondOrdinaryParent != nullptr &&
        FFinareBlueprintClassMaterializer::SynchronizeJson(
            *ScriptAsset,
            *ManagedBlueprint,
            SecondParentDeclaration,
            false,
            Result,
            Error));
    DamageProperty = ManagedBlueprint->GeneratedClass != nullptr
        ? FindFProperty<FBoolProperty>(ManagedBlueprint->GeneratedClass, TEXT("bCanBeDamaged"))
        : nullptr;
    TestTrue(
        TEXT("Second parent is applied after preserving root override"),
        ManagedBlueprint->ParentClass == (SecondOrdinaryParent != nullptr
            ? SecondOrdinaryParent->GeneratedClass
            : nullptr) &&
        ManagedBlueprint->GeneratedClass->GetDefaultObject<AFinareActor>() != nullptr &&
        ManagedBlueprint->GeneratedClass->GetDefaultObject<AFinareActor>()->ManagedActorState ==
            TEXT("ChildExplicitOverride") &&
        DamageProperty != nullptr &&
        DamageProperty->GetPropertyValue_InContainer(
            ManagedBlueprint->GeneratedClass->GetDefaultObject()));
    UFinareComponent* RootComponentAfterSecondParent =
        GetScriptComponent(ManagedBlueprint);
    UFinareComponent* ChildComponentAfterSecondParent =
        GetScriptComponent(ChildBlueprint);
    UFinareComponent* GrandchildComponentAfterSecondParent =
        GetScriptComponent(GrandchildBlueprint);
    TestTrue(
        TEXT("Root ScriptComponent follower adopts the second parent editable defaults"),
        RootComponentAfterSecondParent != nullptr &&
        SecondParentDefaults != nullptr &&
        RootComponentAfterSecondParent->GetArchetype() == SecondParentDefaults->ScriptComponent &&
        RootComponentAfterSecondParent->bTickAutomatically &&
        RootComponentAfterSecondParent->ScriptAssemblyPath == TEXT("ParentComponentB"));
    TestTrue(
        TEXT("Child ScriptComponent explicit value survives the second parent change"),
        ChildComponentAfterSecondParent != nullptr &&
        ChildComponentAfterSecondParent->GetArchetype() == RootComponentAfterSecondParent &&
        ChildComponentAfterSecondParent->ScriptAssemblyPath == TEXT("ChildComponentExplicit"));
    TestTrue(
        TEXT("Grandchild ScriptComponent follows the child after the second parent change"),
        GrandchildComponentAfterSecondParent != nullptr &&
        GrandchildComponentAfterSecondParent->GetArchetype() == ChildComponentAfterSecondParent &&
        GrandchildComponentAfterSecondParent->ScriptAssemblyPath == TEXT("ChildComponentExplicit"));
    TestTrue(
        TEXT("Second parent change preserves the root ScriptAsset binding"),
        RootComponentAfterSecondParent != nullptr &&
        RootComponentAfterSecondParent->ScriptAsset == ScriptAsset);
    TestTrue(
        TEXT("Second parent change preserves the child ScriptAsset binding"),
        ChildComponentAfterSecondParent != nullptr &&
        ChildComponentAfterSecondParent->ScriptAsset == ScriptAsset);
    TestTrue(
        TEXT("Second parent change preserves the grandchild ScriptAsset binding"),
        GrandchildComponentAfterSecondParent != nullptr &&
        GrandchildComponentAfterSecondParent->ScriptAsset == ScriptAsset);
    if (ManagedBlueprint->GeneratedClass->GetDefaultObject<AFinareActor>() != nullptr)
    {
        ManagedBlueprint->GeneratedClass->GetDefaultObject<AFinareActor>()->ManagedActorState =
            TEXT("ParentDefaultB");
    }
    DamageProperty = FindFProperty<FBoolProperty>(
        ManagedBlueprint->GeneratedClass,
        TEXT("bCanBeDamaged"));
    if (DamageProperty != nullptr)
    {
        DamageProperty->SetPropertyValue_InContainer(
            ManagedBlueprint->GeneratedClass->GetDefaultObject(),
            false);
    }
    TestTrue(
        TEXT("Follower value after returning adopts the first parent default"),
        FFinareBlueprintClassMaterializer::SynchronizeJson(
            *ScriptAsset,
            *ManagedBlueprint,
            ReparentedDeclaration,
            false,
            Result,
            Error));
    TestTrue(
        TEXT("Returning to the first parent restores its follower default"),
        ManagedBlueprint->ParentClass == OrdinaryParent->GeneratedClass &&
        ManagedBlueprint->GeneratedClass->GetDefaultObject<AFinareActor>() != nullptr &&
        ManagedBlueprint->GeneratedClass->GetDefaultObject<AFinareActor>()->ManagedActorState ==
            TEXT("ParentDefaultA"));
    DamageProperty = FindFProperty<FBoolProperty>(
        ManagedBlueprint->GeneratedClass,
        TEXT("bCanBeDamaged"));
    TestTrue(
        TEXT("Returning to the first parent restores its native bitfield follower"),
        DamageProperty != nullptr &&
        !DamageProperty->GetPropertyValue_InContainer(
            ManagedBlueprint->GeneratedClass->GetDefaultObject()));
    UFinareComponent* RootComponentAfterReturn =
        GetScriptComponent(ManagedBlueprint);
    UFinareComponent* ChildComponentAfterReturn =
        GetScriptComponent(ChildBlueprint);
    UFinareComponent* GrandchildComponentAfterReturn =
        GetScriptComponent(GrandchildBlueprint);
    TestTrue(
        TEXT("Returning to the first parent restores the root component follower"),
        RootComponentAfterReturn != nullptr &&
        OrdinaryParentDefaults != nullptr &&
        RootComponentAfterReturn->GetArchetype() == OrdinaryParentDefaults->ScriptComponent &&
        !RootComponentAfterReturn->bTickAutomatically &&
        RootComponentAfterReturn->ScriptAssemblyPath == TEXT("ParentComponentA"));
    TestTrue(
        TEXT("Returning to the first parent preserves the child component override"),
        ChildComponentAfterReturn != nullptr &&
        ChildComponentAfterReturn->GetArchetype() == RootComponentAfterReturn &&
        ChildComponentAfterReturn->ScriptAssemblyPath == TEXT("ChildComponentExplicit"));
    TestTrue(
        TEXT("Returning to the first parent preserves the grandchild component follower"),
        GrandchildComponentAfterReturn != nullptr &&
        GrandchildComponentAfterReturn->GetArchetype() == ChildComponentAfterReturn &&
        GrandchildComponentAfterReturn->ScriptAssemblyPath == TEXT("ChildComponentExplicit"));

    TestTrue(
        TEXT("Ordinary descendants retain managed component hierarchy"),
        GrandchildBlueprint != nullptr &&
            GrandchildBlueprint->GeneratedClass != nullptr &&
            GrandchildBlueprint->GeneratedClass->IsChildOf(ManagedBlueprint->GeneratedClass));
    TestTrue(
        TEXT("Ordinary child CDO retains exact managed ScriptAsset"),
        ChildBlueprint != nullptr &&
        Cast<AFinareActor>(ChildBlueprint->GeneratedClass->GetDefaultObject()) != nullptr &&
        Cast<AFinareActor>(ChildBlueprint->GeneratedClass->GetDefaultObject())->ScriptComponent != nullptr &&
        Cast<AFinareActor>(ChildBlueprint->GeneratedClass->GetDefaultObject())->ScriptComponent->ScriptAsset == ScriptAsset);
    TestTrue(
        TEXT("Ordinary grandchild CDO retains exact managed ScriptAsset"),
        GrandchildBlueprint != nullptr &&
        Cast<AFinareActor>(GrandchildBlueprint->GeneratedClass->GetDefaultObject()) != nullptr &&
        Cast<AFinareActor>(GrandchildBlueprint->GeneratedClass->GetDefaultObject())->ScriptComponent != nullptr &&
        Cast<AFinareActor>(GrandchildBlueprint->GeneratedClass->GetDefaultObject())->ScriptComponent->ScriptAsset == ScriptAsset);

    TArray<uint8> PackageBytesBeforeSaveFailure;
    TestTrue(
        TEXT("Persistent parent-change package has a byte snapshot"),
        FFileHelper::LoadFileToArray(PackageBytesBeforeSaveFailure, *ManagedPackageFilename));
    AFinareActor* MixedRollbackDefaults =
        ManagedBlueprint->GeneratedClass->GetDefaultObject<AFinareActor>();
    FIntProperty* CountProperty = MixedRollbackDefaults != nullptr
        ? FindFProperty<FIntProperty>(MixedRollbackDefaults->GetClass(), TEXT("Count"))
        : nullptr;
    if (CountProperty != nullptr)
    {
        CountProperty->SetPropertyValue_InContainer(MixedRollbackDefaults, 17);
    }
    TestTrue(
        TEXT("Mixed rollback starts with an explicit Count value"),
        CountProperty != nullptr &&
        CountProperty->GetPropertyValue_InContainer(MixedRollbackDefaults) == 17);
    AFinareActor* ChildRollbackDefaults = ChildBlueprint != nullptr &&
        ChildBlueprint->GeneratedClass != nullptr
        ? ChildBlueprint->GeneratedClass->GetDefaultObject<AFinareActor>()
        : nullptr;
    FIntProperty* ChildCountProperty = ChildRollbackDefaults != nullptr
        ? FindFProperty<FIntProperty>(ChildRollbackDefaults->GetClass(), TEXT("Count"))
        : nullptr;
    if (ChildCountProperty != nullptr)
    {
        ChildCountProperty->SetPropertyValue_InContainer(ChildRollbackDefaults, 23);
    }
    AFinareActor* GrandchildRollbackDefaults = GrandchildBlueprint != nullptr &&
        GrandchildBlueprint->GeneratedClass != nullptr
        ? GrandchildBlueprint->GeneratedClass->GetDefaultObject<AFinareActor>()
        : nullptr;
    FIntProperty* GrandchildCountProperty = GrandchildRollbackDefaults != nullptr
        ? FindFProperty<FIntProperty>(GrandchildRollbackDefaults->GetClass(), TEXT("Count"))
        : nullptr;
    if (GrandchildCountProperty != nullptr)
    {
        GrandchildCountProperty->SetPropertyValue_InContainer(GrandchildRollbackDefaults, 31);
    }
    TestTrue(
        TEXT("Mixed rollback gives the ordinary child an explicit Count value"),
        ChildCountProperty != nullptr &&
        ChildCountProperty->GetPropertyValue_InContainer(ChildRollbackDefaults) == 23);
    TestTrue(
        TEXT("Mixed rollback gives the ordinary grandchild an explicit Count value"),
        GrandchildCountProperty != nullptr &&
        GrandchildCountProperty->GetPropertyValue_InContainer(GrandchildRollbackDefaults) == 31);
    const FString RenamedAfterSaveDeclaration = WithBaseClassPathAndStateName(
        Declaration,
        AFinareActor::StaticClass()->GetPathName(),
        TEXT("RenamedCount"));
    TestTrue(
        TEXT("Mixed rollback declaration renames Count before publication"),
        !RenamedAfterSaveDeclaration.IsEmpty());
    const bool bManagedPackageDirtyBeforeFailure = ManagedPackage->IsDirty();
    FFinareBlueprintClassMaterializerOptions SaveFailureOptions;
    SaveFailureOptions.FailurePoint =
        EFinareBlueprintClassMaterializerFailurePoint::AfterPackageSave;
    Error.Reset();
    TestFalse(
        TEXT("Injected after-save failure is rejected"),
        FFinareBlueprintClassMaterializer::SynchronizeJson(
            *ScriptAsset,
            *ManagedBlueprint,
            RenamedAfterSaveDeclaration,
            false,
            Result,
            Error,
            SaveFailureOptions));
    const FString SaveFailureError = Error;
    TestTrue(
        TEXT("After-save failure reports the injected publication seam immediately"),
        SaveFailureError.Contains(TEXT("Injected package publication failure")));
    TestEqual(
        TEXT("After-save failure completes rollback without a diagnostic"),
        SaveFailureError,
        FString(TEXT("Injected package publication failure for Finare Blueprint transaction test.")));
    TArray<uint8> PackageBytesAfterSaveFailure;
    TestTrue(
        TEXT("After-save failure restores the package file"),
        FFileHelper::LoadFileToArray(PackageBytesAfterSaveFailure, *ManagedPackageFilename) &&
        PackageBytesAfterSaveFailure == PackageBytesBeforeSaveFailure);
    TestTrue(TEXT("After-save failure restores the parent"),
        ManagedBlueprint->ParentClass == OrdinaryParent->GeneratedClass);
    TestTrue(
        TEXT("After-save failure restores the original Count name and stable ID"),
        ManagedBlueprint->NewVariables.ContainsByPredicate(
            [&OriginalStateGuids](const FBPVariableDescription& Variable)
            {
                return Variable.VarName == TEXT("Count") &&
                    OriginalStateGuids.Contains(Variable.VarGuid);
            }));
    MixedRollbackDefaults =
        ManagedBlueprint->GeneratedClass->GetDefaultObject<AFinareActor>();
    CountProperty = MixedRollbackDefaults != nullptr
        ? FindFProperty<FIntProperty>(MixedRollbackDefaults->GetClass(), TEXT("Count"))
        : nullptr;
    TestTrue(
        TEXT("After-save failure restores the explicit Count value"),
        CountProperty != nullptr &&
        CountProperty->GetPropertyValue_InContainer(MixedRollbackDefaults) == 17);
    ChildRollbackDefaults = ChildBlueprint != nullptr && ChildBlueprint->GeneratedClass != nullptr
        ? ChildBlueprint->GeneratedClass->GetDefaultObject<AFinareActor>()
        : nullptr;
    ChildCountProperty = ChildRollbackDefaults != nullptr
        ? FindFProperty<FIntProperty>(ChildRollbackDefaults->GetClass(), TEXT("Count"))
        : nullptr;
    GrandchildRollbackDefaults = GrandchildBlueprint != nullptr &&
        GrandchildBlueprint->GeneratedClass != nullptr
        ? GrandchildBlueprint->GeneratedClass->GetDefaultObject<AFinareActor>()
        : nullptr;
    GrandchildCountProperty = GrandchildRollbackDefaults != nullptr
        ? FindFProperty<FIntProperty>(GrandchildRollbackDefaults->GetClass(), TEXT("Count"))
        : nullptr;
    TestTrue(
        TEXT("After-save failure restores the ordinary child Count identity and value"),
        ChildCountProperty != nullptr &&
        ChildCountProperty->GetPropertyValue_InContainer(ChildRollbackDefaults) == 23);
    TestTrue(
        TEXT("After-save failure restores the ordinary grandchild Count identity and value"),
        GrandchildCountProperty != nullptr &&
        GrandchildCountProperty->GetPropertyValue_InContainer(GrandchildRollbackDefaults) == 31);
    UFinareComponent* RootComponentAfterSaveRollback =
        GetScriptComponent(ManagedBlueprint);
    UFinareComponent* ChildComponentAfterSaveRollback =
        GetScriptComponent(ChildBlueprint);
    UFinareComponent* GrandchildComponentAfterSaveRollback =
        GetScriptComponent(GrandchildBlueprint);
    TestTrue(
        TEXT("After-save failure restores the root ScriptComponent exact outer and archetype"),
        RootComponentAfterSaveRollback != nullptr &&
        ManagedBlueprint->GeneratedClass != nullptr &&
        RootComponentAfterSaveRollback->GetOuter() ==
            ManagedBlueprint->GeneratedClass->GetDefaultObject() &&
        OrdinaryParentDefaults != nullptr &&
        RootComponentAfterSaveRollback->GetArchetype() == OrdinaryParentDefaults->ScriptComponent &&
        RootComponentAfterSaveRollback->ScriptAssemblyPath == TEXT("ParentComponentA") &&
        !RootComponentAfterSaveRollback->bTickAutomatically);
    TestTrue(
        TEXT("After-save failure restores the child explicit ScriptComponent value and archetype"),
        ChildComponentAfterSaveRollback != nullptr &&
        ChildBlueprint != nullptr &&
        ChildComponentAfterSaveRollback->GetOuter() ==
            ChildBlueprint->GeneratedClass->GetDefaultObject() &&
        ChildComponentAfterSaveRollback->GetArchetype() == RootComponentAfterSaveRollback &&
        ChildComponentAfterSaveRollback->ScriptAssemblyPath == TEXT("ChildComponentExplicit"));
    TestTrue(
        TEXT("After-save failure restores the grandchild follower ScriptComponent value and archetype"),
        GrandchildComponentAfterSaveRollback != nullptr &&
        GrandchildBlueprint != nullptr &&
        GrandchildComponentAfterSaveRollback->GetOuter() ==
            GrandchildBlueprint->GeneratedClass->GetDefaultObject() &&
        GrandchildComponentAfterSaveRollback->GetArchetype() == ChildComponentAfterSaveRollback &&
        GrandchildComponentAfterSaveRollback->ScriptAssemblyPath == TEXT("ChildComponentExplicit"));
    TestTrue(
        TEXT("After-save failure preserves the managed package dirty flag"),
        ManagedPackage->IsDirty() == bManagedPackageDirtyBeforeFailure);
    AssertEditorTransactionState(TEXT("After-save failure"));

    const bool bManagedPackageDirtyBeforeValidateOnly = ManagedPackage->IsDirty();
    Error.Reset();
    TestTrue(
        TEXT("Validate-only mixed rename compiles without mutating the owner"),
        FFinareBlueprintClassMaterializer::SynchronizeJson(
            *ScriptAsset,
            *ManagedBlueprint,
            RenamedAfterSaveDeclaration,
            true,
            Result,
            Error));
    TestTrue(
        TEXT("Validate-only mixed rename preserves the package dirty flag"),
        ManagedPackage->IsDirty() == bManagedPackageDirtyBeforeValidateOnly);
    TestTrue(
        TEXT("Validate-only mixed rename preserves the current parent and Count"),
        ManagedBlueprint->ParentClass == OrdinaryParent->GeneratedClass &&
        ManagedBlueprint->NewVariables.ContainsByPredicate(
            [&OriginalStateGuids](const FBPVariableDescription& Variable)
            {
                return Variable.VarName == TEXT("Count") &&
                    OriginalStateGuids.Contains(Variable.VarGuid);
            }) &&
        CountProperty != nullptr &&
        CountProperty->GetPropertyValue_InContainer(MixedRollbackDefaults) == 17);

    const FString IncompatibleOverrideDeclaration = WithIncompatibleCountType(
        Declaration,
        SecondOrdinaryParent != nullptr && SecondOrdinaryParent->GeneratedClass != nullptr
            ? SecondOrdinaryParent->GeneratedClass->GetPathName()
            : FString());
    Error.Reset();
    TestFalse(
        TEXT("Incompatible explicit Count override is rejected before mutation"),
        FFinareBlueprintClassMaterializer::SynchronizeJson(
            *ScriptAsset,
            *ManagedBlueprint,
            IncompatibleOverrideDeclaration,
            false,
            Result,
            Error));
    TestTrue(
        TEXT("Incompatible override rejection identifies the type migration"),
        Error.Contains(TEXT("type")) || Error.Contains(TEXT("override")));
    TestTrue(
        TEXT("Incompatible override rejection leaves the parent unchanged"),
        ManagedBlueprint->ParentClass == OrdinaryParent->GeneratedClass);
    MixedRollbackDefaults =
        ManagedBlueprint->GeneratedClass->GetDefaultObject<AFinareActor>();
    CountProperty = MixedRollbackDefaults != nullptr
        ? FindFProperty<FIntProperty>(MixedRollbackDefaults->GetClass(), TEXT("Count"))
        : nullptr;
    TestTrue(
        TEXT("Incompatible override rejection leaves Count unchanged"),
        CountProperty != nullptr &&
        CountProperty->GetPropertyValue_InContainer(MixedRollbackDefaults) == 17);

    FFinareBlueprintClassMaterializerOptions FailureOptions;
    FailureOptions.FailurePoint =
        EFinareBlueprintClassMaterializerFailurePoint::AfterParentMutation;
    TestFalse(
        TEXT("Injected parent mutation failure is rejected"),
        FFinareBlueprintClassMaterializer::SynchronizeJson(
            *ScriptAsset,
            *ManagedBlueprint,
            WithBaseClassPath(Declaration, AFinareActor::StaticClass()->GetPathName()),
            false,
            Result,
            Error,
            FailureOptions));
    const FString ParentFailureError = Error;
    TestTrue(
        TEXT("Injected parent mutation failure reports the injected seam"),
        ParentFailureError.Contains(TEXT("Injected parent mutation failure")));
    TestTrue(TEXT("Injected parent failure restores full parent"),
        ManagedBlueprint->ParentClass == OrdinaryParent->GeneratedClass);
    TestTrue(
        TEXT("Injected parent failure preserves the managed package dirty flag"),
        ManagedPackage->IsDirty() == bManagedPackageDirtyBeforeFailure);
    TestTrue(
        TEXT("Injected parent failure completes rollback without a diagnostic"),
        ParentFailureError == TEXT("Injected parent mutation failure for Finare Blueprint transaction test."));
    TestTrue(
        TEXT("Injected parent failure restores generated superclass and class path"),
        ManagedBlueprint->GeneratedClass != nullptr &&
        ManagedBlueprint->GeneratedClass->GetSuperClass() == OrdinaryParent->GeneratedClass &&
        ManagedBlueprint->GeneratedClass->GetPathName() == OriginalGeneratedPath);
    bool bRollbackGraphGuidsPreserved =
        OriginalGraphGuids.Num() ==
        ManagedBlueprint->FunctionGraphs.Num() +
        ManagedBlueprint->UbergraphPages.Num() +
        ManagedBlueprint->MacroGraphs.Num();
    for (UEdGraph* Graph : ManagedBlueprint->FunctionGraphs)
    {
        bRollbackGraphGuidsPreserved &= Graph != nullptr && OriginalGraphGuids.Contains(Graph->GraphGuid);
    }
    for (UEdGraph* Graph : ManagedBlueprint->UbergraphPages)
    {
        bRollbackGraphGuidsPreserved &= Graph != nullptr && OriginalGraphGuids.Contains(Graph->GraphGuid);
    }
    for (UEdGraph* Graph : ManagedBlueprint->MacroGraphs)
    {
        bRollbackGraphGuidsPreserved &= Graph != nullptr && OriginalGraphGuids.Contains(Graph->GraphGuid);
    }
    TestTrue(TEXT("Injected parent failure restores Blueprint graph identities"), bRollbackGraphGuidsPreserved);
    TestTrue(
        TEXT("Injected parent failure restores managed component ownership"),
        ManagedBlueprint->GeneratedClass->GetDefaultObject<AFinareActor>() != nullptr &&
        ManagedBlueprint->GeneratedClass->GetDefaultObject<AFinareActor>()->ScriptComponent != nullptr &&
        ManagedBlueprint->GeneratedClass->GetDefaultObject<AFinareActor>()->ScriptComponent->ScriptAsset == ScriptAsset);
    AssertEditorTransactionState(TEXT("Injected parent failure"));

    FString NullBaseDeclaration = WithBaseClassPath(Declaration, FString(), true);
    TestTrue(TEXT("Null base declaration is constructed by DOM update"), !NullBaseDeclaration.IsEmpty());
    TestTrue(
        TEXT("Null base binding is a no-op after reparent"),
        FFinareBlueprintClassMaterializer::SynchronizeJson(
            *ScriptAsset,
            *ManagedBlueprint,
            NullBaseDeclaration,
            false,
            Result,
            Error));
    TestTrue(TEXT("Null base binding preserves current parent"),
        ManagedBlueprint->ParentClass == OrdinaryParent->GeneratedClass);

    const TArray<uint8> ExistingExternalBefore = { 0x11, 0x22, 0x33, 0x44 };
    const TArray<uint8> ExistingExternalCandidate = { 0x91, 0x92, 0x93, 0x94 };
    const TArray<uint8> CreatedExternalCandidate = { 0xA1, 0xA2, 0xA3, 0xA4 };
    IFileManager::Get().MakeDirectory(*ExternalProbeRoot, true);
    TestTrue(
        TEXT("External publication writes its existing baseline probe"),
        FFileHelper::SaveArrayToFile(ExistingExternalBefore, *ExistingExternalPath));
    TArray<uint8> PackageBytesBeforeExternalFailure;
    TestTrue(
        TEXT("External publication captures the owner package baseline"),
        FFileHelper::LoadFileToArray(PackageBytesBeforeExternalFailure, *ManagedPackageFilename));
    const bool bManagedPackageDirtyBeforeExternalFailure = ManagedPackage->IsDirty();
    const FString ExternalMixedDeclaration = WithBaseClassPathAndStateName(
        Declaration,
        OrdinaryParent != nullptr && OrdinaryParent->GeneratedClass != nullptr
            ? OrdinaryParent->GeneratedClass->GetPathName()
            : FString(),
        TEXT("ExternalRenamedCount"));
    TestTrue(
        TEXT("No-op parent mixed external declaration is constructed"),
        !ExternalMixedDeclaration.IsEmpty());
    AFinareActor* ExternalRootDefaults =
        ManagedBlueprint->GeneratedClass->GetDefaultObject<AFinareActor>();
    AFinareActor* ExternalChildDefaults = ChildBlueprint != nullptr &&
            ChildBlueprint->GeneratedClass != nullptr
        ? ChildBlueprint->GeneratedClass->GetDefaultObject<AFinareActor>()
        : nullptr;
    AFinareActor* ExternalGrandchildDefaults = GrandchildBlueprint != nullptr &&
            GrandchildBlueprint->GeneratedClass != nullptr
        ? GrandchildBlueprint->GeneratedClass->GetDefaultObject<AFinareActor>()
        : nullptr;
    FIntProperty* ExternalRootCountProperty = ExternalRootDefaults != nullptr
        ? FindFProperty<FIntProperty>(ExternalRootDefaults->GetClass(), TEXT("Count"))
        : nullptr;
    FIntProperty* ExternalChildCountProperty = ExternalChildDefaults != nullptr
        ? FindFProperty<FIntProperty>(ExternalChildDefaults->GetClass(), TEXT("Count"))
        : nullptr;
    FIntProperty* ExternalGrandchildCountProperty = ExternalGrandchildDefaults != nullptr
        ? FindFProperty<FIntProperty>(ExternalGrandchildDefaults->GetClass(), TEXT("Count"))
        : nullptr;
    if (ExternalRootCountProperty != nullptr)
    {
        ExternalRootCountProperty->SetPropertyValue_InContainer(ExternalRootDefaults, 37);
    }
    if (ExternalChildCountProperty != nullptr)
    {
        ExternalChildCountProperty->SetPropertyValue_InContainer(ExternalChildDefaults, 47);
    }
    if (ExternalGrandchildCountProperty != nullptr)
    {
        ExternalGrandchildCountProperty->SetPropertyValue_InContainer(ExternalGrandchildDefaults, 57);
    }
    UFinareComponent* ExternalRootComponent = GetScriptComponent(ManagedBlueprint);
    UFinareComponent* ExternalChildComponent = GetScriptComponent(ChildBlueprint);
    UFinareComponent* ExternalGrandchildComponent = GetScriptComponent(GrandchildBlueprint);
    if (ExternalRootComponent != nullptr)
    {
        ExternalRootComponent->ScriptAssemblyPath = TEXT("ExternalRootComponent");
    }
    if (ExternalChildComponent != nullptr)
    {
        ExternalChildComponent->ScriptAssemblyPath = TEXT("ExternalChildComponent");
    }
    if (ExternalGrandchildComponent != nullptr)
    {
        ExternalGrandchildComponent->ScriptAssemblyPath = TEXT("ExternalChildComponent");
    }
    TestTrue(
        TEXT("External rollback starts with distinct managed Count values"),
        ExternalRootCountProperty != nullptr &&
        ExternalChildCountProperty != nullptr &&
        ExternalGrandchildCountProperty != nullptr &&
        ExternalRootCountProperty->GetPropertyValue_InContainer(ExternalRootDefaults) == 37 &&
        ExternalChildCountProperty->GetPropertyValue_InContainer(ExternalChildDefaults) == 47 &&
        ExternalGrandchildCountProperty->GetPropertyValue_InContainer(ExternalGrandchildDefaults) == 57);

    FFinareBlueprintClassMaterializerOptions ExternalFailureOptions;
    ExternalFailureOptions.JournalTransactionId =
        FGuid::NewGuid().ToString(EGuidFormats::Digits);
    ExternalFailureOptions.FailAfterExternalPublicationWrite = 2;
    FFinareExternalPublicationFile ExistingExternalOutput;
    ExistingExternalOutput.Path = ExistingExternalPath;
    ExistingExternalOutput.Content = ExistingExternalCandidate;
    ExternalFailureOptions.ExternalPublicationFiles.Add(MoveTemp(ExistingExternalOutput));
    FFinareExternalPublicationFile CreatedExternalOutput;
    CreatedExternalOutput.Path = CreatedExternalPath;
    CreatedExternalOutput.Content = CreatedExternalCandidate;
    ExternalFailureOptions.ExternalPublicationFiles.Add(MoveTemp(CreatedExternalOutput));

    Error.Reset();
    const bool bExternalFailure = FFinareBlueprintClassMaterializer::SynchronizeJson(
        *ScriptAsset,
        *ManagedBlueprint,
        ExternalMixedDeclaration,
        false,
        Result,
        Error,
        ExternalFailureOptions);
    TestFalse(TEXT("Mid-external-write failure rejects the no-op parent publication"), bExternalFailure);
    TestEqual(
        TEXT("Mid-external-write failure reports its exact seam"),
        Error,
        FString(TEXT("Injected external publication failure after 2 external file writes.")));
    TArray<uint8> ExternalBytesAfterFailure;
    TestTrue(
        TEXT("Mid-external-write failure restores the existing external bytes"),
        FFileHelper::LoadFileToArray(ExternalBytesAfterFailure, *ExistingExternalPath) &&
        ExternalBytesAfterFailure == ExistingExternalBefore);
    TestFalse(
        TEXT("Mid-external-write failure removes the not-yet-published external output"),
        IFileManager::Get().FileExists(*CreatedExternalPath));
    TArray<uint8> PackageBytesAfterExternalFailure;
    TestTrue(
        TEXT("Mid-external-write failure restores the owner package bytes"),
        FFileHelper::LoadFileToArray(PackageBytesAfterExternalFailure, *ManagedPackageFilename) &&
        PackageBytesAfterExternalFailure == PackageBytesBeforeExternalFailure);
    TestTrue(
        TEXT("Mid-external-write failure preserves the no-op parent and dirty flag"),
        ManagedBlueprint->ParentClass == OrdinaryParent->GeneratedClass &&
        ManagedPackage->IsDirty() == bManagedPackageDirtyBeforeExternalFailure);
    TestTrue(
        TEXT("Mid-external-write failure restores the original Count identity"),
        ManagedBlueprint->NewVariables.ContainsByPredicate(
            [&OriginalStateGuids](const FBPVariableDescription& Variable)
            {
                return Variable.VarName == TEXT("Count") &&
                    OriginalStateGuids.Contains(Variable.VarGuid);
            }));
    ExternalRootDefaults = ManagedBlueprint->GeneratedClass != nullptr
        ? ManagedBlueprint->GeneratedClass->GetDefaultObject<AFinareActor>()
        : nullptr;
    ExternalChildDefaults = ChildBlueprint != nullptr && ChildBlueprint->GeneratedClass != nullptr
        ? ChildBlueprint->GeneratedClass->GetDefaultObject<AFinareActor>()
        : nullptr;
    ExternalGrandchildDefaults = GrandchildBlueprint != nullptr &&
            GrandchildBlueprint->GeneratedClass != nullptr
        ? GrandchildBlueprint->GeneratedClass->GetDefaultObject<AFinareActor>()
        : nullptr;
    ExternalRootCountProperty = ExternalRootDefaults != nullptr
        ? FindFProperty<FIntProperty>(ExternalRootDefaults->GetClass(), TEXT("Count"))
        : nullptr;
    ExternalChildCountProperty = ExternalChildDefaults != nullptr
        ? FindFProperty<FIntProperty>(ExternalChildDefaults->GetClass(), TEXT("Count"))
        : nullptr;
    ExternalGrandchildCountProperty = ExternalGrandchildDefaults != nullptr
        ? FindFProperty<FIntProperty>(ExternalGrandchildDefaults->GetClass(), TEXT("Count"))
        : nullptr;
    ExternalRootComponent = GetScriptComponent(ManagedBlueprint);
    ExternalChildComponent = GetScriptComponent(ChildBlueprint);
    ExternalGrandchildComponent = GetScriptComponent(GrandchildBlueprint);
    TestTrue(
        TEXT("Mid-external-write failure restores all managed Count values"),
        ExternalRootCountProperty != nullptr &&
        ExternalChildCountProperty != nullptr &&
        ExternalGrandchildCountProperty != nullptr &&
        ExternalRootCountProperty->GetPropertyValue_InContainer(ExternalRootDefaults) == 37 &&
        ExternalChildCountProperty->GetPropertyValue_InContainer(ExternalChildDefaults) == 47 &&
        ExternalGrandchildCountProperty->GetPropertyValue_InContainer(ExternalGrandchildDefaults) == 57);
    bool bExternalGraphGuidsPreserved =
        OriginalGraphGuids.Num() ==
        ManagedBlueprint->FunctionGraphs.Num() +
        ManagedBlueprint->UbergraphPages.Num() +
        ManagedBlueprint->MacroGraphs.Num();
    for (UEdGraph* Graph : ManagedBlueprint->FunctionGraphs)
    {
        bExternalGraphGuidsPreserved &= Graph != nullptr && OriginalGraphGuids.Contains(Graph->GraphGuid);
    }
    for (UEdGraph* Graph : ManagedBlueprint->UbergraphPages)
    {
        bExternalGraphGuidsPreserved &= Graph != nullptr && OriginalGraphGuids.Contains(Graph->GraphGuid);
    }
    for (UEdGraph* Graph : ManagedBlueprint->MacroGraphs)
    {
        bExternalGraphGuidsPreserved &= Graph != nullptr && OriginalGraphGuids.Contains(Graph->GraphGuid);
    }
    TestTrue(TEXT("Mid-external-write failure restores graph identities"), bExternalGraphGuidsPreserved);
    TestTrue(
        TEXT("Mid-external-write failure restores ScriptComponent ownership and values"),
        ExternalRootComponent != nullptr &&
        ExternalChildComponent != nullptr &&
        ExternalGrandchildComponent != nullptr &&
        ExternalRootComponent->GetOuter() == ExternalRootDefaults &&
        ExternalChildComponent->GetOuter() == ExternalChildDefaults &&
        ExternalGrandchildComponent->GetOuter() == ExternalGrandchildDefaults &&
        ExternalRootComponent->ScriptAsset == ScriptAsset &&
        ExternalChildComponent->ScriptAsset == ScriptAsset &&
        ExternalGrandchildComponent->ScriptAsset == ScriptAsset &&
        ExternalRootComponent->ScriptAssemblyPath == TEXT("ExternalRootComponent") &&
        ExternalChildComponent->ScriptAssemblyPath == TEXT("ExternalChildComponent") &&
        ExternalGrandchildComponent->ScriptAssemblyPath == TEXT("ExternalChildComponent"));

    ExternalFailureOptions.FailAfterExternalPublicationWrite = INDEX_NONE;
    ExternalFailureOptions.JournalTransactionId =
        FGuid::NewGuid().ToString(EGuidFormats::Digits);
    Error.Reset();
    TestTrue(
        TEXT("No-op parent mixed external publication succeeds once"),
        FFinareBlueprintClassMaterializer::SynchronizeJson(
            *ScriptAsset,
            *ManagedBlueprint,
            ExternalMixedDeclaration,
            false,
            Result,
            Error,
            ExternalFailureOptions));
    TestTrue(
        TEXT("Successful external publication writes both candidate outputs"),
        FFileHelper::LoadFileToArray(ExternalBytesAfterFailure, *ExistingExternalPath) &&
            ExternalBytesAfterFailure == ExistingExternalCandidate &&
            FFileHelper::LoadFileToArray(ExternalBytesAfterFailure, *CreatedExternalPath) &&
            ExternalBytesAfterFailure == CreatedExternalCandidate);
    TestTrue(
        TEXT("Successful external publication applies the mixed declaration without changing parent"),
        ManagedBlueprint->ParentClass == OrdinaryParent->GeneratedClass &&
        ManagedBlueprint->NewVariables.ContainsByPredicate(
            [](const FBPVariableDescription& Variable)
            {
                return Variable.VarName == TEXT("ExternalRenamedCount");
            }));

    TestFalse(
        TEXT("A non-carrier Actor parent reports composition requirement"),
        FFinareBlueprintClassMaterializer::SynchronizeJson(
            *ScriptAsset,
            *ManagedBlueprint,
            WithBaseClassPath(Declaration, AActor::StaticClass()->GetPathName()),
            true,
            Result,
            Error));
    TestTrue(TEXT("Composition failure is explicit"), Error.Contains(TEXT("composition")));
    TestFalse(
        TEXT("A generated owner self-parent cycle is rejected"),
        FFinareBlueprintClassMaterializer::SynchronizeJson(
            *ScriptAsset,
            *ManagedBlueprint,
            WithBaseClassPath(Declaration, ManagedBlueprint->GeneratedClass->GetPathName()),
            true,
            Result,
            Error));
    TestTrue(TEXT("Cycle failure is explicit"), Error.Contains(TEXT("cycle")));

    FString LowercasePath = OrdinaryParent->GeneratedClass->GetPathName().ToLower();
    TestFalse(
        TEXT("Case-only parent path is rejected"),
        FFinareBlueprintClassMaterializer::SynchronizeJson(
            *ScriptAsset,
            *ManagedBlueprint,
            WithBaseClassPath(Declaration, LowercasePath),
            true,
            Result,
            Error));
    TestTrue(TEXT("Case-only rejection leaves parent unchanged"),
        ManagedBlueprint->ParentClass == OrdinaryParent->GeneratedClass);

    return true;
}
