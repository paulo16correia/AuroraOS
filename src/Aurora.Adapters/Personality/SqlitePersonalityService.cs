using System.Globalization;
using Aurora.Adapters.Persistence;
using Aurora.Core;
using Aurora.Core.Abstractions;
using Aurora.Core.Contracts;
using Microsoft.Data.Sqlite;

namespace Aurora.Adapters.Personality;

/// <summary>
/// A versioned, auditable communication identity (RFC 07).
/// </summary>
/// <remarks>
/// Versioning is what lets an identity change without losing continuity, and keeping it here rather
/// than in a prompt is what stops an informal instruction becoming an invisible rule nobody can
/// find later.
/// </remarks>
public sealed class SqlitePersonalityService : IPersonalityService
{
    /// <summary>
    /// What Aurora falls back to when no profile can be read.
    /// </summary>
    /// <remarks>
    /// Plain, brief, and not volunteering. RFC 07's limit case says to use a minimum safe profile
    /// and signal degradation rather than invent traces — a personality made up on the spot is
    /// exactly the invisible rule this RFC exists to prevent.
    /// </remarks>
    public static PersonalityProfile MinimumSafe { get; } = new(
        "profile/minimum-safe", 0, "Aurora", ["pt-PT", "en"], "pt-PT",
        new Voice(Formality: 0.6, Conciseness: 0.9, Humour: 0, Proactivity: 0),
        Values: ["say only what is known", "do not fill gaps with invention"],
        ProhibitedClaims: ["I feel", "I want", "I promise"],
        InteractionRules: ["state uncertainty plainly", "offer no opinion that was not asked for"],
        DisclosureText: "Aurora is a software system, not a person.",
        EscalationRules: ["defer to the owner on anything material"],
        ActiveFromUtc: "0001-01-01T00:00:00.0000000+00:00", ActiveToUtc: null, ProfileStatus.Active);

    private readonly SqliteConnectionFactory _factory;
    private readonly IClock _clock;

    public SqlitePersonalityService(SqliteConnectionFactory factory, IClock clock)
    {
        _factory = factory;
        _clock = clock;
    }

    public async Task<ResolvedProfile> ResolveAsync(
        string ownerId, string channel, DateTimeOffset at, CancellationToken ct)
    {
        PersonalityProfile? active;
        CommunicationPreference? preference;

        try
        {
            active = await ActiveAsync(at, ct).ConfigureAwait(false);
            preference = await PreferenceAsync(ownerId, channel, ct).ConfigureAwait(false);
        }
        catch (SqliteException unreadable)
        {
            // Degraded and saying so. Inventing a personality is worse than admitting there is none.
            return Fallback(ownerId, channel, $"the profile could not be read: {unreadable.SqliteErrorCode}");
        }

        if (active is null)
        {
            return Fallback(ownerId, channel, "no profile is active");
        }

        preference ??= new CommunicationPreference(
            ownerId, channel, active.DefaultLocale, Verbosity: 0.5, QuietHours: null,
            AccessibilityJson: "{}", ConsentForProactivity: false, Iso(_clock.UtcNow));

        Voice voice = active.Voice with
        {
            Conciseness = 1 - Math.Clamp(preference.Verbosity, 0, 1),

            // Rule: proactivity is something a person opts into. Without that, Aurora answers what
            // it was asked and stops.
            Proactivity = preference.ConsentForProactivity ? active.Voice.Proactivity : 0,
        };

        // Rule 4: PT-PT unless somebody asked for something else, and only a language the profile
        // claims. Adapting to a language Aurora does not have would be claiming a skill it lacks.
        var locale = active.Languages.Contains(preference.Language, StringComparer.OrdinalIgnoreCase)
            ? preference.Language
            : active.DefaultLocale;

        return new ResolvedProfile(active, preference, voice, locale, Degraded: false, "resolved");
    }

    private ResolvedProfile Fallback(string ownerId, string channel, string reason) =>
        new(MinimumSafe,
            new CommunicationPreference(
                ownerId, channel, MinimumSafe.DefaultLocale, 0.5, null, "{}", false, Iso(_clock.UtcNow)),
            MinimumSafe.Voice, MinimumSafe.DefaultLocale, Degraded: true, reason);

    public async Task<PersonalityProfile> ProposeAsync(PersonalityProfile profile, CancellationToken ct)
    {
        var version = await NextVersionAsync(ct).ConfigureAwait(false);

        var drafted = profile with
        {
            Id = Guid.NewGuid().ToString("N"),
            Version = version,
            Status = ProfileStatus.Draft,
            ActiveFromUtc = Iso(_clock.UtcNow),
            ActiveToUtc = null,
            ApprovalRef = null,
        };

        await SaveAsync(drafted, ct).ConfigureAwait(false);
        return drafted;
    }

    public async Task<PersonalityProfile> ActivateAsync(
        string profileId, string approvalRef, string actor, string reason, CancellationToken ct)
    {
        // Rule 1: a material change to how Aurora presents itself is the owner's decision. An
        // identity that could change itself would not be an identity.
        if (string.IsNullOrWhiteSpace(approvalRef))
        {
            throw new PersonalityException("Changing the active identity needs the owner's approval.");
        }

        if (string.IsNullOrWhiteSpace(actor) || string.IsNullOrWhiteSpace(reason))
        {
            throw new PersonalityException("An identity change records who made it and why.");
        }

        PersonalityProfile candidate = await GetAsync(profileId, ct).ConfigureAwait(false)
            ?? throw new PersonalityException("Unknown profile.");

        if (candidate.Status != ProfileStatus.Draft)
        {
            throw new PersonalityException($"Only a DRAFT profile is activated; this is {candidate.Status}.");
        }

        PersonalityProfile? outgoing = await ActiveAsync(_clock.UtcNow, ct).ConfigureAwait(false);
        var now = Iso(_clock.UtcNow);

        if (outgoing is not null)
        {
            // Retired, not deleted. Rule 1 asks for recoverable, and a version that disappeared is
            // not one anybody can go back to.
            await ExecuteAsync(
                "UPDATE personality_profile SET status = @retired, active_to_utc = @at WHERE id = @id;",
                ct,
                ("@retired", ProfileStatus.Retired), ("@at", now), ("@id", outgoing.Id))
                .ConfigureAwait(false);
        }

        await ExecuteAsync("""
            UPDATE personality_profile
               SET status = @active, active_from_utc = @at, approval_ref = @approval
             WHERE id = @id;
            """, ct,
            ("@active", ProfileStatus.Active), ("@at", now), ("@approval", approvalRef),
            ("@id", profileId)).ConfigureAwait(false);

        await ExecuteAsync("""
            INSERT INTO identity_change
                (id, profile_id, old_version, new_version, actor, reason, approved_at_utc)
            VALUES (@id, @profile, @old, @new, @actor, @reason, @at);
            """, ct,
            ("@id", Guid.NewGuid().ToString("N")), ("@profile", profileId),
            ("@old", outgoing?.Version ?? 0), ("@new", candidate.Version),
            ("@actor", actor), ("@reason", reason), ("@at", now)).ConfigureAwait(false);

        return candidate with
        {
            Status = ProfileStatus.Active, ActiveFromUtc = now, ApprovalRef = approvalRef,
        };
    }

    public async Task<IReadOnlyList<IdentityChange>> HistoryAsync(CancellationToken ct)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, profile_id, old_version, new_version, actor, reason, approved_at_utc
              FROM identity_change ORDER BY approved_at_utc;
            """;

        var changes = new List<IdentityChange>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            changes.Add(new IdentityChange(
                reader.GetString(0), reader.GetString(1), reader.GetInt32(2), reader.GetInt32(3),
                reader.GetString(4), reader.GetString(5), reader.GetString(6)));
        }

        return changes;
    }

    public async Task<CommunicationPreference> SetPreferenceAsync(
        CommunicationPreference preference, CancellationToken ct)
    {
        CommunicationPreference stored = preference with { UpdatedAtUtc = Iso(_clock.UtcNow) };

        await ExecuteAsync("""
            INSERT INTO communication_preference
                (owner_id, channel, language, verbosity, quiet_hours, accessibility_json,
                 consent_for_proactivity, updated_at_utc)
            VALUES (@owner, @channel, @language, @verbosity, @quiet, @accessibility, @consent, @at)
            ON CONFLICT(owner_id, channel) DO UPDATE SET
                language = @language, verbosity = @verbosity, quiet_hours = @quiet,
                accessibility_json = @accessibility, consent_for_proactivity = @consent,
                updated_at_utc = @at;
            """, ct,
            ("@owner", stored.OwnerId), ("@channel", stored.Channel), ("@language", stored.Language),
            ("@verbosity", stored.Verbosity),
            ("@quiet", (object?)stored.QuietHours ?? DBNull.Value),
            ("@accessibility", stored.AccessibilityJson),
            ("@consent", stored.ConsentForProactivity ? 1 : 0), ("@at", stored.UpdatedAtUtc))
            .ConfigureAwait(false);

        return stored;
    }

    // ---- plumbing ----

    private const string Select = """
        SELECT id, version, name, languages, default_locale, voice_json, values_list,
               prohibited_claims, interaction_rules, disclosure_text, escalation_rules,
               active_from_utc, active_to_utc, status, approval_ref
          FROM personality_profile
        """;

    private async Task<PersonalityProfile?> ActiveAsync(DateTimeOffset at, CancellationToken ct)
    {
        IReadOnlyList<PersonalityProfile> found = await ReadAsync($"""
            {Select}
             WHERE status = @active AND active_from_utc <= @at
               AND (active_to_utc IS NULL OR active_to_utc > @at)
             ORDER BY version DESC LIMIT 1;
            """, ct, ("@active", ProfileStatus.Active), ("@at", Iso(at))).ConfigureAwait(false);

        return found.Count == 0 ? null : found[0];
    }

    private async Task<PersonalityProfile?> GetAsync(string profileId, CancellationToken ct)
    {
        IReadOnlyList<PersonalityProfile> found = await ReadAsync(
            $"{Select} WHERE id = @id;", ct, ("@id", profileId)).ConfigureAwait(false);

        return found.Count == 0 ? null : found[0];
    }

    private async Task<int> NextVersionAsync(CancellationToken ct)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(MAX(version), 0) + 1 FROM personality_profile;";

        return Convert.ToInt32(
            await command.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    private async Task<CommunicationPreference?> PreferenceAsync(
        string ownerId, string channel, CancellationToken ct)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT owner_id, channel, language, verbosity, quiet_hours, accessibility_json,
                   consent_for_proactivity, updated_at_utc
              FROM communication_preference WHERE owner_id = @owner AND channel = @channel;
            """;
        command.Parameters.AddWithValue("@owner", ownerId);
        command.Parameters.AddWithValue("@channel", channel);

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false)
            ? new CommunicationPreference(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetDouble(3),
                reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetString(5),
                reader.GetInt32(6) == 1, reader.GetString(7))
            : null;
    }

    private Task SaveAsync(PersonalityProfile profile, CancellationToken ct) =>
        ExecuteAsync("""
            INSERT INTO personality_profile
                (id, version, name, languages, default_locale, voice_json, values_list,
                 prohibited_claims, interaction_rules, disclosure_text, escalation_rules,
                 active_from_utc, active_to_utc, status, approval_ref)
            VALUES (@id, @version, @name, @languages, @locale, @voice, @values, @prohibited,
                    @rules, @disclosure, @escalation, @from, @to, @status, @approval);
            """, ct,
            ("@id", profile.Id), ("@version", profile.Version), ("@name", profile.Name),
            ("@languages", string.Join('\n', profile.Languages)),
            ("@locale", profile.DefaultLocale),
            ("@voice", AuroraJson.Serialize(profile.Voice)),
            ("@values", string.Join('\n', profile.Values)),
            ("@prohibited", string.Join('\n', profile.ProhibitedClaims)),
            ("@rules", string.Join('\n', profile.InteractionRules)),
            ("@disclosure", profile.DisclosureText),
            ("@escalation", string.Join('\n', profile.EscalationRules)),
            ("@from", profile.ActiveFromUtc),
            ("@to", (object?)profile.ActiveToUtc ?? DBNull.Value),
            ("@status", profile.Status),
            ("@approval", (object?)profile.ApprovalRef ?? DBNull.Value));

    private async Task<IReadOnlyList<PersonalityProfile>> ReadAsync(
        string sql, CancellationToken ct, params (string Name, object Value)[] args)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach ((var name, var value) in args)
        {
            command.Parameters.AddWithValue(name, value);
        }

        var profiles = new List<PersonalityProfile>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            profiles.Add(new PersonalityProfile(
                reader.GetString(0), reader.GetInt32(1), reader.GetString(2),
                Lines(reader.GetString(3)), reader.GetString(4),
                AuroraJson.Deserialize<Voice>(reader.GetString(5)),
                Lines(reader.GetString(6)), Lines(reader.GetString(7)), Lines(reader.GetString(8)),
                reader.GetString(9), Lines(reader.GetString(10)), reader.GetString(11),
                reader.IsDBNull(12) ? null : reader.GetString(12),
                reader.GetString(13),
                reader.IsDBNull(14) ? null : reader.GetString(14)));
        }

        return profiles;
    }

    private async Task ExecuteAsync(
        string sql, CancellationToken ct, params (string Name, object Value)[] args)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach ((var name, var value) in args)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static IReadOnlyList<string> Lines(string value) =>
        value.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string Iso(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}
