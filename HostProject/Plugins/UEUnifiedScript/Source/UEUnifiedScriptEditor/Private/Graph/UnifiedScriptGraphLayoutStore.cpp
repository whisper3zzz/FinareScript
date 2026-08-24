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
    TMap<FString, FVector2D>& OutPositions,
    FString& OutError)
{
    using namespace UE::UnifiedScript::Editor::Layout::Private;
    OutPositions.Reset();
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
        const TArray<TSharedPtr<FJsonValue>>* Nodes = nullptr;
        if (!Function.IsValid() ||
            !Function->TryGetStringField(TEXT("functionId"), CandidateId) ||
            CandidateId != FunctionId ||
            !Function->TryGetArrayField(TEXT("nodes"), Nodes) || Nodes == nullptr)
        {
            continue;
        }
        for (const TSharedPtr<FJsonValue>& NodeValue : *Nodes)
        {
            const TSharedPtr<FJsonObject> Node = NodeValue.IsValid() ? NodeValue->AsObject() : nullptr;
            FString NodeId;
            FString Placement;
            const TSharedPtr<FJsonObject>* Position = nullptr;
            double X = 0.0;
            double Y = 0.0;
            if (Node.IsValid() && Node->TryGetStringField(TEXT("nodeId"), NodeId) &&
                Node->TryGetStringField(TEXT("placement"), Placement) &&
                Placement == TEXT("placed") &&
                Node->TryGetObjectField(TEXT("position"), Position) && Position != nullptr &&
                (*Position)->TryGetNumberField(TEXT("x"), X) &&
                (*Position)->TryGetNumberField(TEXT("y"), Y))
            {
                OutPositions.Add(NodeId, FVector2D(X, Y));
            }
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
    TSharedPtr<FJsonObject> Root;
    if (!LoadDocument(LayoutPath, Root, OutError))
    {
        return false;
    }

    TArray<TSharedPtr<FJsonValue>> Functions = Root->GetArrayField(TEXT("functions"));
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
    for (const UEdGraphNode* Node : Nodes)
    {
        if (const UUnifiedScriptEdGraphNode* ScriptNode = Cast<UUnifiedScriptEdGraphNode>(Node))
        {
            ScriptNodes.Add(ScriptNode);
        }
    }
    ScriptNodes.Sort([](const UUnifiedScriptEdGraphNode& A, const UUnifiedScriptEdGraphNode& B)
    {
        return A.SemanticId < B.SemanticId;
    });

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
