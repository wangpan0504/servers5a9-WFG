using System.Net;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Raids;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Dungeons;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Protocol;
using DfoServer.Persistence;
using DfoServer.Testing;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

// 在单成员投递委托边界注入数据库异常；不冒充真实SQLITE_BUSY或1801发包测试。
internal static class BattlePassRaidRecipientFailureTests
{
    internal static async Task RunAsync(TcpTestResources resources)
    {
        string root = Path.Combine(Path.GetTempPath(), "S5A9-raid-recipient-db-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var repository = new CharacterRepository(Path.Combine(root, "game.db"));
            var owners = new AccountOwners();
            using var firstOwner = owners.Acquire("raid-notice-first", () => { });
            using var secondOwner = owners.Acquire("raid-notice-second", () => { });
            var first = repository.Create(firstOwner, new NewCharacter("NoticeOne", resources.Towns.Room, resources.Towns.InitialReturn));
            var second = repository.Create(secondOwner, new NewCharacter("NoticeTwo", resources.Towns.Room, resources.Towns.InitialReturn));
            var firstSession = new GameSession(repository, resources.Towns, firstOwner);
            var secondSession = new GameSession(repository, resources.Towns, secondOwner);
            firstSession.Select(first.Slot); firstSession.MarkReady();
            secondSession.Select(second.Slot); secondSession.MarkReady();
            var firstActor = new RaidActor(firstSession, firstOwner, firstSession.SelectionEpoch, first.Id, first.Name, new(2, 19));
            var secondActor = new RaidActor(secondSession, secondOwner, secondSession.SelectionEpoch, second.Id, second.Name, new(2, 19));
            var battle = new AntonRaidBattle(AntonRaidBattleTests.Script(10));
            That(battle.JoinStart(firstActor) && battle.JoinStart(secondActor), "边界夹具登记两个原会话成员");
            var identity = new DungeonRunIdentity(first.Id, 0, 1, Guid.NewGuid(), 1);
            var map = new DungeonMap("boss", new(0, 0), 77, "fixture/boss.map");
            That(battle.BindDungeon(identity, 10) && battle.CompleteDungeon(new DungeonClearFact(identity, 10, [Guid.NewGuid()]) { ClearMap = map }) is not null,
                "原状态机在发布前固定Reward视图，不由故障回调重做推进");
            var committedView = battle.View;
            string assetsBefore = JsonSerializer.Serialize(new[] { repository.LoadCharacter(firstOwner, first.Slot), repository.LoadCharacter(secondOwner, second.Slot) });

            foreach (string scenario in new[] { "normal", "db-failure", "cancel-callback-failure", "non-db-failure" })
            {
                string logs = Path.Combine(root, scenario);
                using var output = new StringWriter();
                using var firstStop = new CancellationTokenSource();
                using var secondStop = new CancellationTokenSource();
                using var firstStream = new MemoryStream();
                using var secondStream = new MemoryStream();
                await using var firstSender = new GameConnectionSender(firstOwner, firstStream, firstStop, 0, (_, _, _, _, _) => { });
                await using var secondSender = new GameConnectionSender(secondOwner, secondStream, secondStop, 0, (_, _, _, _, _) => { });
                var firstContext = new GameConnectionContext(new ConnectionContext(1, IPAddress.Loopback, firstStream, firstStop), firstOwner, firstSession, firstSender);
                var secondContext = new GameConnectionContext(new ConnectionContext(2, IPAddress.Loopback, secondStream, secondStop), secondOwner, secondSession, secondSender);
                using var callback = scenario == "cancel-callback-failure"
                    ? firstStop.Token.Register(() => throw new InvalidOperationException("private cancellation detail"))
                    : default(CancellationTokenRegistration);
                int firstDelivered = 0, secondDelivered = 0;
                var nonDatabaseError = new InvalidOperationException("non-database error retains original propagation");
                using (var log = new ServerLog(logs, output))
                {
                    firstSession.RaidBattleNoticeArrived = GameProtocolHandler.CreateRaidBattleNotice(firstContext, log, (view, actor) =>
                    {
                        That(ReferenceEquals(view, committedView) && actor == firstActor, "故障注入位于原成员投递边界");
                        if (scenario is "db-failure" or "cancel-callback-failure")
                            throw new SqliteException("private SQL and database path must never be logged", 5);
                        if (scenario == "non-db-failure") throw nonDatabaseError;
                        firstDelivered++;
                    });
                    secondSession.RaidBattleNoticeArrived = GameProtocolHandler.CreateRaidBattleNotice(secondContext, log, (view, actor) =>
                    {
                        That(ReferenceEquals(view, committedView) && actor == secondActor, "后续成员收到同一已固定视图");
                        secondDelivered++;
                    });
                    Exception? observed = null;
                    try { battle.Publish(committedView); }
                    catch (Exception error) { observed = error; }
                    if (scenario == "non-db-failure")
                        That(ReferenceEquals(observed, nonDatabaseError) && secondDelivered == 0 && !firstStop.IsCancellationRequested,
                            "非数据库异常保留原传播，不新增断线或默吞");
                    else
                        That(observed is null && secondDelivered == 1 && firstDelivered == (scenario == "normal" ? 1 : 0) &&
                            firstStop.IsCancellationRequested == (scenario != "normal") && !secondStop.IsCancellationRequested,
                            "默认Publish无recipientFailure时数据库失败仅断开本成员并继续后续成员");
                    That(ReferenceEquals(battle.View, committedView) && firstActor.IsCurrent && secondActor.IsCurrent,
                        "通知失败不回滚阶段、替换owner或使原成员身份失效");
                }

                string text = output.ToString();
                string eventFile = Path.Combine(logs, "server.jsonl");
                string[] eventLines = File.Exists(eventFile) ? File.ReadAllLines(eventFile) : [];
                using var events = JsonDocument.Parse("[" + string.Join(",", eventLines) + "]");
                var rows = events.RootElement.EnumerateArray().ToArray();
                int expectedRows = scenario == "cancel-callback-failure" ? 2 : scenario == "db-failure" ? 1 : 0;
                That(rows.Length == expectedRows && rows.All(row => row.GetProperty("Kind").GetString() == "game_send_failed"),
                    "沿已有事件诊断实际故障，正常与非Db传播不制造数据库失败日志");
                if (expectedRows > 0)
                {
                    var detail = rows[0].GetProperty("Detail");
                    That(detail.GetProperty("Connection").GetInt32() == 1 && detail.GetProperty("Stage").GetString() == "raid_battle_notice" &&
                        detail.GetProperty("Reason").GetString() == nameof(SqliteException) && detail.GetProperty("SqliteCode").GetInt32() == 5 &&
                        detail.GetProperty("Instance").GetGuid() == committedView.Instance && !detail.TryGetProperty("Message", out _),
                        "诊断保连接、阶段身份及数据库类型/代码，不输出原异常正文");
                    That(text.Contains("[WARN] [game_send_failed] game send failed:") && text.Contains("Stage=raid_battle_notice") &&
                        !text.Contains('{') && !text.Contains('"') && !text.Contains("private", StringComparison.Ordinal) && text.All(c => c < 128),
                        "现有筛选输出英文纯文本WARN且不泄露异常SQL或路径");
                }
                if (scenario == "cancel-callback-failure")
                {
                    var detail = rows[1].GetProperty("Detail");
                    That(detail.GetProperty("Stage").GetString() == "raid_battle_disconnect" &&
                        detail.GetProperty("Reason").GetString() == nameof(AggregateException) && detail.GetProperty("Callbacks").GetInt32() > 0,
                        "Cancel回调聚合失败单独记录且不终止扇出");
                }
                That(JsonSerializer.Serialize(new[] { repository.LoadCharacter(firstOwner, first.Slot), repository.LoadCharacter(secondOwner, second.Slot) }) == assetsBefore,
                    "投递故障与断线不重放或修改已有角色资产");
            }
        }
        finally { TestFiles.DeleteDirectory(root, recursive: true); }
    }
}
