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

#include "Containers/StringConv.h"
#include "Dom/JsonObject.h"
#include "HAL/FileManager.h"
#include "Misc/FileHelper.h"
#include "Serialization/JsonReader.h"
#include "Serialization/JsonSerializer.h"
#include "UnifiedScriptCompiledChunk.h"

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

enum class EBinaryDefaultKind : uint8
{
    Null = 0,
    Boolean = 1,
    SignedInteger = 2,
    UnsignedInteger = 3,
    FloatingPoint = 4,
    String = 5,
    Enum = 6,
};

struct FBinaryDefault
{
    EBinaryDefaultKind Kind = EBinaryDefaultKind::Null;
    bool bBoolean = false;
    int64 SignedInteger = 0;
    uint64 UnsignedInteger = 0;
    double FloatingPoint = 0.0;
    FString Text;
};

class FBinaryDeclarationReader
{
public:
    explicit FBinaryDeclarationReader(TConstArrayView<uint8> InData)
        : Data(InData)
    {
    }

    bool ReadHeader(FString& OutError)
    {
        constexpr uint8 Magic[8] = {'U', 'S', 'R', 'D', 'E', 'C', 0, 0};
        if (Data.Num() < 16 || FMemory::Memcmp(Data.GetData(), Magic, sizeof(Magic)) != 0)
        {
            OutError = TEXT("Compiled declaration payload magic is invalid.");
            return false;
        }
        Offset = 8;
        uint16 Major = 0;
        uint16 Minor = 0;
        uint32 Reserved = 0;
        if (!ReadUInt16(Major) || !ReadUInt16(Minor) || !ReadUInt32(Reserved) ||
            Major != 1 || Minor != 0 || Reserved != 0)
        {
            OutError = TEXT("Compiled declaration payload is not canonical v1.0.");
            return false;
        }
        return true;
    }

    bool ReadByte(uint8& OutValue)
    {
        if (!Require(1))
        {
            return false;
        }
        OutValue = Data[Offset++];
        return true;
    }

    bool ReadUInt16(uint16& OutValue)
    {
        if (!Require(2))
        {
            return false;
        }
        OutValue = static_cast<uint16>(Data[Offset]) |
            static_cast<uint16>(Data[Offset + 1]) << 8;
        Offset += 2;
        return true;
    }

    bool ReadUInt32(uint32& OutValue)
    {
        if (!Require(4))
        {
            return false;
        }
        OutValue = static_cast<uint32>(Data[Offset]) |
            static_cast<uint32>(Data[Offset + 1]) << 8 |
            static_cast<uint32>(Data[Offset + 2]) << 16 |
            static_cast<uint32>(Data[Offset + 3]) << 24;
        Offset += 4;
        return true;
    }

    bool ReadUInt64(uint64& OutValue)
    {
        uint32 Low = 0;
        uint32 High = 0;
        if (!ReadUInt32(Low) || !ReadUInt32(High))
        {
            return false;
        }
        OutValue = static_cast<uint64>(Low) | static_cast<uint64>(High) << 32;
        return true;
    }

    bool ReadString(FString& OutValue)
    {
        uint32 Length = 0;
        if (!ReadUInt32(Length) || Length > 16 * 1024 * 1024 ||
            Length > static_cast<uint32>(MAX_int32) ||
            !Require(static_cast<int32>(Length)))
        {
            return false;
        }
        const FUTF8ToTCHAR Converted(
            reinterpret_cast<const ANSICHAR*>(Data.GetData() + Offset),
            static_cast<int32>(Length));
        OutValue = FString(Converted.Length(), Converted.Get());
        Offset += static_cast<int32>(Length);
        return true;
    }

    bool ReadNullableString(FString& OutValue, bool& bOutHasValue)
    {
        uint8 HasValue = 0;
        if (!ReadByte(HasValue) || HasValue > 1)
        {
            return false;
        }
        bOutHasValue = HasValue != 0;
        if (!bOutHasValue)
        {
            OutValue.Reset();
            return true;
        }
        return ReadString(OutValue);
    }

    bool ReadCount(uint32& OutCount)
    {
        return ReadUInt32(OutCount) && OutCount <= 1'000'000;
    }

    bool ReadDefault(FBinaryDefault& OutDefault)
    {
        uint8 Kind = 0;
        if (!ReadByte(Kind) || Kind > static_cast<uint8>(EBinaryDefaultKind::Enum))
        {
            return false;
        }
        OutDefault.Kind = static_cast<EBinaryDefaultKind>(Kind);
        switch (OutDefault.Kind)
        {
        case EBinaryDefaultKind::Null:
            return true;
        case EBinaryDefaultKind::Boolean:
        {
            uint8 Value = 0;
            if (!ReadByte(Value) || Value > 1)
            {
                return false;
            }
            OutDefault.bBoolean = Value != 0;
            return true;
        }
        case EBinaryDefaultKind::SignedInteger:
        {
            uint64 Bits = 0;
            if (!ReadUInt64(Bits))
            {
                return false;
            }
            OutDefault.SignedInteger = static_cast<int64>(Bits);
            return true;
        }
        case EBinaryDefaultKind::UnsignedInteger:
            return ReadUInt64(OutDefault.UnsignedInteger);
        case EBinaryDefaultKind::FloatingPoint:
        {
            uint64 Bits = 0;
            if (!ReadUInt64(Bits))
            {
                return false;
            }
            FMemory::Memcpy(&OutDefault.FloatingPoint, &Bits, sizeof(double));
            return FMath::IsFinite(OutDefault.FloatingPoint);
        }
        case EBinaryDefaultKind::String:
        case EBinaryDefaultKind::Enum:
            return ReadString(OutDefault.Text);
        default:
            return false;
        }
    }

    bool IsAtEnd() const { return Offset == Data.Num(); }

private:
    bool Require(int32 Length) const
    {
        return Length >= 0 && Offset >= 0 && Offset <= Data.Num() &&
            Length <= Data.Num() - Offset;
    }

    TConstArrayView<uint8> Data;
    int32 Offset = 0;
};

bool ResolveBinaryPropertyType(
    uint8 TypeKind,
    const FString& MetadataName,
    EPropertyBagPropertyType& OutType)
{
    switch (TypeKind)
    {
    case 0:
        return ResolvePropertyType(TEXT("boolean"), MetadataName, OutType);
    case 1:
        return ResolvePropertyType(TEXT("integer"), MetadataName, OutType);
    case 2:
        return ResolvePropertyType(TEXT("floatingPoint"), MetadataName, OutType);
    case 3:
        return ResolvePropertyType(TEXT("string"), MetadataName, OutType);
    case 4:
        return ResolvePropertyType(TEXT("enum"), MetadataName, OutType);
    default:
        return false;
    }
}

bool SetBinaryDefaultValue(
    FInstancedPropertyBag& Bag,
    const FPropertyBagPropertyDesc& Desc,
    const FBinaryDefault& Default,
    FString& OutError)
{
    if (Default.Kind == EBinaryDefaultKind::Null)
    {
        return Desc.ValueType == EPropertyBagPropertyType::String;
    }

    EPropertyBagResult Result = EPropertyBagResult::TypeMismatch;
    switch (Desc.ValueType)
    {
    case EPropertyBagPropertyType::Bool:
        if (Default.Kind == EBinaryDefaultKind::Boolean)
        {
            Result = Bag.SetValueBool(Desc.Name, Default.bBoolean);
        }
        break;
    case EPropertyBagPropertyType::Int32:
        if (Default.Kind == EBinaryDefaultKind::SignedInteger &&
            Default.SignedInteger >= MIN_int32 && Default.SignedInteger <= MAX_int32)
        {
            Result = Bag.SetValueInt32(Desc.Name, static_cast<int32>(Default.SignedInteger));
        }
        else if (Default.Kind == EBinaryDefaultKind::UnsignedInteger &&
            Default.UnsignedInteger <= MAX_int32)
        {
            Result = Bag.SetValueInt32(Desc.Name, static_cast<int32>(Default.UnsignedInteger));
        }
        break;
    case EPropertyBagPropertyType::Int64:
        if (Default.Kind == EBinaryDefaultKind::SignedInteger)
        {
            Result = Bag.SetValueInt64(Desc.Name, Default.SignedInteger);
        }
        else if (Default.Kind == EBinaryDefaultKind::UnsignedInteger &&
            Default.UnsignedInteger <= MAX_int64)
        {
            Result = Bag.SetValueInt64(Desc.Name, static_cast<int64>(Default.UnsignedInteger));
        }
        break;
    case EPropertyBagPropertyType::Float:
        if (Default.Kind == EBinaryDefaultKind::FloatingPoint)
        {
            Result = Bag.SetValueFloat(Desc.Name, static_cast<float>(Default.FloatingPoint));
        }
        break;
    case EPropertyBagPropertyType::Double:
        if (Default.Kind == EBinaryDefaultKind::FloatingPoint)
        {
            Result = Bag.SetValueDouble(Desc.Name, Default.FloatingPoint);
        }
        break;
    case EPropertyBagPropertyType::String:
        if (Default.Kind == EBinaryDefaultKind::String ||
            Default.Kind == EBinaryDefaultKind::Enum)
        {
            Result = Bag.SetValueString(Desc.Name, Default.Text);
        }
        break;
    default:
        break;
    }

    if (Result != EPropertyBagResult::Success)
    {
        OutError = FString::Printf(
            TEXT("State '%s' has a binary default incompatible with its declared type."),
            *Desc.Name.ToString());
        return false;
    }
    return true;
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

bool FUnifiedScriptDeclarationMaterializer::MaterializeChunkFileWithOverrides(
    const FString& ChunkPath,
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

    TArray<uint8> Payload;
    if (!FUnifiedScriptCompiledChunk::LoadSection(
            ChunkPath,
            EUnifiedScriptCompiledChunkSection::DeclarationData,
            Payload,
            OutError))
    {
        return false;
    }

    FBinaryDeclarationReader Reader(Payload);
    FString CompilationIdText;
    FString ConfigurationIdText;
    FGuid CompilationId;
    FGuid ConfigurationId;
    uint32 AssetCount = 0;
    if (!Reader.ReadHeader(OutError) ||
        !Reader.ReadString(CompilationIdText) ||
        !Reader.ReadString(ConfigurationIdText) ||
        !ParseSemanticId(CompilationIdText, CompilationId) ||
        !ParseSemanticId(ConfigurationIdText, ConfigurationId) ||
        !Reader.ReadCount(AssetCount) || AssetCount == 0)
    {
        if (OutError.IsEmpty())
        {
            OutError = TEXT("Compiled declaration header or identity is invalid.");
        }
        return false;
    }

    TSet<FGuid> SeenAssetIds;
    TSet<FString> SeenAssetPaths;
    TArray<FPropertyBagPropertyDesc> Descs;
    TArray<FBinaryDefault> Defaults;
    FString SelectedAssetPath;
    bool bSelected = false;
    for (uint32 AssetIndex = 0; AssetIndex < AssetCount; ++AssetIndex)
    {
        FString AssetIdText;
        FString QualifiedName;
        FString AssetPath;
        FGuid AssetId;
        uint32 StateCount = 0;
        if (!Reader.ReadString(AssetIdText) || !ParseSemanticId(AssetIdText, AssetId) ||
            !Reader.ReadString(QualifiedName) || QualifiedName.IsEmpty() ||
            !Reader.ReadString(AssetPath) || AssetPath.IsEmpty() ||
            SeenAssetIds.Contains(AssetId) || SeenAssetPaths.Contains(AssetPath) ||
            !Reader.ReadCount(StateCount))
        {
            OutError = TEXT("Compiled declaration contains an invalid or duplicate asset.");
            return false;
        }
        SeenAssetIds.Add(AssetId);
        SeenAssetPaths.Add(AssetPath);

        const bool bUseAsset =
            (RequestedAssetPath.IsEmpty() && AssetCount == 1) ||
            AssetPath == RequestedAssetPath;
        if (bUseAsset)
        {
            if (bSelected)
            {
                OutError = TEXT("Compiled declaration selects more than one asset.");
                return false;
            }
            bSelected = true;
            SelectedAssetPath = AssetPath;
            Descs.Reserve(static_cast<int32>(StateCount));
            Defaults.Reserve(static_cast<int32>(StateCount));
        }

        TSet<FGuid> SeenStateIds;
        TSet<FName> SeenStateNames;
        for (uint32 StateIndex = 0; StateIndex < StateCount; ++StateIndex)
        {
            FString StateIdText;
            FString Name;
            FString TypeDisplayName;
            FString MetadataName;
            FGuid StateId;
            uint8 TypeKind = 0;
            FBinaryDefault Default;
            uint8 ExposureFlags = 0;
            FString Category;
            bool bHasCategory = false;
            if (!Reader.ReadString(StateIdText) || !ParseSemanticId(StateIdText, StateId) ||
                !Reader.ReadString(Name) || Name.IsEmpty() ||
                !Reader.ReadString(TypeDisplayName) || TypeDisplayName.IsEmpty() ||
                !Reader.ReadString(MetadataName) || MetadataName.IsEmpty() ||
                !Reader.ReadByte(TypeKind) || !Reader.ReadDefault(Default) ||
                !Reader.ReadByte(ExposureFlags) || (ExposureFlags & ~0x07) != 0 ||
                !Reader.ReadNullableString(Category, bHasCategory))
            {
                OutError = FString::Printf(
                    TEXT("Compiled declaration asset '%s' contains an invalid state."),
                    *AssetPath);
                return false;
            }

            if (!bUseAsset)
            {
                continue;
            }

            const FName PropertyName(Name);
            EPropertyBagPropertyType PropertyType = EPropertyBagPropertyType::None;
            if (PropertyName.IsNone() || SeenStateIds.Contains(StateId) ||
                SeenStateNames.Contains(PropertyName) ||
                !ResolveBinaryPropertyType(TypeKind, MetadataName, PropertyType))
            {
                OutError = FString::Printf(
                    TEXT("Compiled declaration contains invalid or duplicate state '%s'."),
                    *Name);
                return false;
            }

            EPropertyFlags Flags = CPF_Edit;
            if ((ExposureFlags & (1 << 0)) == 0)
            {
                Flags |= CPF_EditConst;
            }
            if ((ExposureFlags & (1 << 1)) != 0)
            {
                Flags |= CPF_SaveGame;
            }
            if ((ExposureFlags & (1 << 2)) != 0)
            {
                Flags |= CPF_Net;
            }
            FPropertyBagPropertyDesc& Desc = Descs.Emplace_GetRef(
                PropertyName,
                EPropertyBagContainerType::None,
                PropertyType,
                nullptr,
                Flags);
            Desc.ID = StateId;
#if WITH_EDITOR
            Desc.SetMetaData(TEXT("DisplayName"), Name);
            Desc.SetMetaData(TEXT("UnifiedScriptSemanticId"), StateIdText);
            if (bHasCategory && !Category.IsEmpty())
            {
                Desc.SetMetaData(TEXT("Category"), Category);
            }
#endif
            Defaults.Add(MoveTemp(Default));
            SeenStateIds.Add(StateId);
            SeenStateNames.Add(PropertyName);
        }
    }

    if (!Reader.IsAtEnd())
    {
        OutError = TEXT("Compiled declaration contains trailing or truncated bytes.");
        return false;
    }
    if (!bSelected)
    {
        OutError = RequestedAssetPath.IsEmpty()
            ? TEXT("DeclarationAssetPath is required when a chunk contains multiple assets.")
            : FString::Printf(
                TEXT("Compiled declaration does not contain assetPath '%s'."),
                *RequestedAssetPath);
        return false;
    }

    FInstancedPropertyBag DefaultsBag;
    if (!Descs.IsEmpty() &&
        DefaultsBag.AddProperties(Descs) != EPropertyBagAlterationResult::Success)
    {
        OutError = TEXT("Unreal could not create the compiled Script State layout.");
        return false;
    }
    for (int32 Index = 0; Index < Descs.Num(); ++Index)
    {
        if (!SetBinaryDefaultValue(DefaultsBag, Descs[Index], Defaults[Index], OutError))
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
        Candidate.MigrateToNewBagInstanceWithOverrides(DefaultsBag, CompatibleOverrideIds);
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
