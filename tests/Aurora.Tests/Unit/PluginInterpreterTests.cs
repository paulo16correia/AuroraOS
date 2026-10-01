using Aurora.Adapters.Plugins;
using Aurora.Adapters.Plugins.Sandboxes;
using Aurora.Core.Abstractions;
using Aurora.Core.Contracts;
using Aurora.Tests.Support;
using Xunit;

namespace Aurora.Tests.Unit;

/// <summary>
/// What runs a plugin whose program the platform cannot start on its own (docs/adr/0075).
/// </summary>
/// <remarks>
/// The bug these were written for: every service plugin Aurora ships is a Python script, and on
/// Windows every one of them failed with <c>SERVICE_UNAVAILABLE: could not start:
/// Win32Exception</c> — Voice, Microsoft, Discord and the local voice stack alike. It was never
/// their bug. <c>CreateProcess</c> wants an executable image and will not read a shebang, so a
/// <c>.py</c> file is not something Windows can start, and nothing in Aurora said what should.
/// <para>
/// <b>Most of these run on every platform.</b> The resolver is told whether it is on Windows rather
/// than asking, so the decision — which runtime, from what, and what is refused — is testable from
/// a Mac. Only the tests that actually start a process are platform-dependent, and they say so.
/// </para>
/// </remarks>
public sealed class PluginInterpreterTests
{
    private static CancellationToken Ct => new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token;

    private static PluginManifest Manifest(string executable, string? interpreter = null) =>
        new(
            "acme/notes", "1.0.0", "acme", "", MinPlatformVersion: 1,
            Capabilities: [],
            EventSubscriptions: [], RequiredPermissions: [],
            MaxDataClass: Sensitivity.Private, NetworkEndpoints: [],
            DocumentationRef: "docs", IntegrityHash: "",
            Executable: executable,
            Interpreter: interpreter);

    /// <summary>A file on disk with the given first line, so a shebang can actually be read.</summary>
    private static string Script(string name, string contents)
    {
        var path = Path.Combine(TestTemp.Folder("interp"), name);
        File.WriteAllText(path, contents);
        return path;
    }

    /// <summary>A resolver that behaves as if it were on Windows, wherever the suite is running.</summary>
    private static PluginInterpreters OnWindows(
        IReadOnlyDictionary<string, string>? configured = null) =>
        new(configured, windows: true);

    // ---- 1. is an interpreter wanted at all ----

    [Fact]
    public void OnUnixNothingNeedsAnInterpreterBecauseTheKernelReadsTheShebang()
    {
        var resolver = new PluginInterpreters(windows: false);

        Assert.False(resolver.Needed("/plugins/voice/voice_service.py"));

        // And the resolution is an empty success, which is what keeps the macOS and Linux launch
        // byte-for-byte the one that was verified there.
        InterpreterResolution resolved = resolver.Resolve(
            Manifest("/plugins/voice/voice_service.py"), "/plugins/voice/voice_service.py");

        Assert.True(resolved.Ok);
        Assert.Null(resolved.Interpreter);
    }

    [Theory]
    [InlineData("run.exe", false)]
    [InlineData("run.EXE", false)]
    [InlineData("run.com", false)]
    [InlineData("run.py", true)]
    [InlineData("run", true)]
    [InlineData("run.bat", true)]
    [InlineData("run.cmd", true)]
    public void OnWindowsOnlyARealExecutableImageStartsItself(string fileName, bool needed) =>
        Assert.Equal(needed, OnWindows().Needed(fileName));

    // ---- 2. what the command becomes ----

    [Fact]
    public void APythonPluginIsLaunchedAsTheInterpreterWithTheScriptAsItsArgument()
    {
        var request = new SandboxRequest(
            "acme/notes", @"C:\plugins\notes\run.py", @"C:\work",
            Interpreter: new PluginInterpreter(PluginRuntimes.Python, @"C:\Python313\python.exe"));

        // The whole fix, in one assertion: python.exe run.py, not run.py.
        Assert.Equal(
            [@"C:\Python313\python.exe", @"C:\plugins\notes\run.py"],
            PluginCommand.For(request));
    }

    [Fact]
    public void APluginThatIsItsOwnProgramIsLaunchedUnchanged()
    {
        var request = new SandboxRequest("acme/notes", "/plugins/notes/run", "/work");

        Assert.Equal(["/plugins/notes/run"], PluginCommand.For(request));
    }

    [Fact]
    public void TheUnconfinedPlanNamesTheInterpreterAndPassesItTheScript()
    {
        SandboxPlan plan = new UnconfinedSandbox("tests").Plan(
            new SandboxRequest(
                "acme/notes", @"C:\plugins\notes\run.py", @"C:\work",
                Interpreter: new PluginInterpreter(PluginRuntimes.Python, @"C:\Python313\python.exe")));

        Assert.Equal(@"C:\Python313\python.exe", plan.FileName);
        Assert.Equal([@"C:\plugins\notes\run.py"], plan.Arguments);
    }

    // ---- 3 & 4. missing and unusable interpreters ----

    [Fact]
    public void AnInterpreterTheOwnerNamedThatIsNotThereIsARefusalAndNotASearch()
    {
        var script = Script("run.py", "#!/usr/bin/env python3\nprint()\n");

        PluginInterpreters resolver = OnWindows(
            new Dictionary<string, string> { [PluginRuntimes.Python] = @"C:\nowhere\python.exe" });

        InterpreterResolution resolved = resolver.Resolve(Manifest(script), script);

        // Not "look for a different Python". The owner said which one, and quietly starting
        // another would be Aurora overruling a decision it was given.
        Assert.False(resolved.Ok);
        Assert.Null(resolved.Interpreter);
        Assert.Contains(@"C:\nowhere\python.exe", resolved.Refused!, StringComparison.Ordinal);
        Assert.Contains("Aurora:Plugins:Interpreters", resolved.Refused!, StringComparison.Ordinal);
    }

    [Fact]
    public void AProgramThatSaysNothingAboutWhatRunsItIsRefusedWithSomethingToDoAboutIt()
    {
        // No shebang, no .py, no declaration. Windows cannot start it and nothing says what could.
        var script = Script("mystery", "print()\n");

        InterpreterResolution resolved = OnWindows().Resolve(Manifest(script), script);

        Assert.False(resolved.Ok);

        // The message is the feature: this is what somebody reads at three in the morning when a
        // plugin will not start, and "Win32Exception" told them nothing they could act on.
        Assert.Contains("interpreter", resolved.Refused!, StringComparison.Ordinal);
        Assert.Contains("python3", resolved.Refused!, StringComparison.Ordinal);
    }

    [Fact]
    public void AShebangNamingSomethingAuroraDoesNotStartIsRefusedRatherThanObeyed()
    {
        var script = Script("run.sh", "#!/bin/sh\necho hello\n");

        InterpreterResolution resolved = OnWindows().Resolve(Manifest(script), script);

        // The rule the whole closed set exists for. A shebang may pick a name from Aurora's list
        // and nothing else, so a script cannot talk Aurora into starting a shell for it — which
        // would be the generic command execution Aurora does not have.
        Assert.False(resolved.Ok);
        Assert.Null(resolved.Interpreter);
    }

    [Fact]
    public void AShebangCanOnlyEverChooseANameAndNeverAPath()
    {
        // The path in a shebang is discarded: what is taken from it is the runtime's name, which
        // then resolves the same way as any other. Otherwise a manifest could name a program.
        Assert.Equal(PluginRuntimes.Python, PluginRuntimes.FromShebang("#!/opt/evil/bin/python3"));
        Assert.Equal(PluginRuntimes.Python, PluginRuntimes.FromShebang("#!/usr/bin/env python3"));
        Assert.Equal(PluginRuntimes.Python, PluginRuntimes.FromShebang("#!/usr/local/bin/python3.12 -u"));

        Assert.Null(PluginRuntimes.FromShebang("#!/bin/bash"));
        Assert.Null(PluginRuntimes.FromShebang("#!/usr/bin/env node"));
        Assert.Null(PluginRuntimes.FromShebang("#!C:\\Windows\\System32\\cmd.exe"));
        Assert.Null(PluginRuntimes.FromShebang("import sys"));
        Assert.Null(PluginRuntimes.FromShebang(null));
    }

    // ---- 5. what a manifest may declare ----

    [Theory]
    [InlineData("python3", PluginRuntimes.Python)]
    [InlineData("python", PluginRuntimes.Python)]
    [InlineData("python3.12", PluginRuntimes.Python)]
    [InlineData("python.exe", PluginRuntimes.Python)]
    public void AManifestMayNameARuntimeInAnyOfItsSpellings(string declared, string canonical) =>
        Assert.Equal(canonical, PluginRuntimes.Canonical(declared));

    [Theory]
    [InlineData("bash")]
    [InlineData("sh")]
    [InlineData("cmd")]
    [InlineData("powershell")]
    [InlineData("/usr/bin/python3")]
    [InlineData(@"C:\Python313\python.exe")]
    [InlineData("node")]
    [InlineData("")]
    [InlineData(null)]
    public void AManifestMayNotNameAnythingElse(string? declared)
    {
        Assert.Null(PluginRuntimes.Canonical(declared));
        Assert.False(PluginRuntimes.IsKnown(declared));
    }

    [Fact]
    public void AManifestThatNamesAPathIsRefusedWhenItIsRead()
    {
        PluginManifestRead read = PluginManifestReader.Read(
            """
            {
              "plugin_id": "acme/notes", "version": "1.0.0", "publisher": "acme",
              "executable": "run.py", "interpreter": "/usr/bin/python3",
              "documentation_ref": "README.md",
              "capabilities": [
                {
                  "key": "notes.write", "title": "Write", "description": "Writes.",
                  "input_schema": { "type": "object" }
                }
              ]
            }
            """,
            []);

        Assert.False(read.Ok);
        Assert.Contains(
            read.Problems,
            problem => problem.Contains("interpreter", StringComparison.Ordinal));
    }

    [Fact]
    public void WhatTheManifestDeclaresBeatsWhatTheScriptSays()
    {
        // Both name Python here, which is the only runtime there is — so this asserts precedence
        // through the shape of the answer rather than by inventing a second runtime to prefer.
        var script = Script("run.py", "no shebang at all\n");

        PluginInterpreters resolver = OnWindows(
            new Dictionary<string, string> { [PluginRuntimes.Python] = FakePython() });

        InterpreterResolution declared = resolver.Resolve(
            Manifest(script, interpreter: "python3"), script);

        Assert.True(declared.Ok);
        Assert.Equal(PluginRuntimes.Python, declared.Interpreter!.Runtime);
    }

    // ---- 6. a program that needs nothing ----

    [Fact]
    public void ANativeProgramIsStartedDirectlyEvenOnWindows()
    {
        var executable = Script("run.exe", "not really a program, but named like one");

        InterpreterResolution resolved = OnWindows().Resolve(Manifest(executable), executable);

        Assert.True(resolved.Ok);
        Assert.Null(resolved.Interpreter);
    }

    // ---- 7. no shell, anywhere ----

    [Fact]
    public void NothingResolvesToAShell()
    {
        // Belt and braces over the closed set: whatever a manifest or a script asks for, the
        // program Aurora ends up starting is never a command processor.
        foreach (var asked in new[] { "sh", "bash", "cmd", "cmd.exe", "powershell", "pwsh", "wsl" })
        {
            Assert.Null(PluginRuntimes.Canonical(asked));
        }

        var script = Script("run.py", "#!/usr/bin/env python3\n");

        InterpreterResolution resolved = OnWindows(
            new Dictionary<string, string> { [PluginRuntimes.Python] = FakePython() })
            .Resolve(Manifest(script), script);

        Assert.True(resolved.Ok);
        Assert.EndsWith("python.exe", resolved.Interpreter!.Path, StringComparison.OrdinalIgnoreCase);
    }

    // ---- 8, 9 & 10. the whole thing, as a process ----

    [Fact]
    public async Task APythonServicePluginStartsAndHoldsAConversation()
    {
        // The end-to-end claim, on whichever platform the suite is running: a plugin whose program
        // is a Python script starts, answers over the JSONL protocol, and stays running for a
        // second call. On Windows this is the test that would have caught the original bug.
        var directory = TestTemp.Folder("interp-service");
        var script = Path.Combine(directory, "service.py");

        await File.WriteAllTextAsync(
            script,
            """
            #!/usr/bin/env python3
            import json, sys
            for line in sys.stdin:
                frame = json.loads(line)
                if frame.get("kind") == "hello":
                    print(json.dumps({"kind": "ready"}), flush=True)
                elif frame.get("kind") == "call":
                    print(json.dumps({
                        "kind": "result", "id": frame["id"], "ok": True,
                        "output": {"echo": frame.get("input", {})}}), flush=True)
                elif frame.get("kind") == "shutdown":
                    break
            """,
            Ct);

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        PluginManifest manifest = Manifest(script, interpreter: "python3") with
        {
            Capabilities =
            [
                new PluginCapability(
                    "notes.write", "{}", "{}", [], ApprovalRequired: false,
                    RateLimitPerMinute: 60, Timeout: TimeSpan.FromSeconds(10),
                    IdempotencySupport: true, AuditLevel: "FULL"),
            ],
            Service = new PluginService(script, TimeSpan.FromSeconds(20)),
        };

        await using var host = new ServicePluginHost(
            TestTemp.Folder("interp-root"), new UnconfinedSandbox("tests run the process directly"),
            new NoSecrets(), new NoObservations(), new TestClock(DateTimeOffset.UnixEpoch),
            allowUnconfined: true);

        PluginResult first = await host.InvokeAsync(
            manifest, new PluginInvocation("acme/notes", "notes.write", """{"n":1}""", Sensitivity.Private), Ct);

        Assert.True(first.Ok, first.Detail);
        Assert.Contains("\"n\":1", first.OutputJson!, StringComparison.Ordinal);

        PluginResult second = await host.InvokeAsync(
            manifest, new PluginInvocation("acme/notes", "notes.write", """{"n":2}""", Sensitivity.Private), Ct);

        Assert.True(second.Ok, second.Detail);
        Assert.Contains("\"n\":2", second.OutputJson!, StringComparison.Ordinal);

        // One process for both calls, still ready.
        Assert.Single(host.Running());
        Assert.Equal(PluginServiceStatus.Ready, host.Running()[0].Status);
    }

    [Fact]
    public async Task AServiceWhoseInterpreterCannotBeFoundIsUnavailableAndSaysWhichOne()
    {
        var directory = TestTemp.Folder("interp-broken");
        var script = Path.Combine(directory, "service.py");
        await File.WriteAllTextAsync(script, "#!/usr/bin/env python3\n", Ct);

        PluginManifest manifest = Manifest(script, interpreter: "python3") with
        {
            Service = new PluginService(script, TimeSpan.FromSeconds(5)),
            Capabilities =
            [
                new PluginCapability(
                    "notes.write", "{}", "{}", [], ApprovalRequired: false,
                    RateLimitPerMinute: 60, Timeout: TimeSpan.FromSeconds(5),
                    IdempotencySupport: true, AuditLevel: "FULL"),
            ],
        };

        await using var host = new ServicePluginHost(
            TestTemp.Folder("interp-broken-root"), new UnconfinedSandbox("tests"),
            new NoSecrets(), new NoObservations(), new TestClock(DateTimeOffset.UnixEpoch),
            allowUnconfined: true,
            interpreters: OnWindows(
                new Dictionary<string, string> { [PluginRuntimes.Python] = @"C:\nowhere\python.exe" }));

        PluginResult result = await host.InvokeAsync(
            manifest, new PluginInvocation("acme/notes", "notes.write", "{}", Sensitivity.Private), Ct);

        // The refusal Aurora already has for "the plugin is not there", carrying a detail that
        // names what is missing instead of a Win32Exception nobody can act on.
        Assert.False(result.Ok);
        Assert.Equal(PluginRefusal.ServiceUnavailable, result.Refusal);
        Assert.Contains(@"C:\nowhere\python.exe", result.Detail, StringComparison.Ordinal);

        // And it is held rather than started again on every call.
        Assert.Equal(PluginServiceStatus.Failed, host.Running()[0].Status);
    }

    /// <summary>A file named like a Python that exists, for the tests that only resolve.</summary>
    private static string FakePython()
    {
        var path = Path.Combine(TestTemp.Folder("interp-bin"), "python.exe");
        File.WriteAllText(path, "not a real interpreter; only its path is under test");
        return path;
    }

    private sealed class NoSecrets : IPluginSecretSource
    {
        public Task<string?> FindAsync(string pluginId, string name, CancellationToken ct) =>
            Task.FromResult<string?>(null);
    }

    private sealed class NoObservations : IPluginObservationSink
    {
        public Task ReceiveAsync(PluginObservation observation, CancellationToken ct) =>
            Task.CompletedTask;
    }
}
