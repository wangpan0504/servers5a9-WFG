using DfoServer.Game.Accounts;
using DfoServer.Game.Caching;
using DfoServer.Game.Characters;
using DfoServer.Game.Pvp;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Towns;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class PvpMatchingSessionTests
{
    public static async Task RunCoreAsync()
    {
        PvpTotalRosterTests.RunCore();
        int before = Count;
        string root = Path.Combine(Path.GetTempPath(), "S5A9-matching-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var point = new Location(10, 4, 10, 10);
            var map = new TownMap("matching-fixture.map", [new(new(0, 0, 100, 100), [], [])], [], false, []);
            var towns = new TownCatalog(new() { [(10, 4)] = new(10, 4, 1, [], [], [], point, map, [], []) },
                point, point, new(0, [], []), [], new Dictionary<uint, uint[]>());
            var repository = new CharacterRepository(Path.Combine(root, "game.db"));
            using var memory = new GameCache();
            var owners = new AccountOwners(memory);
            PvpRatingSettlement Ratings() => new(repository, new PvpRatingCalculator(PvpRatingTests.Fixture()));
            var arena = new PvpArenaService(memory, PvpArenaCatalogTests.Fixture([0, 0, 0, 1]), repository, matchingCapacity: 2, ratings: Ratings());
            using var firstOwner = owners.Acquire("matching-first", () => { });
            using var secondOwner = owners.Acquire("matching-second", () => { });
            using var thirdOwner = owners.Acquire("matching-third", () => { });
            using var ordinaryOwner = owners.Acquire("matching-ordinary", () => { });
            using var wrongOwner = owners.Acquire("matching-wrong", () => { });
            var first = Session(firstOwner, "MatchFirst");
            var second = Session(secondOwner, "MatchSecond");
            var third = Session(thirdOwner, "MatchThird");
            var ordinary = Session(ordinaryOwner, "MatchOnly", grow: 0);
            var wrong = Session(wrongOwner, "MatchWrong", channelType: 22);
            That(arena.QueueOrdinary(first) is null, "未选角不能取得匹配票据");
            first.Select(0);
            That(arena.QueueOrdinary(first) is null, "未就绪不能进入等待队列");
            first.MarkReady();
            foreach (var session in new[] { second, third, ordinary, wrong }) { session.Select(0); session.MarkReady(); }
            var original = first.Selected!;
            That(arena.QueueOrdinary(first, true) is null && first.CurrentPvpMatching is null,
                "外部活动门禁拒绝时不安装匹配票据");
            That(arena.QueueOrdinary(ordinary) is null && arena.QueueOrdinary(wrong) is null,
                "普通准入不能替代匹配表，普通频道不能进入决斗匹配");

            var attempts = await Task.WhenAll(Task.Run(() => arena.QueueOrdinary(first)), Task.Run(() => arena.QueueOrdinary(first)));
            var initial = attempts[0] ?? throw new InvalidOperationException("Matching fixture was not admitted");
            That(ReferenceEquals(initial, attempts[1]) && initial.IsCurrent &&
                ReferenceEquals(first.CurrentPvpMatching, initial), "并发重复排队复用Session唯一票据");
            That(arena.Create(first, "room", PvpArenaMode.Individual, 0) is null &&
                first.BeginPvpTraining(_ => throw new Exception("Matching must exclude training")) is null &&
                !first.CanChangeTownParty, "等待匹配时不能同时建房、训练或组队");
            That(arena.QueueOrdinary(second) is not null && arena.QueueOrdinary(third) is null &&
                third.CurrentPvpMatching is null, "队列容量耗尽拒绝新票据，不留下半份会话成员");
            var channel = first.ChannelEntry!.Identity;
            var queued = arena.QueuedMatching(channel);
            That(queued.Length == 2 && ReferenceEquals(queued[0], initial) &&
                ReferenceEquals(queued[1], second.CurrentPvpMatching) && arena.QueuedMatching(new(1, 41)).Length == 0,
                "等待列表保持先后顺序并按真实频道隔离");
            That(!arena.CancelMatching(second, initial) && initial.IsCurrent,
                "局外会话不能取消他人票据");
            That(arena.CancelMatching(first, initial) && !arena.CancelMatching(first, initial) &&
                first.CurrentPvpMatching is null && !initial.IsCurrent && first.CanChangeTownParty,
                "取消精确票据一次即释放等待资格与活动互斥");
            var next = arena.QueueOrdinary(first)!;
            That(next.Token != initial.Token && !arena.CancelMatching(first, initial) && next.IsCurrent,
                "重新排队使用新token，旧取消不能移除新等待成员");
            That(arena.QueueOrdinary(first) == next && arena.QueuedMatching(channel).Length == 2,
                "满队列中的本人重复排队仍幂等，不挤出对方");

            first.ReturnToRoster(); first.Select(0); first.MarkReady();
            That(first.CurrentPvpMatching is null && !next.IsCurrent && arena.QueuedMatching(channel).Length == 1 &&
                arena.QueueOrdinary(third) is not null, "换角立即清唯一票据，过期索引回收后可复用容量");
            await arena.DetachAsync(first);
            That(arena.QueuedMatching(channel).Length == 2 && third.CurrentPvpMatching is { IsCurrent: true },
                "旧选角收尾不移除其他会话排队");
            arena.CancelMatching(third, third.CurrentPvpMatching!);

            var old = second.CurrentPvpMatching!;
            using var replacementOwner = owners.Acquire(secondOwner.Account, () => { });
            var replacement = new GameSession(repository, towns, replacementOwner,
                entry: new(new(1, 40), 24, "[pvp]"));
            replacement.Select(0); replacement.MarkReady();
            var fresh = arena.QueueOrdinary(replacement)!;
            That(!old.IsCurrent && fresh.IsCurrent && !arena.CancelMatching(second, old),
                "被接管旧owner不能取消或重写新票据");
            await arena.DetachAsync(second);
            second.End();
            That(fresh.IsCurrent && arena.QueuedMatching(channel) is [var remaining] && ReferenceEquals(remaining, fresh),
                "旧连接断线仅清原Session索引，保留接管者的等待成员");
            await arena.DetachAsync(replacement);
            That(replacement.CurrentPvpMatching is null && arena.QueuedMatching(channel).Length == 0,
                "当前连接收尾清Session与队列两处精确引用");
            var persisted = repository.LoadCharacter(firstOwner, 0);
            That(persisted.Revision == original.Revision && persisted.Position == original.Position &&
                persisted.Inventory.Gold == original.Inventory.Gold, "排队、取消、接管收尾不写角色资产、位置或版本");
            Preferences(first, third, replacement);
            await MatchingRooms();
            await Loads(first, third, replacement);
            await RatingTakeover();
            await MatchingDriver();

            async Task MatchingDriver()
            {
                using var oa = owners.Acquire("matching-driver-a", () => { });
                using var ob = owners.Acquire("matching-driver-b", () => { });
                var a = Session(oa, "DriverA"); var b = Session(ob, "DriverB");
                foreach (var session in new[] { a, b }) { session.Select(0); session.MarkReady(); }
                var service = new PvpArenaService(memory, PvpArenaCatalogTests.Fixture([0, 0, 0, 1]), repository, ratings: Ratings());
                var ta = service.QueueOrdinary(a)!; var tb = service.QueueOrdinary(b)!;
                var now = DateTimeOffset.UtcNow;
                await b.CommandGate.WaitAsync();
                try
                {
                    That(await service.PrepareNextOrdinaryMatchingAsync(now) is null && ta.Load is null && tb.Load is null &&
                        service.QueuedMatching(channel).SequenceEqual(new[] { ta, tb }),
                        "配对驱动遇忙命令不等待、不绑定半预约且保留双方排队顺序");
                }
                finally { b.CommandGate.Release(); }
                var entry = await service.PrepareNextOrdinaryMatchingAsync(now) ?? throw new InvalidOperationException("Matching driver did not pair");
                try
                {
                    That(entry.Room.Commands.CurrentCount == 0 && entry.Load.Members.SequenceEqual(new[] { ta, tb }) &&
                        entry.Ratings is [{ State.Rating: 900 }, { State.Rating: 900 }] &&
                        a.CommandGate.CurrentCount == 1 && b.CommandGate.CurrentCount == 1 &&
                        service.QueuedMatching(channel).Length == 0,
                        "驱动原子转换双人私有房间，捕获真实显示评分并归还连接门禁，初始化前保留房间门禁");
                    That(await service.PrepareNextOrdinaryMatchingAsync(now) is null,
                        "同一对票据只转换一次，驱动不复用已消费预约");
                    service.CancelMatchingRoom(entry.Room, now);
                }
                finally { entry.Room.Commands.Release(); }
            }

            async Task RatingTakeover()
            {
                using var oa = owners.Acquire("rating-takeover-a", () => { });
                using var ob = owners.Acquire("rating-takeover-b", () => { });
                var a = Session(oa, "RateTakeA"); var b = Session(ob, "RateTakeB");
                foreach (var session in new[] { a, b }) { session.Select(0); session.MarkReady(); }
                var service = new PvpArenaService(memory, PvpArenaCatalogTests.Fixture([0, 0, 0, 1]), repository, ratings: Ratings());
                var now = DateTimeOffset.UtcNow;
                var first = service.QueueOrdinary(a)!; var second = service.QueueOrdinary(b)!;
                var pair = service.PairOrdinary(first, second, now, TimeSpan.FromSeconds(30))!;
                var entry = service.PrepareOrdinaryMatching(pair, 0, now)!;
                var room = entry.Room;
                try
                {
                    service.CompleteMatchingLoad(a, pair.Id, now); service.CompleteMatchingLoad(b, pair.Id, now);
                    That(service.StartOrdinaryMatching(room, now).Length == 2 &&
                        room.Match!.Fighters.All(row => row.Rating is { Revision: 0, State.Rating: 900, State.Initialized: false }),
                        "匹配开战在双方owner下冻结原评分，不能等胜方已更新后再读取对手分");
                    var match = room.Match!;
                    foreach (var session in new[] { a, b })
                    {
                        service.Loaded(session, match.Id, now);
                        service.Connected(session, match.Id, Enumerable.Repeat(true, 8).ToArray(), now);
                    }
                }
                finally { room.Commands.Release(); }
                using var replacement = owners.Acquire(ob.Account, () => { });
                var next = new GameSession(repository, towns, replacement, entry: new(channel, 24, "[pvp]"));
                next.Select(0); next.MarkReady();
                That(service.QueueOrdinary(next) is null && service.Create(next, "blocked", PvpArenaMode.Individual, 0) is null &&
                    repository.ReadPvpRating(replacement, next.Selected!.Id) is null,
                    "接管的新会话在旧局未持久化时不能抢开下一局或提前改变冻结评分");
                await service.DetachAsync(b);
                var ticket = service.QueueOrdinary(next);
                That(ticket is not null && repository.ReadPvpRating(oa, a.Selected!.Id) is
                        { State.Rating: 1050, State.Experience: 1000, State.Played: 1 } &&
                    repository.ReadPvpRating(replacement, next.Selected!.Id) is { State.Rating: 950, State.Played: 1 },
                    "旧连接按精确成员弃权后，新owner先恢复冻结负分再进入新队列，双方独立到账不混用新分");
                That(service.CancelMatching(next, ticket!) && (await service.DetachAsync(a)).Length == 1 &&
                    a.Phase == SessionPhase.Town && service.QueuedMatching(channel).Length == 0,
                    "评分接管夹具清原房间和新票据，不遗留跨局状态");
            }

            async Task MatchingRooms()
            {
                using var oa = owners.Acquire("matching-room-a", () => { });
                using var ob = owners.Acquire("matching-room-b", () => { });
                using var oc = owners.Acquire("matching-room-c", () => { });
                var a = Session(oa, "MatchRoomA"); var b = Session(ob, "MatchRoomB"); var c = Session(oc, "MatchRoomC");
                foreach (var session in new[] { a, b, c }) { session.Select(0); session.MarkReady(); }
                var service = new PvpArenaService(memory, PvpArenaCatalogTests.Fixture([0, 0, 0, 1]), repository, ratings: Ratings());
                var now = DateTimeOffset.UtcNow;
                var duration = TimeSpan.FromSeconds(30);
                PvpMatchingLoadView Pair()
                {
                    var one = service.QueueOrdinary(a)!; var two = service.QueueOrdinary(b)!;
                    return service.PairOrdinary(one, two, now, duration)!;
                }
                PvpMatchingRoomEntry Enter(PvpMatchingLoadView load) => service.PrepareOrdinaryMatching(load, 0, now) ??
                    throw new InvalidOperationException("Matching room fixture was not admitted");
                var original = a.Selected!;
                var pair = Pair();
                That(service.PrepareOrdinaryMatching(pair, ushort.MaxValue, now) is null &&
                    service.PrepareOrdinaryMatching(pair, 0, now + duration) is null &&
                    pair.Members.All(row => row.IsCurrent) && a.CurrentPvpArena is null && b.CurrentPvpArena is null,
                    "非法地图或迟到入房原子拒绝，不消费任一等待票据");
                var entry = Enter(pair);
                var room = entry.Room;
                try
                {
                    That(a.CurrentPvpMatching is null && b.CurrentPvpMatching is null &&
                        a.Phase == SessionPhase.PvpRoom && b.Phase == SessionPhase.PvpRoom &&
                        ReferenceEquals(a.CurrentPvpArena!.Room, room) && ReferenceEquals(b.CurrentPvpArena!.Room, room) &&
                        room.Commands.CurrentCount == 0 && entry.Initial.Length == 2 &&
                        entry.Initial.All(row => row.Recipient.IsCurrent && row.Characters.Length == 2) &&
                        pair.Members.All(row => !row.IsCurrent) && pair.Members[0].Load!.IsCurrent,
                        "双账号转换同时消费原票据并安装真实成员，加载守卫改随房间，初始通知前持新房间门禁");
                    That(service.Directory(channel).Length == 0 && service.Find(channel, room.Id) is null &&
                        service.Join(c, room, false) is null && service.Join(c, room, true) is null &&
                        service.QueuedMatching(channel).Length == 0,
                        "匹配私有房间不进手动目录，猜编号和旁观不能加入，原队列索引已拆");
                    That(service.PrepareOrdinaryMatching(pair, 0, now) is null &&
                        !service.CancelMatching(a, pair.Members[0]) && !service.ExpireMatchingLoad(pair, now + duration) &&
                        a.CurrentPvpArena!.IsCurrent && b.CurrentPvpArena!.IsCurrent,
                        "入房后旧预约安装、排队取消和排队超时不能越过房间门禁清新成员");
                    That(service.SetReady(a, true).Length == 0 && service.Move(a, 2, 1, false).Length == 0 &&
                        service.Configure(a, PvpArenaMode.Team, 1).Length == 0 && service.Start(a, now).Length == 0 &&
                        service.StartOrdinaryMatching(room, now).Length == 0 && room.Match is null,
                        "预加载前不能从手动准备、改席、改图或开战绕过匹配屏障");
                    That(service.CompleteMatchingLoad(c, pair.Id, now) is null &&
                        service.CompleteMatchingLoad(a, pair.Id + 1, now) is null &&
                        service.CompleteMatchingLoad(a, pair.Id, now) is { Phase: PvpMatchingLoadPhase.Loading } &&
                        service.StartOrdinaryMatching(room, now).Length == 0,
                        "真实房间仍校验原标识与原成员，一人完成不能提前开战");
                    That(service.CompleteMatchingLoad(b, pair.Id, now) is { Phase: PvpMatchingLoadPhase.Ready } &&
                        service.CancelMatchingRoom(room, now + duration, expiredOnly: true).Length == 0 &&
                        (await service.MaintainMatchingAsync(now + duration)).Length == 0,
                        "双方完成预加载后，旧期限不再取消就绪房间");
                    var started = service.StartOrdinaryMatching(room, now);
                    That(started.Length == 2 && started.All(row => row.Kind == PvpArenaNoticeKind.Start &&
                        row.Characters.Length == 2 && row.View.MatchPhase == PvpArenaMatchPhase.Loading) &&
                        pair.Members[0].Load!.Phase == PvpMatchingLoadPhase.Started &&
                        service.StartOrdinaryMatching(room, now).Length == 0 &&
                        service.CompleteMatchingLoad(a, pair.Id, now) is null &&
                        service.CancelMatchingRoom(room, now + duration).Length == 0,
                        "匹配预加载只启动一局，后续仍有正式加载与P2P屏障，旧取消不拆开战房间");
                    var match = room.Match!;
                    service.Loaded(a, match.Id, now); service.Loaded(b, match.Id, now);
                    service.Connected(a, match.Id, Enumerable.Repeat(true, 8).ToArray(), now);
                    service.Connected(b, match.Id, Enumerable.Repeat(true, 8).ToArray(), now);
                    service.Death(b, match.Id, b.Selected!.Id, a.Selected!.Id);
                    var result = service.RecordResult(a, now)!;
                    That(result.RatingMode == PvpRatingMode.Matching && result.Participants.All(row => row.Rating is
                            { Revision: 0, State.Rating: 900 }) &&
                        repository.ReadPvpRating(oa, a.Selected!.Id) is { State.Rating: 1050, State.Experience: 1000 } &&
                        repository.ReadPvpRating(ob, b.Selected!.Id) is null,
                        "整局冻结双方赛前分，报告者只结算自身，不能在单账号锁里改对手");
                    service.RecordResult(b, now);
                    That(repository.ReadPvpRating(ob, b.Selected!.Id) is { Revision: 1, State.Rating: 950 } &&
                        ReferenceEquals(service.RecordResult(a, now), result) &&
                        repository.ReadPvpRating(oa, a.Selected.Id)!.Revision == 1,
                        "胜方先到账后，负方仍按原900快照定位为950；重复结果不增加评分revision");
                    foreach (var player in new[] { a, b })
                    {
                        var delivery = service.CaptureResultDelivery(player, match.Id)!;
                        That(service.ReturnResult(player, match.Id).Length == 0 && service.CanPublishResultDelivery(delivery) &&
                            service.ConfirmResultQueued(delivery) && service.ReturnResult(player, match.Id).Length == 2,
                            "匹配结果须逐本人实际回执确认入队，未发布者不能提前返回");
                    }
                    That(room.Match is null && service.StartOrdinaryMatching(room, now).Length == 0 &&
                        pair.Members[0].Load!.Phase == PvpMatchingLoadPhase.Cancelled &&
                        a.CurrentPvpArena is null && b.CurrentPvpArena is null,
                        "结果返回释放旧局后也不能重用已消费的匹配加载预约再次开局");
                    service.Leave(a); service.Leave(b);
                }
                finally { room.Commands.Release(); }
                That(a.Phase == SessionPhase.Town && b.Phase == SessionPhase.Town &&
                    ReferenceEquals(a.Selected, original) && service.Directory(channel).Length == 0,
                    "匹配转换及离场不改角色数据，空私有房间已回收");

                pair = Pair(); entry = Enter(pair); room = entry.Room;
                try
                {
                    That(service.CancelMatchingRoom(room, now + duration - TimeSpan.FromTicks(1), expiredOnly: true).Length == 0 &&
                        service.CompleteMatchingLoad(a, pair.Id, now + duration) is null,
                        "房间期限到达前不超时，恰到截止的确认不能推进");
                    var expired = service.CancelMatchingRoom(room, now + duration, expiredOnly: true);
                    That(expired.Length == 2 && expired.All(row => row.Departure is not null &&
                        row.Recipient.Actor.Session.CanPublishPvpArenaDeparture(row.Departure)) &&
                        a.CurrentPvpArena is null && b.CurrentPvpArena is null &&
                        pair.Members[0].Load!.Phase == PvpMatchingLoadPhase.Cancelled &&
                        service.CancelMatchingRoom(room, now + duration).Length == 0,
                        "房间超时清双方原成员一次，离场通知绑定原选角与版本");
                    var freshWait = service.QueueOrdinary(a)!;
                    var oldDeparture = expired.Single(notice => ReferenceEquals(notice.Recipient.Actor.Session, a)).Departure!;
                    That(freshWait.IsCurrent && !a.CanPublishPvpArenaDeparture(oldDeparture),
                        "重新排队后丢弃上一私有房间的迟到离场通知，不能关闭新匹配界面");
                    service.CancelMatching(a, freshWait);
                }
                finally { room.Commands.Release(); }

                pair = Pair(); entry = Enter(pair); room = entry.Room;
                try
                {
                    That((await service.MaintainMatchingAsync(now + duration)).Length == 0 &&
                        a.CurrentPvpArena!.IsCurrent && b.CurrentPvpArena!.IsCurrent,
                        "维护扫描非阻塞跳过繁忙房间，不越过其命令门禁清成员");
                }
                finally { room.Commands.Release(); }
                That((await service.MaintainMatchingAsync(now + duration - TimeSpan.FromTicks(1))).Length == 0 &&
                    a.CurrentPvpArena!.IsCurrent && b.CurrentPvpArena!.IsCurrent,
                    "周期维护在截止前保留真实私有房间");
                var maintained = await service.MaintainMatchingAsync(now + duration);
                That(maintained.Length == 2 && maintained.All(notice => notice.Kind == PvpArenaNoticeKind.MatchingCancelled &&
                    notice.Departure is not null) && a.CurrentPvpArena is null && b.CurrentPvpArena is null &&
                    (await service.MaintainMatchingAsync(now + duration)).Length == 0,
                    "无人再发请求时周期维护在截止回收双方，并且只生成一次原房间取消通知");
                pair = Pair();
                That((await service.MaintainMatchingAsync(now + duration)).Length == 0 &&
                    a.CurrentPvpMatching is null && b.CurrentPvpMatching is null &&
                    pair.Members[0].Load!.Phase == PvpMatchingLoadPhase.Cancelled,
                    "尚未转换入房的超时预约同样被周期回收，不伪造房间取消通知");

                pair = Pair();
                var race = await Task.WhenAll(Task.Run(() => service.PrepareOrdinaryMatching(pair, 0, now)),
                    Task.Run(() => { service.CancelMatching(b, pair.Members[1]); return (PvpMatchingRoomEntry?)null; }));
                if (race[0] is { } winner)
                {
                    try { service.CancelMatchingRoom(winner.Room, now); }
                    finally { winner.Room.Commands.Release(); }
                }
                That(a.CurrentPvpMatching is null && b.CurrentPvpMatching is null &&
                    a.CurrentPvpArena is null && b.CurrentPvpArena is null && service.QueuedMatching(channel).Length == 0,
                    "原子入房与原排队取消竞争后无单边票据或房间成员");

                pair = Pair(); entry = Enter(pair); room = entry.Room; room.Commands.Release();
                using var takeover = owners.Acquire(ob.Account, () => { });
                var newer = new GameSession(repository, towns, takeover, entry: new(new(1, 40), 24, "[pvp]"));
                newer.Select(0); newer.MarkReady(); var fresh = service.QueueOrdinary(newer)!;
                await service.DetachAsync(b);
                That(a.CurrentPvpArena is null && a.Phase == SessionPhase.Town && !pair.Members[0].Load!.IsCurrent &&
                    fresh.IsCurrent && newer.CurrentPvpArena is null &&
                    service.CompleteMatchingLoad(newer, pair.Id, now) is null &&
                    service.QueuedMatching(channel) is [var remaining] && ReferenceEquals(remaining, fresh),
                    "预加载中接管使原房间双方失效，旧连接收尾保留接管者新票据");
                service.CancelMatching(newer, fresh);
            }

            void Preferences(GameSession a, GameSession b, GameSession c)
            {
                var service = new PvpArenaService(memory, PvpArenaCatalogTests.Fixture([0, 0, 0, 1]), repository, ratings: Ratings());
                var defaults = PvpMatchingPreferences.Default;
                var ban = defaults with { ExclusionsEnabled = true,
                    Exclusions = [new(0, 1), new(255, 255), new(255, 255), new(255, 255)] };
                That(ban.Excludes(a.Selected!) && ban.Excludes(a.Selected! with { GrowType = 17 }) &&
                    !ban.Excludes(a.Selected! with { Job = 1 }) && !ban.Excludes(a.Selected! with { GrowType = 2 }) &&
                    !defaults.Excludes(a.Selected!), "筛选精确匹配职业及基础转职，觉醒高位不改变分支，FF空项不是通配");
                foreach (var invalid in new[] { defaults with { Option = 2 }, defaults with { Final = 2 },
                    defaults with { Exclusions = [] }, defaults with { Exclusions = default },
                    defaults with { Exclusions = [new(0, 255), new(255, 255), new(255, 255), new(255, 255)] },
                    defaults with { Exclusions = [new(16, 1), new(255, 255), new(255, 255), new(255, 255)] },
                    defaults with { Exclusions = [new(0, 6), new(255, 255), new(255, 255), new(255, 255)] } })
                    That(service.QueueOrdinary(a, invalid) is null && a.CurrentPvpMatching is null,
                        "非法开关、非四项及半空/越界筛选在安装票据前拒绝");
                var one = defaults with { Option = 1 };
                var ta = service.QueueOrdinary(a, one)!;
                That(ReferenceEquals(service.QueueOrdinary(a, one with { Exclusions = [..one.Exclusions] }), ta) &&
                    service.QueueOrdinary(a, defaults) is null && ReferenceEquals(a.CurrentPvpMatching, ta),
                    "相同值偏好重复请求幂等，选项变化不能静默替换原排队票据");
                var tb = service.QueueOrdinary(b)!; var tc = service.QueueOrdinary(c)!;
                var now = DateTimeOffset.UtcNow;
                var duration = TimeSpan.FromSeconds(30);
                var pair = service.PairNextOrdinary(tc, now, duration)!;
                That(ReferenceEquals(pair.Members[0], tb) && ReferenceEquals(pair.Members[1], tc) && ta.Load is null &&
                    service.QueuedMatching(channel) is [var unmatched] && ReferenceEquals(unmatched, ta),
                    "候选按FIFO跳过开关不合者，双方绑定最早兼容者，未配成者顺序不变");
                service.CancelMatching(b, tb); service.CancelMatching(a, ta);
                ta = service.QueueOrdinary(a, ban)!; tb = service.QueueOrdinary(b)!;
                That(service.PairNextOrdinary(tb, now, duration) is null && service.PairOrdinary(tb, ta, now, duration) is null &&
                    ta.IsCurrent && tb.IsCurrent && service.QueuedMatching(channel).Length == 2,
                    "双方筛选对称生效，被任意一方排除都不移除或重排原票据");
                service.CancelMatching(a, ta); service.CancelMatching(b, tb);
                ta = service.QueueOrdinary(a, ban with { ExclusionsEnabled = false })!;
                tb = service.QueueOrdinary(b, defaults with { Final = 1 })!;
                That(service.PairNextOrdinary(tb, now, duration) is null,
                    "第二开关独立隔离候选，不与第一开关合并或忽略");
                service.CancelMatching(b, tb); tb = service.QueueOrdinary(b)!;
                That(service.PairNextOrdinary(tb, now, duration) is not null,
                    "关闭排除开关后保留的原筛选项不生效，不强求双方排除名单完全相同");
                service.CancelMatching(a, ta);
            }

            async Task Loads(GameSession a, GameSession b, GameSession c)
            {
                var service = new PvpArenaService(memory, PvpArenaCatalogTests.Fixture([0, 0, 0, 1]), repository, ratings: Ratings());
                var now = DateTimeOffset.UtcNow;
                var timeout = TimeSpan.FromSeconds(30);
                PvpMatchingTicket Queue(GameSession session) => service.QueueOrdinary(session) ??
                    throw new InvalidOperationException("Matching load fixture was not admitted");
                var ta = Queue(a); var tb = Queue(b); var tc = Queue(c);
                using var distantOwner = owners.Acquire("matching-distant", () => { });
                var distant = Session(distantOwner, "MatchFar", channel: 41);
                distant.Select(0); distant.MarkReady();
                var far = Queue(distant);
                That(service.PairOrdinary(ta, ta, now, timeout) is null &&
                    service.PairOrdinary(ta, far, now, timeout) is null &&
                    service.PairOrdinary(ta, tb, now, TimeSpan.Zero) is null &&
                    service.PairOrdinary(ta, tb, now, TimeSpan.FromMinutes(6)) is null,
                    "同票据、不同频道与非法期限不能安装加载预约");
                service.CancelMatching(distant, far);
                a.UpdateProgress(new(CharacterProgressField.Grow, firstOwner.Account, a.Selected!.Name, 0, 0));
                That(service.PairOrdinary(ta, tb, now, timeout) is null && ta.Load is null && tb.Load is null,
                    "排队后职业资格改变，配对重验当前快照，不沿用排队时的转职");
                a.UpdateProgress(new(CharacterProgressField.Grow, firstOwner.Account, a.Selected.Name, 1, 0));
                var attempts = await Task.WhenAll(Task.Run(() => service.PairOrdinary(ta, tb, now, timeout)),
                    Task.Run(() => service.PairOrdinary(tc, ta, now, timeout)));
                var pair = attempts.Single(row => row is not null)!;
                var other = pair.Members.Single(row => !ReferenceEquals(row, ta));
                var spare = ReferenceEquals(other, tb) ? tc : tb;
                That(ta.Load is not null && ReferenceEquals(ta.Load, other.Load) && spare.Load is null &&
                    service.QueuedMatching(channel) is [var waiting] && ReferenceEquals(waiting, spare),
                    "两个并发候选最多取得同一票据一次，双方原子绑定，未中选者继续等待");
                That(pair.Phase == PvpMatchingLoadPhase.Loading && pair.Loaded.IsEmpty && pair.Id != 0 &&
                    a.CurrentPvpMatching == ta && !a.CanChangeTownParty &&
                    ReferenceEquals(pair.Characters[pair.Members.IndexOf(ta)].Character, a.Selected) &&
                    !ReferenceEquals(ta.Actor.Character, a.Selected) &&
                    service.Create(a, "room", PvpArenaMode.Individual, 0) is null,
                    "配对冻结当前完整角色并保留Session唯一票据及活动互斥，不沿用排队快照或提前开战");
                That(service.PairOrdinary(ta, spare, now, timeout) is null &&
                    service.CompleteMatchingLoad(spare.Actor.Session, pair.Id, now) is null &&
                    service.CompleteMatchingLoad(a, pair.Id + 1, now) is null,
                    "已配对成员不能再配对，局外与错误标识确认不改变屏障");
                var loaded = service.CompleteMatchingLoad(a, pair.Id, now)!;
                That(loaded.Phase == PvpMatchingLoadPhase.Loading && loaded.Loaded.SequenceEqual(new[] { ta.Token }) &&
                    pair.Loaded.IsEmpty && service.CompleteMatchingLoad(a, pair.Id, now) is null,
                    "首次确认只推进本人一次，冻结旧视图不随新屏障变化");
                That(!service.ExpireMatchingLoad(pair, now + timeout - TimeSpan.FromTicks(1)),
                    "截止前不能以超时取消加载");
                var ready = service.CompleteMatchingLoad(other.Actor.Session, pair.Id, now)!;
                That(ready.Phase == PvpMatchingLoadPhase.Ready && ready.Loaded.Length == 2 &&
                    !service.ExpireMatchingLoad(pair, now + timeout) &&
                    service.CompleteMatchingLoad(a, pair.Id, now) is null,
                    "双方完成才进入就绪，旧超时快照不能撤销已就绪预约或二次推进");
                That(service.CancelMatching(a, ta) && a.CurrentPvpMatching is null &&
                    other.Actor.Session.CurrentPvpMatching is null && ta.Load!.Phase == PvpMatchingLoadPhase.Cancelled &&
                    !service.CancelMatching(a, ta) && spare.IsCurrent,
                    "取消配对精确清理双方原票据一次，未参与者不受影响");
                service.CancelMatching(spare.Actor.Session, spare);

                ta = Queue(a); tb = Queue(b);
                var timed = service.PairOrdinary(ta, tb, now, timeout)!;
                That(service.CompleteMatchingLoad(a, timed.Id, now + timeout) is null &&
                    service.ExpireMatchingLoad(timed, now + timeout) && a.CurrentPvpMatching is null && b.CurrentPvpMatching is null,
                    "恰到截止拒绝迟到确认，超时释放双方全部占用");
                ta = Queue(a); tb = Queue(b);
                var deadlineRace = service.PairOrdinary(ta, tb, now, timeout)!;
                service.CompleteMatchingLoad(a, deadlineRace.Id, now);
                var deadlineResults = await Task.WhenAll(
                    Task.Run(() => service.CompleteMatchingLoad(b, deadlineRace.Id, now) is not null),
                    Task.Run(() => service.ExpireMatchingLoad(deadlineRace, now + timeout)));
                That(deadlineResults[0] != deadlineResults[1] &&
                    (deadlineResults[1] ? a.CurrentPvpMatching is null && b.CurrentPvpMatching is null :
                        ta.Load!.Phase == PvpMatchingLoadPhase.Ready && ta.IsCurrent && tb.IsCurrent),
                    "最后确认与超时竞争只提交一种结果，不能就绪后又被旧期限清除");
                if (deadlineResults[0]) service.CancelMatching(a, ta);
                ta = Queue(a); tb = Queue(b);
                var after = service.PairOrdinary(ta, tb, now, timeout)!;
                That(after.Id != timed.Id && !service.ExpireMatchingLoad(timed, now + timeout) &&
                    service.CompleteMatchingLoad(a, timed.Id, now) is null && ta.IsCurrent && tb.IsCurrent,
                    "重新配对标识不复用，旧确认及旧超时不能作用于下一次预约");
                a.ReturnToRoster(); a.Select(0); a.MarkReady();
                That(service.QueuedMatching(channel).Length == 0 && b.CurrentPvpMatching is null &&
                    service.CompleteMatchingLoad(b, after.Id, now) is null,
                    "换角使原配对失效，扫描释放有效对方，拒绝旧加载确认");

                ta = Queue(a); tb = Queue(b);
                var changed = service.PairOrdinary(ta, tb, now, timeout)!;
                b.UpdateProgress(new(CharacterProgressField.Level, thirdOwner.Account, b.Selected!.Name, 86, 0));
                That(service.CompleteMatchingLoad(a, changed.Id, now) is null &&
                    service.QueuedMatching(channel).Length == 0 && a.CurrentPvpMatching is null && b.CurrentPvpMatching is null &&
                    changed.Characters[1].Character.Level != b.Selected!.Level,
                    "预加载中角色快照改变使双方预约失效，不以新属性覆盖已经冻结的显示数据");

                ta = Queue(a); tb = Queue(b);
                var oldPair = service.PairOrdinary(ta, tb, now, timeout)!;
                using var takeover = owners.Acquire(thirdOwner.Account, () => { });
                var newer = new GameSession(repository, towns, takeover, entry: new(new(1, 40), 24, "[pvp]"));
                newer.Select(0); newer.MarkReady();
                var fresh = Queue(newer);
                await service.DetachAsync(b);
                b.End();
                That(a.CurrentPvpMatching is null && fresh.IsCurrent &&
                    service.QueuedMatching(channel) is [var only] && ReferenceEquals(only, fresh) &&
                    service.CompleteMatchingLoad(newer, oldPair.Id, now) is null,
                    "接管及旧连接收尾取消原配对，保留接管者新票据，不借旧owner写入");
                service.CancelMatching(newer, fresh);

                ta = Queue(a); tc = Queue(c);
                var racing = service.PairOrdinary(ta, tc, now, timeout)!;
                var results = await Task.WhenAll(Task.Run(() => service.CompleteMatchingLoad(a, racing.Id, now) is not null),
                    Task.Run(() => service.CancelMatching(c, tc)));
                That(results[1] && a.CurrentPvpMatching is null && c.CurrentPvpMatching is null &&
                    service.QueuedMatching(channel).Length == 0 && service.CompleteMatchingLoad(a, racing.Id, now) is null,
                    "完成与取消并发后不留下单边票据或可发布的旧预约");
            }

            GameSession Session(AccountOwners.Lease owner, string name, byte grow = 1, byte channelType = 24, byte channel = 40)
            {
                repository.Create(owner, new(name, point, point) { Level = 85, GrowType = grow });
                return new(repository, towns, owner, entry: new(new(1, channel), channelType, "[pvp]"));
            }
        }
        finally { TestFiles.DeleteDirectory(root, true); }
        Console.WriteLine($"PASS {Count - before} 匹配等待票据、双账号配对、加载与接管隔离断言");
    }
}
