using YukkuriMovieMaker.Plugin;

namespace EditAssist.Plugin;

public sealed class EditAssistToolPlugin : IToolPlugin
{
    public string Name => "EditAssist：一括編集・素材・診断";
    public Type ViewModelType => typeof(EditAssistViewModel);
    public Type ViewType => typeof(EditAssistView);
    public bool AllowMultipleInstances => false;
}

