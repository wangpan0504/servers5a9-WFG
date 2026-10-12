using DfoServer.Game.Adventure;
using DfoServer.GameWorld.Adventure;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    public AdventureSnapshot ReadAdventure(AdventureCatalog catalog, DateTimeOffset now) => repository.ReadAdventure(owner, catalog, now);
    public void InitializeAdventure(AdventureCatalog catalog, DateTimeOffset now) => repository.InitializeAdventure(owner, catalog, now);
}
