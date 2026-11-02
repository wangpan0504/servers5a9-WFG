using DfoServer.GameWorld.Towns;

namespace DfoServer.Development;

internal sealed partial class DevelopmentCatalog
{
    public (Location Room, Location Return) ReadCopySpawn()
    {
        var towns = TownCatalog.Load(pvf);
        return (towns.Room, towns.InitialReturn);
    }
}
