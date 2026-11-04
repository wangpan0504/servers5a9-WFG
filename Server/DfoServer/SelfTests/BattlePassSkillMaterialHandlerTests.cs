using System.Buffers.Binary;
using System.Net;
using DfoServer.Game.BattlePass;
using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.BattlePass;
using DfoServer.Network;
using DfoServer.Network.Handlers.Inventory;
using DfoServer.Network.Projection;
using DfoServer.Network.Projection.Inventory;
using DfoServer.Network.Protocol;
using DfoServer.Testing;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class BattlePassSkillMaterialHandlerTests
{
    internal static async Task RunAsync(TcpTestResources resources)
    {
        var start = new DateTimeOffset(2026, 10, 29, 6, 0, 0, TimeSpan.FromHours(8));
        var policy = new BattlePassMissionPolicy("skill-material-handler", start, BattlePassCatalog.Load(resources.Archive));
        var day = Enumerable.Range(0, policy.Days).Select(offset => start.AddDays(offset).AddHours(1))
            .First(now => policy.Active(now).Any(row => row.Key is { Category: 0, Id: 15 }));
        var clock = new Clock(day);
        var items = resources.ItemResources;
        var service = new InventoryService(items.Items, items.GoldLimits, items.EpicPieces, clock: clock);
        var stats = new CharacterStatComputer(resources.Characters);
        var projection = new InventoryProjection(stats,
            new TownProjection(resources.Towns, stats, null, items.EpicPieces), items.EpicPieces);

        await CheckAsync(mixed: false, changeSelection: false);
        await CheckAsync(mixed: true, changeSelection: false);
        await CheckAsync(mixed: false, changeSelection: true);

        async Task CheckAsync(bool mixed, bool changeSelection)
        {
            using var fixture = new QuestSceneTests.Fixture(resources.Towns, resources.Characters, resources.Learning,
                resources.Growth, resources.Quests, false, level: 95, timeProvider: clock,
                inventorySetup: character => service.Grant(character, [new(3037, 20), new(1000, 6)]).Plan!);
            fixture.Repository.ConfigureBattlePassMissions(policy);
            var initial = State();
            That(Count(initial) == 0 && fixture.Read().Inventory.Find(new(InventoryListType.Main, 65)) is
                { Core.ItemId: 1000, Core.Count: 6 }, "夹具使用真实任务15日期及混合删除普通槽");
            using var stop = new CancellationTokenSource();
            using var output = new MemoryStream();
            var sent = new List<(byte Kind, ushort Opcode, byte[] Body)>();
            long selection = fixture.Session.SelectionEpoch;
            await using var sender = new GameConnectionSender(fixture.Owner, output, stop, 0, (kind, opcode, _, body, _) =>
            {
                sent.Add((kind, opcode, body.ToArray()));
                if (!changeSelection || kind != 1 || opcode != 18) return;
                // ACK已经写出后重新选角；新会话仍Ready，单靠Refresh读状态不能拦住旧回执发布。
                fixture.Session.ReturnToRoster();
                fixture.Session.Select(0);
                fixture.Session.MarkReady();
            });
            var context = new GameConnectionContext(new(1, IPAddress.Loopback, output, stop),
                fixture.Owner, fixture.Session, sender);
            var commands = new GameCommandRegistry();
            new InventoryHandler(service, projection).Register(commands);
            commands.Seal();

            // 复用既有技能材料TCP用例的原始请求，不在本用例重造protobuf编码器。
            byte[] pure = Convert.FromHexString("1000000010001A0A080210E60218DD172005200001");
            byte[] request = mixed
                ? Convert.FromHexString("1A00000010001A0A080210E60218DD1720051A080800104118062001200001")
                : pure;
            That(await commands.DispatchAsync(context, new Packet(1, 18, request)) == CommandResult.Handled,
                "真实DELETE_ITEM Handler处理支付请求");
            var paid = State();
            That(fixture.Read().Inventory.AccountWallet.Get(CurrencyKind.ClearCube) == 15 && Count(paid) == 1,
                "真实支付5无色仅计一次任务15，权威余额和进度已提交");
            byte[] ack = Convert.FromHexString(mixed
                ? "01150000000800120708E6021005180212060841100118001800"
                : "010D0000000800120708E602100518021800");
            That(sent.Count > 0 && sent[0].Kind == 1 && sent[0].Opcode == 18 && sent[0].Body.SequenceEqual(ack),
                "ACK18保留原协议实际扣费数量及混合删除结果");
            if (changeSelection)
            {
                That(fixture.Session.Ready && fixture.Session.SelectionEpoch != selection && sent.Count == 1,
                    "ACK后旧回执失效；新选角仍Ready但不补发旧事务1801");
                return;
            }

            That(sent.Select(packet => (packet.Kind, packet.Opcode)).SequenceEqual(mixed
                ? new (byte, ushort)[] { (1, 18), (0, 14), (0, 1801) }
                : new (byte, ushort)[] { (1, 18), (0, 1801) }),
                "ACK18及原背包差量先于1801，无色本身不重复发送获得通知");
            if (mixed)
            {
                var delta = sent[1].Body;
                That(delta.Length == 126 && delta[0] == 0 && U16(delta, 1) == 1 && U16(delta, 3) == 65 &&
                    BinaryPrimitives.ReadUInt32LittleEndian(delta.AsSpan(5)) == 1000 &&
                    BinaryPrimitives.ReadUInt32LittleEndian(delta.AsSpan(9)) == 5,
                    "混合请求只发布普通槽剩余5，不把无色支付当获得物品");
            }
            var mission = sent[^1].Body;
            int dailySlot = Array.FindIndex(paid.Slots.ToArray(), row => row.Instance.Key is { Category: 0, Id: 15 });
            That(mission.Length == 156 && dailySlot is >= 0 and < 4 &&
                U16(mission, 6 * dailySlot) == 15 && U16(mission, 6 * dailySlot + 2) == 1 &&
                mission[6 * dailySlot + 4] == 0 && mission[6 * dailySlot + 5] == 0,
                "原生1801每日任务15显示count1，未错误完成或就绪");

            // 余额15时请求16，等待真实Handler结束确认无额外帧，不使用超时推断静默。
            sent.Clear();
            byte[] rejected = (byte[])pure.Clone();
            rejected[17] = 16;
            That(await commands.DispatchAsync(context, new Packet(1, 18, rejected)) == CommandResult.Handled,
                "余额不足仍由真实Handler返回失败");
            var after = State();
            That(sent.Count == 1 && sent[0].Kind == 1 && sent[0].Opcode == 18 &&
                sent[0].Body.SequenceEqual(new byte[] { 0, 4, 0, 0, 0, 8, 0, 24, 1 }) &&
                fixture.Read().Inventory.AccountWallet.Get(CurrencyKind.ClearCube) == 15 && Count(after) == 1 &&
                after.Season.Revision == paid.Season.Revision,
                "拒绝仅发失败ACK18，不扣费、不推进任务或季revision、不发1801");

            BattlePassMissionState State() => fixture.Repository.ReadBattlePassMissions(fixture.Owner, clock.GetUtcNow())!;
        }
    }

    static long Count(BattlePassMissionState state) => state.Slots.Single(row => row.Instance.Key is { Category: 0, Id: 15 }).Count;
    static ushort U16(byte[] body, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(offset));
    sealed class Clock(DateTimeOffset now) : TimeProvider { public override DateTimeOffset GetUtcNow() => now; }
}
