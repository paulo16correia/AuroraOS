using System.Text.Json;
using System.Text.Json.Serialization;
using Aurora.Adapters.Personality;
using Aurora.Adapters.Persistence;
using Aurora.Core.Abstractions;
using Aurora.Core.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Aurora.Server;

/// <summary>
/// Setting who Aurora is, from the console, because activating an identity needs the owner.
/// </summary>
/// <remarks>
/// Until this existed there was no way to activate a personality profile at all. The machinery was
/// complete — <see cref="IPersonalityService"/> proposes drafts and activates them with an approval
/// reference, and <c>VoiceIdentity</c> composes an identity from whatever is active — and nothing
/// could put a profile in. So every installation ran on <see cref="SqlitePersonalityService.MinimumSafe"/>
/// forever: correct, deliberately austere, humour and proactivity at zero, and the only Aurora
/// anybody had ever met.
/// <para>
/// On the console for the reason <see cref="SecretConsole"/> is: being at the owner's terminal is
/// the authority. An identity change over HTTP would be reachable by the bearer token, which
/// belongs to the agent — so the agent could decide who Aurora is, which is the one thing RFC 07
/// rule 1 reserves for the owner. The approval reference recorded here says exactly what happened:
/// the operating-system user did it, at the console, at a stated time.
/// </para>
/// <para>
/// A file rather than prompts. A profile is a dozen fields including three lists, and typing that
/// into a terminal once is worse than editing it; <c>identity template</c> writes a starting point
/// to edit, which also means the owner reads the values before activating them.
/// </para>
/// </remarks>
public static class IdentityConsole
{
    public static bool TryHandle(string[] args, AuroraServerOptions options)
    {
        if (args.FirstOrDefault() != "identity")
        {
            return false;
        }

        var verb = args.Length > 1 ? args[1] : "help";

        return verb switch
        {
            "show" => Show(options),
            "template" => Template(Arg(args, 2)),
            "set" => Set(Arg(args, 2), options),
            "history" => History(options),
            _ => Help(),
        };
    }

    private static string? Arg(string[] args, int at) => args.Length > at ? args[at] : null;

    private static bool Help()
    {
        Console.WriteLine("""
            [Aurora] Who Aurora is when it speaks.

              identity show                  the active profile, and whether it is the fallback
              identity template <file.json>  write a profile to edit
              identity set <file.json>       activate the profile in that file
              identity history               how the identity got to where it is

            Activating is the owner's to do, so it happens here rather than over HTTP: the bearer
            token belongs to the agent, and an agent that could rewrite who Aurora is would be
            deciding its own character.
            """);

        return true;
    }

    // ---- what is active ----

    private static bool Show(AuroraServerOptions options)
    {
        ResolvedProfile resolved = Service(options)
            .ResolveAsync(Environment.UserName, "voice", DateTimeOffset.UtcNow, CancellationToken.None)
            .GetAwaiter().GetResult();

        PersonalityProfile p = resolved.Profile;

        Console.WriteLine($"[Aurora] {p.Name}, version {p.Version} ({p.Status})");

        if (resolved.Degraded)
        {
            Console.WriteLine(
                "[Aurora] This is the minimum safe fallback, not a profile anybody chose: "
                + resolved.Reason);
            Console.WriteLine(
                "[Aurora] It is deliberately plain — no humour, no initiative, short answers. "
                + "`identity template` writes one to edit.");
        }

        Console.WriteLine($"          languages: {string.Join(", ", p.Languages)} (default {p.DefaultLocale})");
        Console.WriteLine(
            $"          tone: formality {p.Voice.Formality:0.0}, conciseness {p.Voice.Conciseness:0.0}, "
            + $"humour {p.Voice.Humour:0.0}, proactivity {p.Voice.Proactivity:0.0}");

        Lines("holds to", p.Values);
        Lines("talks like", p.InteractionRules);
        Lines("never claims", p.ProhibitedClaims);

        Console.WriteLine($"          discloses: {p.DisclosureText}");

        if (p.ApprovalRef is not null)
        {
            Console.WriteLine($"          approved: {p.ApprovalRef}");
        }

        return true;
    }

    private static void Lines(string label, IReadOnlyList<string> values)
    {
        if (values.Count == 0)
        {
            return;
        }

        Console.WriteLine($"          {label}:");

        foreach (var value in values)
        {
            Console.WriteLine($"            - {value}");
        }
    }

    private static bool History(AuroraServerOptions options)
    {
        IReadOnlyList<IdentityChange> changes = Service(options)
            .HistoryAsync(CancellationToken.None).GetAwaiter().GetResult();

        if (changes.Count == 0)
        {
            Console.WriteLine("[Aurora] No identity has ever been activated on this machine.");
            return true;
        }

        foreach (IdentityChange change in changes)
        {
            Console.WriteLine($"[Aurora] {change.ApprovedAtUtc}  {change.Actor}  {change.Reason}");
        }

        return true;
    }

    // ---- a starting point to edit ----

    private static bool Template(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            Console.WriteLine("[Aurora] Usage: identity template <file.json>");
            return true;
        }

        if (File.Exists(path))
        {
            // Not overwritten. The likeliest second run of this command is a mistake, and the
            // likeliest file at that path is a profile somebody has been editing.
            Console.WriteLine($"[Aurora] {path} already exists; not overwriting it.");
            return true;
        }

        File.WriteAllText(path, JsonSerializer.Serialize(Starting, Pretty));

        Console.WriteLine($"[Aurora] Wrote {path}.");
        Console.WriteLine(
            "[Aurora] The dials run 0 to 1. `values`, `interaction_rules` and `prohibited_claims` "
            + "are what the voice is actually built from — read them before activating.");
        Console.WriteLine($"[Aurora] Then: identity set {path}");

        return true;
    }

    /// <summary>
    /// A profile that is a reasonable starting point, and says so rather than being a default.
    /// </summary>
    /// <remarks>
    /// Warmer than the minimum safe fallback, because that one exists for when something is broken
    /// and it reads like it. What is kept from it is the part that matters: the two values about not
    /// inventing. Measured against the local model, those are what stop a voice channel producing a
    /// fresh biography per answer — Paris, a marketing career, a name from a grandmother.
    /// </remarks>
    private static ProfileFile Starting { get; } = new(
        Name: "Aurora",
        Languages: ["pt-PT", "en"],
        DefaultLocale: "pt-PT",
        Formality: 0.35,
        Conciseness: 0.75,
        Humour: 0.35,
        Proactivity: 0.45,
        Values:
        [
            "say only what is known, and say plainly when something is not known",
            "do not fill gaps with invention",
            "be useful before being agreeable",
        ],
        InteractionRules:
        [
            "talk the way people talk out loud, in sentences",
            "ask which of two things somebody meant rather than guessing",
            "it is fine to disagree, and to say when something seems like a bad idea",
            "answer first, then offer the next thing only if it is obviously useful",
        ],
        ProhibitedClaims:
        [
            "I promise",
            "I did that",
        ],
        DisclosureText: "Sou a Aurora, um sistema. Não sou uma pessoa.",
        EscalationRules: ["defer to the owner on anything material"]);

    // ---- activating one ----

    private static bool Set(string? path, AuroraServerOptions options)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            Console.WriteLine("[Aurora] Usage: identity set <file.json>");
            return true;
        }

        if (!File.Exists(path))
        {
            Console.WriteLine($"[Aurora] No such file: {path}");
            Console.WriteLine("[Aurora] `identity template <file.json>` writes one to edit.");
            return true;
        }

        ProfileFile file;

        try
        {
            file = JsonSerializer.Deserialize<ProfileFile>(File.ReadAllText(path), Lenient)
                ?? throw new JsonException("the file is empty");
        }
        catch (JsonException broken)
        {
            Console.WriteLine($"[Aurora] {path} is not a profile this can read: {broken.Message}");
            return true;
        }

        if (string.IsNullOrWhiteSpace(file.Name))
        {
            Console.WriteLine("[Aurora] The profile needs a name — it is what Aurora is called.");
            return true;
        }

        IPersonalityService service = Service(options);

        PersonalityProfile drafted = service
            .ProposeAsync(file.ToProfile(), CancellationToken.None)
            .GetAwaiter().GetResult();

        // The approval reference says what actually authorised this, which is somebody being at
        // this terminal. A capability cannot see the Kernel's approval id from inside itself, so an
        // identity change over the API could only ever have recorded something vaguer than this.
        var reference =
            $"console:{Environment.UserName}@{DateTimeOffset.UtcNow:O}";

        PersonalityProfile active = service
            .ActivateAsync(
                drafted.Id, reference, Environment.UserName,
                $"activated from {Path.GetFileName(path)} at the console",
                CancellationToken.None)
            .GetAwaiter().GetResult();

        Console.WriteLine($"[Aurora] {active.Name} is active, version {active.Version}.");
        Console.WriteLine(
            "[Aurora] A voice conversation composes its identity when it answers, so a running "
            + "Aurora picks this up on the next thing it says.");

        return true;
    }

    // ---- the file, which is not the contract ----

    /// <summary>
    /// What a profile looks like on disk: the fields an owner sets, and none of the bookkeeping.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="PersonalityProfile"/> on purpose. That record carries an id, a
    /// version, a status, an approval reference and two timestamps, none of which an owner should
    /// be writing by hand and all of which the service decides. A file that mirrored the record
    /// would invite somebody to set their own version number and wonder why it was ignored.
    /// </remarks>
    private sealed record ProfileFile(
        string Name,
        IReadOnlyList<string> Languages,
        [property: JsonPropertyName("default_locale")] string DefaultLocale,
        double Formality,
        double Conciseness,
        double Humour,
        double Proactivity,
        IReadOnlyList<string> Values,
        [property: JsonPropertyName("interaction_rules")] IReadOnlyList<string> InteractionRules,
        [property: JsonPropertyName("prohibited_claims")] IReadOnlyList<string> ProhibitedClaims,
        [property: JsonPropertyName("disclosure_text")] string DisclosureText,
        [property: JsonPropertyName("escalation_rules")] IReadOnlyList<string> EscalationRules)
    {
        public PersonalityProfile ToProfile() => new(
            // The service assigns these. Whatever is in the file is ignored, which is why the file
            // does not have them.
            Id: string.Empty,
            Version: 0,
            Name: Name.Trim(),
            Languages: Languages ?? ["en"],
            DefaultLocale: string.IsNullOrWhiteSpace(DefaultLocale) ? "en" : DefaultLocale,
            Voice: new Voice(Dial(Formality), Dial(Conciseness), Dial(Humour), Dial(Proactivity)),
            Values: Values ?? [],
            ProhibitedClaims: ProhibitedClaims ?? [],
            InteractionRules: InteractionRules ?? [],
            DisclosureText: DisclosureText ?? string.Empty,
            EscalationRules: EscalationRules ?? [],
            ActiveFromUtc: string.Empty,
            ActiveToUtc: null,
            Status: ProfileStatus.Draft);

        /// <summary>Clamped rather than refused: 1.4 means "as far as it goes", not a typo worth stopping for.</summary>
        private static double Dial(double value) => Math.Clamp(value, 0, 1);
    }

    private static readonly JsonSerializerOptions Pretty =
        new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    private static readonly JsonSerializerOptions Lenient =
        new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };

    /// <summary>
    /// The personality service, built from the container rather than by hand.
    /// </summary>
    /// <remarks>
    /// Resolving it reaches a relationship model, a knowledge graph, an outbox, an event catalogue
    /// and a memory service — the first attempt here wired those by hand and the chain kept going.
    /// A console that assembles half the system by hand is a console that drifts from it, and the
    /// registration is already the one place that knows how these fit together.
    /// </remarks>
    private static IPersonalityService Service(AuroraServerOptions options)
    {
        new SqliteDatabase(new SqliteConnectionFactory(options.DbPath)).Initialize();

        var services = new ServiceCollection();
        services.AddAurora(options);

        // Deliberately not disposed: the verbs below run to completion and the process exits, and
        // disposing the provider would close the connection factory under the command still using
        // it. SecretConsole has the same shape for the same reason.
        return services.BuildServiceProvider().GetRequiredService<IPersonalityService>();
    }
}
