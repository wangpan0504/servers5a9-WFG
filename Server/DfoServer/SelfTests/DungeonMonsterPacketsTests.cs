using DfoServer.Development.Dungeons;
using System.Buffers.Binary;
using DfoServer.Development;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Towns;
using PvfLib;

namespace DfoServer;

internal static class DungeonMonsterPacketsTests
{
    public static void Run(PvfArchive pvf)
    {
        int checks = 0;
        void Check(bool ok, string name)
        {
            checks++;
            if (!ok) throw new Exception("FAIL: " + name);
        }
        var dungeons = DungeonCatalog.Load(pvf);
        var rooms = DungeonRoomCatalog.Load(pvf);
        var character = new Character(0x100, 0, "Monster", 7, new Location(38, 2, 100, 249, 0), null)
        {
            ActiveMainQuests = [new(0, 3145, 1)]
        };
        Check(DungeonEntryPlan.TryCreate(dungeons, character, 3, 3145, 0, out var entry, out _), "monster fixture uses the actual task maze");
        foreach (var map in entry!.Maze.MapSpecification!.Where(map => map.Id is 76121 or 76122))
        {
            var plan = entry with { StartMap = map };
            var monsters = DungeonMonsterPlan.Create(rooms, map, character.Id, 0x100);
            byte[] body = DungeonEntryProbe.BuildStartMap(plan, monsters, 0xF1234567);
            int expectedCount = map.Id == 76121 ? 4 : 5;
            Check(body.Length == 23 + expectedCount * 22 && body[18] == expectedCount, "Client nonempty rows use 22 bytes without losing the signed field");
            Check(body[0] == map.Position.X && body[1] == map.Position.Y
                && BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(14)) == map.Id, "each room retains its own map and grid position");
            using var reader = new BinaryReader(new MemoryStream(body, 19, body.Length - 19));
            var keys = new HashSet<ushort>();
            for (int index = 0; index < expectedCount; index++)
            {
                Check(reader.ReadUInt16() == 0 && reader.ReadUInt32() == index, "template ordinal and independent prefix retain source order");
                ushort key = reader.ReadUInt16();
                Check(key != character.Id && keys.Add(key), "monster keys do not alias the character or another row");
                Check(reader.ReadUInt32() == monsters.Monsters[index].MonsterId && reader.ReadByte() == 3,
                    "monster code and candidate level come from this room's resource");
                Check(reader.ReadByte() == 0 && reader.ReadByte() == 0 && reader.ReadByte() == 0
                    && reader.ReadSByte() == -1 && reader.ReadUInt32() == 0 && reader.ReadByte() == 0,
                    "candidate flags include the mandatory Client signed byte before the u32");
            }
            Check(reader.ReadBytes(4).SequenceEqual(new byte[] { 0, 0, 0, 255 }) && reader.BaseStream.Position == reader.BaseStream.Length,
                "extra list, fog, ridable list and member tail remain aligned after the last actor");
        }
        Console.WriteLine($"PASS {checks} dungeon monster packet assertions");
    }
}
