using System.Runtime.CompilerServices;
using Aurora.Server;

namespace Aurora.Tests.Support;

/// <summary>
/// The test suite never writes to the owner's installation (docs/adr/0083).
/// </summary>
/// <remarks>
/// Unconfigured paths default to <c>%LOCALAPPDATA%\Aurora</c>, which is right for a server
/// somebody started and wrong for a test: the same default aims a test at the live database, the
/// live keys, the live sandbox and the passphrase file that holds the operator's lockout counter.
/// <para>
/// Armed here rather than asserted in each test, because a rule that has to be remembered in a
/// hundred places is a rule that will be forgotten in one. With it set, a test that does not say
/// where its data goes fails with a message saying so — which is the whole point: the failure is
/// loud, and it is not a silent write to somebody's running installation.
/// </para>
/// </remarks>
internal static class DeploymentIsolation
{
    [ModuleInitializer]
    internal static void Arm() =>
        Environment.SetEnvironmentVariable(
            AuroraServerOptions.RequireExplicitPathsVariable, "1");
}
