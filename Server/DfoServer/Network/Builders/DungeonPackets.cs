using System.Buffers.Binary;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Items;
using DfoServer.Network.Builders.Inventory;

namespace DfoServer.Network.Builders;

// 匹配客户端 ACK16 的独立分支；同一码可能按场景显示不同文案或动作，不能当作服务端资格枚举。
internal enum DungeonSelectionError : ushort
{
    SilentDefault = 0, // 默认分支无提示。
    NoAvailableDifficulty = 1, // dstr 49548: 没有可以进入的地图难度。
    BroadcastOrPartyEligibility = 7, // dstr 22366: 进行强化直播时， 无法进入地下城。
                                      // dstr 65073: 队伍中有人未满足条件， 无法进入%s。
    LeaderOrBindingLimit = 8, // dstr 728: FAIL : 您不是队长， 不能选择地下城！
                              // dstr 15083: 超过绑定角色的限制数量， 无法进入。
    Silent = 9, // 默认分支无提示。
    InsufficientGold = 10, // dstr 25047: 因[%s]金币不足， 无法进入地下城
    MemberLevelTooHigh = 13, // dstr 46709: 队伍中存在Lv过高的角色， 因而无法进入地下城。
    MemberLevelTooLow = 14, // dstr 46710: 队伍中存在Lv过低的角色， 因而无法进入地下城。
    Untranslated22118 = 16, // dstr 22118: dnf_22118
    RequiredMaterials = 17, // dstr 31363: 入场所需的材料或费用不足，无法入场。
                            // dstr 47707: 因[%s]缺少入场材料， 无法进入。
                            // dstr 47708: 因存在缺少入场材料的队员， 无法进入%s。
    DailyLimitDialog = 18, // dstr 23126: 抱歉， 已经超出每日入场次数上限。
    SelectionStateOrEligibility = 19, // dstr 727: FAIL : 处于无法选择状态， 不能选择地下城！
                                      // dstr 25161: 超 · 星空裂缝
                                      // dstr 25162: 队伍中有完成"%s"任务的队员， 无法入场。
                                      // dstr 48015: 角色的Lv不符， 无法进入。
                                      // dstr 67061: 因存在缺少未完成“前往圣者之鸣号”任务的队员， 无法进入。
                                      // dstr 73464: 同一公会的冒险家需2人及以上才能入场。
                                      // dstr 100003038: 队伍中有队员尚未完成时空裂缝任务， 无法入场。
                                      // dstr 300000148: 当前状态下无法进入。
    DungeonDifficultyOrLevel = 21, // dstr 726: FAIL : 您选择了不存在的地下城！
                                   // dstr 60397: 地下城或者难度不符。
                                   // dstr 70399: 队伍中存在Lv过低的玩家， 无法进入该地下城。
    MemberFatigueOrClearCondition = 22, // dstr 49061: 玩家[%s]， 因入场所需的疲劳值(%d)不足， 无法挑战该地下城。
                                       // dstr 300000147: 部分队友已通关， 无法进入。
    MatchingInProgress = 23, // dstr 36288: 匹配中，无法使用
    GroupMaterials = 27, // dstr 27156: 部分队员邀请函不足， 无法进入该地下城。
                         // dstr 42462: 队伍中有队员的%s不足， 无法进入深渊派对。
                         // dstr 60718: 队伍中存在缺少入场材料的队员，无法进入。
    GroupInvitationOrShipTicket = 28, // dstr 27156: 部分队员邀请函不足， 无法进入该地下城。
                                      // dstr 42711: 无乔安费雷诺的乘船票。\n\n入场材料可在乔安费雷诺帆船处购买或通过网吧在线奖励获得。
    AdvancementRequired = 30, // dstr 100002282: 只有转职角色可以使用。
    GuildMembershipRequired = 34, // dstr 73463: 只有加入公会的冒险家才能入场。
    EnergyBlockadeCompleted = 57, // dstr 100008090: 存在已通关能量阻截战地下城的玩家， 无法进入。
    MemberLevelRequired = 95, // dstr 25048: [%s]角色Lv低于入场要求， 无法进入该地下城。
    MemberEntryCountExhausted = 97, // dstr 100007194: 队伍中存在已耗尽入场次数的队员， 无法进入。
    Untranslated49522 = 106, // dstr 49517/49522: dnf_49517 / dnf_49522
    Untranslated49523 = 107, // dstr 49517/49523: dnf_49517 / dnf_49523
    DailyLimitOrPendingReward = 108, // dstr 75910: 尚未领取商会支援奖励， 无法进入。
                                     // dstr 100008097: 超过每日进入次数限制， 无法进入地下城。
                                     // dstr 100008101: 存在超过每日进入次数限制的队员， 无法进入地下城。
    HellCharacterOrMerchantQuest = 109, // dstr 22534: 该功能不适用于地狱模式角色。
                                        // dstr 300000151: 需要接受NPC商人特丽丝的任务[商人的考验]， 才能进入地下城。
    AbyssMemberLevel = 111, // dstr 42463: 队伍中有队员的Lv不符合要求， 无法进入深渊派对。
    SecurityVerification = 118, // dstr 65088: 密保卡和二级密码未通过验证， 无法进入。
    MemberAntiMagic = 119, // dstr 75657: 队伍中存在抗魔值未达到要求的队员， 无法进入地下城。
    RaidEntryUnavailable = 133, // dstr 32060: 抱歉， 当前状态下无法入场。
                                // dstr 67018: 其他队伍已经入场或者归属于其他地下城， 目前无法入场。
                                // dstr 67019: 该地下城由于受到卢克的诅咒而无法入场。
    CurrentStateUnavailable = 159, // dstr 32022: 当前状态无法使用。
    DungeonEntryRestrictions = 171, // dstr 32012: 仅限单人进入
                                    // dstr 48110: 该地下城中无法开启练习模式
                                    // dstr 49047: 队伍人数低于%d人， 无法进入该地下城。
                                    // dstr 72967: 只有夫妻2人组队才能进入该地下城。
    MemberLevelInsufficient = 172, // dstr 74121: 队伍中有队员的Lv不足， 无法进入该地下城。
    MemberEntryLimit = 173, // dstr 27007: 因[%s]超过进入上限次数， 无法进入该地下城。
                            // dstr 70538: 玩家[%s]的入场权限不足， 无法进入该地下城。
    SoloOnly = 174, // dstr 60394: 该地下城仅限单人进入。
    TournamentPoints = 180, // dstr 21608: 因[%s]的个人竞技大赛点数不足， 无法进入地下城
    WinningFaction = 189, // dstr 21609: 因[%s]不是胜利势力， 无法进入地下城
    WeeklyEntryLimit = 208, // dstr 100008099: 超过每周进入次数限制， 无法进入地下城。
                            // dstr 100008100: 存在超过每周进入次数限制的队员， 无法进入地下城。
    MinimumPartySize = 210, // dstr 46889: 队伍人数不足， 未达到该地下城要求的最少人数。
    MinimumDungeonPartySize = 217, // dstr 48444: 尚不满足该地下城的最低人数。
    Untranslated38381 = 225, // dstr 38381: dnf_38381
    NoParty = 230, // dstr 46883: 该地下城无法组队进入。
    ImperialArenaProperties = 233, // dstr 66084: 帝国竞技场地下城属性错误。
    ImperialArenaDungeonMismatch = 234, // dstr 66085: 帝国竞技场首次进入的地下城与当前进入的地下城不同。
    EpicCollectionOrHellDifficulty = 235, // dstr 19794: 已完成史诗收集， 无法进入该地下城。
                                          // dstr 22587: 地狱模式角色无法挑战该难度的地下城。
    ReturnForDailyUpdate = 238, // dstr 60736: 为了更新地下城每日信息， 即将返回城镇。
    PartySizeOrSolo = 244, // dstr 40105: 当前队伍人数不符合地下城入场条件。
                           // dstr 70757: 该地下城只限单人进入。
    EntryTimeRestriction = 245, // dstr 40106: 只有在规定时间内， 才能进入该地下城。
                                // dstr 100002345: 05:30~05:59期间， 无法进入魔界盟会地下城。
    DungeonLimitOrStorageReward = 246, // dstr 40110: 超过该地下城每日可进入的次数上限。
                                       // dstr 100000023: 已超过该地下城的最大入场次数。
                                       // dstr 15082: 贮藏箱中有未领取的奖励， 无法进入。
    FestivalOrSiegeRestriction = 247, // dstr 40111: 逆袭之谷、 南部溪谷开放过程中或是怪物攻城进行时， 无法进入该地下城。
    SiegeRestriction = 248, // dstr 40112: 怪物攻城进行时， 无法进入该地下城。
    AltarLevelRestriction = 249, // dstr 40113: Lv65以下的角色无法进入极限的祭坛。
    DifficultyUnavailable = 254, // dstr 49556: 无法进入当前难度。
    DungeonWeeklyLimit = 255 // dstr 67087: 已超过该地下城每周可进入次数。
}

// 两个u16是创建上下文，第二项非零时参与客户端可见性过滤，不能投影地面坐标。
internal sealed record DungeonDropPacketItem(uint ObjectKey, short RecordSlot, ItemCore? Core,
    ushort ContextValue, ushort VisibilityFilter)
{
    public int GoldAmount { get; init; }
}

internal static class DungeonPackets
{
    public static byte[] CharacterLife(ushort characterKey, bool revived) => CharacterLife(characterKey, revived ? (byte)1 : (byte)0);

    public static byte[] CharacterLife(ushort characterKey, byte state)
    {
        if (state > 2) throw new ArgumentOutOfRangeException(nameof(state));
        byte[] body = new byte[4];
        BinaryPrimitives.WriteUInt16LittleEndian(body, characterKey);
        body[2] = state;
        return body;
    }

    // 本版 ACK41 在公共成功字节后固定读取角色 key:u16 和标志:u8。
    public static byte[] CoinUsed(ushort characterKey) => [1, (byte)characterKey, (byte)(characterKey >> 8), 0];

    public static byte[] BossEntranceOpened() => new byte[5];
    // 0237F740不读取正文；向场景发布0x26并把深渊完成字节+0x208置1。
    public static byte[] HellPartyCleared() => [];

    // NOTI603：0237F460依次读取两个u32；首项按秒投影计时基值，次项语义未闭合。
    // 两项均由调用方显式提供；这里只保留线格式，不选择时限、校验玩法范围或发送通知。
    public static byte[] ClearLimitTimeStart(uint timerBaseSeconds, uint opaqueValue)
    {
        byte[] body = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(body, timerBaseSeconds);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4), opaqueValue);
        return body;
    }

    public static byte[] Elevator(DungeonElevatorNotice notice) => [notice.Counter, notice.State];

    public static byte[] StoryPause(ushort characterId, byte state, byte type)
    {
        byte[] body = new byte[4];
        BinaryPrimitives.WriteUInt16LittleEndian(body,characterId);
        body[2] = state; body[3] = type;
        return body;
    }

    // ACK15 成功标志为 1 时，02348EFD 不再读后续字段；这里只用于已有选图界面路径。
    public static byte[] SelectionOpened() => [1];

    // 公共 CMD 成功前缀，不混用同号 END_ASSAULT 通知。
    public static byte[] SelectionReturned() => [1];

    public static byte[] SelectionUnavailable() => [0, 1, 0];

    // NOTI27 在 023D8AC0 的空列表足迹：计数字段为 0 时不读列表项，共 42 字节。
    // 顺序是 u32、u32、u8、u32、u8、u8、u8、u32、u16、u16、u32、u8、u8、u32、u32、u8、u16、u8。
    // 末字节是选图候选列表的计数：023D9B5E 读 u8 计数，随后每项 int32 逐个 push_back 进客户端魔兽 manager 的 id 列表。
    // 该列表为空时客户端没有任何候选副本，世界地图只画底图不画气球；魔兽地图46的五个气球5335–5339就靠它下发。
    public static byte[] SelectionNotice(int hellLegendDungeon = 0, int griefLayer = 0, ushort despairLayer = 0,
        IReadOnlyList<ushort>? incompleteHellQuestMemberSlots = null, IReadOnlyList<int>? selectionCandidates = null)
    {
        if (hellLegendDungeon < 0) throw new ArgumentOutOfRangeException(nameof(hellLegendDungeon));
        if (griefLayer is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(griefLayer));
        int count = incompleteHellQuestMemberSlots?.Count ?? 0;
        if (count > 8 || incompleteHellQuestMemberSlots is { } slots &&
            (slots.Any(slot => slot > 7) || slots.Distinct().Count() != count))
            throw new ArgumentOutOfRangeException(nameof(incompleteHellQuestMemberSlots));
        int candidates = selectionCandidates?.Count ?? 0;
        if (candidates > byte.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(selectionCandidates));
        int tailOffset = count * 2;
        byte[] body = new byte[42 + tailOffset + candidates * 4];
        // 023D8EF7读取countA；非空名单清除深渊按钮资格，并在条件成立时显示原生dstr 5431。
        body[8] = (byte)count;
        for (int i = 0; i < count; i++)
            BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(9 + i * 2), incompleteHellQuestMemberSlots![i]);
        // countA后的u32经03B99380写+57EBD0；所有后续字段跟随列表平移，空名单仍为原42B。
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(9 + tailOffset), checked((uint)hellLegendDungeon));
        // 023D9B20读取末尾u16，经01E0DD30写入root+E8的悲叹楼层。
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(39 + tailOffset),checked((ushort)griefLayer));
        if (despairLayer > 100) throw new ArgumentOutOfRangeException(nameof(despairLayer));
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(20 + tailOffset), despairLayer);
        body[41 + tailOffset] = (byte)candidates;
        for (int i = 0; i < candidates; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(42 + tailOffset + i * 4),
                checked((uint)selectionCandidates![i]));
        return body;
    }

    // NOTI5 DUNGEON_PERMISSION：u16 count + 每项 u32 dungeonId、u8 state。
    // 布局与参考实现 buildCurrentDungeonResourceStateEntriesBody 一致；state=0 不会进入快照。
    // 这里只写世界地图悬浮需要开通的地下城编号，不写名称字符串。
    public static byte[] Permission(IReadOnlyList<int> dungeonIds)
    {
        ArgumentNullException.ThrowIfNull(dungeonIds);
        int count = dungeonIds.Count;
        if (count > ushort.MaxValue)
            count = ushort.MaxValue;
        byte[] body = new byte[2 + count * 5];
        BinaryPrimitives.WriteUInt16LittleEndian(body, (ushort)count);
        int at = 2;
        for (int index = 0; index < count; index++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(at), checked((uint)dungeonIds[index]));
            body[at + 4] = 1;
            at += 5;
        }
        return body;
    }

    // ACK16 成功标志为 1 时，0233B36D 不再读后续字段。这只确认导航房间已建立，不代替地图或怪物初始化。
    public static byte[] SelectionAccepted() => [1];

    // ACK16 失败读取 success:u8、error:u16 和 memberSlot:u8；单人成员槽为0。
    public static byte[] SelectionRejected(DungeonSelectionError error = DungeonSelectionError.Silent, byte memberSlot = 0)
    {
        if (memberSlot > 7) throw new ArgumentOutOfRangeException(nameof(memberSlot));
        // 其他分支可能继续读取字段；枚举完整不代表四字节正文已逐项验证。
        if (error is not (DungeonSelectionError.SilentDefault or DungeonSelectionError.NoAvailableDifficulty or
            DungeonSelectionError.Silent or DungeonSelectionError.InsufficientGold or DungeonSelectionError.RequiredMaterials or
            DungeonSelectionError.MemberFatigueOrClearCondition or DungeonSelectionError.GroupMaterials or
            DungeonSelectionError.DungeonLimitOrStorageReward))
            throw new ArgumentOutOfRangeException(nameof(error));
        return [0, (byte)error, (byte)((ushort)error >> 8), memberSlot];
    }
    // 本版0xAD显示次数/权限上限，不用于缺区域任务；缺任务沿错误9恢复，门票不足保留0x11。
    public static byte[] HellPartyRejected(DungeonSelectionError code) => SelectionRejected(code);
    // NOTI28的匹配36B布局；调用方提供已审核的准入计划，构包不建立实例或确认加载。
    public static byte[] Information(DungeonEntryPlan plan, DungeonPoint? bossPosition = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        byte[] body = new byte[36];
        BinaryPrimitives.WriteUInt32LittleEndian(body, checked((uint)plan.DungeonId));
        body[4] = plan.Difficulty;
        body[7] = checked((byte)plan.Maze.SourceOrdinal);
        body[8] = checked((byte)(bossPosition ?? plan.BossMap.Position).X);
        body[9] = checked((byte)(bossPosition ?? plan.BossMap.Position).Y);
        body[10] = byte.MaxValue;
        body[11] = byte.MaxValue;
        // 0238ABCC读取并经03BB1510安装练习模式；不可只改变服务端奖励策略。
        body[12] = plan.IsPractice ? (byte)1 : (byte)0;
        // 未解释字段保持匹配形状：offset16的u16为0，offset20的FFFFFFFF选择跳过分支。
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(20), uint.MaxValue);
        // 实验性（未在95客户端实证；依据参考项目 dungeon_info.go / dungeon_hell_room.go / A21 DungeonEntryHandler:1323-1335）：
        // 深渊派对入场时 [10,11]=深渊房间迷宫坐标X/Y，[14..15] u16 hellPartyEnabled=1，[18] Value2=0x0B；[20..23] seed 与普通入场一致（FFFFFFFF）。
        if (plan.HellParty is { } hell)
        {
            body[10] = checked((byte)hell.Room.X);
            body[11] = checked((byte)hell.Room.Y);
        }
        else if (plan.IsCosmo || plan.IsInfernoParty)
        {
            body[10] = byte.MaxValue;
            body[11] = byte.MaxValue;
        }
        if (plan.HellParty is not null || plan.IsCosmo || plan.IsInfernoParty)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(14), 1);
            body[18] = 0x0B;
        }
        return body;
    }

    // 实验性（#259，布局来自 Go 项目 dungeon_tournament.go 对 DNF.exe 的反汇编注释，未经 V31 客户端验证）：
    // NOTI386 TOURNAMENT_INFO = dungeonId(u32)、难度(u8)、组队人数(u8)，4 组对阵表（轮次u8、队伍数u8、每队位置u8+成员 code u32/战力 u16），
    // 4 组对手（轮次u8，每名对手 objectKey u16、code u32、level u8、type u8）。
    public static byte[] TournamentInfo(DungeonEntryPlan plan, DungeonTournamentPlan tournament)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(tournament);
        if (tournament.Rounds.Count != DungeonTournamentPlan.RoundCount ||
            tournament.Opponents.Count != DungeonTournamentPlan.RoundCount * tournament.PartyCount)
            throw new InvalidDataException("比赛对阵表与对手数量不一致");
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(checked((uint)plan.DungeonId));
        writer.Write(plan.Difficulty);
        writer.Write(checked((byte)tournament.PartyCount));
        for (int round = 0; round < tournament.Rounds.Count; round++)
        {
            var bracket = tournament.Rounds[round];
            writer.Write(checked((byte)(round + 1)));
            writer.Write(checked((byte)bracket.Count));
            for (int position = 0; position < bracket.Count; position++)
            {
                writer.Write(checked((byte)position));
                foreach (var member in bracket[position].Members)
                {
                    writer.Write(checked((uint)member.Code));
                    writer.Write(checked((ushort)member.Strength));
                }
            }
        }
        for (int round = 1; round <= DungeonTournamentPlan.RoundCount; round++)
        {
            writer.Write((byte)round);
            foreach (var opponent in tournament.Opponents.Where(row => row.Round == round))
            {
                writer.Write(opponent.ObjectKey);
                writer.Write(checked((uint)opponent.Code));
                writer.Write(opponent.Level);
                writer.Write(opponent.Type);
            }
        }
        return stream.ToArray();
    }

    // NOTI387 TOURNAMENT_MAP_INFO（替代比赛副本的 NOTI29）：x、y(u8)，seed(u32)，0，1，地图 id(u32)，三个 0。共 15B。
    public static byte[] TournamentMapInfo(DungeonEntryPlan plan, uint seed)
    {
        ArgumentNullException.ThrowIfNull(plan);
        byte[] body = new byte[15];
        body[0] = checked((byte)plan.StartMap.Position.X);
        body[1] = checked((byte)plan.StartMap.Position.Y);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(2), seed);
        body[7] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(8), checked((uint)plan.StartMap.Id));
        return body;
    }
    // NOTI29空名单为23B；seed由调用方显式提供，不能在Builder推导资源或实例随机规则。
    public static byte[] StartMap(DungeonEntryPlan plan, uint seed, DungeonMapOperation? operation = null,
        DungeonMapPayload payload = DungeonMapPayload.Build)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var mode = operation ?? (plan.StartMap.Kind == "layered" ? DungeonMapOperation.AdvanceLayer : DungeonMapOperation.Current);
        if (mode > DungeonMapOperation.RestoreBase || payload > DungeonMapPayload.Refresh)
            throw new InvalidDataException("未知地图操作或投影模式");
        byte[] body = new byte[payload == DungeonMapPayload.Cached ? 16 : 23];
        body[0] = checked((byte)plan.StartMap.Position.X);
        body[1] = checked((byte)plan.StartMap.Position.Y);
        body[2] = (byte)mode;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(3), seed);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(9), 1);
        // 实验性（未在95客户端实证；依据参考项目 dungeon_start_map.go / A21 DungeonNotificationBuilder.BuildStartMap）：
        // 深渊派对时 [7]=深渊模式（取请求标志，默认1）。
        if (plan.HellParty is not null || plan.IsCosmo || plan.IsInfernoParty) body[7] = plan.HellPartyMode;
        body[13] = (byte)payload;
        if (payload == DungeonMapPayload.Cached)
        {
            // 本版mode0不读取mapId、演员或掉落列表，直接读取空骑乘组及成员选择。
            body[15] = byte.MaxValue;
            return body;
        }
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(14), checked((uint)plan.StartMap.Id));
        // 三个空列表及fog字段保留0，FF跳过成员选择；不代替NOTI28的格记录初始化。
        // FIX(95 client static analysis, NOTI29 reader 0238EF70): the first trailing byte after the passive-item list ([20]) is the hell-mode flag:
        // it is stored in globals 062EF5AA/0643C40C/062DC504, and the minimap hell icon (03A095A6) is only drawn when 062EF5AA != 0.
        // [22] is the party-member selector (FF = none); the previous experimental value 1 there was wrong.
        if (plan.HellParty is not null || plan.IsCosmo || plan.IsInfernoParty) body[20] = 1;
        var ridables = RoomRidables(plan);
        if (ridables.Count == 0)
        {
            body[22] = byte.MaxValue; // 实验性：深渊派对 fog 标志=1（参考项目 HellPartyFogFlag），普通入场保持 FF。
            return body;
        }
        // NOTI29 0238EFE8：groupCount:u8，每组 innerCount:u8，每项 5×u32，然后成员 byte。
        // 空组时 [21]=0 后直接读成员；有对象时不能再塞进固定 23B。
        if (ridables.Count > byte.MaxValue)
            throw new InvalidDataException("房间骑乘组超过通知数量范围");
        using var stream = new MemoryStream(21 + 2 + ridables.Count * 20 + 1);
        stream.Write(body.AsSpan(0, 21));
        stream.WriteByte(1);
        stream.WriteByte((byte)ridables.Count);
        foreach (var row in ridables)
            WriteRidableRecord(stream, row);
        stream.WriteByte(byte.MaxValue);
        return stream.ToArray();
    }

    // NOTI29每个已规划monster完整22B；等级和类型由领域确定，不在此解释资源行。
    public static byte[] StartMap(DungeonEntryPlan plan, DungeonMonsterPlan monsters, uint seed,
        DungeonMapOperation? operation = null, DungeonMapPayload payload = DungeonMapPayload.Build,
        IReadOnlyList<DungeonGroundObject>? passiveItems = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(monsters);
        int actorCount = checked(monsters.Monsters.Count(monster => monster.HellWave is null) + monsters.SpecialObjects.Count + monsters.HellWaves.Count);
        if (monsters.Room.Map != plan.StartMap || actorCount > byte.MaxValue)
            throw new InvalidDataException("怪物计划与房间不匹配或超过通知数量范围");
        byte[] empty = StartMap(plan, seed, operation, payload);
        if (payload == DungeonMapPayload.Cached) return empty;
        passiveItems ??= [];
        if (passiveItems.Count > byte.MaxValue)
            throw new InvalidDataException("特殊物品列表超过入房通知容量");
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(empty.AsSpan(0, 18));
        writer.Write(checked((byte)actorCount));
        foreach (var monster in monsters.Monsters.Where(monster => monster.HellWave is null))
        {
            if (monster.BattleLevel is < 1 or > byte.MaxValue)
                throw new NotSupportedException("怪物生成等级超出单字节范围");
            if (monster.SpawnType is not (<= 3 or >= 5 and <= 8))
                throw new NotSupportedException("演员生成类型不是已核验的怪物或APC类型");
            writer.Write((ushort)0);
            writer.Write(checked((uint)(monster.Source?.SpecialChild?.SourceOrdinal ?? monster.SourceOrdinal)));
            writer.Write(monster.ObjectKey);
            writer.Write(checked((uint)monster.MonsterId));
            writer.Write(checked((byte)monster.BattleLevel));
            writer.Write(monster.SpawnType);
            writer.Write(monster.IsSpecialChild ? (byte)1 : (byte)0);
            writer.Write(monster.IsSpecialChild ? (byte)1 : (byte)0);
            // 行offset16为必读有符号创建组，FF对应-1，不能省略或挪作等级/类型。
            writer.Write(monster.IsSpecialChild ? checked((byte)(monster.Source?.SpecialParent?.SourceOrdinal ??
                throw new InvalidDataException("特殊子actor缺少父资源索引"))) : byte.MaxValue);
            writer.Write(0u);
            // 22B行末byte为清房排除位；保持原ordinal/key与父子关联字段，不伪造死亡。
            writer.Write(monster.IsSpecialChild || monster.IsFriendly || monster.IsScriptedRetirement || monster.Source?.IsDisplayHuntDummy == true ? (byte)1 : (byte)0);
        }
        foreach (var actor in monsters.SpecialObjects)
        {
            writer.Write((ushort)0);
            writer.Write(checked((uint)actor.Source.SourceOrdinal));
            writer.Write(actor.ObjectKey);
            writer.Write(checked((uint)actor.Source.ObjectId));
            writer.Write((byte)0); // 父被动对象没有普通怪物等级。
            writer.Write((byte)9);
            writer.Write((byte)0);
            writer.Write((byte)0);
            writer.Write(byte.MaxValue);
            writer.Write(0u);
            writer.Write((byte)1); // 不参与普通活怪清房扫描。
        }
        // 实验性（未在95客户端实证；依据参考项目 AppendHellPartyTemplateRows）：深渊波次怪作为隐藏模板行追加：TemplateOrder=波次号，
        // Flag0=1(HellPartyHiddenTemplateFlag)，Flag1=0xFF(HellPartyAttachAllWavesSelector)，其余字段同普通行。
        foreach (var wave in monsters.HellWaves)
        {
            writer.Write(checked((ushort)wave.Order));
            writer.Write(checked((uint)wave.PacketIndex));
            writer.Write(wave.ObjectKey);
            writer.Write(checked((uint)wave.MonsterId));
            writer.Write(monsters.BattleLevelFor(wave));
            writer.Write(wave.Type);
            writer.Write((byte)1);
            writer.Write(byte.MaxValue);
            writer.Write(byte.MaxValue);
            writer.Write(0u);
            writer.Write((byte)0);
        }
        writer.Write(checked((byte)passiveItems.Count));
        var keys = new HashSet<uint>();
        foreach (var ground in passiveItems)
        {
            ground.Content.Validate();
            if (ground.PassiveOrigin is not { } origin || ground.Reservation.Map != plan.StartMap ||
                ground.DeathIdentity.CharacterId != plan.CharacterId || !keys.Add(ground.Reservation.GroundKey) ||
                !monsters.SpecialObjects.Any(parent => parent.ObjectKey == ground.Reservation.MonsterKey &&
                    parent.Source.SourceOrdinal == origin.ParentOrdinal))
                throw new InvalidDataException("入房特殊物品缺少原父对象或地面身份重复");
            // ARAD 0238EE44..0238EEE7固定21B，不使用NOTI38的123B物品RAW。
            writer.Write(origin.AssociationSlot);
            writer.Write(ground.Reservation.GroundKey);
            writer.Write(ground.Content.Item?.ItemId ?? 0);
            writer.Write(ground.Content.Item?.Value ?? ground.GoldAmount);
            writer.Write(ground.Content.Item?.Durability ?? (ushort)0);
            // 两字段沿ARAD的u8/u16读取与共同记录构造器，不混入Attr的强化位。
            ItemListProtocolWriter.WriteAmplification(writer, ground.Content.Item);
            writer.Write((ushort)0); // 创建可见性过滤为0；不将父序号或坐标塞进该字段。
            writer.Write((byte)0);
        }
        writer.Write(empty.AsSpan(20));
        return stream.ToArray();
    }

    // 实验性（未在95客户端实证；依据参考项目 BuildHellPartyMonsterInfo / NOTI 679）：int32 count + count×(int32 code, int32 level)，按出现顺序去重 code，等级取 max(1,level)。
    // 没有波次怪时返回 null（不发包）。
    public static byte[]? HellPartyMonsterInfo(DungeonMonsterPlan monsters)
    {
        var entries = new List<(int Code, int Level)>();
        foreach (var wave in monsters.HellWaves)
            if (wave.MonsterId > 0 && entries.All(entry => entry.Code != wave.MonsterId)) entries.Add((wave.MonsterId, Math.Max(1, (int)monsters.BattleLevelFor(wave))));
        if (entries.Count == 0) return null;
        var body = new byte[4 + entries.Count * 8];
        BinaryPrimitives.WriteInt32LittleEndian(body, entries.Count);
        for (int i = 0; i < entries.Count; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(4 + i * 8), entries[i].Code);
            BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(8 + i * 8), entries[i].Level);
        }
        return body;
    }

    // NOTI30 在 023842FC/0238434A 读取 u32 和 u8。当前普通路径是五个 0。
    public static byte[] LoadingNotice() => new byte[5];

    // NOTI3：count、角色 ID、state。state 1 才放行已确认的过房发送点。
    public static byte[] DungeonPlayerState(ushort characterId) => [1, (byte)characterId, (byte)(characterId >> 8), 1];

    public static byte[] MoveAccepted() => [1];

    // ACK42 成功标志为 1。回城位置仍由已经确认的 NOTI24 携带。
    public static byte[] GiveUpAccepted() => [1];

    // NOTI38普通路径：monster key/count，每条u32地面对象key、123B物品核心、两个u16创建上下文，再接死亡尾部。
    // 只投影调用方已经确定的地面身份和完整资产；这里不抽奖、不确认死亡、不创建物品或写库。
    public static byte[] MonsterDeath(ushort monsterKey, IReadOnlyList<DungeonDropPacketItem> drops, DungeonPoint? bossPositionUpdate = null)
    {
        ArgumentNullException.ThrowIfNull(drops);
        const int dropSize = 4 + ItemListProtocolWriter.EntrySize + 4;
        if (bossPositionUpdate is { } point && (point.X is < 0 or > byte.MaxValue || point.Y is < 0 or > byte.MaxValue))
            throw new InvalidDataException("死亡通知的Boss目标坐标超出单字节范围");
        if (drops.Count > (65520 - 9 - (bossPositionUpdate is null ? 0 : 2)) / dropSize)
            throw new InvalidDataException("死亡掉落通知超过本版帧容量");
        var keys = new HashSet<uint>();
        using var bytes = new MemoryStream();
        using var writer = new BinaryWriter(bytes);
        writer.Write(monsterKey);
        writer.Write(checked((ushort)drops.Count));
        foreach (var drop in drops)
        {
            if (drop is null || !keys.Add(drop.ObjectKey) ||
                (drop.Core is null ? drop.GoldAmount <= 0 : drop.GoldAmount != 0))
                throw new InvalidDataException("死亡掉落缺失物品核心或地面对象key重复");
            // 时装和宠物另有RAW/detail消费者，不能用定长核心冒充完整通知。
            if (drop.Core?.Kind is ItemKind.Avatar or ItemKind.Creature)
                throw new NotSupportedException("死亡掉落尚未接入时装或宠物附加记录");
            writer.Write(drop.ObjectKey);
            if (drop.Core is not null)
                ItemListProtocolWriter.Write(writer, drop.RecordSlot, drop.Core);
            else
                ItemListProtocolWriter.WriteGroundGold(writer, drop.RecordSlot, drop.GoldAmount);
            writer.Write(drop.ContextValue);
            writer.Write(drop.VisibilityFilter);
        }
        writer.Write(new byte[] { 0, 0, 255 });
        // ARAD 02395C8E读取更新标志，02395CAC/5CBA读取新目标X/Y，末尾仍保留死亡标志。
        writer.Write(bossPositionUpdate is null ? (byte)0 : (byte)1);
        if (bossPositionUpdate is { } boss)
        {
            writer.Write(checked((byte)boss.X));
            writer.Write(checked((byte)boss.Y));
        }
        writer.Write((byte)0);
        return bytes.ToArray();
    }

    // 0238EFE8 只消费当前格的骑乘记录；其它格的 [map] 声明不进入本包。
    static IReadOnlyList<DungeonRandomizedObject> RoomRidables(DungeonEntryPlan plan)
    {
        var result = new List<DungeonRandomizedObject>();
        foreach (var row in plan.Maze.RandomizedObjects)
            if (row.Map == plan.StartMap.Position && row.Team is "neutral" or "monster" or "character")
                result.Add(row);
        return result;
    }

    static void WriteRidableRecord(MemoryStream stream, DungeonRandomizedObject row)
    {
        Span<byte> buffer = stackalloc byte[20];
        // 0238F055 起连续五个 u32，原顺序交给 02A11610。Q1/Q2 为位置分量，Q3 为模板号。
        // [neutral] 与 92270 步行者 monster team 0 对齐；Q5=0 走 ridable 模式 0，不走 Q5=1 的被动初始化。
        uint faction = row.Team switch { "monster" => 100, "character" => 200, _ => 0 };
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, checked((uint)row.PositionX));
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[4..], checked((uint)row.PositionY));
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[8..], checked((uint)row.ObjectId));
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[12..], faction);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[16..], 0);
        stream.Write(buffer);
    }
}
