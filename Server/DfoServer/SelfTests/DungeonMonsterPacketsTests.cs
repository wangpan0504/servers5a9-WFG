using static DfoServer.Testing.TestCheck;
using DfoServer.Development.Dungeons;
using System.Buffers.Binary;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Towns;
using DfoServer.Network.Builders;
using PvfLib;

namespace DfoServer;

internal static class DungeonMonsterPacketsTests
{
    public static void RunCore()
    {
        int checks = Count;
        var map = new DungeonMap("boss",new(0,0),1,"map/faction-fixture.map");
        var maze = new DungeonMaze(0,1,1,"fixture",[map.Position],[map.Position],[map],[]);
        var entry = new DungeonEntryPlan(99,0,3,0,byte.MaxValue,0,maze,map,map);
        var source = new DungeonRoomMonster(1034,[1034,0,20,993,421,0,1,1],"[fixed]","[normal]","monster/fixture.mob");
        var room = new DungeonRoomDefinition(map,[source,source,source,source],[],[],[100,0,7,-1]);
        foreach (var (team, flags) in new (int? Team, byte[] Flags)[]
        {
            (0,[0,1,0,0]), (7,[0,0,1,0]), (null,[0,0,0,0])
        })
        {
            var plan = DungeonMonsterPlan.FromRoom(room,99,10,playerTeam: team);
            foreach (var payload in new[] { DungeonMapPayload.Build,DungeonMapPayload.Refresh })
            {
                byte[] body = DungeonPackets.StartMap(entry,plan,1,payload: payload);
                That(body.Length == 111 && body[13] == (byte)payload && body[18] == 4 &&
                    body.AsSpan(107).SequenceEqual(new byte[] { 0,0,0,255 }), "阵营标记不改变完整22B演员列表与尾部对齐");
                for (int index = 0; index < flags.Length; index++)
                {
                    int offset = 19+22*index;
                    That(BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(offset+2)) == index &&
                        BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(offset+6)) == 10+index &&
                        BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(offset+8)) == 1034 && body[offset+12] == 20 &&
                        body[offset+14] == 0 && body[offset+15] == 0 && body[offset+16] == 255 &&
                        body[offset+21] == flags[index], "同阵营只置末byte排除位，原ordinal/key/等级与创建组保留");
                }
            }
        }
        byte[] npcRow = [0,0,0,0,0,0,10,0,10,4,0,0,20,3,0,0,255,0,0,0,0,0];
        foreach (int parameter in new[] { 0, -1, 1069 })
        foreach (var payload in new[] { DungeonMapPayload.Build,DungeonMapPayload.Refresh })
        {
            var npcPlan = DungeonMonsterPlan.FromRoom(room with
                { Monsters = [source with { RankKind = "[boss]", Modifiers = ["[NPC]"], NpcParameter = parameter }], MonsterTeams = null },99,10);
            byte[] body = DungeonPackets.StartMap(entry,npcPlan,1,payload: payload);
            That(body.Length == 45 && body[18] == 1 && body.AsSpan(19,22).SequenceEqual(npcRow) &&
                body.AsSpan(41).SequenceEqual(new byte[] { 0,0,0,255 }),
                "纯NPC使用独立预期的完整22B原ordinal/key/模板/Boss行，不附加或截断原NPC参数");
        }
        DungeonRoomAiCharacter Actor(int id,string faction) => new(id,[id,0,0,0,0,0],faction,"[normal]","aicharacter/fixture.aic",20);
        var apcs = DungeonMonsterPlan.FromRoom(new(map,[],[],[])
            { AiCharacters = [Actor(1,"[character]"),Actor(2,"[neutral]"),Actor(3,"[monster]")] },99,10);
        byte[] apcBody = DungeonPackets.StartMap(entry,apcs,1);
        That(apcBody.Length == 89 && apcBody[40] == 1 && apcBody[62] == 1 && apcBody[84] == 0 &&
            apcBody[32] == 5 && apcBody[54] == 5 && apcBody[76] == 5,
            "友方/中立APC排除清房记录，敌对APC保留原类型与标记");
        Console.WriteLine($"PASS {Count - checks} 阵营清房22B生成通知断言");
    }

    public static void Run(PvfArchive pvf)
    {
        int checks = Count;
        var dungeons = global::DfoServer.Testing.SelfTestCatalogs.Dungeons(pvf);
        var rooms = global::DfoServer.Testing.SelfTestCatalogs.Rooms(pvf);
        var character = new Character(0x100, 0, "Monster", 7, new Location(38, 2, 100, 249, 0), null)
        {
            ActiveMainQuests = [new(0, 3145, 1)]
        };
        That(DungeonEntryPlan.TryCreate(dungeons, character, 3, 3145, 0, out var entry, out _), "monster fixture uses the actual task maze");
        var approvedEntry = entry ?? throw new InvalidDataException("3145怪物构包缺少准入计划");
        foreach (var map in approvedEntry.Maze.MapSpecification!)
        {
            var plan = approvedEntry with { StartMap = map };
            var monsters = DungeonMonsterPlan.Create(rooms, map, character.Id, 0x100, approvedEntry.BasisLevel,
                playerTeam: approvedEntry.PlayerTeam);
            byte[] body = DungeonPackets.StartMap(plan, monsters, 0xF1234567);
            That(DungeonEntryProbe.BuildStartMap(plan, monsters, 0xF1234567).SequenceEqual(body),
                "诊断调用委托同一正式纯Builder，保持完整怪物字节");
            int expectedCount = map.Id switch { 76121 => 4, 76122 => 5, 76123 => 7, 76124 => 5, 76125 => 6, 76126 => 2,
                _ => throw new InvalidDataException("3145出现未核验房间") };
            // 76125还包含模板225的特殊父对象，完整通知数量不能只计算普通怪物。
            int expectedParents = map.Id == 76125 ? 1 : 0;
            That(monsters.Monsters.Count == expectedCount && monsters.SpecialObjects.Count == expectedParents,
                "3145各房间保留完整怪物与特殊父对象资源行");
            That(body.Length == 23 + (expectedCount + expectedParents) * 22 && body[18] == expectedCount + expectedParents,
                "Client全部演员行使用22B布局，保留必读有符号字段");
            That(body[0] == map.Position.X && body[1] == map.Position.Y
                && BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(14)) == map.Id, "each room retains its own map and grid position");
            using var reader = new BinaryReader(new MemoryStream(body, 19, body.Length - 19));
            var keys = new HashSet<ushort>();
            for (int index = 0; index < expectedCount; index++)
            {
                That(reader.ReadUInt16() == 0 && reader.ReadUInt32() == index, "template ordinal and independent prefix retain source order");
                ushort key = reader.ReadUInt16();
                That(key != character.Id && keys.Add(key), "monster keys do not alias the character or another row");
                byte expectedLevel = map.Id == 76125 && index == 4 ? (byte)5 : (byte)3;
                byte expectedType = map.Id == 76126 && index == 0 ? (byte)3 : (byte)0;
                That(reader.ReadUInt32() == monsters.Monsters[index].MonsterId && reader.ReadByte() == expectedLevel,
                    "monster code and candidate level come from this room's resource");
                That(reader.ReadByte() == expectedType && reader.ReadByte() == 0 && reader.ReadByte() == 0
                    && reader.ReadSByte() == -1 && reader.ReadUInt32() == 0 && reader.ReadByte() == (map.Id == 76126 && index == 1 ? 1 : 0),
                    "candidate flags include the mandatory Client signed byte before the u32");
            }
            for (int index = 0; index < expectedParents; index++)
            {
                That(reader.ReadUInt16() == 0 && reader.ReadUInt32() == index, "特殊父对象保留独立资源索引与行前缀");
                ushort key = reader.ReadUInt16();
                That(key != character.Id && keys.Add(key), "特殊父对象key与角色及全部怪物不重复");
                That(reader.ReadUInt32() == 225 && reader.ReadByte() == 0 && reader.ReadByte() == 9
                    && reader.ReadByte() == 0 && reader.ReadByte() == 0 && reader.ReadSByte() == -1
                    && reader.ReadUInt32() == 0 && reader.ReadByte() == 1,
                    "76125特殊父对象225保留类型9、零等级及非阻塞位，不丢失或混入普通怪物");
            }
            That(reader.ReadBytes(4).SequenceEqual(new byte[] { 0, 0, 0, 255 }) && reader.BaseStream.Position == reader.BaseStream.Length,
                "extra list, fog, ridable list and member tail remain aligned after the last actor");
        }

        var firstMonsters = DungeonMonsterPlan.Create(rooms, approvedEntry.StartMap, character.Id, 0x100);
        var firstMonster = firstMonsters.Monsters[0];
        DungeonMonsterPlan Single(DungeonPlannedMonster monster) => firstMonsters with
        {
            Monsters = Array.AsReadOnly(new[] { monster })
        };
        var wideMonster = firstMonster with { SourceOrdinal = int.MaxValue, ObjectKey = 0xFEDC, MonsterId = 0x12345678 };
        byte[] wide = DungeonPackets.StartMap(approvedEntry, Single(wideMonster), uint.MaxValue);
        That(wide.Length == 45 && wide[18] == 1 && BinaryPrimitives.ReadUInt32LittleEndian(wide.AsSpan(3)) == uint.MaxValue &&
            BinaryPrimitives.ReadUInt32LittleEndian(wide.AsSpan(21)) == int.MaxValue &&
            BinaryPrimitives.ReadUInt16LittleEndian(wide.AsSpan(25)) == 0xFEDC &&
            BinaryPrimitives.ReadUInt32LittleEndian(wide.AsSpan(27)) == 0x12345678,
            "正式Builder保留完整u32种子、SourceOrdinal、模板ID及u16对象key，不截为byte");
        foreach (int level in new[] { 0, -1, 256, int.MaxValue })
            Reject<NotSupportedException>(() => DungeonPackets.StartMap(approvedEntry, Single(firstMonster with { CandidateLevel = level }), 1),
                "等级超出已核验wire范围时拒绝 " + level);
        foreach (byte type in new byte[] { 0,1,2,3,5,6,7,8 })
        {
            var actor = wideMonster with { CandidateLevel = 255, SpawnType = type };
            byte[] encoded = DungeonPackets.StartMap(approvedEntry,Single(actor),uint.MaxValue);
            byte[] expectedRow = [0,0,255,255,255,127,220,254,120,86,52,18,255,type,0,0,255,0,0,0,0,0];
            That(encoded.Length == 45 && encoded[18] == 1 && encoded.AsSpan(19,22).SequenceEqual(expectedRow) &&
                encoded.AsSpan(41).SequenceEqual(new byte[] { 0,0,0,255 }),
                "已确认怪物0–3/APC5–8按独立固定22B行编码，完整保留索引、key、模板、等级和有符号组 " + type);
        }
        foreach (byte type in new byte[] { 4,9,255 })
            Reject<NotSupportedException>(() => DungeonPackets.StartMap(approvedEntry, Single(firstMonster with { SpawnType = type }), 1),
                "未核验怪物类型不能由Builder猜造 " + type);
        Reject<OverflowException>(() => DungeonPackets.StartMap(approvedEntry, Single(firstMonster with { SourceOrdinal = -1 }), 1),
            "负SourceOrdinal拒绝而不转为FFFFFFFF");
        Reject<OverflowException>(() => DungeonPackets.StartMap(approvedEntry, Single(firstMonster with { MonsterId = -1 }), 1),
            "负模板ID拒绝而不截为u32");
        Reject<InvalidDataException>(() => DungeonPackets.StartMap(approvedEntry, firstMonsters with
            { Room = firstMonsters.Room with { Map = firstMonsters.Room.Map with { Id = 76122 } } }, 1),
            "怪物计划与已选择地图不同不能混合构包");
        Reject<InvalidDataException>(() => DungeonPackets.StartMap(approvedEntry, firstMonsters with
            { Monsters = Array.AsReadOnly(Enumerable.Repeat(firstMonster, 256).ToArray()) }, 1),
            "超过u8数量容量拒绝整包，不截名单");
        byte[] empty = DungeonPackets.StartMap(approvedEntry, firstMonsters with { Monsters = Array.Empty<DungeonPlannedMonster>() }, 1);
        That(empty.SequenceEqual(DungeonPackets.StartMap(approvedEntry, 1)), "真正空怪物计划保持完整23B空名单布局");
        var ordinaryMaze = dungeons.Get(3).Mazes[0];
        var ordinaryMap = ordinaryMaze.MapSpecification!.Single(map => map.Id == 58550);
        var ordinaryMonsters = DungeonMonsterPlan.Create(rooms, ordinaryMap, character.Id, 256, 5);
        var ordinaryEntry = approvedEntry with { QuestId = 0, ActiveQuestSlot = byte.MaxValue, Maze = ordinaryMaze, StartMap = ordinaryMap,
            BossMap = ordinaryMaze.MapSpecification!.Single(map => map.Kind == "boss"), QuestAcceptanceId = Guid.Empty, MonsterBasisLevel = 5 };
        byte[] specialBody = DungeonPackets.StartMap(ordinaryEntry, ordinaryMonsters, 1);
        That(specialBody[18] == 8 && specialBody.Length == 23 + 8 * 22, "58550五只静态怪物、两个机关子怪与一个父对象完整22B行");
        int firstChild = 19 + 5 * 22;
        That(specialBody[firstChild + 14] == 1 && specialBody[firstChild + 15] == 1 && specialBody[firstChild + 16] == 0 &&
            specialBody[firstChild + 21] == 1 && BinaryPrimitives.ReadUInt32LittleEndian(specialBody.AsSpan(firstChild + 2)) == 0,
            "机关子怪显式父关联和非阻塞位，原子行索引保持独立于对象key");
        int passive = 19 + 7 * 22;
        That(BinaryPrimitives.ReadUInt32LittleEndian(specialBody.AsSpan(passive + 8)) == 109006903 && specialBody[passive + 12] == 0 &&
            specialBody[passive + 13] == 9 && specialBody[passive + 16] == 255 && specialBody[passive + 21] == 1,
            "机关父对象保留真实模板，类型9独立于普通怪物等级和死亡资格");
        foreach (var operation in new[] { DungeonMapOperation.Current, DungeonMapOperation.RestoreBase })
        {
            byte[] cached = DungeonPackets.StartMap(ordinaryEntry, ordinaryMonsters, 0xF1234567,
                operation, DungeonMapPayload.Cached);
            That(cached.Length == 16 && cached[0] == ordinaryMap.Position.X && cached[1] == ordinaryMap.Position.Y &&
                cached[2] == (byte)operation && BinaryPrimitives.ReadUInt32LittleEndian(cached.AsSpan(3)) == 0xF1234567 &&
                cached[13] == 0 && cached[14] == 0 && cached[15] == byte.MaxValue,
                "回访缓存体只保留格、操作和公共尾部，不重放地图 ID、机关父子或物品列表");
        }
        Console.WriteLine($"PASS {Count - checks} dungeon monster packet assertions");

        void Reject<T>(Action action, string name) where T : Exception
        {
            bool rejected = false;
            try
            {
                action();
            }
            catch (T)
            {
                rejected = true;
            }
            That(rejected, name);
        }
    }
}
