#include "FinareFunctionClosureVerification.h"

#include "Dom/JsonObject.h"
#include "Engine/World.h"
#include "Engine/Engine.h"
#include "GameFramework/Actor.h"
#include "HAL/FileManager.h"
#include "HAL/PlatformMisc.h"
#include "HAL/PlatformProcess.h"
#include "HAL/PlatformProperties.h"
#include "Misc/CommandLine.h"
#include "Misc/FileHelper.h"
#include "Misc/Parse.h"
#include "Misc/Paths.h"
#include "Misc/ScopeExit.h"
#include "Serialization/JsonReader.h"
#include "Serialization/JsonSerializer.h"
#include "FinareAsset.h"
#include "FinareBlueprint.h"
#include "FinareClassBases.h"
#include "FinareComponent.h"
#include "FinareCoreClrHost.h"
#include "FinareWorldSubsystem.h"
#include "UObject/SoftObjectPtr.h"
#include "UObject/StructOnScope.h"
#include "UObject/UnrealType.h"

namespace UE::Finare::FunctionClosureVerification
{
namespace
{
bool ReadArgument(const TCHAR* Name, FString& OutValue)
{
    if (!FParse::Value(FCommandLine::Get(), Name, OutValue))
    {
        return false;
    }
    OutValue.TrimQuotesInline();
    return !OutValue.IsEmpty();
}

bool ReadStringField(
    const TSharedPtr<FJsonObject>& Root,
    const TCHAR* Name,
    FString& OutValue,
    FString& OutError)
{
    if (!Root.IsValid() || !Root->TryGetStringField(Name, OutValue) || OutValue.IsEmpty())
    {
        OutError = FString::Printf(TEXT("Receipt field '%s' is missing."), Name);
        return false;
    }
    return true;
}

bool ReadStringArray(
    const TSharedPtr<FJsonObject>& Root,
    const TCHAR* Name,
    TArray<FString>& OutValues,
    FString& OutError)
{
    const TArray<TSharedPtr<FJsonValue>>* Values = nullptr;
    if (!Root.IsValid() || !Root->TryGetArrayField(Name, Values) || Values == nullptr)
    {
        OutError = FString::Printf(TEXT("Receipt array '%s' is missing."), Name);
        return false;
    }
    OutValues.Reset();
    for (const TSharedPtr<FJsonValue>& Value : *Values)
    {
        if (!Value.IsValid() || Value->Type != EJson::String)
        {
            OutError = FString::Printf(TEXT("Receipt array '%s' contains a non-string value."), Name);
            return false;
        }
        OutValues.Add(Value->AsString());
    }
    return true;
}

bool LoadReceipt(
    const FString& ReceiptPath,
    FString& OutPackageFilename,
    FString& OutLibraryClassPath,
    FString& OutCallerClassPath,
    TArray<FString>& OutCallerFunctions,
    TArray<FString>& OutObservedProperties,
    FString& OutError)
{
    FString Json;
    if (!FFileHelper::LoadFileToString(Json, *ReceiptPath))
    {
        OutError = FString::Printf(TEXT("Could not read function closure receipt '%s'."), *ReceiptPath);
        return false;
    }
    TSharedPtr<FJsonObject> Root;
    const TSharedRef<TJsonReader<>> Reader = TJsonReaderFactory<>::Create(Json);
    FString SchemaVersion;
    if (!FJsonSerializer::Deserialize(Reader, Root) ||
        !ReadStringField(Root, TEXT("schemaVersion"), SchemaVersion, OutError) ||
        SchemaVersion != TEXT("unifiedscript.function-closure-fixture/v0.1") ||
        !ReadStringField(Root, TEXT("packageFilename"), OutPackageFilename, OutError) ||
        !ReadStringField(Root, TEXT("libraryClassPath"), OutLibraryClassPath, OutError) ||
        !ReadStringField(Root, TEXT("callerClassPath"), OutCallerClassPath, OutError) ||
        !ReadStringArray(Root, TEXT("callerFunctionNames"), OutCallerFunctions, OutError) ||
        !ReadStringArray(Root, TEXT("observedPropertyNames"), OutObservedProperties, OutError))
    {
        if (OutError.IsEmpty())
        {
            OutError = TEXT("Function closure receipt has an invalid schema version.");
        }
        return false;
    }
    if (OutCallerFunctions.Num() != 5 || OutObservedProperties.Num() != 5)
    {
        OutError = TEXT("Function closure receipt must contain five typed caller functions and observations.");
        return false;
    }
    return true;
}

void Finish(bool bSuccess, const FString& Detail)
{
    FString ResultPath;
    ReadArgument(TEXT("FinareFunctionClosureResult="), ResultPath);
    FString Result = FString::Printf(
        TEXT("%s\n%s\n"),
        bSuccess ? TEXT("PASS") : TEXT("FAIL"),
        *Detail);
    bool bResultWritten = ResultPath.IsEmpty() || FFileHelper::SaveStringToFile(
        Result,
        *ResultPath,
        FFileHelper::EEncodingOptions::ForceUTF8WithoutBOM);
    if (!bResultWritten)
    {
        bSuccess = false;
        Result += TEXT("\nCould not write the requested result file.\n");
    }
    const FString LogDetail = bResultWritten
        ? Detail
        : Detail + TEXT("; result file write failed");
    UE_LOG(
        LogTemp,
        Display,
        TEXT("FINARE_FUNCTION_CLOSURE %s %s"),
        bSuccess ? TEXT("PASS") : TEXT("FAIL"),
        *LogDetail.ReplaceCharWithEscapedChar());
    FPlatformMisc::RequestExitWithStatus(
        false,
        bSuccess ? 0 : 1,
        TEXT("FinareFunctionClosure"));
}

bool FillValue(const FProperty& Property, void* Address, int32 ShapeIndex, bool bReplacement)
{
    if (ShapeIndex == 0)
    {
        const FArrayProperty* ArrayProperty = CastField<FArrayProperty>(&Property);
        if (ArrayProperty == nullptr || CastField<FIntProperty>(ArrayProperty->Inner) == nullptr)
        {
            return false;
        }
        FScriptArrayHelper Array(ArrayProperty, Address);
        Array.AddValues(2);
        FIntProperty* Inner = CastFieldChecked<FIntProperty>(ArrayProperty->Inner);
        Inner->SetPropertyValue(Array.GetRawPtr(0), bReplacement ? 42 : 7);
        Inner->SetPropertyValue(Array.GetRawPtr(1), bReplacement ? 99 : -3);
        return true;
    }
    if (ShapeIndex == 1)
    {
        const FSetProperty* SetProperty = CastField<FSetProperty>(&Property);
        if (SetProperty == nullptr || CastField<FNameProperty>(SetProperty->ElementProp) == nullptr)
        {
            return false;
        }
        FScriptSetHelper Set(SetProperty, Address);
        const int32 Index = Set.AddDefaultValue_Invalid_NeedsRehash();
        CastFieldChecked<FNameProperty>(SetProperty->ElementProp)->SetPropertyValue(
            Set.GetElementPtr(Index),
            bReplacement ? TEXT("New") : TEXT("Old"));
        Set.Rehash();
        return true;
    }
    if (ShapeIndex == 2)
    {
        const FMapProperty* MapProperty = CastField<FMapProperty>(&Property);
        if (MapProperty == nullptr ||
            CastField<FStrProperty>(MapProperty->KeyProp) == nullptr ||
            CastField<FSoftObjectProperty>(MapProperty->ValueProp) == nullptr ||
            CastField<FSoftClassProperty>(MapProperty->ValueProp) != nullptr)
        {
            return false;
        }
        FScriptMapHelper Map(MapProperty, Address);
        const int32 Index = Map.AddDefaultValue_Invalid_NeedsRehash();
        CastFieldChecked<FStrProperty>(MapProperty->KeyProp)->SetPropertyValue(
            Map.GetKeyPtr(Index),
            bReplacement ? TEXT("New") : TEXT("Old"));
        CastFieldChecked<FSoftObjectProperty>(MapProperty->ValueProp)->SetPropertyValue(
            Map.GetValuePtr(Index),
            FSoftObjectPtr(FSoftObjectPath(
                bReplacement ? TEXT("/Game/Test/NewAsset.NewAsset") : TEXT("/Game/Test/OldAsset.OldAsset"))));
        Map.Rehash();
        return true;
    }
    if (ShapeIndex == 3)
    {
        const FSoftObjectProperty* SoftProperty = CastField<FSoftObjectProperty>(&Property);
        if (SoftProperty == nullptr || CastField<FSoftClassProperty>(&Property) != nullptr)
        {
            return false;
        }
        SoftProperty->SetPropertyValue(
            Address,
            FSoftObjectPtr(FSoftObjectPath(
                bReplacement ? TEXT("/Game/Test/NewAsset.NewAsset") : TEXT("/Game/Test/OldAsset.OldAsset"))));
        return true;
    }
    const FSoftClassProperty* SoftClassProperty = CastField<FSoftClassProperty>(&Property);
    if (ShapeIndex != 4 || SoftClassProperty == nullptr)
    {
        return false;
    }
    SoftClassProperty->SetPropertyValue(
        Address,
        FSoftObjectPtr(FSoftObjectPath(
            bReplacement ? TEXT("/Game/Test/NewAsset.NewAsset") : TEXT("/Game/Test/OldAsset.OldAsset"))));
    return true;
}

bool InvokeTypedCaller(
    UClass& CallerClass,
    UObject& CallerObject,
    UFinareComponent& WorldContext,
    const FString& CallerFunctionName,
    const FString& ObservedPropertyName,
    int32 ShapeIndex,
    FString& OutError)
{
    UFunction* Caller = CallerClass.FindFunctionByName(
        FName(*CallerFunctionName),
        EIncludeSuperFlag::ExcludeSuper);
    const FProperty* Replacement = Caller != nullptr
        ? FindFProperty<FProperty>(Caller, TEXT("replacement"))
        : nullptr;
    const FProperty* Value = Caller != nullptr
        ? FindFProperty<FProperty>(Caller, TEXT("value"))
        : nullptr;
    const FProperty* Previous = Caller != nullptr
        ? FindFProperty<FProperty>(Caller, TEXT("previous"))
        : nullptr;
    const FProperty* Return = Caller != nullptr ? Caller->GetReturnProperty() : nullptr;
    const FProperty* Observed = FindFProperty<FProperty>(&CallerClass, FName(*ObservedPropertyName));
    const FObjectPropertyBase* WorldContextProperty = Caller != nullptr
        ? FindFProperty<FObjectPropertyBase>(Caller, TEXT("WorldContextObject"))
        : nullptr;
    const FObjectPropertyBase* KismetContextProperty = Caller != nullptr
        ? FindFProperty<FObjectPropertyBase>(Caller, TEXT("__WorldContext"))
        : nullptr;
    if (Caller == nullptr || Caller->Script.Num() == 0 || Caller->HasAnyFunctionFlags(FUNC_Native) ||
        Replacement == nullptr || Value == nullptr || Previous == nullptr || Return == nullptr ||
        Observed == nullptr || (WorldContextProperty == nullptr && KismetContextProperty == nullptr) ||
        !Replacement->SameType(Value) || !Value->SameType(Previous) ||
        !Value->SameType(Return) || !Value->SameType(Observed))
    {
        OutError = FString::Printf(
            TEXT("Caller %s lost its cooked Blueprint VM contract (script=%d native=%d)."),
            *CallerFunctionName,
            Caller != nullptr ? Caller->Script.Num() : 0,
            Caller != nullptr && Caller->HasAnyFunctionFlags(FUNC_Native) ? 1 : 0);
        return false;
    }

    FStructOnScope Call(Caller);
    FStructOnScope Before(Caller);
    void* Memory = Call.GetStructMemory();
    if (WorldContextProperty != nullptr)
    {
        WorldContextProperty->SetObjectPropertyValue_InContainer(Memory, &WorldContext);
    }
    if (KismetContextProperty != nullptr)
    {
        KismetContextProperty->SetObjectPropertyValue_InContainer(Memory, &WorldContext);
    }
    if (!FillValue(*Replacement, Replacement->ContainerPtrToValuePtr<void>(Memory), ShapeIndex, true) ||
        !FillValue(*Value, Value->ContainerPtrToValuePtr<void>(Memory), ShapeIndex, false))
    {
        OutError = FString::Printf(TEXT("Caller %s has an unexpected typed property shape."), *CallerFunctionName);
        return false;
    }
    Value->CopyCompleteValue_InContainer(Before.GetStructMemory(), Memory);
    Replacement->CopyCompleteValue_InContainer(Before.GetStructMemory(), Memory);
    CallerObject.ProcessEvent(Caller, Memory);

    const void* OldValue = Value->ContainerPtrToValuePtr<void>(Before.GetStructMemory());
    const void* OriginalReplacement = Replacement->ContainerPtrToValuePtr<void>(Before.GetStructMemory());
    const bool bReplacementUnchanged = Replacement->Identical(
        Replacement->ContainerPtrToValuePtr<void>(Memory),
        OriginalReplacement);
    const bool bRef = Value->Identical(Value->ContainerPtrToValuePtr<void>(Memory), OriginalReplacement);
    const bool bPrevious = Previous->Identical(Previous->ContainerPtrToValuePtr<void>(Memory), OldValue);
    const bool bReturn = Return->Identical(Return->ContainerPtrToValuePtr<void>(Memory), OldValue);
    const bool bObserved = Observed->Identical(Observed->ContainerPtrToValuePtr<void>(&CallerObject), OldValue);
    if (!bReplacementUnchanged || !bRef || !bPrevious || !bReturn || !bObserved)
    {
        OutError = FString::Printf(
            TEXT("Caller %s returned an unexpected input/ref/out/return/Observed result (input=%d ref=%d previous=%d return=%d observed=%d)."),
            *CallerFunctionName,
            bReplacementUnchanged ? 1 : 0,
            bRef ? 1 : 0,
            bPrevious ? 1 : 0,
            bReturn ? 1 : 0,
            bObserved ? 1 : 0);
        return false;
    }
    return true;
}
}

void Run()
{
    FString ReceiptPath;
    if (!ReadArgument(TEXT("FinareFunctionClosureReceipt="), ReceiptPath))
    {
        Finish(false, TEXT("FinareFunctionClosureReceipt is required."));
        return;
    }

    FString PackageFilename;
    FString LibraryClassPath;
    FString CallerClassPath;
    TArray<FString> CallerFunctions;
    TArray<FString> ObservedProperties;
    FString Error;
    if (!LoadReceipt(
            ReceiptPath,
            PackageFilename,
            LibraryClassPath,
            CallerClassPath,
            CallerFunctions,
            ObservedProperties,
            Error))
    {
        Finish(false, Error);
        return;
    }

    UClass* CallerClass = LoadClass<AActor>(nullptr, *CallerClassPath);
    UClass* LibraryClass = LoadObject<UClass>(nullptr, *LibraryClassPath);
    UFinareBlueprintGeneratedClass* GeneratedLibraryClass =
        Cast<UFinareBlueprintGeneratedClass>(LibraryClass);
    UFinareAsset* ScriptAsset = GeneratedLibraryClass != nullptr
        ? GeneratedLibraryClass->ManagedScriptAsset
        : nullptr;
    FString ProgramPath;
    FString TypeName;
    if (CallerClass == nullptr || GeneratedLibraryClass == nullptr || ScriptAsset == nullptr ||
        !ScriptAsset->ResolveRuntimeConfiguration(true, ProgramPath, TypeName, Error))
    {
        Finish(false, Error.IsEmpty()
            ? TEXT("Cooked caller or generated FunctionLibrary class could not be loaded.")
            : Error);
        return;
    }
    const auto NormalizeRuntimePath = [](FString Path)
    {
        if (FPaths::IsRelative(Path))
        {
            Path = FPaths::Combine(FPlatformProcess::BaseDir(), Path);
        }
        Path = FPaths::ConvertRelativePathToFull(Path);
        FPaths::NormalizeFilename(Path);
        return Path;
    };
    ProgramPath = NormalizeRuntimePath(ProgramPath);

    const int32 InitialManagedWorldCount =
        FFinareCoreClrHost::Get().GetManagedWorldCount();
    if (GEngine == nullptr)
    {
        Finish(false, TEXT("The global Engine was unavailable for the verification World."));
        return;
    }
    UWorld* World = UWorld::CreateWorld(EWorldType::Game, false);
    if (World == nullptr)
    {
        Finish(false, TEXT("Could not create the verification World."));
        return;
    }
    World->AddToRoot();
    FWorldContext* WorldContext = &GEngine->CreateNewWorldContext(EWorldType::Game);
    WorldContext->SetCurrentWorld(World);
    World->InitializeActorsForPlay(FURL());
    UFinareWorldSubsystem* Host = World->GetSubsystem<UFinareWorldSubsystem>();
    UFinareComponent* Context = NewObject<UFinareComponent>(World);
    AActor* CallerObject = CallerClass != nullptr
        ? World->SpawnActor<AActor>(CallerClass)
        : nullptr;
    ON_SCOPE_EXIT
    {
        if (Context != nullptr)
        {
            Context->UnregisterComponent();
        }
        if (World != nullptr && CallerObject != nullptr)
        {
            World->DestroyActor(CallerObject);
        }
        if (World != nullptr)
        {
            World->RemoveFromRoot();
            GEngine->DestroyWorldContext(World);
            World->DestroyWorld(false);
        }
    };
    if (Host == nullptr || !Host->IsManagedWorldReady() || Context == nullptr || CallerObject == nullptr)
    {
        Finish(false, TEXT("Verification World, managed host, context, or caller Actor was not ready."));
        return;
    }
    Context->RegisterComponentWithWorld(World);
    if (!Context->IsRegistered())
    {
        Finish(false, TEXT("Verification World context did not register."));
        return;
    }

    FString HostFxrPath = FFinareCoreClrHost::Get().GetHostFxrPath();
    HostFxrPath = NormalizeRuntimePath(HostFxrPath);
    FString PackagedProjectRoot = FPaths::ConvertRelativePathToFull(
        FPaths::Combine(FPlatformProcess::BaseDir(), TEXT("../../")));
    FPaths::NormalizeFilename(PackagedProjectRoot);
    while (PackagedProjectRoot.EndsWith(TEXT("/")))
    {
        PackagedProjectRoot.LeftChopInline(1);
    }
    const auto IsUnderPackagedProject = [&PackagedProjectRoot](const FString& Path)
    {
        return Path == PackagedProjectRoot ||
            Path.StartsWith(PackagedProjectRoot + TEXT("/"), ESearchCase::IgnoreCase);
    };
    if (!FPlatformProperties::RequiresCookedData() ||
        !ProgramPath.EndsWith(TEXT(".uschunk"), ESearchCase::IgnoreCase) ||
        !ProgramPath.Contains(TEXT("/Plugins/Finare/Binaries/Managed/Demo/"), ESearchCase::IgnoreCase) ||
        !IFileManager::Get().FileExists(*ProgramPath) ||
        !IsUnderPackagedProject(ProgramPath) ||
        !HostFxrPath.Contains(TEXT("/Plugins/Finare/Binaries/ThirdParty/DotNet/10.0/"), ESearchCase::IgnoreCase) ||
        !IsUnderPackagedProject(HostFxrPath))
    {
        Finish(
            false,
            FString::Printf(
                TEXT("Cooked runtime paths are invalid; cooked=%d root=%s package=%s program=%s hostfxr=%s."),
                FPlatformProperties::RequiresCookedData() ? 1 : 0,
                *PackagedProjectRoot,
                *PackageFilename,
                *ProgramPath,
                *HostFxrPath));
        return;
    }

    static const TCHAR* ExpectedFunctions[] = {
        TEXT("Call_ExchangeArray"),
        TEXT("Call_ExchangeSet"),
        TEXT("Call_ExchangeMap"),
        TEXT("Call_ExchangeSoftObject"),
        TEXT("Call_ExchangeSoftClass")};
    for (int32 Index = 0; Index < UE_ARRAY_COUNT(ExpectedFunctions); ++Index)
    {
        if (CallerFunctions[Index] != ExpectedFunctions[Index] ||
            !InvokeTypedCaller(
                *CallerClass,
                *CallerObject,
                *Context,
                CallerFunctions[Index],
                ObservedProperties[Index],
                Index,
                Error))
        {
            Finish(false, Error.IsEmpty()
                ? FString::Printf(TEXT("Receipt function order is invalid at index %d."), Index)
                : Error);
            return;
        }
    }
    Context->UnregisterComponent();
    World->DestroyActor(CallerObject);
    CallerObject = nullptr;
    World->RemoveFromRoot();
    GEngine->DestroyWorldContext(World);
    World->DestroyWorld(false);
    World = nullptr;
    const int32 FinalManagedWorldCount =
        FFinareCoreClrHost::Get().GetManagedWorldCount();
    if (FinalManagedWorldCount != InitialManagedWorldCount)
    {
        Finish(
            false,
            FString::Printf(
                TEXT("Managed World count leaked from %d to %d."),
                InitialManagedWorldCount,
                FinalManagedWorldCount));
        return;
    }
    Finish(
        true,
        FString::Printf(
            TEXT("Cooked Blueprint VM caller executed five DemoLibrary typed exchanges; program=%s; hostfxr=%s."),
            *ProgramPath,
            *HostFxrPath));
}
}
