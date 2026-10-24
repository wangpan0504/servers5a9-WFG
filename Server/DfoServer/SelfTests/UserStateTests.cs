using System.Net;
using DfoServer.Game.Accounts;
using DfoServer.Game.Caching;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Session;
using DfoServer.Game.Town;
using DfoServer.GameWorld.Towns;
using DfoServer.Network;
using DfoServer.Network.Protocol;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class UserStateTests
{
    public static async Task RunCoreAsync()
    {
        int checks = Count;
        foreach (var (phase, state) in new[] { (SessionPhase.Loading, SinglePlayerDungeonPhase.Loading),
            (SessionPhase.Dungeon, SinglePlayerDungeonPhase.Dungeon), (SessionPhase.Settlement, SinglePlayerDungeonPhase.Settlement) })
        {
            That(UserStatePolicy.Read(phase, true, true, false, state, false) == UserState.Dungeon &&
                UserStatePolicy.Read(phase, true, true, false, state, true) == UserState.TutorialDungeon,
                "普通/训练粗业务状态保留加载、战斗、结算的真实细阶段");
            foreach (var wrong in Enum.GetValues<SinglePlayerDungeonPhase>().Where(value => value != state))
                Reject<InvalidOperationException>(() => UserStatePolicy.Read(phase, true, true, false, wrong, false), "会话与Run阶段错配拒绝");
        }
        That(UserStatePolicy.Read(SessionPhase.Unselected, false, false, false, null, false) == UserState.CharacterSelect &&
            UserStatePolicy.Read(SessionPhase.Unselected, true, false, false, null, false) == UserState.EnteringWorld,
            "未选角与已经选角但入城尚未完成是不同粗状态");
        foreach (var state in Enum.GetValues<UserState>())
        foreach (var requirement in Enum.GetValues<UserStateRequirement>())
        {
            bool expected = state switch
            {
                UserState.CharacterSelect => requirement == UserStateRequirement.Owner,
                UserState.EnteringWorld => requirement is UserStateRequirement.Owner or UserStateRequirement.CharacterSelected,
                UserState.Town or UserState.DungeonSelect => requirement != UserStateRequirement.DungeonContext,
                UserState.Dungeon or UserState.TutorialDungeon => true,
                _ => false
            };
            That(UserStatePolicy.Meets(state, requirement) == expected, "最低生命周期门禁不以玩法枚举排序推断资格");
        }
        That(!UserStatePolicy.Meets((UserState)255, UserStateRequirement.Owner) &&
            !UserStatePolicy.Meets(UserState.Dungeon, (UserStateRequirement)255), "未定义状态和资格拒绝");
        Reject<InvalidOperationException>(() => UserStatePolicy.Read(SessionPhase.Town, true, false, false, null, false), "未就绪不能宣称城镇");
        Reject<InvalidOperationException>(() => UserStatePolicy.Read(SessionPhase.MapSelect, true, true, false, null, false), "选图缺少选择身份拒绝");
        Reject<InvalidOperationException>(() => UserStatePolicy.Read(SessionPhase.Town, true, true, false, SinglePlayerDungeonPhase.Dungeon, false), "城镇不能同时持有正式副本");
        foreach (var from in Enum.GetValues<SessionPhase>())
        foreach (var to in Enum.GetValues<SessionPhase>())
        {
            bool expected = to == SessionPhase.Unselected || from switch
            {
                SessionPhase.Unselected => to == SessionPhase.Town,
                SessionPhase.Town or SessionPhase.MapSelect => to is SessionPhase.Town or SessionPhase.MapSelect or SessionPhase.Loading,
                SessionPhase.Loading => to is SessionPhase.Town or SessionPhase.Dungeon or SessionPhase.Settlement,
                SessionPhase.Dungeon => to is SessionPhase.Town or SessionPhase.Loading or SessionPhase.Dungeon or SessionPhase.Settlement,
                SessionPhase.Settlement => to is SessionPhase.Town or SessionPhase.Loading or SessionPhase.Settlement,
                _ => false
            };
            That(UserStatePolicy.CanTransition(from, to) == expected, "现有业务阶段转换图完整保留");
        }
        That(!UserStatePolicy.CanTransition((SessionPhase)255, SessionPhase.Unselected), "未知阶段不能借收尾变成正常状态");

        string root = Path.Combine(Path.GetTempPath(), "S5A9-state-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var point = new Location(1, 0, 10, 10);
            var map = new TownMap("state-fixture.map", [new(new(0, 0, 20, 20), [], [])], [], false, []);
            var towns = new TownCatalog(new() { [(1, 0)] = new(1, 0, 1, [], [], [], point, map, [], []) },
                point, point, new(0, [], []), [], new Dictionary<uint, uint[]>());
            var repository = new CharacterRepository(Path.Combine(root, "game.db"));
            using var memory = new GameCache();
            var owners = new AccountOwners(memory);
            using var owner = owners.Acquire("states", () => { });
            repository.Create(owner, new("StateRole", point, point));
            var session = new GameSession(repository, towns, owner, entry: new(new(2, 19), 23, "[raid]"));
            var online = new OnlineSessionDirectory(memory);
            online.Register(owner, session);
            That(online.Snapshot().Length == 0, "诊断查询不把未选角连接伪装成在线角色");
            That(session.ChannelEntry is { Identity.Number: 19, Type: 23, Group: "[raid]" } && session.GetState() == UserState.CharacterSelect,
                "分类入口是会话只读上下文，未实现玩法不因入口标签自动改变user状态");
            using var stop = new CancellationTokenSource();
            using var stream = new MemoryStream();
            await using var sender = new GameConnectionSender(owner, stream, stop, 0, (_, _, _, _, _) => { });
            var context = new GameConnectionContext(new(1, IPAddress.Loopback, stream, stop), owner, session, sender);
            var commands = new GameCommandRegistry();
            int called = 0;
            commands.Register(CmdPacketType.SET_USER_POSITION, (_, _) => { called++; return Task.FromResult(CommandResult.Handled); },
                "state-fixture", UserStateRequirement.WorldReady);
            Reject<ArgumentOutOfRangeException>(() => commands.Register(CmdPacketType.CREATE_CHARACTER, (_, _) => Task.FromResult(CommandResult.Handled),
                "state-fixture", (UserStateRequirement)255), "非法基础资格不能注册");
            commands.Seal();
            var packet = new Packet(1, 35, []);
            That(session.GetState() == UserState.CharacterSelect && session.CheckState(UserStateRequirement.Owner) &&
                await commands.DispatchAsync(context, packet) == CommandResult.Unhandled && called == 0,
                "分派前基础门禁拒绝未选角，不执行Handler也不写成功帧");
            session.Select(0);
            That(online.Snapshot() is [{ Account: "states", Name: "StateRole", Ready: false, State: UserState.EnteringWorld }],
                "诊断查询从同一owner读取已选角但未就绪的角色");
            That(session.GetState() == UserState.EnteringWorld && session.CheckState(UserStateRequirement.CharacterSelected) &&
                !session.CheckState(UserStateRequirement.WorldReady) && await commands.DispatchAsync(context, packet) == CommandResult.Unhandled,
                "已经选角但124尚未完成时仍不放行世界请求");
            session.MarkReady();
            That(online.Snapshot() is [{ Ready: true, Phase: SessionPhase.Town, State: UserState.Town }],
                "诊断查询不维护另一份就绪或阶段状态");
            That(session.GetState() == UserState.Town && session.CheckState(UserState.Town) &&
                await commands.DispatchAsync(context, packet) == CommandResult.Handled && called == 1 && stream.Length == 0,
                "就绪后基础分派放行，门禁本身不伪造网络响应");
            Task<CommandResult> waiting;
            await context.CommandGate.WaitAsync();
            try
            {
                waiting = commands.DispatchAsync(context, packet);
                That(!waiting.IsCompleted && called == 1,"后台资产发布占用门禁时同连接请求不能提前执行");
                using var otherOwner = owners.Acquire("other-state", () => { });
                using var otherStop = new CancellationTokenSource();
                using var otherBytes = new MemoryStream();
                await using var otherSender = new GameConnectionSender(otherOwner,otherBytes,otherStop,0,(_,_,_,_,_) => { });
                var other = new GameConnectionContext(new(2,IPAddress.Loopback,otherBytes,otherStop),otherOwner,
                    new GameSession(repository,towns,otherOwner),otherSender);
                That(await commands.DispatchAsync(other,packet).WaitAsync(TimeSpan.FromSeconds(1)) == CommandResult.Unhandled,
                    "同连接门禁不阻塞其他账号的正常请求及状态检查");
                otherStop.Cancel();
                bool cancelled = false;
                try { await commands.DispatchAsync(other,packet); }
                catch (OperationCanceledException) { cancelled = true; }
                That(cancelled && other.CommandGate.CurrentCount == 1,"取消等待不泄漏或误释放未取得的门禁");
                using var otherReplacement = owners.Acquire("other-state", () => { });
                bool staleRejected = false;
                try { await commands.DispatchAsync(other,packet); }
                catch (InvalidOperationException) { staleRejected = true; }
                That(staleRejected && other.CommandGate.CurrentCount == 1,
                    "已取消且被接管的连接先拒绝旧owner，不重跑已提交业务或误释放门禁");
            }
            finally { context.CommandGate.Release(); }
            That(await waiting.WaitAsync(TimeSpan.FromSeconds(1)) == CommandResult.Handled && called == 2,
                "发布完成后恢复同连接顺序执行，不丢失等待请求");
            var townReceipt = new TownTravelReceipt(session.Selected!, owner.Generation, session.SelectionEpoch);
            That(session.CheckState(UserState.Town) && session.CanPublishTravel(townReceipt),
                "城镇投影使用正式user状态与当前角色回执");
            session.BeginDungeonSelection();
            That(!session.CanPublishTravel(townReceipt),
                "已进入选图时旧城镇回执不能发布state0或把新上下文加入城镇");
            var selection = session.CurrentDungeonSelection;
            Reject<InvalidOperationException>(() => session.MarkReady(), "重复旧入城回调不能把选图强制改回Town");
            That(session.GetState() == UserState.DungeonSelect && ReferenceEquals(selection, session.CurrentDungeonSelection) &&
                session.CheckState(UserStateRequirement.WorldReady) && !session.CheckState(UserStateRequirement.DungeonContext),
                "拒绝旧回调保留选择身份，基础放行不代替具体城镇阶段资格");
            session.ReturnFromDungeonSelection();
            That(session.GetState() == UserState.Town && session.Phase == SessionPhase.Town, "回城粗状态和细阶段使用同一状态所有权");
            foreach (var state in Enum.GetValues<UserState>().Where(state => !UserStatePolicy.IsImplemented(state)))
                That(!session.CheckState(state), "未实现玩法只定义状态，不可进入或通过门禁");
            session.ReturnToRoster();
            That(online.Snapshot().Length == 0, "返回选角移除诊断在线角色");
            That(session.GetState() == UserState.CharacterSelect && !session.CheckState(UserStateRequirement.CharacterSelected), "回选角原实例状态清理");
            using var replacement = owners.Acquire("states", () => { });
            That(online.Snapshot().Length == 0, "诊断查询不会发布旧generation的角色");
            Reject<InvalidOperationException>(() => session.GetState(), "旧generation不能读取当前权威状态");
            Reject<InvalidOperationException>(() => session.CheckState(UserStateRequirement.Owner), "旧generation连最低门禁也拒绝");
            session.End();
            That(session.Phase == SessionPhase.Unselected && replacement.IsCurrent, "旧会话收尾不修改或释放新owner");
        }
        finally { Directory.Delete(root, true); }
        Console.WriteLine($"PASS {Count - checks} 用户业务状态、阶段图、基础门禁及旧会话隔离断言");
    }
}
