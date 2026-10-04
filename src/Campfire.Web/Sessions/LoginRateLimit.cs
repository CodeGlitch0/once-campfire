using System.Collections.Concurrent;

namespace Campfire.Web.Sessions;

/// <summary>
/// Rails <c>rate_limit to: 10, within: 3.minutes, only: :create</c> on sessions.
/// The 11th create inside the window is rejected. Each host has its own limiter.
/// </summary>
public sealed class LoginRateLimit
{
    private readonly ConcurrentDictionary<string, Queue<DateTimeOffset>> _hits = new();

    public bool IsLimited(string ipAddress, DateTimeOffset? now = null)
    {
        var clock = now ?? DateTimeOffset.UtcNow;
        var queue = _hits.GetOrAdd(ipAddress, _ => new Queue<DateTimeOffset>());
        lock (queue)
        {
            var cutoff = clock.AddMinutes(-3);
            while (queue.Count > 0 && queue.Peek() < cutoff)
            {
                queue.Dequeue();
            }

            if (queue.Count >= 10)
            {
                return true;
            }

            queue.Enqueue(clock);
            return false;
        }
    }
}
