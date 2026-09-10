#if WITH_DEV_AUTOMATION_TESTS

#include "FinareRuntimeDiagnostic.h"

#include "Dom/JsonObject.h"
#include "GenericPlatform/GenericPlatformCrashContext.h"
#include "Misc/AutomationTest.h"
#include "Serialization/JsonReader.h"
#include "Serialization/JsonSerializer.h"

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
    FFinareRuntimeBreadcrumbTest,
    "Finare.Unreal.RuntimeDiagnostics.Breadcrumbs",
    EAutomationTestFlags::EditorContext |
        EAutomationTestFlags::ServerContext |
        EAutomationTestFlags::EngineFilter)

bool FFinareRuntimeBreadcrumbTest::RunTest(const FString& Parameters)
{
    (void)Parameters;
    FFinareRuntimeBreadcrumbs::ResetForTests();

    for (int32 Index = 0;
         Index < FFinareRuntimeBreadcrumbs::MaximumEntryCount + 6;
         ++Index)
    {
        FFinareRuntimeBreadcrumbs::RecordLifecycle(
            FString::Printf(TEXT("Scale.Phase.%d"), Index),
            1000 + Index,
            FString::ChrN(1024, TEXT('x')));
    }

    FFinareRuntimeBreadcrumbSnapshot Snapshot =
        FFinareRuntimeBreadcrumbs::Capture();
    TestEqual(
        TEXT("The process breadcrumb ring has a hard entry bound"),
        Snapshot.Entries.Num(),
        FFinareRuntimeBreadcrumbs::MaximumEntryCount);
    TestEqual(
        TEXT("Overwritten breadcrumbs are counted exactly"),
        Snapshot.DroppedCount,
        static_cast<uint64>(6));
    TestEqual(
        TEXT("The ring evicts the oldest entry first"),
        Snapshot.Entries[0].Operation,
        FString(TEXT("Scale.Phase.6")));
    TestEqual(
        TEXT("The newest lifecycle breadcrumb is retained"),
        Snapshot.Entries.Last().Operation,
        FString(TEXT("Scale.Phase.69")));
    TestEqual(
        TEXT("Breadcrumb messages are detached and bounded"),
        Snapshot.Entries.Last().Message.Len(),
        FFinareRuntimeBreadcrumbs::MaximumMessageLength);

    const FString Json = Snapshot.ToJson();
    TSharedPtr<FJsonObject> Root;
    const TSharedRef<TJsonReader<>> Reader = TJsonReaderFactory<>::Create(Json);
    const TArray<TSharedPtr<FJsonValue>>* Entries = nullptr;
    TestTrue(
        TEXT("The complete breadcrumb snapshot is valid versioned JSON"),
        FJsonSerializer::Deserialize(Reader, Root) && Root.IsValid() &&
            Root->GetStringField(TEXT("schemaVersion")) ==
                TEXT("unifiedscript.runtime-breadcrumbs/v0.1") &&
            Root->TryGetArrayField(TEXT("entries"), Entries) &&
            Entries != nullptr &&
            Entries->Num() ==
                FFinareRuntimeBreadcrumbs::MaximumEntryCount);

    FFinareRuntimeDiagnostic Diagnostic;
    Diagnostic.Kind = FString::ChrN(128, TEXT('k'));
    Diagnostic.Operation = FString::ChrN(192, TEXT('o'));
    Diagnostic.Message = FString::ChrN(2048, TEXT('m'));
    Diagnostic.WorldHostId = 0x1000000000000001ull;
    Diagnostic.InstanceId = 0x2000000000000002ull;
    Diagnostic.Generation = 37;
    FFinareRuntimeBreadcrumbs::RecordDiagnostic(Diagnostic);

    Snapshot = FFinareRuntimeBreadcrumbs::Capture();
    const FFinareRuntimeBreadcrumb& Latest = Snapshot.Entries.Last();
    TestEqual(
        TEXT("Failure kinds are bounded"),
        Latest.Kind.Len(),
        FFinareRuntimeBreadcrumbs::MaximumKindLength);
    TestEqual(
        TEXT("Failure operations are bounded"),
        Latest.Operation.Len(),
        FFinareRuntimeBreadcrumbs::MaximumOperationLength);
    TestEqual(
        TEXT("Failure messages are bounded"),
        Latest.Message.Len(),
        FFinareRuntimeBreadcrumbs::MaximumMessageLength);
    TestEqual(
        TEXT("The latest breadcrumb keeps exact 64-bit World identity"),
        Latest.WorldHostId,
        Diagnostic.WorldHostId);
    TestEqual(
        TEXT("The latest breadcrumb keeps exact 64-bit instance identity"),
        Latest.InstanceId,
        Diagnostic.InstanceId);

    const TMap<FString, FString>& CrashData = FGenericCrashContext::GetGameData();
    const FString* CrashSchema = CrashData.Find(TEXT("FinareBreadcrumbSchema"));
    const FString* CrashOperation = CrashData.Find(TEXT("FinareBreadcrumbOperation"));
    const FString* CrashRing = CrashData.Find(TEXT("FinareBreadcrumbs"));
    TestTrue(
        TEXT("The latest breadcrumb is published into Unreal crash game data"),
        CrashSchema != nullptr &&
            *CrashSchema == TEXT("unifiedscript.runtime-breadcrumbs/v0.1") &&
            CrashOperation != nullptr &&
            *CrashOperation == Latest.Operation &&
            CrashRing != nullptr &&
            !CrashRing->IsEmpty() &&
            CrashRing->Len() <= 8192);

    TSharedPtr<FJsonObject> CrashRoot;
    const TSharedRef<TJsonReader<>> CrashReader =
        TJsonReaderFactory<>::Create(CrashRing != nullptr ? *CrashRing : FString());
    const TArray<TSharedPtr<FJsonValue>>* CrashEntries = nullptr;
    TestTrue(
        TEXT("Crash game data carries only the bounded recent tail"),
        FJsonSerializer::Deserialize(CrashReader, CrashRoot) &&
            CrashRoot.IsValid() &&
            CrashRoot->TryGetArrayField(TEXT("entries"), CrashEntries) &&
            CrashEntries != nullptr &&
            CrashEntries->Num() ==
                FFinareRuntimeBreadcrumbs::MaximumCrashContextEntryCount);

    FFinareRuntimeBreadcrumbs::ResetForTests();
    TestEqual(
        TEXT("Test reset releases every detached breadcrumb"),
        FFinareRuntimeBreadcrumbs::Capture().Entries.Num(),
        0);
    TestFalse(
        TEXT("Test reset removes Finare crash game data"),
        FGenericCrashContext::GetGameData().Contains(
            TEXT("FinareBreadcrumbSchema")));
    return true;
}

#endif
