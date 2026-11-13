using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Party;
using DfoServer.Game.Session;
using DfoServer.Game.Town;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Channels;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Persistence;
using DfoServer.Testing;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class EvildomWarChallengeTests
{
    static readonly ChannelEntry Channel = new(new(2, 56), 64, "[evildom_war_challenge]");

    public static void RunCore()
    {
        int before = Count;
        var definition = Definition();
        var character = new Character(1, 0, "Challenge", 0, new(124, 2, 40, 249), null) { Level = 95 };
        var selection = new DungeonSelectionContext(1, 7, character.Id, character.Position, character.Position);
        string? Check(DungeonDefinition? resource = null, Character? actor = null, int channel = 64,
            DungeonSelectionContext? ticket = null, long epoch = 7, byte difficulty = 2, int count = 4,
            bool party = true, bool special = false) => EvildomWarChallengeEntryPolicy.Rejection(resource ?? definition,
                actor ?? character, channel, ticket ?? selection, epoch, difficulty, count, party, special);

        That(Check() is null && definition.IsEvildomWarChallenge && definition.MinimumPartyCount == 4,
            "英雄模式正式身份、95级、四人、固定难度及原选图合同成立");
        foreach (int type in new[] { 0, 22, 54 })
            That(Check(channel: type) == "evildom-war-challenge-channel-town-required", "普通与54频道不能借英雄选图进入：" + type);
        foreach (int count in new[] { 0, 5 })
            That(Check(count: count) == "evildom-war-challenge-party-required", "零人或超出四人不放行：" + count);
        foreach (int count in new[] { 1, 2, 3, 4 })
            That(Check(count: count) is null, "真实一至四人Party均允许英雄入场：" + count);
        That(Check(party: false) == "evildom-war-challenge-party-required" &&
            Check(actor: character with { Level = 94 }) == "evildom-war-challenge-level-required",
            "伪造人数的单人请求及94级拒绝");
        foreach (byte difficulty in new byte[] { 0, 1, 3, 4 })
            That(Check(difficulty: difficulty) == "evildom-war-challenge-mode-conflict", "英雄模式不自动改写请求难度：" + difficulty);
        That(Check(special: true) == "evildom-war-challenge-mode-conflict" &&
            Check(actor: character with { Position = new(67, 2, 40, 249) }) == "evildom-war-challenge-channel-town-required",
            "任务、练习、深渊等特殊请求及普通战役城镇不能借入口");
        foreach (var ticket in new[] { selection with { SelectionEpoch = 6 }, selection with { CharacterId = 2 },
            selection with { OriginPosition = new(124, 1, 40, 249) }, selection with { IsTutorial = true },
            selection with { IsClassExperience = true } })
            That(Check(ticket: ticket) == "evildom-war-challenge-selection-required", "过期epoch、换角、来源及特殊选图票据拒绝");
        That(EvildomWarChallengeEntryPolicy.Rejection(definition, character, 64, null, 7, 2, 4, true, false) ==
            "evildom-war-challenge-selection-required", "裸CMD16缺少正式选图票据拒绝");
        foreach (var bad in new[] { definition with { Id = 1 }, definition with { Path = "dungeon/unrelated.dgn" },
            definition with { MinimumLevel = 94 }, definition with { DisableFatigueConsumption = false },
            Remove("no experience monster and result"),
            Replace("evildom war challenge dungeon", new PvfToken(0, 1)), Replace("min party count", new PvfToken(0, 3)),
            Replace("min party count", new PvfToken(0, 4), new PvfToken(0, 4)), Replace("designate dungeon difficulty", new PvfToken(0, 1)),
            Replace("enter without fatigue", new PvfToken(0, 0)),
            definition with { RawSections = [..definition.RawSections, new("evildom war challenge dungeon", [])] },
            definition with { RawSections = definition.RawSections.Where(row => row.Tag != "evildom war challenge dungeon").ToArray() } })
            That(bad.RequiresEvildomWarChallengeContract && Check(resource: bad) == "evildom-war-challenge-resource-contract-invalid",
                "畸形标签、重复声明、人数、固定档和经济合同不退化为普通入场");
        DungeonDefinition Replace(string tag, params PvfToken[] tokens) => definition with
        { RawSections = definition.RawSections.Select(row => row.Tag == tag ? new DungeonRawSection(tag, tokens) : row).ToArray() };
        DungeonDefinition Remove(string tag) => definition with
        { RawSections = definition.RawSections.Where(row => row.Tag != tag).ToArray() };

        byte[] plain = ChannelPackets.Endpoint("127.0.0.1", 10011, 7);
        byte[] typed = ChannelPackets.Endpoint("127.0.0.1", 10011, 7, ChannelPackets.LoginChannelType(Channel));
        That(typed[3] == 64 && typed.Length == plain.Length &&
            typed.Where((_, index) => index != 3).SequenceEqual(plain.Where((_, index) => index != 3)),
            "LOGIN只在类型字节保留64，关联令牌与其它字段逐字节保持");
        foreach (int type in new[] { 22, 54 })
            That(!ChannelAreaLanding.SameSnapshotPredicates(type, 64) && !ChannelAreaLanding.SameSnapshotPredicates(64, type),
                "英雄与普通或54双向换频道独立快照：" + type);
        That(ChannelAreaLanding.SameSnapshotPredicates(64, 64) &&
            !ChannelAreaLanding.AllowsEvildomWarChallengeTown(new(new(2, 41), 54, "[evildom_war]"), 124) &&
            !ChannelAreaLanding.AllowsEvildomWarChallengeTown(null, 124) &&
            ChannelAreaLanding.AllowsEvildomWarChallengeTown(Channel, 124) &&
            ChannelAreaLanding.OrdinaryRoomReturn(new(124, 1, 1, 1)) is null,
            "同64保留独立身份，普通频道拒绝124且旧124返回存档不污染普通地区");
        That(ChannelAreaLanding.LandsInChannelTown(Channel) &&
            !ChannelAreaLanding.LandsInChannelTown(new(new(2, 41), 54, "[evildom_war]")),
            "英雄直接落本频道广场，普通54仍保留共享房间出门合同");
        RunWarpCore();
        RunSpawnCore();
        Console.WriteLine($"PASS evildom war challenge core {Count - before}");
    }

    static DungeonDefinition Definition() => new(400000105,
        "dungeon/gcontents/200114_theevildomwar_challenge/400000105_eastharlem.dgn", 95, 135, [],
        [new("evildom war challenge dungeon", []), new("min party count", [new PvfToken(0, 4)]),
         new("designate dungeon difficulty", [new PvfToken(0, 2)]), new("enter without fatigue", [new PvfToken(0, 1)]),
         new("no experience monster and result", [])])
        { DisableFatigueConsumption = true };

    public static void RunResources(TownCatalog towns, PvfArchive pvf, CharacterCatalog characters, ItemResources itemResources)
    {
        int before = Count;
        var none = new HashSet<int>();
        var catalog = SelfTestCatalogs.Dungeons(pvf);
        var definition = catalog.Get(EvildomWarChallengeEntryPolicy.DungeonId);
        var prices = SelfTestCatalogs.CardPrices(pvf);
        var limits = ClearRewardLimitCatalog.Load(pvf);
        var rewards = new DungeonCardRewardPolicy(SelfTestCatalogs.DropRules(pvf), itemResources.DungeonDropItems,
            itemResources.Items, itemResources.GoldLimits, itemResources.EpicPieces, prices: prices,
            tayberrsRewards: new(prices, itemResources.Boosters, itemResources.Items, limits));
        var rewardNow = DateTimeOffset.Parse("2026-11-06T00:00:00Z");
        foreach (var usage in new[] { new DungeonIncountUsage(0, 0), new DungeonIncountUsage(0, 1) })
        {
            var card = rewards.Prepare(definition, 2, 13, draw: _ => throw new InvalidOperationException("英雄专奖不能抽普通池"),
                utcNow: rewardNow, clearRewardUsage: usage);
            That(card is { Gold: 0, Item: { ItemId: 490014021, Value: 1 }, RuleId: EvildomWarChallengeRewardPolicy.Rule } &&
                card.Items().Count() == 1, "英雄合格通关只冻结一件真实闪耀装饰，不借普通装备或金币牌");
        }
        foreach (var usage in new[] { new DungeonIncountUsage(1, 1), new DungeonIncountUsage(0, 2) })
            That(rewards.Prepare(definition, 2, 13, utcNow: rewardNow, clearRewardUsage: usage) is
                { Gold: 0, Item: null }, "英雄每日1或每周2耗尽仍可通关，不再冻结奖励");
        var heroOptions = rewards.PrepareOptions(definition, 2, 13,
            new(rewardNow.AddDays(1).ToUnixTimeSeconds(), rewardNow.AddDays(1).ToUnixTimeSeconds()), rewardNow);
        That(heroOptions is { PaidDisabled: true, BlackDiamond: null, UseGoldCard: false },
            "英雄专奖不能经付费、黄金契约或黑钻多领");
        var withoutTable = new DungeonCardRewardPolicy(SelfTestCatalogs.DropRules(pvf), itemResources.DungeonDropItems,
            itemResources.Items, itemResources.GoldLimits, itemResources.EpicPieces);
        Reject<InvalidDataException>(() => withoutTable.Prepare(definition, 2, 13),
            "英雄奖励专表缺失明确拒绝，不能静默改发普通装备");
        var maze = definition.Mazes.Single();
        That(definition.IsEvildomWarChallenge && definition.MinimumPartyCount == 4 && definition.MinimumLevel == 95 &&
            definition.TryGetDesignatedDifficulty(out byte difficulty) && difficulty == 2 &&
            definition.DisableFatigueConsumption && definition.EnterWithoutFatigue && definition.DisableMonsterAndClearExperience &&
            definition.DisableItemDrops && !definition.DisableClearReward,
            "候选英雄DGN独立标签、min4、95级、fixed2及无疲劳无普通经验合同");
        That(maze is { Width: 13, Height: 3, MapSpecification.Count: 15 } &&
            maze.Starts.SequenceEqual([new DungeonPoint(0, 0)]) && maze.Bosses.SequenceEqual([new DungeonPoint(12, 0)]) &&
            maze.MapSpecification!.Select(map => map.Id).Order().SequenceEqual(Enumerable.Range(400000512, 15)),
            "正式英雄路线保持13乘3、15实际MAP及原起点领主位置");
        That(towns.Areas[(124, 2)].DungeonWorldMap == 244 && towns.DungeonMaps[244].Error is null &&
            towns.DungeonMaps[244].Options.Select(option => option.DungeonId).SequenceEqual([400000105]) &&
            ChannelAreaLanding.IsChannelBoundTown(towns, 124), "英雄城镇124唯一世界图244保持正式独立入口");
        var landing = ChannelAreaLanding.Decide(towns, Channel, 95, none);
        That(landing.Spot is { Town: 124, Area: 1 } && towns.IsPosition(landing.Spot, none) && !towns.IsRoom(landing.Spot, none) &&
            ChannelAreaLanding.Decide(towns, Channel, 94, none).Spot is null && ChannelAreaLanding.LandsInChannelTown(Channel),
            "英雄选角直接取124自己广场的有效门口，94级不能落124");
        That(towns.Areas[(124, 1)].Map(none).Npcs.Select(npc => npc.Id).Order().SequenceEqual([400001110u, 400001111u]) &&
            towns.Areas[(124, 2)].Map(none).Npcs.Select(npc => npc.Id).SequenceEqual([612u]),
            "英雄广场使用自己的伊奇与莫妮卡，副本门使用修理机，不借普通战役NPC");
        var ordinary = new ChannelEntry(new(2, 10), 22, "[granfloris]");
        var war = new ChannelEntry(new(2, 41), 54, "[evildom_war]");
        That(ChannelAreaLanding.ClientKeepsTownSnapshot(towns, Channel, landing.Spot!) &&
            !ChannelAreaLanding.ClientKeepsTownSnapshot(towns, ordinary, landing.Spot!) &&
            !ChannelAreaLanding.ClientKeepsTownSnapshot(towns, war, landing.Spot!) &&
            !ChannelAreaLanding.ClientKeepsTownSnapshot(towns, null, landing.Spot!) &&
            !ChannelAreaLanding.ClientKeepsTownSnapshot(towns, Channel, new(67, 1, 656, 154)) &&
            ChannelAreaLanding.PreservesRoomReturn(towns, 124), "124快照仅同64保留，离开保持原返回地区");
        var probe = new Character(1, 0, "ChallengeGate", 0, landing.Spot!, null) { Level = 95 };
        That(TownTravel.Plan(probe, new(124, 2, 40, 249, 5, 124, 1, 0), towns, none, ordinary).Reason ==
            "evildom-war-challenge-channel-town-mismatch", "跨频道CMD36在保存位置前拒绝124");
        RunWarpResources(pvf);
        RunSpawnResources(pvf);
        RunPartyResources(towns, pvf, characters, itemResources);
        Console.WriteLine($"PASS evildom war challenge resources {Count - before}");
    }

    static void RunPartyResources(TownCatalog towns, PvfArchive pvf, CharacterCatalog characters, ItemResources items)
    {
        string root = Path.Combine(Path.GetTempPath(), "S5A9-challenge-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var repository = new CharacterRepository(Path.Combine(root, "game.db"));
        var owners = new AccountOwners();
        var leases = new List<AccountOwners.Lease>();
        var all = new List<GameSession>();
        var learning = SelfTestCatalogs.SkillLearning(pvf);
        var catalog = SelfTestCatalogs.Dungeons(pvf);
        var rooms = SelfTestCatalogs.Rooms(pvf);
        var drops = SelfTestCatalogs.MonsterDrops(pvf);
        var none = new HashSet<int>();
        var waiting = ChannelAreaLanding.DecideArea(towns, 124, 2, 95, none).Spot!;
        try
        {
            foreach (bool changeParty in new[] { true, false })
            {
                var parties = new PartyService(repository);
                var service = new PartyDungeonService(parties, repository);
                var team = Enumerable.Range(0, 4).Select(_ =>
                {
                    int index = all.Count;
                    var lease = owners.Acquire("challenge-" + index, () => { });
                    leases.Add(lease);
                    var origin = index % 4 == 1 ? new Location(67, 1, 656, 154) :
                        index % 4 == 2 ? towns.Room : towns.InitialReturn;
                    repository.Create(lease, CharacterCreation.Prepare("Challenge" + index, characters.Get(0),
                        origin, towns.InitialReturn, learning.Get) with { Level = 95 });
                    var history = new ChannelHistory();
                    history.Record(lease.Account, ChannelAreaLanding.EvildomWarChallengeType);
                    var session = new GameSession(repository, towns, lease, entry: Channel, channelHistory: history);
                    all.Add(session);
                    session.Select(0, resumeTown: index % 4 is 1 or 2);
                    session.MarkReady();
                    session.CurrentParty = () => parties.Current(session);
                    session.HasMultiplePartyMembers = () => parties.HasMultipleMembers(session);
                    return session;
                }).ToArray();
                That(team.All(session => session.Selected!.Position is { Town: 124, Area: 1 } &&
                    session.Selected.Return == towns.InitialReturn), "自然选角与同64历史旧67/共享房间均纠正到英雄广场，保留原普通返回地区");
                var arrival = team[0].Selected!.Position;
                That(!team[0].Travel(new(124, 2, waiting.X, waiting.Y, 5, 124, 1, 0)) &&
                    team[0].Selected!.Position == arrival && team[0].TravelReason == "evildom-war-challenge-party-required",
                    "单人从英雄广场走真实门被拒绝，不提前保存等待区位置");
                That(parties.SetInformation(team[0], 0, PartySettings.Default).Accepted,
                    "英雄广场沿正式普通队伍创建，不要求54战役NPC或引导绑定");
                foreach (var member in team.Skip(1))
                    That(parties.Invite(team[0], member, 0).Accepted && parties.Respond(member, team[0].Selected!.Id, 0, true).Accepted,
                        "四真实账号通过正式PartyService邀请加入");
                That(team.All(session => session.Travel(new(124, 2, waiting.X, waiting.Y, 5, 124, 1, 0)) &&
                    session.Selected!.Position is { Town: 124, Area: 2 }), "四真实成员沿英雄广场实际有向门进入124等待区");
                That(service.BeginSelection(team[0]).Length == 4 && team.All(session => session.Phase == SessionPhase.MapSelect),
                    "四人通过原共享选图链建立各自票据");
                if (changeParty)
                {
                    var token = team[0].PreparePartyDungeon(400000105, 2, catalog, rooms, drops, items.Items, items.EpicPieces,
                        null, Guid.NewGuid(), 65536, memberCount: 4);
                    That(token is not null, "英雄房间按正式资源在owner外准备");
                    parties.Detach(team[3]);
                    That(team[0].ReservePartyDungeon(token!) is null && team.All(session => session.PartyDungeon is null),
                        "准备后真实成员离队，原名册令牌提交前拒绝，无费用及实例提前安装");
                    team[0].DiscardPartyPreparation(token!);
                }
                else
                {
                    var before = team.Select(session => session.Selected!).ToArray();
                    That(service.Enter(team[0], 400000105, 1, catalog, rooms, drops, items.Items, items.EpicPieces, null).Length == 0 &&
                        team.All(session => session.PartyDungeon is null), "错误难度不进入共享事务");
                    var notices = service.Enter(team[0], 400000105, 2, catalog, rooms, drops, items.Items, items.EpicPieces, null);
                    That(notices.Count(notice => notice.Kind == PartyDungeonNoticeKind.Entry) == 4 &&
                        team.All(session => ReferenceEquals(session.PartyDungeon, team[0].PartyDungeon) &&
                            session.FormalEnteredDungeonId == 400000105 && session.Phase == SessionPhase.Loading),
                        "四真实成员进入同一正式共享Run，不创建mode3或平行实例");
                    That(team.All(session => session.CurrentFormalDungeonScene() is
                        { Entry.DisableItemDrops: true, Entry.DisableClearReward: false }),
                        "英雄禁掉落由PVF冻结到每名成员，保留专用翻牌资格");
                    for (int index = 0; index < team.Length; index++)
                        That(team[index].Selected!.Fatigue == before[index].Fatigue && team[index].ReadEvildomWar() is null,
                            "英雄入场不扣普通战役30疲劳、不借54战役资格：" + index);
                }
                foreach (var session in team) { parties.Detach(session); session.End(); }
            }
            foreach (int type in new[] { 22, 54, 64 })
            {
                var lease = owners.Acquire("challenge-history-" + type, () => { });
                leases.Add(lease);
                var spot = ChannelAreaLanding.Decide(towns, Channel, 95, none).Spot!;
                var role = repository.Create(lease, CharacterCreation.Prepare("History" + type, characters.Get(0),
                    spot, towns.InitialReturn, learning.Get) with { Level = 95 });
                var history = new ChannelHistory();
                history.Record(lease.Account, type);
                var channel = new ChannelEntry(new(2, (byte)(type == 64 ? 56 : type == 54 ? 41 : 10)), type,
                    type == 64 ? "[evildom_war_challenge]" : type == 54 ? "[evildom_war]" : "[granfloris]");
                var session = new GameSession(repository, towns, lease, entry: channel, channelHistory: history);
                all.Add(session);
                session.Select(role.Slot, resumeTown: true);
                That(session.TownSnapshotResumed == (type == 64) && session.Selected!.Position == (type == 64 ? spot : towns.Room) &&
                    session.Selected.Return == towns.InitialReturn,
                    "已有同类型历史仍按英雄城镇独占边界复核，只有64恢复124快照：" + type);
                session.End();
            }
        }
        finally
        {
            foreach (var session in all) session.End();
            foreach (var lease in leases) lease.Dispose();
            TestFiles.DeleteDirectory(root, true);
        }
    }
}
