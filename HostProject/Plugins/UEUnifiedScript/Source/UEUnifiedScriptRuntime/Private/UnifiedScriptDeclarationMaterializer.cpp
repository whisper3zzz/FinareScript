/**
 * @Copyright: 2026 Tencent Inc. All rights reserved. reserved.
 * @Author: whispersong
 * @Date: 2026-08-24 17:37:05
 * @LastEditors: whispersong
 * @LastEditTime: 2026-08-24 19:01:04
 * @FilePath: \Plugins\UEUnifiedScript\Source\UEUnifiedScriptRuntime\Private\UnifiedScriptDeclarationMaterializer.cpp
 * @Description:
 */
#include "UnifiedScriptDeclarationMaterializer.h"

#include "Dom/JsonObject.h"
#include "HAL/FileManager.h"
#include "Misc/FileHelper.h"
#include "Serialization/JsonReader.h"
#include "Serialization/JsonSerializer.h"

namespace UE::UnifiedScript::Declaration::Private
{
constexpr TCHAR SchemaVersion[] = TEXT("unifiedscript.script-asset/v0.1");

bool ParseSemanticId(const FString& SemanticId, FGuid& OutId)
{
    FString Digits = SemanticId;
    if (!Digits.RemoveFromStart(TEXT("us_")) || Digits.Len() != 32)
    {
        return false;
    }
    return FGuid::ParseExact(Digits, EGuidFormats::Digits, OutId);
}

bool ResolvePropertyType(
    const FString& Kind,
    const FString& MetadataName,
    EPropertyBagPropertyType& OutType)
{
    if (Kind == TEXT("boolean"))
    {
        OutType = EPropertyBagPropertyType::Bool;
        return MetadataName == TEXT("System.Boolean");
    }
    if (Kind == TEXT("integer"))
    {
        if (MetadataName == TEXT("System.Int64"))
        {
            OutType = EPropertyBagPropertyType::Int64;
            return true;
        }
        if (MetadataName == TEXT("System.Int32"))
        {
            OutType = EPropertyBagPropertyType::Int32;
            return true;
        }
        return false;
    }
    if (Kind == TEXT("floatingPoint"))
    {
        if (MetadataName == TEXT("System.Single"))
        {
            OutType = EPropertyBagPropertyType::Float;
            return true;
        }
        if (MetadataName == TEXT("System.Double"))
        {
            OutType = EPropertyBagPropertyType::Double;
            return true;
        }
        return false;
    }
    if (Kind == TEXT("string") && MetadataName == TEXT("System.String"))
    {
        OutType = EPropertyBagPropertyType::String;
        return true;
    }
    if (Kind == TEXT("enum"))
    {
        // Declaration v0.1 does not carry the Unreal UEnum needed by PropertyBag.
        // Preserve the declared enum token as an editable string until that contract exists.
        OutType = EPropertyBagPropertyType::String;
        return true;
    }
    return false;
}

bool SetDefaultValue(
    FInstancedPropertyBag& Bag,
    const FPropertyBagPropertyDesc& Desc,
    const TSharedPtr<FJsonValue>& DefaultValue,
    FString& OutError)
{
    if (!DefaultValue.IsValid() || DefaultValue->IsNull())
    {
        return true;
    }

    EPropertyBagResult Result = EPropertyBagResult::TypeMismatch;
    switch (Desc.ValueType)
    {
    case EPropertyBagPropertyType::Bool:
        if (DefaultValue->Type == EJson::Boolean)
        {
            Result = Bag.SetValueBool(Desc.Name, DefaultValue->AsBool());
        }
        break;
    case EPropertyBagPropertyType::Int32:
        if (DefaultValue->Type == EJson::Number)
        {
            const double Number = DefaultValue->AsNumber();
            if (Number >= MIN_int32 && Number <= MAX_int32 && FMath::TruncToDouble(Number) == Number)
            {
                Result = Bag.SetValueInt32(Desc.Name, static_cast<int32>(Number));
            }
        }
        break;
    case EPropertyBagPropertyType::Int64:
        if (DefaultValue->Type == EJson::Number)
        {
            const double Number = DefaultValue->AsNumber();
            if (Number >= static_cast<double>(MIN_int64) &&
                Number <= static_cast<double>(MAX_int64) &&
                FMath::TruncToDouble(Number) == Number)
            {
                Result = Bag.SetValueInt64(Desc.Name, static_cast<int64>(Number));
            }
        }
        break;
    case EPropertyBagPropertyType::Float:
        if (DefaultValue->Type == EJson::Number)
        {
            Result = Bag.SetValueFloat(Desc.Name, static_cast<float>(DefaultValue->AsNumber()));
        }
        break;
    case EPropertyBagPropertyType::Double:
        if (DefaultValue->Type == EJson::Number)
        {
            Result = Bag.SetValueDouble(Desc.Name, DefaultValue->AsNumber());
        }
        break;
    case EPropertyBagPropertyType::String:
        if (DefaultValue->Type == EJson::String)
        {
            Result = Bag.SetValueString(Desc.Name, DefaultValue->AsString());
        }
        break;
    default:
        break;
    }

    if (Result != EPropertyBagResult::Success)
    {
        OutError = FString::Printf(
            TEXT("State '%s' has a defaultValue incompatible with its declared type."),
            *Desc.Name.ToString());
        return false;
    }
    return true;
}

bool SelectAsset(
    const TArray<TSharedPtr<FJsonValue>>& Assets,
    const FString& RequestedAssetPath,
    TSharedPtr<FJsonObject>& OutAsset,
    FString& OutAssetPath,
    FString& OutError)
{
    for (const TSharedPtr<FJsonValue>& AssetValue : Assets)
    {
        const TSharedPtr<FJsonObject> Asset = AssetValue.IsValid()
            ? AssetValue->AsObject()
            : nullptr;
        FString AssetPath;
        if (!Asset.IsValid() || !Asset->TryGetStringField(TEXT("assetPath"), AssetPath))
        {
            OutError = TEXT("Declaration assets must contain an assetPath string.");
            return false;
        }
        if ((RequestedAssetPath.IsEmpty() && Assets.Num() == 1) ||
            AssetPath == RequestedAssetPath)
        {
            OutAsset = Asset;
            OutAssetPath = MoveTemp(AssetPath);
            return true;
        }
    }

    OutError = RequestedAssetPath.IsEmpty()
        ? TEXT("DeclarationAssetPath is required when a declaration contains multiple assets.")
        : FString::Printf(
            TEXT("Declaration does not contain assetPath '%s'."),
            *RequestedAssetPath);
    return false;
}

TArray<FGuid> CollectAllPropertyIds(const FInstancedPropertyBag& Bag)
{
    TArray<FGuid> Result;
    if (const UPropertyBag* BagStruct = Bag.GetPropertyBagStruct())
    {
        Result.Reserve(BagStruct->GetPropertyDescs().Num());
        for (const FPropertyBagPropertyDesc& Desc : BagStruct->GetPropertyDescs())
        {
            Result.Add(Desc.ID);
        }
    }
    return Result;
}
}

bool FUnifiedScriptDeclarationMaterializer::MaterializeFile(
    const FString& DeclarationPath,
    const FString& RequestedAssetPath,
    const FInstancedPropertyBag& ExistingState,
    FInstancedPropertyBag& OutState,
    FString& OutAssetPath,
    FString& OutError)
{
    FString Json;
    if (!IFileManager::Get().FileExists(*DeclarationPath) ||
        !FFileHelper::LoadFileToString(Json, *DeclarationPath))
    {
        OutError = FString::Printf(
            TEXT("The Script Asset declaration does not exist or cannot be read: '%s'."),
            *DeclarationPath);
        return false;
    }
    return MaterializeJson(
        Json,
        RequestedAssetPath,
        ExistingState,
        OutState,
        OutAssetPath,
        OutError);
}

bool FUnifiedScriptDeclarationMaterializer::MaterializeFileWithOverrides(
    const FString& DeclarationPath,
    const FString& RequestedAssetPath,
    const FInstancedPropertyBag& ExistingState,
    TConstArrayView<FGuid> ExistingOverrideIds,
    FInstancedPropertyBag& OutState,
    TArray<FGuid>& OutOverrideIds,
    FUnifiedScriptStateMigrationReport& OutMigration,
    FString& OutAssetPath,
    FString& OutError)
{
    FString Json;
    if (!IFileManager::Get().FileExists(*DeclarationPath) ||
        !FFileHelper::LoadFileToString(Json, *DeclarationPath))
    {
        OutError = FString::Printf(
            TEXT("The Script Asset declaration does not exist or cannot be read: '%s'."),
            *DeclarationPath);
        return false;
    }
    return MaterializeJsonWithOverrides(
        Json,
        RequestedAssetPath,
        ExistingState,
        ExistingOverrideIds,
        OutState,
        OutOverrideIds,
        OutMigration,
        OutAssetPath,
        OutError);
}

bool FUnifiedScriptDeclarationMaterializer::MaterializeJson(
    const FString& DeclarationJson,
    const FString& RequestedAssetPath,
    const FInstancedPropertyBag& ExistingState,
    FInstancedPropertyBag& OutState,
    FString& OutAssetPath,
    FString& OutError)
{
    using namespace UE::UnifiedScript::Declaration::Private;

    const TArray<FGuid> LegacyOverrideIds = CollectAllPropertyIds(ExistingState);
    TArray<FGuid> MigratedOverrideIds;
    FUnifiedScriptStateMigrationReport Migration;
    return MaterializeJsonWithOverrides(
        DeclarationJson,
        RequestedAssetPath,
        ExistingState,
        LegacyOverrideIds,
        OutState,
        MigratedOverrideIds,
        Migration,
        OutAssetPath,
        OutError);
}

bool FUnifiedScriptDeclarationMaterializer::MaterializeJsonWithOverrides(
    const FString& DeclarationJson,
    const FString& RequestedAssetPath,
    const FInstancedPropertyBag& ExistingState,
    TConstArrayView<FGuid> ExistingOverrideIds,
    FInstancedPropertyBag& OutState,
    TArray<FGuid>& OutOverrideIds,
    FUnifiedScriptStateMigrationReport& OutMigration,
    FString& OutAssetPath,
    FString& OutError)
{
    using namespace UE::UnifiedScript::Declaration::Private;

    TSharedPtr<FJsonObject> Root;
    const TSharedRef<TJsonReader<>> Reader = TJsonReaderFactory<>::Create(DeclarationJson);
    if (!FJsonSerializer::Deserialize(Reader, Root) || !Root.IsValid())
    {
        OutError = FString::Printf(
            TEXT("Script Asset declaration JSON is invalid: %s"),
            *Reader->GetErrorMessage());
        return false;
    }

    FString ParsedSchema;
    if (!Root->TryGetStringField(TEXT("schemaVersion"), ParsedSchema) ||
        ParsedSchema != SchemaVersion)
    {
        OutError = FString::Printf(
            TEXT("Unsupported Script Asset declaration schema '%s'."),
            *ParsedSchema);
        return false;
    }

    const TArray<TSharedPtr<FJsonValue>>* Assets = nullptr;
    if (!Root->TryGetArrayField(TEXT("assets"), Assets) || Assets == nullptr || Assets->IsEmpty())
    {
        OutError = TEXT("Script Asset declaration must contain at least one asset.");
        return false;
    }

    TSharedPtr<FJsonObject> Asset;
    FString SelectedAssetPath;
    if (!SelectAsset(*Assets, RequestedAssetPath, Asset, SelectedAssetPath, OutError))
    {
        return false;
    }

    const TArray<TSharedPtr<FJsonValue>>* States = nullptr;
    if (!Asset->TryGetArrayField(TEXT("states"), States) || States == nullptr)
    {
        OutError = FString::Printf(
            TEXT("Declaration asset '%s' must contain a states array."),
            *SelectedAssetPath);
        return false;
    }

    TArray<FPropertyBagPropertyDesc> Descs;
    TArray<TSharedPtr<FJsonValue>> Defaults;
    TSet<FGuid> SeenIds;
    TSet<FName> SeenNames;
    Descs.Reserve(States->Num());
    Defaults.Reserve(States->Num());
    for (const TSharedPtr<FJsonValue>& StateValue : *States)
    {
        const TSharedPtr<FJsonObject> State = StateValue.IsValid()
            ? StateValue->AsObject()
            : nullptr;
        FString SemanticId;
        FString Name;
        FString TypeKind;
        FString MetadataName;
        if (!State.IsValid() ||
            !State->TryGetStringField(TEXT("id"), SemanticId) ||
            !State->TryGetStringField(TEXT("name"), Name) ||
            !State->TryGetStringField(TEXT("typeKind"), TypeKind) ||
            !State->TryGetStringField(TEXT("typeMetadataName"), MetadataName))
        {
            OutError = FString::Printf(
                TEXT("Declaration asset '%s' contains an incomplete state."),
                *SelectedAssetPath);
            return false;
        }

        FGuid Id;
        const FName PropertyName(Name);
        EPropertyBagPropertyType PropertyType = EPropertyBagPropertyType::None;
        if (!ParseSemanticId(SemanticId, Id))
        {
            OutError = FString::Printf(TEXT("State '%s' has invalid semantic id '%s'."), *Name, *SemanticId);
            return false;
        }
        if (Name.IsEmpty() || PropertyName.IsNone())
        {
            OutError = TEXT("Declaration state names cannot be empty.");
            return false;
        }
        if (SeenIds.Contains(Id) || SeenNames.Contains(PropertyName))
        {
            OutError = FString::Printf(TEXT("Declaration contains duplicate state '%s' or semantic id."), *Name);
            return false;
        }
        if (!ResolvePropertyType(TypeKind, MetadataName, PropertyType))
        {
            OutError = FString::Printf(
                TEXT("State '%s' uses unsupported declaration type '%s' (%s)."),
                *Name,
                *TypeKind,
                *MetadataName);
            return false;
        }

        bool bInstanceEditable = false;
        bool bSaveGame = false;
        bool bReplicated = false;
        FString Category;
        const TSharedPtr<FJsonObject>* Exposure = nullptr;
        if (State->TryGetObjectField(TEXT("exposure"), Exposure) && Exposure != nullptr)
        {
            (*Exposure)->TryGetBoolField(TEXT("instanceEditable"), bInstanceEditable);
            (*Exposure)->TryGetBoolField(TEXT("saveGame"), bSaveGame);
            (*Exposure)->TryGetBoolField(TEXT("replicated"), bReplicated);
            (*Exposure)->TryGetStringField(TEXT("category"), Category);
        }

        EPropertyFlags Flags = CPF_Edit;
        if (!bInstanceEditable)
        {
            Flags |= CPF_EditConst;
        }
        if (bSaveGame)
        {
            Flags |= CPF_SaveGame;
        }
        if (bReplicated)
        {
            Flags |= CPF_Net;
        }

        FPropertyBagPropertyDesc& Desc = Descs.Emplace_GetRef(
            PropertyName,
            EPropertyBagContainerType::None,
            PropertyType,
            nullptr,
            Flags);
        Desc.ID = Id;
#if WITH_EDITOR
        Desc.SetMetaData(TEXT("DisplayName"), Name);
        Desc.SetMetaData(TEXT("UnifiedScriptSemanticId"), SemanticId);
        if (!Category.IsEmpty())
        {
            Desc.SetMetaData(TEXT("Category"), Category);
        }
#endif
        Defaults.Add(State->TryGetField(TEXT("defaultValue")));
        SeenIds.Add(Id);
        SeenNames.Add(PropertyName);
    }

    FInstancedPropertyBag DefaultsBag;
    if (!Descs.IsEmpty() &&
        DefaultsBag.AddProperties(Descs) != EPropertyBagAlterationResult::Success)
    {
        OutError = TEXT("Unreal could not create the declared Script State layout.");
        return false;
    }
    for (int32 Index = 0; Index < Descs.Num(); ++Index)
    {
        if (!SetDefaultValue(DefaultsBag, Descs[Index], Defaults[Index], OutError))
        {
            return false;
        }
    }

    TSet<FGuid> RequestedOverrideIds;
    RequestedOverrideIds.Reserve(ExistingOverrideIds.Num());
    for (const FGuid& ExistingOverrideId : ExistingOverrideIds)
    {
        RequestedOverrideIds.Add(ExistingOverrideId);
    }

    TArray<FGuid> CompatibleOverrideIds;
    CompatibleOverrideIds.Reserve(RequestedOverrideIds.Num());
    for (const FPropertyBagPropertyDesc& TargetDesc : Descs)
    {
        if (!RequestedOverrideIds.Contains(TargetDesc.ID))
        {
            continue;
        }

        const FPropertyBagPropertyDesc* ExistingDesc =
            ExistingState.FindPropertyDescByID(TargetDesc.ID);
        if (ExistingDesc != nullptr && TargetDesc.CompatibleType(*ExistingDesc))
        {
            CompatibleOverrideIds.Add(TargetDesc.ID);
        }
    }

    FInstancedPropertyBag Candidate;
    if (Descs.IsEmpty())
    {
        Candidate.Reset();
    }
    else if (ExistingState.IsValid())
    {
        Candidate = ExistingState;
        Candidate.MigrateToNewBagInstanceWithOverrides(
            DefaultsBag,
            CompatibleOverrideIds);
    }
    else
    {
        Candidate = MoveTemp(DefaultsBag);
    }

    FUnifiedScriptStateMigrationReport Migration;
    Migration.DeclaredStateCount = Descs.Num();
    Migration.PreservedOverrideCount = CompatibleOverrideIds.Num();
    Migration.DefaultedStateCount = Descs.Num() - CompatibleOverrideIds.Num();
    Migration.DroppedOverrideCount = RequestedOverrideIds.Num() -
        CompatibleOverrideIds.Num();

    OutState = MoveTemp(Candidate);
    OutOverrideIds = MoveTemp(CompatibleOverrideIds);
    OutMigration = Migration;
    OutAssetPath = MoveTemp(SelectedAssetPath);
    OutError.Reset();
    return true;
}
