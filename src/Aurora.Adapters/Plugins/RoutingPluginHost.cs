using Aurora.Core.Abstractions;
using Aurora.Core.Contracts;

namespace Aurora.Adapters.Plugins;

/// <summary>
/// Sends each call to the host that suits the plugin: one-shot, or the long-lived one.
/// </summary>
/// <remarks>
/// The two hosts are not interchangeable and the difference is not the caller's business. A plugin
/// that answers a question is started, written to, read from and waited on; a plugin that holds a
/// connection is started once and spoken to over the pipes it keeps. The manifest says which it is,
/// so nothing above this has to know.
/// <para>
/// Without this the registry holds one host and a service plugin gets the wrong one — started,
/// handed a call, and then having its stdin closed underneath it, which ends the loop it reads
/// from and makes every call fail for a reason that has nothing to do with the plugin.
/// </para>
/// </remarks>
public sealed class RoutingPluginHost : IPluginHost, IAsyncDisposable, IDisposable
{
    private readonly IPluginHost _oneShot;
    private readonly ServicePluginHost _services;

    public RoutingPluginHost(IPluginHost oneShot, ServicePluginHost services)
    {
        _oneShot = oneShot;
        _services = services;
    }

    public Task<PluginResult> InvokeAsync(
        PluginManifest manifest, PluginInvocation invocation, CancellationToken ct) =>
        manifest.Service is null
            ? _oneShot.InvokeAsync(manifest, invocation, ct)
            : _services.InvokeAsync(manifest, invocation, ct);

    public void Dispose() => _services.Dispose();

    public async ValueTask DisposeAsync() => await _services.DisposeAsync().ConfigureAwait(false);
}
