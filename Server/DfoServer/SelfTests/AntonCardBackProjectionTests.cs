using System.Net;
using DfoServer.Game.Raids;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Handlers;
using DfoServer.Network.Protocol;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

// 复用已提交真实阶段奖励的夹具；这里只验证生产Handler和发送泵，不另建资格或资产。
internal static class AntonCardBackProjectionTests
{
    internal static async Task VerifyQueuedAsync(RaidActor actor, AntonRaidBattle battle, string root,
        string scenario, Action change, bool expectedAck, byte card = 3)
    {
        var back = actor.Session.SelectAntonEmptyCardBack(card);
        That(back is not null && back.Value.Actor == actor && back.Value.View.Instance == battle.Instance,
            "牌背排队夹具必须已有当前阶段真实选位：" + scenario);
        var original = back!.Value.View;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var stream = new GameSenderBudgetTests.HeldStream();
        var frames = new List<(byte Kind, ushort Opcode, byte[] Body)>();
        await using var sender = new GameConnectionSender(actor.Owner, stream, stop, 0,
            (kind, opcode, _, body, _) => frames.Add((kind, opcode, body.ToArray())));
        using var log = new ServerLog(Path.Combine(root, "anton-card-back-" + scenario));
        var context = new GameConnectionContext(new(81, IPAddress.Loopback, stream, stop), actor.Owner, actor.Session, sender);
        var commands = new GameCommandRegistry();
        // kind0在生产Handler的资产依赖之前返回，不能通过替身仓储伪造奖励成功。
        new RaidCombatHandler(null!, null!, null!, null!, null!, log).Register(commands);
        commands.Seal();
        var held = sender.Send(1, 1347, [1], "anton_card_back_held");
        try
        {
            await stream.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            long epoch = actor.Session.SelectionEpoch;
            using (sender.QueuePublication(() => actor.Owner.IsCurrent && actor.Session.SelectionEpoch == epoch))
                That(await commands.DispatchAsync(context, new Packet(1, 660, [0, card])) == CommandResult.Handled,
                    "真实kind0请求进入生产Handler并在阻塞流之前完成入队：" + scenario);
            change();
            if (expectedAck)
                That(actor.IsCurrent && battle.View.Phase == original.Phase && !battle.IsCurrent(original),
                    "同阶段排队测试确实改变view且保留原actor：" + scenario);
            else
                That(!actor.IsCurrent || actor.Session.CurrentRaidBattle(actor) != battle || battle.View.Phase != original.Phase,
                    "隔离测试确实使原epoch或阶段失效：" + scenario);
            stream.Release.TrySetResult();
            await Task.WhenAll(held, sender.DrainAsync()).WaitAsync(TimeSpan.FromSeconds(3));
            That(frames.Count == (expectedAck ? 2 : 1) && frames[0].Opcode == 1347 &&
                frames.All(frame => frame.Opcode != 600),
                "旧view牌背600不覆盖窗口，同阶段ACK保留而旧epoch或阶段ACK丢弃：" + scenario);
            if (expectedAck)
                That(frames[1] is { Kind: 1, Opcode: 660, Body: [1] },
                    "同阶段变化后仍写出真实ACK660成功正文：" + scenario);
            That(sender.PendingBytes == 0, "牌背写出或丢弃均归还发送预算：" + scenario);
        }
        finally
        {
            stream.Release.TrySetResult();
        }
    }
}
