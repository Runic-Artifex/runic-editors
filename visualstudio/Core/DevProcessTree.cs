using System;
using System.Linq;
using System.Collections.Generic;
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

    /// <summary>
    /// Puts the processes that <paramref name="root"/> started before it was assigned in the job too, with their
    /// descendants (runic-editors#58): <c>Process.Start</c> returns a running process, so a child it starts at once
    /// (a stand-in <c>cmd</c>, or a <c>dev</c> on a busy machine) can come before <see cref="TryAssign"/>, and would
    /// stay outside the job. A process created before the root is not its child, whatever its parent ID says (a reused
    /// ID). Returns how many it assigned. A child whose own parent has exited by now cannot be found.
    /// </summary>
    public int AssignDescendants(Process root)
    {
        lock (gate)
        {
            if (job == IntPtr.Zero) return 0;
            DateTime rootStart;
            try { rootStart = root.StartTime.ToUniversalTime(); }
            catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { return 0; }
            int assigned = 0;
            foreach (int id in PosixProcessTree.Tree(root.Id, Parents()).Skip(1))
            {
                IntPtr handle = OpenProcess(ProcessSetQuota | ProcessTerminate | ProcessQueryLimitedInformation, false, id);
                if (handle == IntPtr.Zero) continue;
                try
                {
                    if (GetProcessTimes(handle, out long created, out _, out _, out _) && DateTime.FromFileTimeUtc(created) >= rootStart
                        && AssignProcessToJobObject(job, handle)) assigned++;
                }
                finally { CloseHandle(handle); }
            }
            return assigned;
        }
    }

    /// <summary>Every running process's parent ID (a Toolhelp snapshot).</summary>
    private static Dictionary<int, int> Parents()
    {
        var parents = new Dictionary<int, int>();
        IntPtr snapshot = CreateToolhelp32Snapshot(SnapProcess, 0);
        if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1)) return parents;
        try
        {
            var entry = new ProcessEntry32 { dwSize = (uint)Marshal.SizeOf<ProcessEntry32>() };
            for (bool more = Process32First(snapshot, ref entry); more; more = Process32Next(snapshot, ref entry))
                parents[(int)entry.th32ProcessID] = (int)entry.th32ParentProcessID;
        }
        finally { CloseHandle(snapshot); }
        return parents;
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

    private const uint SnapProcess = 0x00000002;
    private const uint ProcessTerminate = 0x0001, ProcessSetQuota = 0x0100, ProcessQueryLimitedInformation = 0x1000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint dwSize, cntUsage, th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID, cntThreads, th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "Process32FirstW")]
    private static extern bool Process32First(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "Process32NextW")]
    private static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessTimes(IntPtr process, out long creation, out long exit, out long kernel, out long user);

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
