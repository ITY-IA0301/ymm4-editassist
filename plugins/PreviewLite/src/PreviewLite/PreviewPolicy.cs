namespace PreviewLite;

// Values affect preview scheduling only, never timeline FPS or export.
internal sealed record PreviewOptions(bool Enabled, bool Measure, int PlaybackFps, int IdleFps)
{
    internal static PreviewOptions Off { get; } = new(false, false, 30, 10);
    internal PreviewOptions Validate()
    {
        if (PlaybackFps is not (15 or 20 or 30)) throw new ArgumentOutOfRangeException(nameof(PlaybackFps));
        if (IdleFps is not (5 or 10 or 15)) throw new ArgumentOutOfRangeException(nameof(IdleFps));
        return this;
    }
}

internal static class PreviewPolicy
{
    // Additional wait at the preview entrance; YMM's original waits remain untouched.
    internal static int AdditionalWait(double elapsedMs, bool playing, PreviewOptions options)
    {
        if (!options.Enabled || !double.IsFinite(elapsedMs) || elapsedMs < 0) return 0;
        int fps = playing ? options.PlaybackFps : options.IdleFps;
        return (int)Math.Ceiling(Math.Clamp(1000d / fps - elapsedMs, 0, 200));
    }
}
