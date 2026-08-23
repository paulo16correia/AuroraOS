using Aurora.Adapters.Capabilities;
using Aurora.Adapters.Consent;
using Aurora.Adapters.Files;
using Aurora.Adapters.Persistence;
using Aurora.Adapters.Policy;
using Aurora.Adapters.Reasoning;
using Aurora.Adapters.Time;
using Aurora.Adapters.Validation;
using Aurora.Core.Abstractions;
using Aurora.Core.Kernel;
using Aurora.Server.Security;

namespace Aurora.Server;

/// <summary>Composes the Aurora pipeline: Core kernel + all adapters, wired fail-closed by default.</summary>
public static class ServiceRegistration
{
    public static IServiceCollection AddAurora(this IServiceCollection services, AuroraServerOptions options)
    {
        services.AddSingleton(options);

        // Persistence (single SQLite database, WAL, migrated at startup).
        services.AddSingleton(new SqliteConnectionFactory(options.DbPath));
        services.AddSingleton<SqliteDatabase>();
        services.AddSingleton<IAuditStore, SqliteAuditStore>();
        services.AddSingleton<IIdempotencyStore, SqliteIdempotencyStore>();
        services.AddSingleton<IApprovalStore, SqliteApprovalStore>();
        services.AddSingleton<INoteStore, SqliteNoteStore>();

        // Runtime.
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<IPrincipalAccessor, WindowsPrincipalAccessor>();

        // Domain adapters.
        services.AddSingleton<ISchemaValidator, JsonSchemaValidator>();
        services.AddSingleton<IPolicyEngine, AllowlistPolicyEngine>();
        services.AddSingleton<IConsentGate, PersistentApprovalConsentGate>();
        services.AddSingleton<IReasoner, NullReasoner>();
        services.AddSingleton<ISandboxFileWriter>(_ => new SandboxFileWriter(options.SandboxRoot));
        services.AddSingleton<ICapability, ClockNowCapability>();
        services.AddSingleton<ICapability, EchoSayCapability>();
        services.AddSingleton<ICapability, RememberNoteCapability>();
        services.AddSingleton<ICapability, RecallNotesCapability>();
        services.AddSingleton<ICapability, WriteSandboxFileCapability>();
        services.AddSingleton<ICapabilityRegistry, StaticCapabilityRegistry>();
        services.AddSingleton<ICapabilityExecutor, CapabilityExecutor>();

        // Kernel.
        services.AddSingleton<AuroraKernel>();

        return services;
    }
}
