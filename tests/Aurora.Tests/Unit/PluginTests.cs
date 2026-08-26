using System.Globalization;
using Aurora.Adapters.Events;
using Aurora.Adapters.Persistence;
using Aurora.Adapters.Plugins;
using Aurora.Core.Abstractions;
using Aurora.Core.Contracts;
using Aurora.Tests.Support;
using Xunit;

namespace Aurora.Tests.Unit;

/// <summary>
/// The plugin SDK (RFC 060): extensibility as a security contract.
/// </summary>
/// <remarks>
/// The RFC's own justification is the test plan: a plugin for one thing can be useful without
/// gaining powers over email, SSH or the Mind. Everything here checks that a declaration stays a
/// limit rather than becoming an authority.
/// </remarks>
public sealed class PluginTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly byte[] Key = Enumerable.Repeat((byte)9, 32).ToArray();

    private static DateTimeOffset At(string iso) =>
        DateTimeOffset.Parse(iso, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    /// <summary>A host that returns whatever the test says the plugin returned.</summary>
    private sealed class ScriptedHost : IPluginHost
    {
        public string? Output { get; set; } = """{"said":"hello"}""";

        public bool Succeed { get; set; } = true;

        public int Invocations { get; private set; }

        public Task<PluginResult> InvokeAsync(
            PluginManifest manifest, PluginInvocation invocation, CancellationToken ct)
        {
            Invocations++;

            return Task.FromResult(Succeed
                ? new PluginResult(true, Output, null, "completed", 1)
                : new PluginResult(false, null, "nonzero_exit", "exited 1", 1));
        }
    }

    private static (SqlitePluginRegistry Registry, ScriptedHost Host, SqliteEventBus Bus) Build(
        SqliteTestDb db, string now = "2026-01-15T09:00:00+00:00")
    {
        var clock = new TestClock(At(now));
        var host = new ScriptedHost();
        var bus = new SqliteEventBus(db.Factory, new SqliteOutbox(new DeclaredEventCatalogue(), clock), clock);

        return (new SqlitePluginRegistry(db.Factory, host, bus, Key, clock), host, bus);
    }

    /// <summary>A manifest that is internally consistent: signed, hashed, and declaring its limits.</summary>
    private static PluginManifest Manifest(
        string publisher = "acme",
        string maxDataClass = Sensitivity.Private,
        IReadOnlyList<string>? permissions = null,
        IReadOnlyList<string>? subscriptions = null)
    {
        var draft = new PluginManifest(
            "plugin/notes", "1.0.0", publisher, "", MinPlatformVersion: 1,
            Capabilities:
            [
                new PluginCapability(
                    "notes.append", "{}", "{}", ["notes.write"], ApprovalRequired: true,
                    RateLimitPerMinute: 30, Timeout: TimeSpan.FromSeconds(5),
                    IdempotencySupport: true, AuditLevel: "FULL"),
            ],
            EventSubscriptions: subscriptions ?? [],
            RequiredPermissions: permissions ?? ["notes.write"],
            MaxDataClass: maxDataClass,
            NetworkEndpoints: [],
            DocumentationRef: "docs/plugin/notes",
            IntegrityHash: "");

        draft = draft with
        {
            Signature = SqlitePluginRegistry.Sign(Key, draft.PluginId, draft.Version, draft.Publisher),
        };

        return draft with { IntegrityHash = SqlitePluginRegistry.HashOf(draft) };
    }

    private static PluginInvocation Call(string capability = "notes.append", string dataClass = Sensitivity.Private) =>
        new("plugin/notes", capability, """{"note":"hello"}""", dataClass);

    // ---- verification happens before anything runs ----

    [Fact]
    public async Task AnUnsignedManifestIsRefusedBeforeInstalling()
    {
        using var db = new SqliteTestDb();
        var (registry, host, _) = Build(db);

        PluginManifest forged = Manifest() with { Signature = "not-a-signature" };

        PluginVerification verification = await registry.VerifyAsync(forged, Ct);

        Assert.False(verification.Ok);
        Assert.Contains(PluginRefusal.SignatureInvalid, verification.Refusals);

        // It does not even run in preview: there is no mode in which unattributable code executes.
        await Assert.ThrowsAsync<PluginException>(() =>
            registry.InstallAsync(forged, ["notes.write"], "approval/1", Ct));

        Assert.Equal(0, host.Invocations);
    }

    [Fact]
    public async Task AManifestEditedAfterSigningFailsItsHash()
    {
        using var db = new SqliteTestDb();
        var (registry, _, _) = Build(db);

        // The signature covers identity; the hash covers everything declared. Widening the data
        // class after signing changes what the plugin may be handed, and the hash catches it.
        PluginManifest tampered = Manifest() with { MaxDataClass = Sensitivity.Secret };

        PluginVerification verification = await registry.VerifyAsync(tampered, Ct);

        Assert.False(verification.Ok);
        Assert.Contains(PluginRefusal.IntegrityMismatch, verification.Refusals);
    }

    [Fact]
    public async Task APluginBuiltForALaterPlatformIsRefused()
    {
        using var db = new SqliteTestDb();
        var (registry, _, _) = Build(db);

        PluginManifest future = Manifest() with { MinPlatformVersion = 99 };
        future = future with
        {
            Signature = SqlitePluginRegistry.Sign(Key, future.PluginId, future.Version, future.Publisher),
        };
        future = future with { IntegrityHash = SqlitePluginRegistry.HashOf(future) };

        Assert.Contains(
            PluginRefusal.PlatformTooOld, (await registry.VerifyAsync(future, Ct)).Refusals);
    }

    // ---- rule 3: installation is a review somebody did ----

    [Fact]
    public async Task InstallingNeedsAnApprovalAndGrantsOnlyWhatWasAskedFor()
    {
        using var db = new SqliteTestDb();
        var (registry, _, _) = Build(db);
        PluginManifest manifest = Manifest();

        await Assert.ThrowsAsync<PluginException>(() =>
            registry.InstallAsync(manifest, ["notes.write"], "", Ct));

        // Granting more than was asked for is how a review stops meaning anything.
        PluginInstallation installed = await registry.InstallAsync(
            manifest, ["notes.write", "ssh.execute", "mail.send"], "approval/1", Ct);

        Assert.Equal(["notes.write"], installed.GrantedPermissions);
    }

    // ---- rule 1: an undeclared request is denied ----

    [Fact]
    public async Task ACapabilityTheManifestDidNotDeclareIsDenied()
    {
        using var db = new SqliteTestDb();
        var (registry, host, _) = Build(db);
        await registry.InstallAsync(Manifest(), ["notes.write"], "approval/1", Ct);

        PluginResult result = await registry.InvokeAsync(Call("mail.send"), Ct);

        // Not "unsupported" — denied. The manifest is the whole of what this plugin was reviewed
        // to do, and it never reaches the host to find out.
        Assert.False(result.Ok);
        Assert.Equal(PluginRefusal.UndeclaredEffect, result.Refusal);
        Assert.Equal(0, host.Invocations);
    }

    [Fact]
    public async Task ACapabilityTheManifestDeclaredRuns()
    {
        using var db = new SqliteTestDb();
        var (registry, host, _) = Build(db);
        await registry.InstallAsync(Manifest(), ["notes.write"], "approval/1", Ct);

        PluginResult result = await registry.InvokeAsync(Call(), Ct);

        Assert.True(result.Ok);
        Assert.Equal(1, host.Invocations);
    }

    // ---- rule 4: never handed data above what it declared ----

    [Fact]
    public async Task APluginIsNeverHandedDataAboveTheClassItDeclared()
    {
        using var db = new SqliteTestDb();
        var (registry, host, _) = Build(db);
        await registry.InstallAsync(
            Manifest(maxDataClass: Sensitivity.Public), ["notes.write"], "approval/1", Ct);

        PluginResult result = await registry.InvokeAsync(Call(dataClass: Sensitivity.Confidential), Ct);

        // Checked before the call, because afterwards the plugin has already seen it.
        Assert.False(result.Ok);
        Assert.Equal(PluginRefusal.AboveDeclaredClassification, result.Refusal);
        Assert.Equal(0, host.Invocations);
    }

    [Fact]
    public async Task ASubscriptionIsARequestAndTheAnswerIsFilteredByWhatItMaySee()
    {
        using var db = new SqliteTestDb();
        var (registry, _, _) = Build(db);

        PluginManifest asking = Manifest(
            maxDataClass: Sensitivity.Public,
            subscriptions: [EventCatalogue.JobDue, EventCatalogue.ApprovalDecided, "NoSuchEvent"]);

        asking = asking with
        {
            Signature = SqlitePluginRegistry.Sign(Key, asking.PluginId, asking.Version, asking.Publisher),
        };
        asking = asking with { IntegrityHash = SqlitePluginRegistry.HashOf(asking) };

        await registry.InstallAsync(asking, ["notes.write"], "approval/1", Ct);

        IReadOnlyList<string> permitted = await registry.PermittedSubscriptionsAsync("plugin/notes", Ct);

        // Both declared events are PRIVATE and this plugin declared PUBLIC, so it receives neither
        // — and an event nobody declared at all is not a subscription anybody can hold.
        Assert.Empty(permitted);
    }

    // ---- rule 5: an update that changes the deal goes to quarantine ----

    [Fact]
    public async Task AnUpdateFromANewPublisherIsQuarantined()
    {
        using var db = new SqliteTestDb();
        var (registry, _, _) = Build(db);
        await registry.InstallAsync(Manifest(), ["notes.write"], "approval/1", Ct);

        PluginManifest fromElsewhere = Manifest(publisher: "somebody-else") with { Version = "2.0.0" };
        fromElsewhere = fromElsewhere with
        {
            Signature = SqlitePluginRegistry.Sign(
                Key, fromElsewhere.PluginId, fromElsewhere.Version, fromElsewhere.Publisher),
        };
        fromElsewhere = fromElsewhere with { IntegrityHash = SqlitePluginRegistry.HashOf(fromElsewhere) };

        PluginInstallation updated = await registry.UpdateAsync(fromElsewhere, Ct);

        // A new publisher is a different party behind the same name. The previous decision was not
        // about them.
        Assert.Equal(InstallationStatus.Quarantined, updated.Status);
        Assert.Contains(PluginRefusal.NewPublisher, updated.QuarantineReason!, StringComparison.Ordinal);

        Assert.False((await registry.InvokeAsync(Call(), Ct)).Ok);
    }

    [Fact]
    public async Task AnUpdateAskingForMorePermissionsIsQuarantined()
    {
        using var db = new SqliteTestDb();
        var (registry, _, _) = Build(db);
        await registry.InstallAsync(Manifest(), ["notes.write"], "approval/1", Ct);

        PluginManifest greedier = Manifest(permissions: ["notes.write", "mail.send"]) with { Version = "1.1.0" };
        greedier = greedier with
        {
            Signature = SqlitePluginRegistry.Sign(Key, greedier.PluginId, greedier.Version, greedier.Publisher),
        };
        greedier = greedier with { IntegrityHash = SqlitePluginRegistry.HashOf(greedier) };

        PluginInstallation updated = await registry.UpdateAsync(greedier, Ct);

        Assert.Equal(InstallationStatus.Quarantined, updated.Status);
        Assert.Contains("mail.send", updated.QuarantineReason!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReleasingAQuarantineIsADecisionAndNeedsAnApproval()
    {
        using var db = new SqliteTestDb();
        var (registry, _, _) = Build(db);
        PluginInstallation installed = await registry.InstallAsync(
            Manifest(), ["notes.write"], "approval/1", Ct);

        PluginManifest greedier = Manifest(permissions: ["notes.write", "mail.send"]) with { Version = "1.1.0" };
        greedier = greedier with
        {
            Signature = SqlitePluginRegistry.Sign(Key, greedier.PluginId, greedier.Version, greedier.Publisher),
        };
        greedier = greedier with { IntegrityHash = SqlitePluginRegistry.HashOf(greedier) };
        await registry.UpdateAsync(greedier, Ct);

        await Assert.ThrowsAsync<PluginException>(() =>
            registry.ReleaseAsync(installed.Id, "", "paulo", Ct));

        // A quarantine ends because somebody looked and decided, not because time passed.
        PluginInstallation released = await registry.ReleaseAsync(
            installed.Id, "approval/2", "paulo", Ct);

        Assert.Equal(InstallationStatus.Installed, released.Status);
    }

    // ---- limit case: a plugin that keeps failing stops being asked ----

    [Fact]
    public async Task APluginThatKeepsFailingIsQuarantinedRatherThanRetriedForever()
    {
        using var db = new SqliteTestDb();
        var (registry, host, _) = Build(db);
        await registry.InstallAsync(Manifest(), ["notes.write"], "approval/1", Ct);

        host.Succeed = false;

        for (var i = 0; i < 3; i++)
        {
            Assert.False((await registry.InvokeAsync(Call(), Ct)).Ok);
        }

        PluginInstallation installation = (await registry.GetAsync("plugin/notes", Ct))!;

        Assert.Equal(InstallationStatus.Quarantined, installation.Status);
        Assert.Contains(PluginRefusal.CircuitOpen, installation.QuarantineReason!, StringComparison.Ordinal);

        // And it stops being asked, which is the point of a circuit.
        var before = host.Invocations;
        await registry.InvokeAsync(Call(), Ct);
        Assert.Equal(before, host.Invocations);
    }

    [Fact]
    public async Task ASuccessClearsTheRunOfFailures()
    {
        using var db = new SqliteTestDb();
        var (registry, host, _) = Build(db);
        await registry.InstallAsync(Manifest(), ["notes.write"], "approval/1", Ct);

        host.Succeed = false;
        await registry.InvokeAsync(Call(), Ct);
        await registry.InvokeAsync(Call(), Ct);

        host.Succeed = true;
        await registry.InvokeAsync(Call(), Ct);

        // A bad moment is not a broken plugin.
        Assert.Equal(0, (await registry.GetAsync("plugin/notes", Ct))!.ConsecutiveFailures);
    }

    // ---- limit case: a plugin returning a secret ----

    [Theory]
    [InlineData("""{"result":"Bearer abcdefghijklmnopqrstuvwxyz012345"}""")]
    [InlineData("""{"api_key":"sk-verylongsecretvalue"}""")]
    [InlineData("-----BEGIN RSA PRIVATE KEY-----\nMIIE...")]
    public async Task APluginReturningSomethingThatLooksLikeACredentialIsHeldAndItsOutputDropped(
        string output)
    {
        using var db = new SqliteTestDb();
        var (registry, host, bus) = Build(db);
        await registry.InstallAsync(Manifest(), ["notes.write"], "approval/1", Ct);

        host.Output = output;

        PluginResult result = await registry.InvokeAsync(Call(), Ct);

        // Withheld, not returned and then flagged. Whether it was malice or an accident, the next
        // call is not made until somebody looks.
        Assert.False(result.Ok);
        Assert.Equal(PluginRefusal.SecretInOutput, result.Refusal);
        Assert.Null(result.OutputJson);

        PluginInstallation held = (await registry.GetAsync("plugin/notes", Ct))!;
        Assert.Equal(InstallationStatus.Quarantined, held.Status);

        // And it is a security event, not just a row somebody would have to know to look at.
        IReadOnlyList<SequencedEvent> published = await bus.ReadAsync(0, 50, Sensitivity.Private, Ct);
        Assert.Contains(published, e => e.Event.Type == EventCatalogue.PluginQuarantined);
    }

    [Fact]
    public async Task ADisabledPluginDoesNotRun()
    {
        using var db = new SqliteTestDb();
        var (registry, host, _) = Build(db);
        PluginInstallation installed = await registry.InstallAsync(
            Manifest(), ["notes.write"], "approval/1", Ct);

        await registry.DisableAsync(installed.Id, "paulo", Ct);

        Assert.False((await registry.InvokeAsync(Call(), Ct)).Ok);
        Assert.Equal(0, host.Invocations);
    }

    // ---- the host actually leaves this process ----

    [Fact]
    public async Task ThePluginRunsInItsOwnProcessAndInheritsNothingOfAuroraS()
    {
        if (OperatingSystem.IsWindows())
        {
            // Needs a POSIX shell to write a plugin in three lines. The host is the same code on
            // every platform; this exercises it where the setup is honest.
            return;
        }

        var root = TestTemp.Path("plug");
        var script = Path.Combine(root, "echo-env.sh");
        Directory.CreateDirectory(root);

        // The plugin reports what it can see: its own environment.
        await File.WriteAllTextAsync(
            script,
            "#!/bin/sh\ncat > /dev/null\nprintf '{\"secret_seen\":\"%s\",\"id\":\"%s\"}' " +
            "\"${AURORA_VAULT_KEY_PATH:-none}\" \"${AURORA_PLUGIN_ID:-none}\"\n",
            Ct);

        File.SetUnixFileMode(
            script,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        // Something Aurora holds that must not travel.
        Environment.SetEnvironmentVariable("AURORA_VAULT_KEY_PATH", "/very/secret/path");

        try
        {
            var host = new SubprocessPluginHost(root);
            PluginManifest manifest = Manifest() with { Executable = script };

            PluginResult result = await host.InvokeAsync(manifest, Call(), Ct);

            Assert.True(result.Ok, result.Detail);

            // The environment is not inherited: a key path sitting in the parent is exactly the
            // sort of thing that leaks without anybody deciding to pass it.
            Assert.Contains("\"secret_seen\":\"none\"", result.OutputJson!, StringComparison.Ordinal);

            // And what Aurora chose to pass did arrive.
            Assert.Contains("plugin/notes", result.OutputJson!, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AURORA_VAULT_KEY_PATH", null);
            TryDelete(root);
        }
    }

    [Fact]
    public async Task APluginThatHangsIsKilledRatherThanWaitedOn()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var root = TestTemp.Path("plug");
        var script = Path.Combine(root, "hang.sh");
        Directory.CreateDirectory(root);

        await File.WriteAllTextAsync(script, "#!/bin/sh\ncat > /dev/null\nsleep 60\n", Ct);
        File.SetUnixFileMode(
            script,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        try
        {
            var host = new SubprocessPluginHost(root);

            PluginManifest manifest = Manifest() with { Executable = script };
            manifest = manifest with
            {
                Capabilities =
                [
                    manifest.Capabilities[0] with { Timeout = TimeSpan.FromMilliseconds(400) },
                ],
            };

            PluginResult result = await host.InvokeAsync(manifest, Call(), Ct);

            // A plugin's timeout is its own declared one, and exceeding it ends the process rather
            // than the caller's patience.
            Assert.False(result.Ok);
            Assert.Equal("timed_out", result.Refusal);
            Assert.True(result.DurationMs < 30_000);
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
