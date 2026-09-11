#include "Widgets/SFinareAuthoringSurface.h"

#include "FinareAsset.h"
#include "Misc/AutomationTest.h"
#include "Misc/Paths.h"
#include "UObject/Package.h"

namespace UE::Finare::NativePublicationCancellationTests
{
	struct FRequestRecord
	{
		FString Verb;
		FString Url;
		FString TransactionId;
		FString PreviewToken;
		FString Body;
		TFunction<void(bool, int32, FString)> Completion;
	};

	static const FString Token =
		TEXT("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef");
	static const FString TransactionId = TEXT("native-cancel-client-test");

	static FString BuildPrepareResponse(const FString& OutputPath)
	{
		return FString::Printf(
			TEXT("{\"schemaVersion\":\"unifiedscript.native-publication-prepare/v0.1\","
				"\"transactionId\":\"%s\",\"previewToken\":\"%s\","
				"\"baselineRevision\":1,\"candidateRevision\":2,"
				"\"publishesRuntime\":true,\"historyEntryTransactionId\":\"%s\","
				"\"historyDirection\":\"commit\","
				"\"scriptAssets\":{\"schemaVersion\":\"unifiedscript.script-assets/v0.1\",\"assets\":[]},"
				"\"bindingChanges\":[],"
				"\"outputs\":[{\"path\":\"%s\",\"baselineExists\":false,"
				"\"baselineDigest\":\"sha256:e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855\","
				"\"candidateDigest\":\"sha256:e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855\","
				"\"content\":\"\"}]}"),
			*TransactionId,
			*Token,
			*TransactionId,
			*OutputPath);
	}

	static FString BuildCancelAck(
		const FString& InTransactionId = TransactionId,
		const FString& InToken = Token,
		const FString& State = TEXT("cancelled"))
	{
		return FString::Printf(
			TEXT("{\"schemaVersion\":\"unifiedscript.native-publication-finish/v0.1\","
				"\"state\":\"%s\",\"transactionId\":\"%s\","
				"\"prepare\":{\"transactionId\":\"%s\",\"previewToken\":\"%s\"}}"),
			*State,
			*InTransactionId,
			*InTransactionId,
			*InToken);
	}
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
	FFinareNativePublicationCancellationTest,
	"Finare.Unreal.Editor.Authoring.NativePublicationCancellation",
	EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FFinareNativePublicationCancellationTest::RunTest(const FString& Parameters)
{
	(void)Parameters;
	using namespace UE::Finare::NativePublicationCancellationTests;

	FString OutputPath = FPaths::ConvertRelativePathToFull(FPaths::Combine(
		FPaths::ProjectSavedDir(),
		TEXT("Finare/Tests/NativePublicationCancellation_"),
		FGuid::NewGuid().ToString(EGuidFormats::Digits),
		TEXT("candidate.bin")));
	OutputPath.ReplaceInline(TEXT("\\"), TEXT("/"));

	const auto SeedPendingTransaction = [](const TSharedRef<SFinareAuthoringSurface>& Surface)
	{
		Surface->PendingAuthoringTransaction = FFinarePendingAuthoringTransaction{
			TEXT("{}"),
			TEXT("http://127.0.0.1:4173/api/authoring/source-text"),
			TEXT("native cancellation test"),
			TransactionId,
			FString(),
			true,
		};
		FFinareAuthoringPreviewResult Preview;
		Preview.TransactionId = TransactionId;
		Preview.PreviewToken = Token;
		Preview.BaselineRevision = 1;
		Preview.CandidateRevision = 2;
		Preview.bPublishesRuntime = true;
		Surface->PendingAuthoringPreview = MoveTemp(Preview);
		Surface->PendingNativePublication.Reset();
		Surface->PendingNativePublicationContext.Reset();
		Surface->bPatchRequestPending = false;
		Surface->bNativePublicationReservationPending = false;
		Surface->bNativePublicationPrepareRecoveryPending = false;
		Surface->bNativePublicationCancelRecoveryPending = false;

		UFinareAsset* Asset = NewObject<UFinareAsset>(
			GetTransientPackage(),
			NAME_None,
			RF_Transient);
		Asset->AuthoringServerUrl = TEXT("http://127.0.0.1:4173");
		Surface->LoadedScriptAsset = Asset;
	};

	const auto InstallTransport = [](
		const TSharedRef<SFinareAuthoringSurface>& Surface,
		TArray<FRequestRecord>& Requests,
		bool& bAllowStart)
	{
		Surface->NativePublicationTestTransport =
			SFinareAuthoringSurface::FNativePublicationTestTransport(
				[&Requests, &bAllowStart](
				const FString& Verb,
				const FString& Url,
				const FString& InTransactionId,
				const FString& PreviewToken,
				const FString& Body,
				SFinareAuthoringSurface::FNativePublicationTestCompletion Completion)
				{
					FRequestRecord& Request = Requests.AddDefaulted_GetRef();
					Request.Verb = Verb;
					Request.Url = Url;
					Request.TransactionId = InTransactionId;
					Request.PreviewToken = PreviewToken;
					Request.Body = Body;
					Request.Completion = MoveTemp(Completion);
					return bAllowStart;
				});
	};

	TArray<FRequestRecord> Requests;
	const TSharedRef<SFinareAuthoringSurface> Surface =
		SNew(SFinareAuthoringSurface);
	SeedPendingTransaction(Surface);
	bool bAllowStart = true;
	InstallTransport(Surface, Requests, bAllowStart);

	Surface->HandleNativePublicationPrepareResponse(
		true,
		200,
		BuildPrepareResponse(OutputPath));
	TestEqual(TEXT("Local apply rejection emits exactly one cancel request"), Requests.Num(), 1);
	if (Requests.Num() != 1)
	{
		return false;
	}
	TestTrue(
		TEXT("Cancel request uses native cancel endpoint"),
		Requests[0].Url.EndsWith(TEXT("/api/authoring/native/cancel")));
	TestEqual(TEXT("Cancel request preserves transaction identity"), Requests[0].TransactionId, TransactionId);
	TestEqual(TEXT("Cancel request preserves preview token"), Requests[0].PreviewToken, Token);
	TestEqual(TEXT("Cancel request uses empty JSON body"), Requests[0].Body, TEXT("{}"));
	TestTrue(TEXT("Cancel request is marked in flight"), Surface->bPatchRequestPending);

	// A second click while the first cancellation is in flight must not emit
	// a second POST or replace the pending transaction.
	Surface->HandleRetryNativePublicationCancel();
	TestEqual(TEXT("In-flight cancellation cannot be duplicated"), Requests.Num(), 1);

	Requests[0].Completion(false, 0, FString());
	TestFalse(TEXT("Transport failure ends the in-flight flag"), Surface->bPatchRequestPending);
	TestTrue(TEXT("Transport failure retains native reservation"), Surface->bNativePublicationReservationPending);
	TestTrue(TEXT("Transport failure retains pending transaction"), Surface->PendingAuthoringTransaction.IsSet());

	Surface->SubmitAuthoringTransaction(
		TEXT("{}"),
		TEXT("http://127.0.0.1:4173/api/authoring/source-text"),
		TEXT("must remain blocked"));
	Surface->RequestWorkspaceRefresh();
	TestEqual(TEXT("New submit is blocked while cancellation is unresolved"), Requests.Num(), 1);
	TestFalse(TEXT("Refresh is blocked while cancellation is unresolved"), Surface->bPatchRequestPending);

	Surface->HandleRetryNativePublicationCancel();
	TestEqual(TEXT("Verified retry emits one new cancel request"), Requests.Num(), 2);
	if (Requests.Num() != 2)
	{
		return false;
	}
	Surface->HandleRetryNativePublicationCancel();
	TestEqual(TEXT("Retried cancellation is also single-flight"), Requests.Num(), 2);
	Requests[1].Completion(true, 200, BuildCancelAck());
	TestFalse(TEXT("Exact cancelled ACK releases native reservation"), Surface->bNativePublicationReservationPending);
	TestFalse(TEXT("Exact cancelled ACK clears pending transaction"), Surface->PendingAuthoringTransaction.IsSet());

	const auto ExerciseInvalidAck = [&](
		const FString& Label,
		const FString& Ack)
	{
		TArray<FRequestRecord> InvalidRequests;
		const TSharedRef<SFinareAuthoringSurface> InvalidSurface =
			SNew(SFinareAuthoringSurface);
		SeedPendingTransaction(InvalidSurface);
		bool bInvalidAllowStart = true;
		InstallTransport(InvalidSurface, InvalidRequests, bInvalidAllowStart);
		InvalidSurface->HandleNativePublicationPrepareResponse(
			true,
			200,
			BuildPrepareResponse(OutputPath));
		TestEqual(*FString::Printf(TEXT("%s emits cancellation"), *Label), InvalidRequests.Num(), 1);
		if (InvalidRequests.Num() == 1)
		{
			InvalidRequests[0].Completion(true, 200, Ack);
			TestTrue(*FString::Printf(TEXT("%s keeps reservation"), *Label),
			         InvalidSurface->bNativePublicationReservationPending);
			TestTrue(*FString::Printf(TEXT("%s keeps pending transaction"), *Label),
			         InvalidSurface->PendingAuthoringTransaction.IsSet());
			InvalidSurface->SubmitAuthoringTransaction(
				TEXT("{}"),
				TEXT("http://127.0.0.1:4173/api/authoring/source-text"),
				TEXT("must remain blocked"));
			InvalidSurface->RequestWorkspaceRefresh();
			TestEqual(
				*FString::Printf(TEXT("%s blocks submit and refresh"), *Label),
				InvalidRequests.Num(),
				1);
			TestFalse(
				*FString::Printf(TEXT("%s leaves no replacement request in flight"), *Label),
				InvalidSurface->bPatchRequestPending);
		}
	};
	ExerciseInvalidAck(TEXT("Malformed ACK"), TEXT("{}"));
	ExerciseInvalidAck(TEXT("Foreign ACK"), BuildCancelAck(TEXT("foreign-transaction"), Token));
	ExerciseInvalidAck(TEXT("Adopted ACK"), BuildCancelAck(TransactionId, Token, TEXT("adopted")));

	// A malformed prepare response has no safe output manifest to validate,
	// so recovery retries prepare with the same transaction/token instead of
	// pretending that a cancel baseline has been proven.
	TArray<FRequestRecord> PrepareRecoveryRequests;
	const TSharedRef<SFinareAuthoringSurface> PrepareRecoverySurface =
		SNew(SFinareAuthoringSurface);
	SeedPendingTransaction(PrepareRecoverySurface);
	bool bPrepareRecoveryAllowStart = true;
	InstallTransport(PrepareRecoverySurface, PrepareRecoveryRequests, bPrepareRecoveryAllowStart);
	PrepareRecoverySurface->HandleNativePublicationPrepareResponse(
		true,
		200,
		TEXT("{malformed"));
	TestEqual(TEXT("Malformed prepare emits no unsafe cancel"), PrepareRecoveryRequests.Num(), 0);
	TestTrue(TEXT("Malformed prepare keeps reservation"), PrepareRecoverySurface->bNativePublicationReservationPending);
	TestTrue(
		TEXT("Malformed prepare exposes retry prepare"),
		PrepareRecoverySurface->bNativePublicationPrepareRecoveryPending);
	PrepareRecoverySurface->SubmitAuthoringTransaction(
		TEXT("{}"),
		TEXT("http://127.0.0.1:4173/api/authoring/source-text"),
		TEXT("must remain blocked"));
	PrepareRecoverySurface->RequestWorkspaceRefresh();
	TestFalse(TEXT("Malformed prepare blocks replacement work"), PrepareRecoverySurface->bPatchRequestPending);
	PrepareRecoverySurface->HandleRetryNativePublicationPrepare();
	TestEqual(TEXT("Retry prepare emits one request"), PrepareRecoveryRequests.Num(), 1);
	if (PrepareRecoveryRequests.Num() == 1)
	{
		TestEqual(TEXT("Retry prepare preserves transaction"), PrepareRecoveryRequests[0].TransactionId, TransactionId);
		TestEqual(TEXT("Retry prepare preserves token"), PrepareRecoveryRequests[0].PreviewToken, Token);
		PrepareRecoverySurface->HandleRetryNativePublicationPrepare();
		TestEqual(TEXT("In-flight prepare cannot be duplicated"), PrepareRecoveryRequests.Num(), 1);
		PrepareRecoveryRequests[0].Completion(true, 200, BuildPrepareResponse(OutputPath));
		TestEqual(TEXT("Recovered prepare then emits cancel"), PrepareRecoveryRequests.Num(), 2);
		if (PrepareRecoveryRequests.Num() == 2)
		{
			PrepareRecoveryRequests[1].Completion(true, 200, BuildCancelAck());
			TestFalse(
				TEXT("Recovered exact cancel ACK releases reservation"),
				PrepareRecoverySurface->bNativePublicationReservationPending);
		}
	}

	TArray<FRequestRecord> StartFailureRequests;
	const TSharedRef<SFinareAuthoringSurface> StartFailureSurface =
		SNew(SFinareAuthoringSurface);
	SeedPendingTransaction(StartFailureSurface);
	bool bStartFailureAllowStart = false;
	InstallTransport(StartFailureSurface, StartFailureRequests, bStartFailureAllowStart);
	StartFailureSurface->DispatchNativePublicationPrepare(Token);
	TestEqual(TEXT("Failed prepare start is recorded once"), StartFailureRequests.Num(), 1);
	TestTrue(TEXT("Failed prepare start keeps reservation"), StartFailureSurface->bNativePublicationReservationPending);
	TestTrue(TEXT("Failed prepare start exposes retry"), StartFailureSurface->bNativePublicationPrepareRecoveryPending);
	TestFalse(
		TEXT("Failed prepare start has no cancel candidate"), StartFailureSurface->PendingNativePublication.IsSet());
	bStartFailureAllowStart = true;
	StartFailureSurface->HandleRetryNativePublicationPrepare();
	TestEqual(TEXT("Failed prepare start retries with same request identity"), StartFailureRequests.Num(), 2);
	if (StartFailureRequests.Num() == 2)
	{
		TestEqual(TEXT("Failed prepare retry preserves transaction"), StartFailureRequests[1].TransactionId,
		          TransactionId);
		TestEqual(TEXT("Failed prepare retry preserves token"), StartFailureRequests[1].PreviewToken, Token);
		StartFailureRequests[1].Completion(true, 200, BuildPrepareResponse(OutputPath));
		TestEqual(TEXT("Successful prepare retry emits cancellation"), StartFailureRequests.Num(), 3);
		if (StartFailureRequests.Num() == 3)
		{
			StartFailureRequests[2].Completion(true, 200, BuildCancelAck());
			TestFalse(
				TEXT("Successful prepare retry can be cancelled"),
				StartFailureSurface->bNativePublicationReservationPending);
		}
	}
	return true;
}
