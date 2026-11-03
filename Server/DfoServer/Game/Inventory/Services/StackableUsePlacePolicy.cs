using DfoServer.Game.Session;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Inventory.Services;

internal static class StackableUsePlacePolicy
{
    public static StackableUseContext Context(UserState state, bool seriaRoom = false, bool tournamentChannel = false,
        bool practice = false, bool assault = false, bool inGuild = false, bool pvpPrivate = false, bool guildMaster = false) => new(state switch
        {
            UserState.Town => seriaRoom ? StackableUsePlace.SeriaRoom : StackableUsePlace.Village,
            UserState.PvpTown or UserState.PvpRoom or UserState.PvpBattle =>
                tournamentChannel ? StackableUsePlace.Tournament : StackableUsePlace.Pvp,
            UserState.Dungeon or UserState.TutorialDungeon => StackableUsePlace.Dungeon,
            _ => StackableUsePlace.None
        }, practice, assault, inGuild, pvpPrivate, guildMaster);
}
