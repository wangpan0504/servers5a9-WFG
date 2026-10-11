using DfoServer.Game.Dungeons;

namespace DfoServer.Game.Session;

// 本服业务分类，不是ARAD的MODULE_TYPE、旧端ch_state数值或NOTI3字段。
internal enum UserState
{
    CharacterSelect,
    EnteringWorld,
    Town,
    DungeonSelect,
    Dungeon,
    TutorialDungeon,
    PvpRoom,
    PvpBattle,
    WarRoom,
    DeathTower,
    BlueMarble,
    BossPlay,
    AdvanceAltar
}

// 最低生命周期资格只做基础门禁；具体玩法、阶段、实例及资产资格仍由所属业务复验。
internal enum UserStateRequirement { Owner, CharacterSelected, WorldReady, DungeonContext }

internal static class UserStatePolicy
{
    public static bool IsImplemented(UserState state) => state is UserState.CharacterSelect or UserState.EnteringWorld or
        UserState.Town or UserState.DungeonSelect or UserState.Dungeon;

    public static bool Meets(UserState state, UserStateRequirement requirement)
    {
        if (!IsImplemented(state)) return false;
        return requirement switch
        {
            UserStateRequirement.Owner => true,
            UserStateRequirement.CharacterSelected => state != UserState.CharacterSelect,
            UserStateRequirement.WorldReady => state is UserState.Town or UserState.DungeonSelect or UserState.Dungeon or UserState.TutorialDungeon,
            UserStateRequirement.DungeonContext => state is UserState.Dungeon or UserState.TutorialDungeon,
            _ => false
        };
    }

    public static UserState Read(SessionPhase phase, bool selected, bool ready, bool selectingDungeon,
        SinglePlayerDungeonPhase? dungeon, bool tutorial)
    {
        if (!selected && (phase != SessionPhase.Unselected || ready || selectingDungeon || dungeon is not null) ||
            !ready && phase != SessionPhase.Unselected || selectingDungeon && dungeon is not null ||
            tutorial && dungeon is null)
            throw new InvalidOperationException("用户状态与角色、就绪或实例所有权不一致");
        return phase switch
        {
            SessionPhase.Unselected when !ready && !selectingDungeon && dungeon is null => selected ? UserState.EnteringWorld : UserState.CharacterSelect,
            SessionPhase.Town when !selectingDungeon && dungeon is null => UserState.Town,
            SessionPhase.MapSelect when selectingDungeon && dungeon is null => UserState.DungeonSelect,
            SessionPhase.Loading when dungeon == SinglePlayerDungeonPhase.Loading => tutorial ? UserState.TutorialDungeon : UserState.Dungeon,
            SessionPhase.Dungeon when dungeon == SinglePlayerDungeonPhase.Dungeon => tutorial ? UserState.TutorialDungeon : UserState.Dungeon,
            SessionPhase.Settlement when dungeon == SinglePlayerDungeonPhase.Settlement => tutorial ? UserState.TutorialDungeon : UserState.Dungeon,
            _ => throw new InvalidOperationException("会话阶段与当前玩法阶段不一致")
        };
    }

    public static bool CanTransition(SessionPhase from, SessionPhase to)
    {
        if (!Enum.IsDefined(from) || !Enum.IsDefined(to)) return false;
        if (to == SessionPhase.Unselected) return true;
        return (from, to) switch
        {
            (SessionPhase.Unselected, SessionPhase.Town) => true,
            (SessionPhase.Town, SessionPhase.Town or SessionPhase.MapSelect or SessionPhase.Loading) => true,
            (SessionPhase.MapSelect, SessionPhase.MapSelect or SessionPhase.Town or SessionPhase.Loading) => true,
            (SessionPhase.Loading, SessionPhase.Dungeon or SessionPhase.Settlement or SessionPhase.Town) => true,
            (SessionPhase.Dungeon, SessionPhase.Dungeon or SessionPhase.Loading or SessionPhase.Settlement or SessionPhase.Town) => true,
            (SessionPhase.Settlement, SessionPhase.Settlement or SessionPhase.Loading or SessionPhase.Town) => true,
            _ => false
        };
    }
}
