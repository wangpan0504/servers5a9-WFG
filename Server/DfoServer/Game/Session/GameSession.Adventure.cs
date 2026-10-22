using DfoServer.Game.Adventure;
using DfoServer.GameWorld.Adventure;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    public AdventureSnapshot ReadAdventure(AdventureCatalog catalog, DateTimeOffset now) => repository.ReadAdventure(owner, catalog, now);
    // USERINFO subtype1尾部的本人冒险团等级由客户端02B644B0安装并重建常驻效果，每次本人Player刷新都要取账号当前等级。
    public int ReadAdventureLevel() => repository.ReadAdventureLevel(owner);
    public void InitializeAdventure(AdventureCatalog catalog, DateTimeOffset now) => repository.InitializeAdventure(owner, catalog, now);
}
