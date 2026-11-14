using DfoServer.Game.Accounts;
using DfoServer.Game.Auth;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Party;
using DfoServer.Game.PrivilegeServices;
using DfoServer.Game.Raids;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static partial class PrivilegeServiceTests
{
    static void RunRaidRewards(TownCatalog towns, CharacterCatalog characters, ItemResources resources, PvfArchive archive)
    {
        var definition = new RaidStartSettings { StartMinimumOverride = 1 }.Apply(
            DfoServer.GameWorld.Raids.RaidCatalog.Load(archive)).PrepareResources(archive,
                SelfTestCatalogs.Dungeons(archive), resources.Items).Find(2)!;
        foreach (var (count, guide) in new[] { (1, false), (4, false), (8, false), (1, true) })
        {
            int ticket = (guide ? definition.Resources!.FiendwarGuide! : definition.Resources!).Script.EntryCost!.ItemId;
            string root = Path.Combine(Path.GetTempPath(), "s5a9-privilege-raid-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var owners = new AccountOwners();
            var leases = new List<AccountOwners.Lease>();
            var sessions = new List<GameSession>();
            try
            {
                var repository = new CharacterRepository(Path.Combine(root, "game.db"));
                var clock = new LukeRaidClockTests.Clock(DateTimeOffset.UtcNow);
                long at = clock.GetUtcNow().ToUnixTimeSeconds();
                int day = PrivilegeServicePolicy.Day(clock.GetUtcNow());
                var registry = new RaidRegistry(clock);
                var parties = new PartyService(repository);
                var actors = new List<RaidActor>();
                for (int index = 0; index < count; index++)
                {
                    new AuthenticationService(new AuthenticationRepository(Path.Combine(root, "game.db")))
                        .Create("privilege-raid-" + index, "fixture-password");
                    var owner = owners.Acquire("privilege-raid-" + index, static () => { });
                    leases.Add(owner);
                    var point = new Location(38, 0, 629, 196);
                    var character = repository.Create(owner, new("PrivRaid" + index, point, point)
                    {
                        Level = 95, AcceptedStoryLevel = 95, CompletedQuests = [8672],
                        Inventory = InventoryRewardGrantService.CreateStarter(characters.Get(0))
                    });
                    character = InventoryService.Commit(repository, owner, character,
                        InventoryRewardGrantService.TryGrantBatch(character,
                            [new(ticket, 2)],
                            resources.Items, resources.GoldLimits, resources.EpicPieces, clock.GetUtcNow()));
                    var privilege = (index % 5) switch
                    {
                        2 => PrivilegeServiceState.Empty,
                        1 => new(at - 60, at, 1, 1, day, 1, 0, 0),
                        3 => new(at, at + 2, 1, 1, day, 1, 0, 0),
                        4 => new(at + 1, at + 3, 1, 1, day, 1, 0, 0),
                        _ => new PrivilegeServiceState(at - 60, at + 2, 1, 1, day, 1, 0, 0)
                    };
                    repository.CommitInventory(owner, character, new(character.Inventory,
                        character.Inventory with { PrivilegeService = privilege }, "fixture_privilege_raid"));
                    var session = new GameSession(repository, towns, owner,
                        entry: new(new(2, 39), 45, "[raid]"), timeProvider: clock);
                    sessions.Add(session);
                    session.CurrentParty = () => parties.Current(session);
                    session.Select(0); session.MarkReady();
                    actors.Add(session.CaptureRaidActor(registry)!);
                }
                var leader = actors[0];
                That(registry.Create(leader, 2, "Privilege", "Privilege"u8.ToArray(), guide ? 1 : 8, guide: guide) is not null,
                    "礼遇用例通过原目录创建团队或官方引导");
                foreach (var actor in actors.Skip(1))
                {
                    registry.Invite(leader, actor, 0);
                    That(registry.Respond(actor, leader.Character, true).Accepted, "礼遇成员沿原邀请实际加入");
                }
                var rewards = new FiendwarRewards(definition.Resources!.Fiendwar!, resources.Items, resources.Boosters,
                    definition.Resources.FiendwarGuide!.Fiendwar);
                var start = new AntonRaidStart(registry, parties, repository, towns, resources.EpicPieces, clock,
                    fiendwarRewards: rewards, fiendwarFacilities: new(definition.Resources.Fiendwar!,
                        definition.Resources.EntryCatalog!, SelfTestCatalogs.Rooms(archive)));
                var started = start.Start(leader, registry.Find(leader)!, definition);
                That(started.Error == 0, "礼遇用例沿真实开团事务扣票并建立共享Battle：" + started.Reason);
                var battle = leader.Session.CurrentRaidBattle(leader)!;
                var frozen = actors.Select(actor => battle.RewardPolicy(actor, 0)!).ToArray();
                for (int index = 0; index < actors.Count; index++)
                {
                    bool entitled = index % 5 is 0 or 3;
                    That(frozen[index].Offers.Single(row => row.Kind == 1).Grants.Length == (entitled ? 2 : 1),
                        "开团逐人冻结资格：有效及起始秒增加礼遇牌，到期秒、未购买、未来起始不增加");
                }
                clock.Advance(3);
                That(start.Start(leader, registry.Find(leader)!, definition) is { Error: 0, Replay: true } &&
                    actors.Select((actor, index) => ReferenceEquals(battle.RewardPolicy(actor, 0), frozen[index])).All(value => value),
                    "开团后到期或进入有效期均不重抽、不改变各人冻结奖励，重复开团不二次扣票");
            }
            finally
            {
                foreach (var session in sessions) session.End();
                foreach (var owner in leases) owner.Dispose();
                SqliteConnection.ClearAllPools();
                Directory.Delete(root, true);
            }
        }
        Console.WriteLine("PASS privilege raid start per-member boundaries and frozen rewards for 1/4/8 and guide");
    }
}
