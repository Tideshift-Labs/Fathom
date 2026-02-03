using JetBrains.Application.BuildScript.Application.Zones;
using JetBrains.ReSharper.Feature.Services;

namespace ReSharperPlugin.RiderActionExplorer
{
    [ZoneDefinition]
    public interface IRiderActionExplorerZone : IZone,
        IRequire<ICodeEditingZone>
    {
    }
}
