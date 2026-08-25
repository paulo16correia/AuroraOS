using Aurora.Adapters.Applications;
using Aurora.Adapters.Beliefs;
using Aurora.Adapters.Capabilities;
using Aurora.Adapters.Capability;
using Aurora.Adapters.Cognition;
using Aurora.Adapters.Consent;
using Aurora.Adapters.Development;
using Aurora.Adapters.Deliberation;
using Aurora.Adapters.Curiosity;
using Aurora.Adapters.Events;
using Aurora.Adapters.Vault;
using Aurora.Adapters.World;
using Aurora.Adapters.Files;
using Aurora.Adapters.Genomes;
using Aurora.Adapters.Lifecycle;
using Aurora.Adapters.Knowledge;
using Aurora.Adapters.LifeHistory;
using Aurora.Adapters.Memories;
using Aurora.Adapters.Observations;
using Aurora.Adapters.Operations;
using Aurora.Adapters.MindStates;
using Aurora.Adapters.Observability;
using Aurora.Adapters.Personality;
using Aurora.Adapters.Persistence;
using Aurora.Adapters.Pilot;
using Aurora.Adapters.Maintenance;
using Aurora.Adapters.Missions;
using Aurora.Adapters.Needs;
using Aurora.Adapters.Resources;
using Aurora.Adapters.Retention;
using Aurora.Adapters.Situation;
using Aurora.Adapters.Scheduling;
using Aurora.Adapters.Self;
using Aurora.Adapters.Signals;
using Aurora.Adapters.Planning;
using Aurora.Adapters.Plugins;
using Aurora.Adapters.Policy;
using Aurora.Adapters.Reasoning;
using Aurora.Adapters.Relationships;
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

        services.AddSingleton<IEventCatalogue, DeclaredEventCatalogue>();
        services.AddSingleton<IOutbox, SqliteOutbox>();
        services.AddSingleton<IEventBus, SqliteEventBus>();
        services.AddSingleton(ConsentSessionOptions.Default);
        services.AddSingleton<IConsentSessionStore, SqliteConsentSessionStore>();
        services.AddSingleton<INoteStore, SqliteNoteStore>();

        // Runtime.
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<IAuroraMetrics, InMemoryMetrics>();
        services.AddSingleton<IPrincipalAccessor, LocalPrincipalAccessor>();

        // The person's credential, which the agent does not hold (RFC 11).
        services.AddSingleton<OperatorSessions>();

        // Operations (RFC 12): can this build serve traffic, and can its clock be trusted.
        // Internal deliberation (RFC 025), with its own key: the trace is protected technical
        // material kept for a week, and the vault's secrets are kept indefinitely. Sharing a key
        // would mean they stand or fall together.
        services.AddSingleton<IDeliberationService>(sp => new SqliteDeliberationService(
            sp.GetRequiredService<SqliteConnectionFactory>(),
            sp.GetRequiredService<ICognitiveCycle>(),
            new AesGcmSecretProtector(
                LocalKeyFile.LoadOrCreate(options.DeliberationKeyPath, "Deliberation")),
            sp.GetRequiredService<IClock>()));

        // Patterns Aurora thinks it sees, kept apart from the memories it saw them in (RFC 028).
        services.AddSingleton(BeliefPolicy.Default);
        services.AddSingleton<IBeliefSystem, SqliteBeliefSystem>();

        // Who has a tie to whom, and how the person likes things done. Neither is a permission,
        // and nothing here can turn one into one (RFC 029).
        services.AddSingleton<IRelationshipModel, SqliteRelationshipModel>();

        services.AddSingleton<IClockGuard, AuditClockGuard>();
        services.AddSingleton<IHealthService, AuroraHealthService>();

        // What Aurora knows about itself, observed rather than assumed. Installed, permitted and
        // safe-right-now are three separate answers here, and none implies another (RFC 027).
        services.AddSingleton<ISelfModel, SqliteSelfModel>();

        // How Aurora sounds, versioned and auditable — kept here rather than in a prompt, so an
        // informal instruction cannot become an invisible rule nobody can find (RFC 07).
        services.AddSingleton<IPersonalityService, SqlitePersonalityService>();
        services.AddSingleton<IComposer, MessageComposer>();

        // Operational maturity, earned rather than accrued. A stage changes how much of Aurora's
        // own caution sits on top of the rules, and never the rules (RFC 037).
        services.AddSingleton(SqliteDevelopmentModel.DefaultProfile);
        services.AddSingleton<IDevelopmentModel, SqliteDevelopmentModel>();

        // What happened to this instance, cited rather than recalled (RFC 038).
        services.AddSingleton<ILifeHistory, SqliteLifeHistory>();

        // Third parties on a security contract rather than as a privileged exception (RFC 060).
        // The host runs them out of process; ADR 0048 states exactly what that does and does not
        // isolate, because rule 2 is the rule most easily claimed and least easily kept.
        services.AddSingleton<IPluginHost>(_ => new SubprocessPluginHost(options.PluginRoot));
        services.AddSingleton<IPluginRegistry>(sp => new SqlitePluginRegistry(
            sp.GetRequiredService<SqliteConnectionFactory>(),
            sp.GetRequiredService<IPluginHost>(),
            sp.GetRequiredService<IEventBus>(),
            LocalKeyFile.LoadOrCreate(options.PluginKeyPath, "Plugin"),
            sp.GetRequiredService<IClock>()));
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

        // Rhythm, with no authority of its own: a tick produces due runs and events, and what
        // answers them goes through the cycle like anything else (RFC 026).
        services.AddSingleton<IScheduler, SqliteScheduler>();

        // What deserves attention, and what is waiting on Aurora. Neither grants any authority:
        // both change order and focus, and the cycle still decides what may happen (RFC 030, 031).
        services.AddSingleton<ISignalService, SqliteSignalService>();
        services.AddSingleton<INeedsService, SqliteNeedsService>();

        // Real capacity, the moment it is being asked in, and the upkeep that keeps both current.
        // None of the three permits anything; they can only make Aurora quieter or more careful.
        services.AddSingleton(QuietHours.Default);
        services.AddSingleton<IResourceProbe, SystemResourceProbe>();
        services.AddSingleton<IResourceModel, SystemResourceModel>();
        services.AddSingleton<ISituationService, SituationService>();
        // Ninety days of cycles, thirty of the rest. Working by-products only: ADR 0031 and 0033
        // recorded that these grow without bound, and the audit chain is deliberately out of reach.
        services.AddSingleton(RetentionPolicy.Default);
        services.AddSingleton<IRetentionService, SqliteRetentionService>();
        services.AddSingleton<IMaintenanceService, MaintenanceService>();

        // What Aurora is for, decided by the person it is for (RFC 052).
        services.AddSingleton<IMissionService, SqliteMissionService>();

        // Curiosity, and the allowlist it is confined to. The default reaches Aurora's own records
        // and nothing further; widening it is a deployment decision, never a default (RFC 032).
        services.AddSingleton(CuriosityPolicy.Default);
        services.AddSingleton<ICuriosityEngine, SqliteCuriosityEngine>();

        // The second application the frozen order allows: low-risk, reading-only, no external
        // tool. The rest of the system is only as governed as it is legible.
        services.AddSingleton<IReviewApplication, DailyReviewApplication>();

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
        // Unfrozen by the owner's decision (docs/adr/0037). Both are MEDIUM and approval-gated, so
        // being in the catalog is not permission to use them: every call still needs a persisted
        // approval scoped to that exact input. Aurora offering to read a file and Aurora reading
        // one are different events, and only the first of them happens without being asked.
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
