using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;

namespace PreviewLite;

internal static class PreviewRuntime
{
    internal const string Owner = "local.YMM4.PreviewLite.0.3";
    private static readonly object Gate = new();
    private static readonly ConditionalWeakTable<object, FrameState> States = new();
    internal static readonly PreviewMetrics Metrics = new();
    private static PreviewOptions options = PreviewOptions.Off;
    private static HostBinding? binding;
    private static Harmony? harmony;
    private static string status = "未接続";
    internal static bool Ready { get; private set; }
    internal static string Status => status;
    internal static PreviewOptions Options => Volatile.Read(ref options);

    internal static void Prepare()
    {
        lock (Gate)
        {
            if (Ready) return;
            try { Install(HostBinding.Resolve(Assembly.Load("YukkuriMovieMaker"))); }
            catch (Exception ex) { Disable(); status = "接続停止：" + ex.GetBaseException().Message; }
        }
    }

    internal static void Install(HostBinding target)
    {
        lock (Gate)
        {
            if (Ready) throw new InvalidOperationException("Already installed");
            HostBinding.CheckConflicts(target);
            binding = target;
            harmony = new(Owner);
            try
            {
                // Keep the loop's device-lost exception filter intact.
                // Verified Edit entrance is before the preview frame's audio-clock lookup.
                harmony.Patch(target.Edit, prefix: new HarmonyMethod(typeof(PreviewRuntime), nameof(BeginIteration)));
                harmony.Patch(target.Draw, prefix: new HarmonyMethod(typeof(PreviewRuntime), nameof(BeginDraw)),
                    finalizer: new HarmonyMethod(typeof(PreviewRuntime), nameof(EndDraw)));
                Ready = true;
                status = "接続済み（YMM4 4.56.1.1 専用／初期状態はOFF）";
            }
            catch (Exception original)
            {
                Disable();
                try { RemoveOwnedPatches(target); }
                catch (Exception cleanup) { throw new AggregateException("接続に失敗しました。OFF状態で再起動してください。", original, cleanup); }
                binding = null;
                throw;
            }
        }
    }

    internal static void Configure(PreviewOptions value)
    {
        value.Validate();
        if (!Ready) throw new InvalidOperationException(status);
        HostBinding.CheckConflicts(binding!);
        Metrics.Clear();
        States.Clear();
        Volatile.Write(ref options, value with { });
    }

    internal static void Disable() => Volatile.Write(ref options, PreviewOptions.Off);

    private static void RemoveOwnedPatches(HostBinding target)
    {
        harmony?.Unpatch(target.Draw, HarmonyPatchType.All, Owner);
        harmony?.Unpatch(target.Edit, HarmonyPatchType.All, Owner);
    }

    internal static void RemoveForChecks()
    {
        Disable();
        if (binding is not null) RemoveOwnedPatches(binding);
        Ready = false;
        binding = null;
        States.Clear();
        Metrics.Clear();
    }

    private sealed class FrameState
    {
        internal long Start;
        internal bool Playing, Active, Waited;
        internal PreviewOptions? Configuration;
    }

    private static void BeginIteration(object __instance)
    {
        var config = Options;
        if (!config.Enabled && !config.Measure) return;
        var state = States.GetOrCreateValue(__instance);
        state.Active = false;
        bool playing = binding!.IsPlaying(__instance);
        int wait = state.Start == 0 ? 0 : PreviewPolicy.AdditionalWait(Stopwatch.GetElapsedTime(state.Start).TotalMilliseconds, playing, config);
        long start = Stopwatch.GetTimestamp();
        // OFF/tool unload interrupts the added wait within a short sleep slice.
        while (ReferenceEquals(Options, config) && config.Enabled && Stopwatch.GetElapsedTime(start).TotalMilliseconds < wait)
            Thread.Sleep(Math.Min(10, Math.Max(1, wait - (int)Stopwatch.GetElapsedTime(start).TotalMilliseconds)));
        if (!ReferenceEquals(Options, config)) return;
        state.Start = Stopwatch.GetTimestamp();
        state.Playing = playing;
        state.Waited = wait > 0 && Options.Enabled;
        state.Active = true;
        state.Configuration = config;
    }

    private sealed record DrawObservation(FrameState State, long DrawStart);

    private static void BeginDraw(object __instance, out DrawObservation? __state)
    {
        __state = null;
        var config = Options;
        if ((config.Enabled || config.Measure) && States.TryGetValue(__instance, out var state) && state.Active)
            __state = new(state, Stopwatch.GetTimestamp());
    }

    private static Exception? EndDraw(Exception? __exception, DrawObservation? __state)
    {
        if (__state is not null)
        {
            var state = __state.State;
            if (__exception is null && ReferenceEquals(Options, state.Configuration) && (Options.Enabled || Options.Measure))
                Metrics.Add(state.Start, Stopwatch.GetTimestamp(),
                    Stopwatch.GetElapsedTime(state.Start, __state.DrawStart).TotalMilliseconds,
                    Stopwatch.GetElapsedTime(__state.DrawStart).TotalMilliseconds, state.Playing, state.Waited);
            state.Active = false;
        }
        return __exception; // Preserve the host's own exception recovery.
    }
}
