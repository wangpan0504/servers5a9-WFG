using DfoServer.Game.Accounts;
using DfoServer.Game.Caching;
using DfoServer.Game.Characters;
using DfoServer.Game.Pvp;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Towns;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class PvpArenaSessionTests
{
    public static async Task RunCoreAsync()
    {
        int before = Count;
        string root = Path.Combine(Path.GetTempPath(), "S5A9-arena-session-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var point = new Location(10, 4, 10, 10);
            var map = new TownMap("arena-fixture.map", [new(new(0, 0, 100, 100), [], [])], [], false, []);
            var towns = new TownCatalog(new() { [(10, 4)] = new(10, 4, 1, [], [], [], point, map, [], []) },
                point, point, new(0, [], []), [], new Dictionary<uint, uint[]>());
            var repository = new CharacterRepository(Path.Combine(root, "game.db"));
            using var memory = new GameCache();
            var owners = new AccountOwners(memory);
            var arena = new PvpArenaService(memory, PvpArenaCatalogTests.Fixture(), repository,
                ratings: new PvpRatingSettlement(repository, new PvpRatingCalculator(PvpRatingTests.Fixture())));
            using var hostOwner = owners.Acquire("arena-host", () => { });
            using var guestOwner = owners.Acquire("arena-guest", () => { });
            var host = Session(hostOwner, "ArenaHost");
            var guest = Session(guestOwner, "ArenaGuest");
            That(arena.Create(host, "room", PvpArenaMode.Individual, 0) is null,
                "未选角连接不能创建正式决斗房间");
            host.Select(0);
            That(arena.Create(host, "room", PvpArenaMode.Individual, 0) is null,
                "未就绪连接不能创建正式决斗房间");
            host.MarkReady(); guest.Select(0); guest.MarkReady();
            var original = host.Selected!;
            That(arena.Create(host, "bad-map", PvpArenaMode.Individual, 50000) is null &&
                host.CurrentPvpArena is null, "地图脚本ID不能冒充普通房间选择序号");
            That(arena.Create(host, "room", PvpArenaMode.Individual, 0, true) is null,
                "其它活动门禁禁止正式决斗准入");
            using var wrongOwner = owners.Acquire("arena-wrong", () => { });
            var wrong = Session(wrongOwner, "ArenaWrong", 22);
            wrong.Select(0); wrong.MarkReady();
            That(arena.Create(wrong, "room", PvpArenaMode.Individual, 0) is null,
                "普通频道不能借决斗命令安装正式房间");
            using var ordinaryOwner = owners.Acquire("arena-ordinary", () => { });
            var ordinary = Session(ordinaryOwner, "ArenaNormal", 4);
            ordinary.Select(0); ordinary.MarkReady();
            That(ordinary.UsesArenaSkills && ordinary.CapturePvpArenaAdmission() is not null &&
                ordinary.CapturePvpMatchingAdmission() is null,
                "普通决斗使用独立决斗技能并允许手动房间，不扩大自动匹配门禁");
            var ordinaryCreated = arena.Create(ordinary, "ordinary", PvpArenaMode.Individual, 0)!;
            That(ordinaryCreated.Member.IsCurrent && ordinary.GetState() == UserState.PvpRoom,
                "type4在原Session和房间服务中安装真实成员");
            ordinaryCreated.Member.Room.Commands.Release();
            await arena.DetachAsync(ordinary);
            var parties = new DfoServer.Game.Party.PartyService(repository);
            guest.CurrentParty = () => parties.Current(guest);
            That(parties.SetInformation(guest, 0, DfoServer.Game.Party.PartySettings.Default).Accepted &&
                arena.Create(guest, "room", PvpArenaMode.Individual, 0) is null,
                "单人队伍也与正式决斗房间互斥");
            That(parties.Leave(guest).Accepted, "退出队伍后恢复正式准入资格");
            var created = arena.Create(host, "", PvpArenaMode.Individual, 0, titlePreset: 4, password: "secret")!;
            var room = created.Member.Room;
            That(created.Member.IsCurrent && room.Commands.CurrentCount == 0 &&
                host.GetState() == UserState.PvpRoom && ReferenceEquals(original, host.Selected),
                "建房安装原会话成员并持新房间门禁，不改角色资产");
            room.Commands.Release();
            That(arena.Create(host, "duplicate", PvpArenaMode.Individual, 0) is null &&
                host.BeginPvpTraining(_ => throw new Exception("Arena must exclude training")) is null,
                "正式房间不能重复进入或覆盖为训练实例");
            await room.Commands.WaitAsync();
            PvpArenaChange joined;
            try
            {
                long priorRevision = room.Revision;
                That(arena.Join(guest, room, false) is null && arena.Join(guest, room, false, password: "wrong") is null &&
                    guest.CurrentPvpArena is null && guest.GetState() == UserState.PvpTown && room.Revision == priorRevision,
                    "缺密码或错密码不安装成员、不改变席位和房间版本");
                joined = arena.Join(guest, room, false, password: "secret")!;
                That(joined.View.Members.Length == 2 && joined.Notices is [{ Recipient: var recipient }] &&
                    ReferenceEquals(recipient, created.Member), "入房仅向原成员发布同一房间快照");
                That(joined.Initial is { View.TitlePreset: 4 } && joined.Notices[0].Characters.Length == 1 &&
                    joined.Notices[0].Characters[0].Character.Id == guest.Selected!.Id,
                    "预设标题恢复且原成员只补新入房角色，避免重建旧角色引用");
                That(arena.Join(wrong, room, false) is null && arena.Join(guest, room, false) is null,
                    "跨频道和重复入房不增加成员");
                That(arena.SetReady(guest, true).Length == 2 && room.Member(joined.Member.Token)!.Ready,
                    "准备操作绑定当前成员并发布给全房间");
                var now = DateTimeOffset.UnixEpoch;
                That(arena.Start(guest, now).Length == 0 && arena.Start(host, now).Length == 2 &&
                    room.Match is { Phase: PvpArenaMatchPhase.Loading }, "仅房主启动冻结局，客人不能用准备请求启动");
                var match = room.Match!;
                That(arena.Start(host, now).Length == 0 && arena.Configure(host, PvpArenaMode.Team, 1).Length == 0 &&
                    arena.Move(guest, 3, 1, false).Length == 0, "加载期间重复启动、改模式和挪席均无副作用");
                That(arena.Loaded(host, Guid.NewGuid(), now).Length == 0 &&
                    arena.Loaded(host, match.Id, now).Length == 2 && arena.Loaded(host, match.Id, now).Length == 0 &&
                    arena.Death(guest, match.Id, guest.Selected!.Id, host.Selected!.Id).Length == 0,
                    "精确局ID加载且重复不计数，屏障未完不接受死亡");
                That(arena.Connected(host, match.Id, new bool[8], now).Length == 0 &&
                    arena.Connected(host, match.Id, Enumerable.Repeat(true, 8).ToArray(), now).Length == 2 &&
                    arena.Connected(guest, match.Id, Enumerable.Repeat(true, 8).ToArray(), now).Length == 2 &&
                    host.GetState() == UserState.PvpRoom, "连接失败席不能冒充成功，连接完成仍须等待所有人加载");
                var lastLoad = arena.Loaded(guest, match.Id, now);
                That(lastLoad.Length == 2 && lastLoad.All(row => row.View.MatchPhase == PvpArenaMatchPhase.Fighting) &&
                    host.GetState() == UserState.PvpBattle && guest.GetState() == UserState.PvpBattle,
                    "最后一个加载报告也能跨越双屏障并使全体进入对战状态");
                That(arena.Death(host, match.Id, guest.Selected!.Id, host.Selected!.Id).Length == 0 &&
                    arena.Death(guest, match.Id, guest.Selected.Id, 60000).Length == 0,
                    "不能替其他成员报死亡或捏造局外击杀者");
                That(arena.Death(guest, match.Id, guest.Selected.Id, host.Selected!.Id) is
                    [{ VictimSeat: 1, KillerSeat: 0 }, { VictimSeat: 1, KillerSeat: 0 }] &&
                    match.Result is not null && arena.Death(guest, match.Id, guest.Selected.Id, host.Selected.Id).Length == 0,
                    "死亡转换为原局席位且仅结算一次");
                using (var db = new SqliteGameDatabase(Path.Combine(root, "game.db")).Open())
                {
                    using var fault = db.CreateCommand();
                    fault.CommandText = "CREATE TRIGGER arena_result_fail BEFORE INSERT ON pvp_arena_match_results BEGIN SELECT RAISE(ABORT,'Injected result failure'); END";
                    fault.ExecuteNonQuery();
                    Reject<Microsoft.Data.Sqlite.SqliteException>(() => arena.RecordResult(guest, now),
                        "结果持久化失败不能安装已提交状态或发布结果成功");
                    That(match.CommittedRecord is null && repository.ReadPvpArenaResult(hostOwner, match.Id) is null,
                        "提交异常没有内存成功状态或半份整局结果");
                    int failures = 0;
                    room.Commands.Release();
                    try
                    {
                        That(await arena.MaintainMatchingAsync(now.AddSeconds(1), failed: (_, _) => failures++) is [] &&
                            failures == 1 && match.CommittedRecord is null,
                            "维护捕获本房间数据库失败且保留原局，不使全服维护任务退出");
                    }
                    finally { await room.Commands.WaitAsync(); }
                    fault.CommandText = "DROP TRIGGER arena_result_fail";
                    fault.ExecuteNonQuery();
                }
                room.Commands.Release();
                try { await arena.MaintainMatchingAsync(now.AddSeconds(2)); }
                finally { await room.Commands.WaitAsync(); }
                var recorded = match.CommittedRecord!;
                That(arena.CaptureResultDelivery(host, match.Id) is not null &&
                    arena.CaptureResultDelivery(guest, Guid.NewGuid()) is null && arena.ReturnResult(guest, match.Id).Length == 0,
                    "无人再报死亡时维护重试整局及双方评分，旧局和未发布结果仍不能提前返回");
                That(recorded.MatchId == match.Id && recorded.Participants.Length == 2 &&
                    recorded.Participants.Single(row => row.Account == hostOwner.Account).Outcome == PvpArenaOutcome.Win &&
                    ReferenceEquals(arena.RecordResult(host, now.AddSeconds(1)), recorded),
                    "整局事实提交后安装同一回执，重复请求不更换参与者或时间");
                That(repository.ReadPvpRating(hostOwner, host.Selected!.Id) is { State.Experience: 450, Revision: 1 } &&
                    repository.ReadPvpRating(guestOwner, guest.Selected!.Id) is { State.Experience: 0, Revision: 1 },
                    "各成员在自己的owner下消费普通经验来源，整局回执重试不重复发放");
                var delivery = arena.CaptureResultDelivery(host, match.Id)!;
                That(delivery.Receipt.Change.ExperienceDelta == 450 && delivery.Receipt.Change.After.Experience == 450 &&
                    delivery.Receipt.Outcome == PvpArenaOutcome.Win && ReferenceEquals(delivery.Record, recorded) &&
                    arena.CanPublishResultDelivery(delivery), "发布凭据携带本人的真实提交回执及整局事实，不再仅保留到账布尔值");
                That(!arena.CanPublishResultDelivery(delivery with { Receipt = delivery.Receipt with { } }) &&
                    !arena.ConfirmResultQueued(delivery with { Record = recorded with { } }) &&
                    arena.ReturnResult(host, match.Id).Length == 0,
                    "重新拼装的等值回执不能冒充实际发布来源，未确认入队不改变返回状态");
                int attempts = 0;
                bool accept = false;
                host.PvpArenaResultArrived = value =>
                {
                    attempts++;
                    That(value.Rows.Length == 2 && value.Rows.Single(row => row.Character == host.Selected.Id).Kills == 1 &&
                        value.Rows.Single(row => row.Character == guest.Selected!.Id).Kills == 0 && value.WinPoints == host.Selected.Inventory.WinPoints,
                        "完整结果采用双方真实结算段位、有效死亡击杀及本人当前胜点");
                    return accept;
                };
                guest.PvpArenaResultArrived = _ => false;
                arena.PublishResults(room);
                That(!match.CanReturnResult(created.Member.Token) && arena.ReturnResult(host, match.Id).Length == 0,
                    "发送队列明确拒绝时不确认结果，不开放返回门禁");
                accept = true;
                arena.PublishResults(room);
                arena.PublishResults(room);
                That(attempts == 2 && match.IsResultQueued(created.Member.Token) && !arena.ConfirmResultQueued(delivery) &&
                    arena.ReturnResult(host, Guid.NewGuid()).Length == 0 && arena.ReturnResult(host, match.Id).Length == 2 &&
                    arena.ReturnResult(host, match.Id).Length == 0 && ReferenceEquals(room.Match, match) &&
                    !room.SetReady(created.Member.Token, true) &&
                    repository.ReadPvpRating(hostOwner, host.Selected.Id)!.Revision == 1,
                    "返回只消费本人的已入队原局，不能重复到账或越过另一原成员开启下一局");
                host.PvpArenaResultArrived = null;
                var reopened = new CharacterRepository(Path.Combine(root, "game.db"));
                var durable = reopened.ReadPvpArenaResult(hostOwner, match.Id)!;
                That(durable.MatchId == recorded.MatchId && durable.Participants.SequenceEqual(recorded.Participants),
                    "独立仓储重读同一完整整局结果，不依赖在线内存");
                Reject<InvalidOperationException>(() => repository.CommitPvpArenaResult(hostOwner,
                    recorded with { Forfeit = !recorded.Forfeit }), "同一局冲突结果拒绝，不覆盖原回执");
                using (var outsider = owners.Acquire("arena-outsider", () => { }))
                {
                    That(repository.ReadPvpArenaResult(outsider, match.Id) is null,
                        "局外账号不能读取其他账号的整局结果");
                    Reject<InvalidOperationException>(() => repository.CommitPvpArenaResult(outsider, recorded),
                        "局外账号不能提交其他账号的整局结果");
                }
                var left = arena.Leave(host);
                var departure = left.Single(row => row.Departure is not null).Departure!;
                That(room.View().Leader == room.Member(joined.Member.Token)!.Seat && host.GetState() == UserState.PvpTown &&
                    host.CurrentPvpArena is null && host.CanPublishPvpArenaDeparture(departure),
                    "房主退出移交房主并以原退出版本发布本人返城");
                That(arena.Leave(host).Length == 0 && room.View().Members.Length == 1,
                    "重复退出不移除其他成员");
                var next = arena.Create(host, "next", PvpArenaMode.Individual, 0)!;
                next.Member.Room.Commands.Release();
                That(!arena.CanPublishResultDelivery(delivery) && !arena.ConfirmResultQueued(delivery) &&
                    arena.CaptureResultDelivery(host, match.Id) is null,
                    "进入新房间后，原成员的结果发送与迟到入队确认均失效");
                That(!host.CanPublishPvpArenaDeparture(departure) && !created.Member.IsCurrent,
                    "重新建房后旧退出回执和原成员失效");
                using var forfeitOwner = owners.Acquire("arena-forfeit", () => { });
                var forfeiter = Session(forfeitOwner, "ArenaQuit");
                forfeiter.Select(0); forfeiter.MarkReady();
                var nextRoom = next.Member.Room;
                await nextRoom.Commands.WaitAsync();
                try
                {
                    That(arena.Join(forfeiter, nextRoom, false) is not null, "弃权夹具加入独立下一房间");
                    arena.SetReady(forfeiter, true);
                    arena.Start(host, now);
                    var forfeitMatch = nextRoom.Match!;
                    foreach (var fighter in new[] { host, forfeiter })
                    {
                        arena.Loaded(fighter, forfeitMatch.Id, now);
                        arena.Connected(fighter, forfeitMatch.Id, Enumerable.Repeat(true, 8).ToArray(), now);
                    }
                    That(arena.Leave(forfeiter).Length == 2 && forfeiter.CurrentPvpArena is null &&
                        repository.ReadPvpArenaResult(hostOwner, forfeitMatch.Id) is { Forfeit: true } result &&
                        result.Participants.Single(row => row.Account == hostOwner.Account).Outcome == PvpArenaOutcome.Win,
                        "战斗主动退出先保存双方弃权结果，再清除退出者精确上下文");
                    That(arena.Leave(forfeiter).Length == 0 &&
                        repository.ReadPvpArenaResult(hostOwner, forfeitMatch.Id)!.Participants.Length == 2,
                        "重复退出不能缩短冻结参战表或重复提交弃权");
                }
                finally { nextRoom.Commands.Release(); }
            }
            finally { room.Commands.Release(); }
            await arena.MaintainMatchingAsync(DateTimeOffset.UnixEpoch.AddSeconds(2));
            That(repository.ReadPvpRating(hostOwner, host.Selected!.Id) is { State.Experience: 900, Revision: 2 },
                "维护驱动在房间门禁外逐账号完成已提交普通来源，不要求胜方再报一次死亡");
            host.ReturnToRoster(); host.Select(0); host.MarkReady();
            That(host.CurrentPvpArena is null && host.GetState() == UserState.PvpTown,
                "换角清会话原房间指针但由服务精确收尾索引");
            That(await arena.DetachAsync(host) is [] && arena.Directory(host.ChannelEntry!.Identity).Length == 1,
                "换角后精确收尾空旧房间，不改变新选角上下文");
            var replacementOwner = owners.Acquire("arena-guest", () => { });
            using (replacementOwner)
            {
                var replacement = new GameSession(repository, towns, replacementOwner,
                    entry: new(new(1, 40), 24, "[pvp]"));
                replacement.Select(0); replacement.MarkReady();
                var fresh = arena.Create(replacement, "fresh", PvpArenaMode.Individual, 0)!;
                fresh.Member.Room.Commands.Release();
                That(!joined.Member.IsCurrent && await arena.DetachAsync(guest) is [] &&
                    fresh.Member.IsCurrent && replacementOwner.IsCurrent &&
                    arena.Directory(replacement.ChannelEntry!.Identity) is [{ Id: var id }] && id == fresh.View.Id,
                    "旧owner收尾仅移除原会话成员，保留新代数房间和租约");
                guest.End();
                That(await arena.DetachAsync(guest) is [] && fresh.Member.IsCurrent,
                    "旧连接重复清理不影响接管者");
                await arena.DetachAsync(replacement);
                That(replacement.GetState() == UserState.PvpTown && arena.Directory(replacement.ChannelEntry!.Identity).Length == 0,
                    "最后成员退出回城并回收房间索引");
            }
            var persisted = repository.LoadCharacter(hostOwner, 0);
            That(persisted.Revision == original.Revision && persisted.Position == original.Position,
                "房间生命周期不写角色位置或持久化版本");

            GameSession Session(AccountOwners.Lease owner, string name, byte channelType = 24)
            {
                repository.Create(owner, new(name, point, point) { Level = 85 });
                return new(repository, towns, owner, entry: new(new(1, 40), channelType, "[pvp]"));
            }
        }
        finally { TestFiles.DeleteDirectory(root, true); }
        Console.WriteLine($"PASS {Count - before} 正式决斗会话、准入、精确离场与接管隔离断言");
    }
}
