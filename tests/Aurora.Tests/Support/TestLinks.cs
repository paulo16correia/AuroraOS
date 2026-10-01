using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Aurora.Tests.Support;

/// <summary>
/// Makes the kind of link a sandbox has to refuse to follow, on whichever platform is running.
/// </summary>
/// <remarks>
/// <b>Why this is not just <c>Directory.CreateSymbolicLink</c>.</b> Windows refuses that to an
/// unprivileged process — <c>a required privilege is not held by the client</c> — unless Developer
/// Mode is on. Three tests of a real security property therefore could not build their fixture on
/// an ordinary Windows machine, and a property nothing checks is a property nobody is keeping.
/// <para>
/// The answer is that on Windows the interesting link is a different one. A <b>junction</b> is a
/// reparse point that redirects a directory and <b>any user can create one</b>, which makes it the
/// escape a plugin could actually attempt there — more so than a symlink, which needs a privilege
/// the attacker would not have either. So this asks for a symlink first, because that is the same
/// thing the other platforms are tested with, and falls back to a junction where Windows will not
/// give one. Either way the test asserts the same refusal against the same production code.
/// </para>
/// </remarks>
public static class TestLinks
{
    /// <summary>A link at <paramref name="path"/> leading to the directory <paramref name="target"/>.</summary>
    public static void Directory(string path, string target)
    {
        try
        {
            System.IO.Directory.CreateSymbolicLink(path, target);
            return;
        }
        catch (IOException) when (OperatingSystem.IsWindows())
        {
            // No SeCreateSymbolicLinkPrivilege and no Developer Mode. A junction needs neither.
        }

        Junction(path, target);
    }

    /// <summary>
    /// A link at <paramref name="path"/> leading to the file <paramref name="target"/>, or
    /// <see langword="false"/> if this machine will not make one.
    /// </summary>
    /// <remarks>
    /// Returns rather than throws because Windows has no unprivileged equivalent for a file:
    /// junctions redirect directories only, and a hard link is not a link the resolver can see.
    /// A caller that cannot make one has to say so rather than quietly assert nothing.
    /// </remarks>
    public static bool TryFile(string path, string target)
    {
        try
        {
            File.CreateSymbolicLink(path, target);
            return true;
        }
        catch (IOException) when (OperatingSystem.IsWindows())
        {
            return false;
        }
    }

    /// <summary>
    /// A hard link at <paramref name="path"/> to the existing file <paramref name="target"/>.
    /// </summary>
    /// <remarks>
    /// Unlike a symlink, a hard link needs no privilege on any platform Aurora supports — which is
    /// exactly why it is the interesting case: a plugin, or a leftover from a previous run, can
    /// leave one where Aurora will later read or write. It is a second name for one file, sharing
    /// one security descriptor. Requires write access to the target (the reason a confined plugin
    /// cannot plant one against a file it only reads — docs/adr/0078).
    /// </remarks>
    public static void HardLink(string path, string target)
    {
        if (OperatingSystem.IsWindows())
        {
            if (!CreateHardLinkW(path, target, IntPtr.Zero))
            {
                throw new IOException(
                    $"could not hard-link {path} to {target} (error {Marshal.GetLastWin32Error()})");
            }

            return;
        }

        if (link(target, path) != 0)
        {
            throw new IOException($"could not hard-link {path} to {target}");
        }
    }

    /// <summary>Whether a file link can be made here at all.</summary>
    public static bool FileLinksAvailable => !OperatingSystem.IsWindows() || DeveloperMode.Value;

    private static readonly Lazy<bool> DeveloperMode = new(() =>
    {
        if (!OperatingSystem.IsWindows())
        {
            return true;
        }

        // Asked by trying it, not by reading a registry key: the privilege is what matters and the
        // key is only one of the ways to have it.
        var probe = Path.Combine(TestTemp.Folder("linkprobe"), "link");

        try
        {
            File.CreateSymbolicLink(probe, probe + "-target");
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    });

    /// <summary>
    /// Removes a directory tree that may contain links, without ever following one.
    /// </summary>
    /// <remarks>
    /// <c>Directory.Delete(path, recursive: true)</c> refuses a tree containing a junction —
    /// <c>access to the path is denied</c> — so a fixture that made one cannot clean itself up
    /// with it. Each link is unlinked by itself first, which removes the link and leaves whatever
    /// it pointed at: a cleanup that deleted the target would quietly destroy the very directory
    /// the test is asserting was not written to.
    /// </remarks>
    public static void DeleteTree(string path)
    {
        if (!System.IO.Directory.Exists(path))
        {
            return;
        }

        foreach (var child in System.IO.Directory.EnumerateDirectories(path))
        {
            if (new DirectoryInfo(child).Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                // The link itself, not what it leads to.
                System.IO.Directory.Delete(child);
            }
            else
            {
                DeleteTree(child);
            }
        }

        System.IO.Directory.Delete(path, recursive: true);
    }

    // ---- the junction ----

    private const uint IoReparseTagMountPoint = 0xA0000003;
    private const uint FsctlSetReparsePoint = 0x000900A4;

    [SupportedOSPlatform("windows")]
    private static void Junction(string path, string target)
    {
        // A junction is a reparse point on an existing, empty directory.
        System.IO.Directory.CreateDirectory(path);

        // "\??\" is the object-manager form the reparse point stores; the second is what a person
        // sees. Both are counted in bytes and both are null-terminated in the buffer.
        var substitute = @"\??\" + Path.GetFullPath(target).TrimEnd(Path.DirectorySeparatorChar);
        var print = Path.GetFullPath(target).TrimEnd(Path.DirectorySeparatorChar);

        var substituteBytes = Encoding.Unicode.GetBytes(substitute);
        var printBytes = Encoding.Unicode.GetBytes(print);

        // Two null terminators, and the mount-point header that precedes the names.
        var pathBufferLength = substituteBytes.Length + 2 + printBytes.Length + 2;
        var buffer = new byte[8 + 8 + pathBufferLength];

        BitConverter.TryWriteBytes(buffer.AsSpan(0), IoReparseTagMountPoint);
        BitConverter.TryWriteBytes(buffer.AsSpan(4), (ushort)(8 + pathBufferLength));
        BitConverter.TryWriteBytes(buffer.AsSpan(6), (ushort)0);
        BitConverter.TryWriteBytes(buffer.AsSpan(8), (ushort)0);
        BitConverter.TryWriteBytes(buffer.AsSpan(10), (ushort)substituteBytes.Length);
        BitConverter.TryWriteBytes(buffer.AsSpan(12), (ushort)(substituteBytes.Length + 2));
        BitConverter.TryWriteBytes(buffer.AsSpan(14), (ushort)printBytes.Length);

        substituteBytes.CopyTo(buffer.AsSpan(16));
        printBytes.CopyTo(buffer.AsSpan(16 + substituteBytes.Length + 2));

        using SafeFileHandle handle = CreateFile(
            path,
            0x40000000,           // GENERIC_WRITE
            0x00000003,           // FILE_SHARE_READ | FILE_SHARE_WRITE
            IntPtr.Zero,
            3,                    // OPEN_EXISTING
            0x02200000,           // FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT
            IntPtr.Zero);

        if (handle.IsInvalid)
        {
            throw new IOException(
                $"could not open {path} to make a junction",
                Marshal.GetLastWin32Error());
        }

        if (!DeviceIoControl(
                handle, FsctlSetReparsePoint, buffer, (uint)buffer.Length,
                IntPtr.Zero, 0, out _, IntPtr.Zero))
        {
            throw new IOException(
                $"could not make {path} a junction to {target}", Marshal.GetLastWin32Error());
        }
    }

    // DllImport rather than LibraryImport, matching Aurora's own Win32 interop: the source
    // generator emits unsafe code, and neither project turns that on for two declarations.
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string fileName, uint access, uint share, IntPtr security,
        uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        SafeFileHandle device, uint code,
        byte[] input, uint inputSize,
        IntPtr output, uint outputSize,
        out uint returned, IntPtr overlapped);

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string link, string target, IntPtr attributes);

    [DllImport("libc", SetLastError = true)]
    private static extern int link(string target, string link);
}
