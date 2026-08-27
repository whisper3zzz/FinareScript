/**
 * @Copyright: 2026 Tencent Inc. All rights reserved. reserved.
 * @Author: whispersong
 * @Date: 2026-08-24 19:48:12
 * @LastEditors: whispersong
 * @LastEditTime: 2026-08-25 14:22:40
 * @FilePath: \Plugins\UEUnifiedScript\Source\UEUnifiedScriptEditor\Private\Graph\UnifiedScriptGraphPresentation.cpp
 * @Description:
 */
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

FString CompactNodeText(FString Value)
{
    Value.ReplaceInline(TEXT("\r"), TEXT(""));
    Value.ReplaceInline(TEXT("\n"), TEXT("  ·  "));
    Value.TrimStartAndEndInline();
    constexpr int32 MaxLength = 76;
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

FString FUnifiedScriptGraphPresentation::GetNodeSubtitle(
    const FUnifiedScriptAuthoringAction& Action)
{
    using namespace UE::UnifiedScript::Editor::Presentation::Private;
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

FString FUnifiedScriptGraphPresentation::GetNodeTooltip(
    const FUnifiedScriptAuthoringAction& Action)
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
        TEXT("member"),
        TEXT("unrealEventPath"),
        TEXT("unrealEventSource"),
        TEXT("unrealEventSourceType"),
        TEXT("referencedSymbolId"),
        TEXT("declaredSymbolId")};
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
        for (const FUnifiedScriptAuthoringPort& Port : Action.Ports)
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

bool FUnifiedScriptGraphPresentation::ShouldShowSummary(
    const FUnifiedScriptAuthoringAction& Action)
{
    return Action.GraphKind == TEXT("entry") || Action.GraphKind == TEXT("unrealEventEntry") ||
        Action.GraphKind == TEXT("loop") ||
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
    if (Action.GraphKind == TEXT("unrealEventEntry"))
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
