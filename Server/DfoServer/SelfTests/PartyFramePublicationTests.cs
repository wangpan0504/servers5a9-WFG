using DfoServer.Game.Accounts;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Party;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Towns;
using DfoServer.GameWorld.Items;
using DfoServer.Network;
using DfoServer.Network.Builders;
using DfoServer.Network.Projection;
using DfoServer.Network.Protocol;
using DfoServer.Persistence;
using DfoServer.Testing;

namespace DfoServer;

internal static class PartyFramePublicationTests
{
    public static async Task RunAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), "s5a9-party-frame-" + Guid.NewGuid().ToString("N"));
        try
        {
            var point = new Location(1, 0, 10, 10);
            var map = new TownMap("party.map", [new(new(0, 0, 20, 20), [], [])], [], false, []);
            var towns = new TownCatalog(new() { [(1, 0)] = new(1, 0, 1, [], [], [], point, map, [], []) },
                point, point, new(0, [], []), [], new Dictionary<uint, uint[]>());
            var repository = new CharacterRepository(Path.Combine(root, "game.db"));
            var owners = new AccountOwners();
            using var sourceOwner = owners.Acquire("frame-source", () => { });
            using var targetOwner = owners.Acquire("frame-target", () => { });
            repository.Create(sourceOwner, new("FrameSource", point, point)
            {
                Inventory = new([new(0, new(InventoryListType.Equipment, 12), new(31440, ItemKind.Equipment, 1))])
            });
            repository.Create(targetOwner, new("FrameTarget", point, point));
            var source = new GameSession(repository, towns, sourceOwner);
            var target = new GameSession(repository, towns, targetOwner);
            source.Select(0); source.MarkReady(); target.Select(0); target.MarkReady();
            var granted = source.PlanAndCommitInventory(character => new(true, 0,
                new(character.Inventory, character.Inventory with
                {
                    Skins = SkinStorageState.Empty with { PartyFrames = [new(420000001, 0)], SelectedPartyFrame = 420000001 }
                }, "frame_test_grant", new(character, null))));
            TestCheck.That(granted.Accepted, "延迟边框夹具通过创建后的库存事务取得所有权");
            var parties = new PartyService(repository);
            TestCheck.That(parties.Invite(source, target, 1).Accepted &&
                parties.Respond(target, source.Selected!.Id, 2, true).Accepted, "延迟边框回归使用真实双人队伍");
            using var stop = new CancellationTokenSource();
            using var stream = new GameSenderBudgetTests.HeldStream();
            await using var sender = new GameConnectionSender(targetOwner, stream, stop, 0, (_, _, _, _, _) => { });
            var context = new GameConnectionContext(new(1, System.Net.IPAddress.Loopback, stream, stop), targetOwner, target, sender);
            var held = sender.Send(0, 2, [1], "held");
            await stream.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var identity = source.PublicCharacter(source.Selected!.Id)!;
            var notice = parties.FrameChanged(source, identity).Single(n => ReferenceEquals(n.Recipient.Actor.Session, target));
            PartyProjection.Notice(context, notice);
            source.Move(new(11, 10, 0, 0, 0, 0));
            var result = source.PlanAndCommitInventory(character => new(true, 0,
                new(character.Inventory, character.Inventory with { Items = [] }, "frame_test_remove_weapon", new(character, null))));
            TestCheck.That(result.Accepted && notice.Guard!(), "源移动及无关装备revision不丢弃已提交的边框刷新");
            stream.Release.TrySetResult();
            await held; await sender.DrainAsync().WaitAsync(TimeSpan.FromSeconds(5));
            TestCheck.That(stream.Frames.Count == 3 && sender.PendingBytes == 0, "慢队友恢复后依次收到当前Actor与名册，预算全部释放");
            TestCheck.Bytes(stream.Frames[1], GameFrame.Build(0, 2, 1, TownPackets.Actor(source.Selected!, identity.PvpGrade,
                identity.Channel, adventureName: identity.AdventureName, premiumPcRoom: identity.PremiumPcRoom)),
                "出队使用最新完整Actor，变长装备数组不回滚为选择边框时的旧装备");
            var party = notice.Party!;
            TestCheck.Bytes(stream.Frames[2], GameFrame.Build(0, 9, 2, PartyPackets.Members(party.Id, 0, 0,
                party.Members.Select(m => (m.Slot, m.Actor.Character.Id)).ToArray(), party.Leader, party.Settings)),
                "边框重绘名册保持同队原槽位和队长");
            var obsolete = parties.FrameChanged(source, source.PublicCharacter(source.Selected!.Id)!)
                .Single(n => ReferenceEquals(n.Recipient.Actor.Session, target));
            parties.Leave(target);
            TestCheck.That(!obsolete.Guard!(), "接收者离队使排队中的边框与名册一并失效");
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
