using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RunicArtifex.DevKit.VisualStudio.Core;
using RunicArtifex.DevKit.VisualStudio.ToolWindow;

namespace RunicArtifex.DevKit.VisualStudio.Debugging;

/// <summary>
/// The page attach (or <c>Debugger2.GetProcesses</c>) that is waiting, if any, and how to end it (runic-editors#23, #44).
/// DevKit starts the attach as a js-debug attach configuration that does not hold the UI thread; when it never completes,
/// ending the Visual Studio JavaScript debug adapter it started ends it, and Visual Studio reports the failed attach.
/// DevKit does that when the attach has waited <see cref="PageAttach.AttachTimeout"/> and when Visual Studio starts closing
/// during it (<see cref="VsClosing"/>). It writes to the Output pane only after ending the adapter: the pane's
/// <c>OutputStringThreadSafe</c> waits for the UI thread, which an adapter that stops answering while it starts blocks.
/// <para>
/// Ending the adapter does not release <c>Process2.Attach2</c>, used before 0.23 and still the fallback: it keeps waiting
/// in its nested message loop for an attach nobody completes, and a Visual Studio closed meanwhile never exits (VM runs,
/// 2026-11-19).
/// </para>
/// </summary>
internal static class HungAttach
{
    private static readonly object gate = new();
    private static Pending? pending;

    private sealed class Pending
    {
        public Pending(string page, IReadOnlyCollection<int> before) { Page = page; Before = before; }
        public string Page { get; }
        /// <summary>This Visual Studio's adapters before the attach began.</summary>
        public IReadOnlyCollection<int> Before { get; }
        public readonly HashSet<string> Reasons = new(StringComparer.Ordinal);
    }

    /// <summary>Whether an attach is waiting now.</summary>
    public static bool IsWaiting { get { lock (gate) return pending != null; } }

    /// <summary>Marks an attach as waiting until the returned scope is disposed.</summary>
    public static IDisposable Begin(string page, IReadOnlyCollection<int> before)
    {
        var current = new Pending(page, before);
        lock (gate) pending = current;
        return new Scope(current);
    }

    /// <summary>
    /// When an attach is waiting, ends this Visual Studio's JavaScript debug adapter of it on the thread pool, once
    /// per <paramref name="reason"/>. Safe on any thread; returns at once.
    /// </summary>
    public static void Recover(string reason)
    {
        Pending? current;
        lock (gate)
        {
            current = pending;
            if (current == null || !current.Reasons.Add(reason)) return;
        }
        _ = Task.Run(() =>
        {
            try
            {
                if (JsAdapterProcesses.Census() is not { } census) return;
                lock (gate) if (!ReferenceEquals(pending, current)) return; // it returned meanwhile
                var ended = JsDebugAdapters.ToEndForHungAttach(census.Own, current.Before).Where(JsAdapterProcesses.End).ToList();
                SdkOutput.WriteLine(JsDebugAdapters.EndedForHungAttachMessage($"{reason} while {current.Page}", ended));
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                SdkOutput.WriteLine($"Could not end Visual Studio's JavaScript debug adapter ({current.Page}): {error.Message}");
            }
        });
    }

    private sealed class Scope : IDisposable
    {
        private Pending? current;
        public Scope(Pending current) { this.current = current; }

        public void Dispose()
        {
            var ended = Interlocked.Exchange(ref current, null);
            if (ended == null) return;
            lock (gate) if (ReferenceEquals(pending, ended)) pending = null;
        }
    }
}

/// <summary>
/// A page attach that failed after DevKit stopped waiting for it or Visual Studio started to close. The Runic SDK
/// pane has said so already; callers report it there only, never in a message box, which would keep a closing Visual
/// Studio open (runic-editors#44).
/// </summary>
internal sealed class HungAttachEndedException : InvalidOperationException
{
    public HungAttachEndedException(string message, Exception inner) : base(message, inner) { }
}
