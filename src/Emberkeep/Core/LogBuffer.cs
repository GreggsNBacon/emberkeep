using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Channels;

namespace Emberkeep.Core;

/// <summary>Ring buffer of recent log lines with live subscribers (one per open log stream).</summary>
public sealed class LogBuffer
{
    private const int Capacity = 5000;
    private readonly object _lock = new();
    private readonly Queue<LogLine> _lines = new();
    private readonly List<Channel<LogLine>> _subs = new();
    private long _seq;

    public void Add(string source, string? text)
    {
        if (text == null) return;
        // Strip ANSI colour codes some servers emit.
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\x1B\[[0-9;?]*[ -/]*[@-~]", "");
        LogLine line;
        Channel<LogLine>[] subs;
        lock (_lock)
        {
            line = new LogLine(++_seq, DateTime.Now, source, text);
            _lines.Enqueue(line);
            while (_lines.Count > Capacity) _lines.Dequeue();
            subs = _subs.ToArray();
        }
        foreach (var s in subs) s.Writer.TryWrite(line);
    }

    public (LogLine[] Backlog, Channel<LogLine> Channel) Subscribe(int backlog = 1000)
    {
        var ch = Channel.CreateBounded<LogLine>(new BoundedChannelOptions(10000) { FullMode = BoundedChannelFullMode.DropOldest });
        lock (_lock)
        {
            _subs.Add(ch);
            return (_lines.Skip(Math.Max(0, _lines.Count - backlog)).ToArray(), ch);
        }
    }

    public void Unsubscribe(Channel<LogLine> ch)
    {
        lock (_lock) _subs.Remove(ch);
        ch.Writer.TryComplete();
    }

    public void Clear()
    {
        lock (_lock) _lines.Clear();
    }
}
