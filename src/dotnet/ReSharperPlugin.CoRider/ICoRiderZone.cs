using JetBrains.Application.BuildScript.Application.Zones;
using JetBrains.ReSharper.Feature.Services;

namespace ReSharperPlugin.CoRider
{
    [ZoneDefinition]
    public interface ICoRiderZone : IZone,
        IRequire<ICodeEditingZone>
    {
    }
}
