using System.Net;
using DfoServer.Development;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Towns;
using DfoServer.Network;
using DfoServer.Network.Projection;
using DfoServer.Network.Protocol;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class BattlePassRewardTests
{
    internal static async Task RunReadyProjectionAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), "S5A9-pass-ready-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            foreach (string change in new[] { "unchanged", "reselect", "takeover", "recharge" })
                await Scenario(change);
        }
        finally { TestFiles.DeleteDirectory(root, recursive: true); }

        async Task Scenario(string change)
        {
            const string season = "ready-projection-race";
            var fixture = new Fixture();
            var (policy, now) = BattlePassMissionTests.AckFixturePolicy(season);
            var repository = new CharacterRepository(Path.Combine(root, change + ".db"));
            repository.ConfigureBattlePassMissions(policy);
            var owners = new AccountOwners();
            using var owner = owners.Acquire("ready-pass", () => { });
            var point = new Location(1, 0, 10, 10);
            var towns = new TownCatalog(new Dictionary<(byte, byte), TownArea>(), point, point,
                new TownDock(0, [], []), [], new Dictionary<uint, uint[]>());
            repository.Create(owner, new NewCharacter("ReadyOne", point, null) { Level = 95 });
            repository.Create(owner, new NewCharacter("ReadyTwo", point, null) { Level = 95 });
            var clock = new AckClock(now);
            var session = new GameSession(repository, towns, owner, timeProvider: clock);
            session.Select(0);
            session.MarkReady();
            var selected = session.Selected!;
            long epoch = session.SelectionEpoch;
            var state = repository.ReadBattlePassMissions(owner, now)!;
            That(state.Active, "完整赛季夹具处于活动期并包含26个真实结构任务槽");
            var expected = new[]
            {
                ((ushort)NotiPacketType.BATTLE_PASS_INFO_PASS, BattlePassProjection.PassInfo(fixture.Catalog, state.Season)),
                ((ushort)NotiPacketType.BATTLE_PASS_INFO_MISSION, state.ToPayload()),
                ((ushort)NotiPacketType.BATTLE_PASS_INFO_REWARD, state.Season.ToClaimBytes())
            };

            using var stop = new CancellationTokenSource();
            using var output = new AckHeldOutput();
            var sent = new List<(ushort Opcode, byte[] Body)>();
            await using var sender = new GameConnectionSender(owner, output, stop, 0,
                (_, opcode, _, body, _) => sent.Add((opcode, body.ToArray())));
            var context = new GameConnectionContext(new(1, IPAddress.Loopback, output, stop), owner, session, sender);
            var projection = new BattlePassProjection(season, fixture.Catalog, clock);
            Task publish = projection.ReadyAsync(context);
            await output.Started.WaitAsync(TimeSpan.FromSeconds(5));
            That(!publish.IsCompleted, "首个1800帧已开始写出，三页快照尚未发布完成");
            AccountOwners.Lease? replacement = null;
            try
            {
                if (change == "recharge")
                {
                    string answer = DevelopmentGm.Answer("recharge cera 100", owner.Account, repository, owners, _ => session);
                    That(answer.StartsWith("OK ", StringComparison.Ordinal) && session.Selected is { } credited &&
                        !ReferenceEquals(credited, selected) && credited.Id == selected.Id && session.SelectionEpoch == epoch &&
                        credited.Inventory.AccountWallet.Get(CurrencyKind.Cera) == 100 &&
                        repository.LoadCharacter(owner, 0).Inventory.AccountWallet.Get(CurrencyKind.Cera) == 100,
                        "在线GM充值实际提交，同角色与选角纪元保持");
                }
                else if (change == "reselect")
                {
                    session.ReturnToRoster();
                    session.Select(1);
                    session.MarkReady();
                    That(session.SelectionEpoch != epoch && session.Selected!.Id != selected.Id, "换角推进纪元并替换角色");
                }
                else if (change == "takeover")
                {
                    replacement = owners.Acquire(owner.Account, () => { });
                    That(!owner.IsCurrent && replacement.IsCurrent, "接管使旧owner失效");
                }
            }
            finally { output.Release(); }

            try { await publish.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (Exception error) when (change == "takeover" &&
                error is InvalidOperationException or IOException or OperationCanceledException) { }
            finally { replacement?.Dispose(); }

            if (change is "unchanged" or "recharge")
            {
                That(sent.Count >= 3 && sent.TakeLast(3).Zip(expected).All(pair =>
                    pair.First.Opcode == pair.Second.Item1 && pair.First.Body.SequenceEqual(pair.Second.Item2)),
                    "同一角色和纪元最终收到完整一致的1800、1801、1802快照：" + change);
                var restored = repository.ReadBattlePassMissions(owner, now)!;
                That(restored.Season.Revision == state.Season.Revision && restored.Season.TotalExperience == state.Season.TotalExperience &&
                    restored.Season.Claims.SetEquals(state.Season.Claims), "首快照发布和充值不产生领取或经验副作用");
            }
            else
                That(sent.All(frame => frame.Opcode != (ushort)NotiPacketType.BATTLE_PASS_INFO_MISSION &&
                    frame.Opcode != (ushort)NotiPacketType.BATTLE_PASS_INFO_REWARD),
                    "换角或接管后不向新身份补发旧任务与领奖快照");
        }
    }
}
