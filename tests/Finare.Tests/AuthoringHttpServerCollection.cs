namespace Finare.Tests;

// Real HttpListener instances bind loopback ports.  Keep the two test classes
// that reserve those ports in one collection while allowing other suites to
// continue in parallel.
[CollectionDefinition("Authoring HTTP servers")]
public sealed class AuthoringHttpServerCollection
{
}
