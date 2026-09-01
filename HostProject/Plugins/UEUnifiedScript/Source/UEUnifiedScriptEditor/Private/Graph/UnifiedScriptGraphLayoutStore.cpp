/**
 * @Copyright: 2026 Tencent Inc. All rights reserved. reserved.
 * @Author: whispersong
 * @Date: 2026-08-24 20:45:17
 * @LastEditors: whispersong
 * @LastEditTime: 2026-08-25 10:33:15
 * @FilePath: \Plugins\UEUnifiedScript\Source\UEUnifiedScriptEditor\Private\Graph\UnifiedScriptGraphLayoutStore.cpp
 * @Description:
 */
#include "Graph/UnifiedScriptGraphLayoutStore.h"

#include "Dom/JsonObject.h"
#include "EdGraph/EdGraphNode.h"
#include "HAL/FileManager.h"
#include "Misc/FileHelper.h"
#include "Misc/Paths.h"
#include "Serialization/JsonReader.h"
#include "Serialization/JsonSerializer.h"
#include "Serialization/JsonWriter.h"
#include "UnifiedScriptEdGraph.h"

namespace UE::UnifiedScript::Editor::Layout::Private
{
constexpr TCHAR SchemaVersion[] = TEXT("unifiedscript.graph-layout/v0.1");

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

TSharedRef<FJsonObject> MakeEmptyDocument()
{
    const TSharedRef<FJsonObject> Root = MakeShared<FJsonObject>();
    Root->SetStringField(TEXT("schemaVersion"), SchemaVersion);
    Root->SetArrayField(TEXT("functions"), {});
    Root->SetArrayField(TEXT("orphanedNodes"), {});
    return Root;
}

bool LoadDocument(
    const FString& Path,
    TSharedPtr<FJsonObject>& OutRoot,
    FString& OutError)
{
    if (!IFileManager::Get().FileExists(*Path))
    {
        OutRoot = MakeEmptyDocument();
        return true;
    }
    FString Json;
    FString Schema;
    if (!FFileHelper::LoadFileToString(Json, *Path) ||
        !FJsonSerializer::Deserialize(TJsonReaderFactory<>::Create(Json), OutRoot) ||
        !OutRoot.IsValid() ||
        !OutRoot->TryGetStringField(TEXT("schemaVersion"), Schema) ||
        Schema != SchemaVersion)
    {
        OutError = FString::Printf(TEXT("Graph layout sidecar is invalid or unsupported: %s"), *Path);
        return false;
    }
    return true;
}
}

FString FUnifiedScriptGraphLayoutStore::GetLayoutPath(const FString& SurfacePath)
{
    FString Result = SurfacePath;
    if (Result.EndsWith(TEXT(".surface.json"), ESearchCase::IgnoreCase))
    {
        Result.LeftChopInline(13);
        Result += TEXT(".usgraph");
    }
    else
    {
        Result = FPaths::ChangeExtension(Result, TEXT("usgraph"));
    }
    FPaths::NormalizeFilename(Result);
    return Result;
}

bool FUnifiedScriptGraphLayoutStore::LoadFunction(
    const FString& LayoutPath,
    const FString& FunctionId,
    FUnifiedScriptGraphFunctionLayout& OutLayout,
    FString& OutError)
{
    using namespace UE::UnifiedScript::Editor::Layout::Private;
    OutLayout = {};
    TSharedPtr<FJsonObject> Root;
    if (!LoadDocument(LayoutPath, Root, OutError))
    {
        return false;
    }
    const TArray<TSharedPtr<FJsonValue>>* Functions = nullptr;
    if (!Root->TryGetArrayField(TEXT("functions"), Functions) || Functions == nullptr)
    {
        OutError = TEXT("Graph layout requires a functions array.");
        return false;
    }
    for (const TSharedPtr<FJsonValue>& Value : *Functions)
    {
        const TSharedPtr<FJsonObject> Function = Value.IsValid() ? Value->AsObject() : nullptr;
        FString CandidateId;
        if (!Function.IsValid() ||
            !Function->TryGetStringField(TEXT("functionId"), CandidateId) ||
            !IsSemanticId(CandidateId))
        {
            OutError = TEXT("Graph layout contains an invalid function entry.");
            return false;
        }
        if (CandidateId != FunctionId)
        {
            continue;
        }

        const TArray<TSharedPtr<FJsonValue>>* Nodes = nullptr;
        const TArray<TSharedPtr<FJsonValue>>* Comments = nullptr;
        const TArray<TSharedPtr<FJsonValue>>* Routes = nullptr;
        if (!Function->TryGetArrayField(TEXT("nodes"), Nodes) || Nodes == nullptr ||
            !Function->TryGetArrayField(TEXT("commentRegions"), Comments) || Comments == nullptr ||
            !Function->TryGetArrayField(TEXT("routingHints"), Routes) || Routes == nullptr)
        {
            OutError = FString::Printf(
                TEXT("Graph layout function %s requires nodes, commentRegions, and routingHints arrays."),
                *FunctionId);
            return false;
        }
        TSet<FString> NodeIds;
        for (const TSharedPtr<FJsonValue>& NodeValue : *Nodes)
        {
            const TSharedPtr<FJsonObject> Node = NodeValue.IsValid() ? NodeValue->AsObject() : nullptr;
            FString NodeId;
            FString Placement;
            const TSharedPtr<FJsonObject>* Position = nullptr;
            const TSharedPtr<FJsonObject>* Size = nullptr;
            double X = 0.0;
            double Y = 0.0;
            double Width = 0.0;
            double Height = 0.0;
            bool bCollapsed = false;
            if (!Node.IsValid() ||
                !Node->TryGetStringField(TEXT("nodeId"), NodeId) ||
                !IsSemanticId(NodeId) || NodeIds.Contains(NodeId) ||
                !Node->TryGetStringField(TEXT("placement"), Placement) ||
                (Placement != TEXT("placed") && Placement != TEXT("unplaced")) ||
                !Node->TryGetBoolField(TEXT("collapsed"), bCollapsed))
            {
                OutError = FString::Printf(
                    TEXT("Graph layout has an invalid or duplicate node in function %s."),
                    *FunctionId);
                return false;
            }
            NodeIds.Add(NodeId);
            if (Placement == TEXT("placed"))
            {
                if (!Node->TryGetObjectField(TEXT("position"), Position) || Position == nullptr ||
                    !(*Position)->TryGetNumberField(TEXT("x"), X) ||
                    !(*Position)->TryGetNumberField(TEXT("y"), Y) ||
                    !FMath::IsFinite(X) || !FMath::IsFinite(Y) ||
                    !Node->TryGetObjectField(TEXT("size"), Size) || Size == nullptr ||
                    !(*Size)->TryGetNumberField(TEXT("width"), Width) ||
                    !(*Size)->TryGetNumberField(TEXT("height"), Height) ||
                    !FMath::IsFinite(Width) || !FMath::IsFinite(Height) ||
                    Width <= 0.0 || Height <= 0.0)
                {
                    OutError = FString::Printf(
                        TEXT("Graph layout node %s has an invalid placed position."),
                        *NodeId);
                    return false;
                }
                OutLayout.NodePositions.Add(NodeId, FVector2D(X, Y));
            }
            else if (Node->HasField(TEXT("position")) || Node->HasField(TEXT("size")))
            {
                OutError = FString::Printf(
                    TEXT("Unplaced graph layout node %s cannot carry position or size."),
                    *NodeId);
                return false;
            }
        }
        TSet<FString> CommentIds;
        for (const TSharedPtr<FJsonValue>& CommentValue : *Comments)
        {
            const TSharedPtr<FJsonObject> Comment = CommentValue.IsValid()
                ? CommentValue->AsObject()
                : nullptr;
            const TSharedPtr<FJsonObject>* Bounds = nullptr;
            FUnifiedScriptGraphCommentLayout Entry;
            double X = 0.0;
            double Y = 0.0;
            double Width = 0.0;
            double Height = 0.0;
            const TArray<TSharedPtr<FJsonValue>>* CommentNodeIds = nullptr;
            if (!Comment.IsValid() ||
                !Comment->TryGetStringField(TEXT("id"), Entry.Id) ||
                Entry.Id.TrimStartAndEnd().IsEmpty() ||
                !Comment->TryGetStringField(TEXT("title"), Entry.Title) ||
                !Comment->TryGetObjectField(TEXT("bounds"), Bounds) || Bounds == nullptr ||
                !(*Bounds)->TryGetNumberField(TEXT("x"), X) ||
                !(*Bounds)->TryGetNumberField(TEXT("y"), Y) ||
                !(*Bounds)->TryGetNumberField(TEXT("width"), Width) ||
                !(*Bounds)->TryGetNumberField(TEXT("height"), Height) ||
                !FMath::IsFinite(X) || !FMath::IsFinite(Y) ||
                !FMath::IsFinite(Width) || !FMath::IsFinite(Height) ||
                Width <= 0.0 || Height <= 0.0 ||
                !Comment->TryGetArrayField(TEXT("nodeIds"), CommentNodeIds) || CommentNodeIds == nullptr ||
                !Comment->TryGetBoolField(TEXT("collapsed"), Entry.bCollapsed) ||
                CommentIds.Contains(Entry.Id))
            {
                OutError = FString::Printf(
                    TEXT("Graph layout has an invalid comment region in function %s."),
                    *FunctionId);
                return false;
            }
            CommentIds.Add(Entry.Id);
            Entry.Position = FVector2D(X, Y);
            Entry.Size = FVector2D(Width, Height);
            for (const TSharedPtr<FJsonValue>& NodeIdValue : *CommentNodeIds)
            {
                FString NodeId;
                if (!NodeIdValue.IsValid() || !NodeIdValue->TryGetString(NodeId) ||
                    !IsSemanticId(NodeId) || Entry.NodeIds.Contains(NodeId))
                {
                    OutError = FString::Printf(
                        TEXT("Graph layout comment '%s' has invalid node membership."),
                        *Entry.Id);
                    return false;
                }
                Entry.NodeIds.Add(NodeId);
            }
            OutLayout.CommentRegions.Add(MoveTemp(Entry));
        }
        TSet<FString> RoutedEdges;
        for (const TSharedPtr<FJsonValue>& RouteValue : *Routes)
        {
            const TSharedPtr<FJsonObject> Route = RouteValue.IsValid()
                ? RouteValue->AsObject()
                : nullptr;
            FUnifiedScriptGraphRoutingHintLayout Entry;
            const TArray<TSharedPtr<FJsonValue>>* Points = nullptr;
            if (!Route.IsValid() ||
                !Route->TryGetStringField(TEXT("edgeId"), Entry.EdgeId) ||
                !IsSemanticId(Entry.EdgeId) || RoutedEdges.Contains(Entry.EdgeId) ||
                !Route->TryGetArrayField(TEXT("controlPoints"), Points) ||
                Points == nullptr || Points->IsEmpty())
            {
                OutError = FString::Printf(
                    TEXT("Graph layout has an invalid routing hint in function %s."),
                    *FunctionId);
                return false;
            }
            RoutedEdges.Add(Entry.EdgeId);
            for (const TSharedPtr<FJsonValue>& PointValue : *Points)
            {
                const TSharedPtr<FJsonObject> Point = PointValue.IsValid()
                    ? PointValue->AsObject()
                    : nullptr;
                double X = 0.0;
                double Y = 0.0;
                if (!Point.IsValid() || !Point->TryGetNumberField(TEXT("x"), X) ||
                    !Point->TryGetNumberField(TEXT("y"), Y) ||
                    !FMath::IsFinite(X) || !FMath::IsFinite(Y))
                {
                    OutError = FString::Printf(
                        TEXT("Graph layout route '%s' has an invalid control point."),
                        *Entry.EdgeId);
                    return false;
                }
                Entry.ControlPoints.Add(FVector2D(X, Y));
            }
            OutLayout.RoutingHints.Add(MoveTemp(Entry));
        }
        break;
    }
    OutError.Reset();
    return true;
}

bool FUnifiedScriptGraphLayoutStore::SaveFunction(
    const FString& LayoutPath,
    const FString& FunctionId,
    const TArray<UEdGraphNode*>& Nodes,
    FString& OutError)
{
    using namespace UE::UnifiedScript::Editor::Layout::Private;
    if (!IsSemanticId(FunctionId))
    {
        OutError = TEXT("Graph layout save requires a valid semantic function ID.");
        return false;
    }
    TSharedPtr<FJsonObject> Root;
    if (!LoadDocument(LayoutPath, Root, OutError))
    {
        return false;
    }

    const TArray<TSharedPtr<FJsonValue>>* ExistingFunctions = nullptr;
    if (!Root->TryGetArrayField(TEXT("functions"), ExistingFunctions) ||
        ExistingFunctions == nullptr)
    {
        OutError = TEXT("Graph layout requires a functions array.");
        return false;
    }
    TArray<TSharedPtr<FJsonValue>> Functions = *ExistingFunctions;
    TSharedPtr<FJsonObject> Function;
    for (const TSharedPtr<FJsonValue>& Value : Functions)
    {
        FString CandidateId;
        const TSharedPtr<FJsonObject> Candidate = Value.IsValid() ? Value->AsObject() : nullptr;
        if (Candidate.IsValid() && Candidate->TryGetStringField(TEXT("functionId"), CandidateId) &&
            CandidateId == FunctionId)
        {
            Function = Candidate;
            break;
        }
    }
    if (!Function.IsValid())
    {
        Function = MakeShared<FJsonObject>();
        Function->SetStringField(TEXT("functionId"), FunctionId);
        Function->SetArrayField(TEXT("commentRegions"), {});
        Function->SetArrayField(TEXT("routingHints"), {});
        Functions.Add(MakeShared<FJsonValueObject>(Function));
    }

    TArray<const UUnifiedScriptEdGraphNode*> ScriptNodes;
    TSet<FString> ScriptNodeIds;
    for (const UEdGraphNode* Node : Nodes)
    {
        if (const UUnifiedScriptEdGraphNode* ScriptNode = Cast<UUnifiedScriptEdGraphNode>(Node))
        {
            if (!IsSemanticId(ScriptNode->SemanticId) ||
                ScriptNodeIds.Contains(ScriptNode->SemanticId))
            {
                OutError = TEXT("Graph layout save encountered an invalid or duplicate semantic node.");
                return false;
            }
            ScriptNodeIds.Add(ScriptNode->SemanticId);
            ScriptNodes.Add(ScriptNode);
        }
    }
    ScriptNodes.Sort([](const UUnifiedScriptEdGraphNode& A, const UUnifiedScriptEdGraphNode& B)
    {
        return A.SemanticId < B.SemanticId;
    });

    const TArray<TSharedPtr<FJsonValue>>* ExistingOrphans = nullptr;
    if (!Root->TryGetArrayField(TEXT("orphanedNodes"), ExistingOrphans) ||
        ExistingOrphans == nullptr)
    {
        OutError = TEXT("Graph layout requires an orphanedNodes array.");
        return false;
    }
    TArray<TSharedPtr<FJsonValue>> RetainedOrphans;
    for (const TSharedPtr<FJsonValue>& OrphanValue : *ExistingOrphans)
    {
        const TSharedPtr<FJsonObject> Orphan = OrphanValue.IsValid()
            ? OrphanValue->AsObject()
            : nullptr;
        const TSharedPtr<FJsonObject>* OrphanNode = nullptr;
        FString OrphanFunctionId;
        FString OrphanNodeId;
        if (!Orphan.IsValid() ||
            !Orphan->TryGetStringField(TEXT("functionId"), OrphanFunctionId) ||
            !IsSemanticId(OrphanFunctionId) ||
            !Orphan->TryGetObjectField(TEXT("node"), OrphanNode) ||
            OrphanNode == nullptr || !OrphanNode->IsValid() ||
            !(*OrphanNode)->TryGetStringField(TEXT("nodeId"), OrphanNodeId) ||
            !IsSemanticId(OrphanNodeId))
        {
            OutError = TEXT("Graph layout contains an invalid orphaned node entry.");
            return false;
        }
        if (OrphanFunctionId == FunctionId && ScriptNodeIds.Contains(OrphanNodeId))
        {
            continue;
        }
        RetainedOrphans.Add(OrphanValue);
    }
    Root->SetArrayField(TEXT("orphanedNodes"), MoveTemp(RetainedOrphans));

    TArray<TSharedPtr<FJsonValue>> JsonNodes;
    for (const UUnifiedScriptEdGraphNode* Node : ScriptNodes)
    {
        const TSharedRef<FJsonObject> JsonNode = MakeShared<FJsonObject>();
        JsonNode->SetStringField(TEXT("nodeId"), Node->SemanticId);
        JsonNode->SetStringField(TEXT("placement"), TEXT("placed"));
        const TSharedRef<FJsonObject> Position = MakeShared<FJsonObject>();
        Position->SetNumberField(TEXT("x"), Node->NodePosX);
        Position->SetNumberField(TEXT("y"), Node->NodePosY);
        JsonNode->SetObjectField(TEXT("position"), Position);
        const TSharedRef<FJsonObject> Size = MakeShared<FJsonObject>();
        Size->SetNumberField(TEXT("width"), 240.0);
        Size->SetNumberField(TEXT("height"), 120.0);
        JsonNode->SetObjectField(TEXT("size"), Size);
        JsonNode->SetBoolField(TEXT("collapsed"), false);
        JsonNodes.Add(MakeShared<FJsonValueObject>(JsonNode));
    }
    Function->SetArrayField(TEXT("nodes"), MoveTemp(JsonNodes));

    TArray<const UUnifiedScriptEdGraphCommentNode*> CommentNodes;
    TArray<const UUnifiedScriptEdGraphRerouteNode*> RerouteNodes;
    for (const UEdGraphNode* Node : Nodes)
    {
        if (const UUnifiedScriptEdGraphCommentNode* Comment =
                Cast<UUnifiedScriptEdGraphCommentNode>(Node))
        {
            CommentNodes.Add(Comment);
        }
        else if (const UUnifiedScriptEdGraphRerouteNode* Reroute =
                     Cast<UUnifiedScriptEdGraphRerouteNode>(Node))
        {
            RerouteNodes.Add(Reroute);
        }
    }
    CommentNodes.Sort([](
        const UUnifiedScriptEdGraphCommentNode& A,
        const UUnifiedScriptEdGraphCommentNode& B)
    {
        return A.LayoutId < B.LayoutId;
    });
    TArray<TSharedPtr<FJsonValue>> JsonComments;
    TSet<FString> CommentLayoutIds;
    for (const UUnifiedScriptEdGraphCommentNode* Comment : CommentNodes)
    {
        if (Comment->LayoutId.TrimStartAndEnd().IsEmpty() ||
            CommentLayoutIds.Contains(Comment->LayoutId) ||
            Comment->NodeWidth <= 0 || Comment->NodeHeight <= 0)
        {
            OutError = TEXT("Graph layout save encountered an invalid or duplicate comment region.");
            return false;
        }
        CommentLayoutIds.Add(Comment->LayoutId);
        const TSharedRef<FJsonObject> JsonComment = MakeShared<FJsonObject>();
        JsonComment->SetStringField(TEXT("id"), Comment->LayoutId);
        JsonComment->SetStringField(TEXT("title"), Comment->NodeComment);
        const TSharedRef<FJsonObject> Bounds = MakeShared<FJsonObject>();
        Bounds->SetNumberField(TEXT("x"), Comment->NodePosX);
        Bounds->SetNumberField(TEXT("y"), Comment->NodePosY);
        Bounds->SetNumberField(TEXT("width"), Comment->NodeWidth);
        Bounds->SetNumberField(TEXT("height"), Comment->NodeHeight);
        JsonComment->SetObjectField(TEXT("bounds"), Bounds);

        TSet<FString> MemberIds;
        for (UObject* Member : Comment->GetNodesUnderComment())
        {
            if (const UUnifiedScriptEdGraphNode* ScriptNode =
                    Cast<UUnifiedScriptEdGraphNode>(Member))
            {
                MemberIds.Add(ScriptNode->SemanticId);
            }
        }
        const FBox2D CommentBounds(
            FVector2D(Comment->NodePosX, Comment->NodePosY),
            FVector2D(
                Comment->NodePosX + Comment->NodeWidth,
                Comment->NodePosY + Comment->NodeHeight));
        for (const UUnifiedScriptEdGraphNode* ScriptNode : ScriptNodes)
        {
            if (CommentBounds.IsInside(
                    FVector2D(ScriptNode->NodePosX, ScriptNode->NodePosY)))
            {
                MemberIds.Add(ScriptNode->SemanticId);
            }
        }
        TArray<FString> SortedMemberIds = MemberIds.Array();
        SortedMemberIds.Sort();
        TArray<TSharedPtr<FJsonValue>> JsonMembers;
        for (const FString& MemberId : SortedMemberIds)
        {
            JsonMembers.Add(MakeShared<FJsonValueString>(MemberId));
        }
        JsonComment->SetArrayField(TEXT("nodeIds"), MoveTemp(JsonMembers));
        JsonComment->SetBoolField(TEXT("collapsed"), Comment->bLayoutCollapsed);
        JsonComments.Add(MakeShared<FJsonValueObject>(JsonComment));
    }
    Function->SetArrayField(TEXT("commentRegions"), MoveTemp(JsonComments));

    RerouteNodes.Sort([](
        const UUnifiedScriptEdGraphRerouteNode& A,
        const UUnifiedScriptEdGraphRerouteNode& B)
    {
        return A.EdgeId != B.EdgeId
            ? A.EdgeId < B.EdgeId
            : A.PointIndex < B.PointIndex;
    });
    TArray<TSharedPtr<FJsonValue>> JsonRoutes;
    FString CurrentEdgeId;
    TSharedPtr<FJsonObject> CurrentRoute;
    TArray<TSharedPtr<FJsonValue>> CurrentPoints;
    const auto FlushRoute = [&JsonRoutes, &CurrentRoute, &CurrentPoints]()
    {
        if (CurrentRoute.IsValid() && !CurrentPoints.IsEmpty())
        {
            CurrentRoute->SetArrayField(TEXT("controlPoints"), MoveTemp(CurrentPoints));
            JsonRoutes.Add(MakeShared<FJsonValueObject>(CurrentRoute.ToSharedRef()));
        }
        CurrentRoute.Reset();
        CurrentPoints.Reset();
    };
    for (const UUnifiedScriptEdGraphRerouteNode* Reroute : RerouteNodes)
    {
        if (!IsSemanticId(Reroute->EdgeId) || Reroute->PointIndex < 0)
        {
            OutError = TEXT("Graph layout save encountered an invalid routed edge.");
            return false;
        }
        if (CurrentEdgeId != Reroute->EdgeId)
        {
            FlushRoute();
            CurrentEdgeId = Reroute->EdgeId;
            CurrentRoute = MakeShared<FJsonObject>();
            CurrentRoute->SetStringField(TEXT("edgeId"), CurrentEdgeId);
        }
        if (Reroute->PointIndex != CurrentPoints.Num())
        {
            OutError = FString::Printf(
                TEXT("Graph layout route %s has duplicate or non-contiguous point indices."),
                *Reroute->EdgeId);
            return false;
        }
        const TSharedRef<FJsonObject> Point = MakeShared<FJsonObject>();
        Point->SetNumberField(TEXT("x"), Reroute->NodePosX);
        Point->SetNumberField(TEXT("y"), Reroute->NodePosY);
        CurrentPoints.Add(MakeShared<FJsonValueObject>(Point));
    }
    FlushRoute();
    Function->SetArrayField(TEXT("routingHints"), MoveTemp(JsonRoutes));

    Functions.Sort([](const TSharedPtr<FJsonValue>& A, const TSharedPtr<FJsonValue>& B)
    {
        FString AId;
        FString BId;
        if (A.IsValid() && A->AsObject().IsValid()) A->AsObject()->TryGetStringField(TEXT("functionId"), AId);
        if (B.IsValid() && B->AsObject().IsValid()) B->AsObject()->TryGetStringField(TEXT("functionId"), BId);
        return AId < BId;
    });
    Root->SetArrayField(TEXT("functions"), MoveTemp(Functions));

    FString Json;
    const TSharedRef<TJsonWriter<TCHAR, TPrettyJsonPrintPolicy<TCHAR>>> Writer =
        TJsonWriterFactory<TCHAR, TPrettyJsonPrintPolicy<TCHAR>>::Create(&Json);
    if (!FJsonSerializer::Serialize(Root.ToSharedRef(), Writer))
    {
        OutError = TEXT("Unreal could not serialize the graph layout sidecar.");
        return false;
    }
    Json.AppendChar(TEXT('\n'));
    const FString TempPath = LayoutPath + TEXT(".tmp");
    IFileManager::Get().MakeDirectory(*FPaths::GetPath(LayoutPath), true);
    if (!FFileHelper::SaveStringToFile(Json, *TempPath, FFileHelper::EEncodingOptions::ForceUTF8WithoutBOM) ||
        !IFileManager::Get().Move(*LayoutPath, *TempPath, true, true, false, true))
    {
        IFileManager::Get().Delete(*TempPath, false, true);
        OutError = FString::Printf(TEXT("Could not atomically save graph layout: %s"), *LayoutPath);
        return false;
    }
    OutError.Reset();
    return true;
}
