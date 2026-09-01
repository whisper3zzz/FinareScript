#include "UnifiedScriptBlueprintClassMaterializer.h"

#include "Dom/JsonObject.h"
#include "EdGraphSchema_K2.h"
#include "Engine/Blueprint.h"
#include "Interfaces/IPluginManager.h"
#include "Kismet2/BlueprintEditorUtils.h"
#include "Kismet2/KismetEditorUtilities.h"
#include "Misc/FileHelper.h"
#include "Misc/PackageName.h"
#include "Misc/Paths.h"
#include "Serialization/JsonReader.h"
#include "Serialization/JsonSerializer.h"
#include "UnifiedScriptAsset.h"
#include "UnifiedScriptBlueprint.h"
#include "UObject/Package.h"
#include "UObject/SavePackage.h"
#include "UObject/UObjectGlobals.h"
#include "UObject/UnrealType.h"

namespace UE::UnifiedScript::BlueprintClass::Private
{
constexpr TCHAR SchemaVersion[] = TEXT("unifiedscript.script-asset/v0.1");
constexpr TCHAR DefaultCategory[] = TEXT("UnifiedScript");

const FName ManagedStateKey(TEXT("UnifiedScript.ManagedState"));
const FName StateIdKey(TEXT("UnifiedScript.StateId"));
const FName TypeMetadataNameKey(TEXT("UnifiedScript.TypeMetadataName"));
const FName DeclarationDefaultKey(TEXT("UnifiedScript.DeclarationDefault"));

struct FStateDeclaration
{
    FGuid Id;
    FString SemanticId;
    FName Name;
    FString TypeMetadataName;
    FEdGraphPinType PinType;
    FString DefaultValue;
    FString Category;
    uint64 PropertyFlags = 0;
};

struct FPlannedState
{
    FStateDeclaration Declaration;
    bool bExisting = false;
    FName ExistingName;
    FString AppliedDefault;
    bool bPreserveDefaultOverride = false;
};

struct FSyncPlan
{
    TArray<FPlannedState> States;
    TArray<FName> StaleGeneratedVariables;
};

bool ParseSemanticId(const FString& SemanticId, FGuid& OutId)
{
    FString Digits = SemanticId;
    return Digits.RemoveFromStart(TEXT("us_")) &&
        Digits.Len() == 32 &&
        FGuid::ParseExact(Digits, EGuidFormats::Digits, OutId);
}

bool IsManagedState(const FBPVariableDescription& Variable)
{
    return Variable.HasMetaData(ManagedStateKey) &&
        Variable.GetMetaData(ManagedStateKey) == TEXT("true");
}

int32 FindVariableByGuid(const UBlueprint& Blueprint, const FGuid& Id)
{
    return Blueprint.NewVariables.IndexOfByPredicate(
        [&Id](const FBPVariableDescription& Variable)
        {
            return Variable.VarGuid == Id;
        });
}

int32 FindVariableByStateId(const UBlueprint& Blueprint, const FString& SemanticId)
{
    return Blueprint.NewVariables.IndexOfByPredicate(
        [&SemanticId](const FBPVariableDescription& Variable)
        {
            return IsManagedState(Variable) &&
                Variable.HasMetaData(StateIdKey) &&
                Variable.GetMetaData(StateIdKey) == SemanticId;
        });
}

bool ResolvePinType(
    const FString& TypeKind,
    const FString& MetadataName,
    FEdGraphPinType& OutType)
{
    OutType = FEdGraphPinType();
    if (TypeKind == TEXT("boolean") && MetadataName == TEXT("System.Boolean"))
    {
        OutType.PinCategory = UEdGraphSchema_K2::PC_Boolean;
        return true;
    }
    if (TypeKind == TEXT("integer"))
    {
        if (MetadataName == TEXT("System.Byte"))
        {
            OutType.PinCategory = UEdGraphSchema_K2::PC_Byte;
            return true;
        }
        if (MetadataName == TEXT("System.Int32"))
        {
            OutType.PinCategory = UEdGraphSchema_K2::PC_Int;
            return true;
        }
        if (MetadataName == TEXT("System.Int64"))
        {
            OutType.PinCategory = UEdGraphSchema_K2::PC_Int64;
            return true;
        }
        return false;
    }
    if (TypeKind == TEXT("floatingPoint"))
    {
        OutType.PinCategory = UEdGraphSchema_K2::PC_Real;
        if (MetadataName == TEXT("System.Single"))
        {
            OutType.PinSubCategory = UEdGraphSchema_K2::PC_Float;
            return true;
        }
        if (MetadataName == TEXT("System.Double"))
        {
            OutType.PinSubCategory = UEdGraphSchema_K2::PC_Double;
            return true;
        }
        return false;
    }
    if (TypeKind == TEXT("string") && MetadataName == TEXT("System.String"))
    {
        OutType.PinCategory = UEdGraphSchema_K2::PC_String;
        return true;
    }
    return false;
}

bool ReadCanonicalDefault(
    const FString& StateName,
    const FString& MetadataName,
    const TSharedPtr<FJsonValue>& Value,
    FString& OutDefault,
    FString& OutError)
{
    if (!Value.IsValid() || Value->IsNull())
    {
        OutError = FString::Printf(
            TEXT("State '%s' requires a non-null default for '%s'."),
            *StateName,
            *MetadataName);
        return false;
    }

    if (MetadataName == TEXT("System.Boolean") && Value->Type == EJson::Boolean)
    {
        OutDefault = Value->AsBool() ? TEXT("true") : TEXT("false");
        return true;
    }
    if (MetadataName == TEXT("System.String") && Value->Type == EJson::String)
    {
        OutDefault = Value->AsString();
        return true;
    }
    if (Value->Type != EJson::Number)
    {
        OutError = FString::Printf(
            TEXT("State '%s' has a default incompatible with '%s'."),
            *StateName,
            *MetadataName);
        return false;
    }

    const double Number = Value->AsNumber();
    if (!FMath::IsFinite(Number))
    {
        OutError = FString::Printf(TEXT("State '%s' has a non-finite default."), *StateName);
        return false;
    }
    if (MetadataName == TEXT("System.Byte"))
    {
        if (Number < 0.0 || Number > MAX_uint8 || FMath::TruncToDouble(Number) != Number)
        {
            OutError = FString::Printf(TEXT("State '%s' default is outside System.Byte."), *StateName);
            return false;
        }
        OutDefault = LexToString(static_cast<uint8>(Number));
        return true;
    }
    if (MetadataName == TEXT("System.Int32"))
    {
        if (Number < MIN_int32 || Number > MAX_int32 || FMath::TruncToDouble(Number) != Number)
        {
            OutError = FString::Printf(TEXT("State '%s' default is outside System.Int32."), *StateName);
            return false;
        }
        OutDefault = LexToString(static_cast<int32>(Number));
        return true;
    }
    if (MetadataName == TEXT("System.Int64"))
    {
        if (Number < static_cast<double>(MIN_int64) ||
            Number > static_cast<double>(MAX_int64) ||
            FMath::TruncToDouble(Number) != Number)
        {
            OutError = FString::Printf(TEXT("State '%s' default is outside System.Int64."), *StateName);
            return false;
        }
        OutDefault = LexToString(static_cast<int64>(Number));
        return true;
    }
    if (MetadataName == TEXT("System.Single"))
    {
        const float Converted = static_cast<float>(Number);
        if (!FMath::IsFinite(Converted))
        {
            OutError = FString::Printf(TEXT("State '%s' default is outside System.Single."), *StateName);
            return false;
        }
        OutDefault = LexToString(Converted);
        return true;
    }
    if (MetadataName == TEXT("System.Double"))
    {
        OutDefault = LexToString(Number);
        return true;
    }

    OutError = FString::Printf(
        TEXT("State '%s' uses unsupported Blueprint state type '%s'."),
        *StateName,
        *MetadataName);
    return false;
}

bool ReadRequiredString(
    const TSharedPtr<FJsonObject>& Object,
    const TCHAR* Field,
    FString& OutValue,
    FString& OutError)
{
    if (!Object.IsValid() || !Object->TryGetStringField(Field, OutValue))
    {
        OutError = FString::Printf(TEXT("Declaration requires string field '%s'."), Field);
        return false;
    }
    OutValue.TrimStartAndEndInline();
    if (OutValue.IsEmpty())
    {
        OutError = FString::Printf(TEXT("Declaration field '%s' cannot be empty."), Field);
        return false;
    }
    return true;
}

bool ParseDeclaration(
    UUnifiedScriptAsset& ScriptAsset,
    const FString& DeclarationJson,
    TArray<FStateDeclaration>& OutStates,
    FString& OutError)
{
    TSharedPtr<FJsonObject> Root;
    const TSharedRef<TJsonReader<>> Reader = TJsonReaderFactory<>::Create(DeclarationJson);
    if (!FJsonSerializer::Deserialize(Reader, Root) || !Root.IsValid())
    {
        OutError = FString::Printf(
            TEXT("Script Class declaration JSON is invalid: %s"),
            *Reader->GetErrorMessage());
        return false;
    }

    FString ParsedSchema;
    const TArray<TSharedPtr<FJsonValue>>* Assets = nullptr;
    if (!Root->TryGetStringField(TEXT("schemaVersion"), ParsedSchema) ||
        ParsedSchema != SchemaVersion ||
        !Root->TryGetArrayField(TEXT("assets"), Assets) || Assets == nullptr ||
        Assets->IsEmpty())
    {
        OutError = FString::Printf(
            TEXT("Script Class declaration requires schema '%s' and a non-empty assets array."),
            SchemaVersion);
        return false;
    }

    FString RequestedAssetPath = ScriptAsset.DeclarationAssetPath;
    RequestedAssetPath.TrimStartAndEndInline();
    TSharedPtr<FJsonObject> SelectedAsset;
    for (const TSharedPtr<FJsonValue>& AssetValue : *Assets)
    {
        const TSharedPtr<FJsonObject> Candidate = AssetValue.IsValid()
            ? AssetValue->AsObject()
            : nullptr;
        FString CandidatePath;
        if (!Candidate.IsValid() ||
            !Candidate->TryGetStringField(TEXT("assetPath"), CandidatePath))
        {
            OutError = TEXT("Script Class declaration contains an asset without assetPath.");
            return false;
        }
        if ((RequestedAssetPath.IsEmpty() && Assets->Num() == 1) ||
            CandidatePath == RequestedAssetPath)
        {
            SelectedAsset = Candidate;
            break;
        }
    }
    if (!SelectedAsset.IsValid())
    {
        OutError = FString::Printf(
            TEXT("Script Class declaration does not contain assetPath '%s'."),
            *RequestedAssetPath);
        return false;
    }

    FString AssetIdText;
    FString QualifiedName;
    FGuid AssetId;
    if (!ReadRequiredString(SelectedAsset, TEXT("id"), AssetIdText, OutError) ||
        !ParseSemanticId(AssetIdText, AssetId) ||
        !ReadRequiredString(SelectedAsset, TEXT("qualifiedName"), QualifiedName, OutError))
    {
        if (OutError.IsEmpty())
        {
            OutError = TEXT("Script Class declaration has an invalid class identity.");
        }
        return false;
    }
    if (!ScriptAsset.ValidateDeclarationIdentity(AssetId, QualifiedName, OutError))
    {
        return false;
    }

    const TArray<TSharedPtr<FJsonValue>>* States = nullptr;
    const TArray<TSharedPtr<FJsonValue>>* Functions = nullptr;
    if (!SelectedAsset->TryGetArrayField(TEXT("states"), States) || States == nullptr ||
        !SelectedAsset->TryGetArrayField(TEXT("functions"), Functions) || Functions == nullptr)
    {
        OutError = TEXT("Script Class declaration requires states and functions arrays.");
        return false;
    }
    if (ScriptAsset.ScriptClassKind == EUnifiedScriptClassKind::BlueprintFunctionLibrary &&
        !States->IsEmpty())
    {
        OutError = TEXT("BlueprintFunctionLibrary Script Classes cannot declare instance state.");
        return false;
    }

    TSet<FGuid> SeenIds;
    TSet<FName> SeenNames;
    OutStates.Reset();
    OutStates.Reserve(States->Num());
    for (const TSharedPtr<FJsonValue>& StateValue : *States)
    {
        const TSharedPtr<FJsonObject> State = StateValue.IsValid()
            ? StateValue->AsObject()
            : nullptr;
        FString SemanticId;
        FString Name;
        FString TypeKind;
        FString MetadataName;
        if (!ReadRequiredString(State, TEXT("id"), SemanticId, OutError) ||
            !ReadRequiredString(State, TEXT("name"), Name, OutError) ||
            !ReadRequiredString(State, TEXT("typeKind"), TypeKind, OutError) ||
            !ReadRequiredString(State, TEXT("typeMetadataName"), MetadataName, OutError))
        {
            return false;
        }

        FStateDeclaration Declaration;
        Declaration.SemanticId = SemanticId;
        Declaration.Name = FName(*Name);
        Declaration.TypeMetadataName = MetadataName;
        if (!ParseSemanticId(SemanticId, Declaration.Id) ||
            Declaration.Name.IsNone() || SeenIds.Contains(Declaration.Id) ||
            SeenNames.Contains(Declaration.Name))
        {
            OutError = FString::Printf(
                TEXT("Script Class declaration has duplicate or invalid state '%s' (%s)."),
                *Name,
                *SemanticId);
            return false;
        }
        if (!ResolvePinType(TypeKind, MetadataName, Declaration.PinType))
        {
            OutError = FString::Printf(
                TEXT("State '%s' uses unsupported Blueprint state type '%s' (%s). Reviewed types are bool, byte, int, long, float, double, and non-null string."),
                *Name,
                *TypeKind,
                *MetadataName);
            return false;
        }
        if (!ReadCanonicalDefault(
                Name,
                MetadataName,
                State->TryGetField(TEXT("defaultValue")),
                Declaration.DefaultValue,
                OutError))
        {
            return false;
        }

        const TSharedPtr<FJsonObject>* Exposure = nullptr;
        bool bInstanceEditable = false;
        bool bSaveGame = false;
        bool bReplicated = false;
        if (!State->TryGetObjectField(TEXT("exposure"), Exposure) || Exposure == nullptr ||
            !(*Exposure)->TryGetBoolField(TEXT("instanceEditable"), bInstanceEditable) ||
            !(*Exposure)->TryGetBoolField(TEXT("saveGame"), bSaveGame) ||
            !(*Exposure)->TryGetBoolField(TEXT("replicated"), bReplicated))
        {
            OutError = FString::Printf(
                TEXT("State '%s' requires complete exposure metadata."),
                *Name);
            return false;
        }
        const TSharedPtr<FJsonValue> CategoryValue = (*Exposure)->TryGetField(TEXT("category"));
        if (CategoryValue.IsValid() && !CategoryValue->IsNull())
        {
            if (CategoryValue->Type != EJson::String)
            {
                OutError = FString::Printf(TEXT("State '%s' category must be a string or null."), *Name);
                return false;
            }
            Declaration.Category = CategoryValue->AsString();
            Declaration.Category.TrimStartAndEndInline();
            if (Declaration.Category.IsEmpty())
            {
                OutError = FString::Printf(TEXT("State '%s' category cannot be blank."), *Name);
                return false;
            }
        }
        if (Declaration.Category.IsEmpty())
        {
            Declaration.Category = DefaultCategory;
        }

        Declaration.PropertyFlags = CPF_Edit | CPF_BlueprintVisible | CPF_DisableEditOnInstance;
        if (bInstanceEditable)
        {
            Declaration.PropertyFlags &= ~static_cast<uint64>(CPF_DisableEditOnInstance);
        }
        if (bSaveGame)
        {
            Declaration.PropertyFlags |= CPF_SaveGame;
        }
        if (bReplicated)
        {
            Declaration.PropertyFlags |= CPF_Net;
        }

        SeenIds.Add(Declaration.Id);
        SeenNames.Add(Declaration.Name);
        OutStates.Add(MoveTemp(Declaration));
    }
    return true;
}

bool CanonicalCurrentDefault(
    const UBlueprint& Blueprint,
    const FBPVariableDescription& Variable,
    FString& OutValue,
    FString& OutError)
{
    const UClass* GeneratedClass = Blueprint.GeneratedClass;
    const UObject* Cdo = GeneratedClass != nullptr
        ? GeneratedClass->GetDefaultObject(false)
        : nullptr;
    const FProperty* Property = GeneratedClass != nullptr
        ? FindFProperty<FProperty>(GeneratedClass, Variable.VarName)
        : nullptr;
    if (Cdo == nullptr || Property == nullptr)
    {
        OutValue = Variable.DefaultValue;
        return true;
    }

    if (const FBoolProperty* Typed = CastField<FBoolProperty>(Property))
    {
        OutValue = Typed->GetPropertyValue_InContainer(Cdo) ? TEXT("true") : TEXT("false");
        return true;
    }
    if (const FByteProperty* Typed = CastField<FByteProperty>(Property))
    {
        OutValue = LexToString(Typed->GetPropertyValue_InContainer(Cdo));
        return true;
    }
    if (const FIntProperty* Typed = CastField<FIntProperty>(Property))
    {
        OutValue = LexToString(Typed->GetPropertyValue_InContainer(Cdo));
        return true;
    }
    if (const FInt64Property* Typed = CastField<FInt64Property>(Property))
    {
        OutValue = LexToString(Typed->GetPropertyValue_InContainer(Cdo));
        return true;
    }
    if (const FFloatProperty* Typed = CastField<FFloatProperty>(Property))
    {
        OutValue = LexToString(Typed->GetPropertyValue_InContainer(Cdo));
        return true;
    }
    if (const FDoubleProperty* Typed = CastField<FDoubleProperty>(Property))
    {
        OutValue = LexToString(Typed->GetPropertyValue_InContainer(Cdo));
        return true;
    }
    if (const FStrProperty* Typed = CastField<FStrProperty>(Property))
    {
        OutValue = Typed->GetPropertyValue_InContainer(Cdo);
        return true;
    }

    OutError = FString::Printf(
        TEXT("Generated state '%s' has unsupported reflected property '%s'."),
        *Variable.VarName.ToString(),
        *Property->GetClass()->GetName());
    return false;
}

bool HasMetadataValue(
    const FBPVariableDescription& Variable,
    const FName& Key,
    const FString& Value)
{
    return Variable.HasMetaData(Key) && Variable.GetMetaData(Key) == Value;
}

bool BuildPlan(
    const UBlueprint& Blueprint,
    TConstArrayView<FStateDeclaration> Declarations,
    FSyncPlan& OutPlan,
    FUnifiedScriptBlueprintClassSyncResult& OutResult,
    FString& OutError)
{
    TMap<FGuid, int32> GeneratedById;
    TMap<FString, int32> GeneratedBySemanticId;
    TSet<FGuid> DesiredIds;
    for (const FStateDeclaration& Declaration : Declarations)
    {
        DesiredIds.Add(Declaration.Id);
    }

    for (int32 Index = 0; Index < Blueprint.NewVariables.Num(); ++Index)
    {
        const FBPVariableDescription& Variable = Blueprint.NewVariables[Index];
        if (!IsManagedState(Variable))
        {
            continue;
        }
        if (!Variable.HasMetaData(StateIdKey))
        {
            OutError = FString::Printf(
                TEXT("Generated Blueprint variable '%s' is missing UnifiedScript.StateId metadata."),
                *Variable.VarName.ToString());
            return false;
        }
        const FString SemanticId = Variable.GetMetaData(StateIdKey);
        FGuid StateId;
        if (!ParseSemanticId(SemanticId, StateId) ||
            GeneratedById.Contains(StateId) ||
            GeneratedBySemanticId.Contains(SemanticId))
        {
            OutError = FString::Printf(
                TEXT("Generated Blueprint variable '%s' has duplicate or invalid state identity '%s'."),
                *Variable.VarName.ToString(),
                *SemanticId);
            return false;
        }
        GeneratedById.Add(StateId, Index);
        GeneratedBySemanticId.Add(SemanticId, Index);
        if (!DesiredIds.Contains(StateId))
        {
            OutPlan.StaleGeneratedVariables.Add(Variable.VarName);
        }
    }

    for (const FStateDeclaration& Declaration : Declarations)
    {
        int32 ExistingIndex = FindVariableByGuid(Blueprint, Declaration.Id);
        const int32 MetadataIndex = FindVariableByStateId(Blueprint, Declaration.SemanticId);
        if (ExistingIndex != INDEX_NONE &&
            !IsManagedState(Blueprint.NewVariables[ExistingIndex]))
        {
            OutError = FString::Printf(
                TEXT("State '%s' semantic GUID collides with user Blueprint variable '%s'."),
                *Declaration.Name.ToString(),
                *Blueprint.NewVariables[ExistingIndex].VarName.ToString());
            return false;
        }
        if (ExistingIndex != INDEX_NONE && MetadataIndex != INDEX_NONE &&
            ExistingIndex != MetadataIndex)
        {
            OutError = FString::Printf(
                TEXT("State '%s' has conflicting Blueprint GUID and metadata owners."),
                *Declaration.Name.ToString());
            return false;
        }
        if (ExistingIndex == INDEX_NONE)
        {
            ExistingIndex = MetadataIndex;
        }

        const int32 NameIndex = Blueprint.NewVariables.IndexOfByPredicate(
            [&Declaration](const FBPVariableDescription& Variable)
            {
                return Variable.VarName == Declaration.Name;
            });
        if (NameIndex != INDEX_NONE && NameIndex != ExistingIndex)
        {
            const FBPVariableDescription& Owner = Blueprint.NewVariables[NameIndex];
            FGuid OwnerStateId;
            const bool bGeneratedOwner = IsManagedState(Owner) &&
                Owner.HasMetaData(StateIdKey) &&
                ParseSemanticId(Owner.GetMetaData(StateIdKey), OwnerStateId);
            if (!bGeneratedOwner || DesiredIds.Contains(OwnerStateId))
            {
                OutError = FString::Printf(
                    TEXT("State '%s' collides with Blueprint variable '%s' owned by a different identity."),
                    *Declaration.Name.ToString(),
                    *Owner.VarName.ToString());
                return false;
            }
        }
        if (FindFProperty<FProperty>(Blueprint.ParentClass, Declaration.Name) != nullptr)
        {
            OutError = FString::Printf(
                TEXT("State '%s' masks a property declared by parent class '%s'."),
                *Declaration.Name.ToString(),
                *GetPathNameSafe(Blueprint.ParentClass));
            return false;
        }

        FPlannedState& Planned = OutPlan.States.AddDefaulted_GetRef();
        Planned.Declaration = Declaration;
        Planned.AppliedDefault = Declaration.DefaultValue;
        if (ExistingIndex == INDEX_NONE)
        {
            ++OutResult.AddedPropertyCount;
            OutResult.bChanged = true;
            continue;
        }

        Planned.bExisting = true;
        const FBPVariableDescription& Existing = Blueprint.NewVariables[ExistingIndex];
        Planned.ExistingName = Existing.VarName;
        if (!Existing.HasMetaData(DeclarationDefaultKey))
        {
            OutError = FString::Printf(
                TEXT("Generated Blueprint variable '%s' is missing its previous declaration default."),
                *Existing.VarName.ToString());
            return false;
        }
        FString CurrentDefault;
        if (!CanonicalCurrentDefault(Blueprint, Existing, CurrentDefault, OutError))
        {
            return false;
        }
        const FString PreviousDeclarationDefault = Existing.GetMetaData(DeclarationDefaultKey);
        Planned.bPreserveDefaultOverride = CurrentDefault != PreviousDeclarationDefault;
        if (Planned.bPreserveDefaultOverride)
        {
            if (Existing.VarType != Declaration.PinType)
            {
                OutError = FString::Printf(
                    TEXT("State '%s' changes type while its Blueprint default is overridden. Reset the override or migrate it explicitly."),
                    *Declaration.Name.ToString());
                return false;
            }
            Planned.AppliedDefault = CurrentDefault;
            ++OutResult.PreservedDefaultOverrideCount;
        }

        const bool bRename = Existing.VarName != Declaration.Name;
        if (bRename)
        {
            ++OutResult.RenamedPropertyCount;
            OutResult.bChanged = true;
        }
        const FString FriendlyName = FName::NameToDisplayString(
            Declaration.Name.ToString(),
            Declaration.PinType.PinCategory == UEdGraphSchema_K2::PC_Boolean);
        const bool bGuidChanged = Existing.VarGuid != Declaration.Id;
        const bool bTypeChanged = Existing.VarType != Declaration.PinType;
        const bool bFriendlyNameChanged = Existing.FriendlyName != FriendlyName;
        const bool bCategoryChanged = Existing.Category.ToString() != Declaration.Category;
        const bool bFlagsChanged = Existing.PropertyFlags != Declaration.PropertyFlags;
        const bool bDefaultChanged = CurrentDefault != Planned.AppliedDefault;
        const bool bMarkerChanged = !HasMetadataValue(Existing, ManagedStateKey, TEXT("true"));
        const bool bStateIdChanged =
            !HasMetadataValue(Existing, StateIdKey, Declaration.SemanticId);
        const bool bMetadataTypeChanged = !HasMetadataValue(
            Existing,
            TypeMetadataNameKey,
            Declaration.TypeMetadataName);
        const bool bDeclarationDefaultChanged = !HasMetadataValue(
            Existing,
            DeclarationDefaultKey,
            Declaration.DefaultValue);
        const bool bUpdate = bGuidChanged || bTypeChanged || bFriendlyNameChanged ||
            bCategoryChanged || bFlagsChanged || bDefaultChanged || bMarkerChanged ||
            bStateIdChanged || bMetadataTypeChanged || bDeclarationDefaultChanged;
        if (bUpdate)
        {
            ++OutResult.UpdatedPropertyCount;
            OutResult.bChanged = true;
        }
    }

    OutResult.DeclaredStateCount = Declarations.Num();
    OutResult.RemovedPropertyCount = OutPlan.StaleGeneratedVariables.Num();
    OutResult.bChanged |= !OutPlan.StaleGeneratedVariables.IsEmpty();
    return true;
}

FName MakeTemporaryName(const UBlueprint& Blueprint, const FGuid& Id)
{
    FString Base = FString::Printf(
        TEXT("__UnifiedScript_%s"),
        *Id.ToString(EGuidFormats::Digits).Left(12));
    FName Candidate(*Base);
    int32 Suffix = 1;
    while (Blueprint.NewVariables.ContainsByPredicate(
               [&Candidate](const FBPVariableDescription& Variable)
               {
                   return Variable.VarName == Candidate;
               }) ||
           FindFProperty<FProperty>(Blueprint.ParentClass, Candidate) != nullptr)
    {
        Candidate = FName(*FString::Printf(TEXT("%s_%d"), *Base, Suffix++));
    }
    return Candidate;
}

bool ApplyPlan(UBlueprint& Blueprint, const FSyncPlan& Plan, FString& OutError)
{
    Blueprint.Modify();
    for (const FName& StaleName : Plan.StaleGeneratedVariables)
    {
        FBlueprintEditorUtils::RemoveMemberVariable(&Blueprint, StaleName);
    }

    TMap<FGuid, FName> TemporaryNames;
    for (const FPlannedState& Planned : Plan.States)
    {
        if (!Planned.bExisting || Planned.ExistingName == Planned.Declaration.Name)
        {
            continue;
        }
        const int32 ExistingIndex = FindVariableByStateId(
            Blueprint,
            Planned.Declaration.SemanticId);
        if (ExistingIndex == INDEX_NONE)
        {
            OutError = FString::Printf(
                TEXT("Cannot find generated state '%s' during rename."),
                *Planned.Declaration.SemanticId);
            return false;
        }
        const FName TemporaryName = MakeTemporaryName(Blueprint, Planned.Declaration.Id);
        FBlueprintEditorUtils::RenameMemberVariable(
            &Blueprint,
            Blueprint.NewVariables[ExistingIndex].VarName,
            TemporaryName);
        TemporaryNames.Add(Planned.Declaration.Id, TemporaryName);
    }

    for (const FPlannedState& Planned : Plan.States)
    {
        if (Planned.bExisting)
        {
            continue;
        }
        if (!FBlueprintEditorUtils::AddMemberVariable(
                &Blueprint,
                Planned.Declaration.Name,
                Planned.Declaration.PinType,
                Planned.AppliedDefault))
        {
            OutError = FString::Printf(
                TEXT("Unreal could not add generated Blueprint property '%s'."),
                *Planned.Declaration.Name.ToString());
            return false;
        }
        const int32 AddedIndex = FBlueprintEditorUtils::FindNewVariableIndex(
            &Blueprint,
            Planned.Declaration.Name);
        if (AddedIndex == INDEX_NONE)
        {
            OutError = FString::Printf(
                TEXT("Generated Blueprint property '%s' disappeared after creation."),
                *Planned.Declaration.Name.ToString());
            return false;
        }
        Blueprint.NewVariables[AddedIndex].VarGuid = Planned.Declaration.Id;
    }

    for (const FPlannedState& Planned : Plan.States)
    {
        if (const FName* TemporaryName = TemporaryNames.Find(Planned.Declaration.Id))
        {
            FBlueprintEditorUtils::RenameMemberVariable(
                &Blueprint,
                *TemporaryName,
                Planned.Declaration.Name);
        }

        int32 VariableIndex = FindVariableByGuid(Blueprint, Planned.Declaration.Id);
        if (VariableIndex == INDEX_NONE)
        {
            VariableIndex = FindVariableByStateId(
                Blueprint,
                Planned.Declaration.SemanticId);
        }
        if (VariableIndex == INDEX_NONE)
        {
            OutError = FString::Printf(
                TEXT("Generated Blueprint property '%s' cannot be finalized."),
                *Planned.Declaration.Name.ToString());
            return false;
        }

        FBPVariableDescription& Variable = Blueprint.NewVariables[VariableIndex];
        Variable.VarName = Planned.Declaration.Name;
        Variable.VarGuid = Planned.Declaration.Id;
        Variable.VarType = Planned.Declaration.PinType;
        Variable.FriendlyName = FName::NameToDisplayString(
            Planned.Declaration.Name.ToString(),
            Planned.Declaration.PinType.PinCategory == UEdGraphSchema_K2::PC_Boolean);
        Variable.Category = FText::FromString(Planned.Declaration.Category);
        Variable.PropertyFlags = Planned.Declaration.PropertyFlags;
        Variable.RepNotifyFunc = NAME_None;
        Variable.ReplicationCondition = COND_None;
        Variable.DefaultValue = Planned.AppliedDefault;
        Variable.SetMetaData(ManagedStateKey, TEXT("true"));
        Variable.SetMetaData(StateIdKey, Planned.Declaration.SemanticId);
        Variable.SetMetaData(
            TypeMetadataNameKey,
            Planned.Declaration.TypeMetadataName);
        Variable.SetMetaData(
            DeclarationDefaultKey,
            Planned.Declaration.DefaultValue);
        if (Planned.Declaration.PinType.PinCategory == UEdGraphSchema_K2::PC_String)
        {
            Variable.SetMetaData(TEXT("MultiLine"), TEXT("true"));
        }
        else
        {
            Variable.RemoveMetaData(TEXT("MultiLine"));
        }
    }

    FBlueprintEditorUtils::MarkBlueprintAsStructurallyModified(&Blueprint);
    FKismetEditorUtilities::CompileBlueprint(&Blueprint);
    if (Blueprint.Status == BS_Error)
    {
        OutError = FString::Printf(
            TEXT("Generated Blueprint '%s' failed to compile after state materialization."),
            *Blueprint.GetPathName());
        return false;
    }
    return true;
}

FString ResolveConfiguredPath(const FString& ConfiguredPath)
{
    FString Result = ConfiguredPath;
    Result.TrimStartAndEndInline();
    Result.ReplaceInline(TEXT("{ProjectDir}"), *FPaths::ProjectDir(), ESearchCase::IgnoreCase);
    if (const TSharedPtr<IPlugin> Plugin =
            IPluginManager::Get().FindPlugin(TEXT("UEUnifiedScript")))
    {
        Result.ReplaceInline(
            TEXT("{PluginDir}"),
            *Plugin->GetBaseDir(),
            ESearchCase::IgnoreCase);
    }
    Result = FPaths::ConvertRelativePathToFull(Result);
    FPaths::NormalizeFilename(Result);
    return Result;
}

bool SaveBlueprint(UBlueprint& Blueprint, FString& OutError)
{
    UPackage* Package = Blueprint.GetOutermost();
    if (Package == nullptr || Package == GetTransientPackage() ||
        Package->GetName().StartsWith(TEXT("/Engine/Transient")))
    {
        return true;
    }

    const FString PackageName = Package->GetName();
    const FString Filename = FPackageName::LongPackageNameToFilename(
        PackageName,
        FPackageName::GetAssetPackageExtension());
    IFileManager::Get().MakeDirectory(*FPaths::GetPath(Filename), true);
    FSavePackageArgs SaveArguments;
    SaveArguments.TopLevelFlags = RF_Public | RF_Standalone;
    SaveArguments.SaveFlags = SAVE_NoError;
    if (!UPackage::SavePackage(Package, &Blueprint, *Filename, SaveArguments))
    {
        OutError = FString::Printf(
            TEXT("Failed to save generated Blueprint '%s' to '%s'."),
            *Blueprint.GetPathName(),
            *Filename);
        return false;
    }
    return true;
}
} // namespace UE::UnifiedScript::BlueprintClass::Private

bool FUnifiedScriptBlueprintClassMaterializer::Synchronize(
    UUnifiedScriptAsset& ScriptAsset,
    bool bValidateOnly,
    FUnifiedScriptBlueprintClassSyncResult& OutResult,
    FString& OutError)
{
    using namespace UE::UnifiedScript::BlueprintClass::Private;

    if (ScriptAsset.ScriptClassKind == EUnifiedScriptClassKind::None ||
        !ScriptAsset.ScriptClassId.IsValid() ||
        !ScriptAsset.GeneratedClassPath.IsValid())
    {
        OutError = FString::Printf(
            TEXT("UnifiedScript Asset '%s' does not have complete Script Class identity."),
            *ScriptAsset.GetPathName());
        return false;
    }

    UClass* GeneratedClass = ScriptAsset.GeneratedClassPath.TryLoadClass<UObject>();
    UUnifiedScriptBlueprint* Blueprint = GeneratedClass != nullptr
        ? Cast<UUnifiedScriptBlueprint>(GeneratedClass->ClassGeneratedBy)
        : nullptr;
    if (Blueprint == nullptr || Blueprint->GeneratedClass != GeneratedClass ||
        Blueprint->ScriptAsset != &ScriptAsset)
    {
        OutError = FString::Printf(
            TEXT("UnifiedScript Asset '%s' does not resolve its exact generated Blueprint '%s' and sidecar."),
            *ScriptAsset.GetPathName(),
            *ScriptAsset.GeneratedClassPath.ToString());
        return false;
    }

    FString DeclarationPath;
    FString DeclarationAssetPath;
    ScriptAsset.ResolveDeclarationConfiguration(DeclarationPath, DeclarationAssetPath);
    if (DeclarationPath.IsEmpty() || DeclarationAssetPath.IsEmpty())
    {
        OutError = FString::Printf(
            TEXT("UnifiedScript Class '%s' has incomplete declaration configuration."),
            *ScriptAsset.GetPathName());
        return false;
    }
    const FString ResolvedPath = ResolveConfiguredPath(DeclarationPath);
    FString DeclarationJson;
    if (!FFileHelper::LoadFileToString(DeclarationJson, *ResolvedPath))
    {
        OutError = FString::Printf(
            TEXT("Cannot read Script Class declaration '%s'."),
            *ResolvedPath);
        return false;
    }
    return SynchronizeJson(
        ScriptAsset,
        *Blueprint,
        DeclarationJson,
        bValidateOnly,
        OutResult,
        OutError);
}

bool FUnifiedScriptBlueprintClassMaterializer::SynchronizeJson(
    UUnifiedScriptAsset& ScriptAsset,
    UBlueprint& Blueprint,
    const FString& DeclarationJson,
    bool bValidateOnly,
    FUnifiedScriptBlueprintClassSyncResult& OutResult,
    FString& OutError)
{
    using namespace UE::UnifiedScript::BlueprintClass::Private;

    OutResult = FUnifiedScriptBlueprintClassSyncResult();
    OutError.Reset();
    const UUnifiedScriptBlueprint* UnifiedBlueprint = Cast<UUnifiedScriptBlueprint>(&Blueprint);
    if (UnifiedBlueprint == nullptr || UnifiedBlueprint->ScriptAsset != &ScriptAsset ||
        Blueprint.GeneratedClass == nullptr ||
        ScriptAsset.GeneratedClassPath.ToString() != Blueprint.GeneratedClass->GetPathName())
    {
        OutError = FString::Printf(
            TEXT("Blueprint '%s' is not the exact generated proxy for '%s'."),
            *Blueprint.GetPathName(),
            *ScriptAsset.GetPathName());
        return false;
    }

    TArray<FStateDeclaration> Declarations;
    if (!ParseDeclaration(ScriptAsset, DeclarationJson, Declarations, OutError))
    {
        return false;
    }

    FSyncPlan Plan;
    if (!BuildPlan(Blueprint, Declarations, Plan, OutResult, OutError) ||
        bValidateOnly || !OutResult.bChanged)
    {
        return OutError.IsEmpty();
    }

    UBlueprint* ValidationBlueprint = Cast<UBlueprint>(StaticDuplicateObject(
        &Blueprint,
        GetTransientPackage(),
        MakeUniqueObjectName(
            GetTransientPackage(),
            Blueprint.GetClass(),
            TEXT("UnifiedScriptMaterializationPreflight"))));
    if (ValidationBlueprint == nullptr ||
        !ApplyPlan(*ValidationBlueprint, Plan, OutError))
    {
        if (OutError.IsEmpty())
        {
            OutError = TEXT("Could not create a transactional Blueprint materialization preflight.");
        }
        return false;
    }

    const TArray<FBPVariableDescription> OriginalVariables = Blueprint.NewVariables;
    if (!ApplyPlan(Blueprint, Plan, OutError))
    {
        Blueprint.NewVariables = OriginalVariables;
        FBlueprintEditorUtils::MarkBlueprintAsStructurallyModified(&Blueprint);
        FKismetEditorUtilities::CompileBlueprint(&Blueprint);
        return false;
    }
    if (!SaveBlueprint(Blueprint, OutError))
    {
        return false;
    }
    return true;
}
