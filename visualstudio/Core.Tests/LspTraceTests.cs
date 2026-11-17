using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TUnit.Core;

namespace RunicArtifex.DevKit.VisualStudio.Core.Tests;

/// <summary>The language server trace in the Translations Output pane.</summary>
public class LspTraceTests
{
    private static byte[] Frame(string json)
    {
        byte[] body = Encoding.UTF8.GetBytes(json);
        return Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\nContent-Type: application/vscode-jsonrpc; charset=utf-8\r\n\r\n").Concat(body).ToArray();
    }

    private sealed class Harness
    {
        public LspTraceLevel Level = LspTraceLevel.Messages;
        public DateTime Now = new(2026, 11, 17, 12, 0, 0);
        public readonly List<string> Lines = new();
        public readonly LspTracer Tracer;

        public Harness() { Tracer = new LspTracer(() => Level, Lines.Add, () => Now); }

        public void Feed(LspDirection direction, byte[] bytes, int chunk = int.MaxValue)
        {
            for (int offset = 0; offset < bytes.Length; offset += chunk)
                Tracer.Feed(direction, bytes, offset, Math.Min(chunk, bytes.Length - offset));
        }
    }

    [Test]
    public void Messages_trace_requests_responses_and_notifications_like_vscode_languageclient()
    {
        var trace = new Harness();
        trace.Feed(LspDirection.Sending, Frame("""{"jsonrpc":"2.0","id":5,"method":"textDocument/hover","params":{"method":"not this"}}"""), chunk: 7);
        trace.Now = trace.Now.AddMilliseconds(42);
        trace.Feed(LspDirection.Received, Frame("""{"jsonrpc":"2.0","id":5,"result":{"contents":"x"}}""").Concat(Frame("""{"jsonrpc":"2.0","method":"textDocument/publishDiagnostics","params":{}}""")).ToArray());
        trace.Feed(LspDirection.Received, Frame("""{"jsonrpc":"2.0","id":"c1","method":"workspace/configuration","params":{}}"""));
        trace.Feed(LspDirection.Sending, Frame("""{"jsonrpc":"2.0","id":"c1","error":{"code":-32601,"message":"no"}}"""), chunk: 1);
        Check.Same(new[]
        {
            "[Trace - 12:00:00] Sending request 'textDocument/hover - (5)'.",
            "[Trace - 12:00:00] Received response 'textDocument/hover - (5)' in 42ms.",
            "[Trace - 12:00:00] Received notification 'textDocument/publishDiagnostics'.",
            "[Trace - 12:00:00] Received request 'workspace/configuration - (c1)'.",
            "[Trace - 12:00:00] Sending response 'workspace/configuration - (c1)' in 0ms with an error.",
        }, trace.Lines);
    }

    [Test]
    public void Verbose_adds_the_json_and_off_writes_nothing_but_keeps_the_framing()
    {
        var trace = new Harness { Level = LspTraceLevel.Off };
        trace.Feed(LspDirection.Sending, Frame("""{"id":1,"method":"initialize","params":{"text":"é \"}\" {"}}"""), chunk: 3);
        Check.Equal(0, trace.Lines.Count, "off");
        trace.Level = LspTraceLevel.Verbose;
        string json = """{"jsonrpc":"2.0","method":"initialized","params":{}}""";
        trace.Feed(LspDirection.Sending, Frame(json), chunk: 5);
        Check.Same(new[] { "[Trace - 12:00:00] Sending notification 'initialized'.", json }, trace.Lines);
        trace.Feed(LspDirection.Received, Frame("""{"id":1,"result":null}"""));
        Check.Equal("[Trace - 12:00:00] Received response 'unknown - (1)'.", trace.Lines[2], "a response to a request sent while off");
    }

    [Test]
    public void The_summary_reads_only_the_outer_object()
    {
        var summary = LspMessageSummary.Read("""{ "params": { "id": 9, "method": "inner", "list": [1, {"error": true}] }, "id" : "a\"b", "method": "outer" }""");
        Check.Equal("outer", summary.Method);
        Check.Equal("a\"b", summary.Id);
        Check.True(!summary.IsError, "error is nested");
        var response = LspMessageSummary.Read("""{"id":null,"error":{"code":1}}""");
        Check.True(response.Id == null && response.Method == null && response.IsError, "null id error");
        Check.True(LspMessageSummary.Read("[]").Method == null, "not an object");
    }

    [Test]
    public async Task The_tracing_stream_passes_bytes_through_both_ways()
    {
        var trace = new Harness();
        byte[] request = Frame("""{"id":2,"method":"shutdown"}""");
        var written = new MemoryStream();
        using (var input = new TracingStream(written, trace.Tracer, LspDirection.Sending))
        {
            await input.WriteAsync(request, 0, 10);
            input.Write(request, 10, request.Length - 10);
            Check.Same(request, written.ToArray(), "written unchanged");
        }
        byte[] response = Frame("""{"id":2,"result":null}""");
        using var output = new TracingStream(new MemoryStream(response), trace.Tracer, LspDirection.Received);
        var read = new MemoryStream();
        byte[] buffer = new byte[4];
        int count;
        while ((count = await output.ReadAsync(buffer, 0, buffer.Length)) > 0) read.Write(buffer, 0, count);
        Check.Same(response, read.ToArray(), "read unchanged");
        Check.Same(new[] { "[Trace - 12:00:00] Sending request 'shutdown - (2)'.", "[Trace - 12:00:00] Received response 'shutdown - (2)' in 0ms." }, trace.Lines);
    }
}
