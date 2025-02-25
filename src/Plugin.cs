using Wodistant.PluginLibrary.Hook.Keyboard;
using Wodistant.PluginLibrary.MenuBar;

namespace WodistantListFit
{
    public class Plugin : Wodistant.PluginLibrary.PluginBase
    {
        public override string Name => "ListFit";

        public override string Author => "yumu";

        public override string Version => "1.0.0";

        public override string Description => "ドロップダウンリストの横幅を自動的に調整します。";

        public override MenuBarInfo ManuBarInfomation
        {
            get
            {
                return null;
            }
        }

        public override IKeyboardAction[] KeyboardActions => new IKeyboardAction[]
        {
        };
    }
}
