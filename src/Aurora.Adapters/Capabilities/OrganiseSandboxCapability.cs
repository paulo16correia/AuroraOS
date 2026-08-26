using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Aurora.Core.Abstractions;
using Aurora.Core.Contracts;

namespace Aurora.Adapters.Capabilities;

/// <summary>
/// Files the sandbox's contents into folders by rule (docs/adr/0060).
/// </summary>
/// <remarks>
/// The reference capability: a worked example of the shape a serious one takes, rather than the
/// smallest thing that compiles. What it demonstrates, in order of how easy each is to get wrong:
/// <list type="number">
/// <item><b>A plan is a separate thing from an effect.</b> <c>dry_run</c> returns exactly what
/// would happen and changes nothing — RFC 01 rule 1's ladder, expressed inside one capability
/// rather than across two.</item>
/// <item><b>All of it or none of it.</b> The whole plan is built and checked before the first file
/// moves; a move that fails partway undoes the ones before it. A half-organised sandbox is worse
/// than an unorganised one, because nobody knows which half.</item>
/// <item><b>Reversible in fact, not in claim.</b> The result carries the inverse plan, so
/// "reversible" is something the caller holds rather than something Aurora asserts.</item>
/// <item><b>Idempotent.</b> Running it twice moves nothing the second time; a file already where a
/// rule wants it is reported as already placed, not moved onto itself.</item>
/// <item><b>Ambiguity is refused, not resolved.</b> A file matched by two rules stops the run.
/// Picking the first would make the outcome depend on the order the rules were written in, which is
/// not a decision anybody made.</item>
/// </list>
/// </remarks>
public sealed class OrganiseSandboxCapability : ICapability
{
    /// <summary>
    /// How many files one run may move.
    /// </summary>
    /// <remarks>
    /// RFC 06 rule 4: entry, exit, time and cost respect configured limits. A run that reorganises
    /// a thousand files is one nobody reviewed properly at the approval prompt.
    /// </remarks>
    private const int MaxMoves = 100;

    private const string InputSchemaJson =
        """
        {"$schema":"https://json-schema.org/draft/2020-12/schema","type":"object",
         "additionalProperties":false,"required":["rules"],
         "properties":{
           "rules":{"type":"array","minItems":1,"maxItems":20,
             "items":{"type":"object","additionalProperties":false,"required":["match","into"],
               "properties":{
                 "match":{"type":"string","minLength":1,"maxLength":128},
                 "into":{"type":"string","minLength":1,"maxLength":128}}}},
           "dry_run":{"type":"boolean"}}}
        """;

    private static readonly JsonElement SchemaElement =
        JsonDocument.Parse(InputSchemaJson).RootElement.Clone();

    private readonly ISandboxFileIndex _index;
    private readonly ISandboxFileMover _mover;

    public OrganiseSandboxCapability(ISandboxFileIndex index, ISandboxFileMover mover)
    {
        _index = index;
        _mover = mover;
    }

    public CapabilityDescriptor Descriptor { get; } = new(
        ActionId: "files.organise_sandbox",
        Title: "Organise sandbox files into folders",
        Description:
            "Moves files inside the Aurora sandbox into folders by rule. Each rule matches a glob "
            + "and names a destination folder. With dry_run the plan is returned and nothing "
            + "moves. The result always carries the inverse plan, so a run can be undone. "
            + "Requires approval.",
        InputSchema: SchemaElement,
        Effects: ["files.read", "files.write", "files.move"],

        // HIGH, not MEDIUM: writing one named file is a thing the owner pictured when they
        // approved it. Rearranging a directory by rule is not, and the gap between what a rule
        // says and what it matches is where the surprise lives.
        Risk: RiskLevel.High,
        ApprovalRequired: true,

        // The claim policy reads: the result carries the inverse plan, so whoever ran this can
        // put the sandbox back. A HIGH capability that could not say this would be denied.
        Reversible: true);

    public async ValueTask<JsonElement> ExecuteAsync(JsonElement input, CancellationToken ct)
    {
        var dryRun = input.TryGetProperty("dry_run", out JsonElement flag)
            && flag.ValueKind == JsonValueKind.True;

        List<Rule> rules = ReadRules(input);
        IReadOnlyList<SandboxEntry> files = await _index.ListAsync(ct).ConfigureAwait(false);

        (List<Move> planned, List<string> alreadyPlaced) = Plan(rules, files);

        if (planned.Count > MaxMoves)
        {
            throw new SandboxViolationException(
                $"{planned.Count} files match; the limit for one run is {MaxMoves}.");
        }

        var moved = new List<Move>();

        if (!dryRun)
        {
            await ApplyAsync(planned, moved, ct).ConfigureAwait(false);
        }

        return Result(dryRun, planned, moved, alreadyPlaced);
    }

    /// <summary>
    /// Performs the plan, undoing what it managed if something fails.
    /// </summary>
    /// <remarks>
    /// The undo is best-effort and its own failures are not swallowed: the message says how many
    /// moves were undone out of how many were made, so the caller learns the sandbox is in a state
    /// between the two rather than assuming it was restored.
    /// </remarks>
    private async Task ApplyAsync(List<Move> planned, List<Move> moved, CancellationToken ct)
    {
        foreach (Move move in planned)
        {
            try
            {
                await _mover.MoveAsync(move.From, move.To, ct).ConfigureAwait(false);
                moved.Add(move);
            }
            catch (Exception failure) when (failure is not OperationCanceledException)
            {
                var made = moved.Count;
                var undone = 0;

                foreach (Move done in Enumerable.Reverse(moved))
                {
                    try
                    {
                        await _mover.MoveAsync(done.To, done.From, ct).ConfigureAwait(false);
                        undone++;
                    }
                    catch (Exception stuck) when (stuck is not OperationCanceledException)
                    {
                        // Nothing further can be undone reliably. Stop and report honestly rather
                        // than keep trying and lose count of where things ended up.
                        break;
                    }
                }

                moved.Clear();

                throw new SandboxViolationException(
                    $"Could not move {move.From} ({failure.Message}); {undone} of {made} earlier "
                    + "moves were undone. The run was abandoned.");
            }
        }
    }

    private static (List<Move> Planned, List<string> AlreadyPlaced) Plan(
        List<Rule> rules, IReadOnlyList<SandboxEntry> files)
    {
        var planned = new List<Move>();
        var alreadyPlaced = new List<string>();
        var destinations = new HashSet<string>(StringComparer.Ordinal);

        foreach (SandboxEntry file in files)
        {
            List<Rule> matched = rules.Where(r => r.Matches(file.Path)).ToList();

            if (matched.Count == 0)
            {
                continue;
            }

            if (matched.Count > 1)
            {
                // Ambiguity is refused rather than resolved: picking the first would make the
                // outcome depend on the order the rules were written in.
                throw new SandboxViolationException(
                    $"{file.Path} matches {matched.Count} rules: "
                    + string.Join(", ", matched.Select(m => m.Match)));
            }

            var name = file.Path[(file.Path.LastIndexOf('/') + 1)..];
            var destination = $"{matched[0].Into.TrimEnd('/')}/{name}";

            if (string.Equals(destination, file.Path, StringComparison.Ordinal))
            {
                // Idempotence: already where the rule wants it. Reported, not moved onto itself.
                alreadyPlaced.Add(file.Path);
                continue;
            }

            if (!destinations.Add(destination))
            {
                // Two files of the same name from different folders would land on each other, and
                // the mover refuses to overwrite — so this would fail halfway through instead.
                // Found while planning, when nothing has moved yet.
                throw new SandboxViolationException($"two files would both become {destination}");
            }

            planned.Add(new Move(file.Path, destination));
        }

        return (planned, alreadyPlaced);
    }

    private static List<Rule> ReadRules(JsonElement input)
    {
        var rules = new List<Rule>();

        foreach (JsonElement element in input.GetProperty("rules").EnumerateArray())
        {
            var match = element.GetProperty("match").GetString() ?? string.Empty;
            var into = element.GetProperty("into").GetString() ?? string.Empty;

            // The destination is a folder inside the sandbox and nothing else. The mover would
            // refuse an escape anyway; refusing here means the caller is told which rule was wrong
            // rather than which file failed.
            if (into.Contains("..", StringComparison.Ordinal)
                || into.StartsWith('/')
                || into.Contains('\\', StringComparison.Ordinal))
            {
                throw new SandboxViolationException($"'{into}' is not a folder inside the sandbox.");
            }

            rules.Add(new Rule(match, into));
        }

        return rules;
    }

    private static JsonElement Result(
        bool dryRun, List<Move> planned, List<Move> moved, List<string> alreadyPlaced)
    {
        var plan = new JsonArray();
        foreach (Move move in planned)
        {
            plan.Add(new JsonObject { ["from"] = move.From, ["to"] = move.To });
        }

        // The inverse, in reverse order, so replaying it undoes the run exactly.
        var undo = new JsonArray();
        foreach (Move move in Enumerable.Reverse(dryRun ? planned : moved))
        {
            undo.Add(new JsonObject { ["from"] = move.To, ["to"] = move.From });
        }

        var placed = new JsonArray();
        foreach (var path in alreadyPlaced)
        {
            placed.Add(path);
        }

        return JsonSerializer.SerializeToElement(new JsonObject
        {
            ["dry_run"] = dryRun,
            ["moved"] = dryRun ? 0 : moved.Count,
            ["planned"] = planned.Count,
            ["already_placed"] = placed,
            ["plan"] = plan,
            ["undo"] = undo,
        });
    }

    private sealed record Move(string From, string To);

    /// <summary>One rule: a glob over the relative path, and the folder its matches go into.</summary>
    private sealed record Rule(string Match, string Into)
    {
        private readonly Regex _pattern = Compile(Match);

        public bool Matches(string path) => _pattern.IsMatch(path);

        /// <summary>
        /// Turns a glob into a regex, one token at a time.
        /// </summary>
        /// <remarks>
        /// Written as a walk rather than a chain of replacements because of one case that a chain
        /// gets wrong: <c>**/</c> matches <b>zero</b> or more folders, so <c>**/*.md</c> has to
        /// match a file at the root as well as one three folders down. Substituting <c>**</c> for
        /// <c>.*</c> leaves the slash behind and quietly requires at least one folder — which is
        /// what this did until a test caught it.
        /// <para>
        /// Everything that is not a wildcard is escaped, so a rule containing regex
        /// metacharacters matches them literally rather than becoming a pattern its author did not
        /// write.
        /// </para>
        /// </remarks>
        private static Regex Compile(string glob)
        {
            var pattern = new StringBuilder("^");

            for (var i = 0; i < glob.Length; i++)
            {
                if (glob[i] == '*' && i + 1 < glob.Length && glob[i + 1] == '*')
                {
                    if (i + 2 < glob.Length && glob[i + 2] == '/')
                    {
                        pattern.Append("(?:.*/)?");
                        i += 2;
                    }
                    else
                    {
                        pattern.Append(".*");
                        i++;
                    }
                }
                else
                {
                    pattern.Append(glob[i] switch
                    {
                        '*' => "[^/]*",
                        '?' => "[^/]",
                        _ => Regex.Escape(glob[i].ToString()),
                    });
                }
            }

            return new Regex(
                pattern.Append('$').ToString(),
                RegexOptions.CultureInvariant,
                TimeSpan.FromSeconds(1));
        }
    }
}
