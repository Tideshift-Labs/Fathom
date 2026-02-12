using JetBrains.Application.BuildScript.Application.Zones;
using JetBrains.ReSharper.Feature.Services;

namespace ReSharperPlugin.Fathom
{
    [ZoneDefinition]
    public interface IFathomZone : IZone,
        IRequire<ICodeEditingZone>
    {
    }
}
