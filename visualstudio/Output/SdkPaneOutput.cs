using System;
using System.Collections.Generic;
using System.Diagnostics;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using RunicArtifex.DevKit.VisualStudio.Core;

namespace RunicArtifex.DevKit.VisualStudio.Output;

/// <summary>
/// The "Runic SDK" Output pane as an <see cref="IRunicOutput"/> (VS Code's "Runic" channel): doctor and
/// <c>dotnet runic dev</c> lines from any thread. Lines written before the pane exists wait for it, in order.
/// </summary>
// OutputStringThreadSafe writes from any thread (its contract); the analyzer does not know that.
#pragma warning disable VSTHRD010
internal sealed class SdkPaneOutput : IRunicOutput
{
    public static SdkPaneOutput Instance { get; } = new();

    private readonly object gate = new();
    private readonly Queue<string> pending = new();
    private IVsOutputWindowPane? pane;
    private bool creating;

    private SdkPaneOutput() { }

    public void WriteLine(string line)
    {
        // An Output pane is no terminal: Vite's and MSBuild's colour codes would show raw (0.24.0 checklist P4).
        line = DevOutput.Plain(line);
        IVsOutputWindowPane? target;
        lock (gate)
        {
            target = pane;
            if (target == null)
            {
                pending.Enqueue(line);
                if (creating) return;
                creating = true;
            }
        }
        if (target != null) { target.OutputStringThreadSafe(line + Environment.NewLine); return; }
        _ = ToolWindow.RunicSdkState.Background.RunAsync(async () =>
        {
            IVsOutputWindowPane? created = null;
            try { created = await RunicOutput.SdkAsync(); }
            catch (Exception error) { Trace.WriteLine("Runic: the Runic SDK Output pane is unavailable: " + error.Message); }
            lock (gate)
            {
                creating = false;
                while (pending.Count > 0)
                {
                    string text = pending.Dequeue();
                    if (created != null) created.OutputStringThreadSafe(text + Environment.NewLine);
                    else Trace.WriteLine("Runic: " + text);
                }
                pane = created;
            }
        });
    }
}
#pragma warning restore VSTHRD010
