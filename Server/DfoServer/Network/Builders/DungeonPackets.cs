using System.Buffers.Binary;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Items;
using DfoServer.Network.Builders.Inventory;

namespace DfoServer.Network.Builders;

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
    public static byte[] EmptySelectNotice() => new byte[42];

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

    // ACK16 失败读取 success:u8、error:u16 和 memberSlot:u8；错误9静默恢复选图等待。
    public static byte[] SelectionRejected() => [0, 9, 0, 0];
    // 实验性（未在95客户端实证）：深渊派对拒绝包，code 0xAD=缺前置任务、0x01=区域/房间不可用（参考项目 A21 DungeonAdmissionRejectBuilder）。
    public static byte[] HellPartyRejected(byte code) => [0, code, 0, 0];
    // NOTI28的匹配36B布局；调用方提供已审核的准入计划，构包不建立实例或确认加载。
    public static byte[] Information(DungeonEntryPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        byte[] body = new byte[36];
        BinaryPrimitives.WriteUInt32LittleEndian(body, checked((uint)plan.DungeonId));
        body[4] = plan.Difficulty;
        body[7] = checked((byte)plan.Maze.SourceOrdinal);
        body[8] = checked((byte)plan.BossMap.Position.X);
        body[9] = checked((byte)plan.BossMap.Position.Y);
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
        if (plan.HellParty is not null) body[7] = plan.HellPartyMode;
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
        if (plan.HellParty is not null) body[20] = 1;
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
            if (monster.CandidateLevel is < 1 or > byte.MaxValue)
                throw new NotSupportedException("怪物生成等级超出单字节范围");
            if (monster.SpawnType is not (<= 3 or >= 5 and <= 8))
                throw new NotSupportedException("演员生成类型不是已核验的怪物或APC类型");
            writer.Write((ushort)0);
            writer.Write(checked((uint)(monster.Source?.SpecialChild?.SourceOrdinal ?? monster.SourceOrdinal)));
            writer.Write(monster.ObjectKey);
            writer.Write(checked((uint)monster.MonsterId));
            writer.Write(checked((byte)monster.CandidateLevel));
            writer.Write(monster.SpawnType);
            writer.Write(monster.IsSpecialChild ? (byte)1 : (byte)0);
            writer.Write(monster.IsSpecialChild ? (byte)1 : (byte)0);
            // 行offset16为必读有符号创建组，FF对应-1，不能省略或挪作等级/类型。
            writer.Write(monster.IsSpecialChild ? checked((byte)(monster.Source?.SpecialParent?.SourceOrdinal ??
                throw new InvalidDataException("特殊子actor缺少父资源索引"))) : byte.MaxValue);
            writer.Write(0u);
            // 22B行末byte为清房排除位；保持原ordinal/key与父子关联字段，不伪造死亡。
            writer.Write(monster.IsSpecialChild || monster.IsFriendly || monster.Source?.IsDisplayHuntDummy == true ? (byte)1 : (byte)0);
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
            writer.Write(wave.Level);
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
            if (wave.MonsterId > 0 && entries.All(entry => entry.Code != wave.MonsterId)) entries.Add((wave.MonsterId, Math.Max(1, (int)wave.Level)));
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
