using Aurora.Core.Abstractions;
using Aurora.Core.Contracts;

namespace Aurora.Adapters.Personality;

/// <summary>
/// Turns what the cycle decided to say into terms it may be said on (RFC 07).
/// </summary>
/// <remarks>
/// This class refuses more than it produces, and that is the design. Separating personality from
/// response generation is what stops an informal instruction becoming an invisible, unauditable
/// rule — so the composer is where the rules that outrank tone are actually applied.
/// </remarks>
public sealed class MessageComposer : IComposer
{
    /// <summary>
    /// Phrasings that manufacture urgency, dependence, guilt or exclusivity (RFC 07 rule 5).
    /// </summary>
    /// <remarks>
    /// A backstop, and worth being honest about what it is. Detecting manipulation in arbitrary
    /// text is not something a word list does; the real control is structural — there is no
    /// persuasive segment kind, so nothing in the plan is <i>for</i> inducing action, and Aurora
    /// does not write the final prose anyway. This catches the specific shapes the rule names when
    /// they appear in what Aurora itself contributes.
    /// </remarks>
    private static readonly string[] Manufactured =
    [
        "only i can", "only i understand", "nobody else will", "no one else can",
        "you need me", "you'd be lost without", "you would be lost without",
        "act now or", "last chance", "before it is too late", "before it's too late",
        "you'll regret", "you will regret", "after everything i", "i'm disappointed in you",
        "don't you care", "if you really cared",
    ];

    public MessageDraft Render(ResponsePlan plan, ResolvedProfile profile)
    {
        foreach (MessageSegment segment in plan.Segments)
        {
            if (!SegmentKind.IsKnown(segment.Kind))
            {
                throw new PersonalityException($"Unknown segment kind '{segment.Kind}'.");
            }

            // Rule 5. Refused rather than softened: a message that manufactures urgency does not
            // become acceptable in a gentler voice, because the voice is not what is wrong with it.
            var text = segment.Text.ToLowerInvariant();
            foreach (var phrase in Manufactured)
            {
                if (text.Contains(phrase, StringComparison.Ordinal))
                {
                    throw new PersonalityException(
                        $"This message manufactures pressure (\"{phrase}\"). Aurora does not do that.");
                }
            }

            // Rule 3, the other half: a profile's own prohibitions bind it.
            foreach (var claim in profile.Profile.ProhibitedClaims)
            {
                if (text.Contains(claim.ToLowerInvariant(), StringComparison.Ordinal))
                {
                    throw new PersonalityException(
                        $"'{claim}' is a claim this profile may not make.");
                }
            }
        }

        // Limit case: on a difficult subject, lightness and volunteering are out of place. The
        // person dealing with something hard did not ask for a companion.
        Voice voice = plan.SensitiveSubject
            ? profile.EffectiveVoice.Sobered()
            : profile.EffectiveVoice;

        // Rule 2: say what Aurora is when the channel or the subject warrants it. A sensitive
        // subject warrants it, because that is when somebody is most likely to forget.
        var disclosureNeeded =
            plan.SensitiveSubject
            || plan.Channel != "local"
            || plan.Segments.Any(s => s.Kind == SegmentKind.Escalation);

        var segments = plan.Segments.ToList();

        if (disclosureNeeded && segments.All(s => s.Kind != SegmentKind.Disclosure))
        {
            segments.Insert(0, new MessageSegment(SegmentKind.Disclosure, profile.Profile.DisclosureText));
        }

        // Rule 3: protected segments go last and stay whole. A risk stated at the end of a
        // cheerful paragraph is a risk that was not really stated.
        var ordered = segments
            .Where(s => !SegmentKind.IsProtected(s.Kind) || s.Kind == SegmentKind.Disclosure)
            .Concat(segments.Where(s => SegmentKind.IsProtected(s.Kind) && s.Kind != SegmentKind.Disclosure))
            .ToList();

        return new MessageDraft(
            ordered, voice, profile.Locale, profile.Profile.ProhibitedClaims, disclosureNeeded);
    }
}
