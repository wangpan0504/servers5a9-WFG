using System.Buffers.Binary;
using System.Net;
using DfoServer.Game.Quests;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Raids;
using DfoServer.GameWorld.Quests;
using DfoServer.Network;
using DfoServer.Network.Projection;
using DfoServer.Network.Protocol;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static partial class QuestAntonPhaseProgressTests
{
    static async Task VerifyProjectionAsync(RaidActor actor, RaidActor epochActor, AntonRaidBattle battle,
        QuestCatalog catalog, TcpTestResources resources, DungeonClearFact retainedClear)
    {
        var view = battle.View;
        uint authoritative = actor.Session.Selected!.ActiveMainQuests.Single().ConditionState;
        long revision = actor.Session.Selected!.Revision;
        using (var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
        using (var stream = new MemoryStream())
        {
            var frames = new List<(byte Kind, ushort Opcode, byte[] Body)>();
            await using var sender = new GameConnectionSender(actor.Owner, stream, stop, 0,
                (kind, opcode, _, body, _) => frames.Add((kind, opcode, body.ToArray())));
            var context = new GameConnectionContext(new(70, IPAddress.Loopback, stream, stop), actor.Owner, actor.Session, sender);
            bool Guard() => actor.Session.CanPublishRaidBattle(actor, view);
            RaidCombatProjection.PostState(context, view, actor, Guard, "anton-quest-first");
            RaidCombatProjection.PostState(context, view, actor, Guard, "anton-quest-duplicate");
            await sender.DrainAsync().WaitAsync(TimeSpan.FromSeconds(3));
            That(frames.Count == 3 && frames[0] is { Kind: 0, Opcode: 601 } &&
                frames[1] is { Kind: 1, Opcode: 33, Body.Length: 7 } && frames[2] is { Kind: 0, Opcode: 587 } &&
                BinaryPrimitives.ReadUInt32LittleEndian(frames[1].Body.AsSpan(3)) == authoritative,
                "真实sender先601再CMD33权威scalar再587，同view排队两次只写一轮");
            context.RaidDisplay = null;
            RaidCombatProjection.PostState(context, view, actor, Guard, "anton-quest-restore");
            await sender.DrainAsync().WaitAsync(TimeSpan.FromSeconds(3));
            uint client = authoritative + 1;
            foreach (var frame in frames)
            {
                if (frame is { Kind: 0, Opcode: 601 } && frame.Body[9] != 0) client--;
                if (frame is { Kind: 1, Opcode: 33 }) client = BinaryPrimitives.ReadUInt32LittleEndian(frame.Body.AsSpan(3));
            }
            That(frames.Count == 6 && client == authoritative && actor.Session.Selected!.Revision == revision,
                "恢复同阶段601后以同一已提交scalar覆盖，客户端不累计多减，服务器不再提交版本");
        }
        using (var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
        using (var stream = new MemoryStream())
        {
            bool changed = false;
            var frames = new List<(ushort Opcode, byte[] Body)>();
            await using var sender = new GameConnectionSender(actor.Owner, stream, stop, 0,
                (_, opcode, _, body, _) =>
                {
                    frames.Add((opcode, body.ToArray()));
                    if (opcode == 601 && !changed)
                    {
                        var second = retainedClear with { Identity = retainedClear.Identity with { CharacterId = epochActor.Character } };
                        var roster = battle.RecordAntonPartyClears([(actor, retainedClear), (epochActor, second)]);
                        changed = roster is not null && !ReferenceEquals(roster.Before, roster.After);
                    }
                });
            var context = new GameConnectionContext(new(73, IPAddress.Loopback, stream, stop), actor.Owner, actor.Session, sender);
            var originalView = view;
            RaidCombatProjection.PostState(context, originalView, actor,
                () => actor.Session.CanPublishRaidBattle(actor, originalView), "anton-quest-after601-view-change");
            await sender.DrainAsync().WaitAsync(TimeSpan.FromSeconds(3));
            That(changed && !battle.IsCurrent(originalView) && frames.Count == 2 && frames[0].Opcode == 601 &&
                frames[1].Opcode == 33 && BinaryPrimitives.ReadUInt32LittleEndian(frames[1].Body.AsSpan(3)) == authoritative,
                "601实际写出后同阶段名册revision变动，旧587可丢弃但紧随33仍校正相同接取身份的权威scalar");
            battle.ReleaseDungeon(actor, retainedClear.Identity.InstanceId);
            battle.ReleaseDungeon(epochActor, retainedClear.Identity.InstanceId);
            view = battle.View;
        }
        using (var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
        using (var stream = new GameSenderBudgetTests.HeldStream())
        {
            var frames = new List<(byte Kind, ushort Opcode, byte[] Body)>();
            await using var sender = new GameConnectionSender(actor.Owner, stream, stop, 0,
                (kind, opcode, _, body, _) => frames.Add((kind, opcode, body.ToArray())));
            var context = new GameConnectionContext(new(71, IPAddress.Loopback, stream, stop), actor.Owner, actor.Session, sender);
            Task held = sender.Send(1, 1347, [1], "anton-quest-held");
            await stream.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            RaidCombatProjection.PostState(context, view, actor, () => actor.Session.CanPublishRaidBattle(actor, view), "anton-quest-before-reaccept");
            var old = actor.Session.Selected!.ActiveMainQuests.Single();
            var givenUp = actor.Session.ChangeQuest(old.QuestId, QuestLifecycleAction.Giveup, catalog,
                resources.ItemResources.Items, resources.ItemResources.GoldLimits, resources.ItemResources.EpicPieces);
            var accepted = actor.Session.ChangeQuest(old.QuestId, QuestLifecycleAction.Accept, catalog,
                resources.ItemResources.Items, resources.ItemResources.GoldLimits, resources.ItemResources.EpicPieces);
            That(givenUp?.Outcome == QuestLifecycleOutcome.Committed && accepted?.Outcome == QuestLifecycleOutcome.Committed &&
                accepted.Character.ActiveMainQuests.Single().AcceptanceId != old.AcceptanceId,
                "发送泵等待期间真实放弃重接产生新AcceptanceId，不依赖直接替换Session字段");
            stream.Release.TrySetResult();
            await Task.WhenAll(held, sender.DrainAsync()).WaitAsync(TimeSpan.FromSeconds(3));
            That(frames.Single(frame => frame.Opcode == 601).Body[9] == 0 && frames.All(frame => frame.Opcode != 33) &&
                actor.Session.Selected!.ActiveMainQuests.Single().ConditionState == 5u + (5u << 9),
                "601写前重读新身份后抑制原生事件，旧接取33不能泄露给重接后的任务");
        }
        using (var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
        using (var stream = new GameSenderBudgetTests.HeldStream())
        {
            var frames = new List<ushort>();
            await using var sender = new GameConnectionSender(epochActor.Owner, stream, stop, 0,
                (_, opcode, _, _, _) => frames.Add(opcode));
            var context = new GameConnectionContext(new(72, IPAddress.Loopback, stream, stop), epochActor.Owner, epochActor.Session, sender);
            Task held = sender.Send(1, 1347, [1], "anton-quest-epoch-held");
            await stream.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            RaidCombatProjection.PostState(context, view, epochActor,
                () => epochActor.Session.CanPublishRaidBattle(epochActor, view), "anton-quest-old-epoch");
            epochActor.Session.ReturnToRoster();
            stream.Release.TrySetResult();
            await Task.WhenAll(held, sender.DrainAsync()).WaitAsync(TimeSpan.FromSeconds(3));
            That(frames.SequenceEqual(new ushort[] { 1347 }), "换选角epoch后丢弃排队中的601、33和587，不把旧任务状态发送给后继角色");
        }
    }
}
