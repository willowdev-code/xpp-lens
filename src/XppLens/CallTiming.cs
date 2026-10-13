using System.Diagnostics;

namespace XppLens;

/// <summary>
/// Where the time of one MCP call went: waiting for the index refresh lock, refreshing changed files, and the rest
/// (the query itself). Filled by <see cref="IndexService"/> on the calling thread, written to the usage log.
/// </summary>
public sealed class CallTiming
{
    [ThreadStatic] static CallTiming? _current;

    /// <summary>The timing of the call running on this thread, or null outside an MCP call.</summary>
    public static CallTiming? Current => _current;

    public long WaitMs;
    public long RefreshMs;

    /// <summary>True when a wildcard search was answered from the in-memory name list.</summary>
    public bool NamesInMemory;

    public static CallTiming Begin() => _current = new CallTiming();

    public static void End() => _current = null;

    /// <summary>Adds the elapsed time of <paramref name="sw"/> to <see cref="WaitMs"/> or <see cref="RefreshMs"/>.</summary>
    public static void AddWait(Stopwatch sw)
    {
        if (_current != null) _current.WaitMs += sw.ElapsedMilliseconds;
    }

    public static void AddRefresh(Stopwatch sw)
    {
        if (_current != null) _current.RefreshMs += sw.ElapsedMilliseconds;
    }
}
