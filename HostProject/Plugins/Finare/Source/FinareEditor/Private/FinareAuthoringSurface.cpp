#include "FinareAuthoringSurface.h"

#include "FinareClassBinding.h"

#include "Dom/JsonObject.h"
#include "HAL/FileManager.h"
#include "Interfaces/IPluginManager.h"
#include "Misc/FileHelper.h"
#include "Misc/Paths.h"
#include "Policies/CondensedJsonPrintPolicy.h"
#include "Serialization/JsonReader.h"
#include "Serialization/JsonSerializer.h"

namespace UE::Finare::AuthoringSurface::Private
{
	constexpr TCHAR LegacySchemaVersion[] = TEXT("unifiedscript.authoring-surface/v0.4");
	constexpr TCHAR SchemaVersion[] = TEXT("unifiedscript.authoring-surface/v0.5");

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
			OutError = FString::Printf(
				TEXT("Authoring Surface optional field '%s' must be a non-empty string."), Field);
			return false;
		}
		return true;
	}

	bool ReadDeclarationOwners(
		const TSharedPtr<FJsonObject>& Root,
		TArray<FFinareAuthoringDeclarationOwner>& OutOwners,
		FString& OutError)
	{
		OutOwners.Reset();
		if (!Root->HasField(TEXT("declarationOwners")))
		{
			return true;
		}

		const TArray<TSharedPtr<FJsonValue>>* Values = nullptr;
		if (!Root->TryGetArrayField(TEXT("declarationOwners"), Values) || Values == nullptr)
		{
			OutError = TEXT("Authoring Surface declarationOwners must be an array.");
			return false;
		}
		TSet<FString> DeclarationIds;
		for (const TSharedPtr<FJsonValue>& Value : *Values)
		{
			const TSharedPtr<FJsonObject> Object = Value.IsValid() ? Value->AsObject() : nullptr;
			FFinareAuthoringDeclarationOwner Owner;
			if (!ReadRequiredId(Object, TEXT("declarationId"), Owner.DeclarationId, OutError) ||
				!ReadRequiredId(Object, TEXT("ownerAssetId"), Owner.OwnerAssetId, OutError) ||
				DeclarationIds.Contains(Owner.DeclarationId))
			{
				if (OutError.IsEmpty())
				{
					OutError = TEXT("Authoring Surface declarationOwners contains a duplicate declaration.");
				}
				return false;
			}
			DeclarationIds.Add(Owner.DeclarationId);
			OutOwners.Add(MoveTemp(Owner));
		}
		return true;
	}

	bool CompareRawProjectionWithoutField(
		const TSharedPtr<FJsonObject>& Left,
		const TSharedPtr<FJsonObject>& Right,
		const TCHAR* IgnoredField)
	{
		if (!Left.IsValid() || !Right.IsValid())
		{
			return false;
		}
		const TSharedPtr<FJsonObject> LeftCopy = MakeShared<FJsonObject>(*Left);
		const TSharedPtr<FJsonObject> RightCopy = MakeShared<FJsonObject>(*Right);
		LeftCopy->RemoveField(IgnoredField);
		RightCopy->RemoveField(IgnoredField);
		const FJsonValueObject LeftValue(LeftCopy);
		const FJsonValueObject RightValue(RightCopy);
		return FJsonValue::CompareEqual(LeftValue, RightValue);
	}

	bool ReadSpan(
		const TSharedPtr<FJsonObject>& Owner,
		FFinareAuthoringSpan& OutSpan,
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

	bool JsonScalarToString(
		const TSharedPtr<FJsonValue>& Value,
		FString& OutValue,
		FString& OutError)
	{
		if (!Value.IsValid())
		{
			OutError = TEXT("Authoring Surface state is missing its defaultValue JSON field.");
			return false;
		}
		if (Value->IsNull())
		{
			OutValue = TEXT("null");
			return true;
		}

		// UE's JSON serializer only accepts an object/array as a root value.  Wrap
		// the scalar in a one-element array so numbers, booleans, strings, and
		// nested values retain their actual JSON representation instead of being
		// silently turned into null when authoring metadata is read.
		TArray<TSharedPtr<FJsonValue>> Container;
		Container.Add(Value);
		FString Json;
		const TSharedRef<TJsonWriter<TCHAR, TCondensedJsonPrintPolicy<TCHAR>>> Writer =
			TJsonWriterFactory<TCHAR, TCondensedJsonPrintPolicy<TCHAR>>::Create(&Json);
		if (!FJsonSerializer::Serialize(Container, Writer) ||
			!Json.StartsWith(TEXT("[")) ||
			!Json.EndsWith(TEXT("]")))
		{
			OutError = TEXT("Authoring Surface could not serialize a JSON scalar default value.");
			return false;
		}

		OutValue = Json.Mid(1, Json.Len() - 2);
		return true;
	}

	bool ReadState(
		const TSharedPtr<FJsonObject>& Object,
		FFinareAuthoringState& OutState,
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

		if (!Object->HasField(TEXT("defaultValue")) ||
			!JsonScalarToString(Object->TryGetField(TEXT("defaultValue")), OutState.DefaultValue, OutError))
		{
			return false;
		}
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
		FFinareAuthoringEdit& OutEdit,
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
		FFinareAuthoringPort& OutPort,
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
		FFinareAuthoringEdge& OutEdge,
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
		FFinareAuthoringAction& OutAction,
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

		const TSharedPtr<FJsonObject>* Properties = nullptr;
		if (!Object->TryGetObjectField(TEXT("properties"), Properties) ||
			Properties == nullptr || !Properties->IsValid())
		{
			OutError = FString::Printf(TEXT("Action '%s' requires properties."), *OutAction.Id);
			return false;
		}
		for (const TPair<FString, TSharedPtr<FJsonValue>>& Property : (*Properties)->Values)
		{
			if (Property.Key.IsEmpty() || !Property.Value.IsValid() ||
				Property.Value->Type != EJson::String)
			{
				OutError = FString::Printf(
					TEXT("Action '%s' has an invalid property '%s'."),
					*OutAction.Id,
					*Property.Key);
				return false;
			}
			OutAction.Properties.Add(Property.Key, Property.Value->AsString());
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
			FFinareAuthoringEdit Edit;
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
			FFinareAuthoringPort Port;
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
		FFinareAuthoringRegion& OutRegion,
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
		const FFinareAuthoringFunction& Function,
		FString& OutError)
	{
		TMap<FString, const FFinareAuthoringRegion*> Regions;
		TMap<FString, const FFinareAuthoringAction*> Actions;
		TMap<FString, const FFinareAuthoringPort*> InputPorts;
		TMap<FString, const FFinareAuthoringPort*> OutputPorts;
		for (const FFinareAuthoringRegion& Region : Function.Regions)
		{
			if (Regions.Contains(Region.Id))
			{
				OutError = FString::Printf(
					TEXT("Function '%s' has duplicate region '%s'."), *Function.Name, *Region.Id);
				return false;
			}
			Regions.Add(Region.Id, &Region);
		}
		for (const FFinareAuthoringAction& Action : Function.Actions)
		{
			if (Actions.Contains(Action.Id))
			{
				OutError = FString::Printf(
					TEXT("Function '%s' has duplicate action '%s'."), *Function.Name, *Action.Id);
				return false;
			}
			Actions.Add(Action.Id, &Action);
			for (const FFinareAuthoringPort& Port : Action.Ports)
			{
				const FString Key = Action.Id + TEXT("|") + Port.Name;
				TMap<FString, const FFinareAuthoringPort*>& Ports =
					Port.IsInput() ? InputPorts : OutputPorts;
				if (Ports.Contains(Key))
				{
					OutError = FString::Printf(TEXT("Action '%s' has ambiguous port '%s'."), *Action.Id, *Port.Name);
					return false;
				}
				Ports.Add(Key, &Port);
			}
		}

		const FFinareAuthoringRegion* const* Root = Regions.Find(Function.RootRegionId);
		if (Root == nullptr || !(*Root)->ParentRegionId.IsEmpty())
		{
			OutError = FString::Printf(TEXT("Function '%s' has no valid root region."), *Function.Name);
			return false;
		}

		TSet<FString> OwnedActions;
		TSet<FString> OwnedChildren;
		for (const FFinareAuthoringRegion& Region : Function.Regions)
		{
			if (Region.Id != Function.RootRegionId && !Regions.Contains(Region.ParentRegionId))
			{
				OutError = FString::Printf(TEXT("Region '%s' has no parent."), *Region.Id);
				return false;
			}
			for (const FString& ActionId : Region.DirectActionIds)
			{
				const FFinareAuthoringAction* const* Action = Actions.Find(ActionId);
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
				const FFinareAuthoringRegion* const* Child = Regions.Find(ChildId);
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
		for (const FFinareAuthoringEdge& Edge : Function.Edges)
		{
			const FString FromKey = Edge.FromNode + TEXT("|") + Edge.FromPort;
			const FString ToKey = Edge.ToNode + TEXT("|") + Edge.ToPort;
			const FFinareAuthoringPort* const* Output = OutputPorts.Find(FromKey);
			const FFinareAuthoringPort* const* Input = InputPorts.Find(ToKey);
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
		FFinareAuthoringFunction& OutFunction,
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
			FFinareAuthoringParameter Parameter;
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
			FFinareAuthoringRegion Region;
			if (!Value.IsValid() || !ReadRegion(Value->AsObject(), Region, OutError))
			{
				return false;
			}
			OutFunction.Regions.Add(MoveTemp(Region));
		}
		for (const TSharedPtr<FJsonValue>& Value : *Actions)
		{
			FFinareAuthoringAction Action;
			if (!Value.IsValid() || !ReadAction(Value->AsObject(), Action, OutError))
			{
				return false;
			}
			OutFunction.Actions.Add(MoveTemp(Action));
		}
		for (const TSharedPtr<FJsonValue>& Value : *Edges)
		{
			FFinareAuthoringEdge Edge;
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
		FFinareAuthoringAsset& OutAsset,
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
			FFinareAuthoringState State;
			if (!Value.IsValid() || !ReadState(Value->AsObject(), State, OutError) ||
				StateIds.Contains(State.Id) || StateNames.Contains(State.Name))
			{
				return false;
			}
			StateIds.Add(State.Id);
			StateNames.Add(State.Name);
			OutAsset.States.Add(MoveTemp(State));
		}
		TSet<FString> FunctionIds;
		for (const TSharedPtr<FJsonValue>& Value : *Functions)
		{
			FFinareAuthoringFunction Function;
			if (!Value.IsValid() || !ReadFunction(Value->AsObject(), Function, OutError))
			{
				return false;
			}
			if (FunctionIds.Contains(Function.Id))
			{
				OutError = FString::Printf(
					TEXT("Asset '%s' contains duplicate function '%s'."),
					*OutAsset.AssetPath,
					*Function.Id);
				return false;
			}
			FunctionIds.Add(Function.Id);
			OutAsset.Functions.Add(MoveTemp(Function));
		}
		return true;
	}
}

bool FFinareAuthoringSpan::IsValid() const
{
	return !FilePath.IsEmpty() && StartLine >= 0 && StartColumn >= 0 &&
		EndLine >= StartLine && EndColumn >= 0;
}

FString FFinareAuthoringSpan::ToDisplayString() const
{
	return FString::Printf(TEXT("%s:%d:%d"), *FilePath, StartLine, StartColumn);
}

const FFinareAuthoringPort* FFinareAuthoringAction::FindPort(
	const FString& PortName,
	const FString& PortDirection) const
{
	return Ports.FindByPredicate([&PortName, &PortDirection](
		const FFinareAuthoringPort& Port)
		{
			return Port.Name == PortName && Port.Direction == PortDirection;
		});
}

const FFinareAuthoringRegion* FFinareAuthoringFunction::FindRegion(
	const FString& RegionId) const
{
	return Regions.FindByPredicate([&RegionId](const FFinareAuthoringRegion& Region)
	{
		return Region.Id == RegionId;
	});
}

const FFinareAuthoringAction* FFinareAuthoringFunction::FindAction(
	const FString& ActionId) const
{
	return Actions.FindByPredicate([&ActionId](const FFinareAuthoringAction& Action)
	{
		return Action.Id == ActionId;
	});
}

const FFinareAuthoringEdge* FFinareAuthoringFunction::FindEdge(
	const FString& EdgeId) const
{
	return Edges.FindByPredicate([&EdgeId](const FFinareAuthoringEdge& Edge)
	{
		return Edge.Id == EdgeId;
	});
}

int32 FFinareAuthoringSurfaceDocument::GetStateCount() const
{
	int32 Count = 0;
	for (const FFinareAuthoringAsset& Asset : Assets)
	{
		Count += Asset.States.Num();
	}
	return Count;
}

const FFinareAuthoringAsset* FFinareAuthoringSurfaceDocument::FindDeclarationOwnerAsset(
	const FString& DeclarationId) const
{
	const FFinareAuthoringDeclarationOwner* ExplicitOwner = DeclarationOwners.FindByPredicate(
		[&DeclarationId](const FFinareAuthoringDeclarationOwner& Owner)
		{
			return Owner.DeclarationId == DeclarationId;
		});
	if (ExplicitOwner != nullptr)
	{
		return Assets.FindByPredicate([ExplicitOwner](const FFinareAuthoringAsset& Asset)
		{
			return Asset.Id == ExplicitOwner->OwnerAssetId;
		});
	}

	const FFinareAuthoringAsset* UniqueOwner = nullptr;
	for (const FFinareAuthoringAsset& Asset : Assets)
	{
		const bool Contains = Asset.States.ContainsByPredicate([&DeclarationId](const FFinareAuthoringState& State)
		{
			return State.Id == DeclarationId;
		}) || Asset.Functions.ContainsByPredicate([&DeclarationId](const FFinareAuthoringFunction& Function)
		{
			return Function.Id == DeclarationId;
		});
		if (Contains)
		{
			if (UniqueOwner != nullptr)
			{
				return nullptr;
			}
			UniqueOwner = &Asset;
		}
	}
	return UniqueOwner;
}

int32 FFinareAuthoringSurfaceDocument::GetFunctionCount() const
{
	int32 Count = 0;
	for (const FFinareAuthoringAsset& Asset : Assets)
	{
		Count += Asset.Functions.Num();
	}
	return Count;
}

int32 FFinareAuthoringSurfaceDocument::GetActionCount() const
{
	int32 Count = 0;
	for (const FFinareAuthoringAsset& Asset : Assets)
	{
		for (const FFinareAuthoringFunction& Function : Asset.Functions)
		{
			Count += Function.Actions.Num();
		}
	}
	return Count;
}

int32 FFinareAuthoringSurfaceDocument::GetEdgeCount() const
{
	int32 Count = 0;
	for (const FFinareAuthoringAsset& Asset : Assets)
	{
		for (const FFinareAuthoringFunction& Function : Asset.Functions)
		{
			Count += Function.Edges.Num();
		}
	}
	return Count;
}

bool FFinareAuthoringSurfaceReader::LoadFile(
	const FString& SurfacePath,
	FFinareAuthoringSurfaceDocument& OutDocument,
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

bool FFinareAuthoringSurfaceReader::ParseJson(
	const FString& SurfaceJson,
	FFinareAuthoringSurfaceDocument& OutDocument,
	FString& OutError)
{
	using namespace UE::Finare::AuthoringSurface::Private;

	TSharedPtr<FJsonObject> Root;
	const TSharedRef<TJsonReader<>> Reader = TJsonReaderFactory<>::Create(SurfaceJson);
	if (!FJsonSerializer::Deserialize(Reader, Root) || !Root.IsValid())
	{
		OutError = FString::Printf(TEXT("Authoring Surface JSON is invalid: %s"), *Reader->GetErrorMessage());
		return false;
	}

	FFinareAuthoringSurfaceDocument Candidate;
	if (!ReadRequiredString(Root, TEXT("schemaVersion"), Candidate.SchemaVersion, OutError) ||
		(Candidate.SchemaVersion != LegacySchemaVersion && Candidate.SchemaVersion != SchemaVersion) ||
		!ReadRequiredId(Root, TEXT("sourceCompilationId"), Candidate.SourceCompilationId, OutError) ||
		!ReadRequiredId(Root, TEXT("semanticConfigurationId"), Candidate.SemanticConfigurationId, OutError) ||
		!ReadDeclarationOwners(Root, Candidate.DeclarationOwners, OutError))
	{
		if (Candidate.SchemaVersion != LegacySchemaVersion && Candidate.SchemaVersion != SchemaVersion)
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
	TMap<FString, TArray<TSharedPtr<FJsonObject>>> RawStateProjections;
	TMap<FString, TArray<TSharedPtr<FJsonObject>>> RawFunctionProjections;
	const bool bLegacySchema = Candidate.SchemaVersion == LegacySchemaVersion;
	for (const TSharedPtr<FJsonValue>& Value : *Assets)
	{
		const TSharedPtr<FJsonObject> RawAsset = Value.IsValid() ? Value->AsObject() : nullptr;
		FFinareAuthoringAsset Asset;
		if (!RawAsset.IsValid() || !ReadAsset(RawAsset, Asset, OutError) ||
			AssetIds.Contains(Asset.Id) || AssetPaths.Contains(Asset.AssetPath))
		{
			return false;
		}
		for (const FFinareAuthoringFunction& Function : Asset.Functions)
		{
			if (bLegacySchema && FunctionIds.Contains(Function.Id))
			{
				OutError = FString::Printf(TEXT("Duplicate Authoring Surface function '%s'."), *Function.Id);
				return false;
			}
			FunctionIds.Add(Function.Id);
		}
		const TArray<TSharedPtr<FJsonValue>>* RawStates = nullptr;
		if (RawAsset->TryGetArrayField(TEXT("states"), RawStates) && RawStates != nullptr)
		{
			for (const TSharedPtr<FJsonValue>& RawStateValue : *RawStates)
			{
				const TSharedPtr<FJsonObject> RawState = RawStateValue.IsValid()
					                                         ? RawStateValue->AsObject()
					                                         : nullptr;
				FString StateId;
				if (RawState.IsValid() && RawState->TryGetStringField(TEXT("id"), StateId))
				{
					RawStateProjections.FindOrAdd(StateId).Add(RawState);
				}
			}
		}
		const TArray<TSharedPtr<FJsonValue>>* RawFunctions = nullptr;
		if (RawAsset->TryGetArrayField(TEXT("functions"), RawFunctions) && RawFunctions != nullptr)
		{
			for (const TSharedPtr<FJsonValue>& RawFunctionValue : *RawFunctions)
			{
				const TSharedPtr<FJsonObject> RawFunction = RawFunctionValue.IsValid()
					                                            ? RawFunctionValue->AsObject()
					                                            : nullptr;
				FString FunctionId;
				if (RawFunction.IsValid() && RawFunction->TryGetStringField(TEXT("id"), FunctionId))
				{
					RawFunctionProjections.FindOrAdd(FunctionId).Add(RawFunction);
				}
			}
		}
		AssetIds.Add(Asset.Id);
		AssetPaths.Add(Asset.AssetPath);
		Candidate.Assets.Add(MoveTemp(Asset));
	}

	TMap<FString, TArray<int32>> StateProjectionIndices;
	TMap<FString, TArray<int32>> FunctionProjectionIndices;
	for (int32 AssetIndex = 0; AssetIndex < Candidate.Assets.Num(); ++AssetIndex)
	{
		const FFinareAuthoringAsset& Asset = Candidate.Assets[AssetIndex];
		for (const FFinareAuthoringState& State : Asset.States)
		{
			StateProjectionIndices.FindOrAdd(State.Id).Add(AssetIndex);
		}
		for (const FFinareAuthoringFunction& Function : Asset.Functions)
		{
			FunctionProjectionIndices.FindOrAdd(Function.Id).Add(AssetIndex);
		}
	}

	for (const TPair<FString, TArray<int32>>& Pair : StateProjectionIndices)
	{
		if (FunctionProjectionIndices.Contains(Pair.Key))
		{
			OutError = FString::Printf(TEXT("Declaration '%s' is both a state and a function."), *Pair.Key);
			return false;
		}
		const TArray<TSharedPtr<FJsonObject>>* RawProjections = RawStateProjections.Find(Pair.Key);
		for (int32 Index = 1; Index < Pair.Value.Num(); ++Index)
		{
			if (RawProjections == nullptr || RawProjections->Num() != Pair.Value.Num() ||
				!CompareRawProjectionWithoutField(
					(*RawProjections)[0],
					(*RawProjections)[Index],
					TEXT("defaultValue")))
			{
				OutError = FString::Printf(TEXT("Shared state '%s' has inconsistent declaration metadata."), *Pair.Key);
				return false;
			}
		}
	}

	for (const TPair<FString, TArray<int32>>& Pair : FunctionProjectionIndices)
	{
		const TArray<TSharedPtr<FJsonObject>>* RawProjections = RawFunctionProjections.Find(Pair.Key);
		for (int32 Index = 1; Index < Pair.Value.Num(); ++Index)
		{
			if (RawProjections == nullptr || RawProjections->Num() != Pair.Value.Num() ||
				!CompareRawProjectionWithoutField(
					(*RawProjections)[0],
					(*RawProjections)[Index],
					TEXT("qualifiedName")))
			{
				OutError = FString::Printf(
					TEXT("Shared function '%s' has inconsistent signature or graph."), *Pair.Key);
				return false;
			}
		}
	}

	TSet<FString> OwnerDeclarationIds;
	for (const FFinareAuthoringDeclarationOwner& Owner : Candidate.DeclarationOwners)
	{
		if (OwnerDeclarationIds.Contains(Owner.DeclarationId))
		{
			OutError = FString::Printf(TEXT("Duplicate declaration owner '%s'."), *Owner.DeclarationId);
			return false;
		}
		OwnerDeclarationIds.Add(Owner.DeclarationId);
		const int32 OwnerAssetIndex = Candidate.Assets.IndexOfByPredicate([&Owner](const FFinareAuthoringAsset& Asset)
		{
			return Asset.Id == Owner.OwnerAssetId;
		});
		const bool bState = StateProjectionIndices.Contains(Owner.DeclarationId);
		const bool bFunction = FunctionProjectionIndices.Contains(Owner.DeclarationId);
		const bool bContains = OwnerAssetIndex != INDEX_NONE &&
		((bState && Candidate.Assets[OwnerAssetIndex].States.ContainsByPredicate(
			[&Owner](const FFinareAuthoringState& State)
			{
				return State.Id == Owner.DeclarationId;
			})) || (bFunction && Candidate.Assets[OwnerAssetIndex].Functions.ContainsByPredicate(
			[&Owner](const FFinareAuthoringFunction& Function)
			{
				return Function.Id == Owner.DeclarationId;
			})));
		if (!bContains)
		{
			OutError = FString::Printf(
				TEXT("Declaration owner '%s' does not contain '%s'."), *Owner.OwnerAssetId, *Owner.DeclarationId);
			return false;
		}
	}

	if (!bLegacySchema)
	{
		for (const TPair<FString, TArray<int32>>& Pair : StateProjectionIndices)
		{
			if (Pair.Value.Num() > 1 && !OwnerDeclarationIds.Contains(Pair.Key))
			{
				OutError = FString::Printf(TEXT("Shared state '%s' requires an explicit source owner."), *Pair.Key);
				return false;
			}
		}
		for (const TPair<FString, TArray<int32>>& Pair : FunctionProjectionIndices)
		{
			if (Pair.Value.Num() > 1 && !OwnerDeclarationIds.Contains(Pair.Key))
			{
				OutError = FString::Printf(TEXT("Shared function '%s' requires an explicit source owner."), *Pair.Key);
				return false;
			}
		}
	}

	OutDocument = MoveTemp(Candidate);
	OutError.Reset();
	return true;
}

bool FFinareAuthoringSurfaceReader::BuildSemanticPatch(
	const FFinareAuthoringSurfaceDocument& Document,
	const FFinareAuthoringEdit& Edit,
	const FString& NewValue,
	FString& OutPatchJson,
	FString& OutError)
{
	using namespace UE::Finare::AuthoringSurface::Private;

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
	Root->SetStringField(TEXT("schemaVersion"), TEXT("unifiedscript.patch/v0.28"));
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

bool FFinareAuthoringSurfaceReader::BuildDataEdgeReconnectionPatch(
	const FFinareAuthoringSurfaceDocument& Document,
	const FFinareAuthoringFunction& Function,
	const FFinareAuthoringEdge& ExpectedEdge,
	const FString& NewSourceNodeId,
	const FString& NewSourcePort,
	FString& OutPatchJson,
	FString& OutError)
{
	using namespace UE::Finare::AuthoringSurface::Private;

	const FFinareAuthoringAction* ExpectedSource =
		Function.FindAction(ExpectedEdge.FromNode);
	const FFinareAuthoringAction* NewSource = Function.FindAction(NewSourceNodeId);
	const FFinareAuthoringAction* Target = Function.FindAction(ExpectedEdge.ToNode);
	const FFinareAuthoringPort* ExpectedSourceOutput = ExpectedSource != nullptr
		                                                   ? ExpectedSource->FindPort(
			                                                   ExpectedEdge.FromPort, TEXT("output"))
		                                                   : nullptr;
	const FFinareAuthoringPort* NewSourceOutput = NewSource != nullptr
		                                              ? NewSource->FindPort(NewSourcePort, TEXT("output"))
		                                              : nullptr;
	const FFinareAuthoringPort* TargetInput = Target != nullptr
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
	Root->SetStringField(TEXT("schemaVersion"), TEXT("unifiedscript.patch/v0.28"));
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

namespace UE::Finare::AuthoringSurface::Editing::Private
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
		const FFinareAuthoringSurfaceDocument& Document,
		const TSharedRef<FJsonObject>& Operation)
	{
		const TSharedRef<FJsonObject> Root = MakeShared<FJsonObject>();
		Root->SetStringField(TEXT("schemaVersion"), TEXT("unifiedscript.patch/v0.28"));
		Root->SetStringField(TEXT("sourceCompilationId"), Document.SourceCompilationId);
		Root->SetStringField(TEXT("semanticConfigurationId"), Document.SemanticConfigurationId);
		Root->SetArrayField(TEXT("operations"), {MakeShared<FJsonValueObject>(Operation)});
		return Root;
	}

	TSharedRef<FJsonObject> MakeDeclarationPatchRoot(
		const FFinareAuthoringSurfaceDocument& Document,
		const TSharedRef<FJsonObject>& Operation)
	{
		const TSharedRef<FJsonObject> Root = MakeShared<FJsonObject>();
		Root->SetStringField(TEXT("schemaVersion"), TEXT("unifiedscript.declaration-patch/v0.3"));
		Root->SetStringField(TEXT("sourceCompilationId"), Document.SourceCompilationId);
		Root->SetStringField(TEXT("semanticConfigurationId"), Document.SemanticConfigurationId);
		Root->SetArrayField(TEXT("operations"), {MakeShared<FJsonValueObject>(Operation)});
		return Root;
	}

	TSharedRef<FJsonObject> MakeClassBindingDeclarationPatchRoot(
		const FFinareAuthoringSurfaceDocument& Document,
		const TSharedRef<FJsonObject>& Operation)
	{
		const TSharedRef<FJsonObject> Root = MakeShared<FJsonObject>();
		Root->SetStringField(TEXT("schemaVersion"), TEXT("unifiedscript.declaration-patch/v0.4"));
		Root->SetStringField(TEXT("sourceCompilationId"), Document.SourceCompilationId);
		Root->SetStringField(TEXT("semanticConfigurationId"), Document.SemanticConfigurationId);
		Root->SetArrayField(TEXT("operations"), {MakeShared<FJsonValueObject>(Operation)});
		return Root;
	}

	bool TryParseJsonValue(
		const FString& ValueText,
		TSharedPtr<FJsonValue>& OutValue,
		FString& OutError)
	{
		TSharedPtr<FJsonObject> Wrapper;
		if (!FJsonSerializer::Deserialize(
				TJsonReaderFactory<>::Create(
					FString::Printf(TEXT("{\"value\":%s}"), *ValueText)),
				Wrapper) ||
			!Wrapper.IsValid() || !Wrapper->HasField(TEXT("value")))
		{
			OutError = FString::Printf(
				TEXT("State default '%s' is not a valid JSON value."),
				*ValueText);
			return false;
		}
		OutValue = Wrapper->TryGetField(TEXT("value"));
		if (!OutValue.IsValid())
		{
			OutError = TEXT("A state default must be a valid JSON value.");
			return false;
		}
		return true;
	}

	TSharedRef<FJsonObject> MakeExposure(const FFinareAuthoringExposure& Exposure)
	{
		const TSharedRef<FJsonObject> Json = MakeShared<FJsonObject>();
		Json->SetBoolField(TEXT("instanceEditable"), Exposure.bInstanceEditable);
		Json->SetBoolField(TEXT("saveGame"), Exposure.bSaveGame);
		Json->SetBoolField(TEXT("replicated"), Exposure.bReplicated);
		if (Exposure.Category.IsEmpty())
		{
			Json->SetField(TEXT("category"), MakeShared<FJsonValueNull>());
		}
		else
		{
			Json->SetStringField(TEXT("category"), Exposure.Category);
		}
		return Json;
	}

	bool MakeStateSpec(
		const FString& Name,
		const FString& TypeName,
		const FString& TypeKind,
		const FString& DefaultValueText,
		const FFinareAuthoringExposure& Exposure,
		TSharedPtr<FJsonObject>& OutSpec,
		FString& OutError)
	{
		TSharedPtr<FJsonValue> DefaultValue;
		if (Name.IsEmpty() || TypeName.IsEmpty() || TypeKind.IsEmpty() ||
			!TryParseJsonValue(DefaultValueText, DefaultValue, OutError))
		{
			if (OutError.IsEmpty())
			{
				OutError = TEXT("A state requires a name, C# type, type kind, and JSON default.");
			}
			return false;
		}
		OutSpec = MakeShared<FJsonObject>();
		OutSpec->SetStringField(TEXT("name"), Name);
		OutSpec->SetStringField(TEXT("typeName"), TypeName);
		OutSpec->SetStringField(TEXT("typeKind"), TypeKind);
		OutSpec->SetField(TEXT("defaultValue"), DefaultValue);
		OutSpec->SetObjectField(TEXT("exposure"), MakeExposure(Exposure));
		return true;
	}

	bool MakeStateExpected(
		const FFinareAuthoringState& State,
		TSharedPtr<FJsonObject>& OutExpected,
		FString& OutError)
	{
		TSharedPtr<FJsonValue> DefaultValue;
		if (!TryParseJsonValue(State.DefaultValue, DefaultValue, OutError))
		{
			return false;
		}
		OutExpected = MakeShared<FJsonObject>();
		OutExpected->SetStringField(TEXT("name"), State.Name);
		OutExpected->SetStringField(TEXT("typeMetadataName"), State.TypeMetadataName);
		OutExpected->SetStringField(TEXT("typeKind"), State.TypeKind);
		OutExpected->SetField(TEXT("defaultValue"), DefaultValue);
		OutExpected->SetObjectField(TEXT("exposure"), MakeExposure(State.Exposure));
		return true;
	}

	TSharedRef<FJsonObject> MakeFunctionParameters(
		const TArray<FFinareAuthoringParameter>& Parameters,
		bool bMetadataTypes)
	{
		const TSharedRef<FJsonObject> Container = MakeShared<FJsonObject>();
		TArray<TSharedPtr<FJsonValue>> Values;
		for (const FFinareAuthoringParameter& Parameter : Parameters)
		{
			const TSharedRef<FJsonObject> Json = MakeShared<FJsonObject>();
			Json->SetStringField(TEXT("name"), Parameter.Name);
			Json->SetStringField(
				bMetadataTypes ? TEXT("typeMetadataName") : TEXT("typeName"),
				Parameter.TypeMetadataName);
			Values.Add(MakeShared<FJsonValueObject>(Json));
		}
		Container->SetArrayField(TEXT("parameters"), MoveTemp(Values));
		return Container;
	}

	TSharedRef<FJsonObject> MakeFunctionSpec(
		const FString& Name,
		const FString& ReturnTypeName,
		bool bStatic,
		const TArray<FFinareAuthoringParameter>& Parameters)
	{
		const TSharedRef<FJsonObject> Spec = MakeFunctionParameters(Parameters, false);
		Spec->SetStringField(TEXT("name"), Name);
		Spec->SetStringField(TEXT("returnTypeName"), ReturnTypeName);
		Spec->SetBoolField(TEXT("isStatic"), bStatic);
		return Spec;
	}

	TSharedRef<FJsonObject> MakeFunctionExpected(
		const FFinareAuthoringFunction& Function)
	{
		const TSharedRef<FJsonObject> Expected = MakeFunctionParameters(Function.Parameters, true);
		Expected->SetStringField(TEXT("name"), Function.Name);
		Expected->SetStringField(
			TEXT("returnTypeMetadataName"),
			Function.ReturnTypeMetadataName);
		Expected->SetBoolField(TEXT("isStatic"), Function.bStatic);
		return Expected;
	}

	const FFinareAuthoringRegion* FindOwningBlock(
		const FFinareAuthoringFunction& Function,
		const FString& StatementId)
	{
		for (const FFinareAuthoringRegion& Region : Function.Regions)
		{
			if (Region.DirectStatementIds.Contains(StatementId))
			{
				return &Region;
			}
		}
		return nullptr;
	}

	FString ResolveStatementId(const FFinareAuthoringAction& Action)
	{
		return Action.DebugTargetId.IsEmpty() ? Action.Id : Action.DebugTargetId;
	}

	TArray<FString> GetStructuralStatementIds(
		const FFinareAuthoringFunction& Function,
		const FFinareAuthoringRegion& Block)
	{
		struct FStructuralStatement
		{
			FString Id;
			int32 Line = 0;
			int32 Column = 0;
		};
		TArray<FStructuralStatement> Ordered;
		for (const FString& ActionId : Block.DirectStatementIds)
		{
			const FFinareAuthoringAction* Action = Function.FindAction(ActionId);
			if (Action == nullptr)
			{
				continue;
			}
			const FString StatementId = ResolveStatementId(*Action);
			if (!Ordered.ContainsByPredicate([&StatementId](const FStructuralStatement& Entry)
			{
				return Entry.Id == StatementId;
			}))
			{
				Ordered.Add({StatementId, Action->Span.StartLine, Action->Span.StartColumn});
			}
		}
		for (const FString& ChildRegionId : Block.ChildRegionIds)
		{
			const FFinareAuthoringRegion* Child = Function.FindRegion(ChildRegionId);
			if (Child != nullptr && Child->Kind == TEXT("block") &&
				!Child->SourceNodeId.IsEmpty())
			{
				Ordered.Add({Child->SourceNodeId, Child->Span.StartLine, Child->Span.StartColumn});
			}
		}
		Ordered.Sort([](const FStructuralStatement& Left, const FStructuralStatement& Right)
		{
			if (Left.Line != Right.Line)
			{
				return Left.Line < Right.Line;
			}
			if (Left.Column != Right.Column)
			{
				return Left.Column < Right.Column;
			}
			return Left.Id < Right.Id;
		});
		TArray<FString> Result;
		Result.Reserve(Ordered.Num());
		for (const FStructuralStatement& Entry : Ordered)
		{
			Result.Add(Entry.Id);
		}
		return Result;
	}

	void GetStructuralSiblingPreconditions(
		const TArray<FString>& StatementIds,
		const FString& StatementId,
		FString& OutPrevious,
		FString& OutNext)
	{
		OutPrevious.Reset();
		OutNext.Reset();
		const int32 Index = StatementIds.IndexOfByKey(StatementId);
		if (Index > 0)
		{
			OutPrevious = StatementIds[Index - 1];
		}
		if (Index != INDEX_NONE && Index + 1 < StatementIds.Num())
		{
			OutNext = StatementIds[Index + 1];
		}
	}

	const FFinareAuthoringAction* FindContainingStatement(
		const FFinareAuthoringFunction& Function,
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
			if (const FFinareAuthoringAction* Action = Function.FindAction(Current))
			{
				if (Action->Ports.ContainsByPredicate([](const FFinareAuthoringPort& Port)
				{
					return Port.IsControl();
				}))
				{
					return Action;
				}
			}
			for (const FFinareAuthoringEdge& Edge : Function.Edges)
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
		const FFinareAuthoringFunction& Function,
		const FFinareAuthoringRegion& Block,
		const FString& StatementActionId,
		FString& OutPrevious,
		FString& OutNext)
	{
		const FFinareAuthoringAction* Action = Function.FindAction(StatementActionId);
		const FString StatementId = Action != nullptr
			                            ? ResolveStatementId(*Action)
			                            : StatementActionId;
		GetStructuralSiblingPreconditions(
			GetStructuralStatementIds(Function, Block),
			StatementId,
			OutPrevious,
			OutNext);
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
		const FFinareAuthoringFunction& Function,
		const FFinareAuthoringRegion& Block,
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
		const FFinareAuthoringAction* Last = Function.FindAction(LastId);
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

	const FFinareAuthoringPort* FindTypePort(
		const FFinareAuthoringFunction& Function,
		const FString& DisplayName)
	{
		for (const FFinareAuthoringAction& Action : Function.Actions)
		{
			for (const FFinareAuthoringPort& Port : Action.Ports)
			{
				if (!Port.IsControl() && Port.TypeDisplayName == DisplayName && !Port.TypeId.IsEmpty())
				{
					return &Port;
				}
			}
		}
		return nullptr;
	}

	FString FindBoundTypeId(
		const FFinareAuthoringFunction& Function,
		const FString& TypeName)
	{
		for (const FFinareAuthoringParameter& Parameter : Function.Parameters)
		{
			if (!Parameter.TypeId.IsEmpty() &&
				(Parameter.TypeDisplayName == TypeName ||
					Parameter.TypeMetadataName == TypeName))
			{
				return Parameter.TypeId;
			}
		}
		const FFinareAuthoringPort* Port = FindTypePort(Function, TypeName);
		return Port != nullptr ? Port->TypeId : FString();
	}

	TSharedRef<FJsonObject> LiteralExpression(
		const FString& ValueText,
		const FString& TypeId)
	{
		const TSharedRef<FJsonObject> Expression = MakeShared<FJsonObject>();
		Expression->SetStringField(TEXT("expression"), TEXT("literal"));
		Expression->SetStringField(TEXT("valueText"), ValueText);
		Expression->SetStringField(TEXT("expectedTypeId"), TypeId);
		return Expression;
	}

	TSharedRef<FJsonObject> LocalReference(
		const FFinareAuthoringParameter& Parameter);

	int32 FindTopLevelToken(const FString& Text, const FString& Token)
	{
		int32 Depth = 0;
		TCHAR Quote = 0;
		bool bEscaped = false;
		for (int32 Index = 0; Index + Token.Len() <= Text.Len(); ++Index)
		{
			const TCHAR Character = Text[Index];
			if (Quote != 0)
			{
				if (bEscaped)
				{
					bEscaped = false;
				}
				else if (Character == TEXT('\\'))
				{
					bEscaped = true;
				}
				else if (Character == Quote)
				{
					Quote = 0;
				}
				continue;
			}
			if (Character == TEXT('"') || Character == TEXT('\''))
			{
				Quote = Character;
				continue;
			}
			if (Character == TEXT('('))
			{
				++Depth;
				continue;
			}
			if (Character == TEXT(')'))
			{
				--Depth;
				continue;
			}
			if (Depth == 0 && Text.Mid(Index, Token.Len()) == Token)
			{
				return Index;
			}
		}
		return INDEX_NONE;
	}

	bool HasWholeOuterParentheses(const FString& Text)
	{
		if (Text.Len() < 2 || Text[0] != TEXT('(') || Text[Text.Len() - 1] != TEXT(')'))
		{
			return false;
		}
		int32 Depth = 0;
		TCHAR Quote = 0;
		bool bEscaped = false;
		for (int32 Index = 0; Index < Text.Len(); ++Index)
		{
			const TCHAR Character = Text[Index];
			if (Quote != 0)
			{
				if (bEscaped)
				{
					bEscaped = false;
				}
				else if (Character == TEXT('\\'))
				{
					bEscaped = true;
				}
				else if (Character == Quote)
				{
					Quote = 0;
				}
				continue;
			}
			if (Character == TEXT('"') || Character == TEXT('\''))
			{
				Quote = Character;
			}
			else if (Character == TEXT('('))
			{
				++Depth;
			}
			else if (Character == TEXT(')'))
			{
				--Depth;
				if (Depth == 0 && Index != Text.Len() - 1)
				{
					return false;
				}
				if (Depth < 0)
				{
					return false;
				}
			}
		}
		return Depth == 0 && Quote == 0;
	}

	class FSwitchPatternTextParser
	{
	public:
		FSwitchPatternTextParser(
			const FFinareAuthoringFunction& InFunction,
			FString InGoverningTypeId)
			: Function(InFunction), GoverningTypeId(MoveTemp(InGoverningTypeId))
		{
		}

		bool BuildLabel(
			const FString& Input,
			TSharedPtr<FJsonObject>& OutLabel,
			FString& OutError) const
		{
			FString Text = Input;
			Text.TrimStartAndEndInline();
			if (Text.StartsWith(TEXT("case "), ESearchCase::IgnoreCase))
			{
				Text.RightChopInline(5);
				Text.TrimStartAndEndInline();
			}
			if (Text.EndsWith(TEXT(":")))
			{
				Text.LeftChopInline(1);
				Text.TrimStartAndEndInline();
			}
			if (Text.IsEmpty())
			{
				OutError = TEXT("Enter a switch label such as 0, > 10, int value, var value, _, or default.");
				return false;
			}

			if (Text.Equals(TEXT("default"), ESearchCase::IgnoreCase))
			{
				OutLabel = MakeShared<FJsonObject>();
				OutLabel->SetStringField(TEXT("label"), TEXT("default"));
				return true;
			}

			FString PatternText = Text;
			FString GuardText;
			const int32 GuardIndex = FindTopLevelToken(PatternText, TEXT(" when "));
			if (GuardIndex != INDEX_NONE)
			{
				GuardText = PatternText.Mid(GuardIndex + 6);
				PatternText = PatternText.Left(GuardIndex);
				GuardText.TrimStartAndEndInline();
				PatternText.TrimStartAndEndInline();
			}

			TSharedPtr<FJsonObject> Pattern;
			if (!BuildPattern(PatternText, 0, Pattern, OutError))
			{
				return false;
			}
			TSharedPtr<FJsonObject> Guard;
			if (!GuardText.IsEmpty() && !BuildGuard(GuardText, Guard, OutError))
			{
				return false;
			}

			OutLabel = MakeShared<FJsonObject>();
			OutLabel->SetStringField(TEXT("label"), TEXT("pattern"));
			OutLabel->SetObjectField(TEXT("pattern"), Pattern.ToSharedRef());
			if (Guard.IsValid())
			{
				OutLabel->SetObjectField(TEXT("guard"), Guard.ToSharedRef());
			}
			else
			{
				OutLabel->SetField(TEXT("guard"), MakeShared<FJsonValueNull>());
			}
			return true;
		}

	private:
		bool BuildPattern(
			FString Text,
			int32 Depth,
			TSharedPtr<FJsonObject>& OutPattern,
			FString& OutError) const
		{
			Text.TrimStartAndEndInline();
			if (Text.IsEmpty() || Depth > 16)
			{
				OutError = Depth > 16
					           ? TEXT("A switch pattern cannot exceed 16 nested nodes.")
					           : TEXT("A switch pattern cannot be empty.");
				return false;
			}

			if (HasWholeOuterParentheses(Text))
			{
				TSharedPtr<FJsonObject> Inner;
				if (!BuildPattern(Text.Mid(1, Text.Len() - 2), Depth + 1, Inner, OutError))
				{
					return false;
				}
				OutPattern = MakeShared<FJsonObject>();
				OutPattern->SetStringField(TEXT("pattern"), TEXT("parenthesized"));
				OutPattern->SetObjectField(TEXT("inner"), Inner.ToSharedRef());
				return true;
			}

			for (const FString& Operator : {FString(TEXT(" or ")), FString(TEXT(" and "))})
			{
				const int32 OperatorIndex = FindTopLevelToken(Text, Operator);
				if (OperatorIndex != INDEX_NONE)
				{
					TSharedPtr<FJsonObject> Left;
					TSharedPtr<FJsonObject> Right;
					if (!BuildPattern(Text.Left(OperatorIndex), Depth + 1, Left, OutError) ||
						!BuildPattern(
							Text.Mid(OperatorIndex + Operator.Len()),
							Depth + 1,
							Right,
							OutError))
					{
						return false;
					}
					OutPattern = MakeShared<FJsonObject>();
					OutPattern->SetStringField(TEXT("pattern"), TEXT("logical"));
					FString TrimmedOperator = Operator;
					TrimmedOperator.TrimStartAndEndInline();
					OutPattern->SetStringField(TEXT("operator"), TrimmedOperator);
					OutPattern->SetObjectField(TEXT("left"), Left.ToSharedRef());
					OutPattern->SetObjectField(TEXT("right"), Right.ToSharedRef());
					return true;
				}
			}

			if (Text.StartsWith(TEXT("not "), ESearchCase::CaseSensitive))
			{
				TSharedPtr<FJsonObject> Operand;
				if (!BuildPattern(Text.Mid(4), Depth + 1, Operand, OutError))
				{
					return false;
				}
				OutPattern = MakeShared<FJsonObject>();
				OutPattern->SetStringField(TEXT("pattern"), TEXT("not"));
				OutPattern->SetObjectField(TEXT("operand"), Operand.ToSharedRef());
				return true;
			}

			for (const FString& Operator :
			     {FString(TEXT(">=")), FString(TEXT("<=")), FString(TEXT(">")), FString(TEXT("<"))})
			{
				if (Text.StartsWith(Operator))
				{
					FString ValueText = Text.Mid(Operator.Len());
					ValueText.TrimStartAndEndInline();
					if (ValueText.IsEmpty())
					{
						OutError = TEXT("A relational switch pattern requires a typed literal value.");
						return false;
					}
					OutPattern = MakeShared<FJsonObject>();
					OutPattern->SetStringField(TEXT("pattern"), TEXT("relational"));
					OutPattern->SetStringField(TEXT("operator"), Operator);
					OutPattern->SetObjectField(
						TEXT("value"),
						LiteralExpression(ValueText, GoverningTypeId));
					return true;
				}
			}

			if (Text == TEXT("_"))
			{
				OutPattern = MakeShared<FJsonObject>();
				OutPattern->SetStringField(TEXT("pattern"), TEXT("discard"));
				return true;
			}

			if (Text.StartsWith(TEXT("var "), ESearchCase::CaseSensitive))
			{
				FString Designation = Text.Mid(4);
				Designation.TrimStartAndEndInline();
				if (Designation.IsEmpty())
				{
					OutError = TEXT("A var pattern requires a variable name or underscore discard.");
					return false;
				}
				const bool bDiscard = Designation == TEXT("_");
				OutPattern = MakeShared<FJsonObject>();
				OutPattern->SetStringField(TEXT("pattern"), TEXT("var"));
				OutPattern->SetStringField(
					TEXT("variableName"),
					bDiscard ? FString() : Designation);
				OutPattern->SetBoolField(TEXT("isDiscard"), bDiscard);
				return true;
			}

			FString TypeName;
			FString Designation;
			if (Text.Split(TEXT(" "), &TypeName, &Designation))
			{
				TypeName.TrimStartAndEndInline();
				Designation.TrimStartAndEndInline();
				const FString TypeId = FindBoundTypeId(Function, TypeName);
				if (!TypeId.IsEmpty() && !Designation.IsEmpty())
				{
					const bool bDiscard = Designation == TEXT("_");
					OutPattern = MakeShared<FJsonObject>();
					OutPattern->SetStringField(TEXT("pattern"), TEXT("declaration"));
					OutPattern->SetStringField(TEXT("typeName"), TypeName);
					OutPattern->SetStringField(
						TEXT("variableName"),
						bDiscard ? FString() : Designation);
					OutPattern->SetBoolField(TEXT("isDiscard"), bDiscard);
					OutPattern->SetStringField(TEXT("expectedTypeId"), TypeId);
					return true;
				}
			}
			else
			{
				const FString TypeId = FindBoundTypeId(Function, Text);
				if (!TypeId.IsEmpty() && Text != TEXT("true") && Text != TEXT("false") &&
					Text != TEXT("null"))
				{
					OutPattern = MakeShared<FJsonObject>();
					OutPattern->SetStringField(TEXT("pattern"), TEXT("type"));
					OutPattern->SetStringField(TEXT("typeName"), Text);
					OutPattern->SetStringField(TEXT("expectedTypeId"), TypeId);
					return true;
				}
			}

			OutPattern = MakeShared<FJsonObject>();
			OutPattern->SetStringField(TEXT("pattern"), TEXT("constant"));
			OutPattern->SetObjectField(
				TEXT("value"),
				LiteralExpression(Text, GoverningTypeId));
			return true;
		}

		bool BuildGuard(
			FString Text,
			TSharedPtr<FJsonObject>& OutGuard,
			FString& OutError) const
		{
			Text.TrimStartAndEndInline();
			const FString BoolTypeId = FindBoundTypeId(Function, TEXT("bool"));
			if (BoolTypeId.IsEmpty())
			{
				OutError = TEXT("A guarded switch label requires a bound bool type.");
				return false;
			}
			if (Text == TEXT("true") || Text == TEXT("false"))
			{
				OutGuard = LiteralExpression(Text, BoolTypeId);
				return true;
			}
			const FFinareAuthoringParameter* Parameter =
				Function.Parameters.FindByPredicate([&Text, &BoolTypeId](
					const FFinareAuthoringParameter& Candidate)
					{
						return Candidate.Name == Text && Candidate.TypeId == BoolTypeId &&
							!Candidate.Id.IsEmpty();
					});
			if (Parameter == nullptr)
			{
				OutError = TEXT("A switch guard currently accepts true, false, or a bool function parameter.");
				return false;
			}
			OutGuard = LocalReference(*Parameter);
			return true;
		}

		const FFinareAuthoringFunction& Function;
		FString GoverningTypeId;
	};

	struct FSwitchSectionContext
	{
		const FFinareAuthoringRegion* Region = nullptr;
		FString SectionId;
		TArray<const FFinareAuthoringAction*> Labels;
		bool bHasDefault = false;
	};

	bool ResolveSwitchSections(
		const FFinareAuthoringFunction& Function,
		const FFinareAuthoringAction& SwitchStatement,
		const FFinareAuthoringRegion*& OutSwitchRegion,
		TArray<FSwitchSectionContext>& OutSections,
		FString& OutError)
	{
		OutSwitchRegion = Function.FindRegion(SwitchStatement.RegionId);
		if (SwitchStatement.GraphKind != TEXT("switch") ||
			OutSwitchRegion == nullptr || OutSwitchRegion->Kind != TEXT("switch") ||
			OutSwitchRegion->SourceNodeId != SwitchStatement.Id)
		{
			OutError = TEXT("The selected action is not the exact owner of a structured switch region.");
			return false;
		}

		for (const FString& ChildId : OutSwitchRegion->ChildRegionIds)
		{
			const FFinareAuthoringRegion* Child = Function.FindRegion(ChildId);
			FSwitchSectionContext Section;
			Section.Region = Child;
			if (Child == nullptr || Child->Kind != TEXT("switchSection"))
			{
				OutError = TEXT("The switch contains an invalid section region.");
				return false;
			}
			for (const FString& ActionId : Child->DirectActionIds)
			{
				const FFinareAuthoringAction* Action = Function.FindAction(ActionId);
				if (Action != nullptr && Action->GraphKind == TEXT("switchCase"))
				{
					const FString* SectionId = Action->Properties.Find(TEXT("switchSectionId"));
					const FString* BodyId = Action->Properties.Find(TEXT("sectionId"));
					if (SectionId == nullptr || BodyId == nullptr ||
						!UE::Finare::AuthoringSurface::Private::IsSemanticId(*SectionId) ||
						*BodyId != Child->SourceNodeId ||
						(!Section.SectionId.IsEmpty() && Section.SectionId != *SectionId))
					{
						OutError = TEXT("A switch label is missing its exact section/body identity.");
						return false;
					}
					Section.SectionId = *SectionId;
					Section.Labels.Add(Action);
					Section.bHasDefault |=
						Action->Properties.FindRef(TEXT("isDefault")) == TEXT("true");
				}
			}
			if (Section.SectionId.IsEmpty() || Section.Labels.IsEmpty())
			{
				OutError = TEXT("Every switch section requires at least one semantic case label.");
				return false;
			}
			OutSections.Add(MoveTemp(Section));
		}
		return true;
	}

	bool ResolveSwitchSectionForLabel(
		const FFinareAuthoringFunction& Function,
		const FFinareAuthoringAction& CaseLabel,
		const FFinareAuthoringAction*& OutSwitchStatement,
		TArray<FSwitchSectionContext>& OutSections,
		int32& OutSectionIndex,
		FString& OutError)
	{
		if (CaseLabel.GraphKind != TEXT("switchCase"))
		{
			OutError = TEXT("Select a case/default action before editing a switch section.");
			return false;
		}
		const FFinareAuthoringRegion* SectionRegion = Function.FindRegion(CaseLabel.RegionId);
		const FFinareAuthoringRegion* SwitchRegion = SectionRegion != nullptr
			                                             ? Function.FindRegion(SectionRegion->ParentRegionId)
			                                             : nullptr;
		OutSwitchStatement = SwitchRegion != nullptr
			                     ? Function.FindAction(SwitchRegion->SourceNodeId)
			                     : nullptr;
		const FFinareAuthoringRegion* ResolvedSwitchRegion = nullptr;
		if (SectionRegion == nullptr || SectionRegion->Kind != TEXT("switchSection") ||
			OutSwitchStatement == nullptr ||
			!ResolveSwitchSections(
				Function,
				*OutSwitchStatement,
				ResolvedSwitchRegion,
				OutSections,
				OutError))
		{
			if (OutError.IsEmpty())
			{
				OutError = TEXT("The selected label does not belong to a structured switch section.");
			}
			return false;
		}
		OutSectionIndex = OutSections.IndexOfByPredicate([SectionRegion](
			const FSwitchSectionContext& Section)
			{
				return Section.Region == SectionRegion;
			});
		if (OutSectionIndex == INDEX_NONE)
		{
			OutError = TEXT("The selected switch section is no longer present in sibling order.");
			return false;
		}
		return true;
	}

	TArray<TSharedPtr<FJsonValue>> JsonIds(
		const TArray<const FFinareAuthoringAction*>& Labels)
	{
		TArray<TSharedPtr<FJsonValue>> Values;
		for (const FFinareAuthoringAction* Label : Labels)
		{
			Values.Add(MakeShared<FJsonValueString>(Label->Id));
		}
		return Values;
	}

	const FFinareAuthoringRegion* FindNearestEnclosingRegion(
		const FFinareAuthoringFunction& Function,
		const FFinareAuthoringRegion& Start,
		const TArray<FString>& Kinds)
	{
		const FFinareAuthoringRegion* Current = &Start;
		TSet<FString> Visited;
		while (Current != nullptr && !Visited.Contains(Current->Id))
		{
			Visited.Add(Current->Id);
			if (Kinds.Contains(Current->Kind))
			{
				return Current;
			}
			Current = Current->ParentRegionId.IsEmpty()
				          ? nullptr
				          : Function.FindRegion(Current->ParentRegionId);
		}
		return nullptr;
	}

	TSharedRef<FJsonObject> LocalReference(const FFinareAuthoringParameter& Parameter)
	{
		const TSharedRef<FJsonObject> Expression = MakeShared<FJsonObject>();
		Expression->SetStringField(TEXT("expression"), TEXT("localReference"));
		Expression->SetStringField(TEXT("sourceDefinitionId"), Parameter.Id);
		Expression->SetStringField(TEXT("expectedName"), Parameter.Name);
		Expression->SetStringField(TEXT("expectedTypeId"), Parameter.TypeId);
		return Expression;
	}

	bool IsSimpleCSharpIdentifier(const FString& Value)
	{
		if (Value.IsEmpty() || !(FChar::IsAlpha(Value[0]) || Value[0] == TEXT('_')))
		{
			return false;
		}
		for (int32 Index = 1; Index < Value.Len(); ++Index)
		{
			if (!(FChar::IsAlnum(Value[Index]) || Value[Index] == TEXT('_')))
			{
				return false;
			}
		}
		return true;
	}

	TSharedRef<FJsonObject> AuthoredDataInputExpression(
		const FString& ValueText,
		const FFinareAuthoringPort& Port)
	{
		if (ValueText.Equals(TEXT("default"), ESearchCase::IgnoreCase))
		{
			const TSharedRef<FJsonObject> Expression = MakeShared<FJsonObject>();
			Expression->SetStringField(TEXT("expression"), TEXT("default"));
			Expression->SetStringField(TEXT("typeName"), Port.TypeDisplayName);
			Expression->SetStringField(TEXT("expectedTypeId"), Port.TypeId);
			return Expression;
		}

		if (ValueText.StartsWith(TEXT("-")) && ValueText.Len() > 1)
		{
			const TSharedRef<FJsonObject> Expression = MakeShared<FJsonObject>();
			Expression->SetStringField(TEXT("expression"), TEXT("unary"));
			Expression->SetStringField(TEXT("operator"), TEXT("-"));
			Expression->SetObjectField(
				TEXT("operand"),
				LiteralExpression(ValueText.Mid(1), Port.TypeId));
			Expression->SetStringField(TEXT("expectedTypeId"), Port.TypeId);
			return Expression;
		}

		return LiteralExpression(ValueText, Port.TypeId);
	}
}

bool FFinareAuthoringSurfaceReader::BuildDataEdgeDisconnectionPatch(
	const FFinareAuthoringSurfaceDocument& Document,
	const FFinareAuthoringFunction& Function,
	const FFinareAuthoringEdge& ExpectedEdge,
	FString& OutPatchJson,
	FString& OutError)
{
	using namespace UE::Finare::AuthoringSurface::Private;
	using namespace UE::Finare::AuthoringSurface::Editing::Private;
	const FFinareAuthoringAction* Source = Function.FindAction(ExpectedEdge.FromNode);
	const FFinareAuthoringAction* Target = Function.FindAction(ExpectedEdge.ToNode);
	const FFinareAuthoringPort* SourcePort = Source != nullptr
		                                         ? Source->FindPort(ExpectedEdge.FromPort, TEXT("output"))
		                                         : nullptr;
	const FFinareAuthoringPort* TargetPort = Target != nullptr
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
	return SerializeRoot(MakePatchRoot(Document, Operation), OutPatchJson, OutError,
	                     TEXT("the data-edge disconnection patch"));
}

bool FFinareAuthoringSurfaceReader::BuildDataInputValuePatch(
	const FFinareAuthoringSurfaceDocument& Document,
	const FFinareAuthoringFunction& Function,
	const FFinareAuthoringEdge& ExpectedEdge,
	const FString& NewValueText,
	FString& OutPatchJson,
	FString& OutError)
{
	using namespace UE::Finare::AuthoringSurface::Private;
	using namespace UE::Finare::AuthoringSurface::Editing::Private;
	FString TrimmedValue = NewValueText;
	TrimmedValue.TrimStartAndEndInline();
	const FFinareAuthoringAction* Source = Function.FindAction(ExpectedEdge.FromNode);
	const FFinareAuthoringAction* Target = Function.FindAction(ExpectedEdge.ToNode);
	const FFinareAuthoringPort* SourcePort = Source != nullptr
		                                         ? Source->FindPort(ExpectedEdge.FromPort, TEXT("output"))
		                                         : nullptr;
	const FFinareAuthoringPort* TargetPort = Target != nullptr
		                                         ? Target->FindPort(ExpectedEdge.ToPort, TEXT("input"))
		                                         : nullptr;
	if (!IsSemanticId(Document.SourceCompilationId) ||
		!IsSemanticId(Document.SemanticConfigurationId) ||
		!IsSemanticId(ExpectedEdge.Id) || ExpectedEdge.Kind != TEXT("data") ||
		SourcePort == nullptr || TargetPort == nullptr || SourcePort->IsControl() ||
		TargetPort->IsControl() || TargetPort->TypeId.IsEmpty() ||
		TargetPort->TypeDisplayName.IsEmpty() || SourcePort->TypeId != TargetPort->TypeId ||
		TrimmedValue.IsEmpty())
	{
		OutError = TEXT("A pin value edit requires an exact typed data edge and a C# literal or 'default'.");
		return false;
	}

	const TSharedRef<FJsonObject> Operation = MakeShared<FJsonObject>();
	Operation->SetStringField(TEXT("operation"), TEXT("setDataInputValue"));
	Operation->SetStringField(TEXT("targetNodeId"), ExpectedEdge.ToNode);
	Operation->SetStringField(TEXT("targetPort"), ExpectedEdge.ToPort);
	Operation->SetStringField(TEXT("expectedEdgeId"), ExpectedEdge.Id);
	Operation->SetStringField(TEXT("expectedSourceNodeId"), ExpectedEdge.FromNode);
	Operation->SetStringField(TEXT("expectedSourcePort"), ExpectedEdge.FromPort);
	Operation->SetStringField(TEXT("expectedTypeId"), TargetPort->TypeId);
	Operation->SetObjectField(
		TEXT("newValue"),
		AuthoredDataInputExpression(TrimmedValue, *TargetPort));
	return SerializeRoot(
		MakePatchRoot(Document, Operation),
		OutPatchJson,
		OutError,
		TEXT("the typed data-input value patch"));
}

bool FFinareAuthoringSurfaceReader::BuildDataInputPromotionPatch(
	const FFinareAuthoringSurfaceDocument& Document,
	const FFinareAuthoringFunction& Function,
	const FFinareAuthoringEdge& ExpectedEdge,
	const FString& LocalName,
	FString& OutPatchJson,
	FString& OutError)
{
	using namespace UE::Finare::AuthoringSurface::Private;
	using namespace UE::Finare::AuthoringSurface::Editing::Private;
	FString TrimmedName = LocalName;
	TrimmedName.TrimStartAndEndInline();
	const FFinareAuthoringAction* Source = Function.FindAction(ExpectedEdge.FromNode);
	const FFinareAuthoringAction* Target = Function.FindAction(ExpectedEdge.ToNode);
	const FFinareAuthoringPort* SourcePort = Source != nullptr
		                                         ? Source->FindPort(ExpectedEdge.FromPort, TEXT("output"))
		                                         : nullptr;
	const FFinareAuthoringPort* TargetPort = Target != nullptr
		                                         ? Target->FindPort(ExpectedEdge.ToPort, TEXT("input"))
		                                         : nullptr;
	const FFinareAuthoringAction* Statement = Source != nullptr
		                                          ? FindContainingStatement(Function, Source->Id)
		                                          : nullptr;
	const FFinareAuthoringRegion* Block = Statement != nullptr
		                                      ? FindOwningBlock(Function, Statement->Id)
		                                      : nullptr;
	if (!IsSemanticId(Document.SourceCompilationId) ||
		!IsSemanticId(Document.SemanticConfigurationId) ||
		!IsSemanticId(ExpectedEdge.Id) || ExpectedEdge.Kind != TEXT("data") ||
		Source == nullptr || Target == nullptr || SourcePort == nullptr || TargetPort == nullptr ||
		SourcePort->IsControl() || TargetPort->IsControl() || SourcePort->TypeId.IsEmpty() ||
		SourcePort->TypeDisplayName.IsEmpty() || SourcePort->TypeId != TargetPort->TypeId ||
		Statement == nullptr || Block == nullptr ||
		(Statement->GraphKind != TEXT("variable") && Statement->GraphKind != TEXT("return")) ||
		!IsSimpleCSharpIdentifier(TrimmedName))
	{
		OutError = TEXT(
			"Promote to Local requires a named, typed pure expression inside an explicit local initializer or return statement.");
		return false;
	}

	FString Previous;
	FString Next;
	GetSiblingPreconditions(Function, *Block, Statement->Id, Previous, Next);
	const TSharedRef<FJsonObject> Operation = MakeShared<FJsonObject>();
	Operation->SetStringField(TEXT("operation"), TEXT("introduceTemporary"));
	Operation->SetStringField(TEXT("targetNodeId"), Source->Id);
	Operation->SetStringField(TEXT("expectedContainingStatementId"), Statement->Id);
	Operation->SetStringField(TEXT("expectedParentBlockId"), Block->SourceNodeId);
	SetOptionalId(Operation, TEXT("expectedPreviousSiblingId"), Previous);
	SetOptionalId(Operation, TEXT("expectedNextSiblingId"), Next);
	Operation->SetStringField(TEXT("name"), TrimmedName);
	Operation->SetStringField(TEXT("typeName"), SourcePort->TypeDisplayName);
	Operation->SetStringField(TEXT("expectedTypeId"), SourcePort->TypeId);
	return SerializeRoot(
		MakePatchRoot(Document, Operation),
		OutPatchJson,
		OutError,
		TEXT("the data-input promotion patch"));
}

bool FFinareAuthoringSurfaceReader::BuildControlEdgeReconnectionPatch(
	const FFinareAuthoringSurfaceDocument& Document,
	const FFinareAuthoringFunction& Function,
	const FFinareAuthoringEdge& ExpectedEdge,
	const FString& NewSourceNodeId,
	const FString& NewSourcePort,
	FString& OutPatchJson,
	FString& OutError)
{
	using namespace UE::Finare::AuthoringSurface::Private;
	using namespace UE::Finare::AuthoringSurface::Editing::Private;
	const FFinareAuthoringAction* ExpectedSource =
		Function.FindAction(ExpectedEdge.FromNode);
	const FFinareAuthoringAction* NewSource = Function.FindAction(NewSourceNodeId);
	const FFinareAuthoringAction* Target = Function.FindAction(ExpectedEdge.ToNode);
	const FFinareAuthoringPort* ExpectedSourcePort = ExpectedSource != nullptr
		                                                 ? ExpectedSource->FindPort(
			                                                 ExpectedEdge.FromPort, TEXT("output"))
		                                                 : nullptr;
	const FFinareAuthoringPort* NewSourceOutput = NewSource != nullptr
		                                              ? NewSource->FindPort(NewSourcePort, TEXT("output"))
		                                              : nullptr;
	const FFinareAuthoringPort* TargetInput = Target != nullptr
		                                          ? Target->FindPort(ExpectedEdge.ToPort, TEXT("input"))
		                                          : nullptr;
	const auto CountControlPorts = [](const FFinareAuthoringAction* Action,
	                                  const TCHAR* Direction)
	{
		int32 Count = 0;
		if (Action != nullptr)
		{
			for (const FFinareAuthoringPort& Port : Action->Ports)
			{
				Count += Port.Direction == Direction && Port.IsControl() ? 1 : 0;
			}
		}
		return Count;
	};
	const int32 NewSourceControlOutputCount = CountControlPorts(NewSource, TEXT("output"));
	const int32 TargetControlInputCount = CountControlPorts(Target, TEXT("input"));
	if (!IsSemanticId(ExpectedEdge.Id) || ExpectedEdge.Kind != TEXT("control") ||
		ExpectedSource == nullptr || NewSource == nullptr || Target == nullptr ||
		ExpectedSourcePort == nullptr || NewSourceOutput == nullptr || TargetInput == nullptr ||
		!ExpectedSourcePort->IsControl() || !NewSourceOutput->IsControl() ||
		!TargetInput->IsControl() || NewSourceControlOutputCount != 1 ||
		TargetControlInputCount != 1 || NewSource->GraphKind == TEXT("entry") ||
		NewSource->GraphKind == TEXT("switchCase") ||
		NewSourceNodeId == ExpectedEdge.ToNode ||
		(ExpectedEdge.FromNode == NewSourceNodeId && ExpectedEdge.FromPort == NewSourcePort))
	{
		OutError = TEXT(
			"Control reconnection requires a distinct single-path control output and an occupied single-path control input.");
		return false;
	}

	const FFinareAuthoringRegion* TargetBlock = FindOwningBlock(Function, Target->Id);
	const FFinareAuthoringRegion* SourceBlock = FindOwningBlock(Function, NewSource->Id);
	if (TargetBlock == nullptr || SourceBlock == nullptr)
	{
		OutError = TEXT("Both control endpoints must belong to explicit block or switch-section regions.");
		return false;
	}
	if (TargetBlock->Id != SourceBlock->Id &&
		(Target->GraphKind == TEXT("branch") || Target->GraphKind == TEXT("loop") ||
			Target->GraphKind == TEXT("switch") || Target->GraphKind == TEXT("return") ||
			Target->GraphKind == TEXT("break") || Target->GraphKind == TEXT("continue")))
	{
		OutError = TEXT(
			"Cross-region control reconnection moves only variable or expression statements. Use the node's explicit structured transform instead.");
		return false;
	}

	const FString TargetStatementId = ResolveStatementId(*Target);
	const FString NewSourceStatementId = ResolveStatementId(*NewSource);
	if (TargetStatementId == NewSourceStatementId)
	{
		OutError = TEXT("A control edge cannot reorder a statement relative to itself.");
		return false;
	}

	TArray<FString> SourceStructuralIds = GetStructuralStatementIds(Function, *TargetBlock);
	TArray<FString> DestinationStructuralIds = TargetBlock->Id == SourceBlock->Id
		                                           ? SourceStructuralIds
		                                           : GetStructuralStatementIds(Function, *SourceBlock);
	const int32 TargetIndex = SourceStructuralIds.IndexOfByKey(TargetStatementId);
	if (TargetIndex == INDEX_NONE || !DestinationStructuralIds.Contains(NewSourceStatementId))
	{
		OutError = TEXT(
			"Both control endpoints must resolve to direct statements in their selected structured blocks.");
		return false;
	}
	FString SourcePrevious;
	FString SourceNext;
	GetStructuralSiblingPreconditions(
		SourceStructuralIds,
		TargetStatementId,
		SourcePrevious,
		SourceNext);
	if (TargetBlock->Id == SourceBlock->Id)
	{
		DestinationStructuralIds.RemoveAt(TargetIndex);
	}
	const int32 DestinationPreviousIndex = DestinationStructuralIds.IndexOfByKey(
		NewSourceStatementId);
	if (DestinationPreviousIndex == INDEX_NONE)
	{
		OutError = TEXT("The new control predecessor disappeared from the structural statement order.");
		return false;
	}
	const FString DestinationPrevious = DestinationStructuralIds[DestinationPreviousIndex];
	const FString DestinationNext = DestinationPreviousIndex + 1 < DestinationStructuralIds.Num()
		                                ? DestinationStructuralIds[DestinationPreviousIndex + 1]
		                                : FString();
	if (TargetBlock->Id == SourceBlock->Id &&
		SourcePrevious == DestinationPrevious && SourceNext == DestinationNext)
	{
		OutError = TEXT("This control wire already represents the requested statement order.");
		return false;
	}

	const TSharedRef<FJsonObject> Operation = MakeShared<FJsonObject>();
	Operation->SetStringField(TEXT("operation"), TEXT("moveStatement"));
	Operation->SetStringField(TEXT("targetNodeId"), TargetStatementId);
	Operation->SetStringField(TEXT("expectedSourceParentBlockId"), TargetBlock->SourceNodeId);
	SetOptionalId(Operation, TEXT("expectedSourcePreviousSiblingId"), SourcePrevious);
	SetOptionalId(Operation, TEXT("expectedSourceNextSiblingId"), SourceNext);
	Operation->SetStringField(TEXT("destinationParentBlockId"), SourceBlock->SourceNodeId);
	SetOptionalId(
		Operation,
		TEXT("expectedDestinationPreviousSiblingId"),
		DestinationPrevious);
	SetOptionalId(
		Operation,
		TEXT("expectedDestinationNextSiblingId"),
		DestinationNext);
	return SerializeRoot(
		MakePatchRoot(Document, Operation),
		OutPatchJson,
		OutError,
		TEXT("the region-aware control-edge reconnection patch"));
}

bool FFinareAuthoringSurfaceReader::BuildSharedDataEdgePatchPlan(
	const FFinareAuthoringSurfaceDocument& Document,
	const FFinareAuthoringFunction& Function,
	const FFinareAuthoringEdge& ExpectedEdge,
	const FString& NewSourceNodeId,
	const FString& NewSourcePort,
	FString& OutPlanJson,
	FString& OutError)
{
	using namespace UE::Finare::AuthoringSurface::Editing::Private;
	const FFinareAuthoringAction* Source = Function.FindAction(NewSourceNodeId);
	const FFinareAuthoringAction* Target = Function.FindAction(ExpectedEdge.ToNode);
	const FFinareAuthoringPort* SourcePort = Source != nullptr
		                                         ? Source->FindPort(NewSourcePort, TEXT("output"))
		                                         : nullptr;
	const FFinareAuthoringPort* TargetPort = Target != nullptr
		                                         ? Target->FindPort(ExpectedEdge.ToPort, TEXT("input"))
		                                         : nullptr;
	const FFinareAuthoringAction* SourceStatement =
		FindContainingStatement(Function, NewSourceNodeId);
	const FFinareAuthoringAction* TargetStatement =
		FindContainingStatement(Function, ExpectedEdge.FromNode);
	const FFinareAuthoringRegion* SourceBlock = SourceStatement != nullptr
		                                            ? FindOwningBlock(Function, SourceStatement->Id)
		                                            : nullptr;
	const FFinareAuthoringRegion* TargetBlock = TargetStatement != nullptr
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
	GetSiblingPreconditions(
		Function, *SourceBlock, SourceStatement->Id, SourcePrevious, SourceNext);
	GetSiblingPreconditions(
		Function, *TargetBlock, TargetStatement->Id, TargetPrevious, TargetNext);
	// The complete semantic-id payload keeps automatic names stable and prevents two
	// distinct expression nodes from sharing the old six-hex-character prefix.
	const FString TemporaryName = TEXT("sharedValue_") + NewSourceNodeId.Mid(3).ToLower();
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

bool FFinareAuthoringSurfaceReader::BuildBasicNodeInsertionPatch(
	const FFinareAuthoringSurfaceDocument& Document,
	const FFinareAuthoringFunction& Function,
	const FFinareAuthoringRegion& BlockRegion,
	const FString& TemplateId,
	FString& OutPatchJson,
	FString& OutError)
{
	using namespace UE::Finare::AuthoringSurface::Editing::Private;
	FString Previous;
	FString Next;
	if (!GetInsertionGap(Function, BlockRegion, Previous, Next))
	{
		OutError = TEXT("Choose an explicit function, branch, loop, or switch-section block before adding a node.");
		return false;
	}

	const TSharedRef<FJsonObject> Statement = MakeShared<FJsonObject>();
	if (TemplateId == TEXT("local.int"))
	{
		const FFinareAuthoringPort* Type = FindTypePort(Function, TEXT("int"));
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
		TArray<const FFinareAuthoringParameter*> Operands;
		for (const FFinareAuthoringParameter& Parameter : Function.Parameters)
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
	else if (TemplateId == TEXT("flow.switch"))
	{
		const FFinareAuthoringParameter* GoverningParameter =
			Function.Parameters.FindByPredicate([](
				const FFinareAuthoringParameter& Parameter)
				{
					return Parameter.TypeDisplayName == TEXT("int") &&
						!Parameter.Id.IsEmpty() && !Parameter.TypeId.IsEmpty();
				});
		if (GoverningParameter == nullptr)
		{
			OutError = TEXT("Switch requires a typed int parameter in the active function.");
			return false;
		}

		TSharedPtr<FJsonObject> FirstLabel;
		TSharedPtr<FJsonObject> DefaultLabel;
		FSwitchPatternTextParser Parser(Function, GoverningParameter->TypeId);
		if (!Parser.BuildLabel(TEXT("0"), FirstLabel, OutError) ||
			!Parser.BuildLabel(TEXT("default"), DefaultLabel, OutError))
		{
			return false;
		}
		const auto MakeSection = [](const TSharedPtr<FJsonObject>& Label)
		{
			const TSharedRef<FJsonObject> Section = MakeShared<FJsonObject>();
			Section->SetArrayField(
				TEXT("labels"),
				{MakeShared<FJsonValueObject>(Label)});
			return Section;
		};

		Statement->SetStringField(TEXT("statement"), TEXT("switch"));
		Statement->SetObjectField(
			TEXT("governingExpression"),
			LocalReference(*GoverningParameter));
		Statement->SetStringField(
			TEXT("expectedGoverningTypeId"),
			GoverningParameter->TypeId);
		Statement->SetArrayField(
			TEXT("sections"),
			{
				MakeShared<FJsonValueObject>(MakeSection(FirstLabel)),
				MakeShared<FJsonValueObject>(MakeSection(DefaultLabel)),
			});
	}
	else if (TemplateId == TEXT("flow.branch") || TemplateId == TEXT("flow.while") ||
		TemplateId == TEXT("flow.doWhile"))
	{
		const FFinareAuthoringPort* Type = FindTypePort(Function, TEXT("bool"));
		if (Type == nullptr)
		{
			OutError = TEXT("This function has no bound bool type for structured control flow.");
			return false;
		}
		const FString StatementKind = TemplateId == TEXT("flow.branch")
			                              ? TEXT("if")
			                              : TemplateId == TEXT("flow.doWhile")
			                              ? TEXT("doWhile")
			                              : TEXT("while");
		Statement->SetStringField(TEXT("statement"), StatementKind);
		const TSharedRef<FJsonObject> Condition = MakeShared<FJsonObject>();
		Condition->SetStringField(TEXT("expression"), TEXT("literal"));
		Condition->SetStringField(TEXT("valueText"), TEXT("true"));
		Condition->SetStringField(TEXT("expectedTypeId"), Type->TypeId);
		Statement->SetObjectField(TEXT("condition"), Condition);
	}
	else if (TemplateId == TEXT("flow.break") || TemplateId == TEXT("flow.continue"))
	{
		const bool bContinue = TemplateId == TEXT("flow.continue");
		const TArray<FString> AllowedKinds = bContinue
			                                     ? TArray<FString>{TEXT("loop")}
			                                     : TArray<FString>{TEXT("loop"), TEXT("switch")};
		if (FindNearestEnclosingRegion(Function, BlockRegion, AllowedKinds) == nullptr)
		{
			OutError = bContinue
				           ? TEXT("Continue requires a selected block inside a loop region.")
				           : TEXT("Break requires a selected block inside a loop or switch region.");
			return false;
		}
		Statement->SetStringField(
			TEXT("statement"),
			bContinue ? TEXT("continue") : TEXT("break"));
	}
	else if (TemplateId == TEXT("flow.for"))
	{
		const FFinareAuthoringPort* IntType = FindTypePort(Function, TEXT("int"));
		const FFinareAuthoringPort* BoolType = FindTypePort(Function, TEXT("bool"));
		if (IntType == nullptr || BoolType == nullptr)
		{
			OutError = TEXT("For Loop requires bound int and bool types in the active function.");
			return false;
		}

		const FString VariableName =
			FString::Printf(TEXT("graphIndex%d"), BlockRegion.DirectStatementIds.Num() + 1);
		const auto MakeForLocal = [&VariableName, IntType]()
		{
			const TSharedRef<FJsonObject> Reference = MakeShared<FJsonObject>();
			Reference->SetStringField(TEXT("expression"), TEXT("forLocalReference"));
			Reference->SetNumberField(TEXT("variableIndex"), 0);
			Reference->SetStringField(TEXT("expectedName"), VariableName);
			Reference->SetStringField(TEXT("expectedTypeId"), IntType->TypeId);
			return Reference;
		};
		const auto MakeIntLiteral = [IntType](const FString& Value)
		{
			const TSharedRef<FJsonObject> Literal = MakeShared<FJsonObject>();
			Literal->SetStringField(TEXT("expression"), TEXT("literal"));
			Literal->SetStringField(TEXT("valueText"), Value);
			Literal->SetStringField(TEXT("expectedTypeId"), IntType->TypeId);
			return Literal;
		};

		const TSharedRef<FJsonObject> Variable = MakeShared<FJsonObject>();
		Variable->SetStringField(TEXT("name"), VariableName);
		Variable->SetObjectField(TEXT("initializer"), MakeIntLiteral(TEXT("0")));
		Variable->SetStringField(TEXT("expectedTypeId"), IntType->TypeId);
		const TSharedRef<FJsonObject> Declaration = MakeShared<FJsonObject>();
		Declaration->SetStringField(TEXT("typeName"), TEXT("int"));
		Declaration->SetArrayField(
			TEXT("variables"),
			{MakeShared<FJsonValueObject>(Variable)});

		const TSharedRef<FJsonObject> Condition = MakeShared<FJsonObject>();
		Condition->SetStringField(TEXT("expression"), TEXT("binary"));
		Condition->SetStringField(TEXT("operator"), TEXT("<"));
		Condition->SetObjectField(TEXT("left"), MakeForLocal());
		Condition->SetObjectField(TEXT("right"), MakeIntLiteral(TEXT("1")));
		Condition->SetStringField(TEXT("expectedTypeId"), BoolType->TypeId);

		const TSharedRef<FJsonObject> Incrementor = MakeShared<FJsonObject>();
		Incrementor->SetObjectField(TEXT("operand"), MakeForLocal());
		Incrementor->SetStringField(TEXT("operator"), TEXT("++"));
		Incrementor->SetBoolField(TEXT("isPostfix"), true);
		Incrementor->SetStringField(TEXT("expectedTypeId"), IntType->TypeId);

		Statement->SetStringField(TEXT("statement"), TEXT("for"));
		Statement->SetObjectField(TEXT("declaration"), Declaration);
		Statement->SetArrayField(TEXT("initializers"), {});
		Statement->SetObjectField(TEXT("condition"), Condition);
		Statement->SetArrayField(
			TEXT("incrementors"),
			{MakeShared<FJsonValueObject>(Incrementor)});
	}
	else if (TemplateId == TEXT("flow.return"))
	{
		if (!Next.IsEmpty() && Function.FindAction(Next) != nullptr &&
			Function.FindAction(Next)->GraphKind == TEXT("return"))
		{
			OutError = TEXT("The selected block already ends with a return node.");
			return false;
		}
		const FFinareAuthoringPort* ReturnType = nullptr;
		for (const FFinareAuthoringAction& Action : Function.Actions)
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

bool FFinareAuthoringSurfaceReader::BuildSwitchSectionInsertionPatch(
	const FFinareAuthoringSurfaceDocument& Document,
	const FFinareAuthoringFunction& Function,
	const FFinareAuthoringAction& SwitchStatement,
	const FString& LabelText,
	FString& OutPatchJson,
	FString& OutError)
{
	using namespace UE::Finare::AuthoringSurface::Private;
	using namespace UE::Finare::AuthoringSurface::Editing::Private;
	const FFinareAuthoringPort* GoverningType =
		SwitchStatement.FindPort(TEXT("value"), TEXT("input"));
	const FFinareAuthoringRegion* SwitchRegion = nullptr;
	TArray<FSwitchSectionContext> Sections;
	if (!IsSemanticId(Document.SourceCompilationId) ||
		!IsSemanticId(Document.SemanticConfigurationId) ||
		!IsSemanticId(SwitchStatement.Id) || GoverningType == nullptr ||
		!IsSemanticId(GoverningType->TypeId) ||
		!ResolveSwitchSections(
			Function,
			SwitchStatement,
			SwitchRegion,
			Sections,
			OutError))
	{
		if (OutError.IsEmpty())
		{
			OutError = TEXT("A switch section insertion requires an exact switch and governing type.");
		}
		return false;
	}

	TSharedPtr<FJsonObject> Label;
	if (!FSwitchPatternTextParser(Function, GoverningType->TypeId)
		.BuildLabel(LabelText, Label, OutError))
	{
		return false;
	}
	FString LabelKind;
	Label->TryGetStringField(TEXT("label"), LabelKind);
	const bool bDefault = LabelKind == TEXT("default");
	if (bDefault && Sections.ContainsByPredicate([](const FSwitchSectionContext& Section)
	{
		return Section.bHasDefault;
	}))
	{
		OutError = TEXT("This switch already owns a default section.");
		return false;
	}

	int32 InsertionIndex = Sections.Num();
	if (!bDefault)
	{
		const int32 DefaultIndex = Sections.IndexOfByPredicate(
			[](const FSwitchSectionContext& Section)
			{
				return Section.bHasDefault;
			});
		if (DefaultIndex != INDEX_NONE)
		{
			InsertionIndex = DefaultIndex;
		}
	}
	const FString Previous = InsertionIndex > 0
		                         ? Sections[InsertionIndex - 1].SectionId
		                         : FString();
	const FString Next = InsertionIndex < Sections.Num()
		                     ? Sections[InsertionIndex].SectionId
		                     : FString();

	const TSharedRef<FJsonObject> NewSection = MakeShared<FJsonObject>();
	NewSection->SetArrayField(
		TEXT("labels"),
		{MakeShared<FJsonValueObject>(Label)});
	const TSharedRef<FJsonObject> Operation = MakeShared<FJsonObject>();
	Operation->SetStringField(TEXT("operation"), TEXT("insertSwitchSection"));
	Operation->SetStringField(TEXT("targetNodeId"), SwitchStatement.Id);
	SetOptionalId(Operation, TEXT("expectedPreviousSectionId"), Previous);
	SetOptionalId(Operation, TEXT("expectedNextSectionId"), Next);
	Operation->SetObjectField(TEXT("newSection"), NewSection);
	return SerializeRoot(
		MakePatchRoot(Document, Operation),
		OutPatchJson,
		OutError,
		TEXT("the switch-section insertion patch"));
}

bool FFinareAuthoringSurfaceReader::BuildSwitchSectionReplacementPatch(
	const FFinareAuthoringSurfaceDocument& Document,
	const FFinareAuthoringFunction& Function,
	const FFinareAuthoringAction& CaseLabel,
	const FString& LabelText,
	FString& OutPatchJson,
	FString& OutError)
{
	using namespace UE::Finare::AuthoringSurface::Private;
	using namespace UE::Finare::AuthoringSurface::Editing::Private;
	const FFinareAuthoringAction* SwitchStatement = nullptr;
	TArray<FSwitchSectionContext> Sections;
	int32 SectionIndex = INDEX_NONE;
	if (!ResolveSwitchSectionForLabel(
		Function,
		CaseLabel,
		SwitchStatement,
		Sections,
		SectionIndex,
		OutError))
	{
		return false;
	}
	const FFinareAuthoringPort* GoverningType =
		SwitchStatement->FindPort(TEXT("value"), TEXT("input"));
	if (GoverningType == nullptr || !IsSemanticId(GoverningType->TypeId))
	{
		OutError = TEXT("The switch no longer exposes its exact governing type.");
		return false;
	}

	TSharedPtr<FJsonObject> Label;
	if (!FSwitchPatternTextParser(Function, GoverningType->TypeId)
		.BuildLabel(LabelText, Label, OutError))
	{
		return false;
	}
	FString LabelKind;
	Label->TryGetStringField(TEXT("label"), LabelKind);
	if (LabelKind == TEXT("default") && Sections.ContainsByPredicate(
		[SectionIndex, &Sections](const FSwitchSectionContext& Section)
		{
			return &Section != &Sections[SectionIndex] && Section.bHasDefault;
		}))
	{
		OutError = TEXT("Another section already owns the switch default label.");
		return false;
	}

	const FSwitchSectionContext& Section = Sections[SectionIndex];
	const TSharedRef<FJsonObject> Operation = MakeShared<FJsonObject>();
	Operation->SetStringField(TEXT("operation"), TEXT("replaceSwitchSection"));
	Operation->SetStringField(TEXT("targetNodeId"), Section.SectionId);
	Operation->SetStringField(TEXT("expectedSwitchStatementId"), SwitchStatement->Id);
	Operation->SetArrayField(TEXT("expectedLabelIds"), JsonIds(Section.Labels));
	Operation->SetArrayField(
		TEXT("newLabels"),
		{MakeShared<FJsonValueObject>(Label)});
	return SerializeRoot(
		MakePatchRoot(Document, Operation),
		OutPatchJson,
		OutError,
		TEXT("the switch-section replacement patch"));
}

bool FFinareAuthoringSurfaceReader::BuildSwitchSectionMovePatch(
	const FFinareAuthoringSurfaceDocument& Document,
	const FFinareAuthoringFunction& Function,
	const FFinareAuthoringAction& CaseLabel,
	int32 Direction,
	FString& OutPatchJson,
	FString& OutError)
{
	using namespace UE::Finare::AuthoringSurface::Editing::Private;
	const FFinareAuthoringAction* SwitchStatement = nullptr;
	TArray<FSwitchSectionContext> Sections;
	int32 SectionIndex = INDEX_NONE;
	if ((Direction != -1 && Direction != 1) ||
		!ResolveSwitchSectionForLabel(
			Function,
			CaseLabel,
			SwitchStatement,
			Sections,
			SectionIndex,
			OutError))
	{
		if (OutError.IsEmpty())
		{
			OutError = TEXT("A switch section can move only one slot up or down.");
		}
		return false;
	}
	const int32 DestinationIndex = SectionIndex + Direction;
	if (!Sections.IsValidIndex(DestinationIndex))
	{
		OutError = Direction < 0
			           ? TEXT("This switch section is already first.")
			           : TEXT("This switch section is already last.");
		return false;
	}

	const FSwitchSectionContext& Section = Sections[SectionIndex];
	const FString SourcePrevious = SectionIndex > 0
		                               ? Sections[SectionIndex - 1].SectionId
		                               : FString();
	const FString SourceNext = SectionIndex + 1 < Sections.Num()
		                           ? Sections[SectionIndex + 1].SectionId
		                           : FString();
	TArray<FString> Remaining;
	for (const FSwitchSectionContext& Candidate : Sections)
	{
		if (Candidate.SectionId != Section.SectionId)
		{
			Remaining.Add(Candidate.SectionId);
		}
	}
	const FString DestinationPrevious = DestinationIndex > 0
		                                    ? Remaining[DestinationIndex - 1]
		                                    : FString();
	const FString DestinationNext = DestinationIndex < Remaining.Num()
		                                ? Remaining[DestinationIndex]
		                                : FString();

	const TSharedRef<FJsonObject> Operation = MakeShared<FJsonObject>();
	Operation->SetStringField(TEXT("operation"), TEXT("moveSwitchSection"));
	Operation->SetStringField(TEXT("targetNodeId"), Section.SectionId);
	Operation->SetStringField(TEXT("expectedSwitchStatementId"), SwitchStatement->Id);
	SetOptionalId(
		Operation,
		TEXT("expectedSourcePreviousSectionId"),
		SourcePrevious);
	SetOptionalId(Operation, TEXT("expectedSourceNextSectionId"), SourceNext);
	SetOptionalId(
		Operation,
		TEXT("expectedDestinationPreviousSectionId"),
		DestinationPrevious);
	SetOptionalId(
		Operation,
		TEXT("expectedDestinationNextSectionId"),
		DestinationNext);
	return SerializeRoot(
		MakePatchRoot(Document, Operation),
		OutPatchJson,
		OutError,
		TEXT("the switch-section move patch"));
}

bool FFinareAuthoringSurfaceReader::BuildSwitchSectionDeletionPatch(
	const FFinareAuthoringSurfaceDocument& Document,
	const FFinareAuthoringFunction& Function,
	const FFinareAuthoringAction& CaseLabel,
	FString& OutPatchJson,
	FString& OutError)
{
	using namespace UE::Finare::AuthoringSurface::Editing::Private;
	const FFinareAuthoringAction* SwitchStatement = nullptr;
	TArray<FSwitchSectionContext> Sections;
	int32 SectionIndex = INDEX_NONE;
	if (!ResolveSwitchSectionForLabel(
		Function,
		CaseLabel,
		SwitchStatement,
		Sections,
		SectionIndex,
		OutError))
	{
		return false;
	}
	if (Sections.Num() <= 1)
	{
		OutError = TEXT("A switch must retain at least one section.");
		return false;
	}

	const FSwitchSectionContext& Section = Sections[SectionIndex];
	const FString Previous = SectionIndex > 0
		                         ? Sections[SectionIndex - 1].SectionId
		                         : FString();
	const FString Next = SectionIndex + 1 < Sections.Num()
		                     ? Sections[SectionIndex + 1].SectionId
		                     : FString();
	const TSharedRef<FJsonObject> Operation = MakeShared<FJsonObject>();
	Operation->SetStringField(TEXT("operation"), TEXT("deleteSwitchSection"));
	Operation->SetStringField(TEXT("targetNodeId"), Section.SectionId);
	Operation->SetStringField(TEXT("expectedSwitchStatementId"), SwitchStatement->Id);
	SetOptionalId(Operation, TEXT("expectedPreviousSectionId"), Previous);
	SetOptionalId(Operation, TEXT("expectedNextSectionId"), Next);
	Operation->SetArrayField(TEXT("expectedLabelIds"), JsonIds(Section.Labels));
	return SerializeRoot(
		MakePatchRoot(Document, Operation),
		OutPatchJson,
		OutError,
		TEXT("the switch-section deletion patch"));
}

bool FFinareAuthoringSurfaceReader::BuildMemberInvocationInsertionPatch(
	const FFinareAuthoringSurfaceDocument& Document,
	const FFinareAuthoringFunction& Function,
	const FFinareAuthoringRegion& BlockRegion,
	const FFinareAuthoringParameter& Receiver,
	const FString& MemberName,
	const FString& ResultTypeName,
	const FString& ResultTypeId,
	const TArray<FFinareAuthoringParameter>& Arguments,
	FString& OutPatchJson,
	FString& OutError)
{
	using namespace UE::Finare::AuthoringSurface::Private;
	using namespace UE::Finare::AuthoringSurface::Editing::Private;
	FString Previous;
	FString Next;
	if (!GetInsertionGap(Function, BlockRegion, Previous, Next) ||
		!IsSemanticId(Receiver.Id) || !IsSemanticId(Receiver.TypeId) ||
		!IsSemanticId(ResultTypeId) || MemberName.IsEmpty() || ResultTypeName.IsEmpty() ||
		Arguments.ContainsByPredicate([](const FFinareAuthoringParameter& Argument)
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
	for (const FFinareAuthoringParameter& Argument : Arguments)
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
	return SerializeRoot(MakePatchRoot(Document, Operation), OutPatchJson, OutError,
	                     TEXT("the reflected call insertion patch"));
}

bool FFinareAuthoringSurfaceReader::BuildMemberAccessInsertionPatch(
	const FFinareAuthoringSurfaceDocument& Document,
	const FFinareAuthoringFunction& Function,
	const FFinareAuthoringRegion& BlockRegion,
	const FFinareAuthoringParameter& Receiver,
	const FString& MemberName,
	const FString& ResultTypeName,
	const FString& ResultTypeId,
	FString& OutPatchJson,
	FString& OutError)
{
	using namespace UE::Finare::AuthoringSurface::Private;
	using namespace UE::Finare::AuthoringSurface::Editing::Private;
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
	return SerializeRoot(MakePatchRoot(Document, Operation), OutPatchJson, OutError,
	                     TEXT("the reflected property insertion patch"));
}

bool FFinareAuthoringSurfaceReader::BuildScriptAssetCreationPatch(
	const FFinareAuthoringSurfaceDocument& Document,
	const FString& TargetSourcePath,
	const FString& Namespace,
	const FString& TypeName,
	const FString& AssetPath,
	bool bIncludeActorLifecycle,
	FString& OutPatchJson,
	FString& OutError)
{
	using namespace UE::Finare::AuthoringSurface::Private;
	using namespace UE::Finare::AuthoringSurface::Editing::Private;
	if (!IsSemanticId(Document.SourceCompilationId) ||
		!IsSemanticId(Document.SemanticConfigurationId) || TargetSourcePath.IsEmpty() ||
		TypeName.IsEmpty() || AssetPath.IsEmpty())
	{
		OutError = TEXT("A new Script Asset requires a workspace source, type name, and asset path.");
		return false;
	}
	const TSharedRef<FJsonObject> Asset = MakeShared<FJsonObject>();
	Asset->SetStringField(TEXT("namespace"), Namespace);
	Asset->SetStringField(TEXT("name"), TypeName);
	Asset->SetStringField(TEXT("assetPath"), AssetPath);
	if (bIncludeActorLifecycle)
	{
		Asset->SetStringField(
			TEXT("baseTypeName"),
			TEXT("global::Finare.Unreal.UnrealActorScript"));
	}
	else
	{
		Asset->SetField(TEXT("baseTypeName"), MakeShared<FJsonValueNull>());
	}
	Asset->SetBoolField(TEXT("includeActorLifecycle"), bIncludeActorLifecycle);
	const TSharedRef<FJsonObject> Operation = MakeShared<FJsonObject>();
	Operation->SetStringField(TEXT("operation"), TEXT("addAsset"));
	Operation->SetStringField(TEXT("targetSourcePath"), TargetSourcePath);
	Operation->SetObjectField(TEXT("asset"), Asset);
	return SerializeRoot(
		MakeDeclarationPatchRoot(Document, Operation),
		OutPatchJson,
		OutError,
		TEXT("the Script Asset creation patch"));
}

bool FFinareAuthoringSurfaceReader::BuildScriptAssetPathUpdatePatch(
	const FFinareAuthoringSurfaceDocument& Document,
	const FFinareAuthoringAsset& Asset,
	const FString& AssetPath,
	FString& OutPatchJson,
	FString& OutError)
{
	using namespace UE::Finare::AuthoringSurface::Private;
	using namespace UE::Finare::AuthoringSurface::Editing::Private;
	if (!IsSemanticId(Asset.Id) || Asset.QualifiedName.IsEmpty() ||
		Asset.AssetPath.IsEmpty() || AssetPath.IsEmpty() || AssetPath == Asset.AssetPath)
	{
		OutError = TEXT("Changing a Script Asset requires a distinct non-empty asset path.");
		return false;
	}
	const TSharedRef<FJsonObject> Expected = MakeShared<FJsonObject>();
	Expected->SetStringField(TEXT("qualifiedName"), Asset.QualifiedName);
	Expected->SetStringField(TEXT("assetPath"), Asset.AssetPath);
	const TSharedRef<FJsonObject> Operation = MakeShared<FJsonObject>();
	Operation->SetStringField(TEXT("operation"), TEXT("updateAsset"));
	Operation->SetStringField(TEXT("targetAssetId"), Asset.Id);
	Operation->SetObjectField(TEXT("expected"), Expected);
	Operation->SetStringField(TEXT("replacementAssetPath"), AssetPath);
	return SerializeRoot(
		MakeDeclarationPatchRoot(Document, Operation),
		OutPatchJson,
		OutError,
		TEXT("the Script Asset path patch"));
}

bool FFinareAuthoringSurfaceReader::BuildScriptClassBasePatch(
	const FFinareAuthoringSurfaceDocument& Document,
	const FFinareAuthoringAsset& Asset,
	const FFinareClassBindingDeclaration& ExpectedClassBinding,
	const FString& ReplacementBaseClassPath,
	FString& OutPatchJson,
	FString& OutError)
{
	using namespace UE::Finare::AuthoringSurface::Private;
	using namespace UE::Finare::AuthoringSurface::Editing::Private;
	FString BindingError;
	if (!ExpectedClassBinding.Validate(BindingError) ||
		(ExpectedClassBinding.Kind != TEXT("Actor") &&
			ExpectedClassBinding.Kind != TEXT("ActorComponent")))
	{
		OutError = BindingError.IsEmpty()
			           ? TEXT("Only Actor and ActorComponent Script Classes may select a native base class.")
			           : BindingError;
		return false;
	}
	if (!IsSemanticId(Asset.Id) || Asset.QualifiedName.IsEmpty() || Asset.AssetPath.IsEmpty())
	{
		OutError = TEXT("Changing a Script Class base requires an exact current Script Asset.");
		return false;
	}
	FFinareClassBindingDeclaration ReplacementClassBinding = ExpectedClassBinding;
	if (ReplacementBaseClassPath.IsEmpty())
	{
		ReplacementClassBinding.BaseClassPath.Reset();
	}
	else
	{
		ReplacementClassBinding.BaseClassPath = ReplacementBaseClassPath;
	}
	if (!ReplacementClassBinding.Validate(BindingError))
	{
		OutError = BindingError;
		return false;
	}
	const bool bExpectedHasBase = ExpectedClassBinding.BaseClassPath.IsSet();
	if ((!bExpectedHasBase && ReplacementBaseClassPath.IsEmpty()) ||
		(bExpectedHasBase && ExpectedClassBinding.BaseClassPath.GetValue() == ReplacementBaseClassPath))
	{
		OutError = TEXT("The Script Class base patch must change the explicit base-class value.");
		return false;
	}

	const TSharedRef<FJsonObject> ExpectedAsset = MakeShared<FJsonObject>();
	ExpectedAsset->SetStringField(TEXT("qualifiedName"), Asset.QualifiedName);
	ExpectedAsset->SetStringField(TEXT("assetPath"), Asset.AssetPath);
	const TSharedRef<FJsonObject> ExpectedBinding = MakeShared<FJsonObject>();
	ExpectedBinding->SetStringField(TEXT("kind"), ExpectedClassBinding.Kind);
	if (ExpectedClassBinding.BaseClassPath.IsSet())
	{
		ExpectedBinding->SetStringField(TEXT("baseClassPath"), ExpectedClassBinding.BaseClassPath.GetValue());
	}
	else
	{
		ExpectedBinding->SetField(TEXT("baseClassPath"), MakeShared<FJsonValueNull>());
	}
	if (ExpectedClassBinding.GeneratedClassPath.IsSet())
	{
		ExpectedBinding->SetStringField(TEXT("generatedClassPath"), ExpectedClassBinding.GeneratedClassPath.GetValue());
	}
	else
	{
		ExpectedBinding->SetField(TEXT("generatedClassPath"), MakeShared<FJsonValueNull>());
	}

	const TSharedRef<FJsonObject> Operation = MakeShared<FJsonObject>();
	Operation->SetStringField(TEXT("operation"), TEXT("updateClassBase"));
	Operation->SetStringField(TEXT("targetAssetId"), Asset.Id);
	Operation->SetObjectField(TEXT("expected"), ExpectedAsset);
	Operation->SetObjectField(TEXT("expectedClassBinding"), ExpectedBinding);
	if (ReplacementBaseClassPath.IsEmpty())
	{
		Operation->SetField(TEXT("replacementBaseClassPath"), MakeShared<FJsonValueNull>());
	}
	else
	{
		Operation->SetStringField(TEXT("replacementBaseClassPath"), ReplacementBaseClassPath);
	}
	return SerializeRoot(
		MakeClassBindingDeclarationPatchRoot(Document, Operation),
		OutPatchJson,
		OutError,
		TEXT("the Script Class base patch"));
}

bool FFinareAuthoringSurfaceReader::BuildScriptAssetRemovalPatch(
	const FFinareAuthoringSurfaceDocument& Document,
	const FFinareAuthoringAsset& Asset,
	FString& OutPatchJson,
	FString& OutError)
{
	using namespace UE::Finare::AuthoringSurface::Private;
	using namespace UE::Finare::AuthoringSurface::Editing::Private;
	if (!IsSemanticId(Asset.Id) || Asset.QualifiedName.IsEmpty() || Asset.AssetPath.IsEmpty())
	{
		OutError = TEXT("Removing a Script Asset requires an exact current asset contract.");
		return false;
	}
	const TSharedRef<FJsonObject> Expected = MakeShared<FJsonObject>();
	Expected->SetStringField(TEXT("qualifiedName"), Asset.QualifiedName);
	Expected->SetStringField(TEXT("assetPath"), Asset.AssetPath);
	const TSharedRef<FJsonObject> Operation = MakeShared<FJsonObject>();
	Operation->SetStringField(TEXT("operation"), TEXT("removeAsset"));
	Operation->SetStringField(TEXT("targetAssetId"), Asset.Id);
	Operation->SetObjectField(TEXT("expected"), Expected);
	return SerializeRoot(
		MakeDeclarationPatchRoot(Document, Operation),
		OutPatchJson,
		OutError,
		TEXT("the Script Asset removal patch"));
}

bool FFinareAuthoringSurfaceReader::BuildStateAdditionPatch(
	const FFinareAuthoringSurfaceDocument& Document,
	const FFinareAuthoringAsset& Asset,
	const FString& Name,
	const FString& TypeName,
	const FString& TypeKind,
	const FString& DefaultValueText,
	const FFinareAuthoringExposure& Exposure,
	FString& OutPatchJson,
	FString& OutError)
{
	using namespace UE::Finare::AuthoringSurface::Private;
	using namespace UE::Finare::AuthoringSurface::Editing::Private;
	TSharedPtr<FJsonObject> State;
	if (!IsSemanticId(Asset.Id) ||
		!MakeStateSpec(Name, TypeName, TypeKind, DefaultValueText, Exposure, State, OutError))
	{
		if (OutError.IsEmpty())
		{
			OutError = TEXT("Adding state requires a current Script Asset.");
		}
		return false;
	}
	const TSharedRef<FJsonObject> Operation = MakeShared<FJsonObject>();
	Operation->SetStringField(TEXT("operation"), TEXT("addState"));
	Operation->SetStringField(TEXT("targetAssetId"), Asset.Id);
	Operation->SetObjectField(TEXT("state"), State.ToSharedRef());
	return SerializeRoot(
		MakeDeclarationPatchRoot(Document, Operation),
		OutPatchJson,
		OutError,
		TEXT("the state creation patch"));
}

bool FFinareAuthoringSurfaceReader::BuildStateUpdatePatch(
	const FFinareAuthoringSurfaceDocument& Document,
	const FFinareAuthoringState& State,
	const FString& Name,
	const FString& TypeName,
	const FString& TypeKind,
	const FString& DefaultValueText,
	const FFinareAuthoringExposure& Exposure,
	FString& OutPatchJson,
	FString& OutError)
{
	using namespace UE::Finare::AuthoringSurface::Private;
	using namespace UE::Finare::AuthoringSurface::Editing::Private;
	const FFinareAuthoringAsset* Owner = Document.FindDeclarationOwnerAsset(State.Id);
	const FFinareAuthoringState* ExpectedState = Owner != nullptr
		                                             ? Owner->States.FindByPredicate(
			                                             [&State](const FFinareAuthoringState& Candidate)
			                                             {
				                                             return Candidate.Id == State.Id;
			                                             })
		                                             : nullptr;
	if (ExpectedState == nullptr)
	{
		OutError = TEXT("The selected state has no unambiguous source owner.");
		return false;
	}
	TSharedPtr<FJsonObject> Expected;
	TSharedPtr<FJsonObject> Replacement;
	if (!IsSemanticId(State.Id) || !MakeStateExpected(*ExpectedState, Expected, OutError) ||
		!MakeStateSpec(
			Name,
			TypeName,
			TypeKind,
			DefaultValueText,
			Exposure,
			Replacement,
			OutError))
	{
		if (OutError.IsEmpty())
		{
			OutError = TEXT("Updating state requires an exact current state.");
		}
		return false;
	}
	const TSharedRef<FJsonObject> Operation = MakeShared<FJsonObject>();
	Operation->SetStringField(TEXT("operation"), TEXT("updateState"));
	Operation->SetStringField(TEXT("targetStateId"), State.Id);
	Operation->SetObjectField(TEXT("expected"), Expected.ToSharedRef());
	Operation->SetObjectField(TEXT("replacement"), Replacement.ToSharedRef());
	return SerializeRoot(
		MakeDeclarationPatchRoot(Document, Operation),
		OutPatchJson,
		OutError,
		TEXT("the state update patch"));
}

bool FFinareAuthoringSurfaceReader::BuildStateRemovalPatch(
	const FFinareAuthoringSurfaceDocument& Document,
	const FFinareAuthoringState& State,
	FString& OutPatchJson,
	FString& OutError)
{
	using namespace UE::Finare::AuthoringSurface::Private;
	using namespace UE::Finare::AuthoringSurface::Editing::Private;
	const FFinareAuthoringAsset* Owner = Document.FindDeclarationOwnerAsset(State.Id);
	const FFinareAuthoringState* ExpectedState = Owner != nullptr
		                                             ? Owner->States.FindByPredicate(
			                                             [&State](const FFinareAuthoringState& Candidate)
			                                             {
				                                             return Candidate.Id == State.Id;
			                                             })
		                                             : nullptr;
	if (ExpectedState == nullptr)
	{
		OutError = TEXT("The selected state has no unambiguous source owner.");
		return false;
	}
	TSharedPtr<FJsonObject> Expected;
	if (!IsSemanticId(State.Id) || !MakeStateExpected(*ExpectedState, Expected, OutError))
	{
		if (OutError.IsEmpty())
		{
			OutError = TEXT("Removing state requires an exact current state.");
		}
		return false;
	}
	const TSharedRef<FJsonObject> Operation = MakeShared<FJsonObject>();
	Operation->SetStringField(TEXT("operation"), TEXT("removeState"));
	Operation->SetStringField(TEXT("targetStateId"), State.Id);
	Operation->SetObjectField(TEXT("expected"), Expected.ToSharedRef());
	return SerializeRoot(
		MakeDeclarationPatchRoot(Document, Operation),
		OutPatchJson,
		OutError,
		TEXT("the state removal patch"));
}

bool FFinareAuthoringSurfaceReader::BuildFunctionAdditionPatch(
	const FFinareAuthoringSurfaceDocument& Document,
	const FFinareAuthoringAsset& Asset,
	const FString& Name,
	const FString& ReturnTypeName,
	bool bStatic,
	const TArray<FFinareAuthoringParameter>& Parameters,
	FString& OutPatchJson,
	FString& OutError)
{
	using namespace UE::Finare::AuthoringSurface::Private;
	using namespace UE::Finare::AuthoringSurface::Editing::Private;
	if (!IsSemanticId(Asset.Id) || Name.IsEmpty() || ReturnTypeName.IsEmpty() ||
		Parameters.ContainsByPredicate([](const FFinareAuthoringParameter& Parameter)
		{
			return Parameter.Name.IsEmpty() || Parameter.TypeMetadataName.IsEmpty();
		}))
	{
		OutError = TEXT("Adding a function requires an asset, name, scalar return, and typed parameters.");
		return false;
	}
	const TSharedRef<FJsonObject> Operation = MakeShared<FJsonObject>();
	Operation->SetStringField(TEXT("operation"), TEXT("addFunction"));
	Operation->SetStringField(TEXT("targetAssetId"), Asset.Id);
	Operation->SetObjectField(
		TEXT("function"),
		MakeFunctionSpec(Name, ReturnTypeName, bStatic, Parameters));
	return SerializeRoot(
		MakeDeclarationPatchRoot(Document, Operation),
		OutPatchJson,
		OutError,
		TEXT("the function creation patch"));
}

bool FFinareAuthoringSurfaceReader::BuildFunctionUpdatePatch(
	const FFinareAuthoringSurfaceDocument& Document,
	const FFinareAuthoringFunction& Function,
	const FString& Name,
	const FString& ReturnTypeName,
	bool bStatic,
	const TArray<FFinareAuthoringParameter>& Parameters,
	FString& OutPatchJson,
	FString& OutError)
{
	using namespace UE::Finare::AuthoringSurface::Private;
	using namespace UE::Finare::AuthoringSurface::Editing::Private;
	const FFinareAuthoringAsset* Owner = Document.FindDeclarationOwnerAsset(Function.Id);
	const FFinareAuthoringFunction* ExpectedFunction = Owner != nullptr
		                                                   ? Owner->Functions.FindByPredicate(
			                                                   [&Function](const FFinareAuthoringFunction& Candidate)
			                                                   {
				                                                   return Candidate.Id == Function.Id;
			                                                   })
		                                                   : nullptr;
	if (ExpectedFunction == nullptr)
	{
		OutError = TEXT("The selected function has no unambiguous source owner.");
		return false;
	}
	if (!IsSemanticId(Function.Id) || Name.IsEmpty() || ReturnTypeName.IsEmpty() ||
		Parameters.Num() != Function.Parameters.Num())
	{
		OutError = TEXT("Updating a function requires an exact function and the same parameter count.");
		return false;
	}
	const TSharedRef<FJsonObject> Operation = MakeShared<FJsonObject>();
	Operation->SetStringField(TEXT("operation"), TEXT("updateFunction"));
	Operation->SetStringField(TEXT("targetFunctionId"), Function.Id);
	Operation->SetObjectField(TEXT("expected"), MakeFunctionExpected(*ExpectedFunction));
	Operation->SetObjectField(
		TEXT("replacement"),
		MakeFunctionSpec(Name, ReturnTypeName, bStatic, Parameters));
	return SerializeRoot(
		MakeDeclarationPatchRoot(Document, Operation),
		OutPatchJson,
		OutError,
		TEXT("the function update patch"));
}

bool FFinareAuthoringSurfaceReader::BuildFunctionRemovalPatch(
	const FFinareAuthoringSurfaceDocument& Document,
	const FFinareAuthoringFunction& Function,
	FString& OutPatchJson,
	FString& OutError)
{
	using namespace UE::Finare::AuthoringSurface::Private;
	using namespace UE::Finare::AuthoringSurface::Editing::Private;
	const FFinareAuthoringAsset* Owner = Document.FindDeclarationOwnerAsset(Function.Id);
	const FFinareAuthoringFunction* ExpectedFunction = Owner != nullptr
		                                                   ? Owner->Functions.FindByPredicate(
			                                                   [&Function](const FFinareAuthoringFunction& Candidate)
			                                                   {
				                                                   return Candidate.Id == Function.Id;
			                                                   })
		                                                   : nullptr;
	if (ExpectedFunction == nullptr)
	{
		OutError = TEXT("The selected function has no unambiguous source owner.");
		return false;
	}
	if (!IsSemanticId(Function.Id))
	{
		OutError = TEXT("Removing a function requires an exact current function.");
		return false;
	}
	const TSharedRef<FJsonObject> Operation = MakeShared<FJsonObject>();
	Operation->SetStringField(TEXT("operation"), TEXT("removeFunction"));
	Operation->SetStringField(TEXT("targetFunctionId"), Function.Id);
	Operation->SetObjectField(TEXT("expected"), MakeFunctionExpected(*ExpectedFunction));
	return SerializeRoot(
		MakeDeclarationPatchRoot(Document, Operation),
		OutPatchJson,
		OutError,
		TEXT("the function removal patch"));
}

bool FFinareAuthoringSurfaceReader::BuildLifecycleHookAdditionPatch(
	const FFinareAuthoringSurfaceDocument& Document,
	const FFinareAuthoringAsset& Asset,
	const FString& Hook,
	FString& OutPatchJson,
	FString& OutError)
{
	using namespace UE::Finare::AuthoringSurface::Private;
	using namespace UE::Finare::AuthoringSurface::Editing::Private;
	if (!IsSemanticId(Asset.Id) ||
		(Hook != TEXT("beginPlay") && Hook != TEXT("tick") && Hook != TEXT("endPlay")))
	{
		OutError = TEXT("A lifecycle declaration requires beginPlay, tick, or endPlay.");
		return false;
	}
	const TSharedRef<FJsonObject> Operation = MakeShared<FJsonObject>();
	Operation->SetStringField(TEXT("operation"), TEXT("addLifecycleHook"));
	Operation->SetStringField(TEXT("targetAssetId"), Asset.Id);
	Operation->SetStringField(TEXT("hook"), Hook);
	return SerializeRoot(
		MakeDeclarationPatchRoot(Document, Operation),
		OutPatchJson,
		OutError,
		TEXT("the lifecycle hook patch"));
}

bool FFinareAuthoringSurfaceReader::BuildUnrealEventHandlerInsertionPatch(
	const FFinareAuthoringSurfaceDocument& Document,
	const FFinareAuthoringAsset& Asset,
	const FString& HandlerName,
	const FString& SourceTypeName,
	const FString& EventPath,
	const TArray<FFinareAuthoringParameter>& Parameters,
	FString& OutPatchJson,
	FString& OutError)
{
	using namespace UE::Finare::AuthoringSurface::Private;
	using namespace UE::Finare::AuthoringSurface::Editing::Private;
	if (!IsSemanticId(Document.SourceCompilationId) ||
		!IsSemanticId(Document.SemanticConfigurationId) || !IsSemanticId(Asset.Id) ||
		HandlerName.IsEmpty() || SourceTypeName.IsEmpty() ||
		!EventPath.StartsWith(TEXT("/Script/")) || !EventPath.Contains(TEXT(":")) ||
		Parameters.ContainsByPredicate([](const FFinareAuthoringParameter& Parameter)
		{
			return Parameter.Name.IsEmpty() || Parameter.TypeMetadataName.IsEmpty();
		}))
	{
		OutError = TEXT(
			"The UE Event Entry requires an asset, handler, source wrapper, event path, and typed payload.");
		return false;
	}

	const TSharedRef<FJsonObject> Handler = MakeShared<FJsonObject>();
	Handler->SetStringField(TEXT("name"), HandlerName);
	Handler->SetStringField(TEXT("sourceTypeName"), SourceTypeName);
	Handler->SetStringField(TEXT("eventPath"), EventPath);
	Handler->SetStringField(TEXT("source"), TEXT("target"));
	Handler->SetField(TEXT("sourceMember"), MakeShared<FJsonValueNull>());
	TArray<TSharedPtr<FJsonValue>> JsonParameters;
	for (const FFinareAuthoringParameter& Parameter : Parameters)
	{
		const TSharedRef<FJsonObject> JsonParameter = MakeShared<FJsonObject>();
		JsonParameter->SetStringField(TEXT("name"), Parameter.Name);
		JsonParameter->SetStringField(TEXT("typeName"), Parameter.TypeMetadataName);
		JsonParameters.Add(MakeShared<FJsonValueObject>(JsonParameter));
	}
	Handler->SetArrayField(TEXT("parameters"), MoveTemp(JsonParameters));

	const TSharedRef<FJsonObject> Operation = MakeShared<FJsonObject>();
	Operation->SetStringField(TEXT("operation"), TEXT("addUnrealEventHandler"));
	Operation->SetStringField(TEXT("targetAssetId"), Asset.Id);
	Operation->SetObjectField(TEXT("handler"), Handler);

	return SerializeRoot(
		MakeDeclarationPatchRoot(Document, Operation),
		OutPatchJson,
		OutError,
		TEXT("the Unreal Event Entry declaration patch"));
}

bool FFinareAuthoringSurfaceReader::BuildStatementDeletionPatch(
	const FFinareAuthoringSurfaceDocument& Document,
	const FFinareAuthoringFunction& Function,
	const FFinareAuthoringAction& Statement,
	FString& OutPatchJson,
	FString& OutError)
{
	using namespace UE::Finare::AuthoringSurface::Editing::Private;
	const FFinareAuthoringRegion* Block = FindOwningBlock(Function, Statement.Id);
	if (Block == nullptr || Statement.GraphKind == TEXT("entry"))
	{
		OutError = TEXT("Only a statement owned by an explicit structured block can be deleted.");
		return false;
	}

	FString Previous;
	FString Next;
	GetSiblingPreconditions(Function, *Block, Statement.Id, Previous, Next);
	const TSharedRef<FJsonObject> Operation = MakeShared<FJsonObject>();
	Operation->SetStringField(TEXT("operation"), TEXT("deleteStatement"));
	Operation->SetStringField(TEXT("targetNodeId"), Statement.Id);
	Operation->SetStringField(TEXT("expectedParentBlockId"), Block->SourceNodeId);
	SetOptionalId(Operation, TEXT("expectedPreviousSiblingId"), Previous);
	SetOptionalId(Operation, TEXT("expectedNextSiblingId"), Next);
	return SerializeRoot(MakePatchRoot(Document, Operation), OutPatchJson, OutError,
	                     TEXT("the statement deletion patch"));
}

bool FFinareAuthoringSurfaceReader::BuildStatementDuplicationPatch(
	const FFinareAuthoringSurfaceDocument& Document,
	const FFinareAuthoringFunction& Function,
	const FFinareAuthoringAction& SourceStatement,
	const FFinareAuthoringAction& DestinationStatement,
	const FString& NewLocalName,
	FString& OutPatchJson,
	FString& OutError)
{
	using namespace UE::Finare::AuthoringSurface::Editing::Private;
	const FFinareAuthoringRegion* SourceBlock =
		FindOwningBlock(Function, SourceStatement.Id);
	const FFinareAuthoringRegion* DestinationBlock =
		FindOwningBlock(Function, DestinationStatement.Id);
	if (SourceBlock == nullptr || DestinationBlock == nullptr ||
		SourceBlock->SourceNodeId != DestinationBlock->SourceNodeId)
	{
		OutError = TEXT("Copy/paste currently requires source and destination statements in the same explicit block.");
		return false;
	}

	const auto IsTerminalOrSynthetic = [](const FFinareAuthoringAction& Action)
	{
		return Action.GraphKind == TEXT("entry") ||
			Action.GraphKind == TEXT("unrealEventEntry") ||
			Action.GraphKind == TEXT("switchCase") ||
			Action.GraphKind == TEXT("return") ||
			Action.GraphKind == TEXT("break") ||
			Action.GraphKind == TEXT("continue");
	};
	if (IsTerminalOrSynthetic(SourceStatement) || IsTerminalOrSynthetic(DestinationStatement))
	{
		OutError = TEXT(
			"Return, break, continue, entry, and switch-label nodes cannot be copied or used as an after-node destination.");
		return false;
	}

	const bool bCopiesLocal = SourceStatement.GraphKind == TEXT("variable");
	if ((bCopiesLocal && NewLocalName.IsEmpty()) || (!bCopiesLocal && !NewLocalName.IsEmpty()))
	{
		OutError = bCopiesLocal
			           ? TEXT("Copying a local declaration requires a unique destination name.")
			           : TEXT("A copied local name is valid only for a variable declaration.");
		return false;
	}

	const FString SourceStatementId = ResolveStatementId(SourceStatement);
	const FString DestinationStatementId = ResolveStatementId(DestinationStatement);
	const TArray<FString> StatementIds = GetStructuralStatementIds(Function, *SourceBlock);
	const int32 SourceIndex = StatementIds.IndexOfByKey(SourceStatementId);
	const int32 DestinationIndex = StatementIds.IndexOfByKey(DestinationStatementId);
	if (SourceIndex == INDEX_NONE || DestinationIndex == INDEX_NONE)
	{
		OutError = TEXT("The compiler-owned statement order no longer contains the copy source or destination.");
		return false;
	}

	FString SourcePrevious;
	FString SourceNext;
	GetStructuralSiblingPreconditions(
		StatementIds,
		SourceStatementId,
		SourcePrevious,
		SourceNext);
	const FString DestinationPrevious = DestinationStatementId;
	const FString DestinationNext = DestinationIndex + 1 < StatementIds.Num()
		                                ? StatementIds[DestinationIndex + 1]
		                                : FString();

	const TSharedRef<FJsonObject> Operation = MakeShared<FJsonObject>();
	Operation->SetStringField(TEXT("operation"), TEXT("duplicateStatement"));
	Operation->SetStringField(TEXT("targetNodeId"), SourceStatementId);
	Operation->SetStringField(TEXT("expectedParentBlockId"), SourceBlock->SourceNodeId);
	SetOptionalId(Operation, TEXT("expectedSourcePreviousSiblingId"), SourcePrevious);
	SetOptionalId(Operation, TEXT("expectedSourceNextSiblingId"), SourceNext);
	SetOptionalId(Operation, TEXT("expectedDestinationPreviousSiblingId"), DestinationPrevious);
	SetOptionalId(Operation, TEXT("expectedDestinationNextSiblingId"), DestinationNext);
	if (NewLocalName.IsEmpty())
	{
		Operation->SetField(TEXT("newLocalName"), MakeShared<FJsonValueNull>());
	}
	else
	{
		Operation->SetStringField(TEXT("newLocalName"), NewLocalName);
	}
	return SerializeRoot(
		MakePatchRoot(Document, Operation),
		OutPatchJson,
		OutError,
		TEXT("the statement duplication patch"));
}

bool FFinareAuthoringSurfaceReader::BuildStatementRangeDuplicationPatch(
	const FFinareAuthoringSurfaceDocument& Document,
	const FFinareAuthoringFunction& Function,
	const TArray<FString>& SourceStatementActionIds,
	const FFinareAuthoringAction& DestinationStatement,
	const TMap<FString, FString>& NewLocalNamesByActionId,
	FString& OutPatchJson,
	FString& OutError)
{
	using namespace UE::Finare::AuthoringSurface::Editing::Private;
	if (SourceStatementActionIds.IsEmpty())
	{
		OutError = TEXT("Copy requires at least one selected statement node.");
		return false;
	}

	const auto IsTerminalOrSynthetic = [](const FFinareAuthoringAction& Action)
	{
		return Action.GraphKind == TEXT("entry") ||
			Action.GraphKind == TEXT("unrealEventEntry") ||
			Action.GraphKind == TEXT("switchCase") ||
			Action.GraphKind == TEXT("return") ||
			Action.GraphKind == TEXT("break") ||
			Action.GraphKind == TEXT("continue");
	};
	if (IsTerminalOrSynthetic(DestinationStatement))
	{
		OutError = TEXT("The paste after-node must be one ordinary non-terminal statement.");
		return false;
	}

	const FFinareAuthoringRegion* SourceBlock = nullptr;
	TArray<const FFinareAuthoringAction*> SourceActions;
	TArray<FString> SourceStatementIds;
	for (const FString& ActionId : SourceStatementActionIds)
	{
		const FFinareAuthoringAction* Action = Function.FindAction(ActionId);
		const FFinareAuthoringRegion* Block = Action != nullptr
			                                      ? FindOwningBlock(Function, Action->Id)
			                                      : nullptr;
		if (Action == nullptr || Block == nullptr || IsTerminalOrSynthetic(*Action) ||
			(SourceBlock != nullptr && SourceBlock->SourceNodeId != Block->SourceNodeId))
		{
			OutError = TEXT("Copy requires ordinary statement nodes from one explicit source block.");
			return false;
		}
		SourceBlock = Block;
		SourceActions.Add(Action);
		SourceStatementIds.AddUnique(ResolveStatementId(*Action));
	}
	if (SourceBlock == nullptr || SourceStatementIds.Num() != SourceStatementActionIds.Num())
	{
		OutError = TEXT("A statement copy range cannot contain the same semantic statement twice.");
		return false;
	}

	const TArray<FString> SourceStructuralIds = GetStructuralStatementIds(Function, *SourceBlock);
	SourceActions.Sort([&SourceStructuralIds](
		const FFinareAuthoringAction& Left,
		const FFinareAuthoringAction& Right)
		{
			return SourceStructuralIds.IndexOfByKey(ResolveStatementId(Left)) <
				SourceStructuralIds.IndexOfByKey(ResolveStatementId(Right));
		});
	SourceStatementIds.Reset(SourceActions.Num());
	for (const FFinareAuthoringAction* Action : SourceActions)
	{
		SourceStatementIds.Add(ResolveStatementId(*Action));
	}
	const int32 FirstIndex = SourceStructuralIds.IndexOfByKey(SourceStatementIds[0]);
	if (FirstIndex == INDEX_NONE ||
		FirstIndex + SourceStatementIds.Num() > SourceStructuralIds.Num())
	{
		OutError = TEXT("The copied statements no longer exist in compiler-owned source order.");
		return false;
	}
	for (int32 Offset = 0; Offset < SourceStatementIds.Num(); ++Offset)
	{
		if (SourceStructuralIds[FirstIndex + Offset] != SourceStatementIds[Offset])
		{
			OutError = TEXT("Copy requires one consecutive range without hidden regions between selections.");
			return false;
		}
	}

	const FFinareAuthoringRegion* DestinationBlock =
		FindOwningBlock(Function, DestinationStatement.Id);
	if (DestinationBlock == nullptr)
	{
		OutError = TEXT("The paste destination is not owned by an explicit structured block.");
		return false;
	}
	const FString DestinationStatementId = ResolveStatementId(DestinationStatement);
	const TArray<FString> DestinationStructuralIds =
		GetStructuralStatementIds(Function, *DestinationBlock);
	const int32 DestinationIndex =
		DestinationStructuralIds.IndexOfByKey(DestinationStatementId);
	if (DestinationIndex == INDEX_NONE)
	{
		OutError = TEXT("The paste destination no longer exists in compiler-owned block order.");
		return false;
	}

	TArray<TSharedPtr<FJsonValue>> JsonStatementIds;
	JsonStatementIds.Reserve(SourceStatementIds.Num());
	for (const FString& StatementId : SourceStatementIds)
	{
		JsonStatementIds.Add(MakeShared<FJsonValueString>(StatementId));
	}
	TArray<TSharedPtr<FJsonValue>> JsonLocalRenames;
	int32 CopiedLocalCount = 0;
	for (const FFinareAuthoringAction* Action : SourceActions)
	{
		if (Action->GraphKind != TEXT("variable"))
		{
			if (NewLocalNamesByActionId.Contains(Action->Id))
			{
				OutError = TEXT("Only copied direct local declarations may carry rename mappings.");
				return false;
			}
			continue;
		}

		++CopiedLocalCount;
		const FString ExpectedName = Action->Properties.FindRef(TEXT("name"));
		const FString* NewName = NewLocalNamesByActionId.Find(Action->Id);
		if (ExpectedName.IsEmpty() || NewName == nullptr || NewName->IsEmpty() ||
			*NewName == ExpectedName)
		{
			OutError = TEXT("Every copied direct local declaration requires one distinct fresh name.");
			return false;
		}
		const TSharedRef<FJsonObject> Rename = MakeShared<FJsonObject>();
		Rename->SetStringField(TEXT("sourceStatementId"), ResolveStatementId(*Action));
		Rename->SetStringField(TEXT("expectedName"), ExpectedName);
		Rename->SetStringField(TEXT("newName"), *NewName);
		JsonLocalRenames.Add(MakeShared<FJsonValueObject>(Rename));
	}
	if (CopiedLocalCount != NewLocalNamesByActionId.Num())
	{
		OutError = TEXT("The copied local rename set no longer matches the selected source range.");
		return false;
	}

	const FString SourcePrevious = FirstIndex > 0
		                               ? SourceStructuralIds[FirstIndex - 1]
		                               : FString();
	const int32 SourceNextIndex = FirstIndex + SourceStatementIds.Num();
	const FString SourceNext = SourceNextIndex < SourceStructuralIds.Num()
		                           ? SourceStructuralIds[SourceNextIndex]
		                           : FString();
	const FString DestinationPrevious = DestinationStatementId;
	const FString DestinationNext = DestinationIndex + 1 < DestinationStructuralIds.Num()
		                                ? DestinationStructuralIds[DestinationIndex + 1]
		                                : FString();

	const TSharedRef<FJsonObject> Operation = MakeShared<FJsonObject>();
	Operation->SetStringField(TEXT("operation"), TEXT("duplicateStatements"));
	Operation->SetStringField(TEXT("targetNodeId"), SourceStatementIds[0]);
	Operation->SetStringField(
		TEXT("expectedSourceParentBlockId"), SourceBlock->SourceNodeId);
	Operation->SetArrayField(TEXT("expectedStatementIds"), MoveTemp(JsonStatementIds));
	SetOptionalId(Operation, TEXT("expectedSourcePreviousSiblingId"), SourcePrevious);
	SetOptionalId(Operation, TEXT("expectedSourceNextSiblingId"), SourceNext);
	Operation->SetStringField(
		TEXT("destinationParentBlockId"), DestinationBlock->SourceNodeId);
	SetOptionalId(
		Operation,
		TEXT("expectedDestinationPreviousSiblingId"),
		DestinationPrevious);
	SetOptionalId(
		Operation,
		TEXT("expectedDestinationNextSiblingId"),
		DestinationNext);
	Operation->SetArrayField(TEXT("localRenames"), MoveTemp(JsonLocalRenames));
	return SerializeRoot(
		MakePatchRoot(Document, Operation),
		OutPatchJson,
		OutError,
		TEXT("the statement-range duplication patch"));
}

bool FFinareAuthoringSurfaceReader::BuildCallableExtractionPatch(
	const FFinareAuthoringSurfaceDocument& Document,
	const FFinareAuthoringFunction& Function,
	const TArray<FString>& StatementActionIds,
	const FString& MethodName,
	FString& OutPatchJson,
	FString& OutError)
{
	using namespace UE::Finare::AuthoringSurface::Editing::Private;
	if (StatementActionIds.IsEmpty())
	{
		OutError = TEXT("Extract Function requires at least one selected statement node.");
		return false;
	}

	const FString TrimmedMethodName = MethodName.TrimStartAndEnd();
	const auto IsIdentifierStart = [](const TCHAR Character)
	{
		return Character == TEXT('_') || FChar::IsAlpha(Character);
	};
	const auto IsIdentifierPart = [&IsIdentifierStart](const TCHAR Character)
	{
		return IsIdentifierStart(Character) || FChar::IsDigit(Character);
	};
	if (TrimmedMethodName.IsEmpty() || TrimmedMethodName.Len() > 128 ||
		TrimmedMethodName != MethodName || !IsIdentifierStart(TrimmedMethodName[0]))
	{
		OutError = TEXT("Extract Function requires a non-empty C# method identifier of at most 128 characters.");
		return false;
	}
	for (const TCHAR Character : TrimmedMethodName)
	{
		if (!IsIdentifierPart(Character))
		{
			OutError = TEXT("Extract Function method names may contain only letters, digits, and underscores.");
			return false;
		}
	}

	const auto IsTerminalOrSynthetic = [](const FFinareAuthoringAction& Action)
	{
		return Action.GraphKind == TEXT("entry") ||
			Action.GraphKind == TEXT("unrealEventEntry") ||
			Action.GraphKind == TEXT("switchCase") ||
			Action.GraphKind == TEXT("return") ||
			Action.GraphKind == TEXT("break") ||
			Action.GraphKind == TEXT("continue");
	};
	const FFinareAuthoringRegion* ParentBlock = nullptr;
	TArray<FString> SelectedStatementIds;
	SelectedStatementIds.Reserve(StatementActionIds.Num());
	for (const FString& ActionId : StatementActionIds)
	{
		const FFinareAuthoringAction* Action = Function.FindAction(ActionId);
		const FFinareAuthoringRegion* Block = Action != nullptr
			                                      ? FindOwningBlock(Function, Action->Id)
			                                      : nullptr;
		if (Action == nullptr || Block == nullptr || IsTerminalOrSynthetic(*Action) ||
			(ParentBlock != nullptr && ParentBlock->SourceNodeId != Block->SourceNodeId))
		{
			OutError = TEXT("Extract Function requires ordinary statement nodes from one explicit block.");
			return false;
		}
		ParentBlock = Block;
		SelectedStatementIds.AddUnique(ResolveStatementId(*Action));
	}
	if (ParentBlock == nullptr || SelectedStatementIds.Num() != StatementActionIds.Num())
	{
		OutError = TEXT("An extracted statement range cannot contain the same semantic statement twice.");
		return false;
	}

	const TArray<FString> StructuralIds = GetStructuralStatementIds(Function, *ParentBlock);
	SelectedStatementIds.Sort([&StructuralIds](const FString& Left, const FString& Right)
	{
		return StructuralIds.IndexOfByKey(Left) < StructuralIds.IndexOfByKey(Right);
	});
	const int32 FirstIndex = StructuralIds.IndexOfByKey(SelectedStatementIds[0]);
	if (FirstIndex == INDEX_NONE ||
		FirstIndex + SelectedStatementIds.Num() > StructuralIds.Num())
	{
		OutError = TEXT("The extracted statements no longer exist in compiler-owned block order.");
		return false;
	}
	for (int32 Offset = 0; Offset < SelectedStatementIds.Num(); ++Offset)
	{
		if (StructuralIds[FirstIndex + Offset] != SelectedStatementIds[Offset])
		{
			OutError = TEXT(
				"Extract Function requires one consecutive statement range without hidden regions between nodes.");
			return false;
		}
	}

	TArray<TSharedPtr<FJsonValue>> JsonStatementIds;
	JsonStatementIds.Reserve(SelectedStatementIds.Num());
	for (const FString& StatementId : SelectedStatementIds)
	{
		JsonStatementIds.Add(MakeShared<FJsonValueString>(StatementId));
	}
	const FString Previous = FirstIndex > 0 ? StructuralIds[FirstIndex - 1] : FString();
	const int32 NextIndex = FirstIndex + SelectedStatementIds.Num();
	const FString Next = NextIndex < StructuralIds.Num() ? StructuralIds[NextIndex] : FString();

	const TSharedRef<FJsonObject> Operation = MakeShared<FJsonObject>();
	Operation->SetStringField(TEXT("operation"), TEXT("extractCallable"));
	Operation->SetStringField(TEXT("targetNodeId"), SelectedStatementIds[0]);
	Operation->SetStringField(TEXT("expectedContainingMethodId"), Function.Id);
	Operation->SetStringField(TEXT("expectedParentBlockId"), ParentBlock->SourceNodeId);
	Operation->SetArrayField(TEXT("expectedStatementIds"), MoveTemp(JsonStatementIds));
	SetOptionalId(Operation, TEXT("expectedPreviousSiblingId"), Previous);
	SetOptionalId(Operation, TEXT("expectedNextSiblingId"), Next);
	Operation->SetStringField(TEXT("methodName"), TrimmedMethodName);
	return SerializeRoot(
		MakePatchRoot(Document, Operation),
		OutPatchJson,
		OutError,
		TEXT("the callable extraction patch"));
}

bool FFinareAuthoringSurfaceReader::BuildStatementCollapsePatch(
	const FFinareAuthoringSurfaceDocument& Document,
	const FFinareAuthoringFunction& Function,
	const TArray<FString>& StatementActionIds,
	FString& OutPatchJson,
	FString& OutError)
{
	using namespace UE::Finare::AuthoringSurface::Editing::Private;
	if (StatementActionIds.Num() < 2)
	{
		OutError = TEXT("Collapse Scope requires at least two selected statement nodes.");
		return false;
	}

	const FFinareAuthoringRegion* ParentBlock = nullptr;
	TArray<FString> SelectedStatementIds;
	for (const FString& ActionId : StatementActionIds)
	{
		const FFinareAuthoringAction* Action = Function.FindAction(ActionId);
		const FFinareAuthoringRegion* Block = Action != nullptr
			                                      ? FindOwningBlock(Function, Action->Id)
			                                      : nullptr;
		if (Action == nullptr || Block == nullptr ||
			(ParentBlock != nullptr && ParentBlock->SourceNodeId != Block->SourceNodeId))
		{
			OutError = TEXT("Collapse Scope requires statement nodes from one explicit block.");
			return false;
		}
		ParentBlock = Block;
		SelectedStatementIds.AddUnique(ResolveStatementId(*Action));
	}
	if (ParentBlock == nullptr || SelectedStatementIds.Num() != StatementActionIds.Num())
	{
		OutError = TEXT("Collapse Scope cannot contain the same semantic statement more than once.");
		return false;
	}

	const TArray<FString> StructuralIds = GetStructuralStatementIds(Function, *ParentBlock);
	SelectedStatementIds.Sort([&StructuralIds](const FString& Left, const FString& Right)
	{
		return StructuralIds.IndexOfByKey(Left) < StructuralIds.IndexOfByKey(Right);
	});
	const int32 FirstIndex = StructuralIds.IndexOfByKey(SelectedStatementIds[0]);
	if (FirstIndex == INDEX_NONE ||
		FirstIndex + SelectedStatementIds.Num() > StructuralIds.Num())
	{
		OutError = TEXT("The selected statements no longer exist in the compiler-owned block order.");
		return false;
	}
	for (int32 Offset = 0; Offset < SelectedStatementIds.Num(); ++Offset)
	{
		if (StructuralIds[FirstIndex + Offset] != SelectedStatementIds[Offset])
		{
			OutError = TEXT(
				"Collapse Scope requires one consecutive statement range without hidden regions between nodes.");
			return false;
		}
	}

	const FString Previous = FirstIndex > 0 ? StructuralIds[FirstIndex - 1] : FString();
	const int32 NextIndex = FirstIndex + SelectedStatementIds.Num();
	const FString Next = NextIndex < StructuralIds.Num() ? StructuralIds[NextIndex] : FString();
	TArray<TSharedPtr<FJsonValue>> JsonStatementIds;
	JsonStatementIds.Reserve(SelectedStatementIds.Num());
	for (const FString& StatementId : SelectedStatementIds)
	{
		JsonStatementIds.Add(MakeShared<FJsonValueString>(StatementId));
	}

	const TSharedRef<FJsonObject> Operation = MakeShared<FJsonObject>();
	Operation->SetStringField(TEXT("operation"), TEXT("collapseStatements"));
	Operation->SetStringField(TEXT("targetNodeId"), SelectedStatementIds[0]);
	Operation->SetStringField(TEXT("expectedParentBlockId"), ParentBlock->SourceNodeId);
	Operation->SetArrayField(TEXT("expectedStatementIds"), MoveTemp(JsonStatementIds));
	SetOptionalId(Operation, TEXT("expectedPreviousSiblingId"), Previous);
	SetOptionalId(Operation, TEXT("expectedNextSiblingId"), Next);
	return SerializeRoot(
		MakePatchRoot(Document, Operation),
		OutPatchJson,
		OutError,
		TEXT("the statement collapse patch"));
}

bool FFinareAuthoringSurfaceReader::BuildBlockExpansionPatch(
	const FFinareAuthoringSurfaceDocument& Document,
	const FFinareAuthoringFunction& Function,
	const FFinareAuthoringRegion& BlockRegion,
	FString& OutPatchJson,
	FString& OutError)
{
	using namespace UE::Finare::AuthoringSurface::Editing::Private;
	const FFinareAuthoringRegion* Parent =
		Function.FindRegion(BlockRegion.ParentRegionId);
	if (BlockRegion.Kind != TEXT("block") || Parent == nullptr ||
		Parent->Kind == TEXT("function") || BlockRegion.SourceNodeId.IsEmpty())
	{
		OutError = TEXT("Only a nested lexical block inside a function body or structured region can be expanded.");
		return false;
	}

	const TArray<FString> ParentStatementIds = GetStructuralStatementIds(Function, *Parent);
	const int32 BlockIndex = ParentStatementIds.IndexOfByKey(BlockRegion.SourceNodeId);
	const TArray<FString> ChildStatementIds = GetStructuralStatementIds(Function, BlockRegion);
	if (BlockIndex == INDEX_NONE || ChildStatementIds.IsEmpty())
	{
		OutError = TEXT("The nested block or its compiler-owned child statement list is unavailable.");
		return false;
	}
	const FString Previous = BlockIndex > 0 ? ParentStatementIds[BlockIndex - 1] : FString();
	const FString Next = BlockIndex + 1 < ParentStatementIds.Num()
		                     ? ParentStatementIds[BlockIndex + 1]
		                     : FString();
	TArray<TSharedPtr<FJsonValue>> JsonChildIds;
	JsonChildIds.Reserve(ChildStatementIds.Num());
	for (const FString& StatementId : ChildStatementIds)
	{
		JsonChildIds.Add(MakeShared<FJsonValueString>(StatementId));
	}

	const TSharedRef<FJsonObject> Operation = MakeShared<FJsonObject>();
	Operation->SetStringField(TEXT("operation"), TEXT("expandBlock"));
	Operation->SetStringField(TEXT("targetNodeId"), BlockRegion.SourceNodeId);
	Operation->SetStringField(TEXT("expectedParentBlockId"), Parent->SourceNodeId);
	Operation->SetArrayField(TEXT("expectedChildStatementIds"), MoveTemp(JsonChildIds));
	SetOptionalId(Operation, TEXT("expectedPreviousSiblingId"), Previous);
	SetOptionalId(Operation, TEXT("expectedNextSiblingId"), Next);
	return SerializeRoot(
		MakePatchRoot(Document, Operation),
		OutPatchJson,
		OutError,
		TEXT("the block expansion patch"));
}

FString FFinareAuthoringSurfaceReader::ResolveConfiguredPath(
	const FString& ConfiguredPath)
{
	FString Result = ConfiguredPath;
	FString ProjectDir = FPaths::GetPath(FPaths::GetProjectFilePath());
	if (ProjectDir.IsEmpty())
	{
		ProjectDir = FPaths::ProjectDir();
	}
	ProjectDir = FPaths::ConvertRelativePathToFull(ProjectDir);
	FPaths::NormalizeDirectoryName(ProjectDir);
	const TSharedPtr<IPlugin> Plugin = IPluginManager::Get().FindPlugin(TEXT("Finare"));
	if (Plugin.IsValid())
	{
		Result.ReplaceInline(TEXT("{PluginDir}"), *Plugin->GetBaseDir(), ESearchCase::IgnoreCase);
	}
	Result.ReplaceInline(TEXT("{ProjectDir}"), *ProjectDir, ESearchCase::IgnoreCase);
	if (FPaths::IsRelative(Result))
	{
		Result = FPaths::Combine(ProjectDir, Result);
	}
	Result = FPaths::ConvertRelativePathToFull(Result);
	FPaths::NormalizeFilename(Result);
	return Result;
}
