using System.Text.Json;
using Aurora.Core.Cryptography;
using Aurora.Core.Serialization;
using Xunit;

namespace Aurora.Tests.Unit;

public sealed class CoreSerializationTests
{
    [Fact]
    public void Canonicalize_IsKeyOrderIndependent()
    {
        var a = JsonDocument.Parse("""{"b":1,"a":2}""").RootElement;
        var b = JsonDocument.Parse("""{"a":2,"b":1}""").RootElement;

        Assert.Equal(CanonicalJson.Canonicalize(a), CanonicalJson.Canonicalize(b));
    }

    [Fact]
    public void Canonicalize_SortsNestedObjectsButPreservesArrayOrder()
    {
        var element = JsonDocument.Parse("""{"z":[3,1,2],"a":{"y":1,"x":2}}""").RootElement;

        Assert.Equal("""{"a":{"x":2,"y":1},"z":[3,1,2]}""", CanonicalJson.Canonicalize(element));
    }

    [Fact]
    public void Sha256Hex_MatchesKnownEmptyStringVector()
    {
        Assert.Equal(
            "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            Hashing.Sha256Hex(string.Empty));
    }

    [Fact]
    public void Sha256Hex_IsStableForSameCanonicalInput()
    {
        var a = JsonDocument.Parse("""{"m":"hi","n":1}""").RootElement;
        var b = JsonDocument.Parse("""{"n":1,"m":"hi"}""").RootElement;

        Assert.Equal(
            Hashing.Sha256Hex(CanonicalJson.Canonicalize(a)),
            Hashing.Sha256Hex(CanonicalJson.Canonicalize(b)));
    }
}
