#include "UnifiedScriptClassOperationJournal.h"

#include "Dom/JsonObject.h"
#include "HAL/FileManager.h"
#include "HAL/PlatformProcess.h"
#include "Misc/Base64.h"
#include "Misc/DateTime.h"
#include "Misc/FileHelper.h"
#include "Misc/Paths.h"
#include "Serialization/JsonReader.h"
#include "Serialization/JsonSerializer.h"
#include "Serialization/JsonWriter.h"

THIRD_PARTY_INCLUDES_START
#include <openssl/sha.h>
THIRD_PARTY_INCLUDES_END

namespace UE::UnifiedScript::ClassOperationJournal
{
namespace
{
constexpr int32 MaxSnapshotCount = 2048;
constexpr int64 MaxSnapshotBytes = 512ll * 1024ll * 1024ll;
const TCHAR* ReceiptSchema = TEXT("unifiedscript.class-operation-receipt/v0.1");
const TCHAR* RecoverySchema = TEXT("unifiedscript.class-operation-recovery/v0.1");

FString NormalizeAbsolutePath(const FString& Path)
{
    FString Result = FPaths::ConvertRelativePathToFull(Path);
    FPaths::NormalizeFilename(Result);
    return Result;
}

bool WriteBytesAtomically(
    const FString& Path,
    const TArray<uint8>& Bytes,
    FString& OutError)
{
    IFileManager::Get().MakeDirectory(*FPaths::GetPath(Path), true);
    const FString Temporary = Path + TEXT(".unifiedscript-journal.tmp");
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

bool WriteTextAtomically(
    const FString& Path,
    const FString& Text,
    FString& OutError)
{
    FTCHARToUTF8 Utf8(*Text);
    TArray<uint8> Bytes;
    Bytes.Append(
        reinterpret_cast<const uint8*>(Utf8.Get()),
        Utf8.Length());
    return WriteBytesAtomically(Path, Bytes, OutError);
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
        OutError = TEXT("Cannot serialize the Script Class operation receipt.");
        return false;
    }
    OutJson.AppendChar(TEXT('\n'));
    return true;
}

FString DigestBytes(const TArray<uint8>& Bytes)
{
    uint8 Digest[SHA256_DIGEST_LENGTH]{};
    if (SHA256(
            Bytes.IsEmpty() ? nullptr : Bytes.GetData(),
            static_cast<size_t>(Bytes.Num()),
            Digest) == nullptr)
    {
        return FString();
    }
    return TEXT("sha256:") +
        BytesToHex(Digest, SHA256_DIGEST_LENGTH).ToLower();
}

const TCHAR* PhaseName(EPhase Phase)
{
    switch (Phase)
    {
    case EPhase::Prepared:
        return TEXT("prepared");
    case EPhase::AssetsMutated:
        return TEXT("assetsMutated");
    case EPhase::ExternalPublished:
        return TEXT("externalPublished");
    case EPhase::Committed:
        return TEXT("committed");
    }
    return TEXT("unknown");
}

bool ParsePhase(const FString& Value, EPhase& OutPhase)
{
    if (Value == TEXT("prepared"))
    {
        OutPhase = EPhase::Prepared;
        return true;
    }
    if (Value == TEXT("assetsMutated"))
    {
        OutPhase = EPhase::AssetsMutated;
        return true;
    }
    if (Value == TEXT("externalPublished"))
    {
        OutPhase = EPhase::ExternalPublished;
        return true;
    }
    if (Value == TEXT("committed"))
    {
        OutPhase = EPhase::Committed;
        return true;
    }
    return false;
}

bool IsNextPhase(EPhase Current, EPhase Next)
{
    return static_cast<uint8>(Next) == static_cast<uint8>(Current) + 1;
}

bool SerializeReceipt(
    const FReceipt& Receipt,
    FString& OutJson,
    FString& OutError)
{
    const TSharedRef<FJsonObject> Root = MakeShared<FJsonObject>();
    Root->SetStringField(TEXT("schemaVersion"), ReceiptSchema);
    Root->SetStringField(TEXT("transactionId"), Receipt.TransactionId);
    Root->SetStringField(TEXT("operation"), Receipt.Operation);
    Root->SetStringField(TEXT("phase"), PhaseName(Receipt.Phase));
    Root->SetStringField(TEXT("createdUtc"), Receipt.CreatedUtc);
    Root->SetStringField(TEXT("updatedUtc"), Receipt.UpdatedUtc);
    Root->SetNumberField(TEXT("ownerProcessId"), Receipt.OwnerProcessId);

    TArray<TSharedPtr<FJsonValue>> Files;
    Files.Reserve(Receipt.Snapshots.Num());
    for (const FSnapshot& Snapshot : Receipt.Snapshots)
    {
        const TSharedRef<FJsonObject> File = MakeShared<FJsonObject>();
        File->SetStringField(TEXT("path"), Snapshot.Path);
        File->SetBoolField(TEXT("existed"), Snapshot.bExisted);
        File->SetStringField(
            TEXT("preimageDigest"),
            Snapshot.bExisted ? DigestBytes(Snapshot.Bytes) : TEXT("missing"));
        File->SetStringField(
            TEXT("preimageBase64"),
            Snapshot.bExisted ? FBase64::Encode(Snapshot.Bytes) : FString());
        Files.Add(MakeShared<FJsonValueObject>(File));
    }
    Root->SetArrayField(TEXT("files"), Files);
    return SerializeJson(Root, OutJson, OutError);
}

bool WriteReceipt(FReceipt& Receipt, FString& OutError)
{
    Receipt.UpdatedUtc = FDateTime::UtcNow().ToIso8601();
    FString Json;
    return SerializeReceipt(Receipt, Json, OutError) &&
        WriteTextAtomically(Receipt.ReceiptPath, Json, OutError);
}

bool ReadReceipt(
    const FString& ReceiptPath,
    FReceipt& OutReceipt,
    FString& OutError)
{
    FString Json;
    if (!FFileHelper::LoadFileToString(Json, *ReceiptPath))
    {
        OutError = FString::Printf(TEXT("Cannot read receipt '%s'."), *ReceiptPath);
        return false;
    }
    TSharedPtr<FJsonObject> Root;
    const TSharedRef<TJsonReader<>> Reader = TJsonReaderFactory<>::Create(Json);
    if (!FJsonSerializer::Deserialize(Reader, Root) || !Root.IsValid())
    {
        OutError = FString::Printf(
            TEXT("Invalid receipt JSON in '%s': %s"),
            *ReceiptPath,
            *Reader->GetErrorMessage());
        return false;
    }

    FString Schema;
    FString TransactionId;
    FString Operation;
    FString PhaseText;
    FString CreatedUtc;
    FString UpdatedUtc;
    double OwnerProcessIdNumber = 0.0;
    const TArray<TSharedPtr<FJsonValue>>* Files = nullptr;
    EPhase Phase;
    FGuid ParsedTransactionId;
    if (!Root->TryGetStringField(TEXT("schemaVersion"), Schema) ||
        Schema != ReceiptSchema ||
        !Root->TryGetStringField(TEXT("transactionId"), TransactionId) ||
        !FGuid::Parse(TransactionId, ParsedTransactionId) ||
        !Root->TryGetStringField(TEXT("operation"), Operation) ||
        (Operation != TEXT("rename") &&
            Operation != TEXT("duplicate") &&
            Operation != TEXT("delete")) ||
        !Root->TryGetStringField(TEXT("phase"), PhaseText) ||
        !ParsePhase(PhaseText, Phase) ||
        !Root->TryGetStringField(TEXT("createdUtc"), CreatedUtc) ||
        !Root->TryGetStringField(TEXT("updatedUtc"), UpdatedUtc) ||
        !Root->TryGetNumberField(TEXT("ownerProcessId"), OwnerProcessIdNumber) ||
        OwnerProcessIdNumber < 1.0 ||
        OwnerProcessIdNumber > static_cast<double>(MAX_uint32) ||
        !Root->TryGetArrayField(TEXT("files"), Files) ||
        Files == nullptr ||
        Files->Num() > MaxSnapshotCount)
    {
        OutError = FString::Printf(
            TEXT("Receipt '%s' does not satisfy %s."),
            *ReceiptPath,
            ReceiptSchema);
        return false;
    }

    FReceipt Receipt;
    Receipt.Operation = Operation;
    Receipt.TransactionId = TransactionId;
    Receipt.ReceiptPath = NormalizeAbsolutePath(ReceiptPath);
    Receipt.CreatedUtc = CreatedUtc;
    Receipt.UpdatedUtc = UpdatedUtc;
    Receipt.OwnerProcessId = static_cast<uint32>(OwnerProcessIdNumber);
    Receipt.Phase = Phase;
    TSet<FString> SeenPaths;
    int64 TotalBytes = 0;
    for (const TSharedPtr<FJsonValue>& Value : *Files)
    {
        const TSharedPtr<FJsonObject>* File = nullptr;
        FString Path;
        FString Digest;
        FString Base64;
        bool bExisted = false;
        if (!Value.IsValid() ||
            !Value->TryGetObject(File) || File == nullptr || !File->IsValid() ||
            !(*File)->TryGetStringField(TEXT("path"), Path) ||
            !(*File)->TryGetBoolField(TEXT("existed"), bExisted) ||
            !(*File)->TryGetStringField(TEXT("preimageDigest"), Digest) ||
            !(*File)->TryGetStringField(TEXT("preimageBase64"), Base64))
        {
            OutError = FString::Printf(
                TEXT("Receipt '%s' contains an invalid file snapshot."),
                *ReceiptPath);
            return false;
        }
        Path = NormalizeAbsolutePath(Path);
        if (Path.IsEmpty() || SeenPaths.Contains(Path))
        {
            OutError = FString::Printf(
                TEXT("Receipt '%s' contains an empty or duplicate path."),
                *ReceiptPath);
            return false;
        }
        SeenPaths.Add(Path);

        FSnapshot Snapshot;
        Snapshot.Path = Path;
        Snapshot.bExisted = bExisted;
        if (bExisted)
        {
            if (!FBase64::Decode(Base64, Snapshot.Bytes) ||
                DigestBytes(Snapshot.Bytes) != Digest)
            {
                OutError = FString::Printf(
                    TEXT("Receipt '%s' has a corrupt preimage for '%s'."),
                    *ReceiptPath,
                    *Path);
                return false;
            }
            TotalBytes += Snapshot.Bytes.Num();
            if (TotalBytes > MaxSnapshotBytes)
            {
                OutError = FString::Printf(
                    TEXT("Receipt '%s' exceeds the recovery byte bound."),
                    *ReceiptPath);
                return false;
            }
        }
        else if (Digest != TEXT("missing") || !Base64.IsEmpty())
        {
            OutError = FString::Printf(
                TEXT("Receipt '%s' has content for missing preimage '%s'."),
                *ReceiptPath,
                *Path);
            return false;
        }
        Receipt.Snapshots.Add(MoveTemp(Snapshot));
    }
    OutReceipt = MoveTemp(Receipt);
    return true;
}

bool PreserveInterruptedFiles(
    const FReceipt& Receipt,
    const FString& ArchiveDirectory,
    FString& OutManifestJson,
    FString& OutError)
{
    TArray<TSharedPtr<FJsonValue>> Files;
    for (int32 Index = 0; Index < Receipt.Snapshots.Num(); ++Index)
    {
        const FSnapshot& Snapshot = Receipt.Snapshots[Index];
        const bool bExists = IFileManager::Get().FileExists(*Snapshot.Path);
        TArray<uint8> CurrentBytes;
        if (bExists && !FFileHelper::LoadFileToArray(CurrentBytes, *Snapshot.Path))
        {
            OutError = FString::Printf(
                TEXT("Cannot preserve interrupted file '%s'."),
                *Snapshot.Path);
            return false;
        }
        if (bExists == Snapshot.bExisted &&
            (!bExists || CurrentBytes == Snapshot.Bytes))
        {
            continue;
        }

        FString BackupPath;
        if (bExists)
        {
            BackupPath = FPaths::Combine(
                ArchiveDirectory,
                FString::Printf(TEXT("current-%04d.bin"), Index));
            if (!WriteBytesAtomically(BackupPath, CurrentBytes, OutError))
            {
                return false;
            }
        }
        const TSharedRef<FJsonObject> File = MakeShared<FJsonObject>();
        File->SetStringField(TEXT("path"), Snapshot.Path);
        File->SetBoolField(TEXT("existed"), bExists);
        File->SetStringField(
            TEXT("digest"),
            bExists ? DigestBytes(CurrentBytes) : TEXT("missing"));
        File->SetStringField(TEXT("backupPath"), BackupPath);
        Files.Add(MakeShared<FJsonValueObject>(File));
    }

    const TSharedRef<FJsonObject> Root = MakeShared<FJsonObject>();
    Root->SetStringField(TEXT("schemaVersion"), RecoverySchema);
    Root->SetStringField(TEXT("transactionId"), Receipt.TransactionId);
    Root->SetStringField(TEXT("operation"), Receipt.Operation);
    Root->SetStringField(TEXT("interruptedPhase"), PhaseName(Receipt.Phase));
    Root->SetStringField(TEXT("recoveredUtc"), FDateTime::UtcNow().ToIso8601());
    Root->SetArrayField(TEXT("preservedCurrentFiles"), Files);
    return SerializeJson(Root, OutManifestJson, OutError);
}

bool RestoreSnapshots(const FReceipt& Receipt, FString& OutError)
{
    TArray<FString> Failures;
    for (const FSnapshot& Snapshot : Receipt.Snapshots)
    {
        FString Error;
        if (Snapshot.bExisted)
        {
            if (!WriteBytesAtomically(Snapshot.Path, Snapshot.Bytes, Error))
            {
                Failures.Add(Error);
            }
        }
        else if (IFileManager::Get().FileExists(*Snapshot.Path) &&
            !IFileManager::Get().Delete(*Snapshot.Path, false, true))
        {
            Failures.Add(FString::Printf(
                TEXT("Cannot remove interrupted output '%s'."),
                *Snapshot.Path));
        }
    }
    OutError = FString::Join(Failures, TEXT("; "));
    return Failures.IsEmpty();
}
}

FString GetPendingRoot()
{
    return NormalizeAbsolutePath(FPaths::Combine(
        FPaths::ProjectSavedDir(),
        TEXT("UnifiedScript/ClassOperations/Pending")));
}

FString GetRecoveredRoot()
{
    return NormalizeAbsolutePath(FPaths::Combine(
        FPaths::ProjectSavedDir(),
        TEXT("UnifiedScript/ClassOperations/Recovered")));
}

bool CaptureSnapshot(
    const FString& Path,
    FSnapshot& OutSnapshot,
    FString& OutError)
{
    FSnapshot Snapshot;
    Snapshot.Path = NormalizeAbsolutePath(Path);
    Snapshot.bExisted = IFileManager::Get().FileExists(*Snapshot.Path);
    if (Snapshot.bExisted &&
        !FFileHelper::LoadFileToArray(Snapshot.Bytes, *Snapshot.Path))
    {
        OutError = FString::Printf(TEXT("Cannot snapshot '%s'."), *Snapshot.Path);
        return false;
    }
    OutSnapshot = MoveTemp(Snapshot);
    return true;
}

bool Begin(
    const FString& Operation,
    const FString& TransactionId,
    const TArray<FSnapshot>& Snapshots,
    FReceipt& OutReceipt,
    FString& OutError)
{
    FGuid ParsedTransactionId;
    if (!FGuid::Parse(TransactionId, ParsedTransactionId) ||
        Snapshots.Num() > MaxSnapshotCount)
    {
        OutError = TEXT("Cannot create an invalid Script Class operation receipt.");
        return false;
    }

    FReceipt Receipt;
    Receipt.Operation = Operation;
    Receipt.TransactionId = TransactionId;
    Receipt.ReceiptPath = FPaths::Combine(
        GetPendingRoot(),
        TransactionId + TEXT(".json"));
    Receipt.CreatedUtc = FDateTime::UtcNow().ToIso8601();
    Receipt.UpdatedUtc = Receipt.CreatedUtc;
    Receipt.OwnerProcessId = FPlatformProcess::GetCurrentProcessId();
    Receipt.Phase = EPhase::Prepared;
    int64 TotalBytes = 0;
    TSet<FString> SeenPaths;
    for (const FSnapshot& Input : Snapshots)
    {
        FSnapshot Snapshot = Input;
        Snapshot.Path = NormalizeAbsolutePath(Snapshot.Path);
        if (Snapshot.Path.IsEmpty() || SeenPaths.Contains(Snapshot.Path))
        {
            OutError = TEXT("Script Class receipt paths must be unique and absolute.");
            return false;
        }
        SeenPaths.Add(Snapshot.Path);
        TotalBytes += Snapshot.Bytes.Num();
        if (TotalBytes > MaxSnapshotBytes)
        {
            OutError = TEXT("Script Class operation receipt exceeds the recovery byte bound.");
            return false;
        }
        Receipt.Snapshots.Add(MoveTemp(Snapshot));
    }
    if (IFileManager::Get().FileExists(*Receipt.ReceiptPath))
    {
        OutError = FString::Printf(
            TEXT("Script Class receipt '%s' already exists."),
            *Receipt.ReceiptPath);
        return false;
    }
    if (!WriteReceipt(Receipt, OutError))
    {
        return false;
    }
    OutReceipt = MoveTemp(Receipt);
    return true;
}

bool Advance(FReceipt& Receipt, EPhase Phase, FString& OutError)
{
    if (!IsNextPhase(Receipt.Phase, Phase))
    {
        OutError = FString::Printf(
            TEXT("Invalid Script Class receipt transition %s -> %s."),
            PhaseName(Receipt.Phase),
            PhaseName(Phase));
        return false;
    }
    Receipt.Phase = Phase;
    return WriteReceipt(Receipt, OutError);
}

bool Close(FReceipt& Receipt, FString& OutError)
{
    if (Receipt.Phase != EPhase::Committed)
    {
        Receipt.Phase = EPhase::Committed;
        if (!WriteReceipt(Receipt, OutError))
        {
            return false;
        }
    }
    // A committed receipt is harmless and will be cleaned on the next startup.
    IFileManager::Get().Delete(*Receipt.ReceiptPath, false, true);
    return true;
}

bool RecoverPending(
    FRecoveryReport& OutReport,
    FString& OutError,
    bool bIncludeCurrentProcessForTesting)
{
    OutReport = {};
    TArray<FString> ReceiptFiles;
    IFileManager::Get().FindFiles(
        ReceiptFiles,
        *(GetPendingRoot() / TEXT("*.json")),
        true,
        false);
    ReceiptFiles.Sort();
    OutReport.PendingReceiptCount = ReceiptFiles.Num();
    TArray<FString> Failures;
    for (const FString& ReceiptFilename : ReceiptFiles)
    {
        const FString ReceiptPath = FPaths::Combine(
            GetPendingRoot(),
            ReceiptFilename);
        FReceipt Receipt;
        FString Error;
        if (!ReadReceipt(ReceiptPath, Receipt, Error))
        {
            Failures.Add(Error);
            continue;
        }
        if (Receipt.Phase == EPhase::Committed)
        {
            if (!IFileManager::Get().Delete(*ReceiptPath, false, true))
            {
                Failures.Add(FString::Printf(
                    TEXT("Cannot clean committed receipt '%s'."),
                    *ReceiptPath));
            }
            else
            {
                ++OutReport.CleanedCommittedReceiptCount;
            }
            continue;
        }
        if (!bIncludeCurrentProcessForTesting &&
            FPlatformProcess::IsApplicationRunning(Receipt.OwnerProcessId))
        {
            ++OutReport.DeferredLiveReceiptCount;
            continue;
        }

        const FString ArchiveDirectory = FPaths::Combine(
            GetRecoveredRoot(),
            Receipt.TransactionId);
        IFileManager::Get().MakeDirectory(*ArchiveDirectory, true);
        FString RecoveryJson;
        if (!PreserveInterruptedFiles(
                Receipt,
                ArchiveDirectory,
                RecoveryJson,
                Error) ||
            !WriteTextAtomically(
                ArchiveDirectory / TEXT("recovery.json"),
                RecoveryJson,
                Error) ||
            IFileManager::Get().Copy(
                *(ArchiveDirectory / TEXT("receipt.json")),
                *ReceiptPath,
                true,
                true) != COPY_OK ||
            !RestoreSnapshots(Receipt, Error) ||
            !IFileManager::Get().Delete(*ReceiptPath, false, true))
        {
            if (Error.IsEmpty())
            {
                Error = FString::Printf(
                    TEXT("Cannot archive or close receipt '%s'."),
                    *ReceiptPath);
            }
            Failures.Add(Error);
            continue;
        }
        ++OutReport.RecoveredReceiptCount;
        OutReport.RecoveredTransactionIds.Add(Receipt.TransactionId);
        OutReport.ArchivePaths.Add(ArchiveDirectory);
    }
    OutError = FString::Join(Failures, TEXT("; "));
    return Failures.IsEmpty();
}
}
