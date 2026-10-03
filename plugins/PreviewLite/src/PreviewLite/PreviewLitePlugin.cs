using YukkuriMovieMaker.Plugin;

namespace PreviewLite;

public sealed class PreviewLitePlugin : IToolPlugin
{
    // Attach at plugin discovery, before preview calls can be JIT-inlined by YMM's worker.
    // No optimizations are enabled until the user opens the tool and explicitly selects ON.
    public PreviewLitePlugin() => PreviewRuntime.Prepare();
    public string Name => "PreviewLite：軽量プレビュー（試作）";
    public Type ViewModelType => typeof(PreviewLiteViewModel);
    public Type ViewType => typeof(PreviewLiteView);
    public bool AllowMultipleInstances => false;
}

public sealed class PreviewLiteViewModel { }
