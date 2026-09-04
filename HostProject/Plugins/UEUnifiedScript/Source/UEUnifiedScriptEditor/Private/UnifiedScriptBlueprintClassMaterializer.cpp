#include "UnifiedScriptBlueprintClassMaterializer.h"

#include "Dom/JsonObject.h"
#include "EdGraph/EdGraph.h"
#include "EdGraph/EdGraphPin.h"
#include "EdGraphSchema_K2.h"
#include "Engine/Blueprint.h"
#include "GameFramework/Actor.h"
#include "Components/ActorComponent.h"
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
const FName BlueprintEventKey(TEXT("UnifiedScript.BlueprintEvent"));
const FName BlueprintEventSuperModeKey(TEXT("UnifiedScript.SuperMode"));
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
    FName RepNotifyFunc;
    ELifetimeCondition ReplicationCondition = COND_None;
};

struct FPlannedState
{
    FStateDeclaration Declaration;
    bool bExisting = false;
    FName ExistingName;
    FString AppliedDefault;
    bool bPreserveDefaultOverride = false;
};

enum class EFunctionValueTypeKind : uint8
{
    None,
    Enum,
    Struct,
    Array,
    Set,
    Map,
};

struct FFunctionValueTypeDeclaration
{
    EFunctionValueTypeKind Kind = EFunctionValueTypeKind::None;
    FString UnrealTypePath;
    FString UnrealNativeName;
    TArray<TArray<FName>> StructFieldPaths;
    TArray<FString> ArgumentMetadataNames;
    TArray<FFunctionValueTypeDeclaration> TypeArguments;
};

struct FFunctionParameterDeclaration
{
    FGuid Id;
    FName Name;
    FString TypeMetadataName;
    FEdGraphPinType PinType;
    FFunctionValueTypeDeclaration ValueType;
    EUnifiedScriptManagedParameterDirection Direction =
        EUnifiedScriptManagedParameterDirection::Input;
};

struct FFunctionDeclaration
{
    FGuid Id;
    FString SemanticId;
    FName ManagedName;
    FName InternalName;
    FString ReturnTypeMetadataName;
    FEdGraphPinType ReturnPinType;
    FFunctionValueTypeDeclaration ReturnValueType;
    bool bHasReturnValue = false;
    bool bIsStatic = false;
    bool bBlueprintEvent = false;
    bool bInterfaceFunction = false;
    EUnifiedScriptRpcTarget RpcTarget = EUnifiedScriptRpcTarget::None;
    bool bReliable = false;
    FName RepNotifyProperty;
    TArray<FString> InterfacePaths;
    EUnifiedScriptBlueprintEventSuperMode BlueprintEventSuperMode =
        EUnifiedScriptBlueprintEventSuperMode::Explicit;
    TArray<FFunctionParameterDeclaration> Parameters;
};

struct FLifecycleDeclaration
{
    EUnifiedScriptManagedLifecycleHookKind Kind =
        EUnifiedScriptManagedLifecycleHookKind::BeginPlay;
    FName ManagedName;
    FGuid Id;
};

struct FInputActionDeclaration
{
    FName ManagedName;
    FGuid BindingId;
    FString ActionPath;
    EUnifiedScriptManagedInputTriggerEvent TriggerEvent =
        EUnifiedScriptManagedInputTriggerEvent::Triggered;
    EUnifiedScriptManagedInputValueType ValueType =
        EUnifiedScriptManagedInputValueType::Boolean;
    bool bConsumeInput = false;
};

struct FDelegateBindingDeclaration
{
    FName ManagedName;
    FGuid BindingId;
    FString SourceTypePath;
    FString EventPath;
    EUnifiedScriptManagedDelegateSource Source =
        EUnifiedScriptManagedDelegateSource::Target;
    FString SourceMember;
};

struct FInterfaceFunctionDeclaration
{
    FString InterfacePath;
    FName FunctionName;
    FName ManagedName;
    FGuid BindingId;
    FGuid FunctionId;
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
    TMap<EUnifiedScriptManagedLifecycleHookKind, FUnifiedScriptManagedLifecycleBinding>
        LifecycleBindings;
    TArray<FUnifiedScriptManagedInputActionBinding> InputBindings;
    TArray<FUnifiedScriptManagedDelegateBinding> DelegateBindings;
    TArray<FUnifiedScriptManagedInterfaceFunctionBinding> InterfaceBindings;
    TArray<FString> InterfacePathsToRemove;
    TArray<FString> InterfacePathsToAdd;
    bool bLifecycleBindingsChanged = false;
    bool bGameplayBindingsChanged = false;
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
    FEdGraphPinType& OutType,
    const FFunctionValueTypeDeclaration* UnrealType = nullptr)
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
    if (TypeKind == TEXT("name") &&
        MetadataName == TEXT("UnifiedScript.Unreal.UnrealName"))
    {
        OutType.PinCategory = UEdGraphSchema_K2::PC_Name;
        return true;
    }
    if (TypeKind == TEXT("text") &&
        MetadataName == TEXT("UnifiedScript.Unreal.UnrealText"))
    {
        OutType.PinCategory = UEdGraphSchema_K2::PC_Text;
        return true;
    }
    if (TypeKind == TEXT("object") &&
        MetadataName == TEXT("UnifiedScript.Unreal.UnrealObjectHandle"))
    {
        OutType.PinCategory = UEdGraphSchema_K2::PC_Object;
        OutType.PinSubCategoryObject = UObject::StaticClass();
        return true;
    }
    if (TypeKind == TEXT("class") &&
        MetadataName == TEXT("UnifiedScript.Unreal.UnrealClassHandle"))
    {
        OutType.PinCategory = UEdGraphSchema_K2::PC_Class;
        OutType.PinSubCategoryObject = UObject::StaticClass();
        return true;
    }
    if (TypeKind == TEXT("struct") && UnrealType != nullptr &&
        UnrealType->Kind == EFunctionValueTypeKind::Struct &&
        MetadataName == TEXT("UnifiedScript.Unreal.UnrealVector") &&
        UnrealType->UnrealTypePath == TEXT("/Script/CoreUObject.Vector"))
    {
        UScriptStruct* Struct = LoadObject<UScriptStruct>(
            nullptr,
            *UnrealType->UnrealTypePath);
        if (Struct == nullptr || Struct->GetName() != UnrealType->UnrealNativeName)
        {
            return false;
        }
        OutType.PinCategory = UEdGraphSchema_K2::PC_Struct;
        OutType.PinSubCategoryObject = Struct;
        return true;
    }
    return false;
}

bool ResolveFunctionPinType(
    const FString& MetadataName,
    const FFunctionValueTypeDeclaration& ValueType,
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
    if (ValueType.Kind == EFunctionValueTypeKind::Array ||
        ValueType.Kind == EFunctionValueTypeKind::Set ||
        ValueType.Kind == EFunctionValueTypeKind::Map)
    {
        const bool bMap = ValueType.Kind == EFunctionValueTypeKind::Map;
        const FString Definition = bMap ? TEXT("System.Collections.Generic.IReadOnlyDictionary") :
            ValueType.Kind == EFunctionValueTypeKind::Array
                ? TEXT("System.Collections.Generic.IReadOnlyList")
                : TEXT("System.Collections.Generic.IReadOnlySet");
        if (ValueType.TypeArguments.Num() != (bMap ? 2 : 1) ||
            ValueType.ArgumentMetadataNames.Num() != ValueType.TypeArguments.Num() ||
            MetadataName != Definition + TEXT("<") +
                FString::Join(ValueType.ArgumentMetadataNames, TEXT(", ")) + TEXT(">"))
        {
            return false;
        }
        bool bHasElement = false;
        if (!ResolveFunctionPinType(ValueType.ArgumentMetadataNames[0], ValueType.TypeArguments[0],
                false, OutType, bHasElement) || !bHasElement || OutType.IsContainer() ||
            (ValueType.Kind != EFunctionValueTypeKind::Array &&
             !FBlueprintEditorUtils::HasGetTypeHash(OutType)))
        {
            return false;
        }
        OutType.ContainerType = bMap ? EPinContainerType::Map :
            ValueType.Kind == EFunctionValueTypeKind::Array
                ? EPinContainerType::Array : EPinContainerType::Set;
        if (bMap)
        {
            FEdGraphPinType MappedType;
            if (!ResolveFunctionPinType(ValueType.ArgumentMetadataNames[1], ValueType.TypeArguments[1],
                    false, MappedType, bHasElement) || !bHasElement || MappedType.IsContainer())
            {
                return false;
            }
            OutType.PinValueType.TerminalCategory = MappedType.PinCategory;
            OutType.PinValueType.TerminalSubCategory = MappedType.PinSubCategory;
            OutType.PinValueType.TerminalSubCategoryObject = MappedType.PinSubCategoryObject;
        }
        return true;
    }
    if (ValueType.Kind == EFunctionValueTypeKind::Enum)
    {
        UEnum* Enum = LoadObject<UEnum>(nullptr, *ValueType.UnrealTypePath);
        if (Enum == nullptr || Enum->GetName() != ValueType.UnrealNativeName)
        {
            return false;
        }
        OutType = FEdGraphPinType();
        OutType.PinCategory = UEdGraphSchema_K2::PC_Byte;
        OutType.PinSubCategoryObject = Enum;
        return true;
    }
    if (ValueType.Kind == EFunctionValueTypeKind::Struct)
    {
        UScriptStruct* Struct = LoadObject<UScriptStruct>(nullptr, *ValueType.UnrealTypePath);
        if (Struct == nullptr)
        {
            return false;
        }
        for (const TArray<FName>& FieldPath : ValueType.StructFieldPaths)
        {
            const UStruct* Owner = Struct;
            const FProperty* Field = nullptr;
            for (int32 Index = 0; Index < FieldPath.Num(); ++Index)
            {
                if (Owner != nullptr)
                    Field = FindFProperty<FProperty>(Owner, FieldPath[Index]);
                else if (const FArrayProperty* Array = CastField<FArrayProperty>(Field);
                         Array && FieldPath[Index] == FName(TEXT("$element")))
                    Field = Array->Inner;
                else if (const FSetProperty* Set = CastField<FSetProperty>(Field);
                         Set && FieldPath[Index] == FName(TEXT("$element")))
                    Field = Set->ElementProp;
                else if (const FMapProperty* Map = CastField<FMapProperty>(Field))
                    Field = FieldPath[Index] == FName(TEXT("$key")) ? Map->KeyProp :
                        FieldPath[Index] == FName(TEXT("$value")) ? Map->ValueProp : nullptr;
                else
                    Field = nullptr;
                if (Field == nullptr)
                {
                    return false;
                }
                if (Index + 1 < FieldPath.Num())
                {
                    const FStructProperty* Nested = CastField<FStructProperty>(Field);
                    Owner = Nested != nullptr ? Nested->Struct : nullptr;
                }
            }
        }
        OutType = FEdGraphPinType();
        OutType.PinCategory = UEdGraphSchema_K2::PC_Struct;
        OutType.PinSubCategoryObject = Struct;
        return true;
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
    else if (MetadataName == TEXT("UnifiedScript.Unreal.UnrealName"))
    {
        OutType = FEdGraphPinType();
        OutType.PinCategory = UEdGraphSchema_K2::PC_Name;
        return true;
    }
    else if (MetadataName == TEXT("UnifiedScript.Unreal.UnrealText"))
    {
        OutType = FEdGraphPinType();
        OutType.PinCategory = UEdGraphSchema_K2::PC_Text;
        return true;
    }
    else if (MetadataName == TEXT("UnifiedScript.Unreal.UnrealSoftObjectHandle") ||
             MetadataName == TEXT("UnifiedScript.Unreal.UnrealSoftClassHandle"))
    {
        OutType = FEdGraphPinType();
        OutType.PinCategory = MetadataName.EndsWith(TEXT("UnrealSoftClassHandle"))
            ? UEdGraphSchema_K2::PC_SoftClass : UEdGraphSchema_K2::PC_SoftObject;
        OutType.PinSubCategoryObject = UObject::StaticClass();
        return true;
    }
    else if (MetadataName == TEXT("UnifiedScript.Unreal.UnrealObjectHandle"))
    {
        OutType = FEdGraphPinType();
        OutType.PinCategory = UEdGraphSchema_K2::PC_Object;
        OutType.PinSubCategoryObject = UObject::StaticClass();
        return true;
    }
    else if (MetadataName == TEXT("UnifiedScript.Unreal.UnrealClassHandle"))
    {
        OutType = FEdGraphPinType();
        OutType.PinCategory = UEdGraphSchema_K2::PC_Class;
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
    else if (MetadataName == TEXT("UnifiedScript.Unreal.UnrealRotator"))
    {
        OutType = FEdGraphPinType();
        OutType.PinCategory = UEdGraphSchema_K2::PC_Struct;
        OutType.PinSubCategoryObject = TBaseStructure<FRotator>::Get();
        return true;
    }
    else if (MetadataName == TEXT("UnifiedScript.Unreal.UnrealTransform"))
    {
        OutType = FEdGraphPinType();
        OutType.PinCategory = UEdGraphSchema_K2::PC_Struct;
        OutType.PinSubCategoryObject = TBaseStructure<FTransform>::Get();
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

bool HasManagedFunctionGraphMetadata(const UEdGraph& Graph)
{
    const FKismetUserDeclaredFunctionMetadata* Metadata =
        FBlueprintEditorUtils::GetGraphFunctionMetaData(&Graph);
    return Metadata != nullptr && Metadata->HasMetaData(ManagedFunctionKey) &&
        Metadata->GetMetaData(ManagedFunctionKey) == TEXT("true");
}

const FUnifiedScriptManagedFunctionBinding* FindPersistedManagedFunctionBinding(
    const UBlueprint& Blueprint,
    const UEdGraph& Graph)
{
    const UUnifiedScriptBlueprintGeneratedClass* GeneratedClass =
        Cast<UUnifiedScriptBlueprintGeneratedClass>(Blueprint.GeneratedClass);
    const FUnifiedScriptManagedFunctionBinding* Binding = GeneratedClass != nullptr
        ? GeneratedClass->ManagedFunctionBindings.Find(Graph.GetFName())
        : nullptr;
    if (Binding == nullptr || !Binding->FunctionId.IsValid())
    {
        return nullptr;
    }

    // Interface implementation graphs are reconstructed by Kismet during
    // package load, and UE does not retain their entry-node metadata. The
    // product GeneratedClass binding is the durable ownership record for that
    // special graph kind. Ordinary function graphs additionally retain their
    // semantic GraphGuid so a stale binding cannot claim a user graph by name.
    return Binding->bInterfaceFunction || Graph.GraphGuid == Binding->FunctionId
        ? Binding
        : nullptr;
}

bool IsManagedFunctionGraph(
    const UBlueprint& Blueprint,
    const UEdGraph& Graph)
{
    return HasManagedFunctionGraphMetadata(Graph) ||
        FindPersistedManagedFunctionBinding(Blueprint, Graph) != nullptr;
}

bool TryReadManagedFunctionGraphId(
    const UBlueprint& Blueprint,
    const UEdGraph& Graph,
    FGuid& OutId)
{
    const FKismetUserDeclaredFunctionMetadata* Metadata =
        FBlueprintEditorUtils::GetGraphFunctionMetaData(&Graph);
    if (HasManagedFunctionGraphMetadata(Graph))
    {
        return Metadata != nullptr && Metadata->HasMetaData(FunctionIdKey) &&
            ParseSemanticId(Metadata->GetMetaData(FunctionIdKey), OutId);
    }
    if (const FUnifiedScriptManagedFunctionBinding* Binding =
            FindPersistedManagedFunctionBinding(Blueprint, Graph))
    {
        OutId = Binding->FunctionId;
        return true;
    }
    return false;
}

UEdGraph* FindManagedFunctionGraphById(UBlueprint& Blueprint, const FGuid& Id)
{
    TArray<UEdGraph*> Graphs;
    Graphs.Reserve(Blueprint.FunctionGraphs.Num() + Blueprint.ImplementedInterfaces.Num() * 4);
    for (UEdGraph* Graph : Blueprint.FunctionGraphs)
    {
        Graphs.AddUnique(Graph);
    }
    for (const FBPInterfaceDescription& Interface : Blueprint.ImplementedInterfaces)
    {
        for (UEdGraph* Graph : Interface.Graphs)
        {
            Graphs.AddUnique(Graph);
        }
    }
    for (UEdGraph* Graph : Graphs)
    {
        FGuid CandidateId;
        if (Graph != nullptr && IsManagedFunctionGraph(Blueprint, *Graph) &&
            TryReadManagedFunctionGraphId(Blueprint, *Graph, CandidateId) &&
            CandidateId == Id)
        {
            return Graph;
        }
    }
    return nullptr;
}

bool IsInterfaceGraph(const UBlueprint& Blueprint, const UEdGraph* Candidate)
{
    if (Candidate == nullptr)
    {
        return false;
    }
    for (const FBPInterfaceDescription& Interface : Blueprint.ImplementedInterfaces)
    {
        if (Interface.Graphs.Contains(Candidate))
        {
            return true;
        }
    }
    return false;
}

UEdGraph* FindFunctionGraphByName(const UBlueprint& Blueprint, FName Name)
{
    for (UEdGraph* Graph : Blueprint.FunctionGraphs)
    {
        if (Graph != nullptr && Graph->GetFName() == Name)
        {
            return Graph;
        }
    }
    for (const FBPInterfaceDescription& Interface : Blueprint.ImplementedInterfaces)
    {
        for (UEdGraph* Graph : Interface.Graphs)
        {
            if (Graph != nullptr && Graph->GetFName() == Name)
            {
                return Graph;
            }
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
    int32 InputParameterCount = 0;
    int32 OutputParameterCount = 0;
    for (const FFunctionParameterDeclaration& Parameter : Declaration.Parameters)
    {
        if (Parameter.Direction ==
            EUnifiedScriptManagedParameterDirection::Output)
        {
            ++OutputParameterCount;
        }
        else
        {
            ++InputParameterCount;
        }
    }
    if (EntryNodes.Num() != 1 || ResultNodes.Num() != 1 ||
        EntryNodes[0]->UserDefinedPins.Num() !=
            InputParameterCount + ParameterOffset ||
        ResultNodes[0]->UserDefinedPins.Num() !=
            (Declaration.bHasReturnValue ? 1 : 0) + OutputParameterCount)
    {
        return false;
    }
    const int32 RelevantFlags = FUNC_Public | FUNC_BlueprintCallable | FUNC_Static |
        FUNC_Event | FUNC_BlueprintEvent | FUNC_Net | FUNC_NetServer | FUNC_NetClient | FUNC_NetMulticast | FUNC_NetReliable;
    const int32 ExpectedFlags = FUNC_Public | FUNC_BlueprintCallable | UnifiedScriptRpcFlags(Declaration.RpcTarget, Declaration.bReliable) |
        (Declaration.bIsStatic ? FUNC_Static : FUNC_None) |
        (Declaration.bBlueprintEvent
            ? static_cast<int32>(FUNC_Event | FUNC_BlueprintEvent)
            : FUNC_None);
    if ((EntryNodes[0]->GetExtraFlags() & RelevantFlags) != ExpectedFlags)
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
    int32 EntryParameterIndex = ParameterOffset;
    int32 ResultParameterIndex = 0;
    for (const FFunctionParameterDeclaration& Parameter : Declaration.Parameters)
    {
        const bool bOutputOnly = Parameter.Direction ==
            EUnifiedScriptManagedParameterDirection::Output;
        const TSharedPtr<FUserPinInfo>& Pin = bOutputOnly
            ? ResultNodes[0]->UserDefinedPins[ResultParameterIndex++]
            : EntryNodes[0]->UserDefinedPins[EntryParameterIndex++];
        if (!Pin.IsValid() || Pin->PinName != Parameter.Name ||
            Pin->PinType != Parameter.PinType ||
            Pin->DesiredPinDirection != (bOutputOnly ? EGPD_Input : EGPD_Output))
        {
            return false;
        }
    }
    if (Declaration.bHasReturnValue)
    {
        const TSharedPtr<FUserPinInfo>& Pin =
            ResultNodes[0]->UserDefinedPins[OutputParameterCount];
        if (!Pin.IsValid() || Pin->PinName != UEdGraphSchema_K2::PN_ReturnValue ||
            Pin->PinType != Declaration.ReturnPinType ||
            Pin->DesiredPinDirection != EGPD_Input)
        {
            return false;
        }
    }
    return true;
}

bool RuntimeValueTypeBindingMatches(
    const FUnifiedScriptManagedValueTypeBinding& Binding,
    const FFunctionValueTypeDeclaration& Declaration)
{
    if (Binding.UnrealTypePath != Declaration.UnrealTypePath ||
        Binding.StructFields.Num() != Declaration.StructFieldPaths.Num())
    {
        return false;
    }
    for (int32 Index = 0; Index < Binding.StructFields.Num(); ++Index)
    {
        if (Binding.StructFields[Index].Segments !=
            Declaration.StructFieldPaths[Index])
        {
            return false;
        }
    }
    return true;
}

bool RuntimeFunctionBindingMatches(
    const FUnifiedScriptManagedFunctionBinding& Binding,
    const FFunctionDeclaration& Declaration)
{
    if (Binding.ManagedName != Declaration.ManagedName.ToString() ||
        Binding.FunctionId != Declaration.Id || Binding.AbiVersion != 3 ||
        Binding.bStatic != Declaration.bIsStatic ||
        Binding.RpcTarget != Declaration.RpcTarget || Binding.bReliable != Declaration.bReliable ||
        Binding.RepNotifyProperty != Declaration.RepNotifyProperty ||
        Binding.bBlueprintEvent != Declaration.bBlueprintEvent ||
        Binding.bInterfaceFunction != Declaration.bInterfaceFunction ||
        Binding.BlueprintEventSuperMode != Declaration.BlueprintEventSuperMode ||
        Binding.ParameterTypes.Num() != Declaration.Parameters.Num() ||
        Binding.ParameterNames.Num() != Declaration.Parameters.Num() ||
        Binding.ParameterDirections.Num() != Declaration.Parameters.Num() ||
        !RuntimeValueTypeBindingMatches(
            Binding.ReturnType,
            Declaration.ReturnValueType))
    {
        return false;
    }
    for (int32 Index = 0; Index < Binding.ParameterTypes.Num(); ++Index)
    {
        if (Binding.ParameterNames[Index] != Declaration.Parameters[Index].Name ||
            Binding.ParameterDirections[Index] !=
                Declaration.Parameters[Index].Direction ||
            !RuntimeValueTypeBindingMatches(
                Binding.ParameterTypes[Index],
                Declaration.Parameters[Index].ValueType))
        {
            return false;
        }
    }
    return true;
}

bool FunctionGraphMatches(
    const UBlueprint& Blueprint,
    const UEdGraph& Graph,
    const FFunctionDeclaration& Declaration)
{
    if (!FunctionGraphSignatureMatches(Graph, Declaration))
    {
        return false;
    }
    const FKismetUserDeclaredFunctionMetadata* Metadata =
        FBlueprintEditorUtils::GetGraphFunctionMetaData(&Graph);
    const bool bMetadataMatches =
        Metadata != nullptr && Metadata->HasMetaData(ManagedFunctionKey) &&
        Metadata->GetMetaData(ManagedFunctionKey) == TEXT("true") &&
        Metadata->HasMetaData(FunctionIdKey) &&
        Metadata->GetMetaData(FunctionIdKey) == Declaration.SemanticId &&
        Metadata->HasMetaData(ManagedFunctionNameKey) &&
        Metadata->GetMetaData(ManagedFunctionNameKey) ==
            Declaration.ManagedName.ToString() &&
        Metadata->HasMetaData(TEXT("DisplayName")) &&
        Metadata->GetMetaData(TEXT("DisplayName")) ==
            Declaration.ManagedName.ToString() &&
        Metadata->HasMetaData(BlueprintEventKey) &&
        Metadata->GetMetaData(BlueprintEventKey) ==
            (Declaration.bBlueprintEvent ? TEXT("true") : TEXT("false")) &&
        Metadata->HasMetaData(BlueprintEventSuperModeKey) &&
        Metadata->GetMetaData(BlueprintEventSuperModeKey) ==
            (Declaration.bBlueprintEvent ? TEXT("explicit") : TEXT("none")) &&
        (!Declaration.bIsStatic ||
         (Metadata->HasMetaData(TEXT("WorldContext")) &&
          Metadata->GetMetaData(TEXT("WorldContext")) ==
              WorldContextObjectName.ToString()));
    if (bMetadataMatches)
    {
        return true;
    }

    // UE reconstructs interface graphs from the native interface on load and
    // drops their user-declared function metadata. The serialized generated
    // class binding carries the same semantic identity and complete ABI shape,
    // so it is the no-change authority for this graph kind.
    const FUnifiedScriptManagedFunctionBinding* PersistedBinding =
        FindPersistedManagedFunctionBinding(Blueprint, Graph);
    return Declaration.bInterfaceFunction && PersistedBinding != nullptr &&
        RuntimeFunctionBindingMatches(*PersistedBinding, Declaration);
}

bool ValidateInterfaceFunctionSignature(
    const FFunctionDeclaration& Declaration,
    FString& OutError)
{
    const UEdGraphSchema_K2* Schema = GetDefault<UEdGraphSchema_K2>();
    for (const FString& InterfacePath : Declaration.InterfacePaths)
    {
        UClass* InterfaceClass = LoadObject<UClass>(nullptr, *InterfacePath);
        UFunction* Function = InterfaceClass != nullptr
            ? InterfaceClass->FindFunctionByName(
                Declaration.InternalName,
                EIncludeSuperFlag::IncludeSuper)
            : nullptr;
        if (InterfaceClass == nullptr ||
            !InterfaceClass->HasAnyClassFlags(CLASS_Interface) ||
            Function == nullptr ||
            !Function->HasAnyFunctionFlags(FUNC_BlueprintEvent))
        {
            OutError = FString::Printf(
                TEXT("Managed interface function '%s' cannot resolve Blueprint event '%s:%s'."),
                *Declaration.ManagedName.ToString(),
                *InterfacePath,
                *Declaration.InternalName.ToString());
            return false;
        }

        TArray<const FProperty*> NativeParameters;
        for (TFieldIterator<FProperty> It(Function, EFieldIteratorFlags::ExcludeSuper); It; ++It)
        {
            const FProperty* Property = *It;
            if (Property->HasAnyPropertyFlags(CPF_Parm) &&
                !Property->HasAnyPropertyFlags(CPF_ReturnParm))
            {
                NativeParameters.Add(Property);
            }
        }
        if (NativeParameters.Num() != Declaration.Parameters.Num())
        {
            OutError = FString::Printf(
                TEXT("Managed interface function '%s' has %d parameters; native '%s:%s' has %d."),
                *Declaration.ManagedName.ToString(),
                Declaration.Parameters.Num(),
                *InterfacePath,
                *Declaration.InternalName.ToString(),
                NativeParameters.Num());
            return false;
        }
        for (int32 Index = 0; Index < NativeParameters.Num(); ++Index)
        {
            FEdGraphPinType NativePinType;
            if (!Schema->ConvertPropertyToPinType(NativeParameters[Index], NativePinType) ||
                NativePinType != Declaration.Parameters[Index].PinType)
            {
                OutError = FString::Printf(
                    TEXT("Managed interface function '%s' parameter %d does not match native '%s:%s'."),
                    *Declaration.ManagedName.ToString(),
                    Index,
                    *InterfacePath,
                    *Declaration.InternalName.ToString());
                return false;
            }
        }

        const FProperty* NativeReturn = Function->GetReturnProperty();
        if (Declaration.bHasReturnValue != (NativeReturn != nullptr))
        {
            OutError = FString::Printf(
                TEXT("Managed interface function '%s' return shape does not match native '%s:%s'."),
                *Declaration.ManagedName.ToString(),
                *InterfacePath,
                *Declaration.InternalName.ToString());
            return false;
        }
        if (NativeReturn != nullptr)
        {
            FEdGraphPinType NativeReturnType;
            if (!Schema->ConvertPropertyToPinType(NativeReturn, NativeReturnType) ||
                NativeReturnType != Declaration.ReturnPinType)
            {
                OutError = FString::Printf(
                    TEXT("Managed interface function '%s' return type does not match native '%s:%s'."),
                    *Declaration.ManagedName.ToString(),
                    *InterfacePath,
                    *Declaration.InternalName.ToString());
                return false;
            }
        }
    }
    return true;
}

bool ReadCanonicalDefault(
    const FString& StateName,
    const FString& MetadataName,
    const TSharedPtr<FJsonValue>& Value,
    FString& OutDefault,
    FString& OutError)
{
    if (!Value.IsValid())
    {
        OutError = FString::Printf(
            TEXT("State '%s' is missing its default for '%s'."),
            *StateName,
            *MetadataName);
        return false;
    }

    if (Value->IsNull())
    {
        if (MetadataName == TEXT("UnifiedScript.Unreal.UnrealObjectHandle") ||
            MetadataName == TEXT("UnifiedScript.Unreal.UnrealClassHandle"))
        {
            OutDefault = TEXT("None");
            return true;
        }
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
    if ((MetadataName == TEXT("UnifiedScript.Unreal.UnrealName") ||
         MetadataName == TEXT("UnifiedScript.Unreal.UnrealText")) &&
        Value->Type == EJson::String)
    {
        OutDefault = Value->AsString();
        return true;
    }
    if (MetadataName == TEXT("UnifiedScript.Unreal.UnrealVector") &&
        Value->Type == EJson::Object)
    {
        const TSharedPtr<FJsonObject> Object = Value->AsObject();
        double X = 0.0;
        double Y = 0.0;
        double Z = 0.0;
        if (Object.IsValid() && Object->Values.Num() == 3 &&
            Object->TryGetNumberField(TEXT("x"), X) && FMath::IsFinite(X) &&
            Object->TryGetNumberField(TEXT("y"), Y) && FMath::IsFinite(Y) &&
            Object->TryGetNumberField(TEXT("z"), Z) && FMath::IsFinite(Z))
        {
            OutDefault = FString::Printf(
                TEXT("(X=%s,Y=%s,Z=%s)"),
                *LexToString(X),
                *LexToString(Y),
                *LexToString(Z));
            return true;
        }
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

bool ParseFunctionValueType(
    const TSharedPtr<FJsonObject>& Owner,
    const TCHAR* Field,
    FFunctionValueTypeDeclaration& OutType,
    FString& OutError,
    int32 Depth = 0);

bool AppendFunctionValueType(
    const TSharedPtr<FJsonObject>& Object,
    bool bTopLevel,
    TArray<FName>& Prefix,
    FFunctionValueTypeDeclaration& OutType,
    FString& OutError,
    int32 Depth)
{
    FString Kind;
    FString UnrealTypePath;
    FString UnrealNativeName;
    const TArray<TSharedPtr<FJsonValue>>* Fields = nullptr;
    if (Depth > 16 || !ReadRequiredString(Object, TEXT("kind"), Kind, OutError))
    {
        OutError = TEXT("Function type nesting exceeds the supported depth or has no kind.");
        return false;
    }
    if (Kind == TEXT("array") || Kind == TEXT("set") || Kind == TEXT("map"))
    {
        const bool bMap = Kind == TEXT("map");
        const FString NativeName = bMap ? TEXT("TMap") :
            Kind == TEXT("array") ? TEXT("TArray") : TEXT("TSet");
        const TArray<TSharedPtr<FJsonValue>>* Arguments = nullptr;
        if (!Object->TryGetStringField(TEXT("unrealTypePath"), UnrealTypePath) ||
            !UnrealTypePath.IsEmpty() ||
            !ReadRequiredString(Object, TEXT("unrealNativeName"), UnrealNativeName, OutError) ||
            UnrealNativeName != NativeName ||
            !Object->TryGetArrayField(TEXT("fields"), Fields) || !Fields->IsEmpty() ||
            !Object->TryGetArrayField(TEXT("typeArguments"), Arguments) ||
            Arguments->Num() != (bMap ? 2 : 1))
        {
            OutError = TEXT("Container function type has invalid identity or type arguments.");
            return false;
        }
        if (bTopLevel)
        {
            OutType.Kind = bMap ? EFunctionValueTypeKind::Map :
                Kind == TEXT("array") ? EFunctionValueTypeKind::Array : EFunctionValueTypeKind::Set;
            OutType.UnrealNativeName = NativeName;
        }
        for (int32 Index = 0; Index < Arguments->Num(); ++Index)
        {
            const TSharedPtr<FJsonObject> Argument = (*Arguments)[Index].IsValid()
                ? (*Arguments)[Index]->AsObject() : nullptr;
            FString MetadataName;
            FFunctionValueTypeDeclaration Child;
            if (!ReadRequiredString(Argument, TEXT("typeMetadataName"), MetadataName, OutError) ||
                !ParseFunctionValueType(Argument, TEXT("unrealType"), Child, OutError, Depth + 1))
            {
                return false;
            }
            // Reserved segments cannot collide with native reflected field names.
            Prefix.Add(FName(bMap ? (Index == 0 ? TEXT("$key") : TEXT("$value")) : TEXT("$element")));
            OutType.StructFieldPaths.Add(Prefix);
            for (const TArray<FName>& ChildPath : Child.StructFieldPaths)
            {
                TArray<FName> Path = Prefix;
                Path.Append(ChildPath);
                OutType.StructFieldPaths.Add(MoveTemp(Path));
            }
            Prefix.Pop();
            if (bTopLevel)
            {
                OutType.ArgumentMetadataNames.Add(MetadataName);
                OutType.TypeArguments.Add(MoveTemp(Child));
            }
        }
        return true;
    }
    if (
        !ReadRequiredString(Object, TEXT("unrealTypePath"), UnrealTypePath, OutError) ||
        !ReadRequiredString(Object, TEXT("unrealNativeName"), UnrealNativeName, OutError) ||
        !Object->TryGetArrayField(TEXT("fields"), Fields) || Fields == nullptr)
    {
        if (OutError.IsEmpty())
        {
            OutError = TEXT("Function Unreal value type requires a fields array.");
        }
        return false;
    }

    const EFunctionValueTypeKind ParsedKind = Kind == TEXT("enum")
        ? EFunctionValueTypeKind::Enum
        : Kind == TEXT("struct")
            ? EFunctionValueTypeKind::Struct
            : EFunctionValueTypeKind::None;
    if (ParsedKind == EFunctionValueTypeKind::None ||
        !UnrealTypePath.StartsWith(TEXT("/Script/")) ||
        Object->HasField(TEXT("typeArguments")) ||
        (ParsedKind == EFunctionValueTypeKind::Enum && !Fields->IsEmpty()))
    {
        OutError = FString::Printf(
            TEXT("Function Unreal value type '%s' has an invalid kind, path, or field contract."),
            *UnrealTypePath);
        return false;
    }
    if (bTopLevel)
    {
        OutType.Kind = ParsedKind;
        OutType.UnrealTypePath = UnrealTypePath;
        OutType.UnrealNativeName = UnrealNativeName;
    }

    if (ParsedKind == EFunctionValueTypeKind::Enum)
    {
        return true;
    }
    for (int32 Index = 0; Index < Fields->Num(); ++Index)
    {
        const TSharedPtr<FJsonObject> Field = (*Fields)[Index].IsValid()
            ? (*Fields)[Index]->AsObject()
            : nullptr;
        FString NativeName;
        double DeclaredIndex = -1.0;
        if (!ReadRequiredString(Field, TEXT("nativeName"), NativeName, OutError) ||
            !Field->TryGetNumberField(TEXT("index"), DeclaredIndex) ||
            DeclaredIndex != static_cast<double>(Index))
        {
            if (OutError.IsEmpty())
            {
                OutError = FString::Printf(
                    TEXT("Function struct '%s' has invalid field identity at index %d."),
                    *UnrealTypePath,
                    Index);
            }
            return false;
        }
        Prefix.Add(FName(*NativeName));
        OutType.StructFieldPaths.Add(Prefix);
        const TSharedPtr<FJsonObject>* Nested = nullptr;
        if (Field->HasField(TEXT("unrealType")))
        {
            if (!Field->TryGetObjectField(TEXT("unrealType"), Nested) ||
                Nested == nullptr || !Nested->IsValid() ||
                !AppendFunctionValueType(*Nested, false, Prefix, OutType, OutError, Depth + 1))
            {
                Prefix.Pop();
                return false;
            }
        }
        Prefix.Pop();
    }
    return true;
}

bool ParseFunctionValueType(
    const TSharedPtr<FJsonObject>& Owner,
    const TCHAR* Field,
    FFunctionValueTypeDeclaration& OutType,
    FString& OutError,
    int32 Depth)
{
    OutType = FFunctionValueTypeDeclaration();
    if (!Owner.IsValid() || !Owner->HasField(Field))
    {
        return true;
    }
    const TSharedPtr<FJsonObject>* Object = nullptr;
    if (!Owner->TryGetObjectField(Field, Object) || Object == nullptr || !Object->IsValid())
    {
        OutError = FString::Printf(TEXT("Function value type field '%s' must be an object."), Field);
        return false;
    }
    TArray<FName> Prefix;
    return AppendFunctionValueType(*Object, true, Prefix, OutType, OutError, Depth);
}

FUnifiedScriptManagedValueTypeBinding MakeRuntimeValueTypeBinding(
    const FFunctionValueTypeDeclaration& Declaration)
{
    FUnifiedScriptManagedValueTypeBinding Result;
    Result.UnrealTypePath = Declaration.UnrealTypePath;
    Result.StructFields.Reserve(Declaration.StructFieldPaths.Num());
    for (const TArray<FName>& Path : Declaration.StructFieldPaths)
    {
        FUnifiedScriptManagedStructFieldPath& Field = Result.StructFields.AddDefaulted_GetRef();
        Field.Segments = Path;
    }
    return Result;
}

bool ParseGameplayBindings(
    EUnifiedScriptClassKind ClassKind,
    const TArray<TSharedPtr<FJsonValue>>& Functions,
    TArray<FInputActionDeclaration>& OutInputBindings,
    TArray<FDelegateBindingDeclaration>& OutDelegateBindings,
    TArray<FInterfaceFunctionDeclaration>& OutInterfaceBindings,
    TMap<FGuid, FName>& OutInterfaceFunctionNames,
    FString& OutError)
{
    OutInputBindings.Reset();
    OutDelegateBindings.Reset();
    OutInterfaceBindings.Reset();
    OutInterfaceFunctionNames.Reset();
    const bool bGameplayClass = ClassKind == EUnifiedScriptClassKind::Actor ||
        ClassKind == EUnifiedScriptClassKind::ActorComponent;
    TSet<FGuid> SeenBindingIds;
    TSet<FString> SeenRoutes;

    for (const TSharedPtr<FJsonValue>& FunctionValue : Functions)
    {
        const TSharedPtr<FJsonObject> Function = FunctionValue.IsValid()
            ? FunctionValue->AsObject()
            : nullptr;
        FString FunctionIdText;
        FString ManagedNameText;
        bool bIsStatic = false;
        bool bIsPublic = false;
        bool bIsEvent = false;
        if (!ReadRequiredString(Function, TEXT("id"), FunctionIdText, OutError) ||
            !ReadRequiredString(Function, TEXT("name"), ManagedNameText, OutError) ||
            !Function->TryGetBoolField(TEXT("isStatic"), bIsStatic) ||
            !Function->TryGetBoolField(TEXT("isPublic"), bIsPublic) ||
            !Function->TryGetBoolField(TEXT("isEvent"), bIsEvent))
        {
            if (OutError.IsEmpty())
            {
                OutError = TEXT("Gameplay bindings require complete function identity and flags.");
            }
            return false;
        }
        FGuid FunctionId;
        const FName ManagedName(*ManagedNameText);
        if (!ParseSemanticId(FunctionIdText, FunctionId) || ManagedName.IsNone())
        {
            OutError = FString::Printf(
                TEXT("Gameplay binding function '%s' has an invalid stable identity."),
                *ManagedNameText);
            return false;
        }

        const TArray<TSharedPtr<FJsonValue>>* InputActions = nullptr;
        const TArray<TSharedPtr<FJsonValue>>* DelegateBindings = nullptr;
        const TArray<TSharedPtr<FJsonValue>>* InterfaceFunctions = nullptr;
        if ((Function->HasField(TEXT("inputActions")) &&
             (!Function->TryGetArrayField(TEXT("inputActions"), InputActions) ||
              InputActions == nullptr)) ||
            (Function->HasField(TEXT("delegateBindings")) &&
             (!Function->TryGetArrayField(TEXT("delegateBindings"), DelegateBindings) ||
              DelegateBindings == nullptr)) ||
            (Function->HasField(TEXT("interfaceFunctions")) &&
             (!Function->TryGetArrayField(TEXT("interfaceFunctions"), InterfaceFunctions) ||
              InterfaceFunctions == nullptr)))
        {
            OutError = FString::Printf(
                TEXT("Gameplay binding arrays for '%s' are invalid."),
                *ManagedNameText);
            return false;
        }
        const bool bHasBindings =
            (InputActions != nullptr && !InputActions->IsEmpty()) ||
            (DelegateBindings != nullptr && !DelegateBindings->IsEmpty()) ||
            (InterfaceFunctions != nullptr && !InterfaceFunctions->IsEmpty());
        if (bHasBindings && !bGameplayClass)
        {
            OutError = FString::Printf(
                TEXT("Only Actor and ActorComponent Script Classes can bind gameplay method '%s'."),
                *ManagedNameText);
            return false;
        }

        if (InputActions != nullptr)
        {
            if (!InputActions->IsEmpty() && (!bIsPublic || bIsStatic || bIsEvent))
            {
                OutError = FString::Printf(
                    TEXT("Enhanced Input handler '%s' must be a public instance non-delegate method."),
                    *ManagedNameText);
                return false;
            }
            for (const TSharedPtr<FJsonValue>& BindingValue : *InputActions)
            {
                const TSharedPtr<FJsonObject> Binding = BindingValue.IsValid()
                    ? BindingValue->AsObject()
                    : nullptr;
                FString BindingIdText;
                FString ActionPath;
                FString TriggerEvent;
                FString ValueType;
                bool bConsumeInput = false;
                if (!ReadRequiredString(Binding, TEXT("id"), BindingIdText, OutError) ||
                    !ReadRequiredString(Binding, TEXT("actionPath"), ActionPath, OutError) ||
                    !ReadRequiredString(Binding, TEXT("triggerEvent"), TriggerEvent, OutError) ||
                    !ReadRequiredString(Binding, TEXT("valueType"), ValueType, OutError) ||
                    !Binding->TryGetBoolField(TEXT("consumeInput"), bConsumeInput))
                {
                    return false;
                }
                FInputActionDeclaration Declaration;
                Declaration.ManagedName = ManagedName;
                Declaration.ActionPath = ActionPath;
                Declaration.bConsumeInput = bConsumeInput;
                if (TriggerEvent == TEXT("started"))
                {
                    Declaration.TriggerEvent = EUnifiedScriptManagedInputTriggerEvent::Started;
                }
                else if (TriggerEvent == TEXT("ongoing"))
                {
                    Declaration.TriggerEvent = EUnifiedScriptManagedInputTriggerEvent::Ongoing;
                }
                else if (TriggerEvent == TEXT("triggered"))
                {
                    Declaration.TriggerEvent = EUnifiedScriptManagedInputTriggerEvent::Triggered;
                }
                else if (TriggerEvent == TEXT("completed"))
                {
                    Declaration.TriggerEvent = EUnifiedScriptManagedInputTriggerEvent::Completed;
                }
                else if (TriggerEvent == TEXT("canceled"))
                {
                    Declaration.TriggerEvent = EUnifiedScriptManagedInputTriggerEvent::Canceled;
                }
                else
                {
                    OutError = FString::Printf(
                        TEXT("Enhanced Input handler '%s' has unsupported trigger '%s'."),
                        *ManagedNameText,
                        *TriggerEvent);
                    return false;
                }
                if (ValueType == TEXT("boolean"))
                {
                    Declaration.ValueType = EUnifiedScriptManagedInputValueType::Boolean;
                }
                else if (ValueType == TEXT("axis1D"))
                {
                    Declaration.ValueType = EUnifiedScriptManagedInputValueType::Axis1D;
                }
                else if (ValueType == TEXT("axis2D"))
                {
                    Declaration.ValueType = EUnifiedScriptManagedInputValueType::Axis2D;
                }
                else if (ValueType == TEXT("axis3D"))
                {
                    Declaration.ValueType = EUnifiedScriptManagedInputValueType::Axis3D;
                }
                else
                {
                    OutError = FString::Printf(
                        TEXT("Enhanced Input handler '%s' has unsupported value type '%s'."),
                        *ManagedNameText,
                        *ValueType);
                    return false;
                }
                const FString Route = FString::Printf(
                    TEXT("input|%s|%s|%s"),
                    *ManagedNameText,
                    *ActionPath,
                    *TriggerEvent);
                if (!ParseSemanticId(BindingIdText, Declaration.BindingId) ||
                    ActionPath.IsEmpty() || SeenBindingIds.Contains(Declaration.BindingId) ||
                    SeenRoutes.Contains(Route))
                {
                    OutError = FString::Printf(
                        TEXT("Enhanced Input handler '%s' has an invalid or duplicate binding."),
                        *ManagedNameText);
                    return false;
                }
                SeenBindingIds.Add(Declaration.BindingId);
                SeenRoutes.Add(Route);
                OutInputBindings.Add(MoveTemp(Declaration));
            }
        }

        if (DelegateBindings != nullptr)
        {
            if (!DelegateBindings->IsEmpty() && (bIsStatic || !bIsEvent))
            {
                OutError = FString::Printf(
                    TEXT("Delegate handler '%s' must be an instance native-event method."),
                    *ManagedNameText);
                return false;
            }
            for (const TSharedPtr<FJsonValue>& BindingValue : *DelegateBindings)
            {
                const TSharedPtr<FJsonObject> Binding = BindingValue.IsValid()
                    ? BindingValue->AsObject()
                    : nullptr;
                FString BindingIdText;
                FString SourceTypePath;
                FString EventPath;
                FString Source;
                if (!ReadRequiredString(Binding, TEXT("id"), BindingIdText, OutError) ||
                    !ReadRequiredString(Binding, TEXT("sourceTypePath"), SourceTypePath, OutError) ||
                    !ReadRequiredString(Binding, TEXT("eventPath"), EventPath, OutError) ||
                    !ReadRequiredString(Binding, TEXT("source"), Source, OutError))
                {
                    return false;
                }
                FDelegateBindingDeclaration Declaration;
                Declaration.ManagedName = ManagedName;
                Declaration.SourceTypePath = SourceTypePath;
                Declaration.EventPath = EventPath;
                if (Source == TEXT("target"))
                {
                    Declaration.Source = EUnifiedScriptManagedDelegateSource::Target;
                }
                else if (Source == TEXT("host"))
                {
                    Declaration.Source = EUnifiedScriptManagedDelegateSource::Host;
                }
                else
                {
                    OutError = FString::Printf(
                        TEXT("Delegate handler '%s' has unsupported source '%s'."),
                        *ManagedNameText,
                        *Source);
                    return false;
                }
                const TSharedPtr<FJsonValue> SourceMember =
                    Binding->TryGetField(TEXT("sourceMember"));
                if (SourceMember.IsValid() && !SourceMember->IsNull() &&
                    !SourceMember->TryGetString(Declaration.SourceMember))
                {
                    OutError = FString::Printf(
                        TEXT("Delegate handler '%s' sourceMember must be a string or null."),
                        *ManagedNameText);
                    return false;
                }
                const FString Route = FString::Printf(
                    TEXT("delegate|%s|%s|%s|%s"),
                    *ManagedNameText,
                    *SourceTypePath,
                    *EventPath,
                    *Source);
                if (!ParseSemanticId(BindingIdText, Declaration.BindingId) ||
                    SourceTypePath.IsEmpty() || EventPath.IsEmpty() ||
                    SeenBindingIds.Contains(Declaration.BindingId) ||
                    SeenRoutes.Contains(Route))
                {
                    OutError = FString::Printf(
                        TEXT("Delegate handler '%s' has an invalid or duplicate binding."),
                        *ManagedNameText);
                    return false;
                }
                SeenBindingIds.Add(Declaration.BindingId);
                SeenRoutes.Add(Route);
                OutDelegateBindings.Add(MoveTemp(Declaration));
            }
        }

        if (InterfaceFunctions != nullptr)
        {
            if (!InterfaceFunctions->IsEmpty() && (!bIsPublic || bIsStatic || bIsEvent))
            {
                OutError = FString::Printf(
                    TEXT("Interface implementation '%s' must be a public instance non-delegate method."),
                    *ManagedNameText);
                return false;
            }
            for (const TSharedPtr<FJsonValue>& BindingValue : *InterfaceFunctions)
            {
                const TSharedPtr<FJsonObject> Binding = BindingValue.IsValid()
                    ? BindingValue->AsObject()
                    : nullptr;
                FString BindingIdText;
                FString InterfacePath;
                FString FunctionNameText;
                if (!ReadRequiredString(Binding, TEXT("id"), BindingIdText, OutError) ||
                    !ReadRequiredString(Binding, TEXT("interfacePath"), InterfacePath, OutError) ||
                    !ReadRequiredString(Binding, TEXT("functionName"), FunctionNameText, OutError))
                {
                    return false;
                }
                FInterfaceFunctionDeclaration Declaration;
                Declaration.InterfacePath = InterfacePath;
                Declaration.FunctionName = FName(*FunctionNameText);
                Declaration.ManagedName = ManagedName;
                Declaration.FunctionId = FunctionId;
                const FString Route = FString::Printf(
                    TEXT("interface|%s|%s"),
                    *InterfacePath,
                    *FunctionNameText);
                if (!ParseSemanticId(BindingIdText, Declaration.BindingId) ||
                    !InterfacePath.StartsWith(TEXT("/Script/")) ||
                    Declaration.FunctionName.IsNone() ||
                    SeenBindingIds.Contains(Declaration.BindingId) ||
                    SeenRoutes.Contains(Route))
                {
                    OutError = FString::Printf(
                        TEXT("Interface implementation '%s' has an invalid or duplicate binding."),
                        *ManagedNameText);
                    return false;
                }
                if (const FName* ExistingName = OutInterfaceFunctionNames.Find(FunctionId);
                    ExistingName != nullptr && *ExistingName != Declaration.FunctionName)
                {
                    OutError = FString::Printf(
                        TEXT("Managed method '%s' cannot implement differently named native interface functions."),
                        *ManagedNameText);
                    return false;
                }
                OutInterfaceFunctionNames.Add(FunctionId, Declaration.FunctionName);
                SeenBindingIds.Add(Declaration.BindingId);
                SeenRoutes.Add(Route);
                OutInterfaceBindings.Add(MoveTemp(Declaration));
            }
        }
    }
    return true;
}

bool ParseDeclaration(
    UUnifiedScriptAsset& ScriptAsset,
    const FString& DeclarationJson,
    TArray<FStateDeclaration>& OutStates,
    TArray<FFunctionDeclaration>& OutFunctions,
    TArray<FLifecycleDeclaration>& OutLifecycleHooks,
    TArray<FInputActionDeclaration>& OutInputBindings,
    TArray<FDelegateBindingDeclaration>& OutDelegateBindings,
    TArray<FInterfaceFunctionDeclaration>& OutInterfaceBindings,
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

    TMap<FGuid, FName> InterfaceFunctionNames;
    if (!ParseGameplayBindings(
            ScriptAsset.ScriptClassKind,
            *Functions,
            OutInputBindings,
            OutDelegateBindings,
            OutInterfaceBindings,
            InterfaceFunctionNames,
            OutError))
    {
        return false;
    }

    const bool bGameplayLifecycle =
        ScriptAsset.ScriptClassKind == EUnifiedScriptClassKind::Actor ||
        ScriptAsset.ScriptClassKind == EUnifiedScriptClassKind::ActorComponent;
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
        FFunctionValueTypeDeclaration StateUnrealType;
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
        if (!ParseFunctionValueType(
                State,
                TEXT("unrealType"),
                StateUnrealType,
                OutError) ||
            !ResolvePinType(
                TypeKind,
                MetadataName,
                Declaration.PinType,
                &StateUnrealType))
        {
            OutError = FString::Printf(
                TEXT("State '%s' uses unsupported Blueprint state type '%s' (%s). Reviewed types are bool, byte, int, long, float, double, string, FName, FText, UObject/UClass handles, and FVector."),
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
        FString Condition = TEXT("none");
        if ((*Exposure)->HasField(TEXT("replicationCondition")) &&
            !(*Exposure)->TryGetStringField(TEXT("replicationCondition"), Condition))
        {
            OutError = TEXT("State replicationCondition must be a named condition.");
            return false;
        }
        if (Condition == TEXT("none")) Declaration.ReplicationCondition = COND_None;
        else if (Condition == TEXT("initialOnly")) Declaration.ReplicationCondition = COND_InitialOnly;
        else if (Condition == TEXT("ownerOnly")) Declaration.ReplicationCondition = COND_OwnerOnly;
        else if (Condition == TEXT("skipOwner")) Declaration.ReplicationCondition = COND_SkipOwner;
        else if (Condition == TEXT("initialOrOwner")) Declaration.ReplicationCondition = COND_InitialOrOwner;
        else { OutError = TEXT("Unsupported state replication condition."); return false; }
        if ((!bReplicated && Declaration.ReplicationCondition != COND_None) || (bReplicated && !bGameplayLifecycle))
        {
            OutError = TEXT("Replicated state requires an Actor/ActorComponent; conditions require replicated state.");
            return false;
        }

        SeenIds.Add(Declaration.Id);
        SeenNames.Add(Declaration.Name);
        OutStates.Add(MoveTemp(Declaration));
    }

    OutLifecycleHooks.Reset();
    const TArray<TSharedPtr<FJsonValue>>* LifecycleHooks = nullptr;
    if (SelectedAsset->HasField(TEXT("lifecycleHooks")) &&
        (!SelectedAsset->TryGetArrayField(TEXT("lifecycleHooks"), LifecycleHooks) ||
         LifecycleHooks == nullptr))
    {
        OutError = TEXT("Script Class lifecycleHooks must be an array.");
        return false;
    }
    if (LifecycleHooks != nullptr && !LifecycleHooks->IsEmpty() && !bGameplayLifecycle)
    {
        OutError = TEXT("Only Actor and ActorComponent Script Classes can declare gameplay lifecycle hooks.");
        return false;
    }
    TSet<FGuid> SeenLifecycleIds;
    TSet<EUnifiedScriptManagedLifecycleHookKind> SeenLifecycleKinds;
    if (LifecycleHooks != nullptr)
    {
        OutLifecycleHooks.Reserve(LifecycleHooks->Num());
        for (const TSharedPtr<FJsonValue>& HookValue : *LifecycleHooks)
        {
            const TSharedPtr<FJsonObject> Hook = HookValue.IsValid()
                ? HookValue->AsObject()
                : nullptr;
            FString SemanticId;
            FString Kind;
            FString ManagedName;
            if (!ReadRequiredString(Hook, TEXT("id"), SemanticId, OutError) ||
                !ReadRequiredString(Hook, TEXT("kind"), Kind, OutError) ||
                !ReadRequiredString(Hook, TEXT("managedName"), ManagedName, OutError))
            {
                return false;
            }

            FLifecycleDeclaration Declaration;
            FString ExpectedName;
            if (Kind == TEXT("beginPlay"))
            {
                Declaration.Kind = EUnifiedScriptManagedLifecycleHookKind::BeginPlay;
                ExpectedName = TEXT("OnBeginPlay");
            }
            else if (Kind == TEXT("tick"))
            {
                Declaration.Kind = EUnifiedScriptManagedLifecycleHookKind::Tick;
                ExpectedName = TEXT("OnTick");
            }
            else if (Kind == TEXT("endPlay"))
            {
                Declaration.Kind = EUnifiedScriptManagedLifecycleHookKind::EndPlay;
                ExpectedName = TEXT("OnEndPlay");
            }
            else
            {
                OutError = FString::Printf(
                    TEXT("Lifecycle hook '%s' has an unsupported kind '%s'."),
                    *ManagedName,
                    *Kind);
                return false;
            }
            Declaration.ManagedName = FName(*ManagedName);
            if (!ParseSemanticId(SemanticId, Declaration.Id) ||
                Declaration.ManagedName.IsNone() || ManagedName != ExpectedName ||
                SeenLifecycleIds.Contains(Declaration.Id) ||
                SeenLifecycleKinds.Contains(Declaration.Kind))
            {
                OutError = FString::Printf(
                    TEXT("Lifecycle hook '%s' has an invalid, duplicate, or mismatched stable identity."),
                    *ManagedName);
                return false;
            }
            SeenLifecycleIds.Add(Declaration.Id);
            SeenLifecycleKinds.Add(Declaration.Kind);
            OutLifecycleHooks.Add(MoveTemp(Declaration));
        }
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

        const TSharedPtr<FJsonObject>* BlueprintEvent = nullptr;
        if (Function->HasField(TEXT("blueprintEvent")))
        {
            FString SuperMode;
            if (!Function->TryGetObjectField(TEXT("blueprintEvent"), BlueprintEvent) ||
                BlueprintEvent == nullptr || !BlueprintEvent->IsValid() ||
                !ReadRequiredString(*BlueprintEvent, TEXT("superMode"), SuperMode, OutError) ||
                SuperMode != TEXT("explicit"))
            {
                OutError = FString::Printf(
                    TEXT("Blueprint event '%s' requires superMode 'explicit'."),
                    *Name);
                return false;
            }
            if (!bGameplayLifecycle || bIsStatic)
            {
                OutError = FString::Printf(
                    TEXT("Blueprint event '%s' must be an Actor or ActorComponent instance method."),
                    *Name);
                return false;
            }
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
        Declaration.bBlueprintEvent = BlueprintEvent != nullptr;
        if (Function->HasField(TEXT("rpc")))
        {
            const TSharedPtr<FJsonObject>* Rpc = nullptr;
            FString Target;
            if (!Function->TryGetObjectField(TEXT("rpc"), Rpc) || Rpc == nullptr || !Rpc->IsValid() ||
                !(*Rpc)->TryGetStringField(TEXT("target"), Target) || !(*Rpc)->TryGetBoolField(TEXT("reliable"), Declaration.bReliable))
            { OutError = TEXT("RPC requires a target and explicit reliability."); return false; }
            if (Target == TEXT("server")) Declaration.RpcTarget = EUnifiedScriptRpcTarget::Server;
            else if (Target == TEXT("client")) Declaration.RpcTarget = EUnifiedScriptRpcTarget::Client;
            else if (Target == TEXT("multicast")) Declaration.RpcTarget = EUnifiedScriptRpcTarget::Multicast;
            else { OutError = TEXT("Unsupported RPC target."); return false; }
        }
        if (Function->HasField(TEXT("repNotifyProperty")))
        {
            FString Property;
            if (!Function->TryGetStringField(TEXT("repNotifyProperty"), Property) || Property.IsEmpty())
            { OutError = TEXT("RepNotify requires an exact property name."); return false; }
            Declaration.RepNotifyProperty = FName(*Property);
        }
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
        if (const FName* InterfaceFunctionName =
                InterfaceFunctionNames.Find(Declaration.Id))
        {
            Declaration.bInterfaceFunction = true;
            Declaration.InternalName = *InterfaceFunctionName;
            for (const FInterfaceFunctionDeclaration& Interface : OutInterfaceBindings)
            {
                if (Interface.FunctionId == Declaration.Id)
                {
                    Declaration.InterfacePaths.AddUnique(Interface.InterfacePath);
                }
            }
        }
        else
        {
            Declaration.InternalName = MakeManagedFunctionInternalName(Declaration.Id);
        }
        if (!ParseFunctionValueType(
                Function,
                TEXT("returnUnrealType"),
                Declaration.ReturnValueType,
                OutError) ||
            !ResolveFunctionPinType(
                ReturnType,
                Declaration.ReturnValueType,
                true,
                Declaration.ReturnPinType,
                Declaration.bHasReturnValue))
        {
            OutError = FString::Printf(
                TEXT("Function '%s' has unsupported return type '%s'. %s Use reviewed values, array/set/map, or Soft References; K2 forbids direct nested containers and requires hashable set elements/map keys."),
                *Name,
                *ReturnType,
                *OutError);
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
            FString ParameterDirection = TEXT("input");
            if (Parameter->HasField(TEXT("direction")) &&
                !Parameter->TryGetStringField(
                    TEXT("direction"),
                    ParameterDirection))
            {
                OutError = FString::Printf(
                    TEXT("Function '%s' parameter '%s' has an invalid direction."),
                    *Name,
                    *ParameterName);
                return false;
            }
            if (ParameterDirection == TEXT("output"))
            {
                ParameterDeclaration.Direction =
                    EUnifiedScriptManagedParameterDirection::Output;
            }
            else if (ParameterDirection == TEXT("inputOutput"))
            {
                ParameterDeclaration.Direction =
                    EUnifiedScriptManagedParameterDirection::InputOutput;
            }
            else if (ParameterDirection != TEXT("input"))
            {
                OutError = FString::Printf(
                    TEXT("Function '%s' parameter '%s' uses unsupported direction '%s'."),
                    *Name,
                    *ParameterName,
                    *ParameterDirection);
                return false;
            }
            bool bHasParameterValue = false;
            if (!ParseSemanticId(ParameterId, ParameterDeclaration.Id) ||
                ParameterDeclaration.Name.IsNone() ||
                SeenParameterIds.Contains(ParameterDeclaration.Id) ||
                SeenParameterNames.Contains(ParameterDeclaration.Name) ||
                !ParseFunctionValueType(
                    Parameter,
                    TEXT("unrealType"),
                    ParameterDeclaration.ValueType,
                    OutError) ||
                !ResolveFunctionPinType(
                    ParameterType,
                    ParameterDeclaration.ValueType,
                    false,
                    ParameterDeclaration.PinType,
                    bHasParameterValue) ||
                !bHasParameterValue)
            {
                OutError = FString::Printf(
                    TEXT("Function '%s' has duplicate, invalid, or unsupported parameter '%s' of type '%s'. %s Use reviewed values, array/set/map, or Soft References; K2 forbids direct nested containers and requires hashable set elements/map keys."),
                    *Name,
                    *ParameterName,
                    *ParameterType,
                    *OutError);
                return false;
            }
            ParameterDeclaration.PinType.bIsReference =
                ParameterDeclaration.Direction ==
                EUnifiedScriptManagedParameterDirection::InputOutput;
            SeenParameterIds.Add(ParameterDeclaration.Id);
            SeenParameterNames.Add(ParameterDeclaration.Name);
            Declaration.Parameters.Add(MoveTemp(ParameterDeclaration));
        }

        if (Declaration.RpcTarget != EUnifiedScriptRpcTarget::None || !Declaration.RepNotifyProperty.IsNone())
        {
            if (!bGameplayLifecycle || Declaration.bIsStatic || Declaration.bHasReturnValue ||
                Declaration.bBlueprintEvent || Declaration.bInterfaceFunction ||
                (Declaration.RpcTarget != EUnifiedScriptRpcTarget::None && !Declaration.RepNotifyProperty.IsNone()) ||
                Declaration.Parameters.ContainsByPredicate([](const FFunctionParameterDeclaration& P)
                    { return P.Direction != EUnifiedScriptManagedParameterDirection::Input; }) ||
                OutInputBindings.ContainsByPredicate([&](const FInputActionDeclaration& B) { return B.ManagedName == Declaration.ManagedName; }) ||
                OutDelegateBindings.ContainsByPredicate([&](const FDelegateBindingDeclaration& B) { return B.ManagedName == Declaration.ManagedName; }))
            { OutError = TEXT("RPC/RepNotify must be instance void input-only gameplay methods without other bindings."); return false; }
        }
        if (Declaration.RpcTarget != EUnifiedScriptRpcTarget::None &&
            (Declaration.Parameters.Num() > 4 || Declaration.Parameters.ContainsByPredicate([](const FFunctionParameterDeclaration& P)
            {
                const FEdGraphPinType& Pin = P.PinType;
                if (Pin.IsContainer() || Pin.PinCategory == UEdGraphSchema_K2::PC_SoftObject || Pin.PinCategory == UEdGraphSchema_K2::PC_SoftClass) return true;
                const UObject* Type = Pin.PinSubCategoryObject.Get();
                return Pin.PinCategory == UEdGraphSchema_K2::PC_Struct &&
                    Type != TBaseStructure<FVector>::Get() && Type != TBaseStructure<FRotator>::Get() && Type != TBaseStructure<FTransform>::Get();
            })))
        { OutError = TEXT("RPC supports 0-4 scalar/common-value inputs; collections, Soft References and complex structures are not supported RPC payloads yet."); return false; }
        SeenFunctionIds.Add(Declaration.Id);
        SeenFunctionNames.Add(Declaration.ManagedName);
        OutFunctions.Add(MoveTemp(Declaration));
    }
    for (const FFunctionDeclaration& Function : OutFunctions)
    {
        if (Function.RepNotifyProperty.IsNone()) continue;
        FStateDeclaration* State = OutStates.FindByPredicate([&](const FStateDeclaration& S) { return S.Name == Function.RepNotifyProperty; });
        if (State == nullptr || !(State->PropertyFlags & CPF_Net) || !State->RepNotifyFunc.IsNone() ||
            Function.Parameters.Num() > 1 || (Function.Parameters.Num() == 1 &&
                (Function.Parameters[0].TypeMetadataName != State->TypeMetadataName || Function.Parameters[0].PinType != State->PinType)))
        { OutError = TEXT("RepNotify needs one replicated property and zero parameters or its exact previous-value type."); return false; }
        State->RepNotifyFunc = Function.InternalName;
        State->PropertyFlags |= CPF_RepNotify;
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
    if (const FNameProperty* Typed = CastField<FNameProperty>(Property))
    {
        OutValue = Typed->GetPropertyValue_InContainer(Cdo).ToString();
        return true;
    }
    if (const FTextProperty* Typed = CastField<FTextProperty>(Property))
    {
        OutValue = Typed->GetPropertyValue_InContainer(Cdo).ToString();
        return true;
    }
    if (const FStructProperty* Typed = CastField<FStructProperty>(Property);
        Typed != nullptr && Typed->Struct == TBaseStructure<FVector>::Get())
    {
        const FVector& Vector = *Typed->ContainerPtrToValuePtr<FVector>(Cdo);
        OutValue = FString::Printf(
            TEXT("(X=%s,Y=%s,Z=%s)"),
            *LexToString(Vector.X),
            *LexToString(Vector.Y),
            *LexToString(Vector.Z));
        return true;
    }
    if (const FClassProperty* Typed = CastField<FClassProperty>(Property))
    {
        const UObject* Value = Typed->GetObjectPropertyValue_InContainer(Cdo);
        OutValue = Value != nullptr ? Value->GetPathName() : TEXT("None");
        return true;
    }
    if (const FObjectPropertyBase* Typed = CastField<FObjectPropertyBase>(Property))
    {
        const UObject* Value = Typed->GetObjectPropertyValue_InContainer(Cdo);
        OutValue = Value != nullptr ? Value->GetPathName() : TEXT("None");
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
        const bool bFlagsChanged = Existing.PropertyFlags != Declaration.PropertyFlags ||
            Existing.RepNotifyFunc != Declaration.RepNotifyFunc || Existing.ReplicationCondition != Declaration.ReplicationCondition;
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
    TArray<UEdGraph*> ExistingGraphs;
    ExistingGraphs.Reserve(
        Blueprint.FunctionGraphs.Num() + Blueprint.ImplementedInterfaces.Num() * 4);
    for (UEdGraph* Graph : Blueprint.FunctionGraphs)
    {
        ExistingGraphs.AddUnique(Graph);
    }
    for (const FBPInterfaceDescription& Interface : Blueprint.ImplementedInterfaces)
    {
        for (UEdGraph* Graph : Interface.Graphs)
        {
            ExistingGraphs.AddUnique(Graph);
        }
    }
    for (UEdGraph* Graph : ExistingGraphs)
    {
        if (Graph == nullptr || !IsManagedFunctionGraph(Blueprint, *Graph))
        {
            continue;
        }
        FGuid Id;
        if (!TryReadManagedFunctionGraphId(Blueprint, *Graph, Id) ||
            ExistingById.Contains(Id))
        {
            OutError = FString::Printf(
                TEXT("Generated Blueprint function graph '%s' has duplicate or invalid UnifiedScript.FunctionId metadata."),
                *GetPathNameSafe(Graph));
            return false;
        }
        ExistingById.Add(Id, Graph);
        if (!DesiredIds.Contains(Id) && !IsInterfaceGraph(Blueprint, Graph))
        {
            OutPlan.StaleGeneratedFunctionIds.Add(Id);
        }
    }

    for (const FFunctionDeclaration& Declaration : Declarations)
    {
        if (Declaration.bInterfaceFunction &&
            !ValidateInterfaceFunctionSignature(Declaration, OutError))
        {
            return false;
        }
        UEdGraph* const* Existing = ExistingById.Find(Declaration.Id);
        UEdGraph* NameOwner = FindFunctionGraphByName(
            Blueprint,
            Declaration.InternalName);
        if (NameOwner != nullptr &&
            (Existing == nullptr || NameOwner != *Existing) &&
            !IsManagedFunctionGraph(Blueprint, *NameOwner))
        {
            OutError = FString::Printf(
                TEXT("Managed function '%s' collides with user-owned Blueprint graph '%s'."),
                *Declaration.ManagedName.ToString(),
                *NameOwner->GetName());
            return false;
        }
        if (!Declaration.bInterfaceFunction && Blueprint.ParentClass != nullptr &&
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
            !FunctionGraphMatches(Blueprint, **Existing, Declaration);
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

bool BuildLifecyclePlan(
    const UBlueprint& Blueprint,
    TConstArrayView<FLifecycleDeclaration> Declarations,
    FSyncPlan& OutPlan,
    FUnifiedScriptBlueprintClassSyncResult& OutResult,
    FString& OutError)
{
    OutPlan.LifecycleBindings.Reset();
    for (const FLifecycleDeclaration& Declaration : Declarations)
    {
        FUnifiedScriptManagedLifecycleBinding Binding;
        Binding.ManagedName = Declaration.ManagedName;
        Binding.HookId = Declaration.Id;
        OutPlan.LifecycleBindings.Add(Declaration.Kind, MoveTemp(Binding));
    }

    const UUnifiedScriptBlueprintGeneratedClass* GeneratedClass =
        Cast<UUnifiedScriptBlueprintGeneratedClass>(Blueprint.GeneratedClass);
    if (!Declarations.IsEmpty() && GeneratedClass == nullptr)
    {
        OutError = FString::Printf(
            TEXT("Generated Blueprint '%s' cannot persist managed lifecycle identities."),
            *Blueprint.GetPathName());
        return false;
    }

    bool bMatches = GeneratedClass != nullptr &&
        GeneratedClass->ManagedLifecycleBindings.Num() ==
            OutPlan.LifecycleBindings.Num();
    if (bMatches)
    {
        for (const TPair<
                 EUnifiedScriptManagedLifecycleHookKind,
                 FUnifiedScriptManagedLifecycleBinding>& Pair :
             OutPlan.LifecycleBindings)
        {
            const FUnifiedScriptManagedLifecycleBinding* Existing =
                GeneratedClass->ManagedLifecycleBindings.Find(Pair.Key);
            if (Existing == nullptr ||
                Existing->ManagedName != Pair.Value.ManagedName ||
                Existing->HookId != Pair.Value.HookId)
            {
                bMatches = false;
                break;
            }
        }
    }

    OutPlan.bLifecycleBindingsChanged = !bMatches;
    OutResult.DeclaredLifecycleHookCount = Declarations.Num();
    OutResult.UpdatedLifecycleHookCount = bMatches ? 0 : Declarations.Num();
    OutResult.bChanged |= !bMatches;
    return true;
}

bool InputBindingMatches(
    const FUnifiedScriptManagedInputActionBinding& Left,
    const FUnifiedScriptManagedInputActionBinding& Right)
{
    return Left.ManagedName == Right.ManagedName &&
        Left.BindingId == Right.BindingId &&
        Left.ActionPath == Right.ActionPath &&
        Left.TriggerEvent == Right.TriggerEvent &&
        Left.ValueType == Right.ValueType &&
        Left.bConsumeInput == Right.bConsumeInput;
}

bool DelegateBindingMatches(
    const FUnifiedScriptManagedDelegateBinding& Left,
    const FUnifiedScriptManagedDelegateBinding& Right)
{
    return Left.ManagedName == Right.ManagedName &&
        Left.BindingId == Right.BindingId &&
        Left.SourceTypePath == Right.SourceTypePath &&
        Left.EventPath == Right.EventPath &&
        Left.Source == Right.Source &&
        Left.SourceMember == Right.SourceMember;
}

bool InterfaceBindingMatches(
    const FUnifiedScriptManagedInterfaceFunctionBinding& Left,
    const FUnifiedScriptManagedInterfaceFunctionBinding& Right)
{
    return Left.InterfacePath == Right.InterfacePath &&
        Left.FunctionName == Right.FunctionName &&
        Left.ManagedName == Right.ManagedName &&
        Left.BindingId == Right.BindingId &&
        Left.FunctionId == Right.FunctionId;
}

template <typename BindingType, typename PredicateType>
bool BindingArraysMatch(
    const TArray<BindingType>& Left,
    const TArray<BindingType>& Right,
    PredicateType&& Predicate)
{
    if (Left.Num() != Right.Num())
    {
        return false;
    }
    for (int32 Index = 0; Index < Left.Num(); ++Index)
    {
        if (!Predicate(Left[Index], Right[Index]))
        {
            return false;
        }
    }
    return true;
}

TArray<FUnifiedScriptManagedInterfaceFunctionBinding> BindingsForInterface(
    const TArray<FUnifiedScriptManagedInterfaceFunctionBinding>& Bindings,
    const FString& InterfacePath)
{
    TArray<FUnifiedScriptManagedInterfaceFunctionBinding> Result;
    for (const FUnifiedScriptManagedInterfaceFunctionBinding& Binding : Bindings)
    {
        if (Binding.InterfacePath == InterfacePath)
        {
            Result.Add(Binding);
        }
    }
    return Result;
}

bool BuildGameplayBindingPlan(
    const UBlueprint& Blueprint,
    TConstArrayView<FInputActionDeclaration> InputDeclarations,
    TConstArrayView<FDelegateBindingDeclaration> DelegateDeclarations,
    TConstArrayView<FInterfaceFunctionDeclaration> InterfaceDeclarations,
    FSyncPlan& OutPlan,
    FUnifiedScriptBlueprintClassSyncResult& OutResult,
    FString& OutError)
{
    OutPlan.InputBindings.Reset();
    OutPlan.InputBindings.Reserve(InputDeclarations.Num());
    for (const FInputActionDeclaration& Declaration : InputDeclarations)
    {
        FUnifiedScriptManagedInputActionBinding& Binding =
            OutPlan.InputBindings.AddDefaulted_GetRef();
        Binding.ManagedName = Declaration.ManagedName;
        Binding.BindingId = Declaration.BindingId;
        Binding.ActionPath = Declaration.ActionPath;
        Binding.TriggerEvent = Declaration.TriggerEvent;
        Binding.ValueType = Declaration.ValueType;
        Binding.bConsumeInput = Declaration.bConsumeInput;
    }

    OutPlan.DelegateBindings.Reset();
    OutPlan.DelegateBindings.Reserve(DelegateDeclarations.Num());
    for (const FDelegateBindingDeclaration& Declaration : DelegateDeclarations)
    {
        FUnifiedScriptManagedDelegateBinding& Binding =
            OutPlan.DelegateBindings.AddDefaulted_GetRef();
        Binding.ManagedName = Declaration.ManagedName;
        Binding.BindingId = Declaration.BindingId;
        Binding.SourceTypePath = Declaration.SourceTypePath;
        Binding.EventPath = Declaration.EventPath;
        Binding.Source = Declaration.Source;
        Binding.SourceMember = Declaration.SourceMember;
    }

    OutPlan.InterfaceBindings.Reset();
    OutPlan.InterfaceBindings.Reserve(InterfaceDeclarations.Num());
    for (const FInterfaceFunctionDeclaration& Declaration : InterfaceDeclarations)
    {
        FUnifiedScriptManagedInterfaceFunctionBinding& Binding =
            OutPlan.InterfaceBindings.AddDefaulted_GetRef();
        Binding.InterfacePath = Declaration.InterfacePath;
        Binding.FunctionName = Declaration.FunctionName;
        Binding.ManagedName = Declaration.ManagedName;
        Binding.BindingId = Declaration.BindingId;
        Binding.FunctionId = Declaration.FunctionId;
    }

    const UUnifiedScriptBlueprintGeneratedClass* GeneratedClass =
        Cast<UUnifiedScriptBlueprintGeneratedClass>(Blueprint.GeneratedClass);
    if ((!InputDeclarations.IsEmpty() || !DelegateDeclarations.IsEmpty() ||
         !InterfaceDeclarations.IsEmpty()) && GeneratedClass == nullptr)
    {
        OutError = FString::Printf(
            TEXT("Generated Blueprint '%s' cannot persist managed gameplay bindings."),
            *Blueprint.GetPathName());
        return false;
    }

    const TArray<FUnifiedScriptManagedInputActionBinding> EmptyInputs;
    const TArray<FUnifiedScriptManagedDelegateBinding> EmptyDelegates;
    const TArray<FUnifiedScriptManagedInterfaceFunctionBinding> EmptyInterfaces;
    const TArray<FUnifiedScriptManagedInputActionBinding>& PreviousInputs =
        GeneratedClass != nullptr
        ? GeneratedClass->ManagedInputActionBindings
        : EmptyInputs;
    const TArray<FUnifiedScriptManagedDelegateBinding>& PreviousDelegates =
        GeneratedClass != nullptr
        ? GeneratedClass->ManagedDelegateBindings
        : EmptyDelegates;
    const TArray<FUnifiedScriptManagedInterfaceFunctionBinding>& PreviousInterfaces =
        GeneratedClass != nullptr
        ? GeneratedClass->ManagedInterfaceBindings
        : EmptyInterfaces;

    const bool bInputsMatch = BindingArraysMatch(
        PreviousInputs,
        OutPlan.InputBindings,
        InputBindingMatches);
    const bool bDelegatesMatch = BindingArraysMatch(
        PreviousDelegates,
        OutPlan.DelegateBindings,
        DelegateBindingMatches);
    const bool bInterfacesMatch = BindingArraysMatch(
        PreviousInterfaces,
        OutPlan.InterfaceBindings,
        InterfaceBindingMatches);

    TSet<FString> ExistingInterfacePaths;
    for (const FBPInterfaceDescription& Existing : Blueprint.ImplementedInterfaces)
    {
        if (const UClass* InterfaceClass = Existing.Interface.Get())
        {
            ExistingInterfacePaths.Add(InterfaceClass->GetPathName());
        }
    }
    TSet<FString> PreviousInterfacePaths;
    for (const FUnifiedScriptManagedInterfaceFunctionBinding& Binding : PreviousInterfaces)
    {
        PreviousInterfacePaths.Add(Binding.InterfacePath);
    }
    TSet<FString> ExpectedInterfacePaths;
    for (const FUnifiedScriptManagedInterfaceFunctionBinding& Binding :
         OutPlan.InterfaceBindings)
    {
        ExpectedInterfacePaths.Add(Binding.InterfacePath);
    }

    TSet<FString> AllManagedInterfacePaths = PreviousInterfacePaths;
    AllManagedInterfacePaths.Append(ExpectedInterfacePaths);
    for (const FString& InterfacePath : AllManagedInterfacePaths)
    {
        const bool bWasGenerated = PreviousInterfacePaths.Contains(InterfacePath);
        const bool bExpected = ExpectedInterfacePaths.Contains(InterfacePath);
        const bool bExists = ExistingInterfacePaths.Contains(InterfacePath);
        if (bExpected && !bWasGenerated && bExists)
        {
            OutError = FString::Printf(
                TEXT("Managed interface '%s' collides with a user-owned Blueprint interface on '%s'."),
                *InterfacePath,
                *Blueprint.GetPathName());
            return false;
        }

        const TArray<FUnifiedScriptManagedInterfaceFunctionBinding> PreviousForPath =
            BindingsForInterface(PreviousInterfaces, InterfacePath);
        const TArray<FUnifiedScriptManagedInterfaceFunctionBinding> ExpectedForPath =
            BindingsForInterface(OutPlan.InterfaceBindings, InterfacePath);
        const bool bRouteMatches = BindingArraysMatch(
            PreviousForPath,
            ExpectedForPath,
            InterfaceBindingMatches);
        const bool bRefreshOwnedInterface =
            bWasGenerated && bExpected && !bRouteMatches;
        if (bExists && (bRefreshOwnedInterface || (bWasGenerated && !bExpected)))
        {
            OutPlan.InterfacePathsToRemove.Add(InterfacePath);
        }
        if (bExpected && (!bExists || bRefreshOwnedInterface))
        {
            OutPlan.InterfacePathsToAdd.Add(InterfacePath);
        }
    }
    OutPlan.InterfacePathsToRemove.Sort();
    OutPlan.InterfacePathsToAdd.Sort();

    OutPlan.bGameplayBindingsChanged = !bInputsMatch || !bDelegatesMatch ||
        !bInterfacesMatch || !OutPlan.InterfacePathsToRemove.IsEmpty() ||
        !OutPlan.InterfacePathsToAdd.IsEmpty();
    OutResult.DeclaredInputBindingCount = InputDeclarations.Num();
    OutResult.UpdatedInputBindingCount = bInputsMatch ? 0 : InputDeclarations.Num();
    OutResult.DeclaredDelegateBindingCount = DelegateDeclarations.Num();
    OutResult.UpdatedDelegateBindingCount =
        bDelegatesMatch ? 0 : DelegateDeclarations.Num();
    OutResult.DeclaredInterfaceBindingCount = InterfaceDeclarations.Num();
    OutResult.UpdatedInterfaceBindingCount =
        bInterfacesMatch ? 0 : InterfaceDeclarations.Num();
    for (FPlannedFunction& Planned : OutPlan.Functions)
    {
        if (!Planned.Declaration.bInterfaceFunction || Planned.bNeedsUpdate)
        {
            continue;
        }
        const bool bInterfaceWillBeRecreated =
            Planned.Declaration.InterfacePaths.ContainsByPredicate(
                [&OutPlan](const FString& InterfacePath)
                {
                    return OutPlan.InterfacePathsToAdd.Contains(InterfacePath);
                });
        if (bInterfaceWillBeRecreated)
        {
            Planned.bNeedsUpdate = true;
            ++OutResult.UpdatedFunctionCount;
        }
    }
    OutResult.bChanged |= OutPlan.bGameplayBindingsChanged;
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
        FUNC_Public | FUNC_BlueprintCallable | UnifiedScriptRpcFlags(Declaration.RpcTarget, Declaration.bReliable) |
        (Declaration.bIsStatic ? FUNC_Static : FUNC_None) |
        (Declaration.bBlueprintEvent
            ? static_cast<int32>(FUNC_Event | FUNC_BlueprintEvent)
            : FUNC_None));
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
    Entry->MetaData.SetMetaData(
        BlueprintEventKey,
        FString(Declaration.bBlueprintEvent ? TEXT("true") : TEXT("false")));
    Entry->MetaData.SetMetaData(
        BlueprintEventSuperModeKey,
        FString(Declaration.bBlueprintEvent ? TEXT("explicit") : TEXT("none")));
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
        UK2Node_EditablePinBase* PinOwner = Parameter.Direction ==
            EUnifiedScriptManagedParameterDirection::Output
            ? static_cast<UK2Node_EditablePinBase*>(Result)
            : static_cast<UK2Node_EditablePinBase*>(Entry);
        const EEdGraphPinDirection PinDirection = Parameter.Direction ==
            EUnifiedScriptManagedParameterDirection::Output
            ? EGPD_Input
            : EGPD_Output;
        if (PinOwner->CreateUserDefinedPin(
                Parameter.Name,
                Parameter.PinType,
                PinDirection,
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

    for (const FString& InterfacePath : Plan.InterfacePathsToRemove)
    {
        UClass* InterfaceClass = LoadObject<UClass>(nullptr, *InterfacePath);
        if (InterfaceClass == nullptr ||
            !InterfaceClass->HasAnyClassFlags(CLASS_Interface))
        {
            OutError = FString::Printf(
                TEXT("Cannot remove generated interface '%s' because its native class is unavailable."),
                *InterfacePath);
            return false;
        }
        FBlueprintEditorUtils::RemoveInterface(
            &Blueprint,
            InterfaceClass->GetClassPathName(),
            false);
    }
    for (const FString& InterfacePath : Plan.InterfacePathsToAdd)
    {
        UClass* InterfaceClass = LoadObject<UClass>(nullptr, *InterfacePath);
        if (InterfaceClass == nullptr ||
            !InterfaceClass->HasAnyClassFlags(CLASS_Interface))
        {
            OutError = FString::Printf(
                TEXT("Cannot implement generated interface '%s' because its native class is unavailable."),
                *InterfacePath);
            return false;
        }
        if (!FBlueprintEditorUtils::ImplementNewInterface(
                &Blueprint,
                InterfaceClass->GetClassPathName()))
        {
            OutError = FString::Printf(
                TEXT("Unreal could not implement generated interface '%s' on '%s'."),
                *InterfacePath,
                *Blueprint.GetPathName());
            return false;
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
        Variable.RepNotifyFunc = Planned.Declaration.RepNotifyFunc;
        Variable.ReplicationCondition = Planned.Declaration.ReplicationCondition;
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
        UEdGraph* Graph = Planned.Declaration.bInterfaceFunction
            ? FindFunctionGraphByName(Blueprint, Planned.Declaration.InternalName)
            : (Planned.bExisting
                ? FindManagedFunctionGraphById(Blueprint, Planned.Declaration.Id)
                : nullptr);
        if ((Planned.bExisting || Planned.Declaration.bInterfaceFunction) &&
            Graph == nullptr)
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
    if ((!Plan.Functions.IsEmpty() || !Plan.LifecycleBindings.IsEmpty() ||
         !Plan.InputBindings.IsEmpty() || !Plan.DelegateBindings.IsEmpty() ||
         !Plan.InterfaceBindings.IsEmpty()) &&
        GeneratedClass == nullptr)
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
        GeneratedClass->ManagedLifecycleBindings = Plan.LifecycleBindings;
        GeneratedClass->ManagedInputActionBindings = Plan.InputBindings;
        GeneratedClass->ManagedDelegateBindings = Plan.DelegateBindings;
        GeneratedClass->ManagedInterfaceBindings = Plan.InterfaceBindings;
        GeneratedClass->ManagedFunctionBindings.Reset();
        for (const FPlannedFunction& Planned : Plan.Functions)
        {
            FUnifiedScriptManagedFunctionBinding Binding;
            Binding.ManagedName = Planned.Declaration.ManagedName.ToString();
            Binding.FunctionId = Planned.Declaration.Id;
            Binding.AbiVersion = 3;
            Binding.bStatic = Planned.Declaration.bIsStatic;
            Binding.RpcTarget = Planned.Declaration.RpcTarget;
            Binding.bReliable = Planned.Declaration.bReliable;
            Binding.RepNotifyProperty = Planned.Declaration.RepNotifyProperty;
            Binding.bBlueprintEvent = Planned.Declaration.bBlueprintEvent;
            Binding.bInterfaceFunction =
                Planned.Declaration.bInterfaceFunction;
            Binding.BlueprintEventSuperMode =
                Planned.Declaration.BlueprintEventSuperMode;
            Binding.ReturnType = MakeRuntimeValueTypeBinding(
                Planned.Declaration.ReturnValueType);
            Binding.ParameterTypes.Reserve(Planned.Declaration.Parameters.Num());
            Binding.ParameterNames.Reserve(Planned.Declaration.Parameters.Num());
            Binding.ParameterDirections.Reserve(
                Planned.Declaration.Parameters.Num());
            for (const FFunctionParameterDeclaration& Parameter :
                 Planned.Declaration.Parameters)
            {
                Binding.ParameterTypes.Add(
                    MakeRuntimeValueTypeBinding(Parameter.ValueType));
                Binding.ParameterNames.Add(Parameter.Name);
                Binding.ParameterDirections.Add(Parameter.Direction);
            }
            GeneratedClass->ManagedFunctionBindings.Add(
                Planned.Declaration.InternalName,
                MoveTemp(Binding));
        }
        GeneratedClass->BindManagedFunctions();
        const bool bNetworked = Plan.States.ContainsByPredicate([](const FPlannedState& S) { return (S.Declaration.PropertyFlags & CPF_Net) != 0; }) ||
            Plan.Functions.ContainsByPredicate([](const FPlannedFunction& F) { return F.Declaration.RpcTarget != EUnifiedScriptRpcTarget::None; });
        if (bNetworked)
        {
            if (AActor* Actor = Cast<AActor>(GeneratedClass->GetDefaultObject())) Actor->SetReplicates(true);
            if (UActorComponent* Component = Cast<UActorComponent>(GeneratedClass->GetDefaultObject())) Component->SetIsReplicated(true);
        }
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
    TArray<FLifecycleDeclaration> LifecycleDeclarations;
    TArray<FInputActionDeclaration> InputDeclarations;
    TArray<FDelegateBindingDeclaration> DelegateDeclarations;
    TArray<FInterfaceFunctionDeclaration> InterfaceDeclarations;
    if (!ParseDeclaration(
            ScriptAsset,
            DeclarationJson,
            StateDeclarations,
            FunctionDeclarations,
            LifecycleDeclarations,
            InputDeclarations,
            DelegateDeclarations,
            InterfaceDeclarations,
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
        !BuildLifecyclePlan(
            Blueprint,
            LifecycleDeclarations,
            Plan,
            OutResult,
            OutError) ||
        !BuildGameplayBindingPlan(
            Blueprint,
            InputDeclarations,
            DelegateDeclarations,
            InterfaceDeclarations,
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
