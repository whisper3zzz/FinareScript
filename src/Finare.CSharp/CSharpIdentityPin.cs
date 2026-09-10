using Finare.Core;

namespace Finare.CSharp;

public sealed record CSharpIdentityPin(
    SemanticId Id,
    string Kind,
    SourceSpan CurrentSpan);
