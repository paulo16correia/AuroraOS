using System.Text.RegularExpressions;

namespace Aurora.Core.Cryptography;

/// <summary>
/// Recognises text that is shaped like a credential.
/// </summary>
/// <remarks>
/// A last line rather than a first one, and deliberately shallow: it catches an accident and an
/// unsophisticated attempt, and it does not catch a determined one. Anything relying on this alone
/// to keep a secret in has already lost — the structural controls are that a secret is never handed
/// out in the first place, and that the types crossing a boundary have nowhere to put one.
/// <para>
/// Shared rather than copied. Two components check the same thing (a plugin's output on the way
/// back, a learning proposal's change set before it is deployed), and a security check that exists
/// twice is a security check that is improved once.
/// </para>
/// </remarks>
public static partial class SecretShape
{
    /// <summary>Whether this text carries something that looks like a credential.</summary>
    public static bool Matches(string text) => Shapes().IsMatch(text);

    [GeneratedRegex(
        @"(-----BEGIN [A-Z ]*PRIVATE KEY-----)|(\bBearer\s+[A-Za-z0-9._~+/-]{20,})|" +
        @"(""(?:api[_-]?key|secret|password|token|credential)""\s*:\s*""[^""]{8,}"")",
        RegexOptions.IgnoreCase)]
    private static partial Regex Shapes();
}
