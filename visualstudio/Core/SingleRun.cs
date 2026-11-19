using System;
using System.Threading;
using System.Threading.Tasks;

namespace RunicArtifex.DevKit.VisualStudio.Core;

/// <summary>
/// The running state of a command that runs once at a time (Debug the Window), which can be released without its run
/// returning (runic-editors#23): a debugger call that never returns, or a solution reload, must not leave the command
/// disabled until Visual Studio restarts. Each run gets a ticket; <see cref="Reset"/> or <see cref="Release"/> ends it,
/// and a ticket that was reset no longer ends a later run.
/// </summary>
public sealed class SingleRun
{
    private readonly object gate = new();
    private long current;
    private bool running;
    private CancellationTokenSource? cancellation;

    /// <summary>Whether a run holds the command; the command is disabled while it does.</summary>
    public bool IsRunning { get { lock (gate) return running; } }

    /// <summary>
    /// Starts a run, linked to <paramref name="cancellationToken"/>, or returns null while one runs. The ticket's
    /// <see cref="Ticket.Token"/> is cancelled when the run is reset or released.
    /// </summary>
    public Ticket? TryStart(CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (running) return null;
            running = true;
            cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            return new Ticket(this, ++current, cancellation.Token);
        }
    }

    /// <summary>
    /// Ends the current run, if any, whatever its ticket: its token is cancelled, so it stops at its next await, and the
    /// command is enabled again. True when a run was ended.
    /// </summary>
    public bool Reset()
    {
        CancellationTokenSource? ended;
        lock (gate)
        {
            if (!running) return false;
            running = false;
            current++;
            ended = cancellation;
            cancellation = null;
        }
        Cancel(ended);
        return true;
    }

    /// <summary>Ends the run of <paramref name="ticket"/> if it is still the current one; true when it was.</summary>
    public bool Release(Ticket ticket)
    {
        CancellationTokenSource? ended;
        lock (gate)
        {
            if (!running || ticket.Id != current || !ReferenceEquals(ticket.Owner, this)) return false;
            running = false;
            ended = cancellation;
            cancellation = null;
        }
        Cancel(ended);
        return true;
    }

    private static void Cancel(CancellationTokenSource? source)
    {
        // Not disposed: a run that is still waiting may read or register on its token later.
        try { source?.Cancel(); } catch (AggregateException) { }
    }

    /// <summary>One run of a <see cref="SingleRun"/>.</summary>
    public sealed class Ticket
    {
        internal Ticket(SingleRun owner, long id, CancellationToken token) { Owner = owner; Id = id; Token = token; }
        internal SingleRun Owner { get; }
        internal long Id { get; }
        /// <summary>Cancelled when the run is reset or released.</summary>
        public CancellationToken Token { get; }
        /// <summary>Whether this run still holds the command.</summary>
        public bool IsCurrent { get { lock (Owner.gate) return Owner.running && Owner.current == Id; } }
    }
}

/// <summary>
/// Calls <paramref name="expired"/> once on the thread pool when a call has not finished within a time, for a call
/// that cannot be cancelled (a page attach that never completes, runic-editors#23 and #44). Dispose it when the
/// call returns.
/// </summary>
public sealed class Watchdog : IDisposable
{
    private readonly CancellationTokenSource cancellation = new();
    private int state; // 0 armed, 1 expired, 2 disposed

    private Watchdog() { }

    public static Watchdog Start(TimeSpan timeout, Action expired)
    {
        if (expired == null) throw new ArgumentNullException(nameof(expired));
        var watchdog = new Watchdog();
        var token = watchdog.cancellation.Token;
        _ = Task.Delay(timeout, token).ContinueWith(_ =>
        {
            if (Interlocked.CompareExchange(ref watchdog.state, 1, 0) == 0) expired();
        }, token, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
        return watchdog;
    }

    /// <summary>Whether the time ran out before the call returned.</summary>
    public bool Expired => Volatile.Read(ref state) == 1;

    public void Dispose()
    {
        if (Interlocked.CompareExchange(ref state, 2, 0) == 0) cancellation.Cancel();
        cancellation.Dispose();
    }
}
