using System.Collections.Immutable;
using DfoServer.Game.Pvp;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class PvpArenaTests
{
    public static void RunCore()
    {
        int checks = Count;
        var now = DateTimeOffset.UnixEpoch;
        var room = new PvpArenaRoom(0xABCD, 300, "决斗", PvpArenaMode.Team, 2);
        var first = room.Join("z", 101, 0)!;
        var second = room.Join("a", 102, 1)!;
        That(room.Join("z", 103, 1) is null && room.Join("b", 102, 1) is null, "房间拒绝同账号或同角色重复占席");
        That(room.Start(first.Token, now, TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(1)) is null,
            "未准备不能开战");
        That(!room.SetSeatClosed(second.Token, 7, true) && room.SetSeatClosed(first.Token, 7, true), "只有房主能关闭空席");
        That(room.SetReady(second.Token, true) && room.Configure(first.Token, PvpArenaMode.Team, 3) &&
            !room.Member(second.Token)!.Ready, "修改地图清除成员准备，防止用旧配置开战");
        room.SetReady(second.Token, true);
        var match = room.Start(first.Token, now, TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(1))!;
        That(match is not null && room.IsPlaying && !room.Member(second.Token)!.Ready &&
            room.Start(first.Token, now, TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(1)) is null,
            "开战冻结本局并消耗准备，重复开战不另建实例");
        That(!room.Configure(first.Token, PvpArenaMode.Individual, 4) && !room.Move(second.Token, 5, 0, false) &&
            room.Join("late", 104, 0) is null, "加载期不能换玩法、挪参战席或追加参战者");
        var spectator = room.Join("observer", 105, 0, true)!;
        That(spectator is not null && match!.Fighters.Length == 2, "观战加入不污染本局参战表");
        That(!match!.MarkLoaded(Guid.NewGuid(), first.Token, now) && !match.MarkConnected(match.Id, spectator!.Token, now),
            "加载屏障拒绝旧局及观战报告");
        That(match.MarkLoaded(match.Id, first.Token, now) && !match.MarkLoaded(match.Id, first.Token, now) &&
            match.MarkLoaded(match.Id, second.Token, now) && match.MarkConnected(match.Id, first.Token, now) &&
            match.Phase == PvpArenaMatchPhase.Loading, "重复加载不重复计数，全部加载仍须等P2P屏障");
        That(!match.ReportDeath(match.Id, first.Token, second.Token) && match.MarkConnected(match.Id, second.Token, now) &&
            match.Phase == PvpArenaMatchPhase.Fighting, "屏障完成才接受战斗报告");
        That(!match.ResolveTimeout(match.Id, first.Token, now.AddSeconds(59)), "客户端超时报告不能提前结束战斗");
        That(match.ReportDeath(match.Id, second.Token, first.Token) && match.Result!.Standings.Single(row =>
            row.Fighter.Membership == first.Token).Outcome == PvpArenaOutcome.Win &&
            !match.ReportDeath(match.Id, second.Token, first.Token), "死亡结束本局且重复报告不重结算");
        That(room.Start(first.Token, now, TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(1)) is null,
            "上一局准备不能复用于下一局");
        room.SetReady(second.Token, true);
        That(room.Start(first.Token, now, TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(1)) is null &&
            !room.EndResult(first.Token, match.Id), "未持久提交的结束局不能由准备或返回请求替换");
        var record = match.CaptureRecord(room.Instance, room.Channel, now)!;
        match.InstallRecord(record);
        That(!room.EndResult(first.Token, match.Id) && !match.ConfirmResultQueued(match.Id, first.Token,
            Receipt(match, first.Token)), "仅整局事实已保存，未安装本人经验回执不能确认发布或返回");
        var firstReceipt = Receipt(match, first.Token);
        Reject<InvalidOperationException>(() => match.InstallRatingReceipt(first.Token,
            firstReceipt with { Outcome = PvpArenaOutcome.Lose }), "拒绝与冻结胜负不一致的评分回执");
        match.InstallRatingReceipt(first.Token, firstReceipt);
        match.InstallRatingReceipt(first.Token, firstReceipt with { });
        Reject<InvalidOperationException>(() => match.InstallRatingReceipt(first.Token,
            firstReceipt with { Revision = firstReceipt.Revision + 1 }), "重复安装不能覆盖本人的原评分回执");
        That(ReferenceEquals(match.RatingReceipt(first.Token), firstReceipt) && !room.EndResult(first.Token, match.Id) &&
            !match.ConfirmResultQueued(Guid.NewGuid(), first.Token, firstReceipt) &&
            !match.ConfirmResultQueued(match.Id, second.Token, firstReceipt) &&
            !match.ConfirmResultQueued(match.Id, first.Token, firstReceipt with { }) &&
            !match.ConfirmResultQueued(match.Id, spectator!.Token, null!),
            "到账但未入队仍拒绝返回，发布须绑定原局原成员和实际安装回执");
        That(match.ConfirmResultQueued(match.Id, first.Token, firstReceipt, now) &&
            !match.ConfirmResultQueued(match.Id, first.Token, firstReceipt), "完整结果入队只确认一次");
        That(!room.ExpireResult(now.AddSeconds(31)) && room.Match == match,
            "即使结果期限已到，另一参战者未收到入队确认也不能自动返回");
        That(!room.EndResult(first.Token, Guid.NewGuid()) && room.EndResult(first.Token, match.Id) &&
            !room.EndResult(first.Token, match.Id) && room.Match == match,
            "结果返回拒绝旧局和重复成员，单方返回不能清掉双方状态");
        That(!room.EndResult(second.Token, match.Id), "另一原参战者不能复用首人的到账或发布状态");
        QueueResult(match, second.Token);
        That(room.EndResult(second.Token, match.Id) && room.Match is null &&
            !room.Member(second.Token)!.Ready && !room.EndResult(second.Token, match.Id),
            "原参战者全部返回后回收本局并清准备，不沿用上一局已保存状态");
        room.SetReady(second.Token, true);
        var exitMatch = room.Start(first.Token, now, TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(1))!;
        exitMatch.Depart(exitMatch.Id, second.Token);
        exitMatch.InstallRecord(exitMatch.CaptureRecord(room.Instance, room.Channel, now)!);
        QueueResult(exitMatch, first.Token);
        That(!exitMatch.CanReturnResult(second.Token) && !exitMatch.ConfirmResultQueued(match.Id, first.Token, firstReceipt),
            "新局不继承上一局发布标记，旧回执不能确认新局结果");
        That(room.EndResult(first.Token, exitMatch.Id) && room.Match == exitMatch,
            "返回仍须等尚在房间的另一原参战者");
        room.Leave(second.Token);
        That(room.Match is null && room.Join("a", 102, 1) is { } rejoined && rejoined.Token != second.Token,
            "最后未返回者离场后释放结果屏障，重新加入获得新成员身份");
        second = room.View().Members.Single(row => row.Account == "a");
        var departed = room.Leave(first.Token);
        That(departed?.Token == first.Token && room.View().Members.All(row => row.Token != first.Token),
            "退出从原房间移除精确成员");
        That(room.Leader == second.Token && room.Leave(first.Token) is null, "房主退出按席位移交，旧token重复清理无副作用");

        var relayMembers = Enumerable.Range(0, 4).Select(index => new PvpArenaFighter(Guid.NewGuid(),
            (ushort)(200 + index), (byte)(7 - index), (byte)(index % 2))).ToImmutableArray();
        var relay = Ready(PvpArenaMode.Relay, relayMembers, now);
        That(relay.IsActive(relayMembers[0].Membership) && !relay.IsActive(relayMembers[2].Membership),
            "擂台按冻结顺序派人，不按席位重排已选出场顺序");
        That(!relay.ReportDeath(relay.Id, relayMembers[0].Membership, relayMembers[2].Membership),
            "同队及候场角色不能作为击杀者");
        That(relay.ReportDeath(relay.Id, relayMembers[0].Membership, relayMembers[1].Membership) &&
            relay.Result is null && relay.IsActive(relayMembers[2].Membership), "擂台一人阵亡后交给同队下一人");
        That(relay.Depart(relay.Id, relayMembers[2].Membership) && relay.Result is { Forfeit: true } &&
            relay.Result.Standings.Count(row => row.Outcome == PvpArenaOutcome.Win) == 2,
            "最后参战者掉线按整队结果结束，保留弃权事实");
        var timed = Ready(PvpArenaMode.Individual, relayMembers[..2], now);
        That(!timed.ResolveTimeout(timed.Id, Guid.NewGuid(), now.AddMinutes(2)) &&
            timed.ResolveTimeout(timed.Id, null, now.AddMinutes(2)) &&
            timed.Result!.Standings.All(row => row.Outcome == PvpArenaOutcome.Draw), "超时结果拒绝局外胜者并支持已确认平局");
        var reported = Ready(PvpArenaMode.Individual, relayMembers[..2], now);
        uint[] values = new uint[8]; values[7] = 100; values[6] = 20;
        That(!reported.ReportTimeout(reported.Id, relayMembers[0].Membership, values, now.AddSeconds(59)) &&
            !reported.ReportTimeout(reported.Id, relayMembers[1].Membership, values, now.AddMinutes(1)),
            "超时报告须到原战斗期限且由最后最大值席发送，报告者不等于胜者");
        values[0] = 1;
        That(!reported.ReportTimeout(reported.Id, relayMembers[0].Membership, values, now.AddMinutes(1)), "空席非零报告拒绝");
        values[0] = 0; values[7] = 0x80000000;
        That(!reported.ReportTimeout(reported.Id, relayMembers[0].Membership, values, now.AddMinutes(1)), "超出有符号累加边界拒绝");
        values[7] = 100;
        That(reported.ReportTimeout(reported.Id, relayMembers[0].Membership, values, now.AddMinutes(1)) &&
            reported.Result!.Standings.Single(row => row.Fighter.Membership == relayMembers[1].Membership).Outcome == PvpArenaOutcome.Win &&
            !reported.ReportTimeout(reported.Id, relayMembers[0].Membership, values, now.AddMinutes(1)),
            "按兼容最小值判胜，不把最大值报告者当胜者，重复报告无效");
        var averageFighters = Enumerable.Range(0, 4).Select(at => new PvpArenaFighter(Guid.NewGuid(),
            (ushort)(400 + at), (byte)at, at == 1 ? (byte)1 : (byte)0)).ToImmutableArray();
        var average = Ready(PvpArenaMode.Team, averageFighters, now);
        That(average.ReportTimeout(average.Id, averageFighters[1].Membership, [80, 100, 80, 80, 0, 0, 0, 0], now.AddMinutes(1)) &&
            average.Result!.Standings.Count(row => row.Outcome == PvpArenaOutcome.Win) == 3,
            "队伍按平均而非总值比较，三人240总值仍胜一人100总值");
        var equal = Ready(PvpArenaMode.Individual, relayMembers[..2], now);
        That(!equal.ReportTimeout(equal.Id, relayMembers[1].Membership, new uint[8], now.AddMinutes(1)) &&
            equal.ReportTimeout(equal.Id, relayMembers[0].Membership, new uint[8], now.AddMinutes(1)) &&
            equal.Result!.Standings.All(row => row.Outcome == PvpArenaOutcome.Draw), "同值按最高席报告并按兼容平局收尾");
        var silent = Ready(PvpArenaMode.Individual, relayMembers[..2], now);
        That(!silent.HasExpiredClock(now.AddSeconds(74)) && !silent.ExpireClock(now.AddSeconds(74)) &&
            silent.ExpireClock(now.AddSeconds(75)) && silent.Result!.Standings.All(row => row.Outcome == PvpArenaOutcome.Draw) &&
            !silent.ExpireClock(now.AddMinutes(2)), "无人报告宽限15秒后一次性平局，不猜HP也不永久卡局");
        var loading = new PvpArenaMatch(PvpArenaMode.Individual, relayMembers[..2], now,
            TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(1));
        That(!loading.ExpireLoading(loading.Id, now.AddSeconds(9)) &&
            !loading.MarkLoaded(loading.Id, relayMembers[0].Membership, now.AddSeconds(10)) &&
            loading.Result!.Standings.All(row => row.Outcome == PvpArenaOutcome.Cancelled), "加载截止取消整局，不产生虚假胜负");
        var unattended = new PvpArenaRoom(10, 300, "返回", PvpArenaMode.Individual, 0);
        var ua = unattended.Join("ua", 501, 0)!; var ub = unattended.Join("ub", 502, 1)!;
        unattended.SetReady(ub.Token, true);
        var ending = unattended.Start(ua.Token, now, TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(1))!;
        ending.ExpireLoading(ending.Id, now.AddSeconds(10));
        ending.InstallRecord(ending.CaptureRecord(unattended.Instance, unattended.Channel, now.AddSeconds(10))!);
        foreach (var fighter in ending.Fighters)
        {
            var receipt = Receipt(ending, fighter.Membership);
            ending.InstallRatingReceipt(fighter.Membership, receipt);
            ending.ConfirmResultQueued(ending.Id, fighter.Membership, receipt, now.AddSeconds(10));
        }
        That(!unattended.ExpireResult(now.AddSeconds(39)) && unattended.ExpireResult(now.AddSeconds(40)) &&
            unattended.Match is null && !unattended.ExpireResult(now.AddSeconds(40)),
            "完整结果全部入队后30秒自动回房一次，取消局同样可解锁下一局");
        var full = new PvpArenaRoom(9, 300, "八人", PvpArenaMode.Individual, 2);
        foreach (int index in Enumerable.Range(0, 8)) That(full.Join("p" + index, (ushort)(300 + index), 0) is not null, "八席均可占用");
        That(full.Join("overflow", 400, 0) is null && full.View().Members.Length == 8, "房间容量严格有界");
        Console.WriteLine($"PvP arena domain tests passed: {Count - checks}");
    }

    static PvpArenaMatch Ready(PvpArenaMode mode, ImmutableArray<PvpArenaFighter> fighters, DateTimeOffset now)
    {
        var match = new PvpArenaMatch(mode, fighters, now, TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(1));
        foreach (var fighter in fighters)
        {
            match.MarkLoaded(match.Id, fighter.Membership, now);
            match.MarkConnected(match.Id, fighter.Membership, now);
        }
        return match;
    }

    static PvpRatingReceipt Receipt(PvpArenaMatch match, Guid member)
    {
        var standing = match.Result!.Standings.Single(row => row.Fighter.Membership == member);
        var calculator = new PvpRatingCalculator(PvpRatingTests.Fixture());
        return new(match.Id, standing.Fighter.CharacterId, standing.Fighter.Account, standing.Outcome, 0, 1,
            calculator.Calculate(calculator.Initial(), PvpRatingMode.Ordinary, standing.Outcome), PvpRatingCalculator.RuleVersion);
    }

    static void QueueResult(PvpArenaMatch match, Guid member)
    {
        var receipt = Receipt(match, member);
        match.InstallRatingReceipt(member, receipt);
        That(match.ConfirmResultQueued(match.Id, member, receipt), "原成员的实际回执可确认本局发布");
    }
}
