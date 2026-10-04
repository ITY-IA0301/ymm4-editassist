using YukkuriMovieMaker.Plugin;

namespace EditAssist.VersionManager;

public sealed class VersionManagerPlugin : IToolPlugin
{
    public string Name => "EditAssist：バージョン選択";
    public Type ViewModelType => typeof(VersionManagerViewModel);
    public Type ViewType => typeof(VersionManagerView);
    public bool AllowMultipleInstances => false;
}
public sealed class VersionManagerViewModel { }
