#include "Graph/FinareGraphPresentation.h"

namespace UE::Finare::Editor::Presentation::Private
{
	FString SentenceCase(const FString& Value)
	{
		if (Value.IsEmpty())
		{
			return Value;
		}
		return Value.Left(1).ToUpper() + Value.Mid(1);
	}

	FString BasePinLabel(
		const FFinareAuthoringAction& Action,
		const FFinareAuthoringPort& Port)
	{
		if (Port.IsControl())
		{
			if (Action.GraphKind == TEXT("branch") && !Port.IsInput())
			{
				return SentenceCase(Port.Name);
			}
			return FString();
		}

		if (Action.GraphKind == TEXT("binary"))
		{
			if (Port.Name == TEXT("left"))
			{
				return TEXT("A");
			}
			if (Port.Name == TEXT("right"))
			{
				return TEXT("B");
			}
			if (!Port.IsInput() && Port.Name == TEXT("value"))
			{
				return TEXT("Result");
			}
		}
		if (Action.GraphKind == TEXT("unary") && Port.Name == TEXT("operand"))
		{
			return TEXT("Value");
		}
		if (Action.GraphKind == TEXT("assignment"))
		{
			if (Port.Name == TEXT("assignedValue"))
			{
				return TEXT("Value");
			}
			if (Port.Name == TEXT("target"))
			{
				return TEXT("Target");
			}
		}
		if (Action.GraphKind == TEXT("return") && Port.Name == TEXT("value"))
		{
			return TEXT("Return Value");
		}
		if (Port.Name == TEXT("condition"))
		{
			return TEXT("Condition");
		}
		if (Port.Name == TEXT("value"))
		{
			return Port.IsInput() ? TEXT("Value") : TEXT("Result");
		}
		return SentenceCase(Port.Name);
	}

	FString CompactInlineValue(const FString& Value)
	{
		constexpr int32 MaxLength = 34;
		return Value.Len() <= MaxLength ? Value : Value.Left(MaxLength - 3) + TEXT("...");
	}

	FString CompactNodeText(FString Value)
	{
		Value.ReplaceInline(TEXT("\r"), TEXT(""));
		Value.ReplaceInline(TEXT("\n"), TEXT("  ·  "));
		Value.TrimStartAndEndInline();
		constexpr int32 MaxLength = 76;
		return Value.Len() <= MaxLength ? Value : Value.Left(MaxLength - 3) + TEXT("...");
	}
}

bool FFinareGraphPresentation::ShouldInlineAction(
	const FFinareAuthoringAction& Action,
	const FFinareAuthoringFunction& Function)
{
	if (Action.GraphKind != TEXT("literal") && Action.GraphKind != TEXT("identifier"))
	{
		return false;
	}

	return Function.Edges.ContainsByPredicate([&Action](const FFinareAuthoringEdge& Edge)
	{
		return Edge.Kind == TEXT("data") && Edge.FromNode == Action.Id;
	});
}

int32 FFinareGraphPresentation::CountVisibleActions(
	const FFinareAuthoringFunction& Function)
{
	int32 Count = 0;
	for (const FFinareAuthoringAction& Action : Function.Actions)
	{
		Count += ShouldInlineAction(Action, Function) ? 0 : 1;
	}
	return Count;
}

int32 FFinareGraphPresentation::CountVisibleEdges(
	const FFinareAuthoringFunction& Function)
{
	int32 Count = 0;
	for (const FFinareAuthoringEdge& Edge : Function.Edges)
	{
		const FFinareAuthoringAction* From = Function.FindAction(Edge.FromNode);
		const FFinareAuthoringAction* To = Function.FindAction(Edge.ToNode);
		if (From != nullptr && To != nullptr && !ShouldInlineAction(*From, Function) &&
			!ShouldInlineAction(*To, Function))
		{
			++Count;
		}
	}
	return Count;
}

FString FFinareGraphPresentation::GetNodeTitle(
	const FFinareAuthoringAction& Action)
{
	return Action.Title;
}

FString FFinareGraphPresentation::GetNodeSubtitle(
	const FFinareAuthoringAction& Action)
{
	using namespace UE::Finare::Editor::Presentation::Private;
	if (Action.Properties.FindRef(TEXT("authoringBoundary")) == TEXT("sourceOnly"))
	{
		const FString Description = CompactNodeText(Action.Summary);
		return Description.IsEmpty()
			       ? TEXT("Source-only C#")
			       : TEXT("Source-only C#  ·  ") + Description;
	}
	if (Action.Summary.IsEmpty() || Action.Summary == Action.Title)
	{
		return FString();
	}
	if (Action.GraphKind == TEXT("unrealEventEntry"))
	{
		TArray<FString> Lines;
		Action.Summary.ParseIntoArrayLines(Lines, true);
		return Lines.IsEmpty() ? FString() : CompactNodeText(Lines.Last());
	}
	if (Action.GraphKind == TEXT("memberAccess"))
	{
		FString Member;
		FString Receiver;
		if (Action.Summary.Split(TEXT(" · "), &Member, &Receiver) && !Receiver.IsEmpty())
		{
			return CompactNodeText(TEXT("From ") + Receiver);
		}
	}
	return CompactNodeText(Action.Summary);
}

FString FFinareGraphPresentation::GetNodeTooltip(
	const FFinareAuthoringAction& Action)
{
	FString Tooltip = Action.Title;
	if (!Action.Summary.IsEmpty() && Action.Summary != Action.Title)
	{
		Tooltip += TEXT("\n") + Action.Summary;
	}
	Tooltip += FString::Printf(
		TEXT("\n\nContract\nKind: %s\nCategory: %s"),
		*Action.GraphKind,
		*Action.Category);
	static const TArray<FString> ContractKeys = {
		TEXT("effects"),
		TEXT("graphSafe"),
		TEXT("resolvedType"),
		TEXT("typeId"),
		TEXT("method"),
		TEXT("methodId"),
		TEXT("calleeBodyRepresentation"),
		TEXT("authoringBoundary"),
		TEXT("calleeBodyEditable"),
		TEXT("member"),
		TEXT("unrealEventPath"),
		TEXT("unrealEventSource"),
		TEXT("unrealEventSourceType"),
		TEXT("referencedSymbolId"),
		TEXT("declaredSymbolId")
	};
	for (const FString& Key : ContractKeys)
	{
		if (const FString* Value = Action.Properties.Find(Key); Value != nullptr)
		{
			Tooltip += FString::Printf(TEXT("\n%s: %s"), *Key, **Value);
		}
	}
	if (!Action.Ports.IsEmpty())
	{
		Tooltip += TEXT("\n\nPorts");
		for (const FFinareAuthoringPort& Port : Action.Ports)
		{
			Tooltip += FString::Printf(
				TEXT("\n%s %s: %s%s"),
				*Port.Direction,
				*Port.Name,
				Port.IsControl() ? TEXT("control") : *Port.TypeDisplayName,
				Port.TypeId.IsEmpty() ? TEXT("") : *FString::Printf(TEXT(" (%s)"), *Port.TypeId));
		}
	}
	Tooltip += FString::Printf(
		TEXT("\n\nSource\n%s\n\nSemantic ID\n%s"),
		*Action.Span.ToDisplayString(),
		*Action.Id);
	return Tooltip;
}

bool FFinareGraphPresentation::ShouldShowSummary(
	const FFinareAuthoringAction& Action)
{
	return Action.GraphKind == TEXT("entry") || Action.GraphKind == TEXT("unrealEventEntry") ||
		Action.GraphKind == TEXT("loop") ||
		Action.GraphKind == TEXT("invocation") || Action.GraphKind == TEXT("await") ||
		Action.GraphKind == TEXT("memberAccess");
}

bool FFinareGraphPresentation::IsCompactNode(
	const FFinareAuthoringAction& Action)
{
	return Action.GraphKind == TEXT("binary") || Action.GraphKind == TEXT("unary") ||
		Action.GraphKind == TEXT("memberAccess");
}

FString FFinareGraphPresentation::GetPinLabel(
	const FFinareAuthoringAction& Action,
	const FFinareAuthoringPort& Port,
	const FFinareAuthoringFunction& Function)
{
	using namespace UE::Finare::Editor::Presentation::Private;
	if (Action.GraphKind == TEXT("unrealEventEntry") &&
		Port.Name.StartsWith(TEXT("eventArg")))
	{
		int32 ParameterIndex = INDEX_NONE;
		if (LexTryParseString(ParameterIndex, *Port.Name.Mid(8)) &&
			Function.Parameters.IsValidIndex(ParameterIndex))
		{
			return Function.Parameters[ParameterIndex].Name;
		}
	}
	FString Label = BasePinLabel(Action, Port);
	if (const FFinareAuthoringAction* Source = FindInlineSource(Action, Port, Function))
	{
		const FString InlineValue = CompactInlineValue(
			Source->Summary.IsEmpty() ? Source->Title : Source->Summary);
		return Label.IsEmpty() ? InlineValue : Label + TEXT(" = ") + InlineValue;
	}
	return Label;
}

FString FFinareGraphPresentation::GetPinTooltip(
	const FFinareAuthoringAction& Action,
	const FFinareAuthoringPort& Port,
	const FFinareAuthoringFunction& Function)
{
	const FString Type = Port.IsControl()
		                     ? TEXT("control flow")
		                     : Port.TypeDisplayName.IsEmpty()
		                     ? TEXT("unknown data")
		                     : Port.TypeDisplayName;
	FString Tooltip = FString::Printf(
		TEXT("%s %s\nIR pin: %s"),
		*Port.Direction,
		*Type,
		*Port.Name);
	if (!Port.TypeId.IsEmpty())
	{
		Tooltip += TEXT("\nType ID: ") + Port.TypeId;
	}
	if (const FFinareAuthoringAction* Source = FindInlineSource(Action, Port, Function))
	{
		Tooltip += FString::Printf(
			TEXT("\nInline source: %s\nSemantic ID: %s"),
			*Source->Summary,
			*Source->Id);
	}
	return Tooltip;
}

bool FFinareGraphPresentation::ShouldHidePin(
	const FFinareAuthoringAction& Action,
	const FFinareAuthoringPort& Port,
	const FFinareAuthoringFunction& Function)
{
	if (Port.IsControl() || Port.IsInput())
	{
		return false;
	}
	if (Action.GraphKind == TEXT("unrealEventEntry"))
	{
		return false;
	}
	return !Function.Edges.ContainsByPredicate(
		[&Action, &Port](const FFinareAuthoringEdge& Edge)
		{
			return Edge.FromNode == Action.Id && Edge.FromPort == Port.Name;
		});
}

const FFinareAuthoringAction* FFinareGraphPresentation::FindInlineSource(
	const FFinareAuthoringAction& Action,
	const FFinareAuthoringPort& Port,
	const FFinareAuthoringFunction& Function)
{
	if (Port.IsControl() || !Port.IsInput())
	{
		return nullptr;
	}
	const FFinareAuthoringEdge* Incoming = Function.Edges.FindByPredicate(
		[&Action, &Port](const FFinareAuthoringEdge& Edge)
		{
			return Edge.Kind == TEXT("data") && Edge.ToNode == Action.Id &&
				Edge.ToPort == Port.Name;
		});
	if (Incoming == nullptr)
	{
		return nullptr;
	}
	const FFinareAuthoringAction* Source = Function.FindAction(Incoming->FromNode);
	return Source != nullptr && ShouldInlineAction(*Source, Function) ? Source : nullptr;
}
