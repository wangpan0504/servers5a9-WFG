using System.Net;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Mailbox;
using DfoServer.Game.Party;
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
            var opening = session.BeginDungeonSelection()!;
            var current = session.CurrentDungeonSelection;
            That(session.Phase == SessionPhase.MapSelect && current?.ReturnPosition == point && session.CanPublishDungeonSelection(opening), "选图阶段拥有返回快照");
            That(session.BeginDungeonSelection()!.SelectionId == opening.SelectionId && ReferenceEquals(current, session.CurrentDungeonSelection), "重复入口不替换选择身份和返回点");
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
            var next = session.BeginDungeonSelection()!;
            That(next.SelectionId > returned.SelectionId && !session.CanPublishDungeonSelection(returned, true), "同epoch再次选图阻止旧返回发布");
            session.ReturnFromDungeonSelection();
            That(!session.CanPublishDungeonSelection(returned, true), "第二次回城后旧回执仍然失效");
            session.BeginDungeonSelection();
            session.ReturnToRoster();
            session.Select(0);
            session.MarkReady();
            That(session.CurrentDungeonSelection is null && session.ReturnFromDungeonSelection() is null, "返回选角后旧选择不能作用于新角色会话");
            var latest = session.BeginDungeonSelection()!;
            using var newer = owners.Acquire("selection", () => { });
            Reject<InvalidOperationException>(() => session.ReturnFromDungeonSelection(), "旧generation不能返回");
            Reject<InvalidOperationException>(() => session.CanPublishDungeonSelection(latest), "旧generation不能发布");
            session.End();
            That(session.CurrentDungeonSelection is null && session.Phase == SessionPhase.Unselected, "旧连接收尾只清本会话");
            GateReturnCore(root);
        }
        finally { TestFiles.DeleteDirectory(root, recursive: true); }
        Console.WriteLine($"PASS {Count - checks} dungeon selection lifecycle assertions");
    }

    public static async Task RunAsync(TownCatalog towns, CharacterCatalog characters, ItemResources items)
    {
        int checks = Count;
        var computer = new CharacterStatComputer(characters);
        That(towns.Areas[(38, 2)].Default.DungeonGates.Single().Bounds == new Rectangle(1, 193, 80, 160), "资源解析保留左入口-1/-1触发区");
        That(towns.SelectionReturn(new(38, 2, 90, 249)) == new Location(38, 2, 89, 249), "实际左入口边界外请求回到可走安全点，保持Y");
        That(towns.SelectionReturn(new(38, 4, 1065, 239)) == new Location(38, 4, 1052, 239), "实际右入口使用自身资源，向内返回");
        string root = Path.Combine(Path.GetTempPath(), "S5A9-selection-wire-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await UnavailableEntryAsync(towns,characters,items,root);
            foreach (string scenario in new[] { "normal", "quick-party", "failed-write", "owner", "reselect", "reopen", "gm" })
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
                var projection = new TownProjection(towns, computer, presence, items.EpicPieces);
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
                var cancel = new Packet(1, 444, []);
                That(await commands.DispatchAsync(context, cancel) == CommandResult.Unhandled && sent.Count == 0,
                    "城镇内迟到的快速组队取消不确认选图操作");
                await commands.DispatchAsync(context, new(1, 15, new byte[4]));
                That(sent.Select(frame => (frame.Kind, frame.Opcode)).SequenceEqual(new (byte, ushort)[] { (1, 15), (0, 3), (0, 1536), (0, 27) }) && presence.Count == 0,
                    "ACK15→本人State1→NOTI27同步选图，离开同屏");
                That(sent[1].Body.SequenceEqual(new byte[] { 1, (byte)session.Selected!.Id, (byte)(session.Selected.Id >> 8), 1 }), "开选图发送实际角色State1");
                That(await commands.DispatchAsync(context, back with { Body = [0] }) == CommandResult.Unhandled && sent.Count == 4, "非空132不消费当前选择");
                var before = repository.LoadCharacter(owner, 0);
                if (scenario == "quick-party")
                {
                    var selection = session.CurrentDungeonSelection;
                    var character = session.Selected;
                    That(await commands.DispatchAsync(context, cancel with { Body = [0] }) == CommandResult.Unhandled && sent.Count == 4,
                        "非空444不应答也不消费选图身份");
                    for (int repeat = 0; repeat < 2; repeat++)
                        That(await commands.DispatchAsync(context, cancel) == CommandResult.Handled &&
                            sent[^2] is { Kind: 1, Opcode: 444 } && sent[^2].Body.SequenceEqual(new byte[] { 1 }) &&
                            sent[^1] is { Kind: 0, Opcode: 27 } && sent[^1].Body.SequenceEqual(new byte[42]),
                            "快速组队取消及重试均回复ACK444并发布完整普通选图刷新");
                    That(sent.Count == 8 && presence.Count == 0 && session.Phase == SessionPhase.MapSelect &&
                        ReferenceEquals(session.CurrentDungeonSelection, selection) && ReferenceEquals(session.Selected, character) &&
                        JsonSerializer.Serialize(repository.LoadCharacter(owner, 0)) == JsonSerializer.Serialize(before),
                        "快速组队取消只完成握手，保留原选图、安全落点及完整持久资产");
                    sent.RemoveRange(4, 4);
                }
                AccountOwners.Lease? replacement = null;
                Task? queued = null;
                try
                {
                    if (scenario is not ("normal" or "quick-party" or "failed-write"))
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
                    if (scenario is "normal" or "quick-party")
                    {
                        That(sent.Skip(4).Select(frame => (frame.Kind, frame.Opcode)).SequenceEqual(new (byte, ushort)[] { (1, 132), (0, 3), (0, 23), (0, 24) }),
                            "返回ACK132→State3→Area23→Transition24，不追加124");
                        That(sent[4].Body.SequenceEqual(new byte[] { 1 }) && sent[5].Body.SequenceEqual(new byte[] { 1, (byte)session.Selected!.Id, (byte)(session.Selected.Id >> 8), 0 }),
                            "ACK及本人状态包体精确");
                        var expected = before with { Position = new(38, 2, 89, 249), Revision = before.Revision + 1 };
                        That(presence.Count == 1 && session.CurrentDungeonSelection is null && JsonSerializer.Serialize(repository.LoadCharacter(owner, 0)) == JsonSerializer.Serialize(expected),
                            "恢复同屏与安全位置，仅提交位置和revision");
                        await commands.DispatchAsync(context, back);
                        That(sent.Count == 8 && session.Move(new(1084, 249, 5, 0, 0, 0)), "重复返回不重发场景，回城后移动恢复");
                        That(await commands.DispatchAsync(context, cancel) == CommandResult.Unhandled && sent.Count == 8,
                            "回城后迟到444不重开选图或发布取消成功");
                    }
                    else
                        That(presence.Count == 0 && sent.Skip(4).All(frame => frame.Opcode is not (132 or 3 or 23 or 24)), "过期/失败出队不发旧回城也不加入场景：" + scenario);
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
        finally { TestFiles.DeleteDirectory(root, recursive: true); }
        Console.WriteLine($"PASS {Count - checks} dungeon selection publication assertions");
    }

    internal static async Task VerifyTrainingPartyEntryAsync(TownCatalog towns, CharacterCatalog characters, ItemResources items, string root)
    {
        // 复用受控发送器核验失败投递身份；真实39/0请求和排队16另由训练TCP覆盖。
        root = Path.Combine(root, "training-party-publication");
        Directory.CreateDirectory(root);
        foreach (string scenario in new[] { "normal", "owner", "reselect", "selection", "party" })
        {
            var repository = new CharacterRepository(Path.Combine(root, scenario + ".db"));
            var owners = new AccountOwners();
            using var owner = owners.Acquire("training-" + scenario, () => { });
            using var peerOwner = owners.Acquire("training-peer", () => { });
            var point = new Location(38, 2, 1085, 249);
            repository.Create(owner, new("TrainLeader", point, point));
            repository.Create(peerOwner, new("TrainMember", point, point));
            var session = new GameSession(repository, towns, owner);
            var member = new GameSession(repository, towns, peerOwner);
            var parties = new PartyService(repository, inviteAntiEvil: _ => 0);
            foreach (var current in new[] { session, member })
            {
                current.Select(0, resumeTown: true);
                current.MarkReady();
                current.HasMultiplePartyMembers = () => parties.HasMultipleMembers(current);
                current.CurrentParty = () => parties.Current(current);
            }
            That(parties.Invite(session, member, 0).Accepted &&
                parties.Respond(member, session.Selected!.Id, 0, true).Accepted,
                "训练失败投递夹具由实际队伍服务建立双成员队伍");
            var before = session.Selected;
            var peerBefore = member.Selected;
            var party = parties.Current(session);
            string persisted = JsonSerializer.Serialize(repository.LoadCharacter(owner, 0));
            string peerPersisted = JsonSerializer.Serialize(repository.LoadCharacter(peerOwner, 0));
            var presence = new TownPresence(towns);
            var projection = new TownProjection(towns, new CharacterStatComputer(characters), presence, items.EpicPieces);
            using var log = new ServerLog(Path.Combine(root, scenario));
            using var stop = new CancellationTokenSource();
            using var stream = new HeldOutput { Hold = true };
            var sent = new List<(byte Kind, ushort Opcode, byte[] Body)>();
            await using var sender = new GameConnectionSender(owner, stream, stop, 0,
                (kind, opcode, _, body, _) => sent.Add((kind, opcode, body.ToArray())));
            var context = new GameConnectionContext(new(1, IPAddress.Loopback, stream, stop), owner, session, sender);
            var commands = new GameCommandRegistry();
            new DungeonSelectionHandler(projection, log).Register(commands);
            commands.Seal();
            projection.Join(context);
            var queued = sender.Send(1, 1347, [1], "training_failure_queue_barrier");
            await stream.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            AccountOwners.Lease? replacement = null;
            try
            {
                That(await commands.DispatchAsync(context, new(1, 15, Convert.FromHexString("88130000"))) == CommandResult.Handled,
                    "训练失败沿实际分派入队，不持共享门禁等待慢连接");
                That(ReferenceEquals(before, session.Selected) && ReferenceEquals(peerBefore, member.Selected) &&
                    ReferenceEquals(party, parties.Current(session)) && presence.Count == 1 &&
                    session.Phase == SessionPhase.Town && member.Phase == SessionPhase.Town &&
                    session.CurrentDungeonSelection is null && member.CurrentDungeonSelection is null &&
                    session.PartyDungeon is null && member.PartyDungeon is null &&
                    JsonSerializer.Serialize(repository.LoadCharacter(owner, 0)) == persisted &&
                    JsonSerializer.Serialize(repository.LoadCharacter(peerOwner, 0)) == peerPersisted,
                    "拒绝不安装任何成员选图或Run，不移除城镇实体、不改变双方快照或持久资产");
                if (scenario == "owner")
                    replacement = owners.Acquire(owner.Account, () => { });
                else if (scenario == "reselect")
                {
                    session.ReturnToRoster();
                    session.Select(0, resumeTown: true);
                    session.MarkReady();
                }
                else if (scenario == "selection")
                    That(session.BeginDungeonSelection(partyAdmission: true) is not null,
                        "同选角epoch的新合法选图使旧Town失败不再适用");
                else if (scenario == "party")
                    That(parties.Leave(member).Accepted, "退队改变实际队伍快照而不改变队长选角epoch");
                stream.Release.TrySetResult();
                bool failed = false;
                try { await queued; await sender.DrainAsync(); }
                catch (Exception error) when (error is IOException or InvalidOperationException or OperationCanceledException)
                { failed = true; }
                That(failed == (scenario == "owner"), "只有失效owner终止发送，场景/队伍过期只丢弃原失败：" + scenario);
                var failures = sent.Where(frame => frame is { Kind: 1, Opcode: 15 }).ToArray();
                That(scenario == "normal"
                    ? failures.Length == 1 && failures[0].Body.SequenceEqual(new byte[] { 0, 1, 0 })
                    : failures.Length == 0,
                    "ACK15失败只向原Town/角色代次/队伍发送，不污染接管或后续合法选图：" + scenario);
            }
            finally
            {
                stream.Release.TrySetResult();
                replacement?.Dispose();
                parties.Detach(session);
                parties.Detach(member);
                session.End();
                member.End();
            }
        }
        Console.WriteLine("PASS 训练入口拒绝的双方状态、owner/epoch、场景与队伍快照发送守卫（受控发送，非实机）");
    }

    static async Task UnavailableEntryAsync(TownCatalog towns, CharacterCatalog characters, ItemResources items, string root)
    {
        // 合成无安全点的几何，只验证生产处理器拒绝和恢复；不将其冒充报告者实际PVF。
        var area = towns.Areas[(38,2)];
        var gate = area.Default.DungeonGates.Single();
        var inside = new Location(38,2,74,249);
        var unavailable = area.Default with { Bounds = [new(gate.Bounds,[],[])] };
        var areas = towns.Areas.ToDictionary(row => row.Key,row => row.Value);
        areas[(38,2)] = area with { Default = unavailable, Phases = [], Shifts = [] };
        var synthetic = new TownCatalog(areas,towns.Room,towns.InitialReturn,towns.Dock,towns.RoomPortals,towns.NpcMoves,towns.DungeonMaps);
        var repository = new CharacterRepository(Path.Combine(root,"unavailable.db"));
        using var owner = new AccountOwners().Acquire("unavailable-wire",() => { });
        repository.Create(owner,new("GateWire",inside,inside));
        var session = new GameSession(repository,synthetic,owner);
        // 普通选角会回房；复用频道恢复入口保留此合成现场的已提交城镇坐标。
        session.Select(0,resumeTown: true);
        session.MarkReady();
        That(session.Selected!.Position == inside, "无安全点协议fixture确实处于目标入口，不因选角回房绕过拒绝分支");
        var before = session.Selected!;
        string persisted = JsonSerializer.Serialize(repository.LoadCharacter(owner,0));
        var presence = new TownPresence(synthetic);
        var projection = new TownProjection(synthetic,new CharacterStatComputer(characters),presence,items.EpicPieces);
        using var log = new ServerLog(Path.Combine(root,"unavailable-wire"));
        using var stop = new CancellationTokenSource();
        using var stream = new HeldOutput();
        var sent = new List<(byte Kind, ushort Opcode, byte[] Body)>();
        await using var sender = new GameConnectionSender(owner,stream,stop,0,(kind,opcode,_,body,_) => sent.Add((kind,opcode,body.ToArray())));
        var context = new GameConnectionContext(new(1,IPAddress.Loopback,stream,stop),owner,session,sender);
        var commands = new GameCommandRegistry();
        new DungeonSelectionHandler(projection,log).Register(commands);
        commands.Seal();
        projection.Join(context);
        That(await commands.DispatchAsync(context,new(1,15,new byte[4])) == CommandResult.Handled && sent.Count == 1 &&
            sent[0] is { Kind: 1, Opcode: 15 } && sent[0].Body.SequenceEqual(new byte[] {0,1,0}),
            "无安全点只回复完整失败ACK15，不通知选图成功");
        That(presence.Count == 1 && session.Phase == SessionPhase.Town && session.CurrentDungeonSelection is null &&
            ReferenceEquals(session.Selected,before) && JsonSerializer.Serialize(repository.LoadCharacter(owner,0)) == persisted,
            "无安全点保留原城镇成员、快照和数据库，不分配选择身份");
        // 职业体验等待拥有自己的选择身份；普通入口拒绝不能消费它或误恢复Town。
        await commands.DispatchAsync(context,new(1,15,Convert.FromHexString("E61B0000")));
        var experience = session.CurrentDungeonSelection;
        That(experience?.IsClassExperience == true && session.Phase == SessionPhase.MapSelect && presence.Count == 0 &&
            sent.Skip(1).Select(frame => (frame.Kind,frame.Opcode)).SequenceEqual(new (byte,ushort)[] {(1,15),(0,3)}),
            "无普通返回点仍可进入专用职业体验等待，不混用普通入口几何");
        int beforeRejection = sent.Count;
        await commands.DispatchAsync(context,new(1,15,new byte[4]));
        That(sent.Count == beforeRejection + 1 && sent[^1] is { Kind: 1, Opcode: 15 } &&
            sent[^1].Body.SequenceEqual(new byte[] {0,1,0}) && ReferenceEquals(session.CurrentDungeonSelection,experience) &&
            session.Phase == SessionPhase.MapSelect && ReferenceEquals(session.Selected,before) &&
            JsonSerializer.Serialize(repository.LoadCharacter(owner,0)) == persisted,
            "从体验等待切普通选图无安全点时只拒绝本次请求，保留原体验身份、阶段和存档");
        areas[(38,2)] = area;
        int beforeRecovery = sent.Count;
        await commands.DispatchAsync(context,new(1,15,new byte[4]));
        That(sent.Skip(beforeRecovery).Select(frame => (frame.Kind,frame.Opcode)).SequenceEqual(new (byte,ushort)[] {(1,15),(0,3),(0,1536),(0,27)}) &&
            session.Phase == SessionPhase.MapSelect && presence.Count == 0 &&
            session.CurrentDungeonSelection is { IsClassExperience: false } ordinary && ordinary.Id > experience!.Id,
            "原连接在恢复合法几何后可正常开选图，没有吞结构错误或遗留选择身份");
        session.ReturnFromDungeonSelection();
        session.End();
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
        foreach (var (y, expectedY) in new (ushort, ushort)[] { (193,197), (353,347) })
            That(towns.SelectionReturn(origin with { Y = y }) == (back with { Y = expectedY }),
                "入口上下边缘超出外侧可走矩形时只调整到最近合法Y");
        var shiftedWalk = Catalog(left with { Bounds = [new(new(32, 240, 1080, 20), [7], [])] });
        That(shiftedWalk.SelectionReturn(origin with { Y = 193 }) is null &&
            shiftedWalk.SelectionReturn(origin with { Y = 193 },new HashSet<int> { 7 }) == (back with { Y = 240 }),
            "Y回退仍检查可走区域任务条件，不以未解锁区域生成落点");
        var overlapping = left with { Bounds = [new(new(32, 240, 65, 20), [], [])],
            DungeonGates = [left.DungeonGates[0],new(new(85,245,50,10), [], [])] };
        var around = Catalog(overlapping).SelectionReturn(origin);
        That(around is { } safe && safe == (back with { Y = 244 }) && !overlapping.DungeonGates.Any(g => g.Bounds.Contains(safe.X,safe.Y)),
            "最近Y被另一触发区覆盖时选择其外侧合法边界，不把玩家放进新入口");
        That(towns.SelectionReturn(origin with { X = 74 }) == back, "触发区内部与边界外使用同一安全侧");
        That(Catalog(right).SelectionReturn(origin with { X = 1070, Y = 239 }) == (back with { X = 1052, Y = 239 }), "右侧入口不使用左侧固定坐标");
        That(towns.SelectionReturn(origin with { X = 1085 }) == back, "入口区域的菜单选择也使用同一资源返回点");
        That(Catalog(left with { DungeonGates = [] }).SelectionReturn(origin) == origin, "普通城镇和房间不做入口传送");
        var conditional = Catalog(left with { DungeonGates = [new(left.DungeonGates[0].Bounds, [7], [])] });
        That(conditional.SelectionReturn(origin) == origin && conditional.SelectionReturn(origin, new HashSet<int> { 7 }) == back, "条件入口遵守当前已完成任务");
        That(Catalog(left with { Bounds = [new(left.DungeonGates[0].Bounds, [], [])] }).SelectionReturn(origin) is null,
            "没有入口外可走区时拒绝，不能把玩家重新放回触发区");
        var noSafe = Catalog(left with { Bounds = [new(left.DungeonGates[0].Bounds, [], [])] });
        var unavailableRepository = new CharacterRepository(Path.Combine(root, "gate-unavailable.db"));
        using (var unavailableOwner = new AccountOwners().Acquire("unavailable", () => { }))
        {
            var inside = origin with { X = 74 };
            unavailableRepository.Create(unavailableOwner,new("NoSafeReturn",inside,inside));
            var unavailableSession = new GameSession(unavailableRepository,noSafe,unavailableOwner);
            unavailableSession.Select(0);
            unavailableSession.MarkReady();
            var unchanged = unavailableSession.Selected!;
            string snapshot = JsonSerializer.Serialize(unavailableRepository.LoadCharacter(unavailableOwner,0));
            That(unavailableSession.BeginDungeonSelection() is null && unavailableSession.Phase == SessionPhase.Town &&
                unavailableSession.CurrentDungeonSelection is null && ReferenceEquals(unavailableSession.Selected,unchanged) &&
                JsonSerializer.Serialize(unavailableRepository.LoadCharacter(unavailableOwner,0)) == snapshot,
                "无真实安全点是业务拒绝，保留Town、原快照与存档，不分配选择身份或抛出断线异常");
            unavailableSession.End();
        }
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
        var opened = session.BeginDungeonSelection()!;
        var selection = session.CurrentDungeonSelection!;
        That(selection.OriginPosition == origin && selection.ReturnPosition == back && session.Selected == before &&
            JsonSerializer.Serialize(repository.LoadCharacter(owner, 0)) == persisted, "开选图仅冻结安全返回点，不提前写库");
        That(session.BeginDungeonSelection()!.SelectionId == opened.SelectionId && ReferenceEquals(session.CurrentDungeonSelection, selection),
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
