using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace RunicArtifex.DevKit.VisualStudio.Core;

/// <summary>
/// On Windows, a Job object that holds <c>dotnet runic dev</c> and every process it starts (the frontend server through
/// its package manager, <c>runic-translations serve</c>, the Window and the build), so they can be ended together even
/// after a process between them has exited: <c>taskkill /T</c> follows parent process IDs and misses those orphans.
/// <para>
/// The job does not kill on close: when Visual Studio closes it writes <c>stop</c> to <c>dev</c> and cannot wait, and a
/// kill-on-close job would end <c>dev</c> before its own stop ran (the session file would be left <c>"ready"</c>).
/// Processes started with breakaway from the job (none of <c>dev</c>'s do) are not held.
/// </para>
/// </summary>
internal sealed class DevProcessTree : IDisposable
{
    private readonly object gate = new();
    private IntPtr job;

    private DevProcessTree(IntPtr job) { this.job = job; }

    /// <summary>Puts the started process in a new job; null elsewhere than on Windows or when Windows refuses.</summary>
    public static DevProcessTree? TryAssign(Process process)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return null;
        try
        {
            IntPtr job = CreateJobObject(IntPtr.Zero, null);
            if (job == IntPtr.Zero) return null;
            if (AssignProcessToJobObject(job, process.Handle)) return new DevProcessTree(job);
            CloseHandle(job);
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or DllNotFoundException or EntryPointNotFoundException) { }
        return null;
    }

    /// <summary>Ends every process still in the job; returns how many there were.</summary>
    public int EndAll()
    {
        lock (gate)
        {
            if (job == IntPtr.Zero) return 0;
            int active = QueryInformationJobObject(job, BasicAccountingInformation, out var info, Marshal.SizeOf<BasicAccounting>(), IntPtr.Zero)
                ? (int)info.ActiveProcesses : 0;
            if (active > 0) TerminateJobObject(job, 1);
            return active;
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (job == IntPtr.Zero) return;
            CloseHandle(job);
            job = IntPtr.Zero;
        }
    }

    private const int BasicAccountingInformation = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicAccounting
    {
        public long TotalUserTime, TotalKernelTime, ThisPeriodTotalUserTime, ThisPeriodTotalKernelTime;
        public uint TotalPageFaultCount, TotalProcesses, ActiveProcesses, TotalTerminatedProcesses;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateJobObject(IntPtr job, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool QueryInformationJobObject(IntPtr job, int informationClass, out BasicAccounting information, int length, IntPtr returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
