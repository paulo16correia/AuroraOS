using Aurora.Adapters.Consent;
using Aurora.Adapters.Time;
using Aurora.Core.Abstractions;

using Aurora.Adapters.Persistence;
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
        if (command is not ("enroll-passphrase" or "revoke-passphrase" or "seal-audit-break"))
        {
            return false;
        }

        if (command == "seal-audit-break")
        {
            return SealAuditBreak(args, options);
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

    /// <summary>
    /// Records that the audit chain before now cannot be verified, and starts a new one.
    /// </summary>
    /// <remarks>
    /// The recovery path for a lost or replaced signing key. Without it, a key that goes missing
    /// leaves Aurora refusing to start with nothing an operator can do about it — which is not
    /// fail-closed, it is bricked.
    /// <para>
    /// On this console and not over HTTP, for the same reason enrolment is: the bearer token
    /// belongs to the agent, and an agent able to declare its own audit trail unverifiable would
    /// be able to erase its own history.
    /// </para>
    /// </remarks>
    private static bool SealAuditBreak(string[] args, AuroraServerOptions options)
    {
        var reason = args.Length > 1 ? string.Join(' ', args[1..]).Trim() : string.Empty;
        if (reason.Length == 0)
        {
            Console.WriteLine(
                "[Aurora] Usage: seal-audit-break <reason>. The reason is written into the log "
                + "and stays there.");
            return true;
        }

        Console.WriteLine(
            "This does not repair anything. Records before the seal stay exactly as they are and");
        Console.WriteLine(
            "can never be verified again; the seal says so, permanently, in the audit log itself.");
        Console.Write("Type SEAL to continue: ");

        if (Console.ReadLine()?.Trim() != "SEAL")
        {
            Console.WriteLine("[Aurora] Nothing was sealed.");
            return true;
        }

        var factory = new SqliteConnectionFactory(options.DbPath);
        new SqliteDatabase(factory).Initialize();

        var clock = new SystemClock();
        var store = new SqliteAuditStore(
            factory, clock, AuditKeyFile.LoadOrCreate(options.AuditKeyPath),
            new AuditAnchorFile(options.AuditAnchorPath));

        var recordHash = store
            .SealBreakAsync(reason, Environment.UserName, CancellationToken.None)
            .GetAwaiter().GetResult();

        Console.WriteLine($"[Aurora] Sealed. The new chain starts at record {recordHash[..16]}...");
        Console.WriteLine("[Aurora] Verification will report the seam from here on.");
        return true;
    }
}
