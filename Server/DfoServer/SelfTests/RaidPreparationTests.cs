using System.Collections.Immutable;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Party;
using DfoServer.Game.Raids;
using DfoServer.Game.Session;
using DfoServer.Game.Social;
using DfoServer.GameWorld.Raids;
using DfoServer.GameWorld.Towns;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class RaidPreparationTests
{
    public static void RunCore()
    {
        int before = Count;
        string root = Path.Combine(Path.GetTempPath(), "s5a9-raid-preparation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        List<AccountOwners.Lease> leases = [];
        List<GameSession> sessions = [];
        try
        {
            var point = new Location(1, 0, 10, 10);
            var map = new TownMap("raid-preparation.map", [new(new(0,0,20,20),[],[])], [], false, []);
            var towns = new TownCatalog(new() { [(1,0)] = new(1,0,1,[],[],[],point,map,[],[]) },
                point, point, new(0,[],[]), [], new Dictionary<uint,uint[]>());
            var owners = new AccountOwners();
            var repository = new CharacterRepository(Path.Combine(root, "game.db"));
            var raids = new RaidRegistry();
            var social = new ProbeSocialStore(repository);
            var parties = new PartyService(social);
            RaidActor Make(int index) => MakeRaid(index,false);
            RaidActor MakeRaid(int index,bool anton)
            {
                var owner = owners.Acquire("raid-preparation-" + index, () => { }); leases.Add(owner);
                if (repository.ListRoster(owner).Length == 0)
                    repository.Create(owner, new("RaidP" + index, point, point) { Level = 90, CompletedQuests = anton ? [12829,3779] : [2903,4872] });
                var session = new GameSession(repository, towns, owner, entry: anton ? new(new(2,19),23,"[raid]") : new(new(2,20),32,"[luke_raid]"));
                sessions.Add(session); session.Select(0); session.MarkReady();
                return session.CaptureRaidActor(raids)!;
            }
            var actors = Enumerable.Range(0, 4).Select(Make).ToArray();
            var leader = actors[0];
            raids.Create(leader, 1, "prepare", "prepare"u8.ToArray(), 8);
            foreach (var actor in actors.Skip(1))
            {
                That(raids.Invite(leader, actor, 0) is not null && raids.Respond(actor, leader.Character, true).Accepted,
                    "准备夹具沿真实招募邀请加入");
            }
            That(RaidPreparationPlan.Capture(raids.Find(leader)!, out var unassigned) is null && unassigned == "raid-squad-unassigned",
                "未编队不能成为准备快照");
            for (int index = 0; index < actors.Length; index++)
                That(raids.Assign(leader, actors[index].Character, (byte)(index / 2 + 1)) is not null, "真实两支分队");
            RaidPreparationPlan Capture() => RaidPreparationPlan.Capture(raids.Find(leader)!, out _) ?? throw new Exception("准备夹具无效");
            var plan = Capture();
            That(plan.IsCurrent(raids) && plan.Squads.Length == 2 && ReferenceEquals(plan.Squads[0].Anchor, actors[0]) &&
                ReferenceEquals(plan.Squads[1].Anchor, actors[2]), "每分队按Slot取594锚点，不等同于攻坚队长");
            var reversed = RaidPreparationPlan.Capture(plan.Source with { Members = plan.Source.Members.Reverse().ToImmutableArray() }, out _)!;
            That(reversed.Squads.Select(s => s.Anchor.Character).SequenceEqual(plan.Squads.Select(s => s.Anchor.Character)) &&
                !reversed.IsCurrent(raids), "规划不依赖数组插入顺序，伪造同key/revision快照不能代替原owner记录");
            That(RaidPreparationPlan.Capture(plan.Source with { Type = 0 }, out var kind) is null && kind == "unsupported-raid-preparation",
                "Luke准备合同不借给其它团本类型");
            That(RaidPreparationPlan.Capture(plan.Source with { Members = plan.Source.Members.SetItem(1, plan.Source.Members[0]) }, out var duplicate) is null &&
                duplicate == "invalid-raid-roster", "重复槽/角色/账号拒绝，不形成两个在线owner");
            That(RaidPreparationPlan.Capture(plan.Source with { Members = plan.Source.Members.SetItem(0, plan.Source.Members[0] with { Squad = 3 }) }, out var squadError) is null &&
                squadError == "invalid-raid-squad", "八人容量不能产生第三分队");
            string stored = JsonSerializer.Serialize(actors.Select(a => repository.LoadCharacter(a.Owner, 0)).ToArray());
            That(plan.InspectOrdinaryParties(raids, parties) == "ordinary-party-missing", "无普通队伍只报告缺失");
            That(!parties.Respond(actors[1].Session, leader.Character, 0, true).Accepted,
                "团本名册不能授权通用无邀请CMD11");
            foreach (var squad in plan.Squads)
            {
                var a = squad.Anchor.Session; var b = squad.Members[1].Actor.Session;
                That(parties.Invite(a, b, 0).Accepted && parties.Respond(b, squad.Anchor.Character, 0, true).Accepted,
                    "复用合法PartyService待邀创建普通队伍");
            }
            That(plan.InspectOrdinaryParties(raids, parties) == "ordinary-party-members-match" && stored ==
                JsonSerializer.Serialize(actors.Select(a => repository.LoadCharacter(a.Owner, 0)).ToArray()),
                "匹配只读已有队伍，不扣票、写周次或改角色资产");
            var beforeConflict = parties.Snapshot();
            raids.Assign(leader, actors[2].Character, 1);
            var conflicting = Capture();
            That(conflicting.InspectOrdinaryParties(raids, parties) == "ordinary-party-conflict" && beforeConflict.All(parties.IsCurrent),
                "分队横跨两个现有普通队伍只报告冲突，不移人或撤销原队伍");
            raids.Assign(leader, actors[2].Character, 2);
            plan = Capture();
            var ordinary = parties.Current(leader.Session)!;
            That(parties.TransferLeader(leader.Session, ordinary.Members.Single(m => m.Actor.Character.Id == actors[1].Character).Slot).Accepted &&
                plan.InspectOrdinaryParties(raids, parties) == "ordinary-party-anchor-mismatch", "普通队长变化不能沿用旧锚点");
            That(parties.TransferLeader(actors[1].Session, ordinary.Leader).Accepted, "恢复原普通队长");
            long assetRevision = leader.Session.Selected!.Revision;
            leader.Session.SetCharacterGold(leader.Name, 123);
            That(leader.Session.Selected!.Revision > assetRevision && plan.IsCurrent(raids) && plan.InspectOrdinaryParties(raids, parties) == "ordinary-party-members-match",
                "无关资产revision更新不作招募版本变化");
            // 单人到四人均沿真实普通party与raid名册增长，不假定每分队必须满员。
            var small = Enumerable.Range(4, 4).Select(Make).ToArray();
            raids.Create(small[0], 1, "small", "small"u8.ToArray(), 8);
            var native = new PartySettings(0, "Party : 1", 4, uint.MaxValue, 5, 0, 0, 0, 0, 7);
            That(parties.SetInformation(small[0].Session, 0, native).Accepted, "单成员预建使用594已证完整settings");
            for (int count = 1; count <= 4; count++)
            {
                var member = small[count - 1];
                if (count > 1)
                {
                    That(raids.Invite(small[0], member, 0) is not null && raids.Respond(member, small[0].Character, true).Accepted &&
                        parties.Invite(small[0].Session, member.Session, 0).Accepted &&
                        parties.Respond(member.Session, small[0].Character, 0, true).Accepted, "合法邀请扩展非满员分队");
                }
                raids.Assign(small[0], member.Character, 1);
                var smallPlan = RaidPreparationPlan.Capture(raids.Find(small[0])!, out _)!;
                That(smallPlan.Squads.Single().Members.Length == count && smallPlan.IsCurrent(raids) &&
                    smallPlan.InspectOrdinaryParties(raids, parties) == "ordinary-party-members-match", "1/2/3/4人分队均可只读匹配");
                var existing = parties.Current(small[0].Session)!;
                var repeated = parties.SetInformation(small[0].Session, 0, native);
                That(repeated.Accepted && repeated.Notices.Length == 0 && parties.IsCurrent(existing),
                    "已有精确settings的原生operation0幂等，不换队伍、不推进revision");
                if (count == 1)
                {
                    That(parties.SetInformation(small[0].Session, 1, native with { Title = "User title" }).Accepted &&
                        smallPlan.InspectOrdinaryParties(raids, parties) == "ordinary-party-members-match", "成员匹配明确不宣称settings或594已就绪");
                    var userParty = parties.Current(small[0].Session)!;
                    smallPlan.InspectOrdinaryParties(raids, parties);
                    That(parties.IsCurrent(userParty) && userParty.Settings.Title == "User title", "只读观察不规范化用户已有队伍设置");
                    That(parties.SetInformation(small[0].Session, 0, native).Notices.Length != 0 && !parties.IsCurrent(userParty),
                        "不同settings的operation0依既有合同修改而非幂等，不冒称已有回执");
                }
            }
            leader.Session.UpdateProgress(new(CharacterProgressField.Level, leader.Owner.Account, leader.Name, 89, 0));
            That(!plan.IsCurrent(raids), "当前Selected资格改变立即失效，不信任旧成员显示快照");
            leader.Session.UpdateProgress(new(CharacterProgressField.Level, leader.Owner.Account, leader.Name, 90, 0));
            raids.Assign(leader, actors[1].Character, 2);
            That(!plan.IsCurrent(raids), "重编队使旧准备revision失效");
            raids.Assign(leader, actors[1].Character, 1);
            plan = Capture();
            raids.TransferLeadership(leader, actors[2].Character);
            That(!plan.IsCurrent(raids), "攻坚队委任使旧准备revision失效");
            raids.TransferLeadership(actors[2], leader.Character);
            plan = Capture();
            var replacement = Make(3);
            That(!plan.IsCurrent(raids) && !actors[3].IsCurrent && replacement.IsCurrent,
                "同账号接管只认新owner，旧准备不能继承");
            var fresh = RaidPreparationPlan.Capture(raids.Find(leader)!, out var stale);
            That(fresh is null && stale == "raid-member-not-current", "尚未清理的旧成员也不能新建准备快照");
            actors[3].Session.End();
            plan = Capture();
            actors[1].Session.ReturnToRoster();
            That(!plan.IsCurrent(raids), "返回选角使原epoch准备失效");
            RunService(owners, repository, raids, parties, Make, social);
            RunIntegratedContracts(repository,raids,parties,Make,index => MakeRaid(index,true));
        }
        finally
        {
            foreach (var session in sessions) session.End();
            foreach (var lease in leases) lease.Dispose();
            TestFiles.DeleteDirectory(root, true);
        }
        Console.WriteLine($"PASS {Count - before} 卢克准备快照、原子分队构造、门禁与幂等断言");
    }

    static void RunService(AccountOwners owners, CharacterRepository repository, RaidRegistry raids, PartyService parties,
        Func<int, RaidActor> make, ProbeSocialStore social)
    {
        var members = Enumerable.Range(8, 8).Select(make).ToArray();
        var leader = members[0];
        raids.Create(leader, 1, "batch", "batch"u8.ToArray(), 8);
        foreach (var member in members.Skip(1))
            That(raids.Invite(leader, member, 0) is not null && raids.Respond(member, leader.Character, true).Accepted,
                "批量准备夹具由合法招募构造八人");
        for (int index = 0; index < members.Length; index++) raids.Assign(leader, members[index].Character, (byte)(index / 4 + 1));
        RaidPreparationPlan Capture() => RaidPreparationPlan.Capture(raids.Find(leader)!, out _) ?? throw new Exception("批量夹具无效");
        var plan = Capture();
        var definition = new RaidDefinition(1, "synthetic-luke.etc", 8, 1, 2);
        var catalog = new RaidCatalog(ImmutableDictionary<byte, RaidDefinition>.Empty.Add(1, definition));
        var service = new RaidPreparationService(raids, parties, catalog);
        string Assets() => JsonSerializer.Serialize(members.Select(m => repository.LoadCharacter(m.Owner, 0)).ToArray());
        string before = Assets();
        That(new RaidPreparationService(raids, parties, RaidCatalog.Empty).TryPrepare(plan).Reason == "raid-definition-mismatch",
            "资源缺失不使用自造默认八人许可");
        That(new RaidPreparationService(raids, parties, new(ImmutableDictionary<byte, RaidDefinition>.Empty.Add(1,
            definition with { MemberMax = 9 }))).TryPrepare(plan).Reason == "raid-definition-mismatch", "独立Raid owner入口仍硬限八人");
        void BusyAtEveryGate()
        {
            var original = parties.Snapshot();
            var gates = original.Where(p => p.Members.Any(m => members.Any(r => ReferenceEquals(r.Session, m.Actor.Session))))
                .OrderBy(p => p.Id).Select(p => p.Commands).Concat(members.OrderBy(m => m.Owner.Account, StringComparer.Ordinal)
                    .ThenBy(m => m.Character).Select(m => m.Session.CommandGate)).Distinct().ToArray();
            for (int index = 0; index < gates.Length; index++)
            {
                That(gates[index].Wait(0), "夹具独占目标门禁");
                try
                {
                    var result = service.TryPrepare(plan);
                    That(!result.Accepted && result.Reason == "raid-preparation-busy" && result.Notices.IsEmpty &&
                        gates.Where((_, at) => at != index).All(g => g.CurrentCount == 1) && gates[index].CurrentCount == 0 &&
                        parties.Snapshot().SequenceEqual(original), "任一取得位置Busy逆序释放其余gate，零新队伍/通知");
                }
                finally { gates[index].Release(); }
            }
        }
        BusyAtEveryGate();
        var foreignParty = parties.Snapshot().First(p => p.Members.All(m => m.IsCurrent()));
        var foreignMember = foreignParty.Members[0];
        var fixedOwners = members.Select(m => (m.Owner, m.Character)).ToArray();
        AccountOwners.Lease.ExecuteRaidGroup(fixedOwners, 8, () =>
        {
            var valid = parties.PlanRaidPreparation(plan, []).Batch!;
            var original = parties.Snapshot();
            var versions = members.Select(m => m.Session.PartyMembershipVersion).ToArray();
            var forged = new[]
            {
                valid with { Bindings = [null!] },
                valid with { Groups = valid.Groups.RemoveAt(1) },
                valid with { Groups = valid.Groups.SetItem(1, valid.Groups[0]) },
                valid with { Groups = valid.Groups.SetItem(0, valid.Groups[0] with { Settings = valid.Groups[0].Settings with { Difficulty = 0 } }) },
                valid with { Groups = valid.Groups.SetItem(0, valid.Groups[0] with { Members = valid.Groups[0].Members.SetItem(0, foreignMember) }) },
                valid with { Groups = valid.Groups.SetItem(0, valid.Groups[0] with { Existing = foreignParty }) },
                valid with { Groups = valid.Groups.SetItem(0, valid.Groups[0] with { Members = valid.Groups[0].Members.SetItem(0,
                    valid.Groups[0].Members[0] with { Slot = 1 }) }) },
                valid with { Groups = valid.Groups.SetItem(0, valid.Groups[0] with { Members = valid.Groups[0].Members.SetItem(0,
                    valid.Groups[0].Members[0] with { Identity = foreignMember.Identity }) }) }
            };
            foreach (var batch in forged)
            {
                var result = raids.CommitPreparation(plan, parties, batch, CancellationToken.None);
                That(!result.Accepted && result.Reason == "invalid-raid-party-batch" && result.Notices.IsEmpty &&
                    parties.Snapshot().SequenceEqual(original) && members.Select(m => m.Session.PartyMembershipVersion).SequenceEqual(versions),
                    "有效raid计划不能夹带漏组/重复组/错误设置/未锁foreign actor或调包identity，拒绝前零安装");
            }
            return true;
        });
        using (var canceled = new CancellationTokenSource())
        {
            canceled.Cancel();
            Reject<OperationCanceledException>(() => service.TryPrepare(plan, canceled.Token), "已取消准备不取得任何门禁");
        }
        social.BeforeRead = () => throw new IOException("synthetic social read failure");
        Reject<IOException>(() => service.TryPrepare(plan), "外部查询异常不安装第一支分队");
        social.BeforeRead = null;
        That(members.All(m => m.Session.CommandGate.CurrentCount == 1 && parties.Current(m.Session) is null && m.Owner.Execute(() => true)),
            "查询异常释放全团门禁、owner组线程上下文与租约锁");
        using (var canceled = new CancellationTokenSource())
        {
            social.BeforeRead = canceled.Cancel;
            Reject<OperationCanceledException>(() => service.TryPrepare(plan, canceled.Token), "持全团门禁后取消在安装前拒绝");
            social.BeforeRead = null;
        }
        That(members.All(m => m.Session.CommandGate.CurrentCount == 1 && parties.Current(m.Session) is null), "取消后无半准备且门禁全释放");
        foreach (var blockedBy in new[] { leader, members[1] })
        {
            var target = ReferenceEquals(blockedBy, leader) ? members[1] : leader;
            var actor = blockedBy.Session.CaptureSocialActor()!;
            That(repository.ChangeCharacterLink(actor, target.Name, true, false, "", DateTimeOffset.UtcNow).Error == 0,
                "真实仓储建立锚点双向屏蔽测试边");
            var blocked = service.TryPrepare(plan);
            That(!blocked.Accepted && blocked.Reason == "ordinary-party-blocked" && blocked.Notices.IsEmpty &&
                members.All(m => parties.Current(m.Session) is null), "任一方向屏蔽拒绝全批新队伍且无部分通知");
            That(repository.ChangeCharacterLink(actor, target.Name, true, true, "", DateTimeOffset.UtcNow).Error == 0,
                "通过原owner写路径移除测试屏蔽");
        }
        social.BeforeRead = () => { social.BeforeRead = null; raids.Assign(leader, members[1].Character, 0); };
        That(service.TryPrepare(plan).Reason == "raid-preparation-stale" && members.All(m => parties.Current(m.Session) is null),
            "规划查询边界重编队使整批失效，不留下第一支队伍");
        raids.Assign(leader, members[1].Character, 1); plan = Capture();
        var second = members[4];
        That(parties.SetInformation(second.Session, 0, RaidPreparationPlan.NativePartySettings(2) with { Title = "User squad" }).Accepted,
            "第二分队原有用户设置");
        foreach (var member in members.Skip(5))
            That(parties.Invite(second.Session, member.Session, 0).Accepted && parties.Respond(member.Session, second.Character, 0, true).Accepted,
                "第二分队已有普通成员");
        var existing = parties.Current(second.Session)!;
        long[] versions = members.Select(m => m.Session.PartyMembershipVersion).ToArray();
        var conflict = service.TryPrepare(plan);
        That(!conflict.Accepted && conflict.Reason == "ordinary-party-settings-conflict" && conflict.Notices.IsEmpty &&
            members.Take(4).All(m => parties.Current(m.Session) is null) && parties.IsCurrent(existing) &&
            members.Select(m => m.Session.PartyMembershipVersion).SequenceEqual(versions), "第二分队设置冲突时第一分队零安装，原party和成员版本不变");
        BusyAtEveryGate();
        That(parties.SetInformation(second.Session, 0, RaidPreparationPlan.NativePartySettings(2)).Accepted, "用户显式将原分队改为精确原生设置");
        var prepared = service.TryPrepare(plan);
        That(prepared.Accepted && prepared.Parties.Length == 2 && prepared.Notices.Length == 4 &&
            prepared.Parties.All(parties.IsCurrent) && prepared.Parties[1] == parties.Current(second.Session),
            "整批只新建缺失分队，保留原已匹配分队和版本");
        var membershipBeforeRetry = members.Select(m => m.Session.PartyMembershipVersion).ToArray();
        var repeated = service.TryPrepare(plan);
        That(repeated.Accepted && repeated.Reason == "ordinary-parties-already-prepared" && repeated.Notices.IsEmpty &&
            repeated.Parties.Zip(prepared.Parties).All(pair => ReferenceEquals(pair.First, pair.Second)) &&
            members.Select(m => m.Session.PartyMembershipVersion).SequenceEqual(membershipBeforeRetry),
            "未发布首次通知也可取回精确原快照，重试不重建/再AdvanceMembership，但不证明客户端已同步");
        var blocker = leader.Session.CaptureSocialActor()!;
        That(repository.ChangeCharacterLink(blocker, members[1].Name, true, false, "", DateTimeOffset.UtcNow).Error == 0 &&
            service.TryPrepare(plan).Accepted && repeated.Parties.All(parties.IsCurrent), "已匹配普通队伍不因后来屏蔽被擅自拆掉");
        That(repository.ChangeCharacterLink(blocker, members[1].Name, true, true, "", DateTimeOffset.UtcNow).Error == 0,
            "移除已匹配队伍的测试屏蔽");
        foreach (var member in members) That(parties.Leave(member.Session).Accepted, "清理本夹具普通队伍后再验证两队全新构造");
        prepared = service.TryPrepare(plan);
        That(prepared.Accepted && prepared.Parties.Length == 2 && prepared.Notices.Length == 8 &&
            prepared.Parties.All(p => p.Members.Length == 4 && p.Leader == 0) &&
            prepared.Parties.Select((p, i) => p.Settings == RaidPreparationPlan.NativePartySettings((byte)(i + 1))).All(value => value),
            "两支新分队完整安装后才返回八人通知，锚点为各自普通队长");
        That(prepared.Notices.All(n => n.Guard?.Invoke() == true), "返回的通知依赖本次raid和ordinary精确版本");
        raids.Assign(leader, members[1].Character, 0);
        That(prepared.Notices.All(n => n.Guard?.Invoke() == false), "准备后重编队阻止迟到名单发布");
        raids.Assign(leader, members[1].Character, 1); plan = Capture();
        That(before == Assets(), "Busy、异常、取消、冲突、成功和重试均不改任何角色资产");
        foreach (var member in members) parties.Leave(member.Session);
        // 实际准备服务创建1/2/3/4人分队；不要求满四人，也不从既有party冒充构造成功。
        var small = Enumerable.Range(16, 4).Select(make).ToArray();
        string smallAssets = JsonSerializer.Serialize(small.Select(m => repository.LoadCharacter(m.Owner, 0)).ToArray());
        raids.Create(small[0], 1, "small-batch", "small-batch"u8.ToArray(), 8);
        for (int count = 1; count <= 4; count++)
        {
            var member = small[count - 1];
            if (count > 1)
                That(raids.Invite(small[0], member, 0) is not null && raids.Respond(member, small[0].Character, true).Accepted,
                    "准备服务非满员分队沿真实raid邀请增长");
            raids.Assign(small[0], member.Character, 1);
            var smallPlan = RaidPreparationPlan.Capture(raids.Find(small[0])!, out _)!;
            var made = service.TryPrepare(smallPlan);
            var ordinary = made.Parties.Single();
            That(made.Accepted && made.Notices.Length == count && ordinary.Members.Length == count &&
                ordinary.Settings == RaidPreparationPlan.NativePartySettings(1) && ordinary.Leader == 0,
                "准备服务真实构造1/2/3/4人队伍及精确594设置");
            if (count == 1)
            {
                var native = parties.SetInformation(small[0].Session, 0, RaidPreparationPlan.NativePartySettings(1));
                That(native.Accepted && native.Notices.Length == 0 && parties.IsCurrent(ordinary),
                    "预建单人队伍后的原生CMD12走既有幂等，无新通知或版本");
            }
            else
                foreach (var accepted in small.Skip(1).Take(count - 1))
                {
                    var native = parties.Respond(accepted.Session, small[0].Character, 0, true);
                    That(native.Accepted && native.Notices.Length == 0 && parties.IsCurrent(ordinary),
                        "预建多人队伍后的原生CMD11走已有同队幂等，不安装无邀特例");
                }
            foreach (var actor in small.Take(count)) That(parties.Leave(actor.Session).Accepted, "同夹具清理普通队伍供下一人数构造");
        }
        That(smallAssets == JsonSerializer.Serialize(small.Select(m => repository.LoadCharacter(m.Owner, 0)).ToArray()),
            "非满员准备、原生重复及离队不改角色资产");
        Task<AccountOwners.Lease>? takeover = null;
        social.BeforeRead = () =>
        {
            social.BeforeRead = null;
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            takeover = Task.Run(() => { entered.SetResult(); return owners.Acquire(members[7].Owner.Account, () => { }); });
            That(entered.Task.Wait(TimeSpan.FromSeconds(2)) && !takeover.IsCompleted, "持八人owner组时并发接管等待原稳定边界");
        };
        var beforeTakeover = service.TryPrepare(plan);
        That(takeover is not null && takeover.Wait(TimeSpan.FromSeconds(3)), "准备归还owner锁后接管正常完成");
        using var replacement = takeover!.Result;
        That(beforeTakeover.Accepted && beforeTakeover.Notices.All(n => n.Guard?.Invoke() == false) &&
            service.TryPrepare(plan).Reason == "raid-preparation-stale", "新generation阻止旧准备通知及旧计划重试，不创建替身资产");
    }

    static void RunIntegratedContracts(CharacterRepository repository, RaidRegistry raids,
        PartyService parties, Func<int,RaidActor> make, Func<int,RaidActor> makeAnton)
    {
        RaidParty Recruit(RaidActor[] actors, byte type)
        {
            raids.Create(actors[0],type,"integrated","integrated"u8.ToArray(),type == 0 ? 20 : 8,type == 0 ? 10 : 1);
            foreach(var actor in actors.Skip(1))
                That(raids.Invite(actors[0],actor,0) is not null && raids.Respond(actor,actors[0].Character,true).Accepted,
                    "整合回归沿真实招募加入各自团本");
            for(int at=0;at<actors.Length;at++) raids.Assign(actors[0],actors[at].Character,(byte)(at/4+1));
            return raids.Find(actors[0])!;
        }
        var catalog = new RaidCatalog(ImmutableDictionary<byte,RaidDefinition>.Empty.Add(1,new(1,"synthetic-luke.etc",8,1,2)));
        var service = new RaidPreparationService(raids,parties,catalog);
        const System.Reflection.BindingFlags fields = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var cursor = typeof(PartyService).GetField("nextId",fields)!;
        var partyGate = typeof(PartyService).GetField("gate",fields)!.GetValue(parties)!;
        var raidGate = typeof(RaidRegistry).GetField("gate",fields)!.GetValue(raids)!;
        foreach(bool fail in new[] { false,true })
        {
            int offset = fail ? 80 : 50;
            var anton = Enumerable.Range(offset,10).Select(makeAnton).ToArray();
            var luke = Enumerable.Range(offset+10,8).Select(make).ToArray();
            var antonRaid = Recruit(anton,0);
            var lukeRaid = Recruit(luke,1);
            var plan = RaidPreparationPlan.Capture(lukeRaid,out _)!;
            var original = parties.Snapshot();
            ushort previous = (ushort)cursor.GetValue(parties)!;
            ushort firstReserved = previous;
            do { firstReserved = (ushort)((firstReserved+1)%ushort.MaxValue); }
            while(original.Any(p => p.Id == firstReserved));
            var versions = anton.Concat(luke).Select(a => a.Session.PartyMembershipVersion).ToArray();
            string assets = JsonSerializer.Serialize(anton.Concat(luke).Select(a => repository.LoadCharacter(a.Owner,0)).ToArray());
            RaidPreparationResult? prepared = null;
            void Start()
            {
                var result = AccountOwners.Lease.ExecuteRaid(anton.Select(a => (a.Owner,a.Character)).ToArray(),() =>
                    parties.CreateRaidSquads(antonRaid,() =>
                    {
                        // 只调整测试分配游标，确定环回命中真实Anton预留；不伪造预留集合、队伍索引或成员。
                        lock(partyGate) cursor.SetValue(parties,previous);
                        var task = Task.Run(() => service.TryPrepare(plan));
                        That(task.Wait(TimeSpan.FromSeconds(5)),"Anton提交窗口不持Party gate阻塞另一团准备");
                        prepared = task.Result;
                        That(prepared.Accepted && prepared.Parties.All(p => p.Id != firstReserved) &&
                            original.All(old => parties.Snapshot().Any(now => ReferenceEquals(old,now))),"Luke真实规划绕开Anton待提交ID且不覆盖既有队伍");
                        if(fail) throw new IOException("synthetic anton commit failure");
                        return (new RaidStarted(Guid.NewGuid(),DateTimeOffset.UtcNow,
                            antonRaid.Members.Select(m => (m.Actor.Character,m.Slot,m.Squad)).ToImmutableArray()),
                            anton.Select(a => a.Session.Selected!).ToImmutableArray());
                    }));
                That(result is not null && anton.Select(a => parties.Current(a.Session)!.Id).Distinct().Count() == 3 &&
                    anton.All(a => !prepared!.Parties.Any(p => p.Id == parties.Current(a.Session)!.Id)),
                    "真实Anton提交完成三个普通分队，与Luke两个分队互不覆盖");
            }
            if(fail) Reject<IOException>(Start,"Anton真实commit回调失败由原finally归还预留"); else Start();
            That(prepared is { Accepted:true } && prepared.Parties.All(parties.IsCurrent) &&
                anton.Concat(luke).Select((a,at) => a.Session.PartyMembershipVersion == versions[at]+(fail && at<10 ? 0 : 1)).All(x => x),
                "双方成功只推进各自一次成员版本，失败Anton零安装而Luke提交保持");
            That(assets == JsonSerializer.Serialize(anton.Concat(luke).Select(a => repository.LoadCharacter(a.Owner,0)).ToArray()),
                "分队预留和安装不改双方角色资产，不冒充真实收费事务");
            if(fail)
            {
                var solo = make(offset+18);
                lock(partyGate) cursor.SetValue(parties,previous);
                That(parties.SetInformation(solo.Session,0,RaidPreparationPlan.NativePartySettings(1)).Accepted &&
                    parties.Current(solo.Session)!.Id == firstReserved && prepared!.Parties.All(parties.IsCurrent) &&
                    anton.All(a => parties.Current(a.Session) is null),"异常释放后原未安装ID可复用，不碰已安装Luke队伍");
            }
        }

        var pair = new[] { make(110),make(111) };
        var source = Recruit(pair,1);
        var current = RaidPreparationPlan.Capture(source,out _)!;
        var foreign = make(112);
        That(parties.SetInformation(foreign.Session,0,RaidPreparationPlan.NativePartySettings(1)).Accepted,"交叉锁回归建立无关普通队伍");
        var fixedOwners = pair.Select(a => (a.Owner,a.Character)).ToArray();
        var batch = AccountOwners.Lease.ExecuteRaidGroup(fixedOwners,8,() => parties.PlanRaidPreparation(current,[]).Batch!);
        using var entered = new ManualResetEventSlim();
        using var changeEntered = new ManualResetEventSlim();
        RaidPreparationResult? committed = null;
        Exception? workerError = null;
        var worker = new Thread(() =>
        {
            try
            {
                committed = AccountOwners.Lease.ExecuteRaidGroup(fixedOwners,8,() =>
                {
                    entered.Set();
                    return raids.CommitPreparation(current,parties,batch,CancellationToken.None);
                });
            }
            catch(Exception error) { workerError = error; }
        }) { IsBackground = true };
        Task<RaidChange?>? changed = null;
        try
        {
            foreign.Owner.Execute(() =>
            {
                lock(partyGate)
                {
                    worker.Start();
                    That(entered.Wait(TimeSpan.FromSeconds(3)) && SpinWait.SpinUntil(() =>
                        (worker.ThreadState & System.Threading.ThreadState.WaitSleepJoin) != 0,TimeSpan.FromSeconds(3)),
                        "准备提交已在真实Party gate等待，固定owner仍保持");
                    bool available = Monitor.TryEnter(raidGate,TimeSpan.FromSeconds(1));
                    if(available) Monitor.Exit(raidGate);
                    That(available,"等待Party gate时不得持Raid gate，避免普通离队StartedRaid反向锁链");
                    That(parties.Leave(foreign.Session).Accepted,"真实无关普通离队在Party gate内读取StartedRaid仍能完成");
                    changed = Task.Run(() => { changeEntered.Set(); return raids.Assign(pair[0],pair[1].Character,2); });
                    That(changeEntered.Wait(TimeSpan.FromSeconds(3)),"竞争改编任务已进入实际调用点");
                    That(!changed.Wait(TimeSpan.FromMilliseconds(30)),"本团改编必须等待准备固定owner，不能穿越复验提交边界");
                }
                return true;
            });
        }
        finally
        {
            That(worker.Join(TimeSpan.FromSeconds(5)),"交叉门禁回归在有界时间内归还全部锁");
            if(changed is not null) That(changed.Wait(TimeSpan.FromSeconds(3)),"准备失败或成功均等待已启动改编任务收尾");
        }
        if(workerError is not null) throw workerError;
        That(changed is not null && changed.Wait(TimeSpan.FromSeconds(3)) && changed.Result is not null &&
            committed is { Accepted:true } && committed.Parties.All(parties.IsCurrent) &&
            raids.Find(pair[0])!.Members.Single(m => m.Actor.Character == pair[1].Character).Squad == 2 && !current.IsCurrent(raids),
            "准备安装后排队改编正常生效，不死锁、不丢变更，也不沿用旧raid计划");

        var startedActor = make(120);
        var pending = Recruit([startedActor],1);
        var started = new RaidStarted(Guid.NewGuid(),DateTimeOffset.UtcNow,[(startedActor.Character,0,1)]);
        That(RaidPreparationPlan.Capture(pending with { Started = started },out var rejected) is null && rejected == "raid-already-started",
            "准备明确拒绝已Started的raid快照");
        var start = startedActor.Owner.Execute(() => raids.CommitStart(startedActor,pending,[],new(1,1),
            () => (started,ImmutableArray.Create(startedActor.Session.Selected!),Array.Empty<PartyNotice>())));
        That(start.Error == 0 && startedActor.Session.StartedRaid == started &&
            RaidPreparationPlan.Capture(pending,out rejected) is null && rejected == "raid-member-already-started",
            "即使旧快照Started为空，也按当前Session实际StartedRaid拒绝准备，不只看展示快照");
    }

    // 测试只拦截已存在的social只读边界；实际查询和写入仍委托真实仓储。
    sealed class ProbeSocialStore(ISocialStore inner) : ISocialStore
    {
        public Action? BeforeRead { get; set; }
        public AccountFriendGroup[] AccountGroups(SocialActor actor) => inner.AccountGroups(actor);
        public void SaveAccountGroups(SocialActor actor, AccountFriendGroup[] groups) => inner.SaveAccountGroups(actor, groups);
        public bool IsBlocked(ushort recipient, ushort sender) { BeforeRead?.Invoke(); return inner.IsBlocked(recipient, sender); }
        public SocialPerson? FindSocialPerson(string name, bool adventure = false) => inner.FindSocialPerson(name, adventure);
        public SocialLink[] CharacterLinks(ushort owner, bool blocked) => inner.CharacterLinks(owner, blocked);
        public SocialResult ChangeCharacterLink(SocialActor actor, string name, bool blocked, bool remove, string memo, DateTimeOffset now) => inner.ChangeCharacterLink(actor, name, blocked, remove, memo, now);
        public SocialLink[] AccountFriends(string account) => inner.AccountFriends(account);
        public AccountFriendRequest[] AccountRequests(string account) => inner.AccountRequests(account);
        public SocialResult RequestAccountFriend(SocialActor actor, string adventure, string message, byte group, string memo, DateTimeOffset now) => inner.RequestAccountFriend(actor, adventure, message, group, memo, now);
        public SocialResult ResolveAccountFriend(SocialActor actor, string adventure, bool accept, bool remove, bool cancel, string memo = "", byte group = 0) => inner.ResolveAccountFriend(actor, adventure, accept, remove, cancel, memo, group);
        public SocialResult UpdateSocialMemo(SocialActor actor, string name, string memo, bool blocked, byte group = 0) => inner.UpdateSocialMemo(actor, name, memo, blocked, group);
        public HashSet<ushort> BlockingCharacters(ushort sender) => inner.BlockingCharacters(sender);
        public string[] SocialObservers(ushort character, string account) => inner.SocialObservers(character, account);
    }

}
