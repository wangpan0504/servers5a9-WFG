using System.Net;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Mailbox;
using DfoServer.Game.Session;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Handlers;
using DfoServer.Network.Projection;
using DfoServer.Network.Protocol;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class DungeonSelectionReturnTests
{
    public static void RunCore()
    {
        int checks = Count;
        string root = Path.Combine(Path.GetTempPath(), "S5A9-selection-core-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var point = new Location(1, 0, 10, 10);
            var map = new TownMap("synthetic.map", [new(new(0, 0, 20, 20), [], [])], [], false, []);
            var towns = new TownCatalog(new() { [(1, 0)] = new(1, 0, 1, [], [], [], point, map, [], []) },
                point, point, new(0, [], []), [], new Dictionary<uint, uint[]>());
            var repository = new CharacterRepository(Path.Combine(root, "game.db"));
            var owners = new AccountOwners();
            using var owner = owners.Acquire("selection", () => { });
            repository.Create(owner, new("SelectReturn", point, point));
            var session = new GameSession(repository, towns, owner);
            Reject<InvalidDataException>(() => session.BeginDungeonSelection(), "未选角不能开选图");
            session.Select(0);
            Reject<InvalidDataException>(() => session.BeginDungeonSelection(), "入城未完成不能开选图");
            session.MarkReady();
            string persisted = JsonSerializer.Serialize(repository.LoadCharacter(owner, 0));
            var opening = session.BeginDungeonSelection();
            var current = session.CurrentDungeonSelection;
            That(session.Phase == SessionPhase.MapSelect && current?.ReturnPosition == point && session.CanPublishDungeonSelection(opening), "选图阶段拥有返回快照");
            That(session.BeginDungeonSelection().SelectionId == opening.SelectionId && ReferenceEquals(current, session.CurrentDungeonSelection), "重复入口不替换选择身份和返回点");
            That(!session.Move(new(11, 10, 5, 0, 0, 0)) && !session.LeaveRoom() && !session.Travel(new(1, 0, 11, 10, 5, 1, 0, 0)), "选图不提交城镇移动和切图");
            That(!session.MutateInventory(_ => throw new Exception("不应规划背包")).Accepted &&
                !session.PlanAndCommitInventory(_ => throw new Exception("不应规划背包")).Accepted, "选图阶段保留背包资格门禁");
            That(!session.LearnSkills(new SkillLearningCommand(0, 0, []), (_, _) => throw new Exception("不应读技能资源")).Success,
                "选图期间不提交技能学习");
            var mail = new MailboxReceipt(session.Selected!, owner.Generation, session.SelectionEpoch);
            That(!session.CanPublishMailbox(mail), "选图不能发布城镇邮件操作结果");
            var returned = session.ReturnFromDungeonSelection()!;
            That(session.Phase == SessionPhase.Town && session.CurrentDungeonSelection is null &&
                !session.CanPublishDungeonSelection(opening) && session.CanPublishDungeonSelection(returned, true), "返回只消费当前选择，恢复城镇发布资格");
            That(session.ReturnFromDungeonSelection() is null && session.CanPublishMailbox(mail), "重复返回无结果，城镇邮件资格恢复");
            That(JsonSerializer.Serialize(repository.LoadCharacter(owner, 0)) == persisted, "选图和返回保留完整持久资产与revision");
            var next = session.BeginDungeonSelection();
            That(next.SelectionId > returned.SelectionId && !session.CanPublishDungeonSelection(returned, true), "同epoch再次选图阻止旧返回发布");
            session.ReturnFromDungeonSelection();
            That(!session.CanPublishDungeonSelection(returned, true), "第二次回城后旧回执仍然失效");
            session.BeginDungeonSelection();
            session.ReturnToRoster();
            session.Select(0);
            session.MarkReady();
            That(session.CurrentDungeonSelection is null && session.ReturnFromDungeonSelection() is null, "返回选角后旧选择不能作用于新角色会话");
            var latest = session.BeginDungeonSelection();
            using var newer = owners.Acquire("selection", () => { });
            Reject<InvalidOperationException>(() => session.ReturnFromDungeonSelection(), "旧generation不能返回");
            Reject<InvalidOperationException>(() => session.CanPublishDungeonSelection(latest), "旧generation不能发布");
            session.End();
            That(session.CurrentDungeonSelection is null && session.Phase == SessionPhase.Unselected, "旧连接收尾只清本会话");
            GateReturnCore(root);
        }
        finally { Directory.Delete(root, recursive: true); }
        Console.WriteLine($"PASS {Count - checks} dungeon selection lifecycle assertions");
    }

    public static async Task RunAsync(TownCatalog towns, CharacterCatalog characters, ItemResources items)
    {
        int checks = Count;
        That(towns.Areas[(38, 2)].Default.DungeonGates.Single().Bounds == new Rectangle(1, 193, 80, 160), "资源解析保留左入口-1/-1触发区");
        That(towns.SelectionReturn(new(38, 2, 90, 249)) == new Location(38, 2, 89, 249), "实际左入口边界外请求回到可走安全点，保持Y");
        That(towns.SelectionReturn(new(38, 4, 1065, 239)) == new Location(38, 4, 1052, 239), "实际右入口使用自身资源，向内返回");
        string root = Path.Combine(Path.GetTempPath(), "S5A9-selection-wire-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            foreach (string scenario in new[] { "normal", "failed-write", "owner", "reselect", "reopen", "gm" })
            {
                var repository = new CharacterRepository(Path.Combine(root, scenario + ".db"));
                var owners = new AccountOwners();
                using var owner = owners.Acquire(scenario, () => { });
                repository.Create(owner, new("ReturnWire", towns.Room, towns.InitialReturn));
                var session = new GameSession(repository, towns, owner);
                session.Select(0);
                session.MarkReady();
                That(session.LeaveRoom() && session.Travel(new(38, 2, 1085, 249, 5, 38, 0, 0)), "协议fixture进入实际选图入口");
                var presence = new TownPresence(towns);
                var projection = new TownProjection(towns, characters, presence, items.EpicPieces);
                using var log = new ServerLog(Path.Combine(root, scenario));
                using var stop = new CancellationTokenSource();
                using var stream = new HeldOutput();
                var sent = new List<(byte Kind, ushort Opcode, byte[] Body)>();
                await using var sender = new GameConnectionSender(owner, stream, stop, 0, (kind, opcode, _, body, _) => sent.Add((kind, opcode, body.ToArray())));
                var context = new GameConnectionContext(new(1, IPAddress.Loopback, stream, stop), owner, session, sender);
                var commands = new GameCommandRegistry();
                new DungeonSelectionHandler(projection, log).Register(commands);
                commands.Seal();
                projection.Join(context);
                var back = new Packet(1, 132, []);
                await commands.DispatchAsync(context, new(1, 15, new byte[4]));
                That(sent.Select(frame => (frame.Kind, frame.Opcode)).SequenceEqual(new (byte, ushort)[] { (1, 15), (0, 3), (0, 27) }) && presence.Count == 0,
                    "ACK15→本人State1→NOTI27同步选图，离开同屏");
                That(sent[1].Body.SequenceEqual(new byte[] { 1, (byte)session.Selected!.Id, (byte)(session.Selected.Id >> 8), 1 }), "开选图发送实际角色State1");
                That(await commands.DispatchAsync(context, back with { Body = [0] }) == CommandResult.Unhandled && sent.Count == 3, "非空132不消费当前选择");
                var before = repository.LoadCharacter(owner, 0);
                AccountOwners.Lease? replacement = null;
                Task? queued = null;
                try
                {
                    if (scenario is not ("normal" or "failed-write"))
                    {
                        stream.Hold = true;
                        queued = sender.Send(1, 1347, [1], "queue_barrier");
                        await stream.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    }
                    stream.Fail = scenario == "failed-write";
                    var returning = commands.DispatchAsync(context, back);
                    if (scenario == "owner")
                        replacement = owners.Acquire(scenario, () => { });
                    else if (scenario == "reselect")
                    {
                        session.ReturnToRoster();
                        session.Select(0);
                        session.MarkReady();
                    }
                    else if (scenario == "reopen")
                        session.BeginDungeonSelection();
                    else if (scenario == "gm")
                        session.UpdateProgress(new(CharacterProgressField.Level, scenario, "ReturnWire", 20, 0));
                    stream.Release.TrySetResult();
                    bool failed = false;
                    try { await returning; }
                    catch (Exception e) when (e is IOException or InvalidOperationException or OperationCanceledException) { failed = true; }
                    if (queued is not null)
                    {
                        try { await queued; }
                        catch (Exception e) when (e is IOException or InvalidOperationException or OperationCanceledException) { }
                    }
                    That(failed == (scenario is "owner" or "gm" or "failed-write"), "失败/接管/同revision快照更新正确收尾：" + scenario);
                    if (scenario == "normal")
                    {
                        That(sent.Skip(3).Select(frame => (frame.Kind, frame.Opcode)).SequenceEqual(new (byte, ushort)[] { (1, 132), (0, 3), (0, 23), (0, 24) }),
                            "返回ACK132→State3→Area23→Transition24，不追加124");
                        That(sent[3].Body.SequenceEqual(new byte[] { 1 }) && sent[4].Body.SequenceEqual(new byte[] { 1, (byte)session.Selected!.Id, (byte)(session.Selected.Id >> 8), 0 }),
                            "ACK及本人状态包体精确");
                        var expected = before with { Position = new(38, 2, 89, 249), Revision = before.Revision + 1 };
                        That(presence.Count == 1 && session.CurrentDungeonSelection is null && JsonSerializer.Serialize(repository.LoadCharacter(owner, 0)) == JsonSerializer.Serialize(expected),
                            "恢复同屏与安全位置，仅提交位置和revision");
                        await commands.DispatchAsync(context, back);
                        That(sent.Count == 7 && session.Move(new(1084, 249, 5, 0, 0, 0)), "重复返回不重发场景，回城后移动恢复");
                    }
                    else
                        That(presence.Count == 0 && sent.Skip(3).All(frame => frame.Opcode is not (132 or 3 or 23 or 24)), "过期/失败出队不发旧回城也不加入场景：" + scenario);
                }
                finally
                {
                    stream.Release.TrySetResult();
                    replacement?.Dispose();
                    projection.Remove(context);
                    session.End();
                }
            }
        }
        finally { Directory.Delete(root, recursive: true); }
        Console.WriteLine($"PASS {Count - checks} dungeon selection publication assertions");
    }

    static void GateReturnCore(string root)
    {
        var origin = new Location(38, 2, 90, 249, 1);
        var left = new TownMap("left.map", [new(new(32, 197, 1080, 150), [], [])], [], false, [])
        {
            DungeonGates = [new(new(1, 193, 80, 160), [], [])]
        };
        var right = left with
        {
            Path = "right.map", Bounds = [new(new(21, 163, 1100, 155), [], [])],
            DungeonGates = [new(new(1060, 154, 80, 170), [], [])]
        };
        TownCatalog Catalog(TownMap map) => new(new() { [(38, 2)] = new(38, 2, 1, [], [], [], origin, map, [], []) },
            origin, origin, new(0, [], []), [], new Dictionary<uint, uint[]>());
        var towns = Catalog(left);
        var back = new Location(38, 2, 89, 249, 1);
        That(towns.SelectionReturn(origin) == back && towns.IsPosition(back), "左入口返回可走区，保留方向");
        foreach (ushort y in new ushort[] { 197, 249, 347 })
            That(towns.SelectionReturn(origin with { Y = y }) == (back with { Y = y }), "顶部、中部、底部站位返回均保持Y");
        That(towns.SelectionReturn(origin with { X = 74 }) == back, "触发区内部与边界外使用同一安全侧");
        That(Catalog(right).SelectionReturn(origin with { X = 1070, Y = 239 }) == (back with { X = 1052, Y = 239 }), "右侧入口不使用左侧固定坐标");
        That(towns.SelectionReturn(origin with { X = 1085 }) == back, "入口区域的菜单选择也使用同一资源返回点");
        That(Catalog(left with { DungeonGates = [] }).SelectionReturn(origin) == origin, "普通城镇和房间不做入口传送");
        var conditional = Catalog(left with { DungeonGates = [new(left.DungeonGates[0].Bounds, [7], [])] });
        That(conditional.SelectionReturn(origin) == origin && conditional.SelectionReturn(origin, new HashSet<int> { 7 }) == back, "条件入口遵守当前已完成任务");
        That(Catalog(left with { Bounds = [new(left.DungeonGates[0].Bounds, [], [])] }).SelectionReturn(origin) is null,
            "没有入口外可走区时拒绝，不能把玩家重新放回触发区");
        var blocked = left with { DungeonGates = [left.DungeonGates[0], new(new(85, 240, 50, 50), [], [])] };
        var alternate = Catalog(blocked).SelectionReturn(origin);
        That(alternate is not null && alternate != back && !blocked.DungeonGates.Any(g => g.Bounds.Contains(alternate.X, alternate.Y)),
            "另一触发区占据候选点时选择其他可走安全点");
        var shifted = new TownCatalog(new() { [(38, 2)] = new(38, 2, 1, [], [], [], origin, left, [right], [new(0, 7)]) },
            origin, origin, new(0, [], []), [], new Dictionary<uint, uint[]>());
        That(shifted.SelectionReturn(origin with { X = 1070, Y = 239 }, new HashSet<int> { 7 }) == (back with { X = 1052, Y = 239 }),
            "阶段地图使用当前入口与可走区");

        var repository = new CharacterRepository(Path.Combine(root, "gate.db"));
        var owners = new AccountOwners();
        using var owner = owners.Acquire("gate", () => { });
        repository.Create(owner, new("GateReturn", origin, origin) { Level = 23, CompletedQuests = [7] });
        var session = new GameSession(repository, towns, owner);
        session.Select(0);
        session.MarkReady();
        var before = session.Selected!;
        string persisted = JsonSerializer.Serialize(repository.LoadCharacter(owner, 0));
        var opened = session.BeginDungeonSelection();
        var selection = session.CurrentDungeonSelection!;
        That(selection.OriginPosition == origin && selection.ReturnPosition == back && session.Selected == before &&
            JsonSerializer.Serialize(repository.LoadCharacter(owner, 0)) == persisted, "开选图仅冻结安全返回点，不提前写库");
        That(session.BeginDungeonSelection().SelectionId == opened.SelectionId && ReferenceEquals(session.CurrentDungeonSelection, selection),
            "重复入口保留本次原位置和安全点");
        using var connection = new SqliteConnection("Data Source=" + Path.Combine(root, "gate.db") + ";Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TRIGGER reject_return BEFORE UPDATE ON characters BEGIN SELECT RAISE(FAIL, 'return fixture'); END";
        command.ExecuteNonQuery();
        Reject<SqliteException>(() => session.ReturnFromDungeonSelection(), "落点提交失败不消费选图身份");
        That(session.Phase == SessionPhase.MapSelect && ReferenceEquals(session.Selected, before) && ReferenceEquals(session.CurrentDungeonSelection, selection) &&
            JsonSerializer.Serialize(repository.LoadCharacter(owner, 0)) == persisted, "失败保留原会话、资产和位置");
        command.CommandText = "DROP TRIGGER reject_return";
        command.ExecuteNonQuery();
        var returned = session.ReturnFromDungeonSelection()!;
        var expected = before with { Position = back, Revision = before.Revision + 1 };
        That(JsonSerializer.Serialize(returned.Character) == JsonSerializer.Serialize(expected) &&
            JsonSerializer.Serialize(repository.LoadCharacter(owner, 0)) == JsonSerializer.Serialize(expected), "成功只提交位置和revision，完整角色资产不变");
        That(session.CanPublishDungeonSelection(returned, true) && !session.CanPublishDungeonSelection(opened), "回包使用落点提交后的实际快照");
        That(session.ReturnFromDungeonSelection() is null && session.Selected!.Revision == expected.Revision, "重复返回不二次写位置");
        session.BeginDungeonSelection();
        session.ReturnFromDungeonSelection();
        That(session.Selected!.Revision == expected.Revision, "安全落点再次打开和返回不重复提交");
        That(session.Move(new(100, 249, 5, 0, 0, 0)) && !left.DungeonGates[0].Bounds.Contains(session.Selected.Position.X, session.Selected.Position.Y),
            "回城后移动恢复，可以走离入口");
        session.End();
    }

    static void Reject<T>(Action action, string name) where T : Exception
    {
        try { action(); }
        catch (T) { That(true, name); return; }
        throw new InvalidOperationException(name);
    }

    sealed class HeldOutput : MemoryStream
    {
        public bool Hold { get; set; }
        public bool Fail { get; set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default)
        {
            if (Hold)
            {
                Hold = false;
                Started.TrySetResult();
                await Release.Task.WaitAsync(token);
            }
            if (Fail)
                throw new IOException("selection return write fixture");
            await base.WriteAsync(buffer, token);
        }
    }
}
