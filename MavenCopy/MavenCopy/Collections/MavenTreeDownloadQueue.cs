using System.Diagnostics.CodeAnalysis;
using MavenCopy.Data;

namespace MavenCopy.Collections;

public class MavenTreeDownloadQueue : IDisposable
{
    private readonly object _lock = new();

    private readonly PriorityQueue<MavenTreeRequest, int> _queue = new();
    
    public bool IsDisposed { get; private set; }

    public bool IsEmpty
    {
        get
        {
            lock (_lock) return _queue.Count <= 0;
        }
    }

    public void Enqueue(MavenTreeRequest element)
    {
        lock (_lock) _queue.Enqueue(element, element.Priority);
    }
    
    public bool TryDequeue([NotNullWhen(true)] out MavenTreeRequest? element)
    {
        lock (_lock)
        {
            return _queue.TryDequeue(out element, out _);
        }
    }
    
    public bool TryDequeue([NotNullWhen(true)] out MavenTreeRequest? element, out int priority)
    {
        lock (_lock)
        {
            return _queue.TryDequeue(out element, out priority);
        }
    }
    
    public MavenTreeRequest Dequeue()
    {
        lock (_lock) return _queue.Dequeue();
    }

    public void Dispose()
    {
        IsDisposed = true;
    }
}