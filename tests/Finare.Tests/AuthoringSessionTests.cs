using Finare.Cli;
using Finare.CSharp;

namespace Finare.Tests;

[Trait("Category", "Authoring")]
public sealed class AuthoringSessionTests
{
    private const string Source = """
        using System;
        using Finare;

        namespace Tests.DirectAuthoring;

        [ScriptAsset("Tests/DirectAuthoring")]
        public sealed class DirectAuthoring
        {
            public int BaseDamage { get; set; } = 20;

            public int MaxDamage { get; set; } = 999;

            [Visual]
            public float Run(int value)
            {
                float scaled = value * BaseDamage;
                scaled = Math.Clamp(scaled, 0f, MaxDamage);
                return scaled;
            }
        }
        """;

    [Fact]
    public async Task ExpiredPreviewRejectsWithoutChangingTheCommittedSourceOrWorkspace()
    {
        (AuthoringSession session, string directory, string sourcePath) = CreateSession();
        using (session)
        {
            try
            {
                const string logicalPath = "DirectAuthoring.cs";
                AuthoringTransactionContext previewTransaction = Transaction(
                    null,
                    "preview-stale",
                    "Prepare stale edit");
                CSharpSourceTextPatchDocument stalePatch = Patch(logicalPath, Source, Source.Replace(
                    "BaseDamage { get; set; } = 20;",
                    "BaseDamage { get; set; } = 21;",
                    StringComparison.Ordinal));

                AuthoringPreviewResponse preview = await session.PreviewAsync(
                    stalePatch,
                    previewTransaction,
                    CancellationToken.None);

                string committedSource = Source.Replace(
                    "BaseDamage { get; set; } = 20;",
                    "BaseDamage { get; set; } = 22;",
                    StringComparison.Ordinal);
                AuthoringWorkspaceResponse committed = await session.ApplyAsync(
                    Patch(logicalPath, Source, committedSource),
                    Transaction(null, "advance", "Advance workspace"),
                    CancellationToken.None);

                AuthoringProtocolException error = await Assert.ThrowsAsync<AuthoringProtocolException>(() =>
                    session.ApplyAsync(
                        stalePatch,
                        previewTransaction with { PreviewToken = preview.PreviewToken },
                        CancellationToken.None));

                Assert.Equal("UAW1012", error.Code);
                Assert.Equal(committedSource, committed.SourceText);
                Assert.Equal(committedSource, session.Workspace.SourceText);
                Assert.Equal(committedSource, File.ReadAllText(sourcePath));
                Assert.Equal(2, session.Workspace.Revision);
            }
            finally
            {
                Delete(directory);
            }
        }
    }

    [Fact]
    public async Task DuplicateNativeFinishAndCancelAreIdempotentWithoutDuplicateCommits()
    {
        (AuthoringSession session, string directory, string sourcePath) = CreateSession();
        using (session)
        {
            try
            {
                const string logicalPath = "DirectAuthoring.cs";
                string finishedSource = Source.Replace(
                    "BaseDamage { get; set; } = 20;",
                    "BaseDamage { get; set; } = 21;",
                    StringComparison.Ordinal);
                AuthoringTransactionContext finishTransaction = Transaction(
                    1,
                    "native-finish",
                    "Finish native edit");
                AuthoringPreviewResponse finishPreview = await session.PreviewAsync(
                    Patch(logicalPath, Source, finishedSource),
                    finishTransaction,
                    CancellationToken.None);
                AuthoringTransactionContext confirmedFinish = finishTransaction with
                {
                    PreviewToken = finishPreview.PreviewToken,
                };
                NativePublicationPrepareResponse preparedFinish = await session.PrepareNativePublicationAsync(
                    confirmedFinish,
                    CancellationToken.None);
                foreach (NativePublicationOutput output in preparedFinish.Outputs)
                {
                    File.WriteAllBytes(output.Path, output.Content);
                }
                NativePublicationFinishRequest finish = new(finishPreview.PreviewToken, Succeeded: true);

                NativePublicationFinishResponse adopted = await session.FinishNativePublicationAsync(
                    confirmedFinish,
                    finish,
                    CancellationToken.None);
                NativePublicationFinishResponse duplicateFinish = await session.FinishNativePublicationAsync(
                    confirmedFinish,
                    finish,
                    CancellationToken.None);

                Assert.Same(adopted, duplicateFinish);
                Assert.Equal("adopted", duplicateFinish.State);
                Assert.Equal(2, session.Workspace.Revision);
                Assert.Equal(finishedSource, session.Workspace.SourceText);
                Assert.Equal(finishedSource, File.ReadAllText(sourcePath));

                string cancelledSource = finishedSource.Replace(
                    "BaseDamage { get; set; } = 21;",
                    "BaseDamage { get; set; } = 22;",
                    StringComparison.Ordinal);
                AuthoringTransactionContext cancelTransaction = Transaction(
                    2,
                    "native-cancel",
                    "Cancel native edit");
                AuthoringPreviewResponse cancelPreview = await session.PreviewAsync(
                    Patch(logicalPath, finishedSource, cancelledSource),
                    cancelTransaction,
                    CancellationToken.None);
                AuthoringTransactionContext confirmedCancel = cancelTransaction with
                {
                    PreviewToken = cancelPreview.PreviewToken,
                };
                _ = await session.PrepareNativePublicationAsync(
                    confirmedCancel,
                    CancellationToken.None);

                NativePublicationFinishResponse cancelled = await session.CancelNativePublicationAsync(
                    confirmedCancel,
                    CancellationToken.None);
                NativePublicationFinishResponse duplicateCancel = await session.CancelNativePublicationAsync(
                    confirmedCancel,
                    CancellationToken.None);

                Assert.Same(cancelled, duplicateCancel);
                Assert.Equal("cancelled", duplicateCancel.State);
                Assert.Equal(2, session.Workspace.Revision);
                Assert.Equal(finishedSource, session.Workspace.SourceText);
                Assert.Equal(finishedSource, File.ReadAllText(sourcePath));
            }
            finally
            {
                Delete(directory);
            }
        }
    }

    private static (AuthoringSession Session, string Directory, string SourcePath) CreateSession()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "Finare.AuthoringSession",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string sourcePath = Path.Combine(directory, "DirectAuthoring.cs");
        File.WriteAllText(sourcePath, Source);
        CSharpCompilationRequest request = CSharpCompilationRequest.Single(Source, "DirectAuthoring.cs");
        AuthoringSession session = new(
            request,
            sourcePath,
            identityPath: null,
            new AuthoringArtifactOutputs(),
            sourceFiles: new Dictionary<string, string>
            {
                ["DirectAuthoring.cs"] = sourcePath,
            },
            readOnlySources: new HashSet<string>(StringComparer.Ordinal),
            refreshProject: null,
            identityFiles: null);
        return (session, directory, sourcePath);
    }

    private static AuthoringTransactionContext Transaction(
        long? baselineRevision,
        string transactionId,
        string description) => new(
        baselineRevision,
        transactionId,
        description,
        GroupId: null,
        PreviewToken: null,
        BoundPayloadId: null);

    private static CSharpSourceTextPatchDocument Patch(
        string logicalPath,
        string before,
        string after) => CSharpSourceTextPatchDocument.Create(logicalPath, before, after);

    private static void Delete(string directory)
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
