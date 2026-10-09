using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Quests;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Quests;
using DfoServer.GameWorld.Towns;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using System.Buffers.Binary;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class QuestReplayTests
{
    public static void RunCore()
    {
        int checks = Count;
        byte[] request = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(request,400);
        That(QuestReplayRequests.TryParse(request,out uint requested) && requested == 400 &&
            !QuestReplayRequests.TryParse(request.AsSpan(0,3),out _) && !QuestReplayRequests.TryParse([.. request,0],out _),
            "1459按完整单u32生产者读取，不猜路线或接受未知尾部");
        That(QuestReplayPackets.Accepted() is [1,0,0,0,0] && QuestReplayPackets.Rejected() is [0,19,0],
            "1459成功保留必读且丢弃的u32尾，失败仅使用普通前缀");
        var quest = QuestEligibilityTests.Quest(400) with { Condition = "[quest clear]",SubType = 2,IntData = [401] };
        var quests = new QuestCatalog(new[] { quest,QuestEligibilityTests.Quest(401) }.ToDictionary(row => row.Id));
        var connections = Connections((400,1100),(401,0),(402,-1));
        var active = new ActiveMainQuest(0,401,1) { AcceptanceId = Guid.NewGuid() };
        var character = new Character(1,0,"Replay",12,new Location(38,2,450,240),null)
        { Level = 20,CompletedQuests = [400],ActiveMainQuests = [active] };
        var start = new DungeonMap("map",new(0,0),2001,"map/replay-start.map");
        var boss = new DungeonMap("boss",new(1,0),2002,"map/replay-boss.map");
        var maze = new DungeonMaze(3,2,1,"BB EE",[start.Position],[boss.Position],[start,boss],
            [new(0,0),new(0,400),new(0,-1)]);
        var definition = new DungeonDefinition(1100,"dungeon/replay.dgn",1,20,[maze],[]);
        That(connections.TryGetReplayDungeon(400,out uint route) && route == 1100 &&
            !connections.TryGetReplayDungeon(401,out _) && !connections.TryGetReplayDungeon(402,out _),
            "回放路线使用sequence正参数，不用零/负值或任务目标猜副本");
        for (byte job = 0; job < 16; job++)
        {
            var source = character with { Job = job };
            var replay = QuestReplayPlan.Prepare(source,400,quests,connections);
            That(replay is not null && ReferenceEquals(replay.Expected,source) && replay.DungeonId == 1100,
                "已完成回放资格适用当前角色全部基础职业 " + job);
            That(DungeonEntryPlan.TryCreate(definition,source,1100,400,0,out var entry,out _,quest: quest,isReplay: true) &&
                entry is { IsQuestReplay: true,QuestId: 0,RouteQuestId: 400,ActiveQuestSlot: byte.MaxValue,QuestAcceptanceId: var acceptance } &&
                acceptance == Guid.Empty && entry.QuestAcceptances.SequenceEqual([active]) && entry.Maze == maze &&
                source.CompletedQuests.SequenceEqual([400]) && source.ActiveMainQuests.SequenceEqual([active]) && source.Revision == 12,
                "回放选择原剧情迷宫，保留实际其他接取且不创建回放接取/完成/奖励 " + job);
        }
        That(QuestReplayPlan.Prepare(character with { CompletedQuests = [],ActiveMainQuests = [new(0,400,0)] },400,quests,connections) is null,
            "仅条件已达成或活动任务不能冒充已完成回放资格");
        That(QuestReplayPlan.Prepare(character with { CompletedQuests = [],AccountCompletedQuests = [400] },400,quests,connections) is not null,
            "已保存账号完成位与正常客户端完成投影保持一致");
        That(QuestReplayPlan.Prepare(character,0,quests,connections) is null && QuestReplayPlan.Prepare(character,30000,quests,connections) is null &&
            QuestReplayPlan.Prepare(character,400,quests,Connections((400,0))) is null &&
            QuestReplayPlan.Prepare(character,400,quests,Connections((400,1100),(400,1101))) is null,
            "未知编号、无显式路线及冲突路线不签发回放入口");
        That(!DungeonEntryPlan.TryCreate(definition with { Mazes = [maze with { QuestConnectionTokens = [new(0,1),new(0,400),new(0,-1)] }] },
            character,1100,400,0,out _,out _,quest: quest,isReplay: true),
            "回放原剧情路线不误选已完成connection或普通地图");
        That(!DungeonEntryPlan.TryCreate(definition with { Mazes = [maze,maze] },character,1100,400,0,out _,out _,quest: quest,isReplay: true) &&
            !DungeonEntryPlan.TryCreate(definition,character with { CompletedQuests = [] },1100,400,0,out _,out _,quest: quest,isReplay: true),
            "迷宫歧义和失去真实完成事实时拒绝回放，不借用普通接取绕过");
        Console.WriteLine($"PASS {Count-checks} 合成任务回放资格、原剧情路线及状态隔离断言");
    }

    internal static QuestConnectionCatalog Connections(params (int Quest,int Dungeon)[] steps)
    {
        string[] strings = ["[connect quest unit]","[type]","episode quest","[parent key]","[name]","fixture","[sequence]","[/sequence]","[/connect quest unit]"];
        var tokens = new List<PvfToken> { new(3,0),new(3,1),new(6,2),new(3,3),new(0,1),new(3,4),new(6,5),new(3,6) };
        foreach (var (quest,dungeon) in steps) { tokens.Add(new(0,quest)); tokens.Add(new(0,dungeon)); }
        tokens.Add(new(3,7)); tokens.Add(new(3,8));
        return QuestConnectionCatalog.Parse(tokens,index => strings[index]);
    }
}
