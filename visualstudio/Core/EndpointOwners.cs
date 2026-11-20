using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;

namespace RunicArtifex.DevKit.VisualStudio.Core;

/// <summary>A TCP listener on IPv4, and the process that holds it (<c>GetExtendedTcpTable</c>).</summary>
public sealed class TcpListenerEntry
{
    public TcpListenerEntry(IPAddress address, int port, int processId) { Address = address; Port = port; ProcessId = processId; }

    public IPAddress Address { get; }
    public int Port { get; }
    public int ProcessId { get; }
    /// <summary>Whether a connection to <c>127.0.0.1:Port</c> can reach it: bound to 127.0.0.1 or to every address.</summary>
    public bool ServesLoopback => Address.Equals(IPAddress.Loopback) || Address.Equals(IPAddress.Any);
}

/// <summary>What an ownership check found.</summary>
public enum EndpointOwnerVerdict
{
    /// <summary>Every listener that 127.0.0.1:port reaches belongs to the expected processes.</summary>
    Owned,
    /// <summary>Nothing listens on 127.0.0.1:port (yet).</summary>
    NotListening,
    /// <summary>A process outside the expected ones holds the port.</summary>
    Foreign,
    /// <summary>Windows did not list the listeners.</summary>
    Unknown,
}

public sealed class EndpointOwnerCheck
{
    public EndpointOwnerCheck(EndpointOwnerVerdict verdict, int port, IReadOnlyList<int> foreign) { Verdict = verdict; Port = port; Foreign = foreign; }

    public EndpointOwnerVerdict Verdict { get; }
    public int Port { get; }
    /// <summary>The process IDs that hold the port and are not expected.</summary>
    public IReadOnlyList<int> Foreign { get; }

    /// <summary>Why DevKit refuses to attach, for an error message (DEV-SEC-83).</summary>
    public string Refusal(string url) => Verdict switch
    {
        EndpointOwnerVerdict.Foreign => $"The DevTools endpoint {url} is held by process {string.Join(", ", Foreign.Select(id => id.ToString(CultureInfo.InvariantCulture)))}, which is not the Window's page or another process of yours started after the Window. Runic DevKit does not attach the debugger to it. Close the program that holds port {Port.ToString(CultureInfo.InvariantCulture)}, or start the Window again.",
        EndpointOwnerVerdict.Unknown => $"Runic DevKit could not tell which process holds the DevTools endpoint {url} (Windows did not list its TCP listeners), so it does not attach the debugger to it.",
        _ => $"Nothing listens on the DevTools endpoint {url}.",
    };
}

/// <summary>
/// Whether the page's DevTools endpoint is the Window's before DevKit attaches the debugger to it (DEV-SEC-83). A
/// planned port can be taken by another user's program first (a fixed CS-WebUI port, or the gap between DevKit picking a
/// free port and WebView2 binding it), and a session endpoint outlives the browser that held it.
/// </summary>
public static class EndpointOwners
{
    /// <summary>
    /// The decision without Windows. Only the listeners a connection to 127.0.0.1 can reach count; each must be
    /// <paramref name="expected"/>. Any other one makes the port <see cref="EndpointOwnerVerdict.Foreign"/>.
    /// </summary>
    public static EndpointOwnerCheck Check(int port, IReadOnlyList<TcpListenerEntry>? listeners, Func<int, bool> expected)
    {
        if (listeners == null) return new EndpointOwnerCheck(EndpointOwnerVerdict.Unknown, port, Array.Empty<int>());
        var holders = listeners.Where(item => item.Port == port && item.ServesLoopback).Select(item => item.ProcessId).Distinct().ToList();
        if (holders.Count == 0) return new EndpointOwnerCheck(EndpointOwnerVerdict.NotListening, port, Array.Empty<int>());
        var foreign = holders.Where(id => !expected(id)).ToList();
        return new EndpointOwnerCheck(foreign.Count == 0 ? EndpointOwnerVerdict.Owned : EndpointOwnerVerdict.Foreign, port, foreign);
    }

    /// <summary>
    /// Whether <paramref name="processId"/> may hold a Window's endpoint. With a Window: the Window itself, or a process
    /// started after it that is in its tree (WebView2's browser is the Window's child) or is this user's (CS-WebUI starts
    /// its browser through <c>cmd /c START</c>, whose exit leaves the browser without a running parent). Without a
    /// Window: any process of this user.
    /// </summary>
    public static bool Expected(int processId, int? window, IReadOnlyDictionary<int, int> parents, Func<int, bool> isCurrentUser, Func<int, DateTime?> startTime)
    {
        if (window is not { } root) return isCurrentUser(processId);
        if (processId == root) return true;
        // A process created before the Window is not its child or its browser, whatever its parent ID says (a reused ID).
        if (startTime(root) is not { } windowStart || startTime(processId) is not { } started || started < windowStart) return false;
        return PosixProcessTree.Tree(root, parents).Contains(processId) || isCurrentUser(processId);
    }

    /// <summary>The check on this machine (Windows); <see cref="EndpointOwnerVerdict.Unknown"/> elsewhere.</summary>
    public static EndpointOwnerCheck CheckNow(int port, int? window)
    {
        var listeners = ListenersNow();
        if (listeners == null) return Check(port, null, _ => false);
        var parents = DevProcessTree.Parents();
        string? self = OwnerSid(GetCurrentProcessId());
        return Check(port, listeners, id => Expected(id, window, parents, candidate => self != null && OwnerSid(candidate) == self, StartTime));
    }

    /// <summary>The IPv4 TCP listeners and their processes; null when Windows does not list them.</summary>
    public static IReadOnlyList<TcpListenerEntry>? ListenersNow()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return null;
        try
        {
            int size = 0;
            uint result = GetExtendedTcpTable(IntPtr.Zero, ref size, false, AfInet, TcpTableOwnerPidListener, 0);
            for (int attempt = 0; attempt < 4; attempt++)
            {
                IntPtr buffer = Marshal.AllocHGlobal(size);
                try
                {
                    result = GetExtendedTcpTable(buffer, ref size, false, AfInet, TcpTableOwnerPidListener, 0);
                    if (result == InsufficientBuffer) continue;
                    if (result != 0) return null;
                    int count = Marshal.ReadInt32(buffer);
                    int rowSize = Marshal.SizeOf<TcpRowOwnerPid>();
                    var listeners = new List<TcpListenerEntry>(count);
                    for (int index = 0; index < count; index++)
                    {
                        var row = Marshal.PtrToStructure<TcpRowOwnerPid>(buffer + 4 + index * rowSize);
                        // dwLocalPort holds the port in network byte order in its low 16 bits.
                        int port = (int)(((row.LocalPort & 0xFF) << 8) | ((row.LocalPort >> 8) & 0xFF));
                        listeners.Add(new TcpListenerEntry(new IPAddress(row.LocalAddress), port, (int)row.OwningPid));
                    }
                    return listeners;
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
            return null;
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException) { return null; }
    }

    /// <summary>The SID of the process's user, or null when this user may not read its token (another user's process).</summary>
    internal static string? OwnerSid(int processId)
    {
        IntPtr process = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (process == IntPtr.Zero) return null;
        try
        {
            if (!OpenProcessToken(process, TokenQuery, out IntPtr token)) return null;
            try
            {
                GetTokenInformation(token, TokenUserClass, IntPtr.Zero, 0, out int length);
                if (length <= 0) return null;
                IntPtr buffer = Marshal.AllocHGlobal(length);
                try
                {
                    if (!GetTokenInformation(token, TokenUserClass, buffer, length, out _)) return null;
                    // TOKEN_USER starts with SID_AND_ATTRIBUTES, whose first field is the SID pointer.
                    if (!ConvertSidToStringSid(Marshal.ReadIntPtr(buffer), out IntPtr text)) return null;
                    try { return Marshal.PtrToStringUni(text); }
                    finally { LocalFree(text); }
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
            finally { CloseHandle(token); }
        }
        finally { CloseHandle(process); }
    }

    private static DateTime? StartTime(int processId)
    {
        IntPtr process = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (process == IntPtr.Zero) return null;
        try { return GetProcessTimes(process, out long created, out _, out _, out _) ? DateTime.FromFileTimeUtc(created) : null; }
        finally { CloseHandle(process); }
    }

    private const int AfInet = 2, TcpTableOwnerPidListener = 3;
    private const uint InsufficientBuffer = 122;
    private const uint ProcessQueryLimitedInformation = 0x1000, TokenQuery = 0x0008;
    private const int TokenUserClass = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct TcpRowOwnerPid
    {
        public uint State, LocalAddress, LocalPort, RemoteAddress, RemotePort, OwningPid;
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int addressFamily, int tableClass, uint reserved);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int processId);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessTimes(IntPtr process, out long creation, out long exit, out long kernel, out long user);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(IntPtr token, int informationClass, IntPtr information, int length, out int returnLength);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "ConvertSidToStringSidW")]
    private static extern bool ConvertSidToStringSid(IntPtr sid, out IntPtr text);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    [DllImport("kernel32.dll")]
    private static extern int GetCurrentProcessId();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
