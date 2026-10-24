using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Chorus.Core.Interop;

/// <summary>Windows Job Object：把核心进程绑定到随宿主关闭而终止的 Job，避免 GUI 崩溃后核心变成孤儿进程。</summary>
internal static class JobObjectInterop
{
    private const int JobObjectExtendedLimitInformationClass = 9;
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

    public static IntPtr CreateKillOnCloseJob()
    {
        var handle = NativeCreateJobObjectW(IntPtr.Zero, null);
        if (handle == IntPtr.Zero)
            throw new Win32Exception();

        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        if (!NativeSetInformationJobObject(handle, JobObjectExtendedLimitInformationClass,
                ref info, Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()))
        {
            NativeCloseHandle(handle);
            throw new Win32Exception();
        }
        return handle;
    }

    public static void AssignToJob(IntPtr job, IntPtr process)
    {
        if (!NativeAssignProcessToJobObject(job, process))
            throw new Win32Exception();
    }

    public static void CloseJob(IntPtr job) => NativeCloseHandle(job);

    // 注意：下面四个方法的 C# 名称带了 Native 前缀，但 kernel32 的真实导出名没有前缀，
    // 必须显式用 EntryPoint 指定，否则运行时报 "Unable to find an entry point named ..."
    [DllImport("kernel32.dll", EntryPoint = "CreateJobObjectW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr NativeCreateJobObjectW(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", EntryPoint = "SetInformationJobObject", SetLastError = true)]
    private static extern bool NativeSetInformationJobObject(
        IntPtr hJob, int infoClass, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION lpInfo, int size);

    [DllImport("kernel32.dll", EntryPoint = "AssignProcessToJobObject", SetLastError = true)]
    private static extern bool NativeAssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [DllImport("kernel32.dll", EntryPoint = "CloseHandle", SetLastError = true)]
    private static extern bool NativeCloseHandle(IntPtr h);

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }
}
