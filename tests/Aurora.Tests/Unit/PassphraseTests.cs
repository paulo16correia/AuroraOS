using Aurora.Adapters.Consent;
using Aurora.Core.Abstractions;
using Aurora.Tests.Support;
using Xunit;

namespace Aurora.Tests.Unit;

public sealed class PassphraseTests : IDisposable
{
    private readonly string _path =
        TestTemp.Path("pass") + ".json";

    // Low iteration count: these tests exercise the policy around the KDF, not the KDF's cost.
    private static readonly PassphraseOptions Fast = new(Iterations: 1_000, FailuresBeforeLockout: 3);

    private Pbkdf2PassphraseAuthenticator New(DateTimeOffset? now = null) =>
        new(_path, new TestClock(now ?? DateTimeOffset.UnixEpoch), Fast);

    public void Dispose()
    {
        if (File.Exists(_path))
        {
            File.Delete(_path);
        }
    }

    [Fact]
    public void NotEnrolled_ReportsSo()
    {
        var auth = New();

        Assert.False(auth.IsEnrolled);
        Assert.Equal(PassphraseOutcome.NotEnrolled, auth.Verify("anything").Outcome);
    }

    [Fact]
    public void EnrollThenVerify()
    {
        var auth = New();
        auth.Enroll("correct horse battery");

        Assert.True(auth.IsEnrolled);
        Assert.Equal(PassphraseOutcome.Verified, auth.Verify("correct horse battery").Outcome);
        Assert.Equal(PassphraseOutcome.Rejected, auth.Verify("wrong passphrase").Outcome);
    }

    [Fact]
    public void Verify_WithNoPassphraseSupplied_IsRejectedNotAccepted()
    {
        var auth = New();
        auth.Enroll("correct horse battery");

        Assert.Equal(PassphraseOutcome.Rejected, auth.Verify(null).Outcome);
        Assert.Equal(PassphraseOutcome.Rejected, auth.Verify(string.Empty).Outcome);
    }

    [Fact]
    public void Enroll_RefusesToOverwriteSilently()
    {
        var auth = New();
        auth.Enroll("correct horse battery");

        Assert.Throws<InvalidOperationException>(() => auth.Enroll("a different one"));
    }

    [Fact]
    public void Enroll_RejectsAShortPassphrase()
    {
        Assert.Throws<ArgumentException>(() => New().Enroll("short"));
    }

    [Fact]
    public void Revoke_RemovesTheGuard()
    {
        var auth = New();
        auth.Enroll("correct horse battery");
        auth.Revoke();

        Assert.False(auth.IsEnrolled);
        Assert.Equal(PassphraseOutcome.NotEnrolled, auth.Verify("correct horse battery").Outcome);
    }

    [Fact]
    public void PlaintextIsNeverStored()
    {
        var auth = New();
        auth.Enroll("correct horse battery");

        var contents = File.ReadAllText(_path);

        Assert.DoesNotContain("correct horse battery", contents, StringComparison.Ordinal);
    }

    [Fact]
    public void Salt_MakesTwoEnrollmentsOfTheSamePassphraseDiffer()
    {
        var auth = New();
        auth.Enroll("correct horse battery");
        var first = File.ReadAllText(_path);

        auth.Revoke();
        auth.Enroll("correct horse battery");
        var second = File.ReadAllText(_path);

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void Lockout_EngagesAfterRepeatedFailures()
    {
        var auth = New();
        auth.Enroll("correct horse battery");

        Assert.Equal(PassphraseOutcome.Rejected, auth.Verify("no").Outcome);
        Assert.Equal(PassphraseOutcome.Rejected, auth.Verify("no").Outcome);

        var third = auth.Verify("no");

        Assert.Equal(PassphraseOutcome.LockedOut, third.Outcome);
        Assert.NotNull(third.LockedUntilUtc);
    }

    [Fact]
    public void Lockout_RefusesEvenTheCorrectPassphrase()
    {
        var auth = New();
        auth.Enroll("correct horse battery");
        for (var i = 0; i < 3; i++)
        {
            auth.Verify("no");
        }

        // Otherwise an attacker could keep guessing and simply ignore the lockout on a hit.
        Assert.Equal(PassphraseOutcome.LockedOut, auth.Verify("correct horse battery").Outcome);
    }

    [Fact]
    public void Lockout_ExpiresAndThenTheCorrectPassphraseWorks()
    {
        var start = DateTimeOffset.UnixEpoch;
        var auth = New(start);
        auth.Enroll("correct horse battery");
        for (var i = 0; i < 3; i++)
        {
            auth.Verify("no");
        }

        var later = New(start.AddHours(1));

        Assert.Equal(PassphraseOutcome.Verified, later.Verify("correct horse battery").Outcome);
    }

    [Fact]
    public void SuccessfulVerify_ClearsTheFailureCount()
    {
        var auth = New();
        auth.Enroll("correct horse battery");
        auth.Verify("no");
        auth.Verify("no");
        Assert.Equal(PassphraseOutcome.Verified, auth.Verify("correct horse battery").Outcome);

        // The counter reset, so two more failures must not trip the lockout.
        Assert.Equal(PassphraseOutcome.Rejected, auth.Verify("no").Outcome);
        Assert.Equal(PassphraseOutcome.Rejected, auth.Verify("no").Outcome);
    }

    [Fact]
    public void CorruptFile_FailsClosedRatherThanReportingNotEnrolled()
    {
        var auth = New();
        auth.Enroll("correct horse battery");
        File.WriteAllText(_path, "{ this is not the file we wrote");

        // Reporting NotEnrolled here would silently disable the guard, which is the one outcome a
        // corrupt file must never produce.
        Assert.True(auth.IsEnrolled);
        Assert.Equal(PassphraseOutcome.Rejected, auth.Verify("correct horse battery").Outcome);
    }
}
