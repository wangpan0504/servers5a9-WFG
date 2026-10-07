using System.Buffers.Binary;
using System.Collections;
using DfoServer.GameWorld.Dungeons;
using PvfLib;

namespace DfoServer;

internal static class DungeonResourceSurveyTests
{
    public static void Run(PvfArchive archive)
    {
        int checks = 0;
        void Check(bool result, string name)
        {
            checks++;
            if (!result)
                throw new InvalidOperationException("FAIL dungeon survey: " + name);
        }

        byte[] raw = new byte[20];
        Write(0, 3, 1);
        Write(5, 7, 123);
        Write(10, 3, 1);
        Write(15, 3, 2);
        var file = new PvfFile("map/test.map", 1, raw.Length);
        string Resolve(int id) => id switch
        {
            1 => "[monster]",
            2 => "[special mechanism]",
            _ => throw new InvalidDataException("unknown string")
        };
        var parsed = DungeonResourceSurvey.InspectFile(file, () => raw, Resolve);
        Check(parsed.Error is null && parsed.Size == raw.Length, "complete source parsed");
        Check(parsed.Tags.SequenceEqual(["[monster]", "[special mechanism]"]), "unknown and repeated tags inventoried without invented semantics");
        Check(parsed.TokenTypes.SequenceEqual(new byte[] { 3, 7 }), "opaque reference type preserved");
        Check(parsed.Tags is IList tags && tags.IsReadOnly, "published tags immutable");
        Check(DungeonResourceSurvey.InspectFile(file, () => raw[..^1], Resolve).Error is not null, "short archive data rejected");
        Check(DungeonResourceSurvey.InspectFile(file with { Size = 19 }, () => raw[..^1], Resolve).Error is not null, "incomplete token rejected");
        var broken = DungeonResourceSurvey.InspectFile(file, () => raw, _ => throw new InvalidDataException("missing string"));
        Check(broken.Error is not null && broken.Tags.Count == 0 && broken.TokenTypes.Count == 0, "failed script does not publish partial inventory");
        Check(DungeonResourceSurvey.InspectFile(file with { DataType = 3, Size = 3 }, () => new byte[] { 1, 2, 3 }, Resolve).Error is null, "non-token file accepted without token decoding");
        var survey = DungeonResourceSurvey.Scan(archive);
        Check(survey.Files.Count == 1_044_002 && survey.Tags.Count == 8684, "complete matched file and distinct root/tag counts are retained");
        Check(survey.Files.Zip(survey.Files.Skip(1)).All(pair => StringComparer.Ordinal.Compare(pair.First.Path, pair.Second.Path) < 0),
            "grouped physical reads preserve ordinal output order");
        Check(survey.Files.Sum(file => (long)file.Tags.Count) == survey.Tags.Sum(tag => (long)tag.FileCount),
            "streamed tag counters include every per-file distinct tag exactly once");
        Check(survey.Dungeons.Count == 1372, "every indexed dungeon receives a result");
        Check(survey.Files.Count == archive.Files.Count(file => file.Path.StartsWith("dungeon/", StringComparison.Ordinal)
            || file.Path.StartsWith("map/", StringComparison.Ordinal) || file.Path.StartsWith("monster/", StringComparison.Ordinal)
            || file.Path.StartsWith("passiveobject/", StringComparison.Ordinal)), "all domain files included, not only indexed scripts");
        Check(survey.Files.Select(file => file.Path).Distinct(StringComparer.Ordinal).Count() == survey.Files.Count, "no duplicate file results");
        Check(survey.Dungeons.Any(dungeon => dungeon.Error is not null) && survey.Dungeons.Single(dungeon => dungeon.Id == 3).MazeCount == 5,
            "unsupported definitions remain visible and do not stop ordinary definition scan");
        var greed = survey.Dungeons.Single(dungeon => dungeon.Id == 3).GreedGrids;
        Check(greed.Count == 5 && greed[1].Format == "ascii-pairs" && greed[1].CellCount == 10 && greed[1].UnsupportedReason is null &&
            greed is IList grids && grids.IsReadOnly, "全量扫描提供真实十格Greed摘要且不可变");
        Check(survey.Dungeons.SelectMany(dungeon => dungeon.GreedGrids).All(grid => grid.Format == "ascii-pairs"
            ? grid.CellCount > 0 && grid.UnsupportedReason is null : grid.CellCount is null && grid.UnsupportedReason is not null),
            "全量扫描中的未知Greed单独记录原因，不与副本结构错误混淆");
        Check(survey.Dungeons.Any(dungeon => dungeon.Error is null && dungeon.GreedGrids.Any(grid => grid.Format == "unsupported")),
            "匹配PVF实际存在Greed Unsupported，摘要保留而不拒绝完整定义");
        Check(survey.Maps.Count == DungeonCatalog.ParseDirectory(archive.ReadTokens("map/map.lst"), archive.ResolveString, "map").Count,
            "every indexed map receives a result");
        Check(survey.Maps.Single(map => map.Id == 76126).Error is null, "real starter boss room included");
        Check(survey.Tags.All(tag => tag.FileCount > 0) && survey.Evidence.Contains("不证明", StringComparison.Ordinal), "resource counts do not declare playability");
        Console.WriteLine($"PASS {checks} dungeon resource survey assertions");
        Console.WriteLine($"Greed survey: definitions={survey.Dungeons.Count(dungeon => dungeon.Error is null)}/{survey.Dungeons.Count}; " +
            $"decoded={survey.Dungeons.SelectMany(dungeon => dungeon.GreedGrids).Count(grid => grid.Format == "ascii-pairs")}; " +
            $"unsupported={survey.Dungeons.SelectMany(dungeon => dungeon.GreedGrids).Count(grid => grid.Format == "unsupported")}");
        foreach (var definition in survey.Dungeons.Where(dungeon => dungeon.GreedGrids.Any(grid => grid.Format == "unsupported")))
        {
            foreach (var grid in definition.GreedGrids.Where(grid => grid.Format == "unsupported"))
                Console.WriteLine($"Greed Unsupported: dungeon={definition.Id}, maze={grid.SourceOrdinal}, reason={grid.UnsupportedReason}");
        }

        void Write(int offset, byte type, int value)
        {
            raw[offset] = type;
            BinaryPrimitives.WriteInt32LittleEndian(raw.AsSpan(offset + 1, 4), value);
        }
    }
}
