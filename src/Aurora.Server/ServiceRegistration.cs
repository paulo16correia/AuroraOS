using Aurora.Adapters.Capabilities;
using Aurora.Adapters.Consent;
using Aurora.Adapters.Events;
using Aurora.Adapters.Files;
using Aurora.Adapters.Observability;
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
        // The audit signing key and head anchor live outside the database on purpose, so write
        // access to the .db alone cannot forge or silently shorten the chain (docs/adr/0005).
        services.AddSingleton<IAuditStore>(sp => new SqliteAuditStore(
            sp.GetRequiredService<SqliteConnectionFactory>(),
            sp.GetRequiredService<IClock>(),
            AuditKeyFile.LoadOrCreate(options.AuditKeyPath),
            new AuditAnchorFile(options.AuditAnchorPath)));
        services.AddSingleton<IIdempotencyStore, SqliteIdempotencyStore>();
        services.AddSingleton<IApprovalStore, SqliteApprovalStore>();

        // Event Bus (RFC 050, step 3 of the frozen implementation order).
        services.AddSingleton<IOutbox, SqliteOutbox>();
        services.AddSingleton<IEventBus, SqliteEventBus>();
        services.AddSingleton(ConsentSessionOptions.Default);
        services.AddSingleton<IConsentSessionStore, SqliteConsentSessionStore>();
        services.AddSingleton<INoteStore, SqliteNoteStore>();

        // Runtime.
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<IAuroraMetrics, InMemoryMetrics>();
        services.AddSingleton<IPrincipalAccessor, WindowsPrincipalAccessor>();
        services.AddSingleton<IServerIdentity, ProcessServerIdentity>();

        // Domain adapters.
        services.AddSingleton<ISchemaValidator, JsonSchemaValidator>();
        services.AddSingleton<IPolicyEngine, AllowlistPolicyEngine>();
        services.AddSingleton<IConsentGate, SessionAwareConsentGate>();
        services.AddSingleton<IPassphraseAuthenticator>(sp => new Pbkdf2PassphraseAuthenticator(
            options.PassphrasePath, sp.GetRequiredService<IClock>(), PassphraseOptions.Default));
        // Untrusted proposers, tried in order. The kernel commits, never the reasoner.
        services.AddHttpClient();
        services.AddSingleton<IReasoner>(sp =>
        {
            var proposers = new List<IReasoner>();
            if (options.AzureOpenAi is { } azure)
            {
                var factory = sp.GetRequiredService<IHttpClientFactory>();
                proposers.Add(new AzureOpenAiReasoner(factory.CreateClient("azure-openai"), azure));
            }

            proposers.Add(new KeywordReasoner());
            return new CompositeReasoner(proposers);
        });
        services.AddSingleton<ISandboxFileWriter>(_ => new SandboxFileWriter(options.SandboxRoot));
        services.AddSingleton<ISandboxFileReader>(_ => new SandboxFileReader(options.SandboxRoot));
        services.AddSingleton<ICapability, ClockNowCapability>();
        services.AddSingleton<ICapability, EchoSayCapability>();
        services.AddSingleton<ICapability, RememberNoteCapability>();
        services.AddSingleton<ICapability, RecallNotesCapability>();
        // Frozen by the re-baseline (docs/adr/0012): filesystem capabilities are step 8 of the
        // frozen implementation order and were built before steps 3-7 existed. Off by default.
        if (options.SandboxFilesEnabled)
        {
            services.AddSingleton<ICapability, WriteSandboxFileCapability>();
            services.AddSingleton<ICapability, ReadSandboxFileCapability>();
        }
        services.AddSingleton<ICapabilityRegistry, StaticCapabilityRegistry>();
        services.AddSingleton<ICapabilityExecutor, CapabilityExecutor>();

        // Kernel.
        services.AddSingleton<AuroraKernel>();

        return services;
    }
}
