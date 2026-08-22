using System.Collections.Concurrent;

namespace FlickGit.Diagnostics;

/// <summary>
/// A ring buffer of recent latency measurements, surfaced by `flick diag timings`.
///
/// CLAUDE.md, "Performance Targets": "Every one of these must be measurable and
/// surfaced by `flick diag timings`" — and "Its performance target is measured, not
/// assumed" is in the Definition of Done. That only holds if measuring is cheaper than
/// not measuring, so this records a name and a TimeSpan into a bounded queue and does
/// no aggregation until something asks.
/// </summary>
public sealed class OperationTimings
{
    private const int Capacity = 200;

    private readonly ConcurrentQueue<Measurement> _measurements = new();

    public void Record(string operation, TimeSpan duration)
    {
        _measurements.Enqueue(new Measurement(operation, duration, DateTime.Now));

        while (_measurements.Count > Capacity && _measurements.TryDequeue(out _))
        {
            //Bounded on purpose: this lives in a process that stays up for weeks.
        }
    }

    /// <summary>
    /// One line per operation: count, median and worst. Median rather than mean because
    /// the interesting number is what the user feels on a typical right-click, and a
    /// single cold-cache outlier moves a mean far more than it moves the experience.
    /// </summary>
    public IReadOnlyList<Summary> Summarise() =>
        _measurements
            .GroupBy(m => m.Operation, StringComparer.Ordinal)
            .Select(g =>
            {
                double[] sorted = g.Select(m => m.Duration.TotalMilliseconds).Order().ToArray();
                return new Summary(
                    g.Key,
                    sorted.Length,
                    sorted[sorted.Length / 2],
                    sorted[^1]);
            })
            .OrderByDescending(s => s.MedianMs)
            .ToArray();

    public readonly record struct Measurement(string Operation, TimeSpan Duration, DateTime At);

    public readonly record struct Summary(string Operation, int Count, double MedianMs, double MaxMs);
}
