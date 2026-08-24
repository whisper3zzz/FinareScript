/**
 * @Copyright: 2026 Tencent Inc. All rights reserved. reserved.
 * @Author: whispersong
 * @Date: 2026-08-24 17:54:22
 * @LastEditors: whispersong
 * @LastEditTime: 2026-08-24 19:40:18
 * @FilePath: \Plugins\UEUnifiedScript\Source\UEUnifiedScriptEditor\Private\UnifiedScriptAuthoringSurface.cpp
 * @Description:
 */
#include "UnifiedScriptAuthoringSurface.h"

#include "Dom/JsonObject.h"
#include "HAL/FileManager.h"
#include "Interfaces/IPluginManager.h"
#include "Misc/FileHelper.h"
#include "Misc/Paths.h"
#include "Serialization/JsonReader.h"
#include "Serialization/JsonSerializer.h"

namespace UE::UnifiedScript::AuthoringSurface::Private
{
constexpr TCHAR SchemaVersion[] = TEXT("unifiedscript.authoring-surface/v0.3");

bool IsSemanticId(const FString& Value)
{
    if (!Value.StartsWith(TEXT("us_")) || Value.Len() != 35)
    {
        return false;
    }
    for (int32 Index = 3; Index < Value.Len(); ++Index)
    {
        if (!FChar::IsHexDigit(Value[Index]))
        {
            return false;
        }
    }
    return true;
}

bool ReadRequiredString(
    const TSharedPtr<FJsonObject>& Object,
    const TCHAR* Field,
    FString& OutValue,
    FString& OutError)
{
    if (!Object.IsValid() ||
        !Object->TryGetStringField(Field, OutValue) ||
        OutValue.IsEmpty())
    {
        OutError = FString::Printf(TEXT("Authoring Surface requires non-empty '%s'."), Field);
        return false;
    }
    return true;
}

bool ReadRequiredId(
    const TSharedPtr<FJsonObject>& Object,
    const TCHAR* Field,
    FString& OutValue,
    FString& OutError)
{
    if (!ReadRequiredString(Object, Field, OutValue, OutError) || !IsSemanticId(OutValue))
    {
        OutError = FString::Printf(TEXT("Authoring Surface field '%s' is not a semantic ID."), Field);
        return false;
    }
    return true;
}

bool ReadOptionalId(
    const TSharedPtr<FJsonObject>& Object,
    const TCHAR* Field,
    FString& OutValue,
    FString& OutError)
{
    OutValue.Reset();
    if (!Object->HasField(Field) || Object->HasTypedField<EJson::Null>(Field))
    {
        return true;
    }
    return ReadRequiredId(Object, Field, OutValue, OutError);
}

bool ReadOptionalString(
    const TSharedPtr<FJsonObject>& Object,
    const TCHAR* Field,
    FString& OutValue,
    FString& OutError)
{
    OutValue.Reset();
    if (!Object->HasField(Field) || Object->HasTypedField<EJson::Null>(Field))
    {
        return true;
    }
    if (!Object->TryGetStringField(Field, OutValue) || OutValue.IsEmpty())
    {
        OutError = FString::Printf(TEXT("Authoring Surface optional field '%s' must be a non-empty string."), Field);
        return false;
    }
    return true;
}

bool ReadSpan(
    const TSharedPtr<FJsonObject>& Owner,
    FUnifiedScriptAuthoringSpan& OutSpan,
    FString& OutError)
{
    const TSharedPtr<FJsonObject>* SpanObject = nullptr;
    if (!Owner.IsValid() ||
        !Owner->TryGetObjectField(TEXT("span"), SpanObject) ||
        SpanObject == nullptr ||
        !ReadRequiredString(*SpanObject, TEXT("filePath"), OutSpan.FilePath, OutError))
    {
        if (OutError.IsEmpty())
        {
            OutError = TEXT("Authoring Surface item requires a source span.");
        }
        return false;
    }

    const TSharedPtr<FJsonObject>* Start = nullptr;
    const TSharedPtr<FJsonObject>* End = nullptr;
    double StartLine = 0;
    double StartColumn = 0;
    double EndLine = 0;
    double EndColumn = 0;
    if (!(*SpanObject)->TryGetObjectField(TEXT("start"), Start) || Start == nullptr ||
        !(*SpanObject)->TryGetObjectField(TEXT("end"), End) || End == nullptr ||
        !(*Start)->TryGetNumberField(TEXT("line"), StartLine) ||
        !(*Start)->TryGetNumberField(TEXT("column"), StartColumn) ||
        !(*End)->TryGetNumberField(TEXT("line"), EndLine) ||
        !(*End)->TryGetNumberField(TEXT("column"), EndColumn))
    {
        OutError = TEXT("Authoring Surface span requires numeric start/end positions.");
        return false;
    }

    OutSpan.StartLine = static_cast<int32>(StartLine);
    OutSpan.StartColumn = static_cast<int32>(StartColumn);
    OutSpan.EndLine = static_cast<int32>(EndLine);
    OutSpan.EndColumn = static_cast<int32>(EndColumn);
    if (StartLine != OutSpan.StartLine || StartColumn != OutSpan.StartColumn ||
        EndLine != OutSpan.EndLine || EndColumn != OutSpan.EndColumn || !OutSpan.IsValid())
    {
        OutError = TEXT("Authoring Surface contains an invalid source span.");
        return false;
    }
    return true;
}

bool ReadIdArray(
    const TSharedPtr<FJsonObject>& Object,
    const TCHAR* Field,
    TArray<FString>& OutIds,
    FString& OutError)
{
    const TArray<TSharedPtr<FJsonValue>>* Values = nullptr;
    if (!Object->TryGetArrayField(Field, Values) || Values == nullptr)
    {
        OutError = FString::Printf(TEXT("Authoring Surface requires '%s' array."), Field);
        return false;
    }

    TSet<FString> Seen;
    for (const TSharedPtr<FJsonValue>& Value : *Values)
    {
        FString Id;
        if (!Value.IsValid() || !Value->TryGetString(Id) ||
            !IsSemanticId(Id) || Seen.Contains(Id))
        {
            OutError = FString::Printf(TEXT("Authoring Surface '%s' contains invalid or duplicate IDs."), Field);
            return false;
        }
        Seen.Add(Id);
        OutIds.Add(MoveTemp(Id));
    }
    return true;
}

FString JsonScalarToString(const TSharedPtr<FJsonValue>& Value)
{
    if (!Value.IsValid() || Value->IsNull())
    {
        return TEXT("null");
    }
    switch (Value->Type)
    {
    case EJson::String:
        return FString::Printf(TEXT("\"%s\""), *Value->AsString());
    case EJson::Number:
        return FString::SanitizeFloat(Value->AsNumber());
    case EJson::Boolean:
        return Value->AsBool() ? TEXT("true") : TEXT("false");
    default:
        return TEXT("<complex>");
    }
}

bool ReadState(
    const TSharedPtr<FJsonObject>& Object,
    FUnifiedScriptAuthoringState& OutState,
    FString& OutError)
{
    if (!ReadRequiredId(Object, TEXT("id"), OutState.Id, OutError) ||
        !ReadRequiredString(Object, TEXT("name"), OutState.Name, OutError) ||
        !ReadRequiredString(Object, TEXT("typeDisplayName"), OutState.TypeDisplayName, OutError) ||
        !ReadRequiredString(Object, TEXT("typeMetadataName"), OutState.TypeMetadataName, OutError) ||
        !ReadRequiredString(Object, TEXT("typeKind"), OutState.TypeKind, OutError) ||
        !ReadSpan(Object, OutState.Span, OutError))
    {
        return false;
    }

    OutState.DefaultValue = JsonScalarToString(Object->TryGetField(TEXT("defaultValue")));
    const TSharedPtr<FJsonObject>* Exposure = nullptr;
    if (!Object->TryGetObjectField(TEXT("exposure"), Exposure) || Exposure == nullptr)
    {
        OutError = FString::Printf(TEXT("State '%s' requires exposure metadata."), *OutState.Name);
        return false;
    }
    (*Exposure)->TryGetBoolField(TEXT("instanceEditable"), OutState.Exposure.bInstanceEditable);
    (*Exposure)->TryGetBoolField(TEXT("saveGame"), OutState.Exposure.bSaveGame);
    (*Exposure)->TryGetBoolField(TEXT("replicated"), OutState.Exposure.bReplicated);
    (*Exposure)->TryGetStringField(TEXT("category"), OutState.Exposure.Category);
    return true;
}

bool ReadEdit(
    const TSharedPtr<FJsonObject>& Object,
    FUnifiedScriptAuthoringEdit& OutEdit,
    FString& OutError)
{
    if (!ReadRequiredId(Object, TEXT("id"), OutEdit.Id, OutError) ||
        !ReadRequiredString(Object, TEXT("kind"), OutEdit.Kind, OutError) ||
        !ReadRequiredString(Object, TEXT("label"), OutEdit.Label, OutError) ||
        !ReadRequiredString(Object, TEXT("operation"), OutEdit.Operation, OutError) ||
        !ReadRequiredId(Object, TEXT("targetNodeId"), OutEdit.TargetNodeId, OutError) ||
        !Object->TryGetStringField(TEXT("currentValue"), OutEdit.CurrentValue))
    {
        return false;
    }

    Object->TryGetStringField(TEXT("expectedValueText"), OutEdit.ExpectedValueText);
    Object->TryGetStringField(TEXT("expectedOperator"), OutEdit.ExpectedOperator);
    Object->TryGetStringField(TEXT("expectedResultTypeId"), OutEdit.ExpectedResultTypeId);
    Object->TryGetStringField(TEXT("expectedOperandTypeId"), OutEdit.ExpectedOperandTypeId);
    Object->TryGetStringField(TEXT("expectedRightOperandTypeId"), OutEdit.ExpectedRightOperandTypeId);
    const bool bLiteral = OutEdit.Kind == TEXT("literal") &&
        OutEdit.Operation == TEXT("replaceLiteral") && !OutEdit.ExpectedValueText.IsEmpty();
    const bool bOperator = OutEdit.Kind == TEXT("operator") &&
        OutEdit.Operation == TEXT("changeOperator") && !OutEdit.ExpectedOperator.IsEmpty() &&
        IsSemanticId(OutEdit.ExpectedResultTypeId) &&
        IsSemanticId(OutEdit.ExpectedOperandTypeId) &&
        (OutEdit.ExpectedRightOperandTypeId.IsEmpty() ||
            IsSemanticId(OutEdit.ExpectedRightOperandTypeId));
    if (!bLiteral && !bOperator)
    {
        OutError = FString::Printf(TEXT("Edit capability '%s' has an incomplete typed contract."), *OutEdit.Id);
        return false;
    }

    const TArray<TSharedPtr<FJsonValue>>* Options = nullptr;
    if (!Object->TryGetArrayField(TEXT("options"), Options) || Options == nullptr)
    {
        OutError = FString::Printf(TEXT("Edit capability '%s' requires options."), *OutEdit.Id);
        return false;
    }
    for (const TSharedPtr<FJsonValue>& Option : *Options)
    {
        FString Text;
        if (!Option.IsValid() || !Option->TryGetString(Text))
        {
            OutError = FString::Printf(TEXT("Edit capability '%s' has a non-string option."), *OutEdit.Id);
            return false;
        }
        OutEdit.Options.Add(MoveTemp(Text));
    }
    return true;
}

bool ReadPort(
    const TSharedPtr<FJsonObject>& Object,
    FUnifiedScriptAuthoringPort& OutPort,
    FString& OutError)
{
    if (!ReadRequiredString(Object, TEXT("name"), OutPort.Name, OutError) ||
        !ReadRequiredString(Object, TEXT("direction"), OutPort.Direction, OutError) ||
        !ReadRequiredString(Object, TEXT("kind"), OutPort.Kind, OutError) ||
        !ReadOptionalId(Object, TEXT("typeId"), OutPort.TypeId, OutError))
    {
        return false;
    }

    OutPort.TypeDisplayName.Reset();
    if (Object->HasField(TEXT("typeDisplayName")) &&
        !Object->HasTypedField<EJson::Null>(TEXT("typeDisplayName")) &&
        !Object->TryGetStringField(TEXT("typeDisplayName"), OutPort.TypeDisplayName))
    {
        OutError = FString::Printf(TEXT("Port '%s' has an invalid type display name."), *OutPort.Name);
        return false;
    }

    const bool bValidDirection = OutPort.Direction == TEXT("input") ||
        OutPort.Direction == TEXT("output");
    const bool bControl = OutPort.Kind == TEXT("control") && OutPort.TypeId.IsEmpty() &&
        OutPort.TypeDisplayName.IsEmpty();
    const bool bKnownData = OutPort.Kind == TEXT("data") &&
        IsSemanticId(OutPort.TypeId) && !OutPort.TypeDisplayName.IsEmpty();
    const bool bUnknownData = OutPort.Kind == TEXT("data") && OutPort.TypeId.IsEmpty() &&
        OutPort.TypeDisplayName.IsEmpty();
    if (!bValidDirection || (!bControl && !bKnownData && !bUnknownData))
    {
        OutError = FString::Printf(TEXT("Port '%s' has an invalid direction/kind/type contract."), *OutPort.Name);
        return false;
    }
    return true;
}

bool ReadEdge(
    const TSharedPtr<FJsonObject>& Object,
    FUnifiedScriptAuthoringEdge& OutEdge,
    FString& OutError)
{
    if (!ReadRequiredId(Object, TEXT("id"), OutEdge.Id, OutError) ||
        !ReadRequiredString(Object, TEXT("kind"), OutEdge.Kind, OutError) ||
        !ReadRequiredId(Object, TEXT("fromNode"), OutEdge.FromNode, OutError) ||
        !ReadRequiredString(Object, TEXT("fromPort"), OutEdge.FromPort, OutError) ||
        !ReadRequiredId(Object, TEXT("toNode"), OutEdge.ToNode, OutError) ||
        !ReadRequiredString(Object, TEXT("toPort"), OutEdge.ToPort, OutError))
    {
        return false;
    }
    if (OutEdge.Kind != TEXT("control") && OutEdge.Kind != TEXT("data"))
    {
        OutError = FString::Printf(TEXT("Edge '%s' has invalid kind '%s'."), *OutEdge.Id, *OutEdge.Kind);
        return false;
    }
    return true;
}

bool ReadAction(
    const TSharedPtr<FJsonObject>& Object,
    FUnifiedScriptAuthoringAction& OutAction,
    FString& OutError)
{
    if (!ReadRequiredId(Object, TEXT("id"), OutAction.Id, OutError) ||
        !ReadRequiredString(Object, TEXT("graphKind"), OutAction.GraphKind, OutError) ||
        !ReadRequiredString(Object, TEXT("category"), OutAction.Category, OutError) ||
        !ReadRequiredString(Object, TEXT("title"), OutAction.Title, OutError) ||
        !Object->TryGetStringField(TEXT("summary"), OutAction.Summary) ||
        !ReadSpan(Object, OutAction.Span, OutError) ||
        !ReadRequiredId(Object, TEXT("regionId"), OutAction.RegionId, OutError) ||
        !ReadOptionalId(Object, TEXT("debugTargetId"), OutAction.DebugTargetId, OutError) ||
        !ReadIdArray(Object, TEXT("declarationReferenceIds"), OutAction.DeclarationReferenceIds, OutError))
    {
        return false;
    }

    const TArray<TSharedPtr<FJsonValue>>* Edits = nullptr;
    if (!Object->TryGetArrayField(TEXT("editCapabilities"), Edits) || Edits == nullptr)
    {
        OutError = FString::Printf(TEXT("Action '%s' requires editCapabilities."), *OutAction.Id);
        return false;
    }
    TSet<FString> EditIds;
    for (const TSharedPtr<FJsonValue>& Value : *Edits)
    {
        FUnifiedScriptAuthoringEdit Edit;
        if (!Value.IsValid() || !ReadEdit(Value->AsObject(), Edit, OutError) || EditIds.Contains(Edit.Id))
        {
            return false;
        }
        EditIds.Add(Edit.Id);
        OutAction.EditCapabilities.Add(MoveTemp(Edit));
    }

    const TArray<TSharedPtr<FJsonValue>>* Ports = nullptr;
    if (!Object->TryGetArrayField(TEXT("ports"), Ports) || Ports == nullptr)
    {
        OutError = FString::Printf(TEXT("Action '%s' requires ports."), *OutAction.Id);
        return false;
    }
    TSet<FString> PortKeys;
    for (const TSharedPtr<FJsonValue>& Value : *Ports)
    {
        FUnifiedScriptAuthoringPort Port;
        if (!Value.IsValid() || !ReadPort(Value->AsObject(), Port, OutError))
        {
            return false;
        }
        const FString Key = Port.Direction + TEXT(":") + Port.Name;
        if (PortKeys.Contains(Key))
        {
            OutError = FString::Printf(TEXT("Action '%s' has duplicate port '%s'."), *OutAction.Id, *Key);
            return false;
        }
        PortKeys.Add(Key);
        OutAction.Ports.Add(MoveTemp(Port));
    }
    return true;
}

bool ReadRegion(
    const TSharedPtr<FJsonObject>& Object,
    FUnifiedScriptAuthoringRegion& OutRegion,
    FString& OutError)
{
    if (!ReadRequiredId(Object, TEXT("id"), OutRegion.Id, OutError) ||
        !ReadRequiredString(Object, TEXT("kind"), OutRegion.Kind, OutError) ||
        !ReadOptionalId(Object, TEXT("parentRegionId"), OutRegion.ParentRegionId, OutError) ||
        !ReadRequiredId(Object, TEXT("sourceNodeId"), OutRegion.SourceNodeId, OutError) ||
        !ReadSpan(Object, OutRegion.Span, OutError) ||
        !Object->TryGetBoolField(TEXT("isImplicit"), OutRegion.bImplicit) ||
        !ReadOptionalId(Object, TEXT("anchorActionId"), OutRegion.AnchorActionId, OutError) ||
        !ReadIdArray(Object, TEXT("directStatementIds"), OutRegion.DirectStatementIds, OutError) ||
        !ReadIdArray(Object, TEXT("directActionIds"), OutRegion.DirectActionIds, OutError) ||
        !ReadIdArray(Object, TEXT("childRegionIds"), OutRegion.ChildRegionIds, OutError))
    {
        return false;
    }
    return true;
}

bool ValidateFunction(
    const FUnifiedScriptAuthoringFunction& Function,
    FString& OutError)
{
    TMap<FString, const FUnifiedScriptAuthoringRegion*> Regions;
    TMap<FString, const FUnifiedScriptAuthoringAction*> Actions;
    TMap<FString, const FUnifiedScriptAuthoringPort*> InputPorts;
    TMap<FString, const FUnifiedScriptAuthoringPort*> OutputPorts;
    for (const FUnifiedScriptAuthoringRegion& Region : Function.Regions)
    {
        if (Regions.Contains(Region.Id))
        {
            OutError = FString::Printf(TEXT("Function '%s' has duplicate region '%s'."), *Function.Name, *Region.Id);
            return false;
        }
        Regions.Add(Region.Id, &Region);
    }
    for (const FUnifiedScriptAuthoringAction& Action : Function.Actions)
    {
        if (Actions.Contains(Action.Id))
        {
            OutError = FString::Printf(TEXT("Function '%s' has duplicate action '%s'."), *Function.Name, *Action.Id);
            return false;
        }
        Actions.Add(Action.Id, &Action);
        for (const FUnifiedScriptAuthoringPort& Port : Action.Ports)
        {
            const FString Key = Action.Id + TEXT("|") + Port.Name;
            TMap<FString, const FUnifiedScriptAuthoringPort*>& Ports =
                Port.IsInput() ? InputPorts : OutputPorts;
            if (Ports.Contains(Key))
            {
                OutError = FString::Printf(TEXT("Action '%s' has ambiguous port '%s'."), *Action.Id, *Port.Name);
                return false;
            }
            Ports.Add(Key, &Port);
        }
    }

    const FUnifiedScriptAuthoringRegion* const* Root = Regions.Find(Function.RootRegionId);
    if (Root == nullptr || !(*Root)->ParentRegionId.IsEmpty())
    {
        OutError = FString::Printf(TEXT("Function '%s' has no valid root region."), *Function.Name);
        return false;
    }

    TSet<FString> OwnedActions;
    TSet<FString> OwnedChildren;
    for (const FUnifiedScriptAuthoringRegion& Region : Function.Regions)
    {
        if (Region.Id != Function.RootRegionId && !Regions.Contains(Region.ParentRegionId))
        {
            OutError = FString::Printf(TEXT("Region '%s' has no parent."), *Region.Id);
            return false;
        }
        for (const FString& ActionId : Region.DirectActionIds)
        {
            const FUnifiedScriptAuthoringAction* const* Action = Actions.Find(ActionId);
            if (Action == nullptr || (*Action)->RegionId != Region.Id || OwnedActions.Contains(ActionId))
            {
                OutError = FString::Printf(TEXT("Region '%s' has invalid action ownership."), *Region.Id);
                return false;
            }
            OwnedActions.Add(ActionId);
        }
        TSet<FString> RegionStatements;
        for (const FString& StatementId : Region.DirectStatementIds)
        {
            if (!Actions.Contains(StatementId) || RegionStatements.Contains(StatementId))
            {
                OutError = FString::Printf(TEXT("Region '%s' has invalid statement order."), *Region.Id);
                return false;
            }
            RegionStatements.Add(StatementId);
        }
        for (const FString& ChildId : Region.ChildRegionIds)
        {
            const FUnifiedScriptAuthoringRegion* const* Child = Regions.Find(ChildId);
            if (Child == nullptr || (*Child)->ParentRegionId != Region.Id || OwnedChildren.Contains(ChildId))
            {
                OutError = FString::Printf(TEXT("Region '%s' has invalid child ownership."), *Region.Id);
                return false;
            }
            OwnedChildren.Add(ChildId);
        }
    }
    if (OwnedActions.Num() != Actions.Num() || OwnedChildren.Num() != Regions.Num() - 1)
    {
        OutError = FString::Printf(TEXT("Function '%s' contains orphaned actions or regions."), *Function.Name);
        return false;
    }

    TSet<FString> EdgeIds;
    TSet<FString> DataInputs;
    for (const FUnifiedScriptAuthoringEdge& Edge : Function.Edges)
    {
        const FString FromKey = Edge.FromNode + TEXT("|") + Edge.FromPort;
        const FString ToKey = Edge.ToNode + TEXT("|") + Edge.ToPort;
        const FUnifiedScriptAuthoringPort* const* Output = OutputPorts.Find(FromKey);
        const FUnifiedScriptAuthoringPort* const* Input = InputPorts.Find(ToKey);
        const bool bControl = Edge.Kind == TEXT("control") && Output != nullptr && Input != nullptr &&
            (*Output)->IsControl() && (*Input)->IsControl();
        const bool bMatchingDataTypes = Output != nullptr && Input != nullptr &&
            (*Output)->TypeId == (*Input)->TypeId &&
            (*Output)->TypeDisplayName == (*Input)->TypeDisplayName;
        const bool bData = Edge.Kind == TEXT("data") && Output != nullptr && Input != nullptr &&
            !(*Output)->IsControl() && !(*Input)->IsControl() && bMatchingDataTypes &&
            !DataInputs.Contains(ToKey);
        if (EdgeIds.Contains(Edge.Id) || (!bControl && !bData))
        {
            OutError = FString::Printf(TEXT("Function '%s' has invalid edge '%s'."), *Function.Name, *Edge.Id);
            return false;
        }
        EdgeIds.Add(Edge.Id);
        if (bData)
        {
            DataInputs.Add(ToKey);
        }
    }
    return true;
}

bool ReadFunction(
    const TSharedPtr<FJsonObject>& Object,
    FUnifiedScriptAuthoringFunction& OutFunction,
    FString& OutError)
{
    if (!ReadRequiredId(Object, TEXT("id"), OutFunction.Id, OutError) ||
        !ReadRequiredString(Object, TEXT("name"), OutFunction.Name, OutError) ||
        !ReadRequiredString(Object, TEXT("qualifiedName"), OutFunction.QualifiedName, OutError) ||
        !ReadRequiredString(Object, TEXT("returnTypeMetadataName"), OutFunction.ReturnTypeMetadataName, OutError) ||
        !Object->TryGetBoolField(TEXT("isStatic"), OutFunction.bStatic) ||
        !ReadRequiredId(Object, TEXT("rootRegionId"), OutFunction.RootRegionId, OutError))
    {
        return false;
    }

    const TArray<TSharedPtr<FJsonValue>>* Parameters = nullptr;
    const TArray<TSharedPtr<FJsonValue>>* Regions = nullptr;
    const TArray<TSharedPtr<FJsonValue>>* Actions = nullptr;
    const TArray<TSharedPtr<FJsonValue>>* Edges = nullptr;
    if (!Object->TryGetArrayField(TEXT("parameters"), Parameters) || Parameters == nullptr ||
        !Object->TryGetArrayField(TEXT("regions"), Regions) || Regions == nullptr ||
        !Object->TryGetArrayField(TEXT("actions"), Actions) || Actions == nullptr ||
        !Object->TryGetArrayField(TEXT("edges"), Edges) || Edges == nullptr)
    {
        OutError = FString::Printf(TEXT("Function '%s' has incomplete collections."), *OutFunction.Name);
        return false;
    }

    TSet<FString> ParameterIds;
    TSet<FString> ParameterNames;
    for (const TSharedPtr<FJsonValue>& Value : *Parameters)
    {
        const TSharedPtr<FJsonObject> ParameterObject = Value.IsValid() ? Value->AsObject() : nullptr;
        FUnifiedScriptAuthoringParameter Parameter;
        if (!ReadRequiredId(ParameterObject, TEXT("id"), Parameter.Id, OutError) ||
            !ReadRequiredString(ParameterObject, TEXT("name"), Parameter.Name, OutError) ||
            !ReadRequiredString(ParameterObject, TEXT("typeMetadataName"), Parameter.TypeMetadataName, OutError) ||
            !ReadOptionalId(ParameterObject, TEXT("typeId"), Parameter.TypeId, OutError) ||
            !ReadOptionalString(ParameterObject, TEXT("typeDisplayName"), Parameter.TypeDisplayName, OutError) ||
            (Parameter.TypeId.IsEmpty() != Parameter.TypeDisplayName.IsEmpty()) ||
            ParameterIds.Contains(Parameter.Id) || ParameterNames.Contains(Parameter.Name))
        {
            return false;
        }
        ParameterIds.Add(Parameter.Id);
        ParameterNames.Add(Parameter.Name);
        OutFunction.Parameters.Add(MoveTemp(Parameter));
    }
    for (const TSharedPtr<FJsonValue>& Value : *Regions)
    {
        FUnifiedScriptAuthoringRegion Region;
        if (!Value.IsValid() || !ReadRegion(Value->AsObject(), Region, OutError))
        {
            return false;
        }
        OutFunction.Regions.Add(MoveTemp(Region));
    }
    for (const TSharedPtr<FJsonValue>& Value : *Actions)
    {
        FUnifiedScriptAuthoringAction Action;
        if (!Value.IsValid() || !ReadAction(Value->AsObject(), Action, OutError))
        {
            return false;
        }
        OutFunction.Actions.Add(MoveTemp(Action));
    }
    for (const TSharedPtr<FJsonValue>& Value : *Edges)
    {
        FUnifiedScriptAuthoringEdge Edge;
        if (!Value.IsValid() || !ReadEdge(Value->AsObject(), Edge, OutError))
        {
            return false;
        }
        OutFunction.Edges.Add(MoveTemp(Edge));
    }
    return ValidateFunction(OutFunction, OutError);
}

bool ReadAsset(
    const TSharedPtr<FJsonObject>& Object,
    FUnifiedScriptAuthoringAsset& OutAsset,
    FString& OutError)
{
    if (!ReadRequiredId(Object, TEXT("id"), OutAsset.Id, OutError) ||
        !ReadRequiredString(Object, TEXT("qualifiedName"), OutAsset.QualifiedName, OutError) ||
        !ReadRequiredString(Object, TEXT("assetPath"), OutAsset.AssetPath, OutError) ||
        !ReadSpan(Object, OutAsset.Span, OutError))
    {
        return false;
    }
    const TArray<TSharedPtr<FJsonValue>>* States = nullptr;
    const TArray<TSharedPtr<FJsonValue>>* Functions = nullptr;
    if (!Object->TryGetArrayField(TEXT("states"), States) || States == nullptr ||
        !Object->TryGetArrayField(TEXT("functions"), Functions) || Functions == nullptr)
    {
        OutError = FString::Printf(TEXT("Asset '%s' has incomplete collections."), *OutAsset.AssetPath);
        return false;
    }

    TSet<FString> StateIds;
    TSet<FString> StateNames;
    for (const TSharedPtr<FJsonValue>& Value : *States)
    {
        FUnifiedScriptAuthoringState State;
        if (!Value.IsValid() || !ReadState(Value->AsObject(), State, OutError) ||
            StateIds.Contains(State.Id) || StateNames.Contains(State.Name))
        {
            return false;
        }
        StateIds.Add(State.Id);
        StateNames.Add(State.Name);
        OutAsset.States.Add(MoveTemp(State));
    }
    for (const TSharedPtr<FJsonValue>& Value : *Functions)
    {
        FUnifiedScriptAuthoringFunction Function;
        if (!Value.IsValid() || !ReadFunction(Value->AsObject(), Function, OutError))
        {
            return false;
        }
        OutAsset.Functions.Add(MoveTemp(Function));
    }
    return true;
}
}

bool FUnifiedScriptAuthoringSpan::IsValid() const
{
    return !FilePath.IsEmpty() && StartLine >= 0 && StartColumn >= 0 &&
        EndLine >= StartLine && EndColumn >= 0;
}

FString FUnifiedScriptAuthoringSpan::ToDisplayString() const
{
    return FString::Printf(TEXT("%s:%d:%d"), *FilePath, StartLine, StartColumn);
}

const FUnifiedScriptAuthoringPort* FUnifiedScriptAuthoringAction::FindPort(
    const FString& PortName,
    const FString& PortDirection) const
{
    return Ports.FindByPredicate([&PortName, &PortDirection](
        const FUnifiedScriptAuthoringPort& Port)
    {
        return Port.Name == PortName && Port.Direction == PortDirection;
    });
}

const FUnifiedScriptAuthoringRegion* FUnifiedScriptAuthoringFunction::FindRegion(
    const FString& RegionId) const
{
    return Regions.FindByPredicate([&RegionId](const FUnifiedScriptAuthoringRegion& Region)
    {
        return Region.Id == RegionId;
    });
}

const FUnifiedScriptAuthoringAction* FUnifiedScriptAuthoringFunction::FindAction(
    const FString& ActionId) const
{
    return Actions.FindByPredicate([&ActionId](const FUnifiedScriptAuthoringAction& Action)
    {
        return Action.Id == ActionId;
    });
}

const FUnifiedScriptAuthoringEdge* FUnifiedScriptAuthoringFunction::FindEdge(
    const FString& EdgeId) const
{
    return Edges.FindByPredicate([&EdgeId](const FUnifiedScriptAuthoringEdge& Edge)
    {
        return Edge.Id == EdgeId;
    });
}

int32 FUnifiedScriptAuthoringSurfaceDocument::GetStateCount() const
{
    int32 Count = 0;
    for (const FUnifiedScriptAuthoringAsset& Asset : Assets)
    {
        Count += Asset.States.Num();
    }
    return Count;
}

int32 FUnifiedScriptAuthoringSurfaceDocument::GetFunctionCount() const
{
    int32 Count = 0;
    for (const FUnifiedScriptAuthoringAsset& Asset : Assets)
    {
        Count += Asset.Functions.Num();
    }
    return Count;
}

int32 FUnifiedScriptAuthoringSurfaceDocument::GetActionCount() const
{
    int32 Count = 0;
    for (const FUnifiedScriptAuthoringAsset& Asset : Assets)
    {
        for (const FUnifiedScriptAuthoringFunction& Function : Asset.Functions)
        {
            Count += Function.Actions.Num();
        }
    }
    return Count;
}

int32 FUnifiedScriptAuthoringSurfaceDocument::GetEdgeCount() const
{
    int32 Count = 0;
    for (const FUnifiedScriptAuthoringAsset& Asset : Assets)
    {
        for (const FUnifiedScriptAuthoringFunction& Function : Asset.Functions)
        {
            Count += Function.Edges.Num();
        }
    }
    return Count;
}

bool FUnifiedScriptAuthoringSurfaceReader::LoadFile(
    const FString& SurfacePath,
    FUnifiedScriptAuthoringSurfaceDocument& OutDocument,
    FString& OutError)
{
    const FString ResolvedPath = ResolveConfiguredPath(SurfacePath);
    FString Json;
    if (!IFileManager::Get().FileExists(*ResolvedPath) ||
        !FFileHelper::LoadFileToString(Json, *ResolvedPath))
    {
        OutError = FString::Printf(TEXT("Authoring Surface cannot be read: '%s'."), *ResolvedPath);
        return false;
    }
    return ParseJson(Json, OutDocument, OutError);
}

bool FUnifiedScriptAuthoringSurfaceReader::ParseJson(
    const FString& SurfaceJson,
    FUnifiedScriptAuthoringSurfaceDocument& OutDocument,
    FString& OutError)
{
    using namespace UE::UnifiedScript::AuthoringSurface::Private;

    TSharedPtr<FJsonObject> Root;
    const TSharedRef<TJsonReader<>> Reader = TJsonReaderFactory<>::Create(SurfaceJson);
    if (!FJsonSerializer::Deserialize(Reader, Root) || !Root.IsValid())
    {
        OutError = FString::Printf(TEXT("Authoring Surface JSON is invalid: %s"), *Reader->GetErrorMessage());
        return false;
    }

    FUnifiedScriptAuthoringSurfaceDocument Candidate;
    if (!ReadRequiredString(Root, TEXT("schemaVersion"), Candidate.SchemaVersion, OutError) ||
        Candidate.SchemaVersion != SchemaVersion ||
        !ReadRequiredId(Root, TEXT("sourceCompilationId"), Candidate.SourceCompilationId, OutError) ||
        !ReadRequiredId(Root, TEXT("semanticConfigurationId"), Candidate.SemanticConfigurationId, OutError))
    {
        if (Candidate.SchemaVersion != SchemaVersion)
        {
            OutError = FString::Printf(TEXT("Unsupported Authoring Surface schema '%s'."), *Candidate.SchemaVersion);
        }
        return false;
    }

    const TArray<TSharedPtr<FJsonValue>>* Assets = nullptr;
    if (!Root->TryGetArrayField(TEXT("assets"), Assets) || Assets == nullptr)
    {
        OutError = TEXT("Authoring Surface requires an assets array.");
        return false;
    }

    TSet<FString> AssetIds;
    TSet<FString> AssetPaths;
    TSet<FString> FunctionIds;
    for (const TSharedPtr<FJsonValue>& Value : *Assets)
    {
        FUnifiedScriptAuthoringAsset Asset;
        if (!Value.IsValid() || !ReadAsset(Value->AsObject(), Asset, OutError) ||
            AssetIds.Contains(Asset.Id) || AssetPaths.Contains(Asset.AssetPath))
        {
            return false;
        }
        for (const FUnifiedScriptAuthoringFunction& Function : Asset.Functions)
        {
            if (FunctionIds.Contains(Function.Id))
            {
                OutError = FString::Printf(TEXT("Duplicate Authoring Surface function '%s'."), *Function.Id);
                return false;
            }
            FunctionIds.Add(Function.Id);
        }
        AssetIds.Add(Asset.Id);
        AssetPaths.Add(Asset.AssetPath);
        Candidate.Assets.Add(MoveTemp(Asset));
    }

    OutDocument = MoveTemp(Candidate);
    OutError.Reset();
    return true;
}

bool FUnifiedScriptAuthoringSurfaceReader::BuildSemanticPatch(
    const FUnifiedScriptAuthoringSurfaceDocument& Document,
    const FUnifiedScriptAuthoringEdit& Edit,
    const FString& NewValue,
    FString& OutPatchJson,
    FString& OutError)
{
    using namespace UE::UnifiedScript::AuthoringSurface::Private;

    FString TrimmedValue = NewValue;
    TrimmedValue.TrimStartAndEndInline();
    if (!IsSemanticId(Document.SourceCompilationId) ||
        !IsSemanticId(Document.SemanticConfigurationId) ||
        !IsSemanticId(Edit.TargetNodeId) || TrimmedValue.IsEmpty())
    {
        OutError = TEXT("A semantic patch requires valid compilation/configuration/target IDs and a new value.");
        return false;
    }

    const TSharedRef<FJsonObject> Operation = MakeShared<FJsonObject>();
    Operation->SetStringField(TEXT("operation"), Edit.Operation);
    Operation->SetStringField(TEXT("targetNodeId"), Edit.TargetNodeId);
    if (Edit.Kind == TEXT("literal") && Edit.Operation == TEXT("replaceLiteral") &&
        !Edit.ExpectedValueText.IsEmpty())
    {
        Operation->SetStringField(TEXT("expectedValueText"), Edit.ExpectedValueText);
        Operation->SetStringField(TEXT("newValueText"), TrimmedValue);
    }
    else if (Edit.Kind == TEXT("operator") && Edit.Operation == TEXT("changeOperator") &&
        !Edit.ExpectedOperator.IsEmpty() && IsSemanticId(Edit.ExpectedResultTypeId) &&
        IsSemanticId(Edit.ExpectedOperandTypeId))
    {
        Operation->SetStringField(TEXT("expectedOperator"), Edit.ExpectedOperator);
        Operation->SetStringField(TEXT("newOperator"), TrimmedValue);
        Operation->SetStringField(TEXT("expectedResultTypeId"), Edit.ExpectedResultTypeId);
        Operation->SetStringField(TEXT("expectedOperandTypeId"), Edit.ExpectedOperandTypeId);
        if (!Edit.ExpectedRightOperandTypeId.IsEmpty())
        {
            Operation->SetStringField(
                TEXT("expectedRightOperandTypeId"),
                Edit.ExpectedRightOperandTypeId);
        }
    }
    else
    {
        OutError = FString::Printf(TEXT("Edit capability '%s' cannot produce a typed semantic patch."), *Edit.Id);
        return false;
    }

    const TSharedRef<FJsonObject> Root = MakeShared<FJsonObject>();
    Root->SetStringField(TEXT("schemaVersion"), TEXT("unifiedscript.patch/v0.21"));
    Root->SetStringField(TEXT("sourceCompilationId"), Document.SourceCompilationId);
    Root->SetStringField(TEXT("semanticConfigurationId"), Document.SemanticConfigurationId);
    Root->SetArrayField(TEXT("operations"), {MakeShared<FJsonValueObject>(Operation)});

    OutPatchJson.Reset();
    const TSharedRef<TJsonWriter<>> Writer = TJsonWriterFactory<>::Create(&OutPatchJson);
    if (!FJsonSerializer::Serialize(Root, Writer))
    {
        OutError = TEXT("Unreal could not serialize the semantic patch.");
        return false;
    }
    OutPatchJson.AppendChar(TEXT('\n'));
    OutError.Reset();
    return true;
}

bool FUnifiedScriptAuthoringSurfaceReader::BuildDataEdgeReconnectionPatch(
    const FUnifiedScriptAuthoringSurfaceDocument& Document,
    const FUnifiedScriptAuthoringFunction& Function,
    const FUnifiedScriptAuthoringEdge& ExpectedEdge,
    const FString& NewSourceNodeId,
    const FString& NewSourcePort,
    FString& OutPatchJson,
    FString& OutError)
{
    using namespace UE::UnifiedScript::AuthoringSurface::Private;

    const FUnifiedScriptAuthoringAction* ExpectedSource =
        Function.FindAction(ExpectedEdge.FromNode);
    const FUnifiedScriptAuthoringAction* NewSource = Function.FindAction(NewSourceNodeId);
    const FUnifiedScriptAuthoringAction* Target = Function.FindAction(ExpectedEdge.ToNode);
    const FUnifiedScriptAuthoringPort* ExpectedSourceOutput = ExpectedSource != nullptr
        ? ExpectedSource->FindPort(ExpectedEdge.FromPort, TEXT("output"))
        : nullptr;
    const FUnifiedScriptAuthoringPort* NewSourceOutput = NewSource != nullptr
        ? NewSource->FindPort(NewSourcePort, TEXT("output"))
        : nullptr;
    const FUnifiedScriptAuthoringPort* TargetInput = Target != nullptr
        ? Target->FindPort(ExpectedEdge.ToPort, TEXT("input"))
        : nullptr;
    if (!IsSemanticId(Document.SourceCompilationId) ||
        !IsSemanticId(Document.SemanticConfigurationId) ||
        !IsSemanticId(ExpectedEdge.Id) || !IsSemanticId(ExpectedEdge.FromNode) ||
        !IsSemanticId(ExpectedEdge.ToNode) || !IsSemanticId(NewSourceNodeId) ||
        ExpectedEdge.Kind != TEXT("data") || ExpectedSourceOutput == nullptr ||
        NewSourceOutput == nullptr || TargetInput == nullptr ||
        ExpectedSourceOutput->IsControl() || NewSourceOutput->IsControl() ||
        TargetInput->IsControl() || TargetInput->TypeId.IsEmpty() ||
        ExpectedSourceOutput->TypeId != TargetInput->TypeId ||
        NewSourceOutput->TypeId != TargetInput->TypeId)
    {
        OutError = TEXT("The selected wire is not an exact, typed compiler-owned data edge.");
        return false;
    }
    if (ExpectedEdge.ToNode == NewSourceNodeId ||
        (ExpectedEdge.FromNode == NewSourceNodeId && ExpectedEdge.FromPort == NewSourcePort))
    {
        OutError = TEXT("Choose a distinct data output; self and no-op rewires are not valid.");
        return false;
    }

    const TSharedRef<FJsonObject> Operation = MakeShared<FJsonObject>();
    Operation->SetStringField(TEXT("operation"), TEXT("reconnectDataEdge"));
    Operation->SetStringField(TEXT("targetNodeId"), ExpectedEdge.ToNode);
    Operation->SetStringField(TEXT("targetPort"), ExpectedEdge.ToPort);
    Operation->SetStringField(TEXT("expectedEdgeId"), ExpectedEdge.Id);
    Operation->SetStringField(TEXT("expectedSourceNodeId"), ExpectedEdge.FromNode);
    Operation->SetStringField(TEXT("expectedSourcePort"), ExpectedEdge.FromPort);
    Operation->SetStringField(TEXT("newSourceNodeId"), NewSourceNodeId);
    Operation->SetStringField(TEXT("newSourcePort"), NewSourcePort);
    Operation->SetStringField(TEXT("expectedTypeId"), TargetInput->TypeId);

    const TSharedRef<FJsonObject> Root = MakeShared<FJsonObject>();
    Root->SetStringField(TEXT("schemaVersion"), TEXT("unifiedscript.patch/v0.21"));
    Root->SetStringField(TEXT("sourceCompilationId"), Document.SourceCompilationId);
    Root->SetStringField(TEXT("semanticConfigurationId"), Document.SemanticConfigurationId);
    Root->SetArrayField(TEXT("operations"), {MakeShared<FJsonValueObject>(Operation)});

    OutPatchJson.Reset();
    const TSharedRef<TJsonWriter<>> Writer = TJsonWriterFactory<>::Create(&OutPatchJson);
    if (!FJsonSerializer::Serialize(Root, Writer))
    {
        OutError = TEXT("Unreal could not serialize the data-edge rewire patch.");
        return false;
    }
    OutPatchJson.AppendChar(TEXT('\n'));
    OutError.Reset();
    return true;
}

namespace UE::UnifiedScript::AuthoringSurface::Editing::Private
{
bool SerializeRoot(
    const TSharedRef<FJsonObject>& Root,
    FString& OutJson,
    FString& OutError,
    const TCHAR* Description)
{
    OutJson.Reset();
    const TSharedRef<TJsonWriter<>> Writer = TJsonWriterFactory<>::Create(&OutJson);
    if (!FJsonSerializer::Serialize(Root, Writer))
    {
        OutError = FString::Printf(TEXT("Unreal could not serialize %s."), Description);
        return false;
    }
    OutJson.AppendChar(TEXT('\n'));
    OutError.Reset();
    return true;
}

TSharedRef<FJsonObject> MakePatchRoot(
    const FUnifiedScriptAuthoringSurfaceDocument& Document,
    const TSharedRef<FJsonObject>& Operation)
{
    const TSharedRef<FJsonObject> Root = MakeShared<FJsonObject>();
    Root->SetStringField(TEXT("schemaVersion"), TEXT("unifiedscript.patch/v0.21"));
    Root->SetStringField(TEXT("sourceCompilationId"), Document.SourceCompilationId);
    Root->SetStringField(TEXT("semanticConfigurationId"), Document.SemanticConfigurationId);
    Root->SetArrayField(TEXT("operations"), {MakeShared<FJsonValueObject>(Operation)});
    return Root;
}

const FUnifiedScriptAuthoringRegion* FindOwningBlock(
    const FUnifiedScriptAuthoringFunction& Function,
    const FString& StatementId)
{
    for (const FUnifiedScriptAuthoringRegion& Region : Function.Regions)
    {
        if (Region.DirectStatementIds.Contains(StatementId))
        {
            return &Region;
        }
    }
    return nullptr;
}

const FUnifiedScriptAuthoringAction* FindContainingStatement(
    const FUnifiedScriptAuthoringFunction& Function,
    const FString& NodeId)
{
    TArray<FString> Pending = {NodeId};
    TSet<FString> Visited;
    while (!Pending.IsEmpty())
    {
        const FString Current = Pending.Pop(EAllowShrinking::No);
        if (Visited.Contains(Current))
        {
            continue;
        }
        Visited.Add(Current);
        if (const FUnifiedScriptAuthoringAction* Action = Function.FindAction(Current))
        {
            if (Action->Ports.ContainsByPredicate([](const FUnifiedScriptAuthoringPort& Port)
                {
                    return Port.IsControl();
                }))
            {
                return Action;
            }
        }
        for (const FUnifiedScriptAuthoringEdge& Edge : Function.Edges)
        {
            if (Edge.Kind == TEXT("data") && Edge.FromNode == Current)
            {
                Pending.Add(Edge.ToNode);
            }
        }
    }
    return nullptr;
}

void GetSiblingPreconditions(
    const FUnifiedScriptAuthoringRegion& Block,
    const FString& StatementId,
    FString& OutPrevious,
    FString& OutNext)
{
    OutPrevious.Reset();
    OutNext.Reset();
    const int32 Index = Block.DirectStatementIds.IndexOfByKey(StatementId);
    if (Index > 0)
    {
        OutPrevious = Block.DirectStatementIds[Index - 1];
    }
    if (Index != INDEX_NONE && Index + 1 < Block.DirectStatementIds.Num())
    {
        OutNext = Block.DirectStatementIds[Index + 1];
    }
}

void SetOptionalId(
    const TSharedRef<FJsonObject>& Object,
    const TCHAR* Field,
    const FString& Value)
{
    if (Value.IsEmpty())
    {
        Object->SetField(Field, MakeShared<FJsonValueNull>());
    }
    else
    {
        Object->SetStringField(Field, Value);
    }
}

bool GetInsertionGap(
    const FUnifiedScriptAuthoringFunction& Function,
    const FUnifiedScriptAuthoringRegion& Block,
    FString& OutPrevious,
    FString& OutNext)
{
    OutPrevious.Reset();
    OutNext.Reset();
    if (Block.SourceNodeId.IsEmpty())
    {
        return false;
    }
    if (Block.DirectStatementIds.IsEmpty())
    {
        return true;
    }

    const FString& LastId = Block.DirectStatementIds.Last();
    const FUnifiedScriptAuthoringAction* Last = Function.FindAction(LastId);
    const bool bTerminal = Last != nullptr &&
        (Last->GraphKind == TEXT("return") || Last->GraphKind == TEXT("break") ||
            Last->GraphKind == TEXT("continue"));
    if (bTerminal)
    {
        OutNext = LastId;
        if (Block.DirectStatementIds.Num() > 1)
        {
            OutPrevious = Block.DirectStatementIds[Block.DirectStatementIds.Num() - 2];
        }
    }
    else
    {
        OutPrevious = LastId;
    }
    return true;
}

const FUnifiedScriptAuthoringPort* FindTypePort(
    const FUnifiedScriptAuthoringFunction& Function,
    const FString& DisplayName)
{
    for (const FUnifiedScriptAuthoringAction& Action : Function.Actions)
    {
        for (const FUnifiedScriptAuthoringPort& Port : Action.Ports)
        {
            if (!Port.IsControl() && Port.TypeDisplayName == DisplayName && !Port.TypeId.IsEmpty())
            {
                return &Port;
            }
        }
    }
    return nullptr;
}

TSharedRef<FJsonObject> LocalReference(const FUnifiedScriptAuthoringParameter& Parameter)
{
    const TSharedRef<FJsonObject> Expression = MakeShared<FJsonObject>();
    Expression->SetStringField(TEXT("expression"), TEXT("localReference"));
    Expression->SetStringField(TEXT("sourceDefinitionId"), Parameter.Id);
    Expression->SetStringField(TEXT("expectedName"), Parameter.Name);
    Expression->SetStringField(TEXT("expectedTypeId"), Parameter.TypeId);
    return Expression;
}
}

bool FUnifiedScriptAuthoringSurfaceReader::BuildDataEdgeDisconnectionPatch(
    const FUnifiedScriptAuthoringSurfaceDocument& Document,
    const FUnifiedScriptAuthoringFunction& Function,
    const FUnifiedScriptAuthoringEdge& ExpectedEdge,
    FString& OutPatchJson,
    FString& OutError)
{
    using namespace UE::UnifiedScript::AuthoringSurface::Private;
    using namespace UE::UnifiedScript::AuthoringSurface::Editing::Private;
    const FUnifiedScriptAuthoringAction* Source = Function.FindAction(ExpectedEdge.FromNode);
    const FUnifiedScriptAuthoringAction* Target = Function.FindAction(ExpectedEdge.ToNode);
    const FUnifiedScriptAuthoringPort* SourcePort = Source != nullptr
        ? Source->FindPort(ExpectedEdge.FromPort, TEXT("output"))
        : nullptr;
    const FUnifiedScriptAuthoringPort* TargetPort = Target != nullptr
        ? Target->FindPort(ExpectedEdge.ToPort, TEXT("input"))
        : nullptr;
    if (!IsSemanticId(ExpectedEdge.Id) || ExpectedEdge.Kind != TEXT("data") ||
        SourcePort == nullptr || TargetPort == nullptr || SourcePort->IsControl() ||
        TargetPort->IsControl() || TargetPort->TypeId.IsEmpty() ||
        SourcePort->TypeId != TargetPort->TypeId)
    {
        OutError = TEXT("Only an exact compiler-owned typed data edge can be disconnected.");
        return false;
    }

    const TSharedRef<FJsonObject> Operation = MakeShared<FJsonObject>();
    Operation->SetStringField(TEXT("operation"), TEXT("disconnectDataEdge"));
    Operation->SetStringField(TEXT("targetNodeId"), ExpectedEdge.ToNode);
    Operation->SetStringField(TEXT("targetPort"), ExpectedEdge.ToPort);
    Operation->SetStringField(TEXT("expectedEdgeId"), ExpectedEdge.Id);
    Operation->SetStringField(TEXT("expectedSourceNodeId"), ExpectedEdge.FromNode);
    Operation->SetStringField(TEXT("expectedSourcePort"), ExpectedEdge.FromPort);
    Operation->SetStringField(TEXT("expectedTypeId"), TargetPort->TypeId);
    return SerializeRoot(MakePatchRoot(Document, Operation), OutPatchJson, OutError, TEXT("the data-edge disconnection patch"));
}

bool FUnifiedScriptAuthoringSurfaceReader::BuildSharedDataEdgePatchPlan(
    const FUnifiedScriptAuthoringSurfaceDocument& Document,
    const FUnifiedScriptAuthoringFunction& Function,
    const FUnifiedScriptAuthoringEdge& ExpectedEdge,
    const FString& NewSourceNodeId,
    const FString& NewSourcePort,
    FString& OutPlanJson,
    FString& OutError)
{
    using namespace UE::UnifiedScript::AuthoringSurface::Editing::Private;
    const FUnifiedScriptAuthoringAction* Source = Function.FindAction(NewSourceNodeId);
    const FUnifiedScriptAuthoringAction* Target = Function.FindAction(ExpectedEdge.ToNode);
    const FUnifiedScriptAuthoringPort* SourcePort = Source != nullptr
        ? Source->FindPort(NewSourcePort, TEXT("output"))
        : nullptr;
    const FUnifiedScriptAuthoringPort* TargetPort = Target != nullptr
        ? Target->FindPort(ExpectedEdge.ToPort, TEXT("input"))
        : nullptr;
    const FUnifiedScriptAuthoringAction* SourceStatement =
        FindContainingStatement(Function, NewSourceNodeId);
    const FUnifiedScriptAuthoringAction* TargetStatement =
        FindContainingStatement(Function, ExpectedEdge.FromNode);
    const FUnifiedScriptAuthoringRegion* SourceBlock = SourceStatement != nullptr
        ? FindOwningBlock(Function, SourceStatement->Id)
        : nullptr;
    const FUnifiedScriptAuthoringRegion* TargetBlock = TargetStatement != nullptr
        ? FindOwningBlock(Function, TargetStatement->Id)
        : nullptr;
    if (Source == nullptr || Target == nullptr || SourcePort == nullptr || TargetPort == nullptr ||
        SourceStatement == nullptr || TargetStatement == nullptr || SourceBlock == nullptr ||
        TargetBlock == nullptr || SourceStatement == TargetStatement || SourcePort->IsControl() ||
        TargetPort->IsControl() || SourcePort->TypeId.IsEmpty() ||
        SourcePort->TypeId != TargetPort->TypeId || ExpectedEdge.FromNode == NewSourceNodeId)
    {
        OutError = TEXT("This shared-value gesture requires distinct typed expressions in explicit statements.");
        return false;
    }

    FString SourcePrevious;
    FString SourceNext;
    FString TargetPrevious;
    FString TargetNext;
    GetSiblingPreconditions(*SourceBlock, SourceStatement->Id, SourcePrevious, SourceNext);
    GetSiblingPreconditions(*TargetBlock, TargetStatement->Id, TargetPrevious, TargetNext);
    const FString TemporaryName = TEXT("sharedValue_") + NewSourceNodeId.Mid(3, 6);
    const FString IntroduceStepId = TEXT("introduceSharedValue");

    const TSharedRef<FJsonObject> Introduction = MakeShared<FJsonObject>();
    Introduction->SetStringField(TEXT("step"), TEXT("introduceTemporary"));
    Introduction->SetStringField(TEXT("stepId"), IntroduceStepId);
    Introduction->SetStringField(TEXT("targetNodeId"), NewSourceNodeId);
    Introduction->SetStringField(TEXT("expectedContainingStatementId"), SourceStatement->Id);
    Introduction->SetStringField(TEXT("expectedParentBlockId"), SourceBlock->SourceNodeId);
    SetOptionalId(Introduction, TEXT("expectedPreviousSiblingId"), SourcePrevious);
    SetOptionalId(Introduction, TEXT("expectedNextSiblingId"), SourceNext);
    Introduction->SetStringField(TEXT("name"), TemporaryName);
    Introduction->SetStringField(TEXT("typeName"), SourcePort->TypeDisplayName);
    Introduction->SetStringField(TEXT("expectedTypeId"), SourcePort->TypeId);

    const TSharedRef<FJsonObject> Reconnection = MakeShared<FJsonObject>();
    Reconnection->SetStringField(TEXT("step"), TEXT("reconnectValue"));
    Reconnection->SetStringField(TEXT("stepId"), TEXT("connectSharedValue"));
    Reconnection->SetStringField(TEXT("targetNodeId"), ExpectedEdge.FromNode);
    Reconnection->SetStringField(TEXT("sourceStepId"), IntroduceStepId);
    Reconnection->SetStringField(TEXT("expectedContainingStatementId"), TargetStatement->Id);
    Reconnection->SetStringField(TEXT("expectedParentBlockId"), TargetBlock->SourceNodeId);
    SetOptionalId(Reconnection, TEXT("expectedPreviousSiblingId"), TargetPrevious);
    SetOptionalId(Reconnection, TEXT("expectedNextSiblingId"), TargetNext);
    Reconnection->SetStringField(TEXT("expectedTypeId"), TargetPort->TypeId);

    const TSharedRef<FJsonObject> Root = MakeShared<FJsonObject>();
    Root->SetStringField(TEXT("schemaVersion"), TEXT("unifiedscript.patch-plan/v0.21"));
    Root->SetStringField(TEXT("sourceCompilationId"), Document.SourceCompilationId);
    Root->SetStringField(TEXT("semanticConfigurationId"), Document.SemanticConfigurationId);
    Root->SetArrayField(TEXT("steps"),
        {MakeShared<FJsonValueObject>(Introduction), MakeShared<FJsonValueObject>(Reconnection)});
    return SerializeRoot(Root, OutPlanJson, OutError, TEXT("the shared-value patch plan"));
}

bool FUnifiedScriptAuthoringSurfaceReader::BuildBasicNodeInsertionPatch(
    const FUnifiedScriptAuthoringSurfaceDocument& Document,
    const FUnifiedScriptAuthoringFunction& Function,
    const FUnifiedScriptAuthoringRegion& BlockRegion,
    const FString& TemplateId,
    FString& OutPatchJson,
    FString& OutError)
{
    using namespace UE::UnifiedScript::AuthoringSurface::Editing::Private;
    FString Previous;
    FString Next;
    if (!GetInsertionGap(Function, BlockRegion, Previous, Next))
    {
        OutError = TEXT("Choose an explicit function, branch, or loop block before adding a node.");
        return false;
    }

    const TSharedRef<FJsonObject> Statement = MakeShared<FJsonObject>();
    if (TemplateId == TEXT("local.int"))
    {
        const FUnifiedScriptAuthoringPort* Type = FindTypePort(Function, TEXT("int"));
        if (Type == nullptr)
        {
            OutError = TEXT("This function has no bound int type for a local node.");
            return false;
        }
        Statement->SetStringField(TEXT("statement"), TEXT("localLiteralDeclaration"));
        Statement->SetStringField(TEXT("name"),
            FString::Printf(TEXT("graphValue%d"), BlockRegion.DirectStatementIds.Num() + 1));
        Statement->SetStringField(TEXT("typeName"), TEXT("int"));
        Statement->SetStringField(TEXT("valueText"), TEXT("0"));
        Statement->SetStringField(TEXT("expectedTypeId"), Type->TypeId);
    }
    else if (TemplateId == TEXT("value.add"))
    {
        TArray<const FUnifiedScriptAuthoringParameter*> Operands;
        for (const FUnifiedScriptAuthoringParameter& Parameter : Function.Parameters)
        {
            if (Parameter.TypeDisplayName == TEXT("int") &&
                !Parameter.Id.IsEmpty() && !Parameter.TypeId.IsEmpty())
            {
                Operands.Add(&Parameter);
            }
        }
        if (Operands.Num() < 2)
        {
            OutError = TEXT("Add Expression requires two typed int parameters in the active function.");
            return false;
        }
        const TSharedRef<FJsonObject> Expression = MakeShared<FJsonObject>();
        Expression->SetStringField(TEXT("expression"), TEXT("binary"));
        Expression->SetStringField(TEXT("operator"), TEXT("+"));
        Expression->SetObjectField(TEXT("left"), LocalReference(*Operands[0]));
        Expression->SetObjectField(TEXT("right"), LocalReference(*Operands[1]));
        Expression->SetStringField(TEXT("expectedTypeId"), Operands[0]->TypeId);

        Statement->SetStringField(TEXT("statement"), TEXT("localDeclaration"));
        Statement->SetStringField(TEXT("name"),
            FString::Printf(TEXT("sum%d"), BlockRegion.DirectStatementIds.Num() + 1));
        Statement->SetStringField(TEXT("typeName"), TEXT("int"));
        Statement->SetObjectField(TEXT("initializer"), Expression);
        Statement->SetStringField(TEXT("expectedTypeId"), Operands[0]->TypeId);
    }
    else if (TemplateId == TEXT("flow.branch") || TemplateId == TEXT("flow.while"))
    {
        const FUnifiedScriptAuthoringPort* Type = FindTypePort(Function, TEXT("bool"));
        if (Type == nullptr)
        {
            OutError = TEXT("This function has no bound bool type for structured control flow.");
            return false;
        }
        Statement->SetStringField(
            TEXT("statement"),
            TemplateId == TEXT("flow.branch") ? TEXT("if") : TEXT("while"));
        const TSharedRef<FJsonObject> Condition = MakeShared<FJsonObject>();
        Condition->SetStringField(TEXT("expression"), TEXT("literal"));
        Condition->SetStringField(TEXT("valueText"), TEXT("true"));
        Condition->SetStringField(TEXT("expectedTypeId"), Type->TypeId);
        Statement->SetObjectField(TEXT("condition"), Condition);
    }
    else if (TemplateId == TEXT("flow.return"))
    {
        if (!Next.IsEmpty() && Function.FindAction(Next) != nullptr &&
            Function.FindAction(Next)->GraphKind == TEXT("return"))
        {
            OutError = TEXT("The selected block already ends with a return node.");
            return false;
        }
        const FUnifiedScriptAuthoringPort* ReturnType = nullptr;
        for (const FUnifiedScriptAuthoringAction& Action : Function.Actions)
        {
            if (Action.GraphKind == TEXT("return"))
            {
                ReturnType = Action.FindPort(TEXT("value"), TEXT("input"));
                if (ReturnType != nullptr)
                {
                    break;
                }
            }
        }
        if (ReturnType == nullptr || ReturnType->TypeId.IsEmpty())
        {
            OutError = TEXT("A typed return template requires a bound non-void return type.");
            return false;
        }
        Statement->SetStringField(TEXT("statement"), TEXT("return"));
        Statement->SetStringField(TEXT("expectedReturnTypeId"), ReturnType->TypeId);
        const TSharedRef<FJsonObject> Expression = MakeShared<FJsonObject>();
        Expression->SetStringField(TEXT("expression"), TEXT("default"));
        Expression->SetStringField(TEXT("typeName"), ReturnType->TypeDisplayName);
        Expression->SetStringField(TEXT("expectedTypeId"), ReturnType->TypeId);
        Statement->SetObjectField(TEXT("expression"), Expression);
    }
    else
    {
        OutError = FString::Printf(TEXT("Unknown graph node template '%s'."), *TemplateId);
        return false;
    }

    const TSharedRef<FJsonObject> Operation = MakeShared<FJsonObject>();
    Operation->SetStringField(TEXT("operation"), TEXT("insertStatement"));
    Operation->SetStringField(TEXT("targetNodeId"), BlockRegion.SourceNodeId);
    SetOptionalId(Operation, TEXT("expectedPreviousSiblingId"), Previous);
    SetOptionalId(Operation, TEXT("expectedNextSiblingId"), Next);
    Operation->SetObjectField(TEXT("newStatement"), Statement);
    return SerializeRoot(MakePatchRoot(Document, Operation), OutPatchJson, OutError, TEXT("the node insertion patch"));
}

bool FUnifiedScriptAuthoringSurfaceReader::BuildMemberInvocationInsertionPatch(
    const FUnifiedScriptAuthoringSurfaceDocument& Document,
    const FUnifiedScriptAuthoringFunction& Function,
    const FUnifiedScriptAuthoringRegion& BlockRegion,
    const FUnifiedScriptAuthoringParameter& Receiver,
    const FString& MemberName,
    const FString& ResultTypeName,
    const FString& ResultTypeId,
    const TArray<FUnifiedScriptAuthoringParameter>& Arguments,
    FString& OutPatchJson,
    FString& OutError)
{
    using namespace UE::UnifiedScript::AuthoringSurface::Private;
    using namespace UE::UnifiedScript::AuthoringSurface::Editing::Private;
    FString Previous;
    FString Next;
    if (!GetInsertionGap(Function, BlockRegion, Previous, Next) ||
        !IsSemanticId(Receiver.Id) || !IsSemanticId(Receiver.TypeId) ||
        !IsSemanticId(ResultTypeId) || MemberName.IsEmpty() || ResultTypeName.IsEmpty() ||
        Arguments.ContainsByPredicate([](const FUnifiedScriptAuthoringParameter& Argument)
        {
            return Argument.Id.IsEmpty() || Argument.TypeId.IsEmpty();
        }))
    {
        OutError = TEXT("The reflected call requires a block, typed receiver/result, and typed arguments.");
        return false;
    }

    const TSharedRef<FJsonObject> Invocation = MakeShared<FJsonObject>();
    Invocation->SetStringField(TEXT("expression"), TEXT("memberInvocation"));
    Invocation->SetObjectField(TEXT("receiver"), LocalReference(Receiver));
    Invocation->SetStringField(TEXT("memberName"), MemberName);
    TArray<TSharedPtr<FJsonValue>> JsonArguments;
    for (const FUnifiedScriptAuthoringParameter& Argument : Arguments)
    {
        JsonArguments.Add(MakeShared<FJsonValueObject>(LocalReference(Argument)));
    }
    Invocation->SetArrayField(TEXT("arguments"), MoveTemp(JsonArguments));
    Invocation->SetStringField(TEXT("expectedTypeId"), ResultTypeId);

    const TSharedRef<FJsonObject> Statement = MakeShared<FJsonObject>();
    Statement->SetStringField(TEXT("statement"), TEXT("localDeclaration"));
    Statement->SetStringField(TEXT("name"),
        FString::Printf(TEXT("apiResult%d"), BlockRegion.DirectStatementIds.Num() + 1));
    Statement->SetStringField(TEXT("typeName"), ResultTypeName);
    Statement->SetObjectField(TEXT("initializer"), Invocation);
    Statement->SetStringField(TEXT("expectedTypeId"), ResultTypeId);

    const TSharedRef<FJsonObject> Operation = MakeShared<FJsonObject>();
    Operation->SetStringField(TEXT("operation"), TEXT("insertStatement"));
    Operation->SetStringField(TEXT("targetNodeId"), BlockRegion.SourceNodeId);
    SetOptionalId(Operation, TEXT("expectedPreviousSiblingId"), Previous);
    SetOptionalId(Operation, TEXT("expectedNextSiblingId"), Next);
    Operation->SetObjectField(TEXT("newStatement"), Statement);
    return SerializeRoot(MakePatchRoot(Document, Operation), OutPatchJson, OutError, TEXT("the reflected call insertion patch"));
}

bool FUnifiedScriptAuthoringSurfaceReader::BuildMemberAccessInsertionPatch(
    const FUnifiedScriptAuthoringSurfaceDocument& Document,
    const FUnifiedScriptAuthoringFunction& Function,
    const FUnifiedScriptAuthoringRegion& BlockRegion,
    const FUnifiedScriptAuthoringParameter& Receiver,
    const FString& MemberName,
    const FString& ResultTypeName,
    const FString& ResultTypeId,
    FString& OutPatchJson,
    FString& OutError)
{
    using namespace UE::UnifiedScript::AuthoringSurface::Private;
    using namespace UE::UnifiedScript::AuthoringSurface::Editing::Private;
    FString Previous;
    FString Next;
    if (!GetInsertionGap(Function, BlockRegion, Previous, Next) ||
        !IsSemanticId(Receiver.Id) || !IsSemanticId(Receiver.TypeId) ||
        !IsSemanticId(ResultTypeId) || MemberName.IsEmpty() || ResultTypeName.IsEmpty())
    {
        OutError = TEXT("The reflected property requires a block and typed receiver/result.");
        return false;
    }

    const TSharedRef<FJsonObject> Access = MakeShared<FJsonObject>();
    Access->SetStringField(TEXT("expression"), TEXT("memberAccess"));
    Access->SetObjectField(TEXT("receiver"), LocalReference(Receiver));
    Access->SetStringField(TEXT("memberName"), MemberName);
    Access->SetStringField(TEXT("expectedTypeId"), ResultTypeId);

    const TSharedRef<FJsonObject> Statement = MakeShared<FJsonObject>();
    Statement->SetStringField(TEXT("statement"), TEXT("localDeclaration"));
    Statement->SetStringField(TEXT("name"),
        FString::Printf(TEXT("propertyValue%d"), BlockRegion.DirectStatementIds.Num() + 1));
    Statement->SetStringField(TEXT("typeName"), ResultTypeName);
    Statement->SetObjectField(TEXT("initializer"), Access);
    Statement->SetStringField(TEXT("expectedTypeId"), ResultTypeId);

    const TSharedRef<FJsonObject> Operation = MakeShared<FJsonObject>();
    Operation->SetStringField(TEXT("operation"), TEXT("insertStatement"));
    Operation->SetStringField(TEXT("targetNodeId"), BlockRegion.SourceNodeId);
    SetOptionalId(Operation, TEXT("expectedPreviousSiblingId"), Previous);
    SetOptionalId(Operation, TEXT("expectedNextSiblingId"), Next);
    Operation->SetObjectField(TEXT("newStatement"), Statement);
    return SerializeRoot(MakePatchRoot(Document, Operation), OutPatchJson, OutError, TEXT("the reflected property insertion patch"));
}

bool FUnifiedScriptAuthoringSurfaceReader::BuildStatementDeletionPatch(
    const FUnifiedScriptAuthoringSurfaceDocument& Document,
    const FUnifiedScriptAuthoringFunction& Function,
    const FUnifiedScriptAuthoringAction& Statement,
    FString& OutPatchJson,
    FString& OutError)
{
    using namespace UE::UnifiedScript::AuthoringSurface::Editing::Private;
    const FUnifiedScriptAuthoringRegion* Block = FindOwningBlock(Function, Statement.Id);
    if (Block == nullptr || Statement.GraphKind == TEXT("entry"))
    {
        OutError = TEXT("Only a statement owned by an explicit structured block can be deleted.");
        return false;
    }

    FString Previous;
    FString Next;
    GetSiblingPreconditions(*Block, Statement.Id, Previous, Next);
    const TSharedRef<FJsonObject> Operation = MakeShared<FJsonObject>();
    Operation->SetStringField(TEXT("operation"), TEXT("deleteStatement"));
    Operation->SetStringField(TEXT("targetNodeId"), Statement.Id);
    Operation->SetStringField(TEXT("expectedParentBlockId"), Block->SourceNodeId);
    SetOptionalId(Operation, TEXT("expectedPreviousSiblingId"), Previous);
    SetOptionalId(Operation, TEXT("expectedNextSiblingId"), Next);
    return SerializeRoot(MakePatchRoot(Document, Operation), OutPatchJson, OutError, TEXT("the statement deletion patch"));
}

FString FUnifiedScriptAuthoringSurfaceReader::ResolveConfiguredPath(
    const FString& ConfiguredPath)
{
    FString Result = ConfiguredPath;
    const TSharedPtr<IPlugin> Plugin = IPluginManager::Get().FindPlugin(TEXT("UEUnifiedScript"));
    if (Plugin.IsValid())
    {
        Result.ReplaceInline(TEXT("{PluginDir}"), *Plugin->GetBaseDir(), ESearchCase::IgnoreCase);
    }
    Result.ReplaceInline(TEXT("{ProjectDir}"), *FPaths::ProjectDir(), ESearchCase::IgnoreCase);
    if (FPaths::IsRelative(Result))
    {
        Result = FPaths::Combine(FPaths::ProjectDir(), Result);
    }
    Result = FPaths::ConvertRelativePathToFull(Result);
    FPaths::NormalizeFilename(Result);
    return Result;
}
