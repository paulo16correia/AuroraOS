using System.Text.Json;
using Aurora.Core.Contracts;
using Xunit;

namespace Aurora.Tests.Unit;

/// <summary>
/// The schema builder produces what a capability author would have written by hand, and the two
/// things they might have forgotten.
/// </summary>
public sealed class CapabilityInputTests
{
    private static string Json(JsonElement schema) => schema.GetRawText();

    [Fact]
    public void TheTwoThingsEasyToForgetAreNotOptional()
    {
        var schema = Json(CapabilityInput.Object().String("note", maxLength: 10).Build());

        // additionalProperties:false, so a capability never silently accepts a field it does not
        // understand — and the 2020-12 draft, so two capabilities are never validated against
        // different dialects.
        Assert.Contains("\"additionalProperties\":false", schema, StringComparison.Ordinal);
        Assert.Contains("2020-12", schema, StringComparison.Ordinal);
    }

    [Fact]
    public void RequiredFieldsAreListedAndOptionalOnesAreNot()
    {
        var schema = Json(CapabilityInput.Object()
            .String("path", maxLength: 512, required: true, minLength: 1)
            .Boolean("dry_run")
            .Build());

        Assert.Contains("\"required\":[\"path\"]", schema, StringComparison.Ordinal);
        Assert.Contains("\"minLength\":1", schema, StringComparison.Ordinal);
        Assert.Contains("\"dry_run\":{\"type\":\"boolean\"}", schema, StringComparison.Ordinal);
    }

    [Fact]
    public void AnObjectWithNoFieldsAcceptsOnlyTheEmptyObject()
    {
        var schema = Json(CapabilityInput.Object().Build());

        Assert.Contains("\"properties\":{}", schema, StringComparison.Ordinal);
        Assert.DoesNotContain("\"required\"", schema, StringComparison.Ordinal);
    }

    [Fact]
    public void NestedArraysCarryTheirOwnRules()
    {
        var schema = Json(CapabilityInput.Object()
            .ArrayOf(
                "rules",
                CapabilityInput.Object().String("match", maxLength: 128, required: true),
                maxItems: 20, required: true, minItems: 1)
            .Build());

        Assert.Contains("\"maxItems\":20", schema, StringComparison.Ordinal);
        Assert.Contains("\"minItems\":1", schema, StringComparison.Ordinal);

        // The items schema is closed too. An array whose elements accept unknown fields is the
        // same hole one level down.
        Assert.Contains("\"items\":{\"type\":\"object\",\"additionalProperties\":false",
            schema, StringComparison.Ordinal);

        // And the nested one carries no $schema of its own — only the document root does.
        Assert.Equal(1, schema.Split("$schema").Length - 1);
    }

    [Fact]
    public void AFieldDeclaredTwiceIsAMistakeAndSaysSo()
    {
        // Silently keeping the last one would mean a capability whose schema does not match what
        // its author read.
        ArgumentException twice = Assert.Throws<ArgumentException>(() =>
            CapabilityInput.Object()
                .String("note", maxLength: 10)
                .String("note", maxLength: 20));

        Assert.Contains("already declared", twice.Message, StringComparison.Ordinal);

        Assert.Throws<ArgumentException>(() => CapabilityInput.Object().Boolean("  "));
    }

    [Fact]
    public void TheBuiltSchemaDoesNotChangeWhenTheBuilderIsUsedAgain()
    {
        CapabilityInput builder = CapabilityInput.Object().String("a", maxLength: 5);
        JsonElement first = builder.Build();

        builder.String("b", maxLength: 5);

        // A descriptor holding a live view of a mutable builder would be a published schema that
        // could change afterwards.
        Assert.DoesNotContain("\"b\"", Json(first), StringComparison.Ordinal);
    }
}
