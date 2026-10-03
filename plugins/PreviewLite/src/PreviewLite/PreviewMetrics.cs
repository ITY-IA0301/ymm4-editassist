namespace PreviewLite;

internal sealed record MetricsSnapshot(int Samples, double UpdateMs, double DrawMs, double CadenceFps,
    bool Playing, long DelayChanges, long Iterations);

internal sealed class PreviewMetrics
{
    private readonly object gate = new();
    private readonly Queue<(double update, double draw, double interval)> samples = new();
    private long lastStart, changes, iterations;
    private bool lastPlaying;

    internal void Add(long start, long end, double update, double draw, bool playing, bool changed)
    {
        lock (gate)
        {
            if (lastPlaying != playing) { samples.Clear(); lastStart = 0; }
            lastPlaying = playing;
            iterations++;
            if (changed) changes++;
            // Stopped previews often have no redraw; do not report an imaginary rendering FPS.
            if (end > start && update >= 0 && draw >= 0)
            {
                double interval = lastStart > 0 ? (start - lastStart) * 1000d / System.Diagnostics.Stopwatch.Frequency : 0;
                lastStart = start;
                samples.Enqueue((update, draw, interval));
                while (samples.Count > 60) samples.Dequeue();
            }
        }
    }

    internal MetricsSnapshot Snapshot()
    {
        lock (gate)
        {
            var intervals = samples.Where(x => x.interval > 0).Select(x => x.interval).ToArray();
            return new(samples.Count, samples.Count == 0 ? 0 : samples.Average(x => x.update),
                samples.Count == 0 ? 0 : samples.Average(x => x.draw),
                intervals.Length == 0 ? 0 : 1000d / intervals.Average(), lastPlaying, changes, iterations);
        }
    }

    internal void Clear()
    {
        lock (gate) { samples.Clear(); lastStart = changes = iterations = 0; }
    }
}
