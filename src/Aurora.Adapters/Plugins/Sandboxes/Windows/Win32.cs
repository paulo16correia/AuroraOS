using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Aurora.Adapters.Plugins.Sandboxes.Windows;

/// <summary>
/// The Windows calls an AppContainer needs, and nothing else.
/// </summary>
/// <remarks>
/// Every one of these is here because the managed surface does not reach it.
/// <see cref="System.Diagnostics.ProcessStartInfo"/> cannot carry a security-capabilities
/// attribute, so creating a confined process means <c>CreateProcessW</c> by hand, which means
/// creating the pipes by hand, which means the attribute list and the handle list by hand.
/// <para>
/// Kept to declarations. Everything that decides anything lives in
/// <see cref="AppContainerProfiles"/> and <see cref="AppContainerVerdict"/>, where it can be
/// tested on a machine that is not Windows — this file cannot be, and the smaller it is the less
/// of the implementation is beyond reach.
/// </para>
/// <para>
/// <c>DllImport</c> rather than <c>LibraryImport</c>: the source generator emits unsafe code, and
/// turning <c>AllowUnsafeBlocks</c> on for the whole of Aurora.Adapters to save a little
/// marshalling on a call made once per plugin start is a poor trade in this codebase.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class Win32
{
    internal const int ErrorAlreadyExists = unchecked((int)0x800700B5);
    internal const uint CreateSuspended = 0x00000004;
    internal const uint ExtendedStartupInfoPresent = 0x00080000;
    internal const uint CreateUnicodeEnvironment = 0x00000400;
    internal const uint CreateNoWindow = 0x08000000;
    internal const uint StartfUseStdHandles = 0x00000100;
    internal const int HandleFlagInherit = 0x00000001;

    internal const int ProcThreadAttributeSecurityCapabilities = 0x00020009;
    internal const int ProcThreadAttributeHandleList = 0x00020002;

    internal const uint TokenQuery = 0x0008;
    internal const int TokenIsAppContainer = 29;
    internal const int TokenAppContainerSid = 31;

    internal const int WinCapabilityInternetClientSid = 116;

    internal const uint JobObjectExtendedLimitInformation = 9;
    internal const uint JobObjectLimitKillOnJobClose = 0x00002000;

    [StructLayout(LayoutKind.Sequential)]
    internal struct SecurityAttributes
    {
        internal int Length;
        internal IntPtr SecurityDescriptor;
        internal int InheritHandle;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SidAndAttributes
    {
        internal IntPtr Sid;
        internal uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SecurityCapabilities
    {
        internal IntPtr AppContainerSid;
        internal IntPtr Capabilities;
        internal uint CapabilityCount;
        internal uint Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct StartupInfoEx
    {
        internal StartupInfo StartupInfo;
        internal IntPtr AttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct StartupInfo
    {
        internal int Cb;
        internal IntPtr Reserved;
        internal IntPtr Desktop;
        internal IntPtr Title;
        internal int X;
        internal int Y;
        internal int XSize;
        internal int YSize;
        internal int XCountChars;
        internal int YCountChars;
        internal int FillAttribute;
        internal uint Flags;
        internal short ShowWindow;
        internal short Reserved2;
        internal IntPtr Reserved3;
        internal IntPtr StdInput;
        internal IntPtr StdOutput;
        internal IntPtr StdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ProcessInformation
    {
        internal IntPtr Process;
        internal IntPtr Thread;
        internal int ProcessId;
        internal int ThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct JobObjectBasicLimitInformation
    {
        internal long PerProcessUserTimeLimit;
        internal long PerJobUserTimeLimit;
        internal uint LimitFlags;
        internal UIntPtr MinimumWorkingSetSize;
        internal UIntPtr MaximumWorkingSetSize;
        internal uint ActiveProcessLimit;
        internal UIntPtr Affinity;
        internal uint PriorityClass;
        internal uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct IoCounters
    {
        internal ulong ReadOperationCount;
        internal ulong WriteOperationCount;
        internal ulong OtherOperationCount;
        internal ulong ReadTransferCount;
        internal ulong WriteTransferCount;
        internal ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct JobObjectExtendedLimitInformationStruct
    {
        internal JobObjectBasicLimitInformation BasicLimitInformation;
        internal IoCounters IoInfo;
        internal UIntPtr ProcessMemoryLimit;
        internal UIntPtr JobMemoryLimit;
        internal UIntPtr PeakProcessMemoryUsed;
        internal UIntPtr PeakJobMemoryUsed;
    }

    // ---- the container profile ----

    [DllImport("userenv.dll", CharSet = CharSet.Unicode)]
    internal static extern int CreateAppContainerProfile(
        string appContainerName, string displayName, string description,
        IntPtr capabilities, uint capabilityCount, out IntPtr sid);

    [DllImport("userenv.dll", CharSet = CharSet.Unicode)]
    internal static extern int DeriveAppContainerSidFromAppContainerName(
        string appContainerName, out IntPtr sid);

    [DllImport("userenv.dll", CharSet = CharSet.Unicode)]
    internal static extern int DeleteAppContainerProfile(string appContainerName);

    // ---- identifiers ----

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CreateWellKnownSid(
        int wellKnownSidType, IntPtr domainSid, IntPtr sid, ref uint sidSize);

    [DllImport("advapi32.dll", EntryPoint = "ConvertSidToStringSidW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ConvertSidToStringSid(IntPtr sid, out IntPtr stringSid);

    [DllImport("kernel32.dll")]
    internal static extern IntPtr LocalFree(IntPtr memory);

    [DllImport("advapi32.dll")]
    internal static extern IntPtr FreeSid(IntPtr sid);

    // ---- the child's token, which is the whole point ----

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetTokenInformation(
        IntPtr token, int informationClass, IntPtr information, uint length, out uint returned);

    // ---- creating it ----

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CreatePipe(
        out SafeFileHandle read, out SafeFileHandle write, ref SecurityAttributes attributes, int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetHandleInformation(SafeFileHandle handle, int mask, int flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool InitializeProcThreadAttributeList(
        IntPtr attributeList, int attributeCount, int flags, ref IntPtr size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UpdateProcThreadAttribute(
        IntPtr attributeList, uint flags, IntPtr attribute, IntPtr value, IntPtr size,
        IntPtr previousValue, IntPtr returnSize);

    [DllImport("kernel32.dll")]
    internal static extern void DeleteProcThreadAttributeList(IntPtr attributeList);

    [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", SetLastError = true,
        CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CreateProcess(
        string? applicationName, string commandLine, IntPtr processAttributes,
        IntPtr threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint creationFlags, IntPtr environment, string? currentDirectory,
        ref StartupInfoEx startupInfo, out ProcessInformation processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern uint ResumeThread(IntPtr thread);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool TerminateProcess(IntPtr process, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseHandle(IntPtr handle);

    // ---- so that a plugin does not outlive Aurora ----

    [DllImport("kernel32.dll", EntryPoint = "CreateJobObjectW", SetLastError = true)]
    internal static extern IntPtr CreateJobObject(IntPtr attributes, IntPtr name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetInformationJobObject(
        IntPtr job, uint informationClass, IntPtr information, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
}
