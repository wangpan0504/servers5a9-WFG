using DfoServer.GameWorld.Channels;
using DfoServer.GameWorld.Towns;

namespace DfoServer.Game.Characters;

// 特殊区域与团队频道的落点按频道分组找 TownCatalog 里已有的门口；普通频道的房间出口按角色记住的返回地区（#740）。
// 坐标来自区域 [gate]、共享房间或相邻地区反推的 Entrance，以及已核对的初始返回点。
internal static class ChannelAreaLanding
{
    internal readonly record struct Decision(Location? Spot, string Reason);

    // 超时空之战（卡名超时空漩涡）频道类型 0x2D = 45（CHANNEL_FIEND_WAR）。ARAD 频道虚表 +0x2C（01EB5740）即 type==0x2D；
    // 团队频道卡片构建 017128D0 对 type 0x2D 按事件 2449 与任务 8672 筛选，不需要 RaidChannel 插件。
    internal const int FiendwarType = 0x2D;

    // 魔界大战频道类型：ARAD 频道类型名表 03EFBF1D 写入 CHANNEL_EVILDOM_WAR = 0x36（同表 CHANNEL_EVILDOM_WAR_CHALLENGE = 0x40）。
    internal const int EvildomWarType = 0x36;

    // 普雷·伊希斯攻坚战频道类型：ARAD 频道类型名表 03EFBE19 写入 CHANNEL_PREY_RAID = 0x32。页签分类 01EC58C6 经 vt+0x28（028FA400 含 0x32）放进团队页签；
    // 团队列表 017128D0 的 0x32 分支要（事件 414 在 EVENT_INFO 集合 或 当前频道 type 0x11/0x15）且任务 8935 完成且 Lv≥95 才列出。
    internal const int PreyRaidType = 0x32;

    // 五陵频道类型：ARAD 频道类型名表 03EFBF9F 写入 CHANNEL_XIAN_VILLAGE = 0x38。外层白名单 028FB090 含 0x38，页签分类 vt+0x114（01EB5810，type==0x38）
    // 放进特殊区域页；01EB5AF0 比较的 14 个谓词（vt+0x14/0x28/0x30/0x34/0x38/0x3C/0x40/0x44/0x48/0x4C/0x60/0x64/0x6C/0x78）对 0x38 与普通 0x16 全部相同。
    internal const int XianVillageType = 0x38;

    // 勇士对决：PVF etc/channel_info 名称键060 `勇士对决 - 自由`、061 `勇士对决 - 排位`；ARAD 类型名表 CHANNEL_DNFAOS_FREE = 0x3F、
    // CHANNEL_DNFAOS_RANK = 0x3E，外层白名单 028FB090 含两者，vt+0x74（{0x3E,0x3F}）放进特殊区域页；vt+0x28 团队页签谓词含两者。
    // 03B876C0 对这两类返回城镇 119（dnfaos.twn `混沌之村暴风雨`）。卡片过滤 017128D0 另查事件 2505/2506，本服未实现该玩法。
    internal const int DnfaosFreeType = 0x3F;
    internal const int DnfaosRankType = 0x3E;

    // 安徒恩频道类型 CHANNEL_ANTON_RAID = 0x17。ARAD 03B87FE0（经 02330FF0/02330DF0 读 ACK LOGIN 类型 +0x1010）给出当前频道的起始地点：
    // type 0x17 返回 (0x13=19, 0) 战舰，其余类型一律返回 (0x26=38, 1) 赛丽亚房间。调用方 00D20658（条件求值 00D1F8E0）与 015B26D1（015B2550 路由）
    // 按这一对坐标判断/引导角色回到频道起始地点。故换频道/选角时只有安徒恩直接落 19/0，其余有落点的频道先进赛丽亚房间，出门再到该频道地区。
    internal const int AntonRaidType = 0x17;

    // 进入频道（选角或不保留快照的换频道）时是否直接落到频道地区；其余频道出生赛丽亚房间。
    internal static bool LandsInChannelTown(ChannelEntry channel) => channel.Type == AntonRaidType;

    // 安徒恩频道的起始城镇：客户端 03B87FE0 对 type 0x17 返回 (19, 0)，换频道直接落在战舰房间 19/0。
    // 战舰城镇不是 IsChannelBoundTown（19/0 继承共享房间几何，19/1 另有通往 19/2 的门），但 19/0 是频道出生点，
    // 城镇里的街区不能写成共享房间的返回地区，否则普通频道的 1379 与出门城镇会变成战舰（#740 回归）。
    internal const byte AntonTown = 19;

    // 普通区域频道类型：channel.txt 的 21 个普通区域入口。选角出生共享房间，房间出口只认角色记住的返回地区（#740）；
    // 频道分组只决定特殊区域与团队频道的落点。与 Docs/模块/城镇.md 第 4 条“普通房间出口必须匹配持久返回地区”一致。
    internal const int OrdinaryType = 22;

    // 普通频道的赛丽亚房间出口是否只按角色记住的返回地区：在哪个城镇下线就回哪个城镇，不被频道分组改写成主题城（#740）。
    internal static bool RoomExitUsesRememberedDistrict(ChannelEntry channel) => channel.Type == OrdinaryType;

    // 赛丽亚房间出门的频道地区：用户确认“频道切换应该是到赛利亚房间，然后再出门才是对应的地区”。
    // 只对特殊区域与团队频道生效；普通频道与无频道按角色记住的返回地区出门（RoomExitUsesRememberedDistrict）。
    // 只对共享房间 towns.Room 生效（区域房间仍走自身门），频道无已证落点或条件不足时返回 null，按存档返回地区出门。
    internal static Location? RoomExit(TownCatalog towns, ChannelEntry? channel, Location position, int level, IReadOnlySet<int> completed)
    {
        if (channel is null || RoomExitUsesRememberedDistrict(channel) || LandsInChannelTown(channel) ||
            position.Town != towns.Room.Town || position.Area != towns.Room.Area)
            return null;
        return Place(towns, channel, level, completed);
    }

    // 已知旧频道类型时直接按01EB5AF0已证的团队页、魔界大战与再战魔界谓词比较。
    internal static bool SameSnapshotPredicates(int previousType, int nextType) => SnapshotPredicates(previousType) == SnapshotPredicates(nextType);

    // ARAD 01EB5AF0 逐项比较新旧频道的类型谓词（读频道脚本类型 +8，不读 ACK LOGIN 写入的 +0x1010）。区分这里各频道的是三项：
    // vt+0x28（01EB5720 → 028FA400，type ∈ {0x17,0x19,0x20,0x2D,0x32,0x3E,0x3F}，即团队页签类型）、vt+0x4C（type==0x36）及vt+0x38（type==0x35）。
    // 三项都一致时客户端记录换频道快照并按快照留在原城镇；任一项不同则不记快照，ACK4 按服务端城镇入城。
    // 因此 普通(22)↔安徒恩 0x17/卢克 0x20/超时空之战 0x2D/普雷 0x32 互换不记快照，团队页签类型之间（如 0x20↔0x2D、0x20↔0x32）互换保留快照，
    // 魔界大战 0x36 与其余任何类型互换都不记快照。
    static readonly int[] RaidTabTypes = [0x17, 0x19, 0x20, FiendwarType, PreyRaidType, 0x3E, 0x3F];

    // 有落点城镇的谓词频道；服务端不保存旧频道，按存档城镇推断旧频道类型。
    static readonly (int Type, string Group)[] PredicateChannels =
        [(0x17, "[raid]"), (0x20, "[luke_raid]"), (FiendwarType, "[fiendwar]"), (EvildomWarType, "[evildom_war]"), (PreyRaidType, "[prey]"),
         (DnfaosFreeType, "[dnfaos_free]"), (DnfaosRankType, "[dnfaos_rank]"), (53, "[evildom_gathering]")];

    // 01EB5BCD..5C0B另比较vt+38，01EB5780正是type53；跨入/离开再战不能复用旧城镇快照。
    static (bool RaidTab, bool Evildom, bool TagEvildom) SnapshotPredicates(int type) => (RaidTabTypes.Contains(type), type == EvildomWarType, type == 53);

    public static Location? Place(TownCatalog towns, ChannelEntry channel, int level, IReadOnlySet<int> completed) =>
        Decide(towns, channel, level, completed).Spot;

    internal static Decision Decide(TownCatalog towns, ChannelEntry channel, int level, IReadOnlySet<int> completed)
    {
        if (Target(channel.Type, channel.Group) is not var (town, area))
            return new(null, "no-proven-town");
        // etc/enterablespecialchannel.etc 按频道键写入场任务；只对与分组同名的键生效（当前 fiendwar=8672、prey=8935）。
        if (towns.SpecialChannelQuests.TryGetValue(GroupName(channel.Group), out int channelQuest) && !completed.Contains(channelQuest))
            return new(null, "channel-quest-required");
        return DecideArea(towns, town, area, level, completed);
    }

    // 指定区域的落点判定（目录、PVF 等级/任务准入、门口可站），不含频道分组映射与频道入场任务。
    internal static Decision DecideArea(TownCatalog towns, byte town, byte area, int level, IReadOnlySet<int> completed)
    {
        if (!towns.Areas.TryGetValue((town, area), out var definition))
            return new(null, "town-not-in-catalog");
        var probe = new Location(town, area, 0, 0);
        string admission = towns.DestinationAdmission(probe, level, completed);
        if (admission != "ready")
            return new(null, "admission-" + admission);
        if (definition.Gate is { } gate && towns.IsPosition(gate, completed))
            return new(gate, "area-gate");
        if (towns.Entrance(towns.Room, probe, completed) is { } entrance && towns.IsPosition(entrance, completed))
            return new(entrance, "room-entrance");
        if (towns.InitialReturn.Town == town && towns.InitialReturn.Area == area && towns.IsPosition(towns.InitialReturn, completed))
            return new(towns.InitialReturn, "initial-return");
        var map = definition.Map(completed);
        foreach (var link in map.Links.Where(link => link.Gate.Door).OrderBy(link => link.Town == town ? 0 : 1).ThenBy(link => link.Town).ThenBy(link => link.Area))
        {
            if (towns.Entrance(new(link.Town, link.Area, 0, 0), probe, completed) is { } door && towns.IsPosition(door, completed))
                return new(door, "neighbor-door");
        }
        return new(null, "no-standable-door");
    }

    // 原生换频道是否保留本地城镇快照。ARAD 01714512/01715512 只在 01EB5AF0(新频道) 为真时调用 02906330 记录快照，
    // 01EB5AF0 要求新旧频道在 vt+0x14/0x28/0x30/0x34/0x38/0x3C/0x40/0x44/0x48/0x4C/0x60/0x64/0x6C/0x78 上逐项一致（这里只建模已证的 +0x28/+0x38/+0x4C）；
    // 不记快照时 023E11AF 的 124 恢复也因 028FB500 为假而跳过，ACK4 直接按服务端城镇入城，应走普通选角落点和包序。
    // 服务端不保存旧频道：存档位于某谓词频道的落点城镇即视为旧频道可能是该类型；落点城镇不是频道专属城镇时（安徒恩 19 等）
    // 旧频道也可能是普通频道。只要任一候选旧类型与新频道谓词一致就按保留快照处理——服务端保留原位置而客户端实际入服务端城镇只是
    // 不换城，反过来服务端改落点而客户端按快照留在原城，会让客户端随后按原城请求移动被 route-not-in-pvf/room-return-mismatch 拒绝。
    internal static bool ClientKeepsTownSnapshot(TownCatalog? towns, ChannelEntry? channel, Location position)
    {
        if (channel is null)
            return true;
        var candidates = PredicateChannels.Where(bound => Target(bound.Type, bound.Group) is var (town, _) && position.Town == town)
            .Select(bound => SnapshotPredicates(bound.Type)).ToList();
        if (candidates.Count == 0 || towns is null || !IsChannelBoundTown(towns, position.Town))
            candidates.Add(SnapshotPredicates(OrdinaryType));
        return candidates.Contains(SnapshotPredicates(channel.Type));
    }

    // 只能经频道进入的城镇：本城区域的门口都不出城，其他城镇和赛丽亚房间传送门也不进来（最后防线 115 另写 [disable save point]）。
    // 这类落点不覆盖原返回地区，离开频道后仍按原地区回到普通城镇。
    internal static bool IsChannelBoundTown(TownCatalog towns, byte town)
    {
        var own = towns.Areas.Values.Where(area => area.Town == town).ToArray();
        static IEnumerable<Link> Links(TownArea area) => area.Phases.Prepend(area.Default).SelectMany(map => map.Links);
        return own.Length > 0 && own.All(area => Links(area).All(link => link.Town == town)) &&
            towns.Areas.Values.Where(area => area.Town != town).All(area => Links(area).All(link => link.Town != town)) &&
            towns.RoomPortals.All(portal => portal.Town != town);
    }

    // 共享房间的返回地区只能落在普通地区：频道起始城镇（只能经频道进入的城镇与安徒恩直达战舰）的街区不参与，
    // 进入或城内切图一律沿用进入前的返回地区，避免战舰 19/1 被当成普通频道赛丽亚房间的出口（#740 回归）。
    internal static bool PreservesRoomReturn(TownCatalog towns, byte town) =>
        town == AntonTown || IsChannelBoundTown(towns, town);

    static string GroupName(string group) => group.Trim().TrimStart('[').TrimEnd(']');

    // 频道 10（[granfloris]）是主游戏入口，普通名册选角仍按角色与入城合同出生共享房间 38/1，不在这里落点。
    // 分组对应的是带 [dungeon gate] 的区域，或 channeluiinfo 城镇号已在目录中的特殊类型。没有这两者的分组不落点。
    static (byte Town, byte Area)? Target(int type, string group)
    {
        var name = GroupName(group);
        if (type == 23)
            return (19, 0);
        if (type == 24)
            return (10, 0);
        if (type == 18)
            return (15, 0);
        if (type == 26)
            return (17, 0);
        if (type == 11)
            return (38, 5);
        // 再战频道仍按原生38/1入城；此为本服RoomExit业务落点，非客户端03B876C0的默认映射。
        // 当前PVF town62 Stadium.twn 区域0及map的enter tagdungeon入口；等级/任务继续由Decide校验。
        if (type == 53)
            return (62, 0);
        return name switch
        {
            "elven_guard" => (38, 0),
            "sky_catle" => (40, 3),
            "behemoth" => (40, 4),
            "Alfhlyra" => (41, 2),
            "Noirpera" => (42, 3),
            "Charnak" => (43, 1),
            "north_myre" => (39, 5),
            "metro" => (30, 2),
            "timedoor" => (12, 0),
            "powerstation" => (14, 2),
            "Abnoba" => (46, 6),
            "Meltdown" => (46, 5),
            "FallsToTheSky" => (46, 4),
            // 0区固定为Before图；正式街道1区按任务切换地图，才能保留6356完成后的乔斯拉。
            "harlem" or "east harlem" => (54, 1),
            "luke_raid" => (32, 0),
            "sainthorn" => (17, 0),
            // 任务 8672「[超时空漩涡]奔赴前线」写明完成后“可以通过团队频道的超时空漩涡进入最后防线”；最后防线是 town 115
            // chn_fiendwar_normal.twn，区域 0 为主城（need level 90），区域 1 带 [dungeon gate] 201（超时空之战 400000016）。
            // 频道 39 名称键 048 = 超时空漩涡；115 无任何城镇/传送门连入，只能经频道落点到达。ARAD 03B876C0 把 type 0x2D 的
            // 返回城镇（NOTI 1379 PREV_VILLAGE）改成 57，只管返回城镇、不决定落点，因此落点仍取 115/0（2026-10-22 决定，撤销 5fe61f2f 的 57/0）。
            "fiendwar" => (115, 0),
            // 魔界大战：任务 talisman_00「[护石]未知的石头」写“该任务只能在魔界大战频道完成”，交任务 NPC 100000149 莫妮卡只摆在
            // town 67 TheEvildomWar_P.twn（艾尔丁纪念馆）区域 1；67 没有其他城镇或传送门进入，三个区域 need level 95、无任务。
            // 区域 0 的 [gate] 474 234 是独立标签，TownCatalog 不读，落点由通往 67/1 的门口反推。
            "evildom_war" => (67, 0),
            // 普雷·伊希斯攻坚战：etc/raid/prey/prey.etc [WAITING ROOM] 59 2；town 59 Prey_raid.twn（灰色墓地）区域 0..5 均 need level 95，
            // 区域 0 为 Gate_Prey.map（与 67/0 同为门口图）。59/0 与 59/2 均经 PreyChannelTests 资源测试可站，按用户决定取 59/0。
            "prey" => (59, 0),
            // 五陵：town.lst 117 chn_Teaching_xian.twn 的 [name] 为`五陵`，与名称键 chn_channel_info_055=`五陵` 同名；区域 0 chn_Gate_xian.map
            // 带 [gate] 474 234（need level 无），其余区域 need level 17/95。117 区域 4 连入遗迹城镇，不是频道专属城镇。
            "xian_village" => (117, 0),
            // 勇士对决：town.lst 119 dnfaos.twn；区域 0 的 Gate_dnfaos.map 在 Script.pvf 里不存在（Decide 报 admission-map-resource-missing），
            // 改取区域 1（need level 95），经 Decide 得邻门落点 270,148。
            "dnfaos_free" or "dnfaos_rank" => (119, 1),
            _ => null
        };
    }
}
