#include "Graph/UnifiedScriptGraphPresentation.h"

namespace UE::UnifiedScript::Editor::Presentation::Private
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
    const FUnifiedScriptAuthoringAction& Action,
    const FUnifiedScriptAuthoringPort& Port)
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
}

bool FUnifiedScriptGraphPresentation::ShouldInlineAction(
    const FUnifiedScriptAuthoringAction& Action,
    const FUnifiedScriptAuthoringFunction& Function)
{
    if (Action.GraphKind != TEXT("literal") && Action.GraphKind != TEXT("identifier"))
    {
        return false;
    }

    return Function.Edges.ContainsByPredicate([&Action](const FUnifiedScriptAuthoringEdge& Edge)
    {
        return Edge.Kind == TEXT("data") && Edge.FromNode == Action.Id;
    });
}

int32 FUnifiedScriptGraphPresentation::CountVisibleActions(
    const FUnifiedScriptAuthoringFunction& Function)
{
    int32 Count = 0;
    for (const FUnifiedScriptAuthoringAction& Action : Function.Actions)
    {
        Count += ShouldInlineAction(Action, Function) ? 0 : 1;
    }
    return Count;
}

int32 FUnifiedScriptGraphPresentation::CountVisibleEdges(
    const FUnifiedScriptAuthoringFunction& Function)
{
    int32 Count = 0;
    for (const FUnifiedScriptAuthoringEdge& Edge : Function.Edges)
    {
        const FUnifiedScriptAuthoringAction* From = Function.FindAction(Edge.FromNode);
        const FUnifiedScriptAuthoringAction* To = Function.FindAction(Edge.ToNode);
        if (From != nullptr && To != nullptr && !ShouldInlineAction(*From, Function) &&
            !ShouldInlineAction(*To, Function))
        {
            ++Count;
        }
    }
    return Count;
}

FString FUnifiedScriptGraphPresentation::GetNodeTitle(
    const FUnifiedScriptAuthoringAction& Action)
{
    return Action.Title;
}

bool FUnifiedScriptGraphPresentation::ShouldShowSummary(
    const FUnifiedScriptAuthoringAction& Action)
{
    return Action.GraphKind == TEXT("entry") || Action.GraphKind == TEXT("loop") ||
        Action.GraphKind == TEXT("invocation") || Action.GraphKind == TEXT("await") ||
        Action.GraphKind == TEXT("memberAccess");
}

bool FUnifiedScriptGraphPresentation::IsCompactNode(
    const FUnifiedScriptAuthoringAction& Action)
{
    return Action.GraphKind == TEXT("binary") || Action.GraphKind == TEXT("unary") ||
        Action.GraphKind == TEXT("memberAccess");
}

FString FUnifiedScriptGraphPresentation::GetPinLabel(
    const FUnifiedScriptAuthoringAction& Action,
    const FUnifiedScriptAuthoringPort& Port,
    const FUnifiedScriptAuthoringFunction& Function)
{
    using namespace UE::UnifiedScript::Editor::Presentation::Private;
    FString Label = BasePinLabel(Action, Port);
    if (const FUnifiedScriptAuthoringAction* Source = FindInlineSource(Action, Port, Function))
    {
        const FString InlineValue = CompactInlineValue(
            Source->Summary.IsEmpty() ? Source->Title : Source->Summary);
        return Label.IsEmpty() ? InlineValue : Label + TEXT(" = ") + InlineValue;
    }
    return Label;
}

FString FUnifiedScriptGraphPresentation::GetPinTooltip(
    const FUnifiedScriptAuthoringAction& Action,
    const FUnifiedScriptAuthoringPort& Port,
    const FUnifiedScriptAuthoringFunction& Function)
{
    const FString Type = Port.IsControl()
        ? TEXT("control flow")
        : Port.TypeDisplayName.IsEmpty() ? TEXT("unknown data") : Port.TypeDisplayName;
    FString Tooltip = FString::Printf(
        TEXT("%s %s\nIR pin: %s"),
        *Port.Direction,
        *Type,
        *Port.Name);
    if (!Port.TypeId.IsEmpty())
    {
        Tooltip += TEXT("\nType ID: ") + Port.TypeId;
    }
    if (const FUnifiedScriptAuthoringAction* Source = FindInlineSource(Action, Port, Function))
    {
        Tooltip += FString::Printf(
            TEXT("\nInline source: %s\nSemantic ID: %s"),
            *Source->Summary,
            *Source->Id);
    }
    return Tooltip;
}

bool FUnifiedScriptGraphPresentation::ShouldHidePin(
    const FUnifiedScriptAuthoringAction& Action,
    const FUnifiedScriptAuthoringPort& Port,
    const FUnifiedScriptAuthoringFunction& Function)
{
    if (Port.IsControl() || Port.IsInput())
    {
        return false;
    }
    return !Function.Edges.ContainsByPredicate(
        [&Action, &Port](const FUnifiedScriptAuthoringEdge& Edge)
        {
            return Edge.FromNode == Action.Id && Edge.FromPort == Port.Name;
        });
}

const FUnifiedScriptAuthoringAction* FUnifiedScriptGraphPresentation::FindInlineSource(
    const FUnifiedScriptAuthoringAction& Action,
    const FUnifiedScriptAuthoringPort& Port,
    const FUnifiedScriptAuthoringFunction& Function)
{
    if (Port.IsControl() || !Port.IsInput())
    {
        return nullptr;
    }
    const FUnifiedScriptAuthoringEdge* Incoming = Function.Edges.FindByPredicate(
        [&Action, &Port](const FUnifiedScriptAuthoringEdge& Edge)
        {
            return Edge.Kind == TEXT("data") && Edge.ToNode == Action.Id &&
                Edge.ToPort == Port.Name;
        });
    if (Incoming == nullptr)
    {
        return nullptr;
    }
    const FUnifiedScriptAuthoringAction* Source = Function.FindAction(Incoming->FromNode);
    return Source != nullptr && ShouldInlineAction(*Source, Function) ? Source : nullptr;
}
