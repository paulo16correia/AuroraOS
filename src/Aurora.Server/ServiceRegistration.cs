using Aurora.Adapters.Capabilities;
using Aurora.Adapters.Capability;
using Aurora.Adapters.Cognition;
using Aurora.Adapters.Consent;
using Aurora.Adapters.Events;
using Aurora.Adapters.Vault;
using Aurora.Adapters.World;
using Aurora.Adapters.Files;
using Aurora.Adapters.Genomes;
using Aurora.Adapters.Lifecycle;
using Aurora.Adapters.Knowledge;
using Aurora.Adapters.Memories;
using Aurora.Adapters.Observations;
using Aurora.Adapters.MindStates;
using Aurora.Adapters.Observability;
using Aurora.Adapters.Persistence;
using Aurora.Adapters.Pilot;
using Aurora.Adapters.Planning;
using Aurora.Adapters.Policy;
using Aurora.Adapters.Reasoning;
using Aurora.Adapters.Time;
using Aurora.Adapters.Tools;
using Aurora.Adapters.Validation;
using Aurora.Core.Abstractions;
using Aurora.Core.Contracts;
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
        // Vault (RFC 09, step 4). Key outside the database, AES-GCM from the BCL so the same
        // code runs on Windows, macOS and Linux.
        services.AddSingleton(VaultOptions.Default);
        services.AddSingleton(_ => new AesGcmSecretProtector(
            LocalKeyFile.LoadOrCreate(options.VaultKeyPath, "Vault")));
        services.AddSingleton<IVault, SqliteVault>();

        services.AddSingleton<IOutbox, SqliteOutbox>();
        services.AddSingleton<IEventBus, SqliteEventBus>();
        services.AddSingleton(ConsentSessionOptions.Default);
        services.AddSingleton<IConsentSessionStore, SqliteConsentSessionStore>();
        services.AddSingleton<INoteStore, SqliteNoteStore>();

        // Runtime.
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<IAuroraMetrics, InMemoryMetrics>();
        services.AddSingleton<IPrincipalAccessor, LocalPrincipalAccessor>();
        services.AddSingleton<IServerIdentity, ProcessServerIdentity>();
        services.AddSingleton<IInstanceLifecycle, SqliteInstanceLifecycle>();
        services.AddSingleton<IGenomeSigner>(_ => EcdsaGenomeSigner.FromKeyFile(options.GenomeKeyPath));
        services.AddSingleton<IGenomeService, SqliteGenomeService>();
        services.AddSingleton<IMemoryRanker, LexicalMemoryRanker>();
        services.AddSingleton<IMemoryService, SqliteMemoryService>();
        services.AddSingleton<IKnowledgeGraph, SqliteKnowledgeGraph>();
        services.AddSingleton(WorldModelOptions.Default);
        services.AddSingleton<IWorldModel, SqliteWorldModel>();

        // Cognitive cycle, step 7.
        services.AddSingleton(AttentionPolicy.Default);
        services.AddSingleton(WorkingMemoryOptions.Default);
        services.AddSingleton<IAttentionAuthorization, SensitivityAttentionAuthorization>();
        services.AddSingleton<IAttentionSystem, SqliteAttentionSystem>();
        services.AddSingleton<IWorkingMemory, SqliteWorkingMemory>();
        services.AddSingleton<IDecisionEngine, SqliteDecisionEngine>();
        services.AddSingleton<ICognitiveCycle, SqliteCognitiveCycle>();
        services.AddSingleton<ICapabilityResolver, SqliteCapabilityResolver>();
        services.AddSingleton<IToolManager, SqliteToolManager>();
        services.AddSingleton<IObservationService, SqliteObservationService>();

        // The low-risk pilot: the first vertical slice, using no external tool (step 9).
        services.AddSingleton<IPilotApplication, LocalConversationPilot>();

        // One type serves planner, task service and scheduler: they share the same tables and
        // splitting them would mean three objects arguing about the same rows.
        services.AddSingleton<SqlitePlanner>();
        services.AddSingleton<IPlanner>(sp => sp.GetRequiredService<SqlitePlanner>());
        services.AddSingleton<ITaskService>(sp => sp.GetRequiredService<SqlitePlanner>());
        services.AddSingleton<ITaskScheduler>(sp => sp.GetRequiredService<SqlitePlanner>());

        // Snapshots get their own key: a compromised vault key must not also open every
        // Mind State ever captured (docs/adr/0018).
        services.AddSingleton<IMindStateService>(sp => new SqliteMindStateService(
            sp.GetRequiredService<SqliteConnectionFactory>(),
            new AesGcmSecretProtector(LocalKeyFile.LoadOrCreate(options.SnapshotKeyPath, "Snapshot")),
            sp.GetRequiredService<IClock>(),
            sp.GetRequiredService<IAuditStore>(),
            sp.GetRequiredService<IIdempotencyStore>(),
            sp.GetRequiredService<IConsentSessionStore>(),
            sp.GetRequiredService<IInstanceLifecycle>()));

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

        // Every MCP call is reasoned through the cycle rather than executed beside it (RFC 045
        // rule 3). The Kernel stays the sole authority that commits an effect.
        services.AddSingleton<KernelDispatcher>();

        return services;
    }
}
