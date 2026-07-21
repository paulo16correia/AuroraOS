using Aurora.Core.Abstractions;
using Aurora.Core.Contracts;

namespace Aurora.Server.Security;

/// <summary>
/// It.0 principal: a single local principal. ClientId is a stable label for the local MCP client;
/// WindowsUser is the interactive OS user. (It.1+ may derive ClientId from the authenticated client.)
/// </summary>
public sealed class WindowsPrincipalAccessor : IPrincipalAccessor
{
    public Principal Current { get; } = new("local-mcp-client", Environment.UserName);
}
