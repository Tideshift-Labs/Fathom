#if RIDER
using JetBrains.Application.UI.Options;
using JetBrains.Application.UI.Options.OptionPages;
using JetBrains.Application.UI.Options.OptionsDialog;
using JetBrains.IDE.UI.Options;
using JetBrains.Lifetimes;
using JetBrains.ReSharper.Feature.Services.Resources;

namespace ReSharperPlugin.RiderActionExplorer.Settings
{
    [OptionsPage(Pid, PageTitle, typeof(FeaturesEnvironmentOptionsThemedIcons.CodeInspections),
        ParentId = ToolsPage.PID)]
    public class RiderActionExplorerOptionsPage : BeSimpleOptionsPage
    {
        private const string Pid = "RiderActionExplorerOptions";
        private const string PageTitle = "Code Inspector HTTP Server";

        public RiderActionExplorerOptionsPage(
            Lifetime lifetime,
            OptionsPageContext optionsPageContext,
            OptionsSettingsSmartContext optionsSettingsSmartContext)
            : base(lifetime, optionsPageContext, optionsSettingsSmartContext)
        {
            AddIntOption((RiderActionExplorerSettings s) => s.Port,
                "HTTP Server Port (requires restart)");
        }
    }
}
#endif
