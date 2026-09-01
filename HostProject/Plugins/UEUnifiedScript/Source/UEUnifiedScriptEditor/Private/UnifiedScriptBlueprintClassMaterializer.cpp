#include "UnifiedScriptBlueprintClassMaterializer.h"

#include "Dom/JsonObject.h"
#include "EdGraph/EdGraph.h"
#include "EdGraph/EdGraphPin.h"
#include "EdGraphSchema_K2.h"
#include "Engine/Blueprint.h"
#include "Interfaces/IPluginManager.h"
#include "K2Node_FunctionEntry.h"
#include "K2Node_FunctionResult.h"
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
const FName ManagedFunctionKey(TEXT("UnifiedScript.ManagedFunction"));
const FName FunctionIdKey(TEXT("UnifiedScript.FunctionId"));
const FName ManagedFunctionNameKey(TEXT("UnifiedScript.ManagedName"));
const FName WorldContextObjectName(TEXT("WorldContextObject"));

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

struct FFunctionParameterDeclaration
{
    FGuid Id;
    FName Name;
    FString TypeMetadataName;
    FEdGraphPinType PinType;
};

struct FFunctionDeclaration
{
    FGuid Id;
    FString SemanticId;
    FName ManagedName;
    FName InternalName;
    FString ReturnTypeMetadataName;
    FEdGraphPinType ReturnPinType;
    bool bHasReturnValue = false;
    bool bIsStatic = false;
    TArray<FFunctionParameterDeclaration> Parameters;
};

struct FPlannedFunction
{
    FFunctionDeclaration Declaration;
    bool bExisting = false;
    bool bNeedsUpdate = false;
};

struct FSyncPlan
{
    TArray<FPlannedState> States;
    TArray<FName> StaleGeneratedVariables;
    TArray<FPlannedFunction> Functions;
    TArray<FGuid> StaleGeneratedFunctionIds;
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

bool ResolveFunctionPinType(
    const FString& MetadataName,
    bool bAllowVoid,
    FEdGraphPinType& OutType,
    bool& bOutHasValue)
{
    bOutHasValue = MetadataName != TEXT("System.Void");
    if (!bOutHasValue)
    {
        OutType = FEdGraphPinType();
        return bAllowVoid;
    }
    FString TypeKind;
    if (MetadataName == TEXT("System.Boolean"))
    {
        TypeKind = TEXT("boolean");
    }
    else if (MetadataName == TEXT("System.Byte") ||
             MetadataName == TEXT("System.Int32") ||
             MetadataName == TEXT("System.Int64"))
    {
        TypeKind = TEXT("integer");
    }
    else if (MetadataName == TEXT("System.Single") ||
             MetadataName == TEXT("System.Double"))
    {
        TypeKind = TEXT("floatingPoint");
    }
    else if (MetadataName == TEXT("System.String"))
    {
        TypeKind = TEXT("string");
    }
    else if (MetadataName == TEXT("UnifiedScript.Unreal.UnrealObjectHandle"))
    {
        OutType = FEdGraphPinType();
        OutType.PinCategory = UEdGraphSchema_K2::PC_Object;
        OutType.PinSubCategoryObject = UObject::StaticClass();
        return true;
    }
    else if (MetadataName == TEXT("UnifiedScript.Unreal.UnrealVector"))
    {
        OutType = FEdGraphPinType();
        OutType.PinCategory = UEdGraphSchema_K2::PC_Struct;
        OutType.PinSubCategoryObject = TBaseStructure<FVector>::Get();
        return true;
    }
    else
    {
        return false;
    }
    return ResolvePinType(TypeKind, MetadataName, OutType);
}

FEdGraphPinType MakeWorldContextPinType()
{
    FEdGraphPinType Result;
    Result.PinCategory = UEdGraphSchema_K2::PC_Object;
    Result.PinSubCategoryObject = UObject::StaticClass();
    return Result;
}

FName MakeManagedFunctionInternalName(const FGuid& Id)
{
    return FName(*FString::Printf(
        TEXT("USF_%s"),
        *Id.ToString(EGuidFormats::Digits).ToLower()));
}

bool IsManagedFunctionGraph(const UEdGraph& Graph)
{
    const FKismetUserDeclaredFunctionMetadata* Metadata =
        FBlueprintEditorUtils::GetGraphFunctionMetaData(&Graph);
    return Metadata != nullptr && Metadata->HasMetaData(ManagedFunctionKey) &&
        Metadata->GetMetaData(ManagedFunctionKey) == TEXT("true");
}

bool TryReadManagedFunctionGraphId(const UEdGraph& Graph, FGuid& OutId)
{
    const FKismetUserDeclaredFunctionMetadata* Metadata =
        FBlueprintEditorUtils::GetGraphFunctionMetaData(&Graph);
    return Metadata != nullptr && Metadata->HasMetaData(FunctionIdKey) &&
        ParseSemanticId(Metadata->GetMetaData(FunctionIdKey), OutId);
}

UEdGraph* FindManagedFunctionGraphById(UBlueprint& Blueprint, const FGuid& Id)
{
    for (UEdGraph* Graph : Blueprint.FunctionGraphs)
    {
        FGuid CandidateId;
        if (Graph != nullptr && IsManagedFunctionGraph(*Graph) &&
            TryReadManagedFunctionGraphId(*Graph, CandidateId) && CandidateId == Id)
        {
            return Graph;
        }
    }
    return nullptr;
}

bool FunctionGraphSignatureMatches(
    const UEdGraph& Graph,
    const FFunctionDeclaration& Declaration)
{
    if (Graph.GetFName() != Declaration.InternalName ||
        Graph.GraphGuid != Declaration.Id)
    {
        return false;
    }
    TArray<UK2Node_FunctionEntry*> EntryNodes;
    TArray<UK2Node_FunctionResult*> ResultNodes;
    Graph.GetNodesOfClass(EntryNodes);
    Graph.GetNodesOfClass(ResultNodes);
    const int32 ParameterOffset = Declaration.bIsStatic ? 1 : 0;
    if (EntryNodes.Num() != 1 || ResultNodes.Num() != 1 ||
        EntryNodes[0]->UserDefinedPins.Num() !=
            Declaration.Parameters.Num() + ParameterOffset ||
        ResultNodes[0]->UserDefinedPins.Num() !=
            (Declaration.bHasReturnValue ? 1 : 0))
    {
        return false;
    }
    if (Declaration.bIsStatic)
    {
        const TSharedPtr<FUserPinInfo>& WorldContextPin =
            EntryNodes[0]->UserDefinedPins[0];
        if (!WorldContextPin.IsValid() ||
            WorldContextPin->PinName != WorldContextObjectName ||
            WorldContextPin->PinType != MakeWorldContextPinType() ||
            WorldContextPin->DesiredPinDirection != EGPD_Output)
        {
            return false;
        }
    }
    for (int32 Index = 0; Index < Declaration.Parameters.Num(); ++Index)
    {
        const TSharedPtr<FUserPinInfo>& Pin =
            EntryNodes[0]->UserDefinedPins[Index + ParameterOffset];
        const FFunctionParameterDeclaration& Parameter = Declaration.Parameters[Index];
        if (!Pin.IsValid() || Pin->PinName != Parameter.Name ||
            Pin->PinType != Parameter.PinType ||
            Pin->DesiredPinDirection != EGPD_Output)
        {
            return false;
        }
    }
    if (Declaration.bHasReturnValue)
    {
        const TSharedPtr<FUserPinInfo>& Pin = ResultNodes[0]->UserDefinedPins[0];
        if (!Pin.IsValid() || Pin->PinName != UEdGraphSchema_K2::PN_ReturnValue ||
            Pin->PinType != Declaration.ReturnPinType ||
            Pin->DesiredPinDirection != EGPD_Input)
        {
            return false;
        }
    }
    return true;
}

bool FunctionGraphMatches(
    const UEdGraph& Graph,
    const FFunctionDeclaration& Declaration)
{
    if (!FunctionGraphSignatureMatches(Graph, Declaration))
    {
        return false;
    }
    const FKismetUserDeclaredFunctionMetadata* Metadata =
        FBlueprintEditorUtils::GetGraphFunctionMetaData(&Graph);
    return Metadata != nullptr && Metadata->HasMetaData(ManagedFunctionKey) &&
        Metadata->GetMetaData(ManagedFunctionKey) == TEXT("true") &&
        Metadata->HasMetaData(FunctionIdKey) &&
        Metadata->GetMetaData(FunctionIdKey) == Declaration.SemanticId &&
        Metadata->HasMetaData(ManagedFunctionNameKey) &&
        Metadata->GetMetaData(ManagedFunctionNameKey) ==
            Declaration.ManagedName.ToString() &&
        Metadata->HasMetaData(TEXT("DisplayName")) &&
        Metadata->GetMetaData(TEXT("DisplayName")) ==
            Declaration.ManagedName.ToString() &&
        (!Declaration.bIsStatic ||
         (Metadata->HasMetaData(TEXT("WorldContext")) &&
          Metadata->GetMetaData(TEXT("WorldContext")) ==
              WorldContextObjectName.ToString()));
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
    TArray<FFunctionDeclaration>& OutFunctions,
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

    OutFunctions.Reset();
    const bool bStaticLibrary =
        ScriptAsset.ScriptClassKind == EUnifiedScriptClassKind::BlueprintFunctionLibrary;
    if (ScriptAsset.ScriptClassKind != EUnifiedScriptClassKind::Actor &&
        ScriptAsset.ScriptClassKind != EUnifiedScriptClassKind::ActorComponent &&
        !bStaticLibrary)
    {
        return true;
    }

    TSet<FGuid> SeenFunctionIds;
    TSet<FName> SeenFunctionNames;
    OutFunctions.Reserve(Functions->Num());
    for (const TSharedPtr<FJsonValue>& FunctionValue : *Functions)
    {
        const TSharedPtr<FJsonObject> Function = FunctionValue.IsValid()
            ? FunctionValue->AsObject()
            : nullptr;
        FString SemanticId;
        FString Name;
        FString ReturnType;
        bool bIsStatic = false;
        bool bIsPublic = false;
        bool bIsEvent = false;
        const TArray<TSharedPtr<FJsonValue>>* Parameters = nullptr;
        if (!ReadRequiredString(Function, TEXT("id"), SemanticId, OutError) ||
            !ReadRequiredString(Function, TEXT("name"), Name, OutError) ||
            !ReadRequiredString(
                Function,
                TEXT("returnTypeMetadataName"),
                ReturnType,
                OutError) ||
            !Function->TryGetBoolField(TEXT("isStatic"), bIsStatic) ||
            !Function->TryGetBoolField(TEXT("isPublic"), bIsPublic) ||
            !Function->TryGetBoolField(TEXT("isEvent"), bIsEvent) ||
            !Function->TryGetArrayField(TEXT("parameters"), Parameters) ||
            Parameters == nullptr)
        {
            if (OutError.IsEmpty())
            {
                OutError = FString::Printf(
                    TEXT("Function '%s' requires isStatic, isPublic, isEvent, and parameters. Rebuild its Declaration IR with the current SDK."),
                    *Name);
            }
            return false;
        }
        if (!bIsPublic || bIsEvent)
        {
            continue;
        }
        if (bIsStatic != bStaticLibrary)
        {
            OutError = bStaticLibrary
                ? FString::Printf(
                    TEXT("Function '%s' is not static. BlueprintFunctionLibrary Script Classes own only static managed methods."),
                    *Name)
                : FString::Printf(
                    TEXT("Function '%s' is static. Actor and ActorComponent Script Classes expose instance managed methods; create a BlueprintFunctionLibrary Script Class for static calls."),
                    *Name);
            return false;
        }
        if (Parameters->Num() > 4)
        {
            OutError = FString::Printf(
                TEXT("Function '%s' has %d parameters; the fixed managed function ABI v1 supports at most four."),
                *Name,
                Parameters->Num());
            return false;
        }

        FFunctionDeclaration Declaration;
        Declaration.SemanticId = SemanticId;
        Declaration.ManagedName = FName(*Name);
        Declaration.ReturnTypeMetadataName = ReturnType;
        Declaration.bIsStatic = bIsStatic;
        if (!ParseSemanticId(SemanticId, Declaration.Id) ||
            Declaration.ManagedName.IsNone() ||
            SeenFunctionIds.Contains(Declaration.Id) ||
            SeenFunctionNames.Contains(Declaration.ManagedName))
        {
            OutError = FString::Printf(
                TEXT("Script Class declaration has duplicate, overloaded, or invalid callable function '%s' (%s). Blueprint-callable managed overloads require distinct names."),
                *Name,
                *SemanticId);
            return false;
        }
        Declaration.InternalName = MakeManagedFunctionInternalName(Declaration.Id);
        if (!ResolveFunctionPinType(
                ReturnType,
                true,
                Declaration.ReturnPinType,
                Declaration.bHasReturnValue))
        {
            OutError = FString::Printf(
                TEXT("Function '%s' has unsupported return type '%s'. Managed function ABI v2 supports void, bool, byte, int, long, float, double, string, ObjectHandle, and FVector."),
                *Name,
                *ReturnType);
            return false;
        }

        TSet<FGuid> SeenParameterIds;
        TSet<FName> SeenParameterNames;
        Declaration.Parameters.Reserve(Parameters->Num());
        for (const TSharedPtr<FJsonValue>& ParameterValue : *Parameters)
        {
            const TSharedPtr<FJsonObject> Parameter = ParameterValue.IsValid()
                ? ParameterValue->AsObject()
                : nullptr;
            FString ParameterId;
            FString ParameterName;
            FString ParameterType;
            if (!ReadRequiredString(Parameter, TEXT("id"), ParameterId, OutError) ||
                !ReadRequiredString(Parameter, TEXT("name"), ParameterName, OutError) ||
                !ReadRequiredString(
                    Parameter,
                    TEXT("typeMetadataName"),
                    ParameterType,
                    OutError))
            {
                return false;
            }
            FFunctionParameterDeclaration ParameterDeclaration;
            ParameterDeclaration.Name = FName(*ParameterName);
            ParameterDeclaration.TypeMetadataName = ParameterType;
            bool bHasParameterValue = false;
            if (!ParseSemanticId(ParameterId, ParameterDeclaration.Id) ||
                ParameterDeclaration.Name.IsNone() ||
                SeenParameterIds.Contains(ParameterDeclaration.Id) ||
                SeenParameterNames.Contains(ParameterDeclaration.Name) ||
                !ResolveFunctionPinType(
                    ParameterType,
                    false,
                    ParameterDeclaration.PinType,
                    bHasParameterValue) ||
                !bHasParameterValue)
            {
                OutError = FString::Printf(
                    TEXT("Function '%s' has duplicate, invalid, or unsupported parameter '%s' of type '%s'. Managed function ABI v2 supports bool, byte, int, long, float, double, string, ObjectHandle, and FVector."),
                    *Name,
                    *ParameterName,
                    *ParameterType);
                return false;
            }
            SeenParameterIds.Add(ParameterDeclaration.Id);
            SeenParameterNames.Add(ParameterDeclaration.Name);
            Declaration.Parameters.Add(MoveTemp(ParameterDeclaration));
        }

        SeenFunctionIds.Add(Declaration.Id);
        SeenFunctionNames.Add(Declaration.ManagedName);
        OutFunctions.Add(MoveTemp(Declaration));
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

bool BuildFunctionPlan(
    const UBlueprint& Blueprint,
    TConstArrayView<FFunctionDeclaration> Declarations,
    FSyncPlan& OutPlan,
    FUnifiedScriptBlueprintClassSyncResult& OutResult,
    FString& OutError)
{
    TSet<FGuid> DesiredIds;
    for (const FFunctionDeclaration& Declaration : Declarations)
    {
        DesiredIds.Add(Declaration.Id);
    }

    TMap<FGuid, UEdGraph*> ExistingById;
    for (UEdGraph* Graph : Blueprint.FunctionGraphs)
    {
        if (Graph == nullptr || !IsManagedFunctionGraph(*Graph))
        {
            continue;
        }
        FGuid Id;
        if (!TryReadManagedFunctionGraphId(*Graph, Id) || ExistingById.Contains(Id))
        {
            OutError = FString::Printf(
                TEXT("Generated Blueprint function graph '%s' has duplicate or invalid UnifiedScript.FunctionId metadata."),
                *GetPathNameSafe(Graph));
            return false;
        }
        ExistingById.Add(Id, Graph);
        if (!DesiredIds.Contains(Id))
        {
            OutPlan.StaleGeneratedFunctionIds.Add(Id);
        }
    }

    for (const FFunctionDeclaration& Declaration : Declarations)
    {
        UEdGraph* const* Existing = ExistingById.Find(Declaration.Id);
        const TObjectPtr<UEdGraph>* NameOwnerEntry = Blueprint.FunctionGraphs.FindByPredicate(
            [&Declaration](const UEdGraph* Graph)
            {
                return Graph != nullptr && Graph->GetFName() == Declaration.InternalName;
            });
        UEdGraph* NameOwner = NameOwnerEntry != nullptr
            ? NameOwnerEntry->Get()
            : nullptr;
        if (NameOwner != nullptr &&
            (Existing == nullptr || NameOwner != *Existing) &&
            !IsManagedFunctionGraph(*NameOwner))
        {
            OutError = FString::Printf(
                TEXT("Managed function '%s' collides with user-owned Blueprint graph '%s'."),
                *Declaration.ManagedName.ToString(),
                *NameOwner->GetName());
            return false;
        }
        if (Blueprint.ParentClass != nullptr &&
            Blueprint.ParentClass->FindFunctionByName(
                Declaration.InternalName,
                EIncludeSuperFlag::IncludeSuper) != nullptr)
        {
            OutError = FString::Printf(
                TEXT("Managed function '%s' collides with parent function '%s'."),
                *Declaration.ManagedName.ToString(),
                *Declaration.InternalName.ToString());
            return false;
        }

        FPlannedFunction& Planned = OutPlan.Functions.AddDefaulted_GetRef();
        Planned.Declaration = Declaration;
        Planned.bExisting = Existing != nullptr;
        Planned.bNeedsUpdate = Existing == nullptr ||
            !FunctionGraphMatches(**Existing, Declaration);
        if (!Planned.bExisting)
        {
            ++OutResult.AddedFunctionCount;
        }
        else if (Planned.bNeedsUpdate)
        {
            ++OutResult.UpdatedFunctionCount;
        }
        OutResult.bChanged |= Planned.bNeedsUpdate;
    }

    OutResult.DeclaredFunctionCount = Declarations.Num();
    OutResult.RemovedFunctionCount = OutPlan.StaleGeneratedFunctionIds.Num();
    OutResult.bChanged |= !OutPlan.StaleGeneratedFunctionIds.IsEmpty();
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

bool ConfigureFunctionGraph(
    UBlueprint& Blueprint,
    const FFunctionDeclaration& Declaration,
    UEdGraph*& InOutGraph,
    FString& OutError)
{
    const bool bCreated = InOutGraph == nullptr;
    if (InOutGraph == nullptr)
    {
        InOutGraph = FBlueprintEditorUtils::CreateNewGraph(
            &Blueprint,
            Declaration.InternalName,
            UEdGraph::StaticClass(),
            UEdGraphSchema_K2::StaticClass());
        if (InOutGraph == nullptr)
        {
            OutError = FString::Printf(
                TEXT("Unreal could not create generated Blueprint function '%s'."),
                *Declaration.ManagedName.ToString());
            return false;
        }
        FBlueprintEditorUtils::AddFunctionGraph<UFunction>(
            &Blueprint,
            InOutGraph,
            true,
            nullptr);
    }

    InOutGraph->Modify();
    InOutGraph->GraphGuid = Declaration.Id;
    TArray<UK2Node_FunctionEntry*> EntryNodes;
    TArray<UK2Node_FunctionResult*> ResultNodes;
    InOutGraph->GetNodesOfClass(EntryNodes);
    InOutGraph->GetNodesOfClass(ResultNodes);
    if (EntryNodes.Num() == 1 && ResultNodes.IsEmpty())
    {
        if (UK2Node_FunctionResult* CreatedResult =
                FBlueprintEditorUtils::FindOrCreateFunctionResultNode(EntryNodes[0]))
        {
            ResultNodes.Add(CreatedResult);
        }
    }
    if (EntryNodes.Num() != 1 || ResultNodes.Num() != 1)
    {
        OutError = FString::Printf(
            TEXT("Generated Blueprint function '%s' requires one entry and one result node."),
            *Declaration.ManagedName.ToString());
        return false;
    }

    UK2Node_FunctionEntry* Entry = EntryNodes[0];
    UK2Node_FunctionResult* Result = ResultNodes[0];
    Entry->Modify();
    Result->Modify();
    Entry->CustomGeneratedFunctionName = Declaration.InternalName;
    Entry->SetExtraFlags(
        FUNC_Public | FUNC_BlueprintCallable |
        (Declaration.bIsStatic ? FUNC_Static : FUNC_None));
    Entry->MetaData.Category = FText::FromString(DefaultCategory);
    Entry->MetaData.ToolTip = FText::FromString(FString::Printf(
        TEXT("Managed Script Class method %s"),
        *Declaration.ManagedName.ToString()));
    Entry->MetaData.SetMetaData(ManagedFunctionKey, FString(TEXT("true")));
    Entry->MetaData.SetMetaData(FunctionIdKey, FString(Declaration.SemanticId));
    Entry->MetaData.SetMetaData(
        ManagedFunctionNameKey,
        FString(Declaration.ManagedName.ToString()));
    Entry->MetaData.SetMetaData(
        TEXT("DisplayName"),
        FString(Declaration.ManagedName.ToString()));
    if (Declaration.bIsStatic)
    {
        Entry->MetaData.SetMetaData(
            TEXT("WorldContext"),
            FString(WorldContextObjectName.ToString()));
        Entry->MetaData.SetMetaData(
            TEXT("DefaultToSelf"),
            FString(WorldContextObjectName.ToString()));
    }
    else
    {
        Entry->MetaData.RemoveMetaData(TEXT("WorldContext"));
        Entry->MetaData.RemoveMetaData(TEXT("DefaultToSelf"));
    }

    if (!bCreated && FunctionGraphSignatureMatches(*InOutGraph, Declaration))
    {
        return true;
    }

    const TArray<TSharedPtr<FUserPinInfo>> PreviousEntryPins = Entry->UserDefinedPins;
    for (const TSharedPtr<FUserPinInfo>& Pin : PreviousEntryPins)
    {
        Entry->RemoveUserDefinedPin(Pin);
    }
    const TArray<TSharedPtr<FUserPinInfo>> PreviousResultPins = Result->UserDefinedPins;
    for (const TSharedPtr<FUserPinInfo>& Pin : PreviousResultPins)
    {
        Result->RemoveUserDefinedPin(Pin);
    }
    if (Declaration.bIsStatic &&
        Entry->CreateUserDefinedPin(
            WorldContextObjectName,
            MakeWorldContextPinType(),
            EGPD_Output,
            false) == nullptr)
    {
        OutError = FString::Printf(
            TEXT("Unreal could not create WorldContextObject for managed static function '%s'."),
            *Declaration.ManagedName.ToString());
        return false;
    }
    for (const FFunctionParameterDeclaration& Parameter : Declaration.Parameters)
    {
        if (Entry->CreateUserDefinedPin(
                Parameter.Name,
                Parameter.PinType,
                EGPD_Output,
                false) == nullptr)
        {
            OutError = FString::Printf(
                TEXT("Unreal could not create parameter '%s' for managed function '%s'."),
                *Parameter.Name.ToString(),
                *Declaration.ManagedName.ToString());
            return false;
        }
    }
    if (Declaration.bHasReturnValue &&
        Result->CreateUserDefinedPin(
            UEdGraphSchema_K2::PN_ReturnValue,
            Declaration.ReturnPinType,
            EGPD_Input,
            false) == nullptr)
    {
        OutError = FString::Printf(
            TEXT("Unreal could not create the return pin for managed function '%s'."),
            *Declaration.ManagedName.ToString());
        return false;
    }
    Entry->ReconstructNode();
    Result->ReconstructNode();
    Entry->BreakAllNodeLinks();
    Result->BreakAllNodeLinks();
    UEdGraphPin* ThenPin = Entry->FindPin(UEdGraphSchema_K2::PN_Then, EGPD_Output);
    UEdGraphPin* ExecutePin = Result->FindPin(UEdGraphSchema_K2::PN_Execute, EGPD_Input);
    const UEdGraphSchema_K2* Schema = GetDefault<UEdGraphSchema_K2>();
    if (ThenPin == nullptr || ExecutePin == nullptr ||
        !Schema->TryCreateConnection(ThenPin, ExecutePin))
    {
        OutError = FString::Printf(
            TEXT("Unreal could not connect generated function '%s' entry to its result."),
            *Declaration.ManagedName.ToString());
        return false;
    }
    return true;
}

bool ApplyPlan(UBlueprint& Blueprint, const FSyncPlan& Plan, FString& OutError)
{
    Blueprint.Modify();
    for (const FName& StaleName : Plan.StaleGeneratedVariables)
    {
        FBlueprintEditorUtils::RemoveMemberVariable(&Blueprint, StaleName);
    }
    for (const FGuid& StaleFunctionId : Plan.StaleGeneratedFunctionIds)
    {
        UEdGraph* StaleGraph = FindManagedFunctionGraphById(
            Blueprint,
            StaleFunctionId);
        if (StaleGraph != nullptr)
        {
            FBlueprintEditorUtils::RemoveGraph(
                &Blueprint,
                StaleGraph,
                EGraphRemoveFlags::MarkTransient);
        }
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

    for (const FPlannedFunction& Planned : Plan.Functions)
    {
        if (!Planned.bNeedsUpdate)
        {
            continue;
        }
        UEdGraph* Graph = Planned.bExisting
            ? FindManagedFunctionGraphById(Blueprint, Planned.Declaration.Id)
            : nullptr;
        if (Planned.bExisting && Graph == nullptr)
        {
            OutError = FString::Printf(
                TEXT("Generated Blueprint function '%s' disappeared before materialization."),
                *Planned.Declaration.ManagedName.ToString());
            return false;
        }
        if (!ConfigureFunctionGraph(
                Blueprint,
                Planned.Declaration,
                Graph,
                OutError))
        {
            return false;
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
    UUnifiedScriptBlueprintGeneratedClass* GeneratedClass =
        Cast<UUnifiedScriptBlueprintGeneratedClass>(Blueprint.GeneratedClass);
    if (!Plan.Functions.IsEmpty() && GeneratedClass == nullptr)
    {
        if (Blueprint.GetOuter() == GetTransientPackage())
        {
            // StaticDuplicateObject uses the stock generated-class type for the
            // disposable preflight copy. Its graph compile is the required
            // validation; persistent binding is applied to the exact proxy.
            return true;
        }
        OutError = FString::Printf(
            TEXT("Generated Blueprint '%s' predates the managed-function class carrier. Recreate or migrate the Script Class proxy before exposing managed functions."),
            *Blueprint.GetPathName());
        return false;
    }
    if (GeneratedClass != nullptr)
    {
        GeneratedClass->Modify();
        const UUnifiedScriptBlueprint* UnifiedBlueprint =
            Cast<UUnifiedScriptBlueprint>(&Blueprint);
        GeneratedClass->ManagedScriptAsset = UnifiedBlueprint != nullptr
            ? UnifiedBlueprint->ScriptAsset
            : nullptr;
        GeneratedClass->ManagedFunctionBindings.Reset();
        for (const FPlannedFunction& Planned : Plan.Functions)
        {
            FUnifiedScriptManagedFunctionBinding Binding;
            Binding.ManagedName = Planned.Declaration.ManagedName.ToString();
            Binding.FunctionId = Planned.Declaration.Id;
            Binding.AbiVersion = 2;
            Binding.bStatic = Planned.Declaration.bIsStatic;
            GeneratedClass->ManagedFunctionBindings.Add(
                Planned.Declaration.InternalName,
                MoveTemp(Binding));
        }
        GeneratedClass->BindManagedFunctions();
        for (const FPlannedFunction& Planned : Plan.Functions)
        {
            if (!GeneratedClass->IsManagedFunctionBound(
                    Planned.Declaration.InternalName))
            {
                OutError = FString::Printf(
                    TEXT("Generated Blueprint function '%s' did not bind its native managed dispatcher."),
                    *Planned.Declaration.ManagedName.ToString());
                return false;
            }
        }
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

    TArray<FStateDeclaration> StateDeclarations;
    TArray<FFunctionDeclaration> FunctionDeclarations;
    if (!ParseDeclaration(
            ScriptAsset,
            DeclarationJson,
            StateDeclarations,
            FunctionDeclarations,
            OutError))
    {
        return false;
    }

    FSyncPlan Plan;
    if (!BuildPlan(Blueprint, StateDeclarations, Plan, OutResult, OutError) ||
        !BuildFunctionPlan(
            Blueprint,
            FunctionDeclarations,
            Plan,
            OutResult,
            OutError) ||
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
