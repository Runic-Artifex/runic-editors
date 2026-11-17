using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace RunicArtifex.DevKit.VisualStudio.Core;

public enum LspDirection
{
    /// <summary>Visual Studio to the server.</summary>
    Sending,
    /// <summary>The server to Visual Studio.</summary>
    Received,
}

/// <summary>
/// The <c>runic.translations.trace.server</c> trace in the Translations Output pane, in vscode-languageclient's form:
/// <c>[Trace - 12:00:00] Sending request 'textDocument/hover - (5)'.</c>; at <see cref="LspTraceLevel.Verbose"/> each
/// line is followed by the message's JSON. It reads the LSP framing (<c>Content-Length</c>) of both streams, so the
/// level can change while the server runs.
/// </summary>
public sealed class LspTracer
{
    private readonly Func<LspTraceLevel> level;
    private readonly Action<string> write;
    private readonly Func<DateTime> now;
    private readonly Dictionary<LspDirection, Framing> framing = new() { [LspDirection.Sending] = new Framing(), [LspDirection.Received] = new Framing() };
    // Request id → method and start, per direction of the request.
    private readonly Dictionary<(LspDirection, string), (string Method, DateTime Started)> pending = new();

    public LspTracer(Func<LspTraceLevel> level, Action<string> write, Func<DateTime>? now = null)
    {
        this.level = level; this.write = write; this.now = now ?? (() => DateTime.Now);
    }

    /// <summary>Bytes that went over the connection in <paramref name="direction"/>.</summary>
    public void Feed(LspDirection direction, byte[] buffer, int offset, int count)
    {
        lock (framing)
        {
            var frame = framing[direction];
            for (int index = offset; index < offset + count;)
            {
                if (frame.Remaining == 0)
                {
                    // Header bytes, up to the blank line.
                    frame.Header.Append((char)buffer[index++]);
                    if (!EndsWithBlankLine(frame.Header)) continue;
                    frame.Remaining = ContentLength(frame.Header.ToString());
                    frame.Header.Clear();
                    frame.Traced = level() != LspTraceLevel.Off;
                    frame.Body.SetLength(0);
                    continue;
                }
                int take = Math.Min(frame.Remaining, offset + count - index);
                if (frame.Traced) frame.Body.Write(buffer, index, take);
                index += take; frame.Remaining -= take;
                if (frame.Remaining == 0 && frame.Traced) Trace(direction, Encoding.UTF8.GetString(frame.Body.ToArray()));
            }
        }
    }

    private void Trace(LspDirection direction, string json)
    {
        var message = LspMessageSummary.Read(json);
        var other = direction == LspDirection.Sending ? LspDirection.Received : LspDirection.Sending;
        DateTime time = now();
        string verb = direction == LspDirection.Sending ? "Sending" : "Received";
        string line;
        if (message.Method != null && message.Id != null)
        {
            pending[(direction, message.Id)] = (message.Method, time);
            line = $"{verb} request '{message.Method} - ({message.Id})'.";
        }
        else if (message.Method != null) line = $"{verb} notification '{message.Method}'.";
        else if (message.Id != null)
        {
            string method = "unknown";
            string duration = "";
            if (pending.TryGetValue((other, message.Id), out var request))
            {
                pending.Remove((other, message.Id));
                method = request.Method;
                duration = $" in {(long)(time - request.Started).TotalMilliseconds}ms";
            }
            line = $"{verb} response '{method} - ({message.Id})'{duration}{(message.IsError ? " with an error" : "")}.";
        }
        else line = $"{verb} a message that is not JSON-RPC.";
        write($"[Trace - {time:HH:mm:ss}] {line}");
        if (level() == LspTraceLevel.Verbose) write(json);
    }

    private static bool EndsWithBlankLine(StringBuilder header)
        => header.Length >= 4 && header[header.Length - 4] == '\r' && header[header.Length - 3] == '\n' && header[header.Length - 2] == '\r' && header[header.Length - 1] == '\n';

    private static int ContentLength(string header)
    {
        foreach (string line in header.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries))
        {
            int colon = line.IndexOf(':');
            if (colon > 0 && line.Substring(0, colon).Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(line.Substring(colon + 1).Trim(), out int length) && length >= 0) return length;
        }
        return 0;
    }

    private sealed class Framing
    {
        public readonly StringBuilder Header = new();
        public readonly MemoryStream Body = new();
        public int Remaining;
        public bool Traced;
    }
}

/// <summary>The top-level <c>method</c> and <c>id</c> of a JSON-RPC message, and whether it carries an <c>error</c>.</summary>
public sealed class LspMessageSummary
{
    private LspMessageSummary(string? method, string? id, bool isError) { Method = method; Id = id; IsError = isError; }

    public string? Method { get; }
    /// <summary>The id as written: a number, or a string without its quotes.</summary>
    public string? Id { get; }
    public bool IsError { get; }

    /// <summary>Scans only the outermost object; members of <c>params</c> and <c>result</c> are skipped.</summary>
    public static LspMessageSummary Read(string json)
    {
        string? method = null, id = null; bool error = false;
        int index = SkipSpace(json, 0);
        if (index >= json.Length || json[index] != '{') return new LspMessageSummary(null, null, false);
        index++;
        while (true)
        {
            index = SkipSpace(json, index);
            if (index >= json.Length || json[index] == '}') break;
            if (json[index] == ',') { index++; continue; }
            if (json[index] != '"') break;
            string key = ReadString(json, ref index);
            index = SkipSpace(json, index);
            if (index >= json.Length || json[index] != ':') break;
            index = SkipSpace(json, index + 1);
            int start = index;
            if (index < json.Length && json[index] == '"')
            {
                string value = ReadString(json, ref index);
                if (key == "method") method = value; else if (key == "id") id = value;
            }
            else
            {
                index = SkipValue(json, index);
                string raw = json.Substring(start, index - start).Trim();
                if (key == "id" && raw != "null") id = raw;
            }
            if (key == "error") error = true;
        }
        return new LspMessageSummary(method, id, error);
    }

    private static int SkipSpace(string json, int index)
    {
        while (index < json.Length && char.IsWhiteSpace(json[index])) index++;
        return index;
    }

    private static string ReadString(string json, ref int index)
    {
        var text = new StringBuilder();
        for (index++; index < json.Length; index++)
        {
            char character = json[index];
            if (character == '"') { index++; break; }
            if (character == '\\' && index + 1 < json.Length) { index++; character = json[index] switch { 'n' => '\n', 't' => '\t', 'r' => '\r', _ => json[index] }; }
            text.Append(character);
        }
        return text.ToString();
    }

    /// <summary>Skips a number, literal, object or array (strings inside are skipped whole).</summary>
    private static int SkipValue(string json, int index)
    {
        int depth = 0;
        for (; index < json.Length; index++)
        {
            char character = json[index];
            if (character == '"') { ReadString(json, ref index); index--; continue; }
            if (character is '{' or '[') depth++;
            else if (character is '}' or ']') { if (depth == 0) return index; depth--; if (depth == 0) return index + 1; }
            else if (character == ',' && depth == 0) return index;
        }
        return index;
    }
}

/// <summary>A stream that passes every byte read or written through an <see cref="LspTracer"/>.</summary>
public sealed class TracingStream : Stream
{
    private readonly Stream inner;
    private readonly LspTracer tracer;
    private readonly LspDirection direction;

    /// <param name="direction"><see cref="LspDirection.Received"/> for the server's output, <see cref="LspDirection.Sending"/> for its input.</param>
    public TracingStream(Stream inner, LspTracer tracer, LspDirection direction)
    {
        this.inner = inner; this.tracer = tracer; this.direction = direction;
    }

    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => inner.CanWrite;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() => inner.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    public override int Read(byte[] buffer, int offset, int count)
    {
        int read = inner.Read(buffer, offset, count);
        Observe(buffer, offset, read);
        return read;
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        int read = await inner.ReadAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);
        Observe(buffer, offset, read);
        return read;
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        Observe(buffer, offset, count);
        inner.Write(buffer, offset, count);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        Observe(buffer, offset, count);
        return inner.WriteAsync(buffer, offset, count, cancellationToken);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) inner.Dispose();
        base.Dispose(disposing);
    }

    // The trace never breaks the connection.
    private void Observe(byte[] buffer, int offset, int count)
    {
        if (count <= 0) return;
        try { tracer.Feed(direction, buffer, offset, count); }
        catch (Exception) { }
    }
}
