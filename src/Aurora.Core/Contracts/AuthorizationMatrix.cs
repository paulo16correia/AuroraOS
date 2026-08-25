using System.Globalization;
using System.Text;

namespace Aurora.Core.Contracts;

/// <summary>
/// Publishes what each capability may reach and what it takes to reach it.
/// </summary>
/// <remarks>
/// The architecture review's fifth mandatory condition asks for authorization matrices published
/// per capability. Rendered from the live registry rather than written by hand, and checked against
/// the committed file by a test — a matrix that drifts from the code is worse than none, because it
/// is the document somebody will reach for when deciding whether something is safe.
/// </remarks>
public static class AuthorizationMatrix
{
    public static string Render(
        IReadOnlyList<CapabilityDescriptor> capabilities, IReadOnlyList<EventContract> events)
    {
        var page = new StringBuilder();

        page.AppendLine("# Reference — capability authorization and event contracts");
        page.AppendLine();
        page.AppendLine("**Generated from the running registry. Do not edit by hand.**");
        page.AppendLine("`AuthorizationMatrixTests` fails when this file and the code disagree.");
        page.AppendLine();
        page.AppendLine("Closes condition 5 of `docs/reviews/architecture-review-v1.0.md`.");
        page.AppendLine();

        page.AppendLine("## Capability authorization");
        page.AppendLine();
        page.AppendLine("| Action | Risk | Effects | Approval | Consent path |");
        page.AppendLine("| --- | --- | --- | --- | --- |");

        foreach (CapabilityDescriptor c in capabilities.OrderBy(c => c.ActionId, StringComparer.Ordinal))
        {
            var effects = c.Effects.Count == 0 ? "none — reads only" : string.Join(", ", c.Effects);

            // The consent path is a consequence of risk and the approval flag, not a separate
            // setting. Spelling it out here is what makes the table answer the question a reader
            // actually has: what happens when this is called?
            var consent = (c.Risk, c.ApprovalRequired) switch
            {
                (RiskLevel.Low, false) => "automatic (LOW, effect-free)",
                (_, true) => "persisted approval, one-time, scoped to this exact input",
                _ => "refused without an approval policy",
            };

            page.AppendLine(string.Create(
                CultureInfo.InvariantCulture,
                $"| `{c.ActionId}` | {c.Risk} | {effects} | {(c.ApprovalRequired ? "required" : "not required")} | {consent} |"));
        }

        page.AppendLine();
        page.AppendLine("## Declared events (LAW-007)");
        page.AppendLine();
        page.AppendLine("| Type | v | Producer | Class | Payload | Consumers |");
        page.AppendLine("| --- | --- | --- | --- | --- | --- |");

        foreach (EventContract e in events
                     .OrderBy(e => e.Producer, StringComparer.Ordinal)
                     .ThenBy(e => e.Type, StringComparer.Ordinal))
        {
            var consumers = e.Consumers.Count == 0 ? "—" : string.Join(", ", e.Consumers);

            page.AppendLine(string.Create(
                CultureInfo.InvariantCulture,
                $"| `{e.Type}` | {e.SchemaVersion} | `{e.Producer}` | {e.SensitivityClass} | {e.Payload} | {consumers} |"));
        }

        page.AppendLine();
        page.AppendLine("An event type absent from this table cannot be published: the outbox");
        page.AppendLine("refuses it, whichever producer asks. `api` is the only producer reachable");
        page.AppendLine("from outside Aurora, and it may emit exactly one type.");
        page.AppendLine();

        return page.ToString();
    }
}
