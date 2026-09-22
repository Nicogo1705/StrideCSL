using System;

namespace Csl;

/// <summary>
/// Two of something that swap roles each step: a pass reads Current and writes Next, then Swap
/// makes what was written the current one.
/// </summary>
public sealed class PingPong<T> : IDisposable where T : class
{
    private readonly T[] items;
    private int current;

    public PingPong(T first, T second)
    {
        items = new[] { first ?? throw new ArgumentNullException(nameof(first)), second ?? throw new ArgumentNullException(nameof(second)) };
    }

    /// <summary>Two made the same way.</summary>
    public PingPong(Func<T> create) : this(create(), create()) { }

    /// <summary>The one to read this step.</summary>
    public T Current => items[current];

    /// <summary>The one to write this step.</summary>
    public T Next => items[1 - current];

    /// <summary>Index of the current one, 0 or 1.</summary>
    public int CurrentIndex => current;

    public T this[int index] => items[index];

    /// <summary>Makes Next the current one.</summary>
    public void Swap() => current = 1 - current;

    /// <summary>Does something to both.</summary>
    public void ForEach(Action<T> action)
    {
        action(items[0]);
        action(items[1]);
    }

    public void Dispose()
    {
        (items[0] as IDisposable)?.Dispose();
        (items[1] as IDisposable)?.Dispose();
    }
}
