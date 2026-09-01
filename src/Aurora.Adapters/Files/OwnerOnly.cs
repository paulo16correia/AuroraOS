using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Aurora.Adapters.Files;

/// <summary>
/// Makes a file or directory reachable by the account that owns Aurora and by nobody else.
/// </summary>
/// <remarks>
/// ADR 0003 said the sandbox root "should be writable only by the Aurora process's own user", and
/// <see cref="SandboxGuard"/> answered that with "should is not a control" and applied the mode
/// bits itself. The same sentence was left standing on Windows, where the argument was that a
/// per-user application data directory already carries the right inherited ACL. That is true of
/// the default path and says nothing about a configured one: <c>Aurora:DbPath</c> and
/// <c>Aurora:SandboxRoot</c> are settings, and a key file beside a database somebody put on a
/// shared volume inherits whatever that volume grants.
/// <para>
/// So this does on Windows what the mode bits do on Unix — replaces the discretionary ACL with a
/// single entry for the current user and stops inheritance, rather than trusting where the file
/// happens to live.
/// </para>
/// <para>
/// It reports whether it succeeded instead of throwing. A machine where Aurora cannot
/// re-permission its own key is one where something else is wrong, and refusing to start over it
/// would take away the diagnostics along with the service — <c>aurora doctor</c> asks this
/// question directly and answers it out loud.
/// </para>
/// </remarks>
public static class OwnerOnly
{
    /// <summary>
    /// Restricts an existing file to its owner. Returns whether the restriction now holds.
    /// </summary>
    public static bool File(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return RestrictOnWindows(new FileInfo(path));
        }

        return TryUnixMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    /// <summary>
    /// Restricts an existing directory to its owner. Returns whether the restriction now holds.
    /// </summary>
    /// <remarks>
    /// Execute as well as read and write on Unix: without it the owner cannot traverse into their
    /// own directory, which is not a stricter permission but a broken one.
    /// </remarks>
    public static bool Directory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return RestrictOnWindows(new DirectoryInfo(path));
        }

        return TryUnixMode(
            path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    /// <summary>
    /// Writes a file that was never, at any moment, readable by anybody but its owner.
    /// </summary>
    /// <remarks>
    /// On Unix the mode arrives with the <c>open</c> that creates the file and there is no window
    /// at all. Windows has no equivalent through <see cref="FileStream"/>, so the file is created
    /// empty, restricted, and only then written into — the window exists and what sits in it is an
    /// empty file rather than a key.
    /// <para>
    /// Two opens rather than one, because a handle held with <see cref="FileShare.None"/> cannot
    /// also be opened to change its ACL, and sharing the file while it is still unprotected is the
    /// thing being prevented.
    /// </para>
    /// </remarks>
    public static void Write(string path, FileMode mode, Action<Stream> write)
    {
        using (new FileStream(path, Options(mode)))
        {
        }

        File(path);

        using var stream = new FileStream(path, Options(FileMode.Open));
        write(stream);
    }

    private static FileStreamOptions Options(FileMode mode)
    {
        var options = new FileStreamOptions
        {
            Mode = mode,
            Access = FileAccess.Write,
            Share = FileShare.None,
        };

        // Only where the open can create the file: .NET rejects a create mode on FileMode.Open,
        // and the second open in Write is exactly that — by then the file exists and already
        // carries the mode this would have asked for.
        if (!OperatingSystem.IsWindows() && mode != FileMode.Open && mode != FileMode.Truncate)
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        return options;
    }

    [UnsupportedOSPlatform("windows")]
    private static bool TryUnixMode(string path, UnixFileMode mode)
    {
        try
        {
            System.IO.File.SetUnixFileMode(path, mode);
            return true;
        }
        catch (Exception refused) when (refused is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool RestrictOnWindows(FileSystemInfo info)
    {
        try
        {
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            SecurityIdentifier? owner = identity.User;

            if (owner is null)
            {
                return false;
            }

            // Both branches are the same three moves — stop inheriting, drop what is there, grant
            // the owner — but FileSecurity and DirectorySecurity share no base type that carries
            // them, so the sequence is written twice rather than through reflection.
            if (info is FileInfo file)
            {
                FileSecurity security = file.GetAccessControl();
                Reset(security, security.GetAccessRules(true, false, typeof(SecurityIdentifier)));

                security.AddAccessRule(new FileSystemAccessRule(
                    owner, FileSystemRights.FullControl, AccessControlType.Allow));

                file.SetAccessControl(security);
                return true;
            }

            var directory = (DirectoryInfo)info;
            DirectorySecurity directorySecurity = directory.GetAccessControl();
            Reset(
                directorySecurity,
                directorySecurity.GetAccessRules(true, false, typeof(SecurityIdentifier)));

            // Inherited by what is created inside it, which is the point: the sandbox root exists
            // so that the files under it are the owner's alone, and a rule that stopped at the
            // directory would leave every file in it carrying whatever the volume grants.
            directorySecurity.AddAccessRule(new FileSystemAccessRule(
                owner,
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));

            directory.SetAccessControl(directorySecurity);
            return true;
        }
        catch (Exception refused) when (
            refused is IOException or UnauthorizedAccessException
                or PrivilegeNotHeldException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    [SupportedOSPlatform("windows")]
    private static void Reset(FileSystemSecurity security, AuthorizationRuleCollection existing)
    {
        // Protection first. Removing the inherited entries before detaching from the parent does
        // nothing — they are not this object's to remove — and the ACL would end up with the
        // owner's entry added to everything it was supposed to replace.
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

        foreach (AuthorizationRule rule in existing)
        {
            if (rule is FileSystemAccessRule access)
            {
                security.RemoveAccessRuleSpecific(access);
            }
        }
    }
}
