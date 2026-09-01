using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Aurora.Core.Abstractions;
using Microsoft.Win32.SafeHandles;

namespace Aurora.Adapters.Plugins.Sandboxes.Windows;

/// <summary>
/// Confinement on Windows: an AppContainer, proved before the plugin runs (docs/adr/0072).
/// </summary>
/// <remarks>
/// The shape of this is decided by one requirement: <b>a plugin must never execute an instruction
/// before Aurora has demonstrated that it is confined.</b> Checking afterwards would make the
/// interesting failure the one where a plugin does its work in the moments before the check
/// catches up.
/// <para>
/// So the process is created suspended, its token is opened and questioned, and only a token that
/// answers correctly earns a <c>ResumeThread</c>. Anything else — a token that cannot be read, one
/// that is not an app container, one in a container Aurora did not create — is terminated where it
/// stands, having run nothing.
/// </para>
/// <para>
/// <b>What confines it.</b> An AppContainer reaches no part of the filesystem that has not named
/// its SID, so the default is deny and Aurora adds exactly two entries: read-and-execute where the
/// program lives, and full control of the plugin's own directory. It reaches no network without
/// the <c>internetClient</c> capability, which is added only when the owner granted the network —
/// and even then Windows refuses it loopback, so a networked plugin still cannot reach Aurora's
/// own endpoint. It inherits exactly three handles, named one by one, so nothing of Aurora's is
/// reachable through a handle it happened to be holding.
/// </para>
/// <para>
/// <b>What is unverified.</b> Everything in this file. It has never run: it was written on a Mac,
/// and no line of the interop below has met a Windows kernel. That is why the verification exists
/// in the form it does — if any of this is wrong, the first machine to run it terminates the child
/// and reports a refusal, rather than reporting a confinement it did not achieve. See
/// <c>docs/reference/platform-support.md</c>, which says UNVERIFIED and will keep saying it until
/// somebody runs the suite on Windows.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsAppContainerSandbox : IPluginSandbox
{
    internal const string Mechanism = "AppContainer";

    public SandboxPlan Plan(SandboxRequest request) => new(
        // No wrapper program: the plan names the plugin itself, because on this platform the
        // confinement is carried by the token rather than by something standing in front.
        request.Executable,
        [],
        SandboxLevel.Confined,
        Mechanism,
        []);

    public Task<SandboxStart> StartAsync(SandboxLaunch launch, CancellationToken ct)
    {
        AppContainerProfile profile = AppContainerProfiles.For(launch.Request);

        try
        {
            return Task.FromResult(Start(launch, profile));
        }
        catch (Exception failed) when (failed is DllNotFoundException or EntryPointNotFoundException)
        {
            // A Windows without the app-container APIs. Refusing is the only honest answer: there
            // is no weaker confinement to fall back to that would still be confinement.
            return Task.FromResult(new SandboxStart(
                null,
                $"this Windows does not offer the AppContainer API ({failed.GetType().Name}), so "
                + "the plugin cannot be confined and was not started"));
        }
    }

    private static SandboxStart Start(SandboxLaunch launch, AppContainerProfile profile)
    {
        IntPtr containerSid = IntPtr.Zero;
        IntPtr capabilities = IntPtr.Zero;
        var capabilitySids = new List<IntPtr>();
        ProcessAttributes attributes = default;

        AnonymousPipes? pipes = null;
        Win32.ProcessInformation process = default;
        IntPtr environment = IntPtr.Zero;

        try
        {
            if (CreateOrOpenProfile(profile, out containerSid) is { } profileFailure)
            {
                return new SandboxStart(null, profileFailure);
            }

            var expectedSid = SidString(containerSid);

            if (expectedSid is null)
            {
                return new SandboxStart(
                    null, "the container was created but Windows would not name it");
            }

            // The grants come before the process, because a container that cannot read its own
            // program fails in a way that looks like a broken plugin rather than a missing ACE.
            if (GrantPaths(profile, containerSid) is { } grantFailure)
            {
                return new SandboxStart(null, grantFailure);
            }

            capabilities = BuildCapabilities(profile, capabilitySids);

            pipes = AnonymousPipes.Create();

            if (pipes is null)
            {
                return new SandboxStart(null, "the pipes to talk to the plugin could not be made");
            }

            attributes = BuildAttributeList(containerSid, capabilities, profile, pipes);

            if (attributes.List == IntPtr.Zero)
            {
                return new SandboxStart(
                    null, "the process attributes carrying the confinement could not be built");
            }

            var startup = new Win32.StartupInfoEx
            {
                StartupInfo = new Win32.StartupInfo
                {
                    Cb = Marshal.SizeOf<Win32.StartupInfoEx>(),
                    Flags = Win32.StartfUseStdHandles,
                    StdInput = pipes.ChildInput.DangerousGetHandle(),
                    StdOutput = pipes.ChildOutput.DangerousGetHandle(),
                    StdError = pipes.ChildError.DangerousGetHandle(),
                },
                AttributeList = attributes.List,
            };

            var created = Win32.CreateProcess(
                null,
                CommandLine(launch),
                IntPtr.Zero,
                IntPtr.Zero,
                inheritHandles: true,

                // Suspended, so that what follows happens before the plugin runs at all.
                Win32.CreateSuspended | Win32.ExtendedStartupInfoPresent
                    | Win32.CreateUnicodeEnvironment | Win32.CreateNoWindow,
                environment = EnvironmentBlock(launch.Environment),
                launch.WorkingDirectory,
                ref startup,
                out process);

            if (!created)
            {
                return new SandboxStart(
                    null,
                    "the confined process could not be created "
                    + $"(error {Marshal.GetLastWin32Error()})");
            }

            AppContainerVerdict verdict = Verify(process.Process, expectedSid);

            if (!verdict.Confined)
            {
                // Terminated first, answered second. Nothing has run, and nothing will.
                Win32.TerminateProcess(process.Process, 1);
                Win32.CloseHandle(process.Thread);
                Win32.CloseHandle(process.Process);
                process = default;

                return new SandboxStart(null, $"{Mechanism}: {verdict.Refused}");
            }

            // A job the child cannot leave, set to die when Aurora's handle to it closes. The
            // equivalent of bubblewrap's --die-with-parent: a plugin that outlives the thing that
            // was governing it is an unconfined program with a token nobody is watching.
            IntPtr job = CreateDyingJob(process.Process);

            if (Win32.ResumeThread(process.Thread) == unchecked((uint)-1))
            {
                Win32.TerminateProcess(process.Process, 1);

                return new SandboxStart(
                    null, "the confined process was created and could not be resumed");
            }

            Win32.CloseHandle(process.Thread);

            var started = new AppContainerProcess(process.Process, job, pipes);

            // Ownership has moved; the finally block must not close what the process now holds.
            pipes = null;
            process = default;

            return new SandboxStart(started);
        }
        finally
        {
            if (process.Process != IntPtr.Zero)
            {
                Win32.TerminateProcess(process.Process, 1);
                Win32.CloseHandle(process.Thread);
                Win32.CloseHandle(process.Process);
            }

            pipes?.Dispose();

            // After CreateProcess and not before: the kernel reads the security capabilities and
            // the handle list out of these buffers while it is creating the process, so freeing
            // them any earlier hands it memory somebody else may already be using.
            attributes.Free();

            if (environment != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(environment);
            }

            if (capabilities != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(capabilities);
            }

            foreach (IntPtr sid in capabilitySids)
            {
                Marshal.FreeHGlobal(sid);
            }

            if (containerSid != IntPtr.Zero)
            {
                Win32.FreeSid(containerSid);
            }
        }
    }

    /// <summary>
    /// Asks the kernel what the child actually is, and hands the answers to the verdict.
    /// </summary>
    /// <remarks>
    /// Every failure here is read as "not demonstrated" rather than "probably fine", which is why
    /// the flags default to the refusing value and are only set by a call that succeeded.
    /// </remarks>
    private static AppContainerVerdict Verify(IntPtr process, string expectedSid)
    {
        IntPtr token = IntPtr.Zero;

        try
        {
            if (!Win32.OpenProcessToken(process, Win32.TokenQuery, out token))
            {
                return AppContainerVerdict.Of(false, false, null, expectedSid);
            }

            var isAppContainer = ReadFlag(token, Win32.TokenIsAppContainer);
            var actual = ReadContainerSid(token);

            return AppContainerVerdict.Of(true, isAppContainer, actual, expectedSid);
        }
        finally
        {
            if (token != IntPtr.Zero)
            {
                Win32.CloseHandle(token);
            }
        }
    }

    private static bool ReadFlag(IntPtr token, int informationClass)
    {
        IntPtr buffer = Marshal.AllocHGlobal(sizeof(uint));

        try
        {
            return Win32.GetTokenInformation(token, informationClass, buffer, sizeof(uint), out _)
                && Marshal.ReadInt32(buffer) != 0;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>The container the process is really in, as a string, or null if it would not say.</summary>
    private static string? ReadContainerSid(IntPtr token)
    {
        Win32.GetTokenInformation(token, Win32.TokenAppContainerSid, IntPtr.Zero, 0, out var size);

        if (size == 0)
        {
            return null;
        }

        IntPtr buffer = Marshal.AllocHGlobal((int)size);

        try
        {
            if (!Win32.GetTokenInformation(token, Win32.TokenAppContainerSid, buffer, size, out _))
            {
                return null;
            }

            // TOKEN_APPCONTAINER_INFORMATION is one pointer to the SID.
            return SidString(Marshal.ReadIntPtr(buffer));
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static string? SidString(IntPtr sid)
    {
        if (sid == IntPtr.Zero || !Win32.ConvertSidToStringSid(sid, out IntPtr text))
        {
            return null;
        }

        try
        {
            return Marshal.PtrToStringUni(text);
        }
        finally
        {
            Win32.LocalFree(text);
        }
    }

    /// <summary>Creates the container, or opens the one an earlier run left behind.</summary>
    private static string? CreateOrOpenProfile(AppContainerProfile profile, out IntPtr sid)
    {
        var result = Win32.CreateAppContainerProfile(
            profile.Name, profile.DisplayName, profile.Description, IntPtr.Zero, 0, out sid);

        if (result >= 0)
        {
            return null;
        }

        if (result == Win32.ErrorAlreadyExists)
        {
            // Expected on every start after the first. The name is derived from the plugin id, so
            // the container that exists is this plugin's own.
            return Win32.DeriveAppContainerSidFromAppContainerName(profile.Name, out sid) >= 0
                ? null
                : "the plugin's existing AppContainer profile could not be opened";
        }

        return $"the AppContainer profile could not be created (0x{result:X8})";
    }

    /// <summary>
    /// Puts the container's SID on the two paths it is allowed to touch, and on nothing else.
    /// </summary>
    private static string? GrantPaths(AppContainerProfile profile, IntPtr containerSid)
    {
        var sid = SidString(containerSid);

        if (sid is null)
        {
            return "the container has no SID to grant paths to";
        }

        var identity = new SecurityIdentifier(sid);

        foreach (AppContainerGrant grant in profile.Grants)
        {
            FileSystemRights rights = grant.Access == AppContainerAccess.Full
                ? FileSystemRights.FullControl
                : FileSystemRights.ReadAndExecute;

            try
            {
                var directory = new DirectoryInfo(grant.Path);
                DirectorySecurity security = directory.GetAccessControl();

                security.AddAccessRule(new FileSystemAccessRule(
                    identity,
                    rights,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None,
                    AccessControlType.Allow));

                directory.SetAccessControl(security);
            }
            catch (Exception refused) when (
                refused is IOException or UnauthorizedAccessException
                    or PrivilegeNotHeldException or DirectoryNotFoundException)
            {
                // Not started. A container that cannot reach its own program fails in a way that
                // looks like a broken plugin, and one that cannot reach its working directory
                // fails on the first thing it writes.
                return $"'{grant.Path}' could not be granted to the container ({refused.GetType().Name})";
            }
        }

        return null;
    }

    /// <summary>
    /// The capability SIDs, allocated to survive the <c>CreateProcess</c> call that reads them.
    /// </summary>
    private static IntPtr BuildCapabilities(AppContainerProfile profile, List<IntPtr> allocated)
    {
        if (profile.Capabilities.Count == 0)
        {
            return IntPtr.Zero;
        }

        var array = Marshal.AllocHGlobal(
            Marshal.SizeOf<Win32.SidAndAttributes>() * profile.Capabilities.Count);

        for (var index = 0; index < profile.Capabilities.Count; index++)
        {
            var wellKnown = profile.Capabilities[index] switch
            {
                AppContainerCapability.InternetClient => Win32.WinCapabilityInternetClientSid,
                _ => throw new InvalidOperationException(
                    $"no SID is mapped for {profile.Capabilities[index]}"),
            };

            // SECURITY_MAX_SID_SIZE. One allocation, sized for the largest a SID can be, rather
            // than the usual ask-for-the-size dance for something this small.
            var size = 68u;
            IntPtr sid = Marshal.AllocHGlobal((int)size);
            allocated.Add(sid);

            if (!Win32.CreateWellKnownSid(wellKnown, IntPtr.Zero, sid, ref size))
            {
                throw new InvalidOperationException(
                    $"the {profile.Capabilities[index]} capability SID could not be made");
            }

            Marshal.StructureToPtr(
                new Win32.SidAndAttributes { Sid = sid, Attributes = 4 /* SE_GROUP_ENABLED */ },
                array + (Marshal.SizeOf<Win32.SidAndAttributes>() * index),
                fDeleteOld: false);
        }

        return array;
    }

    /// <summary>
    /// The two attributes that make this a confined process: which container, and which handles.
    /// </summary>
    /// <remarks>
    /// The handle list is the quieter of the two and matters as much. Without it,
    /// <c>bInheritHandles</c> hands the child every inheritable handle Aurora happens to be
    /// holding; with it, the child gets exactly three pipes and nothing else, whatever else is
    /// open elsewhere in the process.
    /// </remarks>
    private static ProcessAttributes BuildAttributeList(
        IntPtr containerSid, IntPtr capabilities, AppContainerProfile profile, AnonymousPipes pipes)
    {
        IntPtr size = IntPtr.Zero;
        Win32.InitializeProcThreadAttributeList(IntPtr.Zero, 2, 0, ref size);

        if (size == IntPtr.Zero)
        {
            return default;
        }

        IntPtr list = Marshal.AllocHGlobal(size);

        if (!Win32.InitializeProcThreadAttributeList(list, 2, 0, ref size))
        {
            Marshal.FreeHGlobal(list);
            return default;
        }

        IntPtr security = Marshal.AllocHGlobal(Marshal.SizeOf<Win32.SecurityCapabilities>());
        Marshal.StructureToPtr(
            new Win32.SecurityCapabilities
            {
                AppContainerSid = containerSid,
                Capabilities = capabilities,
                CapabilityCount = (uint)profile.Capabilities.Count,
            },
            security,
            fDeleteOld: false);

        IntPtr handles = Marshal.AllocHGlobal(IntPtr.Size * 3);
        Marshal.WriteIntPtr(handles, 0, pipes.ChildInput.DangerousGetHandle());
        Marshal.WriteIntPtr(handles, IntPtr.Size, pipes.ChildOutput.DangerousGetHandle());
        Marshal.WriteIntPtr(handles, IntPtr.Size * 2, pipes.ChildError.DangerousGetHandle());

        var ok = Win32.UpdateProcThreadAttribute(
                list, 0, Win32.ProcThreadAttributeSecurityCapabilities, security,
                Marshal.SizeOf<Win32.SecurityCapabilities>(), IntPtr.Zero, IntPtr.Zero)
            && Win32.UpdateProcThreadAttribute(
                list, 0, Win32.ProcThreadAttributeHandleList, handles,
                IntPtr.Size * 3, IntPtr.Zero, IntPtr.Zero);

        if (ok)
        {
            return new ProcessAttributes(list, security, handles);
        }

        new ProcessAttributes(list, security, handles).Free();

        return default;
    }

    /// <summary>
    /// The attribute list and the two buffers it points into, freed together and not before.
    /// </summary>
    /// <remarks>
    /// <c>UpdateProcThreadAttribute</c> stores the pointers rather than copying what they hold, so
    /// the security capabilities and the handle list have to stay allocated until CreateProcess has
    /// read them. Keeping the three together is what stops the two buffers being forgotten, which
    /// is what happened when the list alone was returned.
    /// </remarks>
    private readonly record struct ProcessAttributes(IntPtr List, IntPtr Security, IntPtr Handles)
    {
        internal void Free()
        {
            if (List != IntPtr.Zero)
            {
                Win32.DeleteProcThreadAttributeList(List);
                Marshal.FreeHGlobal(List);
            }

            if (Security != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(Security);
            }

            if (Handles != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(Handles);
            }
        }
    }

    private static IntPtr CreateDyingJob(IntPtr process)
    {
        IntPtr job = Win32.CreateJobObject(IntPtr.Zero, IntPtr.Zero);

        if (job == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        var limits = new Win32.JobObjectExtendedLimitInformationStruct
        {
            BasicLimitInformation = new Win32.JobObjectBasicLimitInformation
            {
                LimitFlags = Win32.JobObjectLimitKillOnJobClose,
            },
        };

        var size = Marshal.SizeOf<Win32.JobObjectExtendedLimitInformationStruct>();
        IntPtr buffer = Marshal.AllocHGlobal(size);

        try
        {
            Marshal.StructureToPtr(limits, buffer, fDeleteOld: false);
            Win32.SetInformationJobObject(
                job, Win32.JobObjectExtendedLimitInformation, buffer, (uint)size);
            Win32.AssignProcessToJobObject(job, process);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return job;
    }

    /// <summary>
    /// The command line, quoted the way <c>CreateProcess</c> parses it back.
    /// </summary>
    private static string CommandLine(SandboxLaunch launch)
    {
        var line = new StringBuilder();
        line.Append('"').Append(launch.Executable.Replace("\"", "\\\"", StringComparison.Ordinal))
            .Append('"');

        foreach (var argument in launch.Plan.Arguments)
        {
            line.Append(" \"")
                .Append(argument.Replace("\"", "\\\"", StringComparison.Ordinal))
                .Append('"');
        }

        return line.ToString();
    }

    /// <summary>
    /// The child's whole environment, as the double-null-terminated block CreateProcess wants.
    /// </summary>
    /// <remarks>
    /// Built rather than inherited. Passing <see cref="IntPtr.Zero"/> here would give the child
    /// Aurora's own environment, which is the thing the hosts clear it for.
    /// </remarks>
    private static IntPtr EnvironmentBlock(IReadOnlyDictionary<string, string> environment)
    {
        var block = new StringBuilder();

        foreach ((var name, var value) in environment.OrderBy(e => e.Key, StringComparer.Ordinal))
        {
            block.Append(name).Append('=').Append(value).Append('\0');
        }

        block.Append('\0');

        return Marshal.StringToHGlobalUni(block.ToString());
    }
}

/// <summary>The three pipes, and which end belongs to whom.</summary>
[SupportedOSPlatform("windows")]
internal sealed class AnonymousPipes : IDisposable
{
    internal required SafeFileHandle ChildInput { get; init; }

    internal required SafeFileHandle ChildOutput { get; init; }

    internal required SafeFileHandle ChildError { get; init; }

    internal required SafeFileHandle ParentInput { get; init; }

    internal required SafeFileHandle ParentOutput { get; init; }

    internal required SafeFileHandle ParentError { get; init; }

    internal static AnonymousPipes? Create()
    {
        var attributes = new Win32.SecurityAttributes
        {
            Length = Marshal.SizeOf<Win32.SecurityAttributes>(),
            InheritHandle = 1,
        };

        // Aurora writes to the child's input; the child writes to the other two. Each pair is
        // created inheritable and then the parent's own end is made not-inheritable, so the child
        // receives one end of each pipe and never a handle to the end Aurora is holding.
        if (!Win32.CreatePipe(out SafeFileHandle childInput, out SafeFileHandle parentInput,
                ref attributes, 0)
            || !Win32.CreatePipe(out SafeFileHandle parentOutput, out SafeFileHandle childOutput,
                ref attributes, 0)
            || !Win32.CreatePipe(out SafeFileHandle parentError, out SafeFileHandle childError,
                ref attributes, 0))
        {
            return null;
        }

        Win32.SetHandleInformation(parentInput, Win32.HandleFlagInherit, 0);
        Win32.SetHandleInformation(parentOutput, Win32.HandleFlagInherit, 0);
        Win32.SetHandleInformation(parentError, Win32.HandleFlagInherit, 0);

        return new AnonymousPipes
        {
            ChildInput = childInput,
            ChildOutput = childOutput,
            ChildError = childError,
            ParentInput = parentInput,
            ParentOutput = parentOutput,
            ParentError = parentError,
        };
    }

    /// <summary>Closes the child's ends, which Aurora holds only until the process has them.</summary>
    internal void CloseChildEnds()
    {
        ChildInput.Dispose();
        ChildOutput.Dispose();
        ChildError.Dispose();
    }

    public void Dispose()
    {
        CloseChildEnds();
        ParentInput.Dispose();
        ParentOutput.Dispose();
        ParentError.Dispose();
    }
}
