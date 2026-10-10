using System.Runtime.InteropServices;

namespace Haoyue.Runtime.Tools;

/// <summary>
/// Windows Job Object sandbox for bash subprocess trees (G6 遗留项). A job gives the
/// agent's shell commands an OS-enforced fence: per-process/job memory ceilings, an
/// active-process cap, optional UI restrictions (clipboard, system parameters,
/// desktop switch, shutdown) and kill-on-close so a timed-out command cannot leave
/// orphaned grandchildren behind. Network isolation is NOT provided by job objects —
/// that requires container/firewall policy and stays a documented gap.
/// </summary>
public sealed class WindowsJobObject : IDisposable
{
    private IntPtr _handle;
    private readonly int _memoryLimitBytes;
    private readonly int _maxProcesses;
    private readonly bool _uiRestrictions;

    public static bool IsSupported => OperatingSystem.IsWindows();

    public WindowsJobObject(int memoryLimitMb, int maxProcesses, bool uiRestrictions)
    {
        if (!IsSupported)
            throw new PlatformNotSupportedException("Job objects are Windows-only.");
        _memoryLimitBytes = memoryLimitMb > 0 ? memoryLimitMb * 1024 * 1024 : 0;
        _maxProcesses = maxProcesses;
        _uiRestrictions = uiRestrictions;
        _handle = Interop.CreateJobObjectW(IntPtr.Zero, null);
        if (_handle == IntPtr.Zero)
            throw new System.ComponentModel.Win32Exception();
        ApplyLimits();
    }

    private void ApplyLimits()
    {
        var limits = new Interop.JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            LimitFlags =
                Interop.LimitFlags.JobObjectLimitKillOnJobClose
                | (_memoryLimitBytes > 0 ? Interop.LimitFlags.JobObjectLimitProcessMemory : 0)
                | (_maxProcesses > 0 ? Interop.LimitFlags.JobObjectLimitActiveProcess : 0),
            ActiveProcessLimit = _maxProcesses > 0 ? (uint)_maxProcesses : 0,
        };
        var extended = new Interop.JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            BasicLimitInformation = limits,
            ProcessMemoryLimit = new UIntPtr(unchecked((uint)_memoryLimitBytes)),
        };
        var ok = Interop.SetInformationJobObject(
            _handle, Interop.JobObjectInfoClass.JobObjectExtendedLimitInformation,
            ref extended, Marshal.SizeOf<Interop.JOBOBJECT_EXTENDED_LIMIT_INFORMATION>());
        if (!ok)
            throw new System.ComponentModel.Win32Exception();

        if (!_uiRestrictions) return;
        var ui = new Interop.JOBOBJECT_BASIC_UI_RESTRICTIONS
        {
            UIRestrictionsClass =
                Interop.UiRestrictions.ReadClipboard | Interop.UiRestrictions.WriteClipboard
                | Interop.UiRestrictions.SystemParameters | Interop.UiRestrictions.DisplaySettings
                | Interop.UiRestrictions.ExitWindows | Interop.UiRestrictions.Desktop
                | Interop.UiRestrictions.GlobalAtoms | Interop.UiRestrictions.SetClipboard,
        };
        Interop.SetInformationJobObject(
            _handle, Interop.JobObjectInfoClass.JobObjectBasicUiRestrictions,
            ref ui, Marshal.SizeOf<Interop.JOBOBJECT_BASIC_UI_RESTRICTIONS>());
        // UI 限制设置失败不致命：有些受限环境本身不允许调整 UI 限制。
    }

    /// <summary>Assigns an already-started process (and its whole future child tree) to this job.</summary>
    public bool Assign(IntPtr processHandle) => Interop.AssignProcessToJobObject(_handle, processHandle);

    public void Dispose()
    {
        // KILL_ON_JOB_CLOSE terminates every process in the job when the last handle
        // closes — this is the timed-out command's cleanup guarantee.
        if (_handle != IntPtr.Zero)
        {
            Interop.CloseHandle(_handle);
            _handle = IntPtr.Zero;
        }
    }

    private static class Interop
    {
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern IntPtr CreateJobObjectW(IntPtr lpJobAttributes, string? lpName);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool SetInformationJobObject(
            IntPtr hJob, JobObjectInfoClass infoClass,
            ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION lpJobObjectInfo, int cbJobObjectInfoLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool SetInformationJobObject(
            IntPtr hJob, JobObjectInfoClass infoClass,
            ref JOBOBJECT_BASIC_UI_RESTRICTIONS lpJobObjectInfo, int cbJobObjectInfoLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(IntPtr hObject);

        public enum JobObjectInfoClass
        {
            JobObjectBasicLimitInformation = 2,
            JobObjectBasicUiRestrictions = 4,
            JobObjectExtendedLimitInformation = 9,
        }

        [Flags]
        public enum LimitFlags : uint
        {
            JobObjectLimitActiveProcess = 0x00000008,
            JobObjectLimitProcessMemory = 0x00000100,
            JobObjectLimitJobMemory = 0x00000200,
            JobObjectLimitKillOnJobClose = 0x00002000,
        }

        [Flags]
        public enum UiRestrictions : uint
        {
            Handle = 0x00000001,
            ReadClipboard = 0x00000002,
            WriteClipboard = 0x00000004,
            SystemParameters = 0x00000008,
            DisplaySettings = 0x00000010,
            GlobalAtoms = 0x00000020,
            Desktop = 0x00000040,
            ExitWindows = 0x00000080,
            SetClipboard = 0x00000400,
        }

        public struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public LimitFlags LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        public struct IO_COUNTERS
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        public struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        public struct JOBOBJECT_BASIC_UI_RESTRICTIONS
        {
            public UiRestrictions UIRestrictionsClass;
        }
    }
}
