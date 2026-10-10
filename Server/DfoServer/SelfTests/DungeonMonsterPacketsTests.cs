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
    public static void Run(PvfArchive pvf)
    {
        int checks = Count;
        var dungeons = DungeonCatalog.Load(pvf);
        var rooms = DungeonRoomCatalog.Load(pvf);
        var character = new Character(0x100, 0, "Monster", 7, new Location(38, 2, 100, 249, 0), null)
        {
            ActiveMainQuests = [new(0, 3145, 1)]
        };
        That(DungeonEntryPlan.TryCreate(dungeons, character, 3, 3145, 0, out var entry, out _), "monster fixture uses the actual task maze");
        var approvedEntry = entry ?? throw new InvalidDataException("3145怪物构包缺少准入计划");
        foreach (var map in approvedEntry.Maze.MapSpecification!)
        {
            var plan = approvedEntry with { StartMap = map };
            var monsters = DungeonMonsterPlan.Create(rooms, map, character.Id, 0x100, approvedEntry.BasisLevel);
            byte[] body = DungeonPackets.StartMap(plan, monsters, 0xF1234567);
            That(DungeonEntryProbe.BuildStartMap(plan, monsters, 0xF1234567).SequenceEqual(body),
                "诊断调用委托同一正式纯Builder，保持完整怪物字节");
            int expectedCount = map.Id switch { 76121 => 4, 76122 => 5, 76123 => 7, 76124 => 5, 76125 => 6, 76126 => 2,
                _ => throw new InvalidDataException("3145出现未核验房间") };
            That(body.Length == 23 + expectedCount * 22 && body[18] == expectedCount, "Client nonempty rows use 22 bytes without losing the signed field");
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
                    && reader.ReadSByte() == -1 && reader.ReadUInt32() == 0 && reader.ReadByte() == 0,
                    "candidate flags include the mandatory Client signed byte before the u32");
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
