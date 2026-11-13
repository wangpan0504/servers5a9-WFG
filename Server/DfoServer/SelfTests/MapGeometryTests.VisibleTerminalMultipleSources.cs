using System.Text.Json.Nodes;
using DfoServer.GameWorld.Maps;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class MapGeometryTests
{
    private static void RunVisibleTerminalMultipleSourcesCore(string single)
    {
        int checks = Count;
        var document = JsonNode.Parse(single)!;
        document["aliases"]!["map/clone/displaycompat/ext2.til"] = "map/clone/c.til";
        var rules = document["rules"]!.AsArray();
        rules[1]!["from"]!["all"] = JsonNode.Parse("[[0,0],[6,\"a.til\"],[0,1],[6,\"b.til\"],[0,5],[6,\"c.til\"]]");
        rules[1]!["to"]!["all"] = JsonNode.Parse("[[0,0],[6,\"a.til\"],[0,1],[6,\"b.til\"],[0,5],[6,\"c.til\"],[0,2],[6,\"displaycompat/ext.til\"],[0,7],[6,\"displaycompat/ext2.til\"]]");
        rules[2]!["from"]!["all"] = GeometryAtoms([0,0,1, 0,0,5, 0,0,1]);
        rules[2]!["to"]!["all"] = GeometryAtoms([0,0,2, 0,0,7, 0,0,2]);
        var guard = rules[4]!.DeepClone();
        guard["id"] = "ext2-pass";
        guard["map"] = "map/clone/displaycompat/ext2.til";
        rules.Add(guard);
        var config = MapGeometryConfig.Parse(document.ToJsonString());
        var pool = new List<string>();
        PvfToken Text(byte kind, string value)
        {
            int id = pool.IndexOf(value);
            if (id < 0) { id = pool.Count; pool.Add(value); }
            return new(kind, id);
        }
        PvfToken[] Record(string tag, params PvfToken[] values) => [Text(3, tag), ..values, Text(3, "[/" + tag[1..])];
        PvfToken[] input = [..Record("[tile]", Text(6,"a.til"), Text(6,"a.til"), Text(6,"b.til")),
            Text(3,"[extended tile]"), ..Record("[tile files]", new(0,0), Text(6,"a.til"), new(0,1), Text(6,"b.til"), new(0,5), Text(6,"c.til")),
            ..Record("[tile map]", new(0,0), new(0,0), new(0,1), new(0,0), new(0,0), new(0,5), new(0,0), new(0,0), new(0,1)), Text(3,"[/extended tile]"),
            ..Record("[pathgate pos]", new PvfToken(0,77)), ..Record("[force map width]", new PvfToken(0,1120)), ..Record("[monster]", new PvfToken(0,101))];
        var expected = input.ToList();
        expected[3] = Text(6,"displaycompat/base.til");
        expected[17] = new(0,2); expected[20] = new(0,7); expected[23] = new(0,2);
        expected.InsertRange(13, [new(0,2), Text(6,"displaycompat/ext.til"), new(0,7), Text(6,"displaycompat/ext2.til")]);
        var frozen = input.ToArray();
        var output = config.Apply("map/clone/room.map", input, id => pool[id]);
        string Meaning(PvfToken token) => token.Type is 3 or 6
            ? token.Type + ":" + config.ResolveString(token.Value, id => pool[id]) : token.Type + ":" + token.Value;
        That(input.SequenceEqual(frozen) && output.Select(Meaning).SequenceEqual(expected.Select(Meaning)),
            "多来源末列分别克隆，保留非连续旧ID、重复同源行、非末格及门和actor全部token");
        That(config.Apply("map/clone/room.map", output, id => pool[id]).SequenceEqual(output), "多来源末列目标幂等");
        PvfToken[] tile = [Text(3,"[image]"), Text(6,"source.img"), new(0,0), Text(3,"[pass type]"), ..Enumerable.Repeat(new PvfToken(0,0),14)];
        foreach (string alias in new[] { "base", "ext", "ext2" })
            That(config.Apply("map/clone/displaycompat/" + alias + ".til", tile, id => pool[id]).SequenceEqual(tile), "多来源alias保留完整源图与通行格：" + alias);
        foreach (string name in new[] { "source", "swap-sources", "swap-cells", "unused", "duplicate-source", "split-source", "nonterminal",
            "old-definition", "old-id", "duplicate-new-id", "base-prefix", "columns", "rows", "empty-tail", "missing-guard", "base-pass", "ext-pass", "zero-added", "wrong-old-terminal-id", "missing-alias" })
        {
            var bad = document.DeepClone(); var rows = bad["rules"]!.AsArray();
            switch (name)
            {
                case "source": bad["aliases"]!["map/clone/displaycompat/ext2.til"] = "map/clone/a.til"; break;
                case "swap-sources": bad["aliases"]!["map/clone/displaycompat/ext.til"] = "map/clone/c.til"; bad["aliases"]!["map/clone/displaycompat/ext2.til"] = "map/clone/b.til"; break;
                case "swap-cells": rows[2]!["to"]!["all"]![2]![1] = 7; rows[2]!["to"]!["all"]![5]![1] = 2; break;
                case "unused":
                    bad["aliases"]!["map/clone/displaycompat/ext3.til"] = "map/clone/d.til";
                    var extraGuard = rows[5]!.DeepClone(); extraGuard["id"] = "ext3-pass"; extraGuard["map"] = "map/clone/displaycompat/ext3.til"; rows.Add(extraGuard);
                    rows[1]!["to"]!["all"]!.AsArray().Add(new JsonArray(0,8)); rows[1]!["to"]!["all"]!.AsArray().Add(new JsonArray(6,"displaycompat/ext3.til")); break;
                case "duplicate-source": bad["aliases"]!["map/clone/displaycompat/ext2.til"] = "map/clone/b.til"; break;
                case "split-source":
                    rows[1]!["to"]!["all"]!.AsArray().Add(new JsonArray(0,8)); rows[1]!["to"]!["all"]!.AsArray().Add(new JsonArray(6,"displaycompat/ext.til"));
                    rows[2]!["to"]!["all"]![8]![1] = 8; break;
                case "nonterminal": rows[2]!["to"]!["all"]![0]![1] = 7; break;
                case "old-definition": rows[1]!["to"]!["all"]![1]![1] = "b.til"; break;
                case "old-id": rows[1]!["to"]!["all"]![8]![1] = 5; break;
                case "duplicate-new-id": rows[1]!["to"]!["all"]![8]![1] = 2; break;
                case "base-prefix": rows[0]!["to"]!["all"]![0]![1] = "b.til"; break;
                case "columns": rows[0]!["to"]!["all"]!.AsArray().RemoveAt(0); break;
                case "rows": rows[2]!["to"]!["all"]!.AsArray().RemoveAt(8); rows[2]!["to"]!["all"]!.AsArray().RemoveAt(7); rows[2]!["to"]!["all"]!.AsArray().RemoveAt(6); break;
                case "empty-tail": rows[2]!["from"]!["all"]![2]![1] = -1; break;
                case "missing-guard": rows[5]!["enabled"] = false; break;
                case "base-pass": rows[3]!["to"]!["all"]![0]![1] = 1; break;
                case "ext-pass": rows[5]!["to"]!["all"]![0]![1] = 1; break;
                case "zero-added": rows[1]!["to"]!["all"] = rows[1]!["from"]!["all"]!.DeepClone(); rows[2]!["to"]!["all"] = rows[2]!["from"]!["all"]!.DeepClone(); break;
                case "wrong-old-terminal-id": rows[2]!["to"]!["all"]![2]![1] = 0; break;
                case "missing-alias": bad["aliases"]!.AsObject().Remove("map/clone/displaycompat/ext2.til"); break;
            }
            Reject<InvalidDataException>(() => MapGeometryConfig.Parse(bad.ToJsonString()), "多来源末列拒绝非法配方：" + name);
        }
        tile[^1] = new(0,1);
        Reject<InvalidDataException>(() => config.Apply("map/clone/displaycompat/ext2.til", tile, id => pool[id]), "多来源alias读取时核对真实通行格");
        Console.WriteLine($"PASS visible terminal multiple sources {Count - checks}");
        RunVisibleTerminalPreservationCore(single);
    }

    private static void RunVisibleTerminalPreservationCore(string single)
    {
        var document = JsonNode.Parse(single)!;
        var rows = document["rules"]!.AsArray();
        rows[2]!["from"]!["all"] = GeometryAtoms([0,0,1, 0,0,1, 0,0,-1]);
        rows[2]!["to"]!["all"] = GeometryAtoms([0,0,2, 0,0,1, 0,0,-1]);
        var config = MapGeometryConfig.Parse(document.ToJsonString());
        string[] pool = ["[tile]", "a.til", "b.til", "[/tile]", "[extended tile]", "[tile files]", "[/tile files]", "[tile map]", "[/tile map]", "[/extended tile]", "[pathgate pos]", "displaycompat/base.til", "displaycompat/ext.til"];
        PvfToken[] input = [new(3,0), new(6,1), new(6,1), new(6,2), new(3,3), new(3,4), new(3,5), new(0,0), new(6,1), new(0,1), new(6,2), new(3,6), new(3,7),
            new(0,0), new(0,0), new(0,1), new(0,0), new(0,0), new(0,1), new(0,0), new(0,0), new(0,-1), new(3,8), new(3,9), new(3,10), new(0,77)];
        var frozen = input.ToArray();
        var expected = input.ToList();
        expected[3] = new(6,11); expected[15] = new(0,2);
        expected.InsertRange(11, [new(0,2), new(6,12)]);
        var output = config.Apply("map/clone/room.map", input, id => pool[id]);
        string Meaning(PvfToken token) => token.Type is 3 or 6
            ? token.Type + ":" + config.ResolveString(token.Value, id => pool[id]) : token.Type + ":" + token.Value;
        That(input.SequenceEqual(frozen) && output.Select(Meaning).SequenceEqual(expected.Select(Meaning)), "末列选定行克隆，同源未选行和空格原样保留，门与输入不变");
        That(config.Apply("map/clone/room.map", output, id => pool[id]).SequenceEqual(output), "选择性末列克隆幂等");
        foreach (string name in new[] { "unknown-preserved", "old-switch", "empty-fill", "unused" })
        {
            var bad = document.DeepClone(); var matrix = bad["rules"]![2]!;
            switch (name)
            {
                case "unknown-preserved": matrix["from"]!["all"]![5]![1] = 9; matrix["to"]!["all"]![5]![1] = 9; break;
                case "old-switch": matrix["to"]!["all"]![5]![1] = 0; break;
                case "empty-fill": matrix["to"]!["all"]![8]![1] = 2; break;
                case "unused": matrix["to"]!["all"]![2]![1] = 1; break;
            }
            Reject<InvalidDataException>(() => MapGeometryConfig.Parse(bad.ToJsonString()), "选择性末列克隆拒绝非法配方：" + name);
        }
    }
}
