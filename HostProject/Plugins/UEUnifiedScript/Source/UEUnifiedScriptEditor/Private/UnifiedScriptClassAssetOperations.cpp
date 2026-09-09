#include "UnifiedScriptClassAssetOperations.h"

#include "UnifiedScriptClassOperationJournal.h"

#include "AssetRegistry/AssetRegistryModule.h"
#include "AssetToolsModule.h"
#include "Dom/JsonObject.h"
#include "Editor.h"
#include "Engine/Blueprint.h"
#include "GameFramework/Actor.h"
#include "HAL/FileManager.h"
#include "HAL/PlatformProcess.h"
#include "Internationalization/Regex.h"
#include "Interfaces/IPluginManager.h"
#include "Kismet2/BlueprintEditorUtils.h"
#include "Kismet2/KismetEditorUtilities.h"
#include "Misc/FileHelper.h"
#include "Misc/PackageName.h"
#include "Misc/Paths.h"
#include "ObjectTools.h"
#include "Serialization/JsonReader.h"
#include "Serialization/JsonSerializer.h"
#include "Serialization/JsonWriter.h"
#include "UnifiedScriptActor.h"
#include "UnifiedScriptAsset.h"
#include "UnifiedScriptAuthoringSurface.h"
#include "UnifiedScriptBlueprint.h"
#include "UnifiedScriptComponent.h"
#include "UnifiedScriptProjectSetup.h"
#include "UObject/ObjectRedirector.h"
#include "UObject/SavePackage.h"

THIRD_PARTY_INCLUDES_START
#include <openssl/sha.h>
THIRD_PARTY_INCLUDES_END

namespace UE::UnifiedScript::ClassAssetOperations::Private
{
using FFileSnapshot = UE::UnifiedScript::ClassOperationJournal::FSnapshot;

bool GAssetToolsMutationActive = false;

class FScopedAssetToolsMutation
{
public:
    FScopedAssetToolsMutation()
    {
        check(!GAssetToolsMutationActive);
        GAssetToolsMutationActive = true;
    }

    ~FScopedAssetToolsMutation()
    {
        GAssetToolsMutationActive = false;
    }
};

struct FClassOperationContext
{
    UUnifiedScriptAsset* ScriptAsset = nullptr;
    UBlueprint* Blueprint = nullptr;
    FString ProjectRoot;
    FString DeploymentPath;
    FString FirstPlayRequestPath;
    FString ManagedProjectPath;
    FString CliExecutable;
    FString SourcePath;
    FString ConfiguredSourcePath;
    FString BlueprintPackagePath;
    FString ScriptAssetPackagePath;
    FString OldTypeName;
    FString ManagedNamespace;
    FString ManagedTypeName;
    FString DeclarationAssetPath;
    FString ClassId;
    FString Source;
    TSharedPtr<FJsonObject> DeploymentRoot;
    TArray<uint8> OriginalDeploymentBytes;
};

struct FTargetIdentity
{
    FString TypeName;
    FString BlueprintPackagePath;
    FString BlueprintPackageDirectory;
    FString BlueprintAssetPath;
    FString BlueprintClassPath;
    FString ScriptAssetName;
    FString ScriptAssetPackagePath;
    FString ScriptAssetObjectPath;
    FString SourcePath;
    FString ConfiguredSourcePath;
    FString ManagedTypeName;
    FString DeclarationAssetPath;
    FString ClassId;
};

struct FManagedReferenceEdit
{
    int32 Start = 0;
    int32 Length = 0;
    int32 Line = 0;
    int32 Column = 0;
    FString Kind;
    FString OriginalText;
    FString Replacement;
};

struct FManagedReferenceDocument
{
    FString LogicalPath;
    FString Path;
    FString TargetPath;
    FString SourceDigest;
    TArray<FManagedReferenceEdit> Edits;
};

struct FManagedReferencePlan
{
    FString ManagedTypeName;
    FString ReplacementTypeName;
    TArray<FManagedReferenceDocument> Documents;
};

struct FManagedIdentityDocument
{
    FString SourcePath;
    FString TargetPath;
    FString SourceDigest;
    FString SourceSidecarDigest;
    bool bHasSourceSidecarDigest = false;
    bool bPreserveIdentity = false;
    FString TargetSidecarPath;
    FString IdentitySidecarJson;
    bool bHasIdentitySidecarJson = false;
};

struct FManagedIdentityPlan
{
    FString ProjectPath;
    FString SemanticConfigurationId;
    TArray<FManagedIdentityDocument> Documents;
};

struct FManagedIdentityTransition
{
    FString SourcePath;
    FString TargetPath;
    bool bPreserveIdentity = false;
};

bool IsManagedIdentifier(const FString& Value)
{
    FRegexMatcher Matcher(
        FRegexPattern(TEXT("^[A-Za-z_][A-Za-z0-9_]*$")),
        Value);
    return Matcher.FindNext();
}

bool ReadJsonFile(const FString& Path, TSharedPtr<FJsonObject>& OutRoot, FString& OutError)
{
    FString Json;
    if (!FFileHelper::LoadFileToString(Json, *Path))
    {
        OutError = FString::Printf(TEXT("Cannot read '%s'."), *Path);
        return false;
    }
    const TSharedRef<TJsonReader<>> Reader = TJsonReaderFactory<>::Create(Json);
    if (!FJsonSerializer::Deserialize(Reader, OutRoot) || !OutRoot.IsValid())
    {
        OutError = FString::Printf(
            TEXT("Invalid JSON in '%s': %s"),
            *Path,
            *Reader->GetErrorMessage());
        return false;
    }
    return true;
}

FString ResolveRelativeTo(const FString& BaseDirectory, const FString& Value)
{
    FString Result = Value;
    if (FPaths::IsRelative(Result))
    {
        Result = FPaths::Combine(BaseDirectory, Result);
    }
    Result = FPaths::ConvertRelativePathToFull(Result);
    FPaths::NormalizeFilename(Result);
    return Result;
}

bool WriteBytesAtomically(
    const FString& Path,
    const TArray<uint8>& Bytes,
    FString& OutError)
{
    IFileManager::Get().MakeDirectory(*FPaths::GetPath(Path), true);
    const FString Temporary = Path + TEXT(".unifiedscript-class-op.tmp");
    if (!FFileHelper::SaveArrayToFile(Bytes, *Temporary))
    {
        IFileManager::Get().Delete(*Temporary, false, true);
        OutError = FString::Printf(TEXT("Cannot write '%s'."), *Temporary);
        return false;
    }
    if (!IFileManager::Get().Move(*Path, *Temporary, true, true, false, true))
    {
        IFileManager::Get().Delete(*Temporary, false, true);
        OutError = FString::Printf(TEXT("Cannot publish '%s'."), *Path);
        return false;
    }
    return true;
}

bool WriteTextAtomically(const FString& Path, const FString& Text, FString& OutError)
{
    FTCHARToUTF8 Utf8(*Text);
    TArray<uint8> Bytes;
    Bytes.Append(
        reinterpret_cast<const uint8*>(Utf8.Get()),
        Utf8.Length());
    return WriteBytesAtomically(Path, Bytes, OutError);
}

bool CaptureFile(const FString& Path, FFileSnapshot& OutSnapshot, FString& OutError)
{
    return UE::UnifiedScript::ClassOperationJournal::CaptureSnapshot(
        Path,
        OutSnapshot,
        OutError);
}

bool RestoreFiles(const TArray<FFileSnapshot>& Snapshots, FString& OutError)
{
    TArray<FString> Failures;
    for (const FFileSnapshot& Snapshot : Snapshots)
    {
        FString RestoreError;
        if (Snapshot.bExisted)
        {
            if (!WriteBytesAtomically(Snapshot.Path, Snapshot.Bytes, RestoreError))
            {
                Failures.Add(RestoreError);
            }
        }
        else if (IFileManager::Get().FileExists(*Snapshot.Path) &&
            !IFileManager::Get().Delete(*Snapshot.Path, false, true))
        {
            Failures.Add(FString::Printf(
                TEXT("Cannot remove newly published file '%s'."),
                *Snapshot.Path));
        }
    }
    OutError = FString::Join(Failures, TEXT("; "));
    return Failures.IsEmpty();
}

bool VerifyFilesUnchanged(
    const TArray<FFileSnapshot>& Snapshots,
    FString& OutError)
{
    for (const FFileSnapshot& Snapshot : Snapshots)
    {
        const bool bExists = IFileManager::Get().FileExists(*Snapshot.Path);
        if (bExists != Snapshot.bExisted)
        {
            OutError = FString::Printf(
                TEXT("Script Class transaction conflict: '%s' changed after preflight."),
                *Snapshot.Path);
            return false;
        }
        if (!bExists)
        {
            continue;
        }
        TArray<uint8> CurrentBytes;
        if (!FFileHelper::LoadFileToArray(CurrentBytes, *Snapshot.Path) ||
            CurrentBytes != Snapshot.Bytes)
        {
            OutError = FString::Printf(
                TEXT("Script Class transaction conflict: '%s' changed after preflight."),
                *Snapshot.Path);
            return false;
        }
    }
    return true;
}

bool SerializeJson(
    const TSharedRef<FJsonObject>& Root,
    FString& OutJson,
    FString& OutError)
{
    OutJson.Reset();
    const TSharedRef<TJsonWriter<TCHAR, TPrettyJsonPrintPolicy<TCHAR>>> Writer =
        TJsonWriterFactory<TCHAR, TPrettyJsonPrintPolicy<TCHAR>>::Create(&OutJson);
    if (!FJsonSerializer::Serialize(Root, Writer))
    {
        OutError = TEXT("Cannot serialize the Script Class transaction JSON.");
        return false;
    }
    OutJson.AppendChar(TEXT('\n'));
    return true;
}

FString QuoteProcessArgument(const FString& Value)
{
    FString Escaped = Value;
    Escaped.ReplaceInline(TEXT("\""), TEXT("\\\""));
    return FString::Printf(TEXT("\"%s\""), *Escaped);
}

bool ComputeSourceDigest(const TArray<uint8>& Bytes, FString& OutDigest)
{
    uint8 Digest[SHA256_DIGEST_LENGTH]{};
    if (SHA256(
            Bytes.GetData(),
            static_cast<size_t>(Bytes.Num()),
            Digest) == nullptr)
    {
        return false;
    }
    OutDigest = TEXT("sha256:") +
        BytesToHex(Digest, SHA256_DIGEST_LENGTH).ToLower();
    return true;
}

bool ParseManagedReferencePlan(
    const FString& PlanPath,
    const FString& ExpectedManagedTypeName,
    const FString& ExpectedReplacementTypeName,
    FManagedReferencePlan& OutPlan,
    FString& OutError)
{
    TSharedPtr<FJsonObject> Root;
    if (!ReadJsonFile(PlanPath, Root, OutError))
    {
        return false;
    }
    FString SchemaVersion;
    FString ManagedTypeName;
    FString ReplacementTypeName;
    if (!Root->TryGetStringField(TEXT("schemaVersion"), SchemaVersion) ||
        SchemaVersion != TEXT("unifiedscript.csharp-type-reference-plan/v0.1") ||
        !Root->TryGetStringField(TEXT("managedTypeName"), ManagedTypeName) ||
        ManagedTypeName != ExpectedManagedTypeName)
    {
        OutError = TEXT("Managed reference plan identity does not match the Script Class transaction.");
        return false;
    }
    Root->TryGetStringField(TEXT("replacementTypeName"), ReplacementTypeName);
    if (ReplacementTypeName != ExpectedReplacementTypeName)
    {
        OutError = TEXT("Managed reference plan replacement does not match the Script Class transaction.");
        return false;
    }

    const TArray<TSharedPtr<FJsonValue>>* DocumentValues = nullptr;
    if (!Root->TryGetArrayField(TEXT("documents"), DocumentValues) ||
        DocumentValues == nullptr)
    {
        OutError = TEXT("Managed reference plan documents are unavailable.");
        return false;
    }
    FManagedReferencePlan Plan;
    Plan.ManagedTypeName = ManagedTypeName;
    Plan.ReplacementTypeName = ReplacementTypeName;
    for (const TSharedPtr<FJsonValue>& DocumentValue : *DocumentValues)
    {
        const TSharedPtr<FJsonObject> DocumentObject =
            DocumentValue.IsValid() ? DocumentValue->AsObject() : nullptr;
        FManagedReferenceDocument Document;
        if (!DocumentObject.IsValid() ||
            !DocumentObject->TryGetStringField(TEXT("logicalPath"), Document.LogicalPath) ||
            !DocumentObject->TryGetStringField(TEXT("path"), Document.Path) ||
            !DocumentObject->TryGetStringField(TEXT("sourceDigest"), Document.SourceDigest))
        {
            OutError = TEXT("Managed reference plan contains an invalid document.");
            return false;
        }
        Document.Path = FPaths::ConvertRelativePathToFull(Document.Path);
        FPaths::NormalizeFilename(Document.Path);
        const TArray<TSharedPtr<FJsonValue>>* EditValues = nullptr;
        if (!DocumentObject->TryGetArrayField(TEXT("edits"), EditValues) ||
            EditValues == nullptr || EditValues->IsEmpty())
        {
            OutError = TEXT("Managed reference plan contains an empty document edit set.");
            return false;
        }
        for (const TSharedPtr<FJsonValue>& EditValue : *EditValues)
        {
            const TSharedPtr<FJsonObject> EditObject =
                EditValue.IsValid() ? EditValue->AsObject() : nullptr;
            double Start = 0.0;
            double Length = 0.0;
            double Line = 0.0;
            double Column = 0.0;
            FManagedReferenceEdit Edit;
            if (!EditObject.IsValid() ||
                !EditObject->TryGetNumberField(TEXT("start"), Start) ||
                !EditObject->TryGetNumberField(TEXT("length"), Length) ||
                !EditObject->TryGetNumberField(TEXT("line"), Line) ||
                !EditObject->TryGetNumberField(TEXT("column"), Column) ||
                !EditObject->TryGetStringField(TEXT("kind"), Edit.Kind) ||
                !EditObject->TryGetStringField(TEXT("originalText"), Edit.OriginalText))
            {
                OutError = TEXT("Managed reference plan contains an invalid edit.");
                return false;
            }
            EditObject->TryGetStringField(TEXT("replacement"), Edit.Replacement);
            Edit.Start = static_cast<int32>(Start);
            Edit.Length = static_cast<int32>(Length);
            Edit.Line = static_cast<int32>(Line);
            Edit.Column = static_cast<int32>(Column);
            if (Edit.Start < 0 || Edit.Length <= 0 || Edit.Line <= 0 ||
                Edit.Column <= 0 ||
                (!ExpectedReplacementTypeName.IsEmpty() &&
                    Edit.Replacement != ExpectedReplacementTypeName))
            {
                OutError = TEXT("Managed reference plan edit is outside the supported transaction contract.");
                return false;
            }
            Document.Edits.Add(MoveTemp(Edit));
        }
        Document.Edits.Sort(
            [](const FManagedReferenceEdit& Left, const FManagedReferenceEdit& Right)
            {
                return Left.Start < Right.Start;
            });
        Plan.Documents.Add(MoveTemp(Document));
    }
    OutPlan = MoveTemp(Plan);
    return true;
}

bool RunManagedReferencePlanner(
    const FClassOperationContext& Context,
    const FString& ReplacementTypeName,
    const FString& TransactionId,
    FManagedReferencePlan& OutPlan,
    FString& OutError)
{
    const FString PlanDirectory = FPaths::Combine(
        Context.ProjectRoot,
        TEXT("Saved/UnifiedScript/ClassOperations"));
    IFileManager::Get().MakeDirectory(*PlanDirectory, true);
    const FString PlanPath = FPaths::Combine(
        PlanDirectory,
        TransactionId + TEXT(".references.json"));
    FString Arguments = FString::Printf(
        TEXT("script-class-reference-plan %s --type %s --out %s --configuration Debug"),
        *QuoteProcessArgument(Context.ManagedProjectPath),
        *QuoteProcessArgument(Context.ManagedTypeName),
        *QuoteProcessArgument(PlanPath));
    FString DotNetExecutable = FPaths::ConvertRelativePathToFull(FPaths::Combine(
        FPaths::EngineDir(),
        TEXT("Binaries/ThirdParty/DotNet/10.0/win-x64/dotnet.exe")));
    FPaths::NormalizeFilename(DotNetExecutable);
    if (IFileManager::Get().FileExists(*DotNetExecutable))
    {
        Arguments += TEXT(" --dotnet ") + QuoteProcessArgument(DotNetExecutable);
    }
    if (!ReplacementTypeName.IsEmpty())
    {
        Arguments += TEXT(" --new-name ") + QuoteProcessArgument(ReplacementTypeName);
    }

    int32 ReturnCode = INDEX_NONE;
    FString StandardOutput;
    FString StandardError;
    if (!FPlatformProcess::ExecProcess(
            *Context.CliExecutable,
            *Arguments,
            &ReturnCode,
            &StandardOutput,
            &StandardError) ||
        ReturnCode != 0)
    {
        StandardError.TrimStartAndEndInline();
        StandardOutput.TrimStartAndEndInline();
        OutError = FString::Printf(
            TEXT("Managed reference planning failed with exit code %d: %s"),
            ReturnCode,
            *(StandardError.IsEmpty() ? StandardOutput : StandardError));
        return false;
    }
    if (!ParseManagedReferencePlan(
            PlanPath,
            Context.ManagedTypeName,
            ReplacementTypeName,
            OutPlan,
            OutError))
    {
        return false;
    }
    IFileManager::Get().Delete(*PlanPath, false, true);
    return true;
}

bool ParseNullableStringField(
    const TSharedPtr<FJsonObject>& Object,
    const TCHAR* FieldName,
    bool& bOutPresent,
    FString& OutValue,
    FString& OutError)
{
    bOutPresent = false;
    OutValue.Reset();
    const TSharedPtr<FJsonValue>* Value = Object->Values.Find(FieldName);
    if (Value == nullptr || !Value->IsValid())
    {
        OutError = FString::Printf(TEXT("Identity plan is missing '%s'."), FieldName);
        return false;
    }
    if ((*Value)->IsNull())
    {
        return true;
    }
    bOutPresent = true;
    if (!(*Value)->TryGetString(OutValue))
    {
        OutError = FString::Printf(TEXT("Identity plan field '%s' must be a string or null."), FieldName);
        return false;
    }
    return true;
}

bool ValidateManagedSidecarJson(
    const FString& SidecarJson,
    const FString& ExpectedSourcePath,
    const FString& TargetSource,
    FString& OutError)
{
    TSharedPtr<FJsonObject> Sidecar;
    const TSharedRef<TJsonReader<>> Reader = TJsonReaderFactory<>::Create(SidecarJson);
    if (!FJsonSerializer::Deserialize(Reader, Sidecar) || !Sidecar.IsValid())
    {
        OutError = TEXT("Identity plan returned malformed identitySidecarJson.");
        return false;
    }
    FString SchemaVersion;
    FString SourcePath;
    FString SourceDigest;
    if (!Sidecar->TryGetStringField(TEXT("schemaVersion"), SchemaVersion) ||
        SchemaVersion != TEXT("unifiedscript.identity/v0.1") ||
        !Sidecar->TryGetStringField(TEXT("sourcePath"), SourcePath) ||
        !FPaths::IsSamePath(SourcePath, ExpectedSourcePath) ||
        !Sidecar->TryGetStringField(TEXT("sourceDigest"), SourceDigest))
    {
        OutError = TEXT("Identity plan returned an invalid identity sidecar identity.");
        return false;
    }
    FTCHARToUTF8 Utf8(*TargetSource);
    TArray<uint8> TextBytes;
    TextBytes.Append(
        reinterpret_cast<const uint8*>(Utf8.Get()),
        Utf8.Length());
    FString ExpectedDigest;
    if (!ComputeSourceDigest(TextBytes, ExpectedDigest) || SourceDigest != ExpectedDigest)
    {
        OutError = TEXT("Identity plan sidecar sourceDigest does not match the final source text.");
        return false;
    }
    return true;
}

const TArray<uint8>* FindOutputBytes(
    const TMap<FString, TArray<uint8>>& Outputs,
    const FString& Path)
{
    for (const TPair<FString, TArray<uint8>>& Pair : Outputs)
    {
        if (FPaths::IsSamePath(Pair.Key, Path))
        {
            return &Pair.Value;
        }
    }
    return nullptr;
}

const FFileSnapshot* FindSnapshot(
    const TArray<FFileSnapshot>& Snapshots,
    const FString& Path);

FString GetManagedTransitionTargetPath(
    const FClassOperationContext& Context,
    const FTargetIdentity& Target,
    const FManagedReferenceDocument& Reference)
{
    if (!Reference.TargetPath.IsEmpty())
    {
        return Reference.TargetPath;
    }
    return FPaths::IsSamePath(Reference.Path, Context.SourcePath)
        ? Target.SourcePath
        : Reference.Path;
}

bool ParseManagedIdentityPlan(
    const FString& PlanPath,
    const TArray<FManagedIdentityTransition>& Transitions,
    const FClassOperationContext& Context,
    const FTargetIdentity& Target,
    const TMap<FString, TArray<uint8>>& SourceOutputs,
    const TArray<FFileSnapshot>& Snapshots,
    FManagedIdentityPlan& OutPlan,
    TMap<FString, TArray<uint8>>& OutSidecarOutputs,
    TArray<FString>& OutSidecarDeletes,
    FString& OutError)
{
    TSharedPtr<FJsonObject> Root;
    if (!ReadJsonFile(PlanPath, Root, OutError))
    {
        return false;
    }
    FString SchemaVersion;
    FString ProjectPath;
    FString SemanticConfigurationId;
    if (!Root->TryGetStringField(TEXT("schemaVersion"), SchemaVersion) ||
        SchemaVersion != TEXT("unifiedscript.csharp-source-identity-plan/v0.1") ||
        !Root->TryGetStringField(TEXT("projectPath"), ProjectPath) ||
        !FPaths::IsSamePath(ProjectPath, Context.ManagedProjectPath) ||
        !Root->TryGetStringField(TEXT("semanticConfigurationId"), SemanticConfigurationId) ||
        SemanticConfigurationId.IsEmpty())
    {
        OutError = TEXT("Managed identity plan identity does not match the Script Class transaction.");
        return false;
    }
    const TArray<TSharedPtr<FJsonValue>>* DocumentValues = nullptr;
    if (!Root->TryGetArrayField(TEXT("documents"), DocumentValues) ||
        DocumentValues == nullptr || DocumentValues->Num() != Transitions.Num())
    {
        OutError = TEXT("Managed identity plan document set does not match the reference plan.");
        return false;
    }

    TArray<bool> SeenTransitions;
    SeenTransitions.Init(false, Transitions.Num());
    FManagedIdentityPlan Parsed;
    Parsed.ProjectPath = ProjectPath;
    Parsed.SemanticConfigurationId = SemanticConfigurationId;
    for (const TSharedPtr<FJsonValue>& Value : *DocumentValues)
    {
        const TSharedPtr<FJsonObject> Object = Value.IsValid() ? Value->AsObject() : nullptr;
        FManagedIdentityDocument Document;
        FString SourcePath;
        FString TargetPath;
        FString SourceDigest;
        bool bPreserveIdentity = false;
        if (!Object.IsValid() ||
            !Object->TryGetStringField(TEXT("sourcePath"), SourcePath) ||
            !Object->TryGetStringField(TEXT("targetPath"), TargetPath) ||
            !Object->TryGetStringField(TEXT("sourceDigest"), SourceDigest) ||
            !Object->TryGetBoolField(TEXT("preserveIdentity"), bPreserveIdentity))
        {
            OutError = TEXT("Managed identity plan contains an invalid document.");
            return false;
        }
        FString NormalizedSourcePath = FPaths::ConvertRelativePathToFull(SourcePath);
        FString NormalizedTargetPath = FPaths::ConvertRelativePathToFull(TargetPath);
        FPaths::NormalizeFilename(NormalizedSourcePath);
        FPaths::NormalizeFilename(NormalizedTargetPath);
        int32 TransitionIndex = INDEX_NONE;
        for (int32 Index = 0; Index < Transitions.Num(); ++Index)
        {
            if (!SeenTransitions[Index] &&
                FPaths::IsSamePath(Transitions[Index].SourcePath, NormalizedSourcePath) &&
                FPaths::IsSamePath(Transitions[Index].TargetPath, NormalizedTargetPath) &&
                Transitions[Index].bPreserveIdentity == bPreserveIdentity)
            {
                TransitionIndex = Index;
                break;
            }
        }
        if (TransitionIndex == INDEX_NONE)
        {
            OutError = TEXT("Managed identity plan contains an unexpected, duplicate, or mismatched transition.");
            return false;
        }
        SeenTransitions[TransitionIndex] = true;
        Document.SourcePath = MoveTemp(NormalizedSourcePath);
        Document.TargetPath = MoveTemp(NormalizedTargetPath);
        Document.SourceDigest = SourceDigest;
        Document.bPreserveIdentity = bPreserveIdentity;
        if (!ParseNullableStringField(
                Object,
                TEXT("sourceSidecarDigest"),
                Document.bHasSourceSidecarDigest,
                Document.SourceSidecarDigest,
                OutError))
        {
            return false;
        }
        if (!Object->TryGetStringField(TEXT("targetSidecarPath"), Document.TargetSidecarPath) ||
            !FPaths::IsSamePath(
                Document.TargetSidecarPath,
                Document.TargetPath + TEXT(".usids")))
        {
            OutError = TEXT("Managed identity plan target sidecar path is invalid.");
            return false;
        }
        Document.TargetSidecarPath = FPaths::ConvertRelativePathToFull(Document.TargetSidecarPath);
        FString TargetSource;
        const TArray<uint8>* SourceBytes = FindOutputBytes(SourceOutputs, Document.TargetPath);
        if (SourceBytes == nullptr)
        {
            OutError = FString::Printf(TEXT("Managed identity plan source output is missing '%s'."), *Document.TargetPath);
            return false;
        }
        FFileHelper::BufferToString(
            TargetSource,
            SourceBytes->GetData(),
            SourceBytes->Num());
        if (!ParseNullableStringField(
                Object,
                TEXT("identitySidecarJson"),
                Document.bHasIdentitySidecarJson,
                Document.IdentitySidecarJson,
                OutError))
        {
            return false;
        }
        const FFileSnapshot* SourceSnapshot = FindSnapshot(Snapshots, Document.SourcePath);
        FString ExpectedSourceDigest;
        if (SourceSnapshot == nullptr || !ComputeSourceDigest(SourceSnapshot->Bytes, ExpectedSourceDigest) ||
            Document.SourceDigest != ExpectedSourceDigest)
        {
            OutError = FString::Printf(TEXT("Managed identity plan source digest is stale for '%s'."), *Document.SourcePath);
            return false;
        }
        const FString SourceSidecarPath = Document.SourcePath + TEXT(".usids");
        const FFileSnapshot* SourceSidecarSnapshot = FindSnapshot(Snapshots, SourceSidecarPath);
        const bool bSourceSidecarExisted = SourceSidecarSnapshot != nullptr && SourceSidecarSnapshot->bExisted;
        if (bSourceSidecarExisted != Document.bHasSourceSidecarDigest ||
            (bSourceSidecarExisted &&
                (!ComputeSourceDigest(SourceSidecarSnapshot->Bytes, ExpectedSourceDigest) ||
                    Document.SourceSidecarDigest != ExpectedSourceDigest)))
        {
            OutError = FString::Printf(TEXT("Managed identity plan sidecar digest is stale for '%s'."), *SourceSidecarPath);
            return false;
        }
        if (bSourceSidecarExisted != Document.bHasIdentitySidecarJson)
        {
            OutError = TEXT("Managed identity plan did not preserve the source sidecar presence contract.");
            return false;
        }
        if (Document.bHasIdentitySidecarJson &&
            !ValidateManagedSidecarJson(
                Document.IdentitySidecarJson,
                Document.TargetPath,
                TargetSource,
                OutError))
        {
            return false;
        }
        if (Document.bHasIdentitySidecarJson)
        {
            FTCHARToUTF8 Utf8(*Document.IdentitySidecarJson);
            TArray<uint8> SidecarBytes;
            SidecarBytes.Append(reinterpret_cast<const uint8*>(Utf8.Get()), Utf8.Length());
            OutSidecarOutputs.Add(Document.TargetSidecarPath, MoveTemp(SidecarBytes));
        }
        if (Document.bPreserveIdentity &&
            !FPaths::IsSamePath(Document.SourcePath, Document.TargetPath) &&
            bSourceSidecarExisted)
        {
            OutSidecarDeletes.AddUnique(SourceSidecarPath);
        }
        Parsed.Documents.Add(MoveTemp(Document));
    }
    for (const bool bSeen : SeenTransitions)
    {
        if (!bSeen)
        {
            OutError = TEXT("Managed identity plan is missing one or more expected transitions.");
            return false;
        }
    }
    OutPlan = MoveTemp(Parsed);
    return true;
}

bool RunManagedIdentityPlanner(
    const FClassOperationContext& Context,
    const FTargetIdentity& Target,
    const FManagedReferencePlan& ReferencePlan,
    const TMap<FString, TArray<uint8>>& SourceOutputs,
    const TArray<FFileSnapshot>& Snapshots,
    const FString& TransactionId,
    const bool bPreserveIdentity,
    FManagedIdentityPlan& OutPlan,
    TMap<FString, TArray<uint8>>& OutSidecarOutputs,
    TArray<FString>& OutSidecarDeletes,
    FString& OutError)
{
    TArray<FManagedIdentityTransition> Transitions;
    for (const FManagedReferenceDocument& Reference : ReferencePlan.Documents)
    {
        FManagedIdentityTransition Transition;
        Transition.SourcePath = Reference.Path;
        Transition.TargetPath = GetManagedTransitionTargetPath(Context, Target, Reference);
        Transition.bPreserveIdentity = bPreserveIdentity;
        Transitions.Add(MoveTemp(Transition));
    }
    OutSidecarOutputs.Reset();
    OutSidecarDeletes.Reset();
    const FString PlanDirectory = FPaths::Combine(
        Context.ProjectRoot,
        TEXT("Saved/UnifiedScript/ClassOperations"));
    IFileManager::Get().MakeDirectory(*PlanDirectory, true);
    const FString RequestPath = FPaths::Combine(PlanDirectory, TransactionId + TEXT(".identity-request.json"));
    const FString PlanPath = FPaths::Combine(PlanDirectory, TransactionId + TEXT(".identity-plan.json"));
    const TSharedRef<FJsonObject> Root = MakeShared<FJsonObject>();
    Root->SetStringField(TEXT("schemaVersion"), TEXT("unifiedscript.csharp-source-identity-request/v0.1"));
    TArray<TSharedPtr<FJsonValue>> Documents;
    for (const FManagedReferenceDocument& Reference : ReferencePlan.Documents)
    {
        const FString TargetPath = GetManagedTransitionTargetPath(Context, Target, Reference);
        const TArray<uint8>* SourceBytes = FindOutputBytes(SourceOutputs, TargetPath);
        const FFileSnapshot* SourceSnapshot = FindSnapshot(Snapshots, Reference.Path);
        if (SourceBytes == nullptr || SourceSnapshot == nullptr || !SourceSnapshot->bExisted)
        {
            OutError = FString::Printf(TEXT("Cannot build identity request for '%s'."), *Reference.Path);
            IFileManager::Get().Delete(*RequestPath, false, true);
            return false;
        }
        FString TargetSource;
        FFileHelper::BufferToString(TargetSource, SourceBytes->GetData(), SourceBytes->Num());
        FString SourceDigest;
        if (!ComputeSourceDigest(SourceSnapshot->Bytes, SourceDigest))
        {
            OutError = TEXT("Cannot hash managed identity request source.");
            IFileManager::Get().Delete(*RequestPath, false, true);
            return false;
        }
        const FString SourceSidecarPath = Reference.Path + TEXT(".usids");
        const FFileSnapshot* SidecarSnapshot = FindSnapshot(Snapshots, SourceSidecarPath);
        TSharedRef<FJsonObject> Document = MakeShared<FJsonObject>();
        Document->SetStringField(TEXT("sourcePath"), Reference.Path);
        Document->SetStringField(TEXT("targetPath"), TargetPath);
        Document->SetStringField(TEXT("sourceDigest"), SourceDigest);
        if (SidecarSnapshot != nullptr && SidecarSnapshot->bExisted)
        {
            FString SidecarDigest;
            if (!ComputeSourceDigest(SidecarSnapshot->Bytes, SidecarDigest))
            {
                OutError = TEXT("Cannot hash managed source sidecar.");
                IFileManager::Get().Delete(*RequestPath, false, true);
                return false;
            }
            Document->SetStringField(TEXT("sourceSidecarDigest"), SidecarDigest);
        }
        else
        {
            Document->SetField(TEXT("sourceSidecarDigest"), MakeShared<FJsonValueNull>());
        }
        Document->SetStringField(TEXT("targetSource"), TargetSource);
        Document->SetBoolField(TEXT("preserveIdentity"), bPreserveIdentity);
        Documents.Add(MakeShared<FJsonValueObject>(Document));
    }
    Root->SetArrayField(TEXT("documents"), Documents);
    FString RequestJson;
    if (!SerializeJson(Root, RequestJson, OutError) ||
        !WriteTextAtomically(RequestPath, RequestJson, OutError))
    {
        IFileManager::Get().Delete(*RequestPath, false, true);
        return false;
    }
    FString Arguments = FString::Printf(
        TEXT("script-class-identity-plan %s --request %s --out %s --configuration Debug"),
        *QuoteProcessArgument(Context.ManagedProjectPath),
        *QuoteProcessArgument(RequestPath),
        *QuoteProcessArgument(PlanPath));
    FString DotNetExecutable = FPaths::ConvertRelativePathToFull(FPaths::Combine(
        FPaths::EngineDir(), TEXT("Binaries/ThirdParty/DotNet/10.0/win-x64/dotnet.exe")));
    FPaths::NormalizeFilename(DotNetExecutable);
    if (IFileManager::Get().FileExists(*DotNetExecutable))
    {
        Arguments += TEXT(" --dotnet ") + QuoteProcessArgument(DotNetExecutable);
    }
    int32 ReturnCode = INDEX_NONE;
    FString StandardOutput;
    FString StandardError;
    const bool bExecuted = FPlatformProcess::ExecProcess(
        *Context.CliExecutable,
        *Arguments,
        &ReturnCode,
        &StandardOutput,
        &StandardError);
    IFileManager::Get().Delete(*RequestPath, false, true);
    if (!bExecuted || ReturnCode != 0)
    {
        StandardError.TrimStartAndEndInline();
        StandardOutput.TrimStartAndEndInline();
        IFileManager::Get().Delete(*PlanPath, false, true);
        OutError = FString::Printf(
            TEXT("Managed identity planning failed with exit code %d: %s"),
            ReturnCode,
            *(StandardError.IsEmpty() ? StandardOutput : StandardError));
        return false;
    }
    const bool bParsed = ParseManagedIdentityPlan(
        PlanPath,
        Transitions,
        Context,
        Target,
        SourceOutputs,
        Snapshots,
        OutPlan,
        OutSidecarOutputs,
        OutSidecarDeletes,
        OutError);
    IFileManager::Get().Delete(*PlanPath, false, true);
    return bParsed;
}

UUnifiedScriptAsset* GetBlueprintScriptAsset(UBlueprint* Blueprint)
{
    if (Blueprint == nullptr || Blueprint->GeneratedClass == nullptr)
    {
        return nullptr;
    }
    if (const UUnifiedScriptBlueprint* UnifiedBlueprint =
            Cast<UUnifiedScriptBlueprint>(Blueprint);
        UnifiedBlueprint != nullptr && UnifiedBlueprint->ScriptAsset != nullptr)
    {
        return UnifiedBlueprint->ScriptAsset.Get();
    }
    if (const AUnifiedScriptActor* Defaults = Cast<AUnifiedScriptActor>(
            Blueprint->GeneratedClass->GetDefaultObject()))
    {
        return Defaults->ScriptComponent != nullptr
            ? Defaults->ScriptComponent->ScriptAsset.Get()
            : nullptr;
    }
    if (const UUnifiedScriptComponent* Defaults = Cast<UUnifiedScriptComponent>(
            Blueprint->GeneratedClass->GetDefaultObject()))
    {
        return Defaults->ScriptAsset.Get();
    }
    return nullptr;
}

bool SetBlueprintScriptAsset(
    UBlueprint* Blueprint,
    UUnifiedScriptAsset* ScriptAsset,
    FString& OutError)
{
    if (Blueprint == nullptr || Blueprint->GeneratedClass == nullptr)
    {
        OutError = TEXT("The generated Blueprint proxy is unavailable.");
        return false;
    }
    UUnifiedScriptBlueprint* UnifiedBlueprint = Cast<UUnifiedScriptBlueprint>(Blueprint);
    if (UnifiedBlueprint == nullptr)
    {
        OutError = TEXT("The generated proxy is not a product-owned UnifiedScript Blueprint.");
        return false;
    }
    UnifiedBlueprint->Modify();
    UnifiedBlueprint->ScriptAsset = ScriptAsset;
    if (AUnifiedScriptActor* Defaults = Cast<AUnifiedScriptActor>(
            Blueprint->GeneratedClass->GetDefaultObject()))
    {
        if (Defaults->ScriptComponent == nullptr)
        {
            OutError = TEXT("The generated Actor proxy has no Script Component.");
            return false;
        }
        Defaults->ScriptComponent->Modify();
        Defaults->ScriptComponent->ScriptAsset = ScriptAsset;
        return true;
    }
    if (UUnifiedScriptComponent* Defaults = Cast<UUnifiedScriptComponent>(
            Blueprint->GeneratedClass->GetDefaultObject()))
    {
        Defaults->Modify();
        Defaults->ScriptAsset = ScriptAsset;
        return true;
    }
    OutError.Reset();
    return true;
}

bool BuildContext(
    UUnifiedScriptAsset* ScriptAsset,
    FClassOperationContext& OutContext,
    FString& OutError)
{
    if (!IsValid(ScriptAsset) || !ScriptAsset->ScriptClassId.IsValid() ||
        ScriptAsset->ScriptClassKind == EUnifiedScriptClassKind::None)
    {
        OutError = TEXT("The selected Script Asset is not a persistent Script Class.");
        return false;
    }

    UClass* GeneratedClass = ScriptAsset->GeneratedClassPath.TryLoadClass<UObject>();
    UBlueprint* Blueprint = GeneratedClass != nullptr
        ? Cast<UBlueprint>(GeneratedClass->ClassGeneratedBy)
        : nullptr;
    if (Blueprint == nullptr || GetBlueprintScriptAsset(Blueprint) != ScriptAsset)
    {
        OutError = TEXT("The selected Script Asset is not owned by its exact generated Blueprint proxy.");
        return false;
    }

    FString ManagedNamespace;
    FString OldTypeName;
    if (!ScriptAsset->ScriptTypeName.Split(
            TEXT("."),
            &ManagedNamespace,
            &OldTypeName,
            ESearchCase::CaseSensitive,
            ESearchDir::FromEnd) ||
        !IsManagedIdentifier(OldTypeName))
    {
        OutError = FString::Printf(
            TEXT("Managed type identity '%s' is invalid."),
            *ScriptAsset->ScriptTypeName);
        return false;
    }

    FClassOperationContext Context;
    Context.ScriptAsset = ScriptAsset;
    Context.Blueprint = Blueprint;
    Context.ProjectRoot = FPaths::ConvertRelativePathToFull(FPaths::ProjectDir());
    FPaths::NormalizeDirectoryName(Context.ProjectRoot);
    Context.FirstPlayRequestPath =
        FUnifiedScriptProjectSetup::GetFirstPlayRequestPath(Context.ProjectRoot);
    Context.SourcePath =
        FUnifiedScriptAuthoringSurfaceReader::ResolveConfiguredPath(ScriptAsset->SourcePath);
    Context.ConfiguredSourcePath = ScriptAsset->SourcePath;
    Context.BlueprintPackagePath = Blueprint->GetOutermost()->GetName();
    Context.ScriptAssetPackagePath = ScriptAsset->GetOutermost()->GetName();
    Context.OldTypeName = OldTypeName;
    Context.ManagedNamespace = ManagedNamespace;
    Context.ManagedTypeName = ScriptAsset->ScriptTypeName;
    Context.DeclarationAssetPath = ScriptAsset->DeclarationAssetPath;
    Context.ClassId = FString::Printf(
        TEXT("us_%s"),
        *ScriptAsset->ScriptClassId.ToString(EGuidFormats::Digits).ToLower());

    if (!FFileHelper::LoadFileToString(Context.Source, *Context.SourcePath))
    {
        OutError = FString::Printf(
            TEXT("Cannot read managed source '%s'."),
            *Context.SourcePath);
        return false;
    }

    const FString OnboardingPath = FPaths::Combine(
        Context.ProjectRoot,
        TEXT("Config/UnifiedScript.onboarding.json"));
    TSharedPtr<FJsonObject> Onboarding;
    if (!ReadJsonFile(OnboardingPath, Onboarding, OutError))
    {
        return false;
    }
    const TSharedPtr<FJsonObject>* Managed = nullptr;
    const TArray<TSharedPtr<FJsonValue>>* ManagedProjects = nullptr;
    if (!Onboarding->TryGetObjectField(TEXT("managed"), Managed) ||
        Managed == nullptr ||
        !(*Managed)->TryGetArrayField(TEXT("projects"), ManagedProjects) ||
        ManagedProjects == nullptr ||
        ManagedProjects->IsEmpty())
    {
        OutError = TEXT("UnifiedScript onboarding does not identify a managed project.");
        return false;
    }
    TArray<FString> ResolvedManagedProjects;
    for (const TSharedPtr<FJsonValue>& Value : *ManagedProjects)
    {
        FString ProjectPath;
        if (!Value.IsValid() || !Value->TryGetString(ProjectPath))
        {
            OutError = TEXT("UnifiedScript onboarding contains an invalid managed project path.");
            return false;
        }
        ResolvedManagedProjects.Add(ResolveRelativeTo(
            FPaths::GetPath(OnboardingPath),
            ProjectPath));
    }
    Context.ManagedProjectPath = ResolvedManagedProjects[0];
    for (const FString& ProjectPath : ResolvedManagedProjects)
    {
        if (FPaths::IsUnderDirectory(
                Context.SourcePath,
                FPaths::GetPath(ProjectPath)))
        {
            Context.ManagedProjectPath = ProjectPath;
            break;
        }
    }

    const TSharedPtr<IPlugin> Plugin =
        IPluginManager::Get().FindPlugin(TEXT("UEUnifiedScript"));
    const FString PluginRoot = Plugin.IsValid()
        ? Plugin->GetBaseDir()
        : FPaths::Combine(Context.ProjectRoot, TEXT("Plugins/UEUnifiedScript"));
    const FString CliCandidates[] = {
        FPaths::Combine(
            PluginRoot,
            TEXT("Tools/UnifiedScript.Cli/UnifiedScript.Cli.exe")),
        FPaths::Combine(
            Context.ProjectRoot,
            TEXT("../src/UnifiedScript.Cli/bin/Release/net10.0/UnifiedScript.Cli.exe")),
        FPaths::Combine(
            Context.ProjectRoot,
            TEXT("../src/UnifiedScript.Cli/bin/Debug/net10.0/UnifiedScript.Cli.exe")),
    };
    for (FString CliCandidate : CliCandidates)
    {
        CliCandidate = FPaths::ConvertRelativePathToFull(CliCandidate);
        FPaths::NormalizeFilename(CliCandidate);
        if (IFileManager::Get().FileExists(*CliCandidate))
        {
            Context.CliExecutable = MoveTemp(CliCandidate);
            break;
        }
    }
    if (Context.CliExecutable.IsEmpty())
    {
        OutError = TEXT("UnifiedScript CLI is unavailable; run Project Setup Repair before changing Script Class identity.");
        return false;
    }
    const TSharedPtr<FJsonObject>* Deployment = nullptr;
    FString Manifest;
    if (!Onboarding->TryGetObjectField(TEXT("deployment"), Deployment) ||
        Deployment == nullptr ||
        !(*Deployment)->TryGetStringField(TEXT("manifest"), Manifest))
    {
        OutError = TEXT("UnifiedScript onboarding does not identify the deployment manifest.");
        return false;
    }
    Context.DeploymentPath = ResolveRelativeTo(
        FPaths::GetPath(OnboardingPath),
        Manifest);
    if (!ReadJsonFile(Context.DeploymentPath, Context.DeploymentRoot, OutError) ||
        !FFileHelper::LoadFileToArray(
            Context.OriginalDeploymentBytes,
            *Context.DeploymentPath))
    {
        if (OutError.IsEmpty())
        {
            OutError = FString::Printf(
                TEXT("Cannot snapshot deployment manifest '%s'."),
                *Context.DeploymentPath);
        }
        return false;
    }

    OutContext = MoveTemp(Context);
    OutError.Reset();
    return true;
}

bool BuildTarget(
    const FClassOperationContext& Context,
    const FString& TargetBlueprintPackagePath,
    const FGuid& ClassId,
    FTargetIdentity& OutTarget,
    FString& OutError)
{
    FText PackageError;
    if (!FPackageName::IsValidLongPackageName(
            TargetBlueprintPackagePath,
            false,
            &PackageError))
    {
        OutError = PackageError.ToString();
        return false;
    }
    const FString TypeName = FPackageName::GetLongPackageAssetName(
        TargetBlueprintPackagePath);
    if (!IsManagedIdentifier(TypeName))
    {
        OutError = FString::Printf(
            TEXT("'%s' is not a valid managed class name."),
            *TypeName);
        return false;
    }

    const FString PackageDirectory =
        FPackageName::GetLongPackagePath(TargetBlueprintPackagePath);
    const FString ScriptAssetName = TEXT("SA_") + TypeName;
    const FString ScriptAssetPackagePath =
        PackageDirectory / ScriptAssetName;
    if (TargetBlueprintPackagePath == Context.BlueprintPackagePath ||
        FindPackage(nullptr, *TargetBlueprintPackagePath) != nullptr ||
        FPackageName::DoesPackageExist(TargetBlueprintPackagePath) ||
        FindPackage(nullptr, *ScriptAssetPackagePath) != nullptr ||
        FPackageName::DoesPackageExist(ScriptAssetPackagePath))
    {
        OutError = FString::Printf(
            TEXT("Target Script Class assets already exist at '%s'."),
            *TargetBlueprintPackagePath);
        return false;
    }

    FTargetIdentity Target;
    Target.TypeName = TypeName;
    Target.BlueprintPackagePath = TargetBlueprintPackagePath;
    Target.BlueprintPackageDirectory = PackageDirectory;
    Target.BlueprintAssetPath = FString::Printf(
        TEXT("%s.%s"),
        *TargetBlueprintPackagePath,
        *TypeName);
    Target.BlueprintClassPath = Target.BlueprintAssetPath + TEXT("_C");
    Target.ScriptAssetName = ScriptAssetName;
    Target.ScriptAssetPackagePath = ScriptAssetPackagePath;
    Target.ScriptAssetObjectPath = FString::Printf(
        TEXT("%s.%s"),
        *ScriptAssetPackagePath,
        *ScriptAssetName);
    Target.SourcePath = FPaths::Combine(
        FPaths::GetPath(Context.SourcePath),
        TypeName + TEXT(".cs"));
    Target.ConfiguredSourcePath = FPaths::GetPath(Context.ConfiguredSourcePath) /
        (TypeName + TEXT(".cs"));
    Target.ConfiguredSourcePath.ReplaceInline(TEXT("\\"), TEXT("/"));
    Target.ManagedTypeName = Context.ManagedNamespace + TEXT(".") + TypeName;
    Target.DeclarationAssetPath = FPaths::GetPath(Context.DeclarationAssetPath) /
        TypeName;
    Target.ClassId = FString::Printf(
        TEXT("us_%s"),
        *ClassId.ToString(EGuidFormats::Digits).ToLower());
    if (IFileManager::Get().FileExists(*Target.SourcePath))
    {
        OutError = FString::Printf(
            TEXT("Target managed source already exists: '%s'."),
            *Target.SourcePath);
        return false;
    }
    OutTarget = MoveTemp(Target);
    OutError.Reset();
    return true;
}

bool ReplaceUniqueCapture(
    FString& Source,
    const FRegexPattern& Pattern,
    int32 CaptureIndex,
    const FString& Expected,
    const FString& Replacement,
    const TCHAR* Description,
    FString& OutError)
{
    FRegexMatcher Matcher(Pattern, Source);
    if (!Matcher.FindNext() || Matcher.GetCaptureGroup(CaptureIndex) != Expected)
    {
        OutError = FString::Printf(
            TEXT("Cannot find the exact %s identity '%s' in managed source."),
            Description,
            *Expected);
        return false;
    }
    const int32 Begin = Matcher.GetCaptureGroupBeginning(CaptureIndex);
    const int32 End = Matcher.GetCaptureGroupEnding(CaptureIndex);
    if (Matcher.FindNext())
    {
        OutError = FString::Printf(
            TEXT("Managed source contains more than one %s identity."),
            Description);
        return false;
    }
    Source = Source.Left(Begin) + Replacement + Source.Mid(End);
    return true;
}

bool ReplaceOptionalUniqueCapture(
    FString& Source,
    const FRegexPattern& Pattern,
    int32 CaptureIndex,
    const FString& Expected,
    const FString& Replacement,
    const TCHAR* Description,
    FString& OutError)
{
    FRegexMatcher Matcher(Pattern, Source);
    if (!Matcher.FindNext())
    {
        return true;
    }
    if (Matcher.GetCaptureGroup(CaptureIndex) != Expected)
    {
        OutError = FString::Printf(
            TEXT("Managed source contains stale %s identity '%s'; expected '%s'."),
            Description,
            *Matcher.GetCaptureGroup(CaptureIndex),
            *Expected);
        return false;
    }
    const int32 Begin = Matcher.GetCaptureGroupBeginning(CaptureIndex);
    const int32 End = Matcher.GetCaptureGroupEnding(CaptureIndex);
    if (Matcher.FindNext())
    {
        OutError = FString::Printf(
            TEXT("Managed source contains more than one %s identity."),
            Description);
        return false;
    }
    Source = Source.Left(Begin) + Replacement + Source.Mid(End);
    return true;
}

bool TransformSourceText(
    const FClassOperationContext& Context,
    const FTargetIdentity& Target,
    FString Source,
    bool bReplaceClassDeclaration,
    FString& OutSource,
    FString& OutError)
{
    const FRegexPattern ScriptAssetPattern(TEXT(
        R"REGEX(\[\s*ScriptAsset\s*\(\s*"([^"]+)"\s*,\s*ClassId\s*=\s*"([^"]+)"\s*\)\s*\])REGEX"));
    if (!ReplaceUniqueCapture(
            Source,
            ScriptAssetPattern,
            2,
            Context.ClassId,
            Target.ClassId,
            TEXT("Script Class ID"),
            OutError) ||
        !ReplaceUniqueCapture(
            Source,
            ScriptAssetPattern,
            1,
            Context.DeclarationAssetPath,
            Target.DeclarationAssetPath,
            TEXT("declaration asset path"),
            OutError))
    {
        return false;
    }

    const FRegexPattern GeneratedClassPattern(TEXT(
        R"REGEX(\[\s*UnrealScriptClass\s*\([^)]*GeneratedClassPath\s*=\s*"([^"]+)"[^)]*\)\s*\])REGEX"));
    const FString ExistingBlueprintClassPath = FString::Printf(
        TEXT("%s.%s_C"),
        *Context.BlueprintPackagePath,
        *Context.OldTypeName);
    if (!ReplaceOptionalUniqueCapture(
            Source,
            GeneratedClassPattern,
            1,
            ExistingBlueprintClassPath,
            Target.BlueprintClassPath,
            TEXT("generated Blueprint class path"),
            OutError))
    {
        return false;
    }

    if (bReplaceClassDeclaration)
    {
        const FRegexPattern ClassPattern(FString::Printf(
            TEXT(R"(\bclass\s+(%s)\b)"),
            *Context.OldTypeName));
        if (!ReplaceUniqueCapture(
                Source,
                ClassPattern,
                1,
                Context.OldTypeName,
                Target.TypeName,
                TEXT("class declaration"),
                OutError))
        {
            return false;
        }
    }
    OutSource = MoveTemp(Source);
    OutError.Reset();
    return true;
}

bool TransformSource(
    const FClassOperationContext& Context,
    const FTargetIdentity& Target,
    FString& OutSource,
    FString& OutError)
{
    return TransformSourceText(
        Context,
        Target,
        Context.Source,
        true,
        OutSource,
        OutError);
}

TSharedPtr<FJsonObject> FindDeploymentEntry(
    const FClassOperationContext& Context,
    TArray<TSharedPtr<FJsonValue>>& OutScripts,
    FString& OutError)
{
    const TArray<TSharedPtr<FJsonValue>>* ExistingScripts = nullptr;
    if (!Context.DeploymentRoot->TryGetArrayField(TEXT("scripts"), ExistingScripts) ||
        ExistingScripts == nullptr)
    {
        OutError = TEXT("UnifiedScript deployment scripts are unavailable.");
        return nullptr;
    }
    OutScripts = *ExistingScripts;
    TSharedPtr<FJsonObject> Match;
    for (const TSharedPtr<FJsonValue>& Value : OutScripts)
    {
        const TSharedPtr<FJsonObject> Entry = Value->AsObject();
        FString AssetPath;
        FString TypeName;
        if (Entry.IsValid() &&
            Entry->TryGetStringField(TEXT("assetPath"), AssetPath) &&
            Entry->TryGetStringField(TEXT("typeName"), TypeName) &&
            AssetPath == Context.ScriptAssetPackagePath &&
            TypeName == Context.ManagedTypeName)
        {
            if (Match.IsValid())
            {
                OutError = TEXT("Deployment contains duplicate entries for the selected Script Class.");
                return nullptr;
            }
            Match = Entry;
        }
    }
    if (!Match.IsValid())
    {
        OutError = TEXT("Deployment does not contain the exact selected Script Class identity.");
    }
    return Match;
}

bool BuildDeployment(
    const FClassOperationContext& Context,
    const FTargetIdentity& Target,
    bool bDuplicate,
    FString& OutJson,
    FString& OutError)
{
    TArray<TSharedPtr<FJsonValue>> Scripts;
    const TSharedPtr<FJsonObject> Existing = FindDeploymentEntry(
        Context,
        Scripts,
        OutError);
    if (!Existing.IsValid())
    {
        return false;
    }

    TSharedPtr<FJsonObject> TargetEntry = Existing;
    if (bDuplicate)
    {
        TargetEntry = MakeShared<FJsonObject>();
        TargetEntry->Values = Existing->Values;
        Scripts.Add(MakeShared<FJsonValueObject>(TargetEntry));
        Context.DeploymentRoot->SetArrayField(TEXT("scripts"), Scripts);
    }
    TargetEntry->SetStringField(TEXT("assetPath"), Target.ScriptAssetPackagePath);
    TargetEntry->SetStringField(TEXT("typeName"), Target.ManagedTypeName);
    TargetEntry->SetStringField(
        TEXT("sourcePath"),
        Target.ConfiguredSourcePath);
    TargetEntry->SetStringField(
        TEXT("declarationAssetPath"),
        Target.DeclarationAssetPath);
    return SerializeJson(Context.DeploymentRoot.ToSharedRef(), OutJson, OutError);
}

bool BuildDeleteDeployment(
    const FClassOperationContext& Context,
    FString& OutJson,
    FString& OutError)
{
    TArray<TSharedPtr<FJsonValue>> Scripts;
    const TSharedPtr<FJsonObject> Existing = FindDeploymentEntry(
        Context,
        Scripts,
        OutError);
    if (!Existing.IsValid())
    {
        return false;
    }
    const int32 Removed = Scripts.RemoveAll(
        [&Existing](const TSharedPtr<FJsonValue>& Value)
        {
            return Value.IsValid() && Value->AsObject() == Existing;
        });
    if (Removed != 1)
    {
        OutError = TEXT("Deployment did not remove exactly one Script Class identity.");
        return false;
    }
    Context.DeploymentRoot->SetArrayField(TEXT("scripts"), Scripts);
    return SerializeJson(Context.DeploymentRoot.ToSharedRef(), OutJson, OutError);
}

FString GetBlueprintAssetPath(const FClassOperationContext& Context)
{
    return FString::Printf(
        TEXT("%s.%s"),
        *Context.BlueprintPackagePath,
        *Context.OldTypeName);
}

FString GetScriptAssetObjectPath(const FClassOperationContext& Context)
{
    return FString::Printf(
        TEXT("%s.%s"),
        *Context.ScriptAssetPackagePath,
        *FPackageName::GetLongPackageAssetName(Context.ScriptAssetPackagePath));
}

bool ShouldDeleteFirstPlayRequest(
    const FClassOperationContext& Context,
    bool& bOutDelete,
    FString& OutError)
{
    bOutDelete = false;
    if (!IFileManager::Get().FileExists(*Context.FirstPlayRequestPath))
    {
        return true;
    }
    FUnifiedScriptFirstPlayRequest Request;
    if (!FUnifiedScriptProjectSetup::ReadFirstPlayRequest(
            Context.FirstPlayRequestPath,
            Request,
            OutError))
    {
        OutError = TEXT("Cannot safely classify the existing first-Play request: ") +
            OutError;
        return false;
    }
    bOutDelete = Request.RequestId == Context.ClassId ||
        Request.BlueprintAssetPath == GetBlueprintAssetPath(Context) ||
        Request.ScriptAssetPath == GetScriptAssetObjectPath(Context) ||
        FPaths::IsSamePath(Request.SourcePath, Context.SourcePath) ||
        Request.ManagedTypeName == Context.ManagedTypeName;
    return true;
}

bool ValidateNoExternalAssetReferencers(
    const FClassOperationContext& Context,
    FString& OutError)
{
    IAssetRegistry& AssetRegistry =
        FModuleManager::LoadModuleChecked<FAssetRegistryModule>(TEXT("AssetRegistry")).Get();
    const FName InternalPackages[] = {
        FName(*Context.BlueprintPackagePath),
        FName(*Context.ScriptAssetPackagePath),
    };
    TArray<FString> ExternalReferencers;
    for (const FName Package : InternalPackages)
    {
        TArray<FName> Referencers;
        AssetRegistry.GetReferencers(
            Package,
            Referencers,
            UE::AssetRegistry::EDependencyCategory::Package);
        for (const FName Referencer : Referencers)
        {
            if (Referencer != InternalPackages[0] &&
                Referencer != InternalPackages[1])
            {
                ExternalReferencers.AddUnique(Referencer.ToString());
            }
        }
    }
    ExternalReferencers.Sort();
    if (!ExternalReferencers.IsEmpty())
    {
        OutError = FString::Printf(
            TEXT("Cannot delete Script Class '%s'; UE assets still reference it: %s"),
            *Context.ManagedTypeName,
            *FString::Join(ExternalReferencers, TEXT(", ")));
        return false;
    }
    return true;
}

bool CollectExternalAssetReferencers(
    const FClassOperationContext& Context,
    TArray<FName>& OutReferencers,
    FString& OutError)
{
    IAssetRegistry& AssetRegistry =
        FModuleManager::LoadModuleChecked<FAssetRegistryModule>(TEXT("AssetRegistry")).Get();
    const FName InternalPackages[] = {
        FName(*Context.BlueprintPackagePath),
        FName(*Context.ScriptAssetPackagePath),
    };
    TArray<FName> Referencers;
    for (const FName Package : InternalPackages)
    {
        TArray<FName> PackageReferencers;
        AssetRegistry.GetReferencers(
            Package,
            PackageReferencers,
            UE::AssetRegistry::EDependencyCategory::Package);
        for (const FName Referencer : PackageReferencers)
        {
            if (Referencer != InternalPackages[0] &&
                Referencer != InternalPackages[1])
            {
                Referencers.AddUnique(Referencer);
            }
        }
    }
    Referencers.Sort(FNameLexicalLess());
    for (const FName Referencer : Referencers)
    {
        if (const UPackage* LoadedPackage = FindPackage(
                nullptr,
                *Referencer.ToString());
            LoadedPackage != nullptr && LoadedPackage->IsDirty())
        {
            OutError = FString::Printf(
                TEXT("Cannot rename Script Class '%s'; UE referencer '%s' has unsaved changes. Save or revert that package before retrying."),
                *Context.ManagedTypeName,
                *Referencer.ToString());
            return false;
        }
    }
    OutReferencers = MoveTemp(Referencers);
    return true;
}

bool BuildFirstPlayRequest(
    const FClassOperationContext& Context,
    const FTargetIdentity& Target,
    FString& OutJson,
    FString& OutError)
{
    if (Context.ScriptAsset->ScriptClassKind != EUnifiedScriptClassKind::Actor &&
        Context.ScriptAsset->ScriptClassKind != EUnifiedScriptClassKind::ActorComponent)
    {
        OutJson.Reset();
        OutError.Reset();
        return true;
    }
    const TSharedRef<FJsonObject> Root = MakeShared<FJsonObject>();
    Root->SetStringField(
        TEXT("schemaVersion"),
        TEXT("unifiedscript.first-play-request/v0.1"));
    Root->SetStringField(TEXT("requestId"), Target.ClassId);
    Root->SetStringField(TEXT("status"), TEXT("buildRequired"));
    Root->SetStringField(
        TEXT("classKind"),
        Context.ScriptAsset->ScriptClassKind == EUnifiedScriptClassKind::Actor
            ? TEXT("actor")
            : TEXT("actorComponent"));
    Root->SetStringField(TEXT("blueprintAssetPath"), Target.BlueprintAssetPath);
    Root->SetStringField(TEXT("scriptAssetPath"), Target.ScriptAssetObjectPath);
    Root->SetStringField(
        TEXT("sourcePath"),
        FPaths::ConvertRelativePathToFull(Target.SourcePath));
    Root->SetStringField(TEXT("managedTypeName"), Target.ManagedTypeName);
    return SerializeJson(Root, OutJson, OutError);
}

bool CaptureExternalSnapshots(
    const FClassOperationContext& Context,
    const FTargetIdentity& Target,
    const TArray<FString>& AdditionalPaths,
    TArray<FFileSnapshot>& OutSnapshots,
    FString& OutError)
{
    TArray<FString> Paths = {
        Context.SourcePath,
        Target.SourcePath,
        Context.DeploymentPath,
        Context.FirstPlayRequestPath,
    };
    Paths.Append(AdditionalPaths);
    TArray<FFileSnapshot> Snapshots;
    for (const FString& Path : Paths)
    {
        if (Snapshots.ContainsByPredicate(
                [&Path](const FFileSnapshot& Item) { return Item.Path == Path; }))
        {
            continue;
        }
        FFileSnapshot Snapshot;
        if (!CaptureFile(Path, Snapshot, OutError))
        {
            return false;
        }
        Snapshots.Add(MoveTemp(Snapshot));
    }
    OutSnapshots = MoveTemp(Snapshots);
    return true;
}

bool CaptureSnapshotIfMissing(
    const FString& Path,
    TArray<FFileSnapshot>& Snapshots,
    FString& OutError)
{
    if (Snapshots.ContainsByPredicate(
            [&Path](const FFileSnapshot& Item)
            {
                return FPaths::IsSamePath(Item.Path, Path);
            }))
    {
        return true;
    }
    FFileSnapshot Snapshot;
    if (!CaptureFile(Path, Snapshot, OutError))
    {
        return false;
    }
    Snapshots.Add(MoveTemp(Snapshot));
    return true;
}

void AddPackageFileCandidates(
    const FString& PackagePath,
    TArray<FString>& OutPaths)
{
    const FString AssetPath = FPaths::ConvertRelativePathToFull(
        FPackageName::LongPackageNameToFilename(
            PackagePath,
            FPackageName::GetAssetPackageExtension()));
    OutPaths.AddUnique(AssetPath);
    OutPaths.AddUnique(FPaths::ChangeExtension(AssetPath, TEXT("uexp")));
    OutPaths.AddUnique(FPaths::ChangeExtension(AssetPath, TEXT("ubulk")));
    OutPaths.AddUnique(FPaths::ChangeExtension(AssetPath, TEXT("uptnl")));
}

bool BuildRecoverySnapshots(
    const FClassOperationContext& Context,
    const FTargetIdentity& Target,
    const TArray<FFileSnapshot>& ExternalSnapshots,
    const TArray<FName>& AdditionalPackageNames,
    TArray<FFileSnapshot>& OutSnapshots,
    FString& OutError)
{
    TArray<FFileSnapshot> Snapshots = ExternalSnapshots;
    TArray<FString> PackagePaths;
    AddPackageFileCandidates(Context.BlueprintPackagePath, PackagePaths);
    AddPackageFileCandidates(Context.ScriptAssetPackagePath, PackagePaths);
    AddPackageFileCandidates(Target.BlueprintPackagePath, PackagePaths);
    AddPackageFileCandidates(Target.ScriptAssetPackagePath, PackagePaths);
    for (const FName PackageName : AdditionalPackageNames)
    {
        AddPackageFileCandidates(PackageName.ToString(), PackagePaths);
    }
    for (const FString& Path : PackagePaths)
    {
        if (!CaptureSnapshotIfMissing(Path, Snapshots, OutError))
        {
            return false;
        }
    }
    OutSnapshots = MoveTemp(Snapshots);
    return true;
}

bool FixupRedirectorsAndVerify(
    const TArray<FString>& RedirectorObjectPaths,
    const TArray<FName>& ExpectedReferencerPackages,
    const TMap<FSoftObjectPath, FSoftObjectPath>& SoftPathRedirects,
    TArray<FString>& OutRepairedPackages,
    FString& OutError)
{
    IAssetTools& AssetTools = FAssetToolsModule::GetModule().Get();
    TArray<UPackage*> ReferencerPackages;
    TArray<FString> ReferencerFiles;
    for (const FName PackageName : ExpectedReferencerPackages)
    {
        UPackage* Package = LoadPackage(nullptr, *PackageName.ToString(), LOAD_None);
        if (Package == nullptr)
        {
            OutError = FString::Printf(
                TEXT("Cannot load UE referencer package '%s' for repair."),
                *PackageName.ToString());
            return false;
        }
        ReferencerPackages.Add(Package);
    }
    if (!ReferencerPackages.IsEmpty() && !SoftPathRedirects.IsEmpty())
    {
        AssetTools.RenameReferencingSoftObjectPaths(
            ReferencerPackages,
            SoftPathRedirects);
        FSavePackageArgs SaveArguments;
        SaveArguments.TopLevelFlags = RF_Public | RF_Standalone;
        SaveArguments.SaveFlags = SAVE_None;
        for (UPackage* Package : ReferencerPackages)
        {
            const FString Filename = FPackageName::LongPackageNameToFilename(
                Package->GetName(),
                FPackageName::GetAssetPackageExtension());
            if (!UPackage::SavePackage(
                    Package,
                    nullptr,
                    *Filename,
                    SaveArguments))
            {
                OutError = FString::Printf(
                    TEXT("Cannot save repaired UE referencer package '%s'."),
                    *Package->GetName());
                return false;
            }
            ReferencerFiles.Add(Filename);
        }
    }

    TArray<UObjectRedirector*> Redirectors;
    TArray<FName> RedirectorPackages;
    for (const FString& ObjectPath : RedirectorObjectPaths)
    {
        RedirectorPackages.AddUnique(FName(*FPackageName::ObjectPathToPackageName(ObjectPath)));
        if (UObjectRedirector* Redirector = LoadObject<UObjectRedirector>(
                nullptr,
                *ObjectPath,
                nullptr,
                LOAD_NoWarn | LOAD_Quiet))
        {
            Redirectors.AddUnique(Redirector);
        }
    }

    if (!Redirectors.IsEmpty())
    {
        FScopedAssetToolsMutation Mutation;
        AssetTools.FixupReferencers(
            Redirectors,
            false,
            ERedirectFixupMode::DeleteFixedUpRedirectors);
        if (AssetTools.IsFixupReferencersInProgress())
        {
            OutError = TEXT("UE redirector fixup did not complete synchronously.");
            return false;
        }
    }

    IAssetRegistry& AssetRegistry =
        FModuleManager::LoadModuleChecked<FAssetRegistryModule>(TEXT("AssetRegistry")).Get();
    if (!ReferencerFiles.IsEmpty())
    {
        AssetRegistry.ScanModifiedAssetFiles(ReferencerFiles);
    }
    TArray<FString> Failures;
    for (const FName RedirectorPackage : RedirectorPackages)
    {
        const FString Filename = FPackageName::LongPackageNameToFilename(
            RedirectorPackage.ToString(),
            FPackageName::GetAssetPackageExtension());
        if (IFileManager::Get().FileExists(*Filename))
        {
            Failures.Add(FString::Printf(
                TEXT("redirector package remains at '%s'"),
                *RedirectorPackage.ToString()));
        }
        TArray<FName> RemainingReferencers;
        AssetRegistry.GetReferencers(
            RedirectorPackage,
            RemainingReferencers,
            UE::AssetRegistry::EDependencyCategory::Package);
        if (!RemainingReferencers.IsEmpty())
        {
            TArray<FString> Names;
            for (const FName Referencer : RemainingReferencers)
            {
                Names.Add(Referencer.ToString());
            }
            Names.Sort();
            Failures.Add(FString::Printf(
                TEXT("'%s' still has referencers: %s"),
                *RedirectorPackage.ToString(),
                *FString::Join(Names, TEXT(", "))));
        }
    }
    for (const FName ReferencerPackage : ExpectedReferencerPackages)
    {
        TArray<FName> Dependencies;
        AssetRegistry.GetDependencies(
            ReferencerPackage,
            Dependencies,
            UE::AssetRegistry::EDependencyCategory::Package);
        for (const FName RedirectorPackage : RedirectorPackages)
        {
            if (Dependencies.Contains(RedirectorPackage))
            {
                Failures.Add(FString::Printf(
                    TEXT("referencer '%s' still depends on '%s'"),
                    *ReferencerPackage.ToString(),
                    *RedirectorPackage.ToString()));
            }
        }
    }
    if (!Failures.IsEmpty())
    {
        OutError = TEXT("UE reference repair failed: ") +
            FString::Join(Failures, TEXT("; "));
        return false;
    }

    TArray<FString> RepairedPackages;
    for (const FName Package : ExpectedReferencerPackages)
    {
        RepairedPackages.Add(Package.ToString());
    }
    RepairedPackages.Sort();
    OutRepairedPackages = MoveTemp(RepairedPackages);
    return true;
}

TMap<FSoftObjectPath, FSoftObjectPath> BuildRenameSoftPathRedirects(
    const FClassOperationContext& Context,
    const FTargetIdentity& Target,
    bool bReverse)
{
    const FSoftObjectPath OriginalBlueprint(GetBlueprintAssetPath(Context));
    const FSoftObjectPath OriginalClass(FString::Printf(
        TEXT("%s.%s_C"),
        *Context.BlueprintPackagePath,
        *Context.OldTypeName));
    const FSoftObjectPath OriginalScriptAsset(GetScriptAssetObjectPath(Context));
    const FSoftObjectPath TargetBlueprint(Target.BlueprintAssetPath);
    const FSoftObjectPath TargetClass(Target.BlueprintClassPath);
    const FSoftObjectPath TargetScriptAsset(Target.ScriptAssetObjectPath);
    TMap<FSoftObjectPath, FSoftObjectPath> Redirects;
    if (bReverse)
    {
        Redirects.Add(TargetBlueprint, OriginalBlueprint);
        Redirects.Add(TargetClass, OriginalClass);
        Redirects.Add(TargetScriptAsset, OriginalScriptAsset);
    }
    else
    {
        Redirects.Add(OriginalBlueprint, TargetBlueprint);
        Redirects.Add(OriginalClass, TargetClass);
        Redirects.Add(OriginalScriptAsset, TargetScriptAsset);
    }
    return Redirects;
}

const FFileSnapshot* FindSnapshot(
    const TArray<FFileSnapshot>& Snapshots,
    const FString& Path)
{
    return Snapshots.FindByPredicate(
        [&Path](const FFileSnapshot& Snapshot)
        {
            return FPaths::IsSamePath(Snapshot.Path, Path);
        });
}

bool EncodeTextLikeSnapshot(
    const FString& Text,
    const FFileSnapshot& Snapshot,
    TArray<uint8>& OutBytes,
    FString& OutError)
{
    OutBytes.Reset();
    const TArray<uint8>& Original = Snapshot.Bytes;
    if (Original.Num() >= 2 && Original[0] == 0xff && Original[1] == 0xfe)
    {
        OutBytes.Add(0xff);
        OutBytes.Add(0xfe);
        for (const TCHAR Character : Text)
        {
            OutBytes.Add(static_cast<uint8>(Character & 0xff));
            OutBytes.Add(static_cast<uint8>((Character >> 8) & 0xff));
        }
        return true;
    }
    if (Original.Num() >= 2 && Original[0] == 0xfe && Original[1] == 0xff)
    {
        OutBytes.Add(0xfe);
        OutBytes.Add(0xff);
        for (const TCHAR Character : Text)
        {
            OutBytes.Add(static_cast<uint8>((Character >> 8) & 0xff));
            OutBytes.Add(static_cast<uint8>(Character & 0xff));
        }
        return true;
    }
    const bool bUtf8Bom = Original.Num() >= 3 &&
        Original[0] == 0xef && Original[1] == 0xbb && Original[2] == 0xbf;
    if (bUtf8Bom)
    {
        OutBytes.Append({0xef, 0xbb, 0xbf});
    }
    FTCHARToUTF8 Utf8(*Text);
    OutBytes.Append(
        reinterpret_cast<const uint8*>(Utf8.Get()),
        Utf8.Length());
    if (OutBytes.IsEmpty() && !Text.IsEmpty())
    {
        OutError = FString::Printf(
            TEXT("Cannot encode managed source '%s'."),
            *Snapshot.Path);
        return false;
    }
    return true;
}

bool ValidateManagedReferencePlanSnapshots(
    const FManagedReferencePlan& Plan,
    const TArray<FFileSnapshot>& Snapshots,
    FString& OutError)
{
    for (const FManagedReferenceDocument& Document : Plan.Documents)
    {
        const FFileSnapshot* Snapshot = FindSnapshot(Snapshots, Document.Path);
        FString Digest;
        if (Snapshot == nullptr || !Snapshot->bExisted ||
            !ComputeSourceDigest(Snapshot->Bytes, Digest) ||
            Digest != Document.SourceDigest)
        {
            OutError = FString::Printf(
                TEXT("Managed reference plan is stale for '%s'."),
                *Document.Path);
            return false;
        }
    }
    return true;
}

bool BuildManagedRenameOutputs(
    const FClassOperationContext& Context,
    const FTargetIdentity& Target,
    const FManagedReferencePlan& Plan,
    const TArray<FFileSnapshot>& Snapshots,
    TMap<FString, TArray<uint8>>& OutOutputs,
    FString& OutError)
{
    TMap<FString, TArray<uint8>> Outputs;
    bool bFoundPrimarySource = false;
    for (const FManagedReferenceDocument& Document : Plan.Documents)
    {
        const FFileSnapshot* Snapshot = FindSnapshot(Snapshots, Document.Path);
        if (Snapshot == nullptr)
        {
            OutError = FString::Printf(
                TEXT("Managed reference source '%s' was not snapshotted."),
                *Document.Path);
            return false;
        }
        FString Source;
        FFileHelper::BufferToString(
            Source,
            Snapshot->Bytes.GetData(),
            Snapshot->Bytes.Num());
        for (int32 EditIndex = Document.Edits.Num() - 1; EditIndex >= 0; --EditIndex)
        {
            const FManagedReferenceEdit& Edit = Document.Edits[EditIndex];
            if (Edit.Start < 0 || Edit.Length <= 0 ||
                Edit.Start + Edit.Length > Source.Len() ||
                Source.Mid(Edit.Start, Edit.Length) != Edit.OriginalText ||
                Edit.Replacement != Target.TypeName)
            {
                OutError = FString::Printf(
                    TEXT("Managed reference edit is stale at %s(%d,%d)."),
                    *Document.Path,
                    Edit.Line,
                    Edit.Column);
                return false;
            }
            Source = Source.Left(Edit.Start) +
                Edit.Replacement +
                Source.Mid(Edit.Start + Edit.Length);
        }

        FString OutputPath = Document.Path;
        if (FPaths::IsSamePath(Document.Path, Context.SourcePath))
        {
            bFoundPrimarySource = true;
            OutputPath = Target.SourcePath;
            FString IdentityTransformed;
            if (!TransformSourceText(
                    Context,
                    Target,
                    MoveTemp(Source),
                    false,
                    IdentityTransformed,
                    OutError))
            {
                return false;
            }
            Source = MoveTemp(IdentityTransformed);
        }
        TArray<uint8> Bytes;
        if (!EncodeTextLikeSnapshot(Source, *Snapshot, Bytes, OutError))
        {
            return false;
        }
        if (Outputs.Contains(OutputPath))
        {
            OutError = FString::Printf(
                TEXT("Managed reference plan writes '%s' more than once."),
                *OutputPath);
            return false;
        }
        Outputs.Add(OutputPath, MoveTemp(Bytes));
    }
    if (!bFoundPrimarySource)
    {
        OutError = TEXT("Managed reference plan did not include the Script Class declaration source.");
        return false;
    }
    OutOutputs = MoveTemp(Outputs);
    return true;
}

bool BuildManagedDuplicateOutputs(
    const FClassOperationContext& Context,
    const FTargetIdentity& Target,
    FManagedReferencePlan& InOutPlan,
    const TArray<FFileSnapshot>& Snapshots,
    TMap<FString, TArray<uint8>>& OutOutputs,
    FString& OutError)
{
    TArray<FManagedReferenceDocument> DeclarationDocuments;
    for (const FManagedReferenceDocument& Document : InOutPlan.Documents)
    {
        if (Document.Edits.ContainsByPredicate(
                [](const FManagedReferenceEdit& Edit)
                {
                    return Edit.Kind == TEXT("typeDeclaration");
                }))
        {
            DeclarationDocuments.Add(Document);
        }
    }
    if (DeclarationDocuments.IsEmpty())
    {
        OutError = TEXT("Managed duplicate plan contains no selected TypeDeclaration source.");
        return false;
    }
    DeclarationDocuments.Sort(
        [](const FManagedReferenceDocument& Left, const FManagedReferenceDocument& Right)
        {
            return Left.Path < Right.Path;
        });

    TMap<FString, TArray<uint8>> Outputs;
    int32 PartIndex = 1;
    bool bFoundPrimary = false;
    TArray<FManagedReferenceDocument> FilteredPlan;
    for (FManagedReferenceDocument Document : DeclarationDocuments)
    {
        const FFileSnapshot* Snapshot = FindSnapshot(Snapshots, Document.Path);
        if (Snapshot == nullptr || !Snapshot->bExisted)
        {
            OutError = FString::Printf(
                TEXT("Managed duplicate source '%s' was not snapshotted."),
                *Document.Path);
            return false;
        }
        FString Source;
        FFileHelper::BufferToString(
            Source,
            Snapshot->Bytes.GetData(),
            Snapshot->Bytes.Num());
        for (int32 EditIndex = Document.Edits.Num() - 1; EditIndex >= 0; --EditIndex)
        {
            const FManagedReferenceEdit& Edit = Document.Edits[EditIndex];
            if (Edit.Start < 0 || Edit.Length <= 0 ||
                Edit.Start + Edit.Length > Source.Len() ||
                Source.Mid(Edit.Start, Edit.Length) != Edit.OriginalText ||
                Edit.Replacement != Target.TypeName)
            {
                OutError = FString::Printf(
                    TEXT("Managed duplicate edit is stale at %s(%d,%d)."),
                    *Document.Path,
                    Edit.Line,
                    Edit.Column);
                return false;
            }
            Source = Source.Left(Edit.Start) +
                Edit.Replacement +
                Source.Mid(Edit.Start + Edit.Length);
        }

        FString OutputPath;
        if (FPaths::IsSamePath(Document.Path, Context.SourcePath))
        {
            OutputPath = Target.SourcePath;
            FString TransformedSource;
            if (!TransformSourceText(
                    Context,
                    Target,
                    MoveTemp(Source),
                    false,
                    TransformedSource,
                    OutError))
            {
                return false;
            }
            Source = MoveTemp(TransformedSource);
            bFoundPrimary = true;
        }
        else
        {
            OutputPath = FPaths::Combine(
                FPaths::GetPath(Target.SourcePath),
                FString::Printf(TEXT("%s.part%d.cs"), *Target.TypeName, PartIndex++));
        }
        FPaths::NormalizeFilename(OutputPath);
        if (Outputs.Contains(OutputPath))
        {
            OutError = FString::Printf(
                TEXT("Managed duplicate plan writes '%s' more than once."),
                *OutputPath);
            return false;
        }
        TArray<uint8> Bytes;
        if (!EncodeTextLikeSnapshot(Source, *Snapshot, Bytes, OutError))
        {
            return false;
        }
        Outputs.Add(OutputPath, MoveTemp(Bytes));
        Document.TargetPath = OutputPath;
        FilteredPlan.Add(MoveTemp(Document));
    }
    if (!bFoundPrimary)
    {
        OutError = TEXT("Managed duplicate plan did not include the selected source declaration.");
        return false;
    }
    InOutPlan.Documents = MoveTemp(FilteredPlan);
    OutOutputs = MoveTemp(Outputs);
    return true;
}

bool ValidateDeleteManagedReferencePlan(
    const FClassOperationContext& Context,
    const FManagedReferencePlan& Plan,
    FString& OutError)
{
    TArray<FString> ExternalReferences;
    for (const FManagedReferenceDocument& Document : Plan.Documents)
    {
        if (FPaths::IsSamePath(Document.Path, Context.SourcePath))
        {
            continue;
        }
        for (const FManagedReferenceEdit& Edit : Document.Edits)
        {
            ExternalReferences.Add(FString::Printf(
                TEXT("%s(%d,%d)"),
                *Document.Path,
                Edit.Line,
                Edit.Column));
        }
    }
    if (!ExternalReferences.IsEmpty())
    {
        OutError = FString::Printf(
            TEXT("Cannot delete Script Class '%s'; managed semantic references remain: %s"),
            *Context.ManagedTypeName,
            *FString::Join(ExternalReferences, TEXT(", ")));
        return false;
    }
    return true;
}

bool CaptureDeleteExternalSnapshots(
    const FClassOperationContext& Context,
    const TArray<FString>& AdditionalPaths,
    TArray<FFileSnapshot>& OutSnapshots,
    FString& OutError)
{
    TArray<FString> Paths = {
        Context.SourcePath,
        Context.SourcePath + TEXT(".usids"),
        Context.DeploymentPath,
        Context.FirstPlayRequestPath,
    };
    Paths.Append(AdditionalPaths);
    TArray<FFileSnapshot> Snapshots;
    for (const FString& Path : Paths)
    {
        if (Snapshots.ContainsByPredicate(
                [&Path](const FFileSnapshot& Item)
                {
                    return FPaths::IsSamePath(Item.Path, Path);
                }))
        {
            continue;
        }
        FFileSnapshot Snapshot;
        if (!CaptureFile(Path, Snapshot, OutError))
        {
            return false;
        }
        Snapshots.Add(MoveTemp(Snapshot));
    }
    OutSnapshots = MoveTemp(Snapshots);
    return true;
}

bool PublishExternalFiles(
    const FClassOperationContext& Context,
    const FTargetIdentity& Target,
    const TMap<FString, TArray<uint8>>& SourceOutputs,
    const TMap<FString, TArray<uint8>>& SidecarOutputs,
    const FString& Deployment,
    const FString& FirstPlayRequest,
    bool bRename,
    FString& OutError)
{
    TArray<FString> SourcePaths;
    SourceOutputs.GetKeys(SourcePaths);
    SourcePaths.Sort();
    for (const FString& SourcePath : SourcePaths)
    {
        if (!WriteBytesAtomically(
                SourcePath,
                SourceOutputs.FindChecked(SourcePath),
                OutError))
        {
            return false;
        }
    }
    TArray<FString> SidecarPaths;
    SidecarOutputs.GetKeys(SidecarPaths);
    SidecarPaths.Sort();
    for (const FString& SidecarPath : SidecarPaths)
    {
        if (!WriteBytesAtomically(
                SidecarPath,
                SidecarOutputs.FindChecked(SidecarPath),
                OutError))
        {
            return false;
        }
    }
    if (!WriteTextAtomically(Context.DeploymentPath, Deployment, OutError) ||
        (!FirstPlayRequest.IsEmpty() && !WriteTextAtomically(
            Context.FirstPlayRequestPath,
            FirstPlayRequest,
            OutError)))
    {
        return false;
    }
    if (bRename && Context.SourcePath != Target.SourcePath &&
        !IFileManager::Get().Delete(*Context.SourcePath, false, true))
    {
        OutError = FString::Printf(
            TEXT("Cannot remove renamed source '%s'."),
            *Context.SourcePath);
        return false;
    }
    return true;
}

bool PublishManagedRenameExternalFiles(
    const FClassOperationContext& Context,
    const FTargetIdentity& Target,
    const TMap<FString, TArray<uint8>>& SourceOutputs,
    const TMap<FString, TArray<uint8>>& SidecarOutputs,
    const TArray<FString>& SidecarDeletes,
    const FString& Deployment,
    const FString& FirstPlayRequest,
    FString& OutError)
{
    TArray<FString> OutputPaths;
    SourceOutputs.GetKeys(OutputPaths);
    OutputPaths.Sort();
    for (const FString& OutputPath : OutputPaths)
    {
        if (!WriteBytesAtomically(
                OutputPath,
                SourceOutputs.FindChecked(OutputPath),
                OutError))
        {
            return false;
        }
    }
    TArray<FString> SidecarOutputPaths;
    SidecarOutputs.GetKeys(SidecarOutputPaths);
    SidecarOutputPaths.Sort();
    for (const FString& OutputPath : SidecarOutputPaths)
    {
        if (!WriteBytesAtomically(
                OutputPath,
                SidecarOutputs.FindChecked(OutputPath),
                OutError))
        {
            return false;
        }
    }
    if (!WriteTextAtomically(Context.DeploymentPath, Deployment, OutError) ||
        (!FirstPlayRequest.IsEmpty() && !WriteTextAtomically(
            Context.FirstPlayRequestPath,
            FirstPlayRequest,
            OutError)))
    {
        return false;
    }
    for (const FString& SidecarPath : SidecarDeletes)
    {
        if (IFileManager::Get().FileExists(*SidecarPath) &&
            !IFileManager::Get().Delete(*SidecarPath, false, true))
        {
            OutError = FString::Printf(
                TEXT("Cannot remove renamed identity sidecar '%s'."),
                *SidecarPath);
            return false;
        }
    }
    if (Context.SourcePath != Target.SourcePath &&
        IFileManager::Get().FileExists(*Context.SourcePath) &&
        !IFileManager::Get().Delete(*Context.SourcePath, false, true))
    {
        OutError = FString::Printf(
            TEXT("Cannot remove renamed source '%s'."),
            *Context.SourcePath);
        return false;
    }
    return true;
}

bool PublishDeleteExternalFiles(
    const FClassOperationContext& Context,
    const FString& Deployment,
    bool bDeleteFirstPlayRequest,
    FString& OutError)
{
    if (!WriteTextAtomically(Context.DeploymentPath, Deployment, OutError))
    {
        return false;
    }
    if (IFileManager::Get().FileExists(*Context.SourcePath) &&
        !IFileManager::Get().Delete(*Context.SourcePath, false, true))
    {
        OutError = FString::Printf(
            TEXT("Cannot remove deleted Script Class source '%s'."),
            *Context.SourcePath);
        return false;
    }
    const FString SidecarPath = Context.SourcePath + TEXT(".usids");
    if (IFileManager::Get().FileExists(*SidecarPath) &&
        !IFileManager::Get().Delete(*SidecarPath, false, true))
    {
        OutError = FString::Printf(
            TEXT("Cannot remove deleted Script Class identity sidecar '%s'."),
            *SidecarPath);
        return false;
    }
    if (bDeleteFirstPlayRequest &&
        IFileManager::Get().FileExists(*Context.FirstPlayRequestPath) &&
        !IFileManager::Get().Delete(*Context.FirstPlayRequestPath, false, true))
    {
        OutError = FString::Printf(
            TEXT("Cannot remove deleted Script Class first-Play request '%s'."),
            *Context.FirstPlayRequestPath);
        return false;
    }
    return true;
}

void ConfigureTargetScriptAsset(
    UUnifiedScriptAsset& ScriptAsset,
    const FClassOperationContext& Context,
    const FTargetIdentity& Target,
    const FGuid& ClassId)
{
    ScriptAsset.Modify();
    ScriptAsset.ScriptClassId = ClassId;
    ScriptAsset.GeneratedClassPath = FSoftClassPath(Target.BlueprintClassPath);
    ScriptAsset.ScriptTypeName = Target.ManagedTypeName;
    ScriptAsset.SourcePath = Target.ConfiguredSourcePath;
    ScriptAsset.DeclarationAssetPath = Target.DeclarationAssetPath;
    ScriptAsset.MarkPackageDirty();
}

bool SaveClassPackages(
    UBlueprint* Blueprint,
    UUnifiedScriptAsset* ScriptAsset,
    FString& OutError)
{
    if (Blueprint == nullptr || ScriptAsset == nullptr)
    {
        OutError = TEXT("Cannot save the complete Script Class asset transaction.");
        return false;
    }
    FSavePackageArgs SaveArguments;
    SaveArguments.TopLevelFlags = RF_Public | RF_Standalone;
    SaveArguments.SaveFlags = SAVE_None;
    const FString BlueprintFilename = FPackageName::LongPackageNameToFilename(
        Blueprint->GetOutermost()->GetName(),
        FPackageName::GetAssetPackageExtension());
    const FString ScriptAssetFilename = FPackageName::LongPackageNameToFilename(
        ScriptAsset->GetOutermost()->GetName(),
        FPackageName::GetAssetPackageExtension());
    if (!UPackage::SavePackage(
            Blueprint->GetOutermost(),
            Blueprint,
            *BlueprintFilename,
            SaveArguments) ||
        !UPackage::SavePackage(
            ScriptAsset->GetOutermost(),
            ScriptAsset,
            *ScriptAssetFilename,
            SaveArguments))
    {
        OutError = TEXT("Cannot save the complete Script Class asset transaction.");
        return false;
    }
    return true;
}

void FillResult(
    const TCHAR* Operation,
    const FString& TransactionId,
    const FTargetIdentity& Target,
    UBlueprint* Blueprint,
    UUnifiedScriptAsset* ScriptAsset,
    FUnifiedScriptClassAssetOperationResult& OutResult)
{
    OutResult.Operation = Operation;
    OutResult.TransactionId = TransactionId;
    OutResult.BlueprintAssetPath = Target.BlueprintAssetPath;
    OutResult.ScriptAssetPath = Target.ScriptAssetObjectPath;
    OutResult.SourcePath = Target.SourcePath;
    OutResult.ManagedTypeName = Target.ManagedTypeName;
    OutResult.Blueprint = Blueprint;
    OutResult.ScriptAsset = ScriptAsset;
}

void FillDeleteResult(
    const FClassOperationContext& Context,
    const FString& TransactionId,
    FUnifiedScriptClassAssetOperationResult& OutResult)
{
    OutResult.Operation = TEXT("delete");
    OutResult.TransactionId = TransactionId;
    OutResult.BlueprintAssetPath = GetBlueprintAssetPath(Context);
    OutResult.ScriptAssetPath = GetScriptAssetObjectPath(Context);
    OutResult.SourcePath = Context.SourcePath;
    OutResult.ManagedTypeName = Context.ManagedTypeName;
    OutResult.Blueprint = Context.Blueprint;
    OutResult.ScriptAsset = Context.ScriptAsset;
}

bool ShouldFail(
    EUnifiedScriptClassAssetOperationFailurePoint Actual,
    EUnifiedScriptClassAssetOperationFailurePoint Requested,
    FString& OutError)
{
    if (Actual != Requested)
    {
        return false;
    }
    OutError = FString::Printf(
        TEXT("Injected Script Class transaction failure at phase %d."),
        static_cast<int32>(Actual));
    return true;
}

void CloseReceiptAfterRollback(
    UE::UnifiedScript::ClassOperationJournal::FReceipt& Receipt,
    bool bRolledBack,
    FString& InOutRollbackError)
{
    if (!bRolledBack)
    {
        return;
    }
    FString CloseError;
    if (!UE::UnifiedScript::ClassOperationJournal::Close(Receipt, CloseError))
    {
        if (!InOutRollbackError.IsEmpty())
        {
            InOutRollbackError += TEXT("; ");
        }
        InOutRollbackError += TEXT("cannot close recovery receipt: ") + CloseError;
    }
}

bool RollbackRename(
    FClassOperationContext& Context,
    const FTargetIdentity& Target,
    const TArray<FFileSnapshot>& FileSnapshots,
    const TArray<FName>& UEReferencerPackages,
    bool bRestoreExternalFiles,
    FString& OutError)
{
    TArray<FString> Failures;
    Context.ScriptAsset->Modify();
    Context.ScriptAsset->GeneratedClassPath = FSoftClassPath(FString::Printf(
        TEXT("%s.%s_C"),
        *Context.BlueprintPackagePath,
        *Context.OldTypeName));
    Context.ScriptAsset->ScriptTypeName = Context.ManagedTypeName;
    Context.ScriptAsset->SourcePath = Context.ConfiguredSourcePath;
    Context.ScriptAsset->DeclarationAssetPath = Context.DeclarationAssetPath;

    const FString OldDirectory = FPackageName::GetLongPackagePath(
        Context.BlueprintPackagePath);
    const FString OldSidecarDirectory = FPackageName::GetLongPackagePath(
        Context.ScriptAssetPackagePath);
    const FString OldSidecarName = FPackageName::GetLongPackageAssetName(
        Context.ScriptAssetPackagePath);
    TArray<FAssetRenameData> ReverseRenames;
    ReverseRenames.Emplace(Context.Blueprint, OldDirectory, Context.OldTypeName);
    ReverseRenames.Emplace(Context.ScriptAsset, OldSidecarDirectory, OldSidecarName);
    {
        FScopedAssetToolsMutation Mutation;
        if (!FAssetToolsModule::GetModule().Get().RenameAssets(ReverseRenames))
        {
            Failures.Add(TEXT("cannot restore renamed UE assets"));
        }
    }
    const FString TargetObjectPaths[] = {
        Target.BlueprintAssetPath,
        Target.ScriptAssetObjectPath,
    };
    TArray<FString> RepairedPackages;
    FString ReferenceRepairError;
    if (!FixupRedirectorsAndVerify(
            TArray<FString>(TargetObjectPaths, UE_ARRAY_COUNT(TargetObjectPaths)),
            UEReferencerPackages,
            BuildRenameSoftPathRedirects(Context, Target, true),
            RepairedPackages,
            ReferenceRepairError))
    {
        Failures.Add(TEXT("cannot repair rollback redirectors: ") +
            ReferenceRepairError);
    }
    const FString TargetPackages[] = {
        Target.BlueprintPackagePath,
        Target.ScriptAssetPackagePath,
    };
    for (const FString& PackageName : TargetPackages)
    {
        const FString Filename = FPackageName::LongPackageNameToFilename(
            PackageName,
            FPackageName::GetAssetPackageExtension());
        if (IFileManager::Get().FileExists(*Filename) &&
            !IFileManager::Get().Delete(*Filename, false, true))
        {
            Failures.Add(FString::Printf(
                TEXT("cannot remove rollback package '%s'"),
                *Filename));
        }
    }
    FString RestoreError;
    if (bRestoreExternalFiles && !RestoreFiles(FileSnapshots, RestoreError))
    {
        Failures.Add(RestoreError);
    }
    FString SaveError;
    if (!SaveClassPackages(Context.Blueprint, Context.ScriptAsset, SaveError))
    {
        Failures.Add(SaveError);
    }
    OutError = FString::Join(Failures, TEXT("; "));
    return Failures.IsEmpty();
}

bool RollbackDuplicate(
    UBlueprint* Blueprint,
    UUnifiedScriptAsset* ScriptAsset,
    const TArray<FFileSnapshot>& FileSnapshots,
    bool bRestoreExternalFiles,
    FString& OutError)
{
    TArray<FString> Failures;
    if (Blueprint != nullptr || ScriptAsset != nullptr)
    {
        TArray<UObject*> Objects;
        if (Blueprint != nullptr)
        {
            Objects.Add(Blueprint);
        }
        if (ScriptAsset != nullptr)
        {
            Objects.Add(ScriptAsset);
        }
        FScopedAssetToolsMutation Mutation;
        if (ObjectTools::DeleteObjectsUnchecked(Objects) != Objects.Num())
        {
            Failures.Add(TEXT("cannot remove duplicated UE assets"));
        }
    }
    FString RestoreError;
    if (bRestoreExternalFiles && !RestoreFiles(FileSnapshots, RestoreError))
    {
        Failures.Add(RestoreError);
    }
    OutError = FString::Join(Failures, TEXT("; "));
    return Failures.IsEmpty();
}
}

bool FUnifiedScriptClassAssetOperations::Rename(
    UUnifiedScriptAsset* ScriptAsset,
    const FString& TargetBlueprintPackagePath,
    FUnifiedScriptClassAssetOperationResult& OutResult,
    EUnifiedScriptClassAssetOperationFailurePoint FailurePoint)
{
    using namespace UE::UnifiedScript::ClassAssetOperations::Private;
    OutResult = {};
    FClassOperationContext Context;
    FTargetIdentity Target;
    FString Error;
    if (!BuildContext(ScriptAsset, Context, Error) ||
        !BuildTarget(
            Context,
            TargetBlueprintPackagePath,
            ScriptAsset->ScriptClassId,
            Target,
            Error))
    {
        OutResult.Operation = TEXT("rename");
        OutResult.Error = Error;
        return false;
    }
    const FString TransactionId =
        FGuid::NewGuid().ToString(EGuidFormats::DigitsWithHyphensLower);
    FillResult(
        TEXT("rename"),
        TransactionId,
        Target,
        Context.Blueprint,
        ScriptAsset,
        OutResult);

    FManagedReferencePlan ReferencePlan;
    TMap<FString, TArray<uint8>> ManagedSourceOutputs;
    TMap<FString, TArray<uint8>> ManagedSidecarOutputs;
    TArray<FString> ManagedSidecarDeletes;
    FManagedIdentityPlan ManagedIdentityPlan;
    FString Deployment;
    FString FirstPlayRequest;
    TArray<FFileSnapshot> FileSnapshots;
    TArray<FFileSnapshot> RecoverySnapshots;
    TArray<FName> UEReferencerPackages;
    UE::UnifiedScript::ClassOperationJournal::FReceipt Receipt;
    if (!RunManagedReferencePlanner(
            Context,
            Target.TypeName,
            OutResult.TransactionId,
            ReferencePlan,
            Error))
    {
        OutResult.Error = Error;
        return false;
    }
    TArray<FString> ReferencePaths;
    for (const FManagedReferenceDocument& Document : ReferencePlan.Documents)
    {
        ReferencePaths.Add(Document.Path);
        ReferencePaths.AddUnique(Document.Path + TEXT(".usids"));
    }
    ReferencePaths.AddUnique(Context.SourcePath + TEXT(".usids"));
    ReferencePaths.AddUnique(Target.SourcePath + TEXT(".usids"));
    if (IFileManager::Get().FileExists(*(Target.SourcePath + TEXT(".usids"))))
    {
        OutResult.Error = FString::Printf(
            TEXT("Target managed identity sidecar already exists: '%s'."),
            *(Target.SourcePath + TEXT(".usids")));
        return false;
    }
    if (!CollectExternalAssetReferencers(
            Context,
            UEReferencerPackages,
            Error) ||
        !BuildDeployment(Context, Target, false, Deployment, Error) ||
        !BuildFirstPlayRequest(Context, Target, FirstPlayRequest, Error) ||
        !CaptureExternalSnapshots(
            Context,
            Target,
            ReferencePaths,
            FileSnapshots,
            Error) ||
        !ValidateManagedReferencePlanSnapshots(
            ReferencePlan,
            FileSnapshots,
            Error) ||
        !BuildManagedRenameOutputs(
            Context,
            Target,
            ReferencePlan,
            FileSnapshots,
            ManagedSourceOutputs,
            Error) ||
        !RunManagedIdentityPlanner(
            Context,
            Target,
            ReferencePlan,
            ManagedSourceOutputs,
            FileSnapshots,
            OutResult.TransactionId,
            true,
            ManagedIdentityPlan,
            ManagedSidecarOutputs,
            ManagedSidecarDeletes,
            Error) ||
        !VerifyFilesUnchanged(FileSnapshots, Error) ||
        !BuildRecoverySnapshots(
            Context,
            Target,
            FileSnapshots,
            UEReferencerPackages,
            RecoverySnapshots,
            Error) ||
        !UE::UnifiedScript::ClassOperationJournal::Begin(
            TEXT("rename"),
            TransactionId,
            RecoverySnapshots,
            Receipt,
            Error))
    {
        OutResult.Error = Error;
        return false;
    }
    OutResult.RecoveryReceiptPath = Receipt.ReceiptPath;

    TArray<FAssetRenameData> Renames;
    Renames.Emplace(
        Context.Blueprint,
        Target.BlueprintPackageDirectory,
        Target.TypeName);
    Renames.Emplace(
        Context.ScriptAsset,
        Target.BlueprintPackageDirectory,
        Target.ScriptAssetName);
    {
        FScopedAssetToolsMutation Mutation;
        if (!FAssetToolsModule::GetModule().Get().RenameAssets(Renames))
        {
            OutResult.Error = TEXT("AssetTools could not rename the complete Script Class asset pair.");
            return false;
        }
    }

    ConfigureTargetScriptAsset(
        *ScriptAsset,
        Context,
        Target,
        ScriptAsset->ScriptClassId);
    FBlueprintEditorUtils::MarkBlueprintAsModified(Context.Blueprint);
    bool bExternalPublicationStarted = false;
    const auto PublishManagedExternalFiles = [&]()
    {
        bExternalPublicationStarted = true;
        return PublishManagedRenameExternalFiles(
            Context,
            Target,
            ManagedSourceOutputs,
            ManagedSidecarOutputs,
            ManagedSidecarDeletes,
            Deployment,
            FirstPlayRequest,
            Error);
    };
    if (!UE::UnifiedScript::ClassOperationJournal::Advance(
            Receipt,
            UE::UnifiedScript::ClassOperationJournal::EPhase::AssetsMutated,
            Error) ||
        ShouldFail(
            EUnifiedScriptClassAssetOperationFailurePoint::AfterAssetMutation,
            FailurePoint,
            Error) ||
        !VerifyFilesUnchanged(FileSnapshots, Error) ||
        !PublishManagedExternalFiles() ||
        !UE::UnifiedScript::ClassOperationJournal::Advance(
            Receipt,
            UE::UnifiedScript::ClassOperationJournal::EPhase::ExternalPublished,
            Error) ||
        !SaveClassPackages(Context.Blueprint, ScriptAsset, Error) ||
        !FixupRedirectorsAndVerify(
            {
                GetBlueprintAssetPath(Context),
                GetScriptAssetObjectPath(Context),
            },
            UEReferencerPackages,
            BuildRenameSoftPathRedirects(Context, Target, false),
            OutResult.RepairedUEReferencerPackages,
            Error) ||
        ShouldFail(
            EUnifiedScriptClassAssetOperationFailurePoint::AfterExternalPublication,
            FailurePoint,
            Error) ||
        !UE::UnifiedScript::ClassOperationJournal::Close(Receipt, Error))
    {
        FString RollbackError;
        OutResult.bRolledBack = RollbackRename(
            Context,
            Target,
            FileSnapshots,
            UEReferencerPackages,
            bExternalPublicationStarted,
            RollbackError);
        CloseReceiptAfterRollback(
            Receipt,
            OutResult.bRolledBack,
            RollbackError);
        OutResult.RepairedUEReferencerPackages.Reset();
        OutResult.Error = Error;
        if (!RollbackError.IsEmpty())
        {
            OutResult.Error += TEXT(" Rollback failed: ") + RollbackError;
        }
        return false;
    }

    OutResult.bSucceeded = true;
    return true;
}

bool FUnifiedScriptClassAssetOperations::Duplicate(
    UUnifiedScriptAsset* ScriptAsset,
    const FString& TargetBlueprintPackagePath,
    FUnifiedScriptClassAssetOperationResult& OutResult,
    EUnifiedScriptClassAssetOperationFailurePoint FailurePoint)
{
    using namespace UE::UnifiedScript::ClassAssetOperations::Private;
    OutResult = {};
    FClassOperationContext Context;
    FTargetIdentity Target;
    FString Error;
    const FGuid NewClassId = FGuid::NewGuid();
    if (!BuildContext(ScriptAsset, Context, Error) ||
        !BuildTarget(
            Context,
            TargetBlueprintPackagePath,
            NewClassId,
            Target,
            Error))
    {
        OutResult.Operation = TEXT("duplicate");
        OutResult.Error = Error;
        return false;
    }
    const FString TransactionId =
        FGuid::NewGuid().ToString(EGuidFormats::DigitsWithHyphensLower);
    FillResult(
        TEXT("duplicate"),
        TransactionId,
        Target,
        nullptr,
        nullptr,
        OutResult);

    FString Deployment;
    FString FirstPlayRequest;
    FManagedReferencePlan IdentityReferencePlan;
    FManagedIdentityPlan ManagedIdentityPlan;
    TMap<FString, TArray<uint8>> ManagedSourceOutputs;
    TMap<FString, TArray<uint8>> ManagedSidecarOutputs;
    TArray<FString> ManagedSidecarDeletes;
    TArray<FFileSnapshot> FileSnapshots;
    TArray<FFileSnapshot> RecoverySnapshots;
    UE::UnifiedScript::ClassOperationJournal::FReceipt Receipt;
    if (!RunManagedReferencePlanner(
            Context,
            Target.TypeName,
            OutResult.TransactionId,
            IdentityReferencePlan,
            Error))
    {
        OutResult.Error = Error;
        return false;
    }
    TArray<FString> DuplicateReferencePaths;
    for (const FManagedReferenceDocument& Document : IdentityReferencePlan.Documents)
    {
        DuplicateReferencePaths.AddUnique(Document.Path);
        DuplicateReferencePaths.AddUnique(Document.Path + TEXT(".usids"));
    }
    DuplicateReferencePaths.AddUnique(Context.SourcePath + TEXT(".usids"));
    DuplicateReferencePaths.AddUnique(Target.SourcePath + TEXT(".usids"));
    if (!BuildDeployment(Context, Target, true, Deployment, Error) ||
        !BuildFirstPlayRequest(Context, Target, FirstPlayRequest, Error) ||
        !CaptureExternalSnapshots(
            Context,
            Target,
            DuplicateReferencePaths,
            FileSnapshots,
            Error))
    {
        OutResult.Error = Error;
        return false;
    }
    if (!ValidateManagedReferencePlanSnapshots(
            IdentityReferencePlan,
            FileSnapshots,
            Error))
    {
        OutResult.Error = Error;
        return false;
    }
    if (!BuildManagedDuplicateOutputs(
            Context,
            Target,
            IdentityReferencePlan,
            FileSnapshots,
            ManagedSourceOutputs,
            Error))
    {
        OutResult.Error = Error;
        return false;
    }
    for (const TPair<FString, TArray<uint8>>& Pair : ManagedSourceOutputs)
    {
        const FString OutputPaths[] = {Pair.Key, Pair.Key + TEXT(".usids")};
        for (const FString& OutputPath : OutputPaths)
        {
            if (IFileManager::Get().FileExists(*OutputPath))
            {
                Error = FString::Printf(
                    TEXT("Managed duplicate target '%s' already exists."),
                    *OutputPath);
                OutResult.Error = Error;
                return false;
            }
            if (!CaptureSnapshotIfMissing(OutputPath, FileSnapshots, Error))
            {
                OutResult.Error = Error;
                return false;
            }
        }
    }
    if (!RunManagedIdentityPlanner(
            Context,
            Target,
            IdentityReferencePlan,
            ManagedSourceOutputs,
            FileSnapshots,
            OutResult.TransactionId,
            false,
            ManagedIdentityPlan,
            ManagedSidecarOutputs,
            ManagedSidecarDeletes,
            Error) ||
        !BuildRecoverySnapshots(
            Context,
            Target,
            FileSnapshots,
            TArray<FName>(),
            RecoverySnapshots,
            Error) ||
        !VerifyFilesUnchanged(FileSnapshots, Error) ||
        !UE::UnifiedScript::ClassOperationJournal::Begin(
            TEXT("duplicate"),
            TransactionId,
            RecoverySnapshots,
            Receipt,
            Error))
    {
        OutResult.Error = Error;
        return false;
    }
    OutResult.RecoveryReceiptPath = Receipt.ReceiptPath;

    UBlueprint* NewBlueprint = nullptr;
    UUnifiedScriptAsset* NewScriptAsset = nullptr;
    {
        FScopedAssetToolsMutation Mutation;
        IAssetTools& AssetTools = FAssetToolsModule::GetModule().Get();
        NewBlueprint = Cast<UBlueprint>(AssetTools.DuplicateAsset(
            Target.TypeName,
            Target.BlueprintPackageDirectory,
            Context.Blueprint));
        if (NewBlueprint != nullptr)
        {
            NewScriptAsset = Cast<UUnifiedScriptAsset>(AssetTools.DuplicateAsset(
                Target.ScriptAssetName,
                Target.BlueprintPackageDirectory,
                ScriptAsset));
        }
    }
    OutResult.Blueprint = NewBlueprint;
    OutResult.ScriptAsset = NewScriptAsset;
    if (NewBlueprint == nullptr || NewScriptAsset == nullptr)
    {
        FString RollbackError;
        OutResult.bRolledBack = RollbackDuplicate(
            NewBlueprint,
            NewScriptAsset,
            FileSnapshots,
            false,
            RollbackError);
        CloseReceiptAfterRollback(
            Receipt,
            OutResult.bRolledBack,
            RollbackError);
        OutResult.Error = TEXT("AssetTools could not duplicate the complete Script Class asset pair.");
        if (!RollbackError.IsEmpty())
        {
            OutResult.Error += TEXT(" Rollback failed: ") + RollbackError;
        }
        return false;
    }

    ConfigureTargetScriptAsset(
        *NewScriptAsset,
        Context,
        Target,
        NewClassId);
    if (!SetBlueprintScriptAsset(NewBlueprint, NewScriptAsset, Error))
    {
        FString RollbackError;
        OutResult.bRolledBack = RollbackDuplicate(
            NewBlueprint,
            NewScriptAsset,
            FileSnapshots,
            false,
            RollbackError);
        CloseReceiptAfterRollback(
            Receipt,
            OutResult.bRolledBack,
            RollbackError);
        OutResult.Error = Error;
        if (!RollbackError.IsEmpty())
        {
            OutResult.Error += TEXT(" Rollback failed: ") + RollbackError;
        }
        return false;
    }
    FBlueprintEditorUtils::MarkBlueprintAsModified(NewBlueprint);
    FKismetEditorUtilities::CompileBlueprint(NewBlueprint);
    if (!SetBlueprintScriptAsset(NewBlueprint, NewScriptAsset, Error))
    {
        FString RollbackError;
        OutResult.bRolledBack = RollbackDuplicate(
            NewBlueprint,
            NewScriptAsset,
            FileSnapshots,
            false,
            RollbackError);
        CloseReceiptAfterRollback(
            Receipt,
            OutResult.bRolledBack,
            RollbackError);
        OutResult.Error = Error;
        if (!RollbackError.IsEmpty())
        {
            OutResult.Error += TEXT(" Rollback failed: ") + RollbackError;
        }
        return false;
    }

    bool bExternalPublicationStarted = false;
    const auto PublishDuplicateExternalFiles = [&]()
    {
        bExternalPublicationStarted = true;
        return PublishExternalFiles(
            Context,
            Target,
            ManagedSourceOutputs,
            ManagedSidecarOutputs,
            Deployment,
            FirstPlayRequest,
            false,
            Error);
    };
    if (!UE::UnifiedScript::ClassOperationJournal::Advance(
            Receipt,
            UE::UnifiedScript::ClassOperationJournal::EPhase::AssetsMutated,
            Error) ||
        ShouldFail(
            EUnifiedScriptClassAssetOperationFailurePoint::AfterAssetMutation,
            FailurePoint,
            Error) ||
        !VerifyFilesUnchanged(FileSnapshots, Error) ||
        !PublishDuplicateExternalFiles() ||
        !UE::UnifiedScript::ClassOperationJournal::Advance(
            Receipt,
            UE::UnifiedScript::ClassOperationJournal::EPhase::ExternalPublished,
            Error) ||
        ShouldFail(
            EUnifiedScriptClassAssetOperationFailurePoint::AfterExternalPublication,
            FailurePoint,
            Error) ||
        !SaveClassPackages(NewBlueprint, NewScriptAsset, Error) ||
        !UE::UnifiedScript::ClassOperationJournal::Close(Receipt, Error))
    {
        FString RollbackError;
        OutResult.bRolledBack = RollbackDuplicate(
            NewBlueprint,
            NewScriptAsset,
            FileSnapshots,
            bExternalPublicationStarted,
            RollbackError);
        CloseReceiptAfterRollback(
            Receipt,
            OutResult.bRolledBack,
            RollbackError);
        OutResult.Error = Error;
        if (!RollbackError.IsEmpty())
        {
            OutResult.Error += TEXT(" Rollback failed: ") + RollbackError;
        }
        return false;
    }

    OutResult.bSucceeded = true;
    return true;
}

bool FUnifiedScriptClassAssetOperations::Delete(
    UUnifiedScriptAsset* ScriptAsset,
    FUnifiedScriptClassAssetOperationResult& OutResult,
    EUnifiedScriptClassAssetOperationFailurePoint FailurePoint)
{
    using namespace UE::UnifiedScript::ClassAssetOperations::Private;
    OutResult = {};
    FClassOperationContext Context;
    FString Error;
    if (!BuildContext(ScriptAsset, Context, Error))
    {
        OutResult.Operation = TEXT("delete");
        OutResult.Error = Error;
        return false;
    }

    const FString TransactionId =
        FGuid::NewGuid().ToString(EGuidFormats::DigitsWithHyphensLower);
    FillDeleteResult(Context, TransactionId, OutResult);
    FManagedReferencePlan ReferencePlan;
    if (!ValidateNoExternalAssetReferencers(Context, Error) ||
        !RunManagedReferencePlanner(
            Context,
            FString(),
            TransactionId,
            ReferencePlan,
            Error) ||
        !ValidateDeleteManagedReferencePlan(Context, ReferencePlan, Error))
    {
        OutResult.Error = Error;
        return false;
    }

    FString Deployment;
    bool bDeleteFirstPlayRequest = false;
    TArray<FFileSnapshot> FileSnapshots;
    TArray<FString> ReferencePaths;
    for (const FManagedReferenceDocument& Document : ReferencePlan.Documents)
    {
        ReferencePaths.Add(Document.Path);
    }
    if (!BuildDeleteDeployment(Context, Deployment, Error) ||
        !ShouldDeleteFirstPlayRequest(
            Context,
            bDeleteFirstPlayRequest,
            Error) ||
        !CaptureDeleteExternalSnapshots(
            Context,
            ReferencePaths,
            FileSnapshots,
            Error) ||
        !ValidateManagedReferencePlanSnapshots(
            ReferencePlan,
            FileSnapshots,
            Error))
    {
        OutResult.Error = Error;
        return false;
    }

    const FString TombstoneName = TEXT("USD_") +
        TransactionId.Replace(TEXT("-"), TEXT("")).Left(12);
    const FString TombstonePackagePath =
        FPackageName::GetLongPackagePath(Context.BlueprintPackagePath) /
        TombstoneName;
    FTargetIdentity Tombstone;
    if (!BuildTarget(
            Context,
            TombstonePackagePath,
            ScriptAsset->ScriptClassId,
            Tombstone,
            Error))
    {
        OutResult.Error = Error;
        return false;
    }
    TArray<FFileSnapshot> RecoverySnapshots;
    UE::UnifiedScript::ClassOperationJournal::FReceipt Receipt;
    if (!BuildRecoverySnapshots(
            Context,
            Tombstone,
            FileSnapshots,
            TArray<FName>(),
            RecoverySnapshots,
            Error) ||
        !VerifyFilesUnchanged(FileSnapshots, Error) ||
        !UE::UnifiedScript::ClassOperationJournal::Begin(
            TEXT("delete"),
            TransactionId,
            RecoverySnapshots,
            Receipt,
            Error))
    {
        OutResult.Error = Error;
        return false;
    }
    OutResult.RecoveryReceiptPath = Receipt.ReceiptPath;

    TArray<FAssetRenameData> TombstoneRenames;
    TombstoneRenames.Emplace(
        Context.Blueprint,
        Tombstone.BlueprintPackageDirectory,
        Tombstone.TypeName);
    TombstoneRenames.Emplace(
        Context.ScriptAsset,
        Tombstone.BlueprintPackageDirectory,
        Tombstone.ScriptAssetName);
    {
        FScopedAssetToolsMutation Mutation;
        if (!FAssetToolsModule::GetModule().Get().RenameAssets(TombstoneRenames))
        {
            OutResult.Error =
                TEXT("AssetTools could not stage the complete Script Class for deletion.");
            return false;
        }
    }

    bool bExternalPublicationStarted = false;
    const auto PublishDeleteExternal = [&]()
    {
        bExternalPublicationStarted = true;
        return PublishDeleteExternalFiles(
            Context,
            Deployment,
            bDeleteFirstPlayRequest,
            Error);
    };
    const auto Rollback = [&]()
    {
        FString RollbackError;
        OutResult.bRolledBack = RollbackRename(
            Context,
            Tombstone,
            FileSnapshots,
            TArray<FName>(),
            bExternalPublicationStarted,
            RollbackError);
        CloseReceiptAfterRollback(
            Receipt,
            OutResult.bRolledBack,
            RollbackError);
        if (!RollbackError.IsEmpty())
        {
            OutResult.Error += TEXT(" Rollback failed: ") + RollbackError;
        }
        return false;
    };

    if (!UE::UnifiedScript::ClassOperationJournal::Advance(
            Receipt,
            UE::UnifiedScript::ClassOperationJournal::EPhase::AssetsMutated,
            Error) ||
        ShouldFail(
            EUnifiedScriptClassAssetOperationFailurePoint::AfterAssetMutation,
            FailurePoint,
            Error) ||
        !VerifyFilesUnchanged(FileSnapshots, Error) ||
        !PublishDeleteExternal() ||
        !UE::UnifiedScript::ClassOperationJournal::Advance(
            Receipt,
            UE::UnifiedScript::ClassOperationJournal::EPhase::ExternalPublished,
            Error) ||
        ShouldFail(
            EUnifiedScriptClassAssetOperationFailurePoint::AfterExternalPublication,
            FailurePoint,
            Error))
    {
        OutResult.Error = Error;
        return Rollback();
    }

    TArray<UObject*> ObjectsToDelete = {
        Context.Blueprint,
        Context.ScriptAsset,
    };
    FCanDeleteAssetResult CanDeleteResult;
    {
        FScopedAssetToolsMutation Mutation;
        FEditorDelegates::OnAssetsCanDelete.Broadcast(
            ObjectsToDelete,
            CanDeleteResult);
    }
    if (!CanDeleteResult.Get())
    {
        OutResult.Error =
            TEXT("A registered UE editor guard rejected the complete Script Class deletion.");
        return Rollback();
    }

    int32 DeletedCount = 0;
    {
        FScopedAssetToolsMutation Mutation;
        DeletedCount = ObjectTools::DeleteObjectsUnchecked(ObjectsToDelete);
    }
    if (DeletedCount != ObjectsToDelete.Num())
    {
        OutResult.Error = FString::Printf(
            TEXT("UE deleted %d of %d staged Script Class assets; run Repair before continuing."),
            DeletedCount,
            ObjectsToDelete.Num());
        if (IsValid(Context.Blueprint) && IsValid(Context.ScriptAsset))
        {
            return Rollback();
        }
        return false;
    }

    OutResult.Blueprint = nullptr;
    OutResult.ScriptAsset = nullptr;
    if (!UE::UnifiedScript::ClassOperationJournal::Close(Receipt, Error))
    {
        OutResult.Error = Error;
        return false;
    }
    OutResult.bSucceeded = true;
    return true;
}

bool FUnifiedScriptClassAssetOperations::RecoverPendingTransactions(
    FUnifiedScriptClassAssetRecoveryResult& OutResult)
{
    OutResult = {};
    UE::UnifiedScript::ClassOperationJournal::FRecoveryReport Report;
    FString Error;
    const bool bRecovered =
        UE::UnifiedScript::ClassOperationJournal::RecoverPending(Report, Error);
    OutResult.PendingReceiptCount = Report.PendingReceiptCount;
    OutResult.RecoveredReceiptCount = Report.RecoveredReceiptCount;
    OutResult.CleanedCommittedReceiptCount =
        Report.CleanedCommittedReceiptCount;
    OutResult.DeferredLiveReceiptCount = Report.DeferredLiveReceiptCount;
    OutResult.RecoveredTransactionIds = MoveTemp(
        Report.RecoveredTransactionIds);
    OutResult.ArchivePaths = MoveTemp(Report.ArchivePaths);
    OutResult.Error = MoveTemp(Error);
    return bRecovered;
}

bool FUnifiedScriptClassAssetOperations::IsCompleteClassAsset(const UObject* Asset)
{
    using namespace UE::UnifiedScript::ClassAssetOperations::Private;
    if (const UUnifiedScriptAsset* ScriptAsset = Cast<UUnifiedScriptAsset>(Asset))
    {
        return ScriptAsset->ScriptClassId.IsValid() &&
            ScriptAsset->ScriptClassKind != EUnifiedScriptClassKind::None;
    }
    const UBlueprint* Blueprint = Cast<UBlueprint>(Asset);
    const UUnifiedScriptAsset* ScriptAsset = GetBlueprintScriptAsset(
        const_cast<UBlueprint*>(Blueprint));
    return ScriptAsset != nullptr &&
        ScriptAsset->ScriptClassId.IsValid() &&
        ScriptAsset->ScriptClassKind != EUnifiedScriptClassKind::None;
}

bool FUnifiedScriptClassAssetOperations::IsAssetToolsMutationActive()
{
    return UE::UnifiedScript::ClassAssetOperations::Private::GAssetToolsMutationActive;
}

bool FUnifiedScriptClassAssetOperations::IsOrphanedGeneratedBlueprintDuplicate(
    const UBlueprint* Blueprint)
{
    using namespace UE::UnifiedScript::ClassAssetOperations::Private;
    if (Blueprint == nullptr || Blueprint->GeneratedClass == nullptr)
    {
        return false;
    }
    const UUnifiedScriptAsset* ScriptAsset = GetBlueprintScriptAsset(
        const_cast<UBlueprint*>(Blueprint));
    if (ScriptAsset == nullptr || !ScriptAsset->ScriptClassId.IsValid() ||
        ScriptAsset->ScriptClassKind == EUnifiedScriptClassKind::None)
    {
        return false;
    }
    return ScriptAsset->GeneratedClassPath.ToString() !=
        Blueprint->GeneratedClass->GetPathName();
}

bool FUnifiedScriptClassAssetOperations::DeleteOrphanedGeneratedBlueprintDuplicate(
    UBlueprint* Blueprint,
    FString& OutError)
{
    using namespace UE::UnifiedScript::ClassAssetOperations::Private;
    OutError.Reset();
    if (!IsOrphanedGeneratedBlueprintDuplicate(Blueprint))
    {
        OutError = TEXT("The Blueprint is not an orphaned UnifiedScript proxy duplicate.");
        return false;
    }
    TArray<UObject*> Objects{Blueprint};
    FScopedAssetToolsMutation Mutation;
    const int32 DeletedCount = ObjectTools::DeleteObjectsUnchecked(Objects);
    if (DeletedCount != 1)
    {
        OutError = FString::Printf(
            TEXT("UE deleted %d of 1 orphaned UnifiedScript Blueprint proxies."),
            DeletedCount);
        return false;
    }
    return true;
}
