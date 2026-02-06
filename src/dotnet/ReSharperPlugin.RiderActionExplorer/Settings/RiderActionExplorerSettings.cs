using JetBrains.Application.Settings;
using JetBrains.Application.Settings.WellKnownRootKeys;

namespace ReSharperPlugin.RiderActionExplorer.Settings
{
    [SettingsKey(typeof(EnvironmentSettings), "Rider Action Explorer")]
    public class RiderActionExplorerSettings
    {
        [SettingsEntry(19876, "HTTP server port")]
        public int Port;
    }
}
