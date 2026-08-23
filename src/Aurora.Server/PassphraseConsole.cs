using Aurora.Adapters.Consent;
using Aurora.Adapters.Time;
using Aurora.Core.Abstractions;

namespace Aurora.Server;

/// <summary>
/// Enrollment and revocation of the operator passphrase, from the server host's console
/// (docs/adr/0011).
/// </summary>
/// <remarks>
/// Deliberately not an HTTP endpoint and not an MCP tool. The bearer token is held by the MCP
/// client — the agent — so anything reachable with it is reachable by the agent, and an agent that
/// can enrol its own passphrase can approve its own requests. The console of the host process is
/// the one channel the agent does not have.
/// </remarks>
public static class PassphraseConsole
{
    /// <summary>Handles an enrol/revoke command; returns false when the args are a normal server start.</summary>
    public static bool TryHandle(string[] args, AuroraServerOptions options)
    {
        var command = args.FirstOrDefault();
        if (command is not ("enroll-passphrase" or "revoke-passphrase"))
        {
            return false;
        }

        IPassphraseAuthenticator authenticator =
            new Pbkdf2PassphraseAuthenticator(options.PassphrasePath, new SystemClock(), PassphraseOptions.Default);

        if (command == "revoke-passphrase")
        {
            authenticator.Revoke();
            Console.WriteLine("[Aurora] Operator passphrase revoked. Approvals are no longer guarded by one.");
            return true;
        }

        if (authenticator.IsEnrolled)
        {
            Console.WriteLine("[Aurora] A passphrase is already enrolled. Run 'revoke-passphrase' first.");
            return true;
        }

        var first = ReadHidden("Choose an operator passphrase (min 8 chars): ");
        var second = ReadHidden("Repeat it: ");

        if (!string.Equals(first, second, StringComparison.Ordinal))
        {
            Console.WriteLine("[Aurora] The two entries did not match; nothing was enrolled.");
            return true;
        }

        try
        {
            authenticator.Enroll(first);
            Console.WriteLine($"[Aurora] Passphrase enrolled at {options.PassphrasePath}.");
            Console.WriteLine("[Aurora] aurora_approve now requires it. Aurora cannot recover it for you.");
        }
        catch (ArgumentException ex)
        {
            Console.WriteLine($"[Aurora] {ex.Message}");
        }

        return true;
    }

    /// <summary>Reads without echoing. Falls back to a plain read when input is redirected.</summary>
    private static string ReadHidden(string prompt)
    {
        Console.Write(prompt);

        if (Console.IsInputRedirected)
        {
            // Scripted enrollment: nothing is echoed by us, but the caller owns that channel.
            return Console.ReadLine() ?? string.Empty;
        }

        var buffer = new System.Text.StringBuilder();
        while (true)
        {
            ConsoleKeyInfo key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                return buffer.ToString();
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (buffer.Length > 0)
                {
                    buffer.Length--;
                }

                continue;
            }

            if (!char.IsControl(key.KeyChar))
            {
                buffer.Append(key.KeyChar);
            }
        }
    }
}
