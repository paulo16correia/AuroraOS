using Aurora.Core.Abstractions;
using Aurora.Core.Contracts;

namespace Aurora.Server.Security;

/// <summary>
/// A single local principal. ClientId is a stable label for the local MCP client; the second
/// component is the interactive OS user, read portably via <see cref="Environment.UserName"/>.
/// <para>
/// Named Local rather than Windows on purpose: the repository targets net10.0 and runs on
/// Windows, macOS and Linux. The <c>Principal.WindowsUser</c> member still carries the older
/// name and reaches the audit schema, so renaming it is a migration rather than a rename and is
/// tracked separately.
/// </para>
/// </summary>
public sealed class LocalPrincipalAccessor : IPrincipalAccessor
{
    public Principal Current { get; } = new("local-mcp-client", Environment.UserName);
}
