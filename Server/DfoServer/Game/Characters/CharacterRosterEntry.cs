using System.Collections.Immutable;
using DfoServer.Game.Inventory.Models;

namespace DfoServer.Game.Characters;

// 名册只包含身份、穿戴外观与选角前客户端门槛位，不能作为可提交的完整角色快照。
// ARAD 03B55580 在选角前按频道类型读名册行的资格字节，为 0 时弹 IRDPopupWindow 1099：
//   type 0x17 +0x491 dstr 66087 安徒恩；type 0x20 +0x492 dstr 66088 卢克；type 0x28 +0x493 dstr 66089 魔兽；
//   type 0x2D +0x499 dstr 75560 超时空漩涡；type 0x32 +0x494 dstr 32116 普雷·伊希斯。
// 条件取自 dstr 原文 + etc/enterablespecialchannel.etc 的任务号（证据 evidence/roster-raid-flags-20261021.txt，
// evidence/popup1099-fiendwar-roster-flag-20261021.txt，KB-20261021T015635-5c920e4e59）。
// 魔兽（+0x493）未实现：表内 beast=5927 不在 n_quest/quest.lst，服务端也没有 type 0x28 频道，条件无法证明。
internal sealed record CharacterRosterEntry(ushort Id, ushort Slot, string Name, byte Job, byte GrowType,
    byte Level, ImmutableArray<InventoryItem> Equipment, bool FiendWarEligible = false,
    bool AntonEligible = false, bool LukeEligible = false, bool PreyEligible = false, bool IsPvpPrivate = false)
{
    // etc/enterablespecialchannel.etc fiendwar=8672（Fiendwar_enter.qst「[超时空漩涡]奔赴前线」）。
    public const int FiendWarEntryQuest = 8672;
    // anton=12829（Anton/Anton_dimension_enter.qst「[安徒恩团队模式]异次元的安徒恩」，前置主线 3779）。
    public const int AntonEntryQuest = 12829;
    // luke=2903（LukeRaid/Luke_enter_01.qst「[卢克团队模式]制造者卢克 (1/2)」，前置主线 4872）。
    public const int LukeEntryQuest = 2903;
    // prey=8935（GContents/190606_Isys_normal/Isys_normal_clear_03.qst「[伊希斯攻坚战]前往夜之摩天楼」）；
    // dstr 32116 另写“须达到Lv95以上”。
    public const int PreyEntryQuest = 8935;
    public const byte PreyEntryLevel = 95;

    public static bool IsFiendWarEligible(bool questCompleted, bool guildMember) => questCompleted && guildMember;

    public static bool IsPreyEligible(bool questCompleted, byte level) => questCompleted && level >= PreyEntryLevel;

    public static CharacterRosterEntry FromCharacter(Character character) => new(character.Id, character.Slot,
        character.Name, character.Job, character.GrowType, character.Level,
        character.Inventory.Items.Where(item => item.Position.List == InventoryListType.Equipment)
            .OrderBy(item => item.Position.Index).ToImmutableArray(),
        FiendWarEligible: IsFiendWarEligible(character.CompletedQuests.Contains(FiendWarEntryQuest), character.Guild is not null),
        AntonEligible: character.CompletedQuests.Contains(AntonEntryQuest),
        LukeEligible: character.CompletedQuests.Contains(LukeEntryQuest),
        PreyEligible: IsPreyEligible(character.CompletedQuests.Contains(PreyEntryQuest), character.Level), IsPvpPrivate: character.IsPvpPrivate);
}
