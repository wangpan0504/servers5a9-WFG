using DfoServer.GameWorld.Guilds;

namespace DfoServer.Development;

internal sealed partial class DevelopmentCatalog
{
    public GuildParameterCatalog ReadGuildParameters() => new(pvf);
}
