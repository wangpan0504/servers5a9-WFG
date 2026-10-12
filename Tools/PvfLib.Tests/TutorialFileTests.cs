using static PvfLib.Tests.TestCheck;

namespace PvfLib.Tests;

internal static class TutorialFileTests
{
    public static void Run()
    {
        var strings = new List<string>();
        PvfToken I(int value) => new(0, value);
        PvfToken S(string value, byte type = 6) { strings.Add(value); return new(type, strings.Count - 1); }
        PvfToken T(string value) => S("[" + value + "]", 3);
        string Resolve(int index) => strings[index];
        PvfToken[] flow = [T("tutorial flow"), S("[swordman]"), T("dungeon index"), I(7115),
            T("intro"), T("after tutorial"), I(0), T("intro path"), S("Video/Movie/tutorial.avi"), T("/intro"),
            T("go town"), T("town"), I(38), I(0), T("startPos"), I(1677), I(222), T("/go town"), T("/tutorial flow")];
        That(TutorialFile.Flows(flow, Resolve) is [{ Job: "[swordman]", DungeonId: 7115, AfterTutorial: 0, Town: 38, X: 1677, Y: 222 }],
            "训练流程完整解析嵌套影片与回城，保持CHR职业标签");
        Reject<InvalidDataException>(() => TutorialFile.Flows([.. flow, .. flow], Resolve));
        Reject<InvalidDataException>(() => TutorialFile.Flows(flow[..^1], Resolve));
        var malformed = flow.ToArray(); malformed[3] = S("7115");
        Reject<InvalidDataException>(() => TutorialFile.Flows(malformed, Resolve));
        PvfToken[] reward = [T("escalade tutorial reward"), I(38), I(8474), I(1), T("/escalade tutorial reward")];
        That(TutorialFile.Rewards(reward, Resolve) is [{ Progress: 38, ItemId: 8474, Count: 1 }], "训练奖励保持检查点、物品、数量三元组");
        Reject<InvalidDataException>(() => TutorialFile.Rewards([reward[0], reward[1], reward[2], reward[4]], Resolve));
        Reject<InvalidDataException>(() => TutorialFile.Rewards([.. reward, .. reward], Resolve));

        PvfToken[] special = [I(125188), I(968), I(400), I(0), I(2), S("[trap]"), I(1), I(2), I(3), I(4), I(5),
            S("[hellparty]"), I(209), I(100), I(1), I(210), I(50), I(2), S("[/hellparty]"), I(100), I(20), I(30), I(0), I(0)];
        var objects = MapSpecialObjects.Read(special, Resolve);
        That(objects.Length == 2 && objects[0].Children[0].Values.Length == 5 && objects[0].Children[1].Values.SequenceEqual([209, 100, 1, 210, 50, 2]),
            "普通子动作读五整数，hellparty按任意组三元组到闭合，不能吞下一个物件");
        Reject<InvalidDataException>(() => MapSpecialObjects.Read(special[..17], Resolve));
        Reject<InvalidDataException>(() => MapSpecialObjects.Read([I(1), I(2), I(3), I(4), I(1), S("[hellparty]"), I(209), I(100), S("[/hellparty]")], Resolve));
        Reject<InvalidDataException>(() => MapSpecialObjects.Read([I(1), I(2), I(3), I(4), I(1), I(0), I(1)], Resolve));
        That(TutorialSceneFile.BasicAction([T("basic action"), S("Action/53127.act")], Resolve) == "Action/53127.act", "地图动作路径不混入其他字段");
        Reject<InvalidDataException>(() => TutorialSceneFile.BasicAction([T("basic action"), I(1)], Resolve));
        PvfToken[] action = [T("TRIGGER"), T("WHICH"), T("MONSTER"), T("IS INDEX"), I(70216),
            T("DELAY DO BEHAVIOR"), T("ME"), I(0), I(1000), T("/TRIGGER"), T("BEHAVIOR"), T("DESTROY"), T("/BEHAVIOR"),
            T("BEHAVIOR"), T("CINEMATIC"), I(6805), T("/BEHAVIOR")];
        var instructions = TutorialSceneFile.Action(action, Resolve);
        That(instructions.Cinematics.SequenceEqual([6805]) && instructions.DestroySelectedMonsters.SequenceEqual([70216]),
            "只有选择器连到DESTROY的原模板成为非阻挡演员，延迟不是行为编号");
        PvfToken[] movie = [T("MAP"), I(70578), T("SCENE"), T("BEHAVIOR"), T("ACTOR"), T("TYPE"), T("GROUP"),
            T("ACTOR"), T("TYPE"), T("MONSTER"), T("INDEX"), I(0), T("/ACTOR"),
            T("ACTOR"), T("TYPE"), T("MONSTER"), T("INDEX"), I(2), T("/ACTOR"), T("/ACTOR"),
            T("ACTOR"), T("TYPE"), T("CAMERA"), T("MONSTER"), T("INDEX"), I(3), T("/ACTOR"), T("DESTROY"), T("/BEHAVIOR"), T("/SCENE")];
        var cinematic = TutorialSceneFile.Cinematic(movie, Resolve);
        That(cinematic.MapId == 70578 && cinematic.MonsterActors.SequenceEqual([0, 2]) && cinematic.DestroyTargets.SequenceEqual([0, 2]),
            "GROUP组合演员成为DESTROY主体，其他camera字段不因后续MONSTER标签误判");
        Reject<InvalidDataException>(() => TutorialSceneFile.Cinematic(movie[..^3], Resolve));
        Reject<InvalidDataException>(() => TutorialSceneFile.Cinematic([T("SCENE"), T("BEHAVIOR"), T("ACTOR"), T("TYPE"), I(1)], Resolve));
    }
}
