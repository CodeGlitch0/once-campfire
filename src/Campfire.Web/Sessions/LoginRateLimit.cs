using System.Collections.Concurrent;

namespace Campfire.Web.Sessions;

/// <summary>
/// Rails <c>rate_limit to: 10, within: 3.minutes, only: :create</c> on sessions.
/// The 11th create inside the window is rejected. Each host has its own limiter.
/// Idle addresses are removed once their hits slide out of the window.
/// </summary>
public sealed class LoginRateLimit
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(3);
    private readonly ConcurrentDictionary<string, Queue<DateTimeOffset>> _hits = new();

    internal int TrackedAddressCount => _hits.Count;

    public bool IsLimited(string ipAddress, DateTimeOffset? now = null)
    {
        var clock = now ?? DateTimeOffset.UtcNow;
        EvictIdle(clock);
        var queue = _hits.GetOrAdd(ipAddress, _ => new Queue<DateTimeOffset>());
        lock (queue)
        {
            Prune(queue, clock);
            if (queue.Count >= 10)
            {
                return true;
            }

            queue.Enqueue(clock);
            return false;
        }
    }

    private void EvictIdle(DateTimeOffset clock)
    {
        foreach (var pair in _hits)
        {
            lock (pair.Value)
            {
                Prune(pair.Value, clock);
                if (pair.Value.Count == 0)
                {
                    _hits.TryRemove(pair);
                }
            }
        }
    }

    private static void Prune(Queue<DateTimeOffset> queue, DateTimeOffset clock)
    {
        var cutoff = clock - Window;
        while (queue.Count > 0 && queue.Peek() < cutoff)
        {
            queue.Dequeue();
        }
    }
}
