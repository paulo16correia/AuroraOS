using System.Globalization;
using Aurora.Adapters.Personality;
using Aurora.Adapters.Knowledge;
using Aurora.Adapters.Memories;
using Aurora.Adapters.Relationships;
using Aurora.Adapters.Persistence;
using Aurora.Core.Contracts;
using Aurora.Tests.Support;
using Xunit;

namespace Aurora.Tests.Unit;

/// <summary>
/// Communication identity (RFC 07): a setting, not a claim about what Aurora is.
/// </summary>
public sealed class PersonalityTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    private static DateTimeOffset At(string iso) =>
        DateTimeOffset.Parse(iso, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static (SqlitePersonalityService Service, MessageComposer Composer,
        SqliteRelationshipModel Relationships, TestClock Clock) Build(
        SqliteTestDb db, string now = "2026-01-15T09:00:00+00:00")
    {
        var clock = new TestClock(At(now));
        var relationships = new SqliteRelationshipModel(
            db.Factory,
            new SqliteKnowledgeGraph(
                db.Factory,
                new SqliteMemoryService(
                    db.Factory, new LexicalMemoryRanker(), TestBus.Over(db.Factory, clock), clock),
                clock),
            clock);

        return (
            new SqlitePersonalityService(db.Factory, relationships, clock),
            new MessageComposer(), relationships, clock);
    }

    private static PersonalityProfile Candidate() =>
        new("", 0, "Aurora", ["pt-PT", "en"], "pt-PT", Voice.Default,
            ["say what is known"], ["I feel", "I am worried"], ["state uncertainty"],
            "Aurora is a software system, not a person.",
            ["defer to the owner"], "", null, ProfileStatus.Draft);

    private static ResponsePlan Plan(params MessageSegment[] segments) =>
        new(segments, "local");

    // ---- rule 1: versioned, approved, recoverable ----

    [Fact]
    public async Task ChangingWhoAuroraIsNeedsTheOwnerSApproval()
    {
        using var db = new SqliteTestDb();
        var (service, _, _, _) = Build(db);

        PersonalityProfile draft = await service.ProposeAsync(Candidate(), Ct);
        Assert.Equal(ProfileStatus.Draft, draft.Status);

        // An identity that could change itself would not be an identity.
        await Assert.ThrowsAsync<PersonalityException>(() =>
            service.ActivateAsync(draft.Id, "", "paulo", "first profile", Ct));

        PersonalityProfile active = await service.ActivateAsync(
            draft.Id, "approval/1", "paulo", "first profile", Ct);

        Assert.Equal(ProfileStatus.Active, active.Status);
    }

    [Fact]
    public async Task TheOutgoingProfileIsRetiredRatherThanDeleted()
    {
        using var db = new SqliteTestDb();
        var (service, _, _, clock) = Build(db);

        PersonalityProfile first = await service.ProposeAsync(Candidate(), Ct);
        await service.ActivateAsync(first.Id, "approval/1", "paulo", "first", Ct);

        clock.UtcNow = At("2026-02-01T09:00:00+00:00");
        PersonalityProfile second = await service.ProposeAsync(
            Candidate() with { Voice = Voice.Default with { Formality = 0.9 } }, Ct);
        await service.ActivateAsync(second.Id, "approval/2", "paulo", "more formal", Ct);

        // Rule 1 asks for recoverable, and a version that disappeared is not one anybody can go
        // back to. The history says how it got here and who agreed to each step.
        IReadOnlyList<IdentityChange> history = await service.HistoryAsync(Ct);

        Assert.Equal(2, history.Count);
        Assert.Contains(history, c => c.Reason == "more formal" && c.Actor == "paulo");
    }

    [Fact]
    public async Task AnIdentityChangeRecordsWhoAndWhy()
    {
        using var db = new SqliteTestDb();
        var (service, _, _, _) = Build(db);
        PersonalityProfile draft = await service.ProposeAsync(Candidate(), Ct);

        await Assert.ThrowsAsync<PersonalityException>(() =>
            service.ActivateAsync(draft.Id, "approval/1", "", "reason", Ct));

        await Assert.ThrowsAsync<PersonalityException>(() =>
            service.ActivateAsync(draft.Id, "approval/1", "paulo", "", Ct));
    }

    // ---- limit case: no profile means the minimum safe one, and saying so ----

    [Fact]
    public async Task WithNoProfileAuroraUsesTheMinimumSafeOneAndSaysItIsDegraded()
    {
        using var db = new SqliteTestDb();
        var (service, _, _, clock) = Build(db);

        ResolvedProfile resolved = await service.ResolveAsync("paulo", "local", clock.UtcNow, Ct);

        // Inventing a personality is worse than admitting there is none.
        Assert.True(resolved.Degraded);
        Assert.Equal(SqlitePersonalityService.MinimumSafe.Name, resolved.Profile.Name);
        Assert.Equal(0, resolved.EffectiveVoice.Humour);
        Assert.Equal(0, resolved.EffectiveVoice.Proactivity);
    }

    // ---- rule 4: PT-PT, and no claiming a language it does not have ----

    [Fact]
    public async Task TheDefaultIsPortugueseAndAnUnsupportedLanguageFallsBackRatherThanBeingClaimed()
    {
        using var db = new SqliteTestDb();
        var (service, _, _, clock) = Build(db);

        PersonalityProfile draft = await service.ProposeAsync(Candidate(), Ct);
        await service.ActivateAsync(draft.Id, "approval/1", "paulo", "first", Ct);

        Assert.Equal("pt-PT", (await service.ResolveAsync("paulo", "local", clock.UtcNow, Ct)).Locale);

        await service.SetPreferenceAsync(
            new CommunicationPreference("paulo", "local", "ja-JP", 0.5, null, "{}", false, ""), Ct);

        // Adapting to a language Aurora does not have would be claiming a skill it lacks.
        Assert.Equal("pt-PT", (await service.ResolveAsync("paulo", "local", clock.UtcNow, Ct)).Locale);

        await service.SetPreferenceAsync(
            new CommunicationPreference("paulo", "local", "en", 0.5, null, "{}", false, ""), Ct);

        Assert.Equal("en", (await service.ResolveAsync("paulo", "local", clock.UtcNow, Ct)).Locale);
    }

    [Fact]
    public async Task ProactivityIsSomethingSomebodyOptsInto()
    {
        using var db = new SqliteTestDb();
        var (service, _, _, clock) = Build(db);

        PersonalityProfile draft = await service.ProposeAsync(Candidate(), Ct);
        await service.ActivateAsync(draft.Id, "approval/1", "paulo", "first", Ct);

        // Without consent, Aurora answers what it was asked and stops.
        Assert.Equal(0, (await service.ResolveAsync("paulo", "local", clock.UtcNow, Ct)).EffectiveVoice.Proactivity);

        await service.SetPreferenceAsync(
            new CommunicationPreference("paulo", "local", "pt-PT", 0.5, null, "{}", true, ""), Ct);

        Assert.True(
            (await service.ResolveAsync("paulo", "local", clock.UtcNow, Ct)).EffectiveVoice.Proactivity > 0);
    }

    // ---- rule 5: never manufacture pressure ----

    [Theory]
    [InlineData("Only I can help you with this.")]
    [InlineData("Act now or you will regret it.")]
    [InlineData("After everything I have done for you.")]
    [InlineData("You need me to handle this.")]
    [InlineData("If you really cared you would do it now.")]
    public async Task AMessageThatManufacturesPressureIsRefusedRatherThanSoftened(string text)
    {
        using var db = new SqliteTestDb();
        var (service, composer, _, clock) = Build(db);

        ResolvedProfile profile = await service.ResolveAsync("paulo", "local", clock.UtcNow, Ct);

        // A message that manufactures urgency does not become acceptable in a gentler voice,
        // because the voice is not what is wrong with it.
        PersonalityException refused = Assert.Throws<PersonalityException>(() =>
            composer.Render(Plan(new MessageSegment(SegmentKind.Content, text)), profile));

        Assert.Contains("manufactures pressure", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ThereIsNoSegmentKindForPersuading()
    {
        // The real control is structural: nothing in a plan is *for* inducing action, because there
        // is no kind that means that. The phrase list is a backstop, not the mechanism.
        Assert.False(SegmentKind.IsKnown("PERSUASION"));
        Assert.False(SegmentKind.IsKnown("URGENCY"));

        foreach (var kind in new[]
                 {
                     SegmentKind.Disclosure, SegmentKind.Content,
                     SegmentKind.Risk, SegmentKind.Escalation,
                 })
        {
            Assert.True(SegmentKind.IsKnown(kind));
        }
    }

    // ---- rule 3: personality does not outrank a risk ----

    [Fact]
    public async Task ARiskIsNotReshapedByTheVoiceAndDoesNotGetBuriedMidParagraph()
    {
        using var db = new SqliteTestDb();
        var (service, composer, _, clock) = Build(db);

        ResolvedProfile profile = await service.ResolveAsync("paulo", "local", clock.UtcNow, Ct);

        MessageDraft draft = composer.Render(
            Plan(
                new MessageSegment(SegmentKind.Risk, "This deletes the file permanently."),
                new MessageSegment(SegmentKind.Content, "Here is what I found.")),
            profile);

        // A risk stated at the end of a cheerful paragraph is a risk that was not really stated.
        Assert.Equal(SegmentKind.Content, draft.Segments[0].Kind);
        Assert.Equal(SegmentKind.Risk, draft.Segments[^1].Kind);
        Assert.Equal("This deletes the file permanently.", draft.Segments[^1].Text);
    }

    [Fact]
    public async Task AProfileCannotMakeAClaimItForbadeItself()
    {
        using var db = new SqliteTestDb();
        var (service, composer, _, clock) = Build(db);

        PersonalityProfile draft = await service.ProposeAsync(Candidate(), Ct);
        await service.ActivateAsync(draft.Id, "approval/1", "paulo", "first", Ct);

        ResolvedProfile profile = await service.ResolveAsync("paulo", "local", clock.UtcNow, Ct);

        Assert.Throws<PersonalityException>(() =>
            composer.Render(
                Plan(new MessageSegment(SegmentKind.Content, "I feel that this is the right choice.")),
                profile));
    }

    // ---- rule 2 and the sensitive-content limit case ----

    [Fact]
    public async Task OnADifficultSubjectAuroraSaysWhatItIsAndStopsBeingLight()
    {
        using var db = new SqliteTestDb();
        var (service, composer, _, clock) = Build(db);

        PersonalityProfile draft = await service.ProposeAsync(
            Candidate() with { Voice = new Voice(0.5, 0.7, Humour: 0.8, Proactivity: 0.9) }, Ct);
        await service.ActivateAsync(draft.Id, "approval/1", "paulo", "first", Ct);

        await service.SetPreferenceAsync(
            new CommunicationPreference("paulo", "local", "pt-PT", 0.5, null, "{}", true, ""), Ct);

        ResolvedProfile profile = await service.ResolveAsync("paulo", "local", clock.UtcNow, Ct);
        Assert.True(profile.EffectiveVoice.Humour > 0);

        MessageDraft rendered = composer.Render(
            new ResponsePlan(
                [new MessageSegment(SegmentKind.Content, "Here is what the records show.")],
                "local", SensitiveSubject: true),
            profile);

        // Somebody dealing with something hard did not ask for a companion.
        Assert.Equal(0, rendered.Voice.Humour);
        Assert.True(rendered.Voice.Proactivity <= 0.1);

        // And this is exactly when a person is most likely to forget what they are talking to.
        Assert.True(rendered.DisclosureRequired);
        Assert.Equal(SegmentKind.Disclosure, rendered.Segments[0].Kind);
    }

    [Fact]
    public async Task AnEscalationAlwaysCarriesTheDisclosureWithIt()
    {
        using var db = new SqliteTestDb();
        var (service, composer, _, clock) = Build(db);

        ResolvedProfile profile = await service.ResolveAsync("paulo", "local", clock.UtcNow, Ct);

        MessageDraft draft = composer.Render(
            Plan(
                new MessageSegment(SegmentKind.Content, "Here is what I can see."),
                new MessageSegment(SegmentKind.Escalation, "Speak to a doctor about this.")),
            profile);

        Assert.True(draft.DisclosureRequired);
        Assert.Equal(SegmentKind.Disclosure, draft.Segments[0].Kind);
        Assert.Equal(SegmentKind.Escalation, draft.Segments[^1].Kind);
    }

    [Fact]
    public async Task AnOrdinaryLocalAnswerDoesNotReciteADisclosureEveryTime()
    {
        using var db = new SqliteTestDb();
        var (service, composer, _, clock) = Build(db);

        ResolvedProfile profile = await service.ResolveAsync("paulo", "local", clock.UtcNow, Ct);

        MessageDraft draft = composer.Render(
            Plan(new MessageSegment(SegmentKind.Content, "It is 3pm.")), profile);

        // Rule 2 says when the context warrants it, not always. A disclosure on every sentence
        // stops being read, which defeats the point of having one.
        Assert.False(draft.DisclosureRequired);
        Assert.Single(draft.Segments);
    }

    // ---- RFC 029 meets RFC 07: a habit shapes how something is said ----

    [Fact]
    public async Task WhatThePersonPrefersAboutToneShapesTheVoice()
    {
        using var db = new SqliteTestDb();
        var (service, _, relationships, clock) = Build(db);

        PersonalityProfile draft = await service.ProposeAsync(
            Candidate() with { Voice = Voice.Default with { Formality = 0.5 } }, Ct);
        await service.ActivateAsync(draft.Id, "approval/1", "paulo", "first", Ct);

        Assert.Equal(
            0.5, (await service.ResolveAsync("paulo", "local", clock.UtcNow, Ct)).EffectiveVoice.Formality, 2);

        await relationships.SetExplicitAsync(
            "paulo", "paulo", PreferenceDimension.Tone, """{"tone":"blunt"}""",
            ["conversation/9"], Ct);

        ResolvedProfile shaped = await service.ResolveAsync("paulo", "local", clock.UtcNow, Ct);

        // A habit shapes how something is said. The same preference could not authorise anything,
        // which is why it is resolved under the presentational effect and no other.
        Assert.True(shaped.EffectiveVoice.Formality <= 0.3);
        Assert.Equal(1.0, shaped.EffectiveVoice.Conciseness, 2);
    }

    [Fact]
    public async Task AnInferredTonePreferenceStillOnlyShapesPresentation()
    {
        using var db = new SqliteTestDb();
        var (service, _, relationships, clock) = Build(db);

        PersonalityProfile draft = await service.ProposeAsync(Candidate(), Ct);
        await service.ActivateAsync(draft.Id, "approval/1", "paulo", "first", Ct);

        await relationships.InferAsync(
            new Preference(
                "", "paulo", "paulo", PreferenceDimension.Tone, """{"tone":"formal"}""",
                0.8, PreferenceBasis.Observed, [], "{}", PreferenceStatus.Active, "", true),
            ["conversation/3"], Ct);

        ResolvedProfile shaped = await service.ResolveAsync("paulo", "local", clock.UtcNow, Ct);
        Assert.True(shaped.EffectiveVoice.Formality >= 0.8);

        // And the very same preference buys nothing that reaches outside Aurora.
        PreferenceResolution acting = await relationships.ResolveAsync(
            "paulo", PreferenceDimension.Tone, PreferenceEffect.ExternalCommunication, Ct);

        Assert.False(acting.MayActWithoutConfirmation);
    }
}
