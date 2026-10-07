using System.Net;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Handlers;
using DfoServer.Network.Protocol;
using DfoServer.Persistence;
using PvfLib;
using DfoServer.Testing;
using Microsoft.Data.Sqlite;

namespace DfoServer;

internal static class SkillPublicationTests
{
    public static async Task RunAsync(TownCatalog towns, PvfArchive pvf, CharacterCatalog characters)
    {
        int before = TestCheck.Count;
        string root = Path.Combine(Path.GetTempPath(), "S5A9-skill-publication-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var catalog = SkillLearningCatalog.Load(pvf);
            await CommittedFailureAsync(towns, catalog, characters, root);
            await QueuedReplyAsync(towns, catalog, characters, root, reselect: true);
            await QueuedReplyAsync(towns, catalog, characters, root, reselect: false);
        }
        finally
        {
            Directory.Delete(root, true);
        }
        Console.WriteLine($"PASS {TestCheck.Count - before} 技能提交与发布断言");
    }

    private static async Task CommittedFailureAsync(TownCatalog towns, SkillLearningCatalog catalog, CharacterCatalog characters, string root)
    {
        string path = Path.Combine(root, "game.db");
        var repository = new CharacterRepository(path);
        var owners = new AccountOwners();
        using var owner = owners.Acquire("skill-publication", () => { });
        Character original = repository.Create(owner, SkillRepositoryTests.Fixture("SkillSend", towns, characters, catalog));
        var commands = new GameCommandRegistry();
        using var log = new ServerLog(root);
        new SkillHandler(catalog, log).Register(commands);
        commands.Seal();
        var session = new GameSession(repository, towns, owner);
        session.Select(original.Slot);
        session.MarkReady();
        Character selected = session.Selected!;
        byte slot = selected.Learning.Trees.Single(tree => tree.Context == 0).Skills.Single(skill => skill.Id == 5).Slot;
        int publications = 0;
        using var failedStop = new CancellationTokenSource();
        using var failedStream = new FailedOutput(() =>
        {
            // ACK 的真实写入入口使用独立连接查询，验证扣费已提交而不是仅内存改变。
            using var db = new SqliteConnection("Data Source=" + path + ";Pooling=False");
            db.Open();
            using var query = db.CreateCommand();
            query.CommandText = "SELECT sp FROM character_skill_points WHERE character_id=$id AND context=0";
            query.Parameters.AddWithValue("$id", selected.Id);
            TestCheck.That((long)query.ExecuteScalar()! == 45, "ACK写入时独立SQL连接已见扣15SP");
            query.CommandText = "SELECT level FROM character_skill_bindings WHERE character_id=$id AND context=0 AND skill_id=5";
            TestCheck.That((long)query.ExecuteScalar()! == 2, "ACK写入时技能等级已提交");
        });
        await using var failedSender = new GameConnectionSender(owner, failedStream, failedStop, 0,
            (_, _, _, _, _) => publications++);
        var failedContext = new GameConnectionContext(new(1, IPAddress.Loopback, failedStream, failedStop), owner, session, failedSender);
        var purchase = Request([0, 1, 5, 0, 0, 1, 0]);
        await RejectAsync<IOException>(() => commands.DispatchAsync(failedContext, purchase), "真实Handler提交后ACK写失败传播");
        TestCheck.That(failedStream.Writes == 1 && publications == 0 && failedStop.IsCancellationRequested,
            "发送失败只写一次、取消连接且无成功发布回调");
        Character committed = repository.List(owner).Single();
        TestCheck.That(committed.Revision == selected.Revision + 1 && committed.Learning.Experience == 2035 &&
            committed.Skills.Single(skill => skill.Id == 5).Level == 2 &&
            committed.Learning.Trees.Single(tree => tree.Context == 0).Skills.Single(skill => skill.Id == 5).Slot == slot,
            "发送失败保留已提交revision、主树等级和稳定槽位");
        using var fresh = owners.Acquire("skill-publication", () => { });
        await RejectAsync<InvalidOperationException>(() => commands.DispatchAsync(failedContext, purchase), "旧owner不能重跑已提交学习");
        var reopened = new CharacterRepository(path);
        TestCheck.That(SkillRepositoryTests.Equal(reopened.List(fresh).Single(), committed), "freshowner重读相同SP等级槽位不重扣费");

        // 已知失败保持连接可继续；未知状态只请求关闭，不制造失败码。
        NewCharacter fixture = SkillRepositoryTests.Fixture("SkillZero", towns, characters, catalog);
        fixture = fixture with { InitialLearning = fixture.InitialLearning with
        {
            Trees = Array.AsReadOnly(fixture.InitialLearning.Trees.Select(tree => tree with { Sp = 0 }).ToArray())
        } };
        Character zero = reopened.Create(fresh, fixture);
        var freshSession = new GameSession(reopened, towns, fresh);
        freshSession.Select(zero.Slot);
        freshSession.MarkReady();
        Character before = freshSession.Selected!;
        using var freshStop = new CancellationTokenSource();
        using var freshBytes = new MemoryStream();
        var packets = new List<Packet>();
        await using var freshSender = new GameConnectionSender(fresh, freshBytes, freshStop, 0,
            (kind, opcode, _, body, _) => packets.Add(new(kind, opcode, body.ToArray())));
        var context = new GameConnectionContext(new(2, IPAddress.Loopback, freshBytes, freshStop), fresh, freshSession, freshSender);
        var healthySession = new GameSession(reopened, towns, fresh);
        healthySession.Select(committed.Slot);
        healthySession.MarkReady();
        var healthyContext = new GameConnectionContext(new(3, IPAddress.Loopback, freshBytes, freshStop), fresh, healthySession, freshSender);
        TestCheck.That(await commands.DispatchAsync(healthyContext, Request([1, 1, 5, 0, 0, 1, 1])) == CommandResult.Handled,
            "真实Handler context1正向单阶学习成功");
        TestCheck.That(packets.Count == 1 && packets[0].Body.SequenceEqual(new byte[]
            { 1, 1, 45, 0, 0, 0, 1, slot, 5, 0, 2, 0, 1 }),
            "成功仅13字节ACK含真实slot、目标等级、两余额和原mode");
        TestCheck.That(healthySession.Selected!.Skills.SequenceEqual(committed.Skills) &&
            healthySession.Selected.Learning.Trees.Single(tree => tree.Context == 0).Skills.SequenceEqual(
                committed.Learning.Trees.Single(tree => tree.Context == 0).Skills), "context1成功发布保持主树技能事实");
        packets.Clear();
        TestCheck.That(await commands.DispatchAsync(context, Request([0, 1, 255, 255, 0, 1, 0])) == CommandResult.Handled,
            "不存在技能返回已知拒绝且连接可继续");
        TestCheck.That(await commands.DispatchAsync(context, purchase) == CommandResult.Handled && !freshStop.IsCancellationRequested,
            "同连接后续SP不足请求继续处理");
        TestCheck.That(packets.Count == 2 && packets.All(packet => packet.Kind == 1 && packet.Opcode == (ushort)CmdPacketType.BUY_SKILL) &&
            packets[0].Body.SequenceEqual(new byte[] { 0, 0x15, 0 }) && packets[1].Body.SequenceEqual(new byte[] { 0, 0x16, 0 }),
            "两次拒绝均仅完整3字节ACK29，没有伪成功或额外刷新");
        TestCheck.That(await commands.DispatchAsync(context, Request([0, 1, 5, 0, 0, 1, 3])) == CommandResult.CloseConnection,
            "未支持mode3请求关闭且不猜失败码");
        await RejectAsync<InvalidDataException>(() => commands.DispatchAsync(context, Request([0, 1, 5, 0, 0, 1])), "真实Handler短包抛出协议拒绝");
        await RejectAsync<InvalidDataException>(() => commands.DispatchAsync(context, Request([0, 1, 5, 0, 0, 1, 0, 0])), "真实Handler长包抛出协议拒绝");
        TestCheck.That(packets.Count == 2 && SkillRepositoryTests.Equal(reopened.List(fresh).Single(character => character.Id == zero.Id), before),
            "失败和非法包不改SQLite事实且不发布未知ACK");
    }

    private static async Task QueuedReplyAsync(TownCatalog towns, SkillLearningCatalog catalog, CharacterCatalog characters, string root, bool reselect)
    {
        var repository = new CharacterRepository(Path.Combine(root, "queued-" + reselect + ".db"));
        var owners = new AccountOwners();
        using var owner = owners.Acquire("skill-queued", () => { });
        var original = repository.Create(owner, SkillRepositoryTests.Fixture("QueuedSkill", towns, characters, catalog));
        var session = new GameSession(repository, towns, owner);
        session.Select(original.Slot);
        session.MarkReady();
        using var log = new ServerLog(root);
        var commands = new GameCommandRegistry();
        new SkillHandler(catalog, log).Register(commands);
        commands.Seal();
        using var stop = new CancellationTokenSource();
        using var stream = new HeldOutput();
        var sent = new List<string>();
        await using var sender = new GameConnectionSender(owner, stream, stop, 0, (_, _, _, _, action) => sent.Add(action));
        var context = new GameConnectionContext(new(4, IPAddress.Loopback, stream, stop), owner, session, sender);
        Task first = sender.Send(1, 0, [], "held");
        await stream.Started.WaitAsync(TimeSpan.FromSeconds(5));
        Task<CommandResult>? publication = null;
        try
        {
            long revision = session.Selected!.Revision;
            publication = commands.DispatchAsync(context, Request([0, 1, 5, 0, 0, 1, 0]));
            TestCheck.That(session.Selected!.Revision == revision + 1 && !publication.IsCompleted &&
                repository.LoadCharacter(owner, original.Slot).Learning.Trees[0].Sp == 45, "ACK排队前技能已提交且只扣一次");
            if (reselect)
            {
                session.ReturnToRoster();
                session.Select(original.Slot);
                session.MarkReady();
            }
            else
            {
                session.UpdateProgress(new(CharacterProgressField.Level, owner.Account, original.Name, 11, 0));
                TestCheck.That(session.Selected!.Revision == revision + 1, "排队期间GM保持原revision");
            }
        }
        finally { stream.Release(); }
        await first.WaitAsync(TimeSpan.FromSeconds(5));
        TestCheck.That(await publication!.WaitAsync(TimeSpan.FromSeconds(5)) == CommandResult.Handled && sent.SequenceEqual(["held"]),
            "重选或GM替换快照后排队的旧ACK不发布");
        TestCheck.That(repository.LoadCharacter(owner, original.Slot).Learning.Trees[0].Sp == 45 && !stop.IsCancellationRequested,
            "过期ACK跳过保留提交且不因跳过重扣费用");
        await commands.DispatchAsync(context, Request([1, 1, 5, 0, 0, 1, 1]));
        TestCheck.That(sent.SequenceEqual(["held", "skill_purchase_committed"]), "当前快照的新学习正常通过发布检查");
    }

    private sealed class HeldOutput : MemoryStream
    {
        readonly TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Started => started.Task;
        public void Release() => release.TrySetResult();
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            started.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            await base.WriteAsync(buffer, cancellationToken);
        }
    }

    private static Packet Request(byte[] body) => new(1, (ushort)CmdPacketType.BUY_SKILL, body);

    private static async Task RejectAsync<T>(Func<Task> action, string name) where T : Exception
    {
        try
        {
            await action();
        }
        catch (T)
        {
            TestCheck.That(true, name);
            return;
        }
        throw new InvalidOperationException("FAIL expected " + typeof(T).Name + ": " + name);
    }

    private sealed class FailedOutput(Action beforeFailure) : MemoryStream
    {
        public int Writes { get; private set; }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Writes++;
            beforeFailure();
            return ValueTask.FromException(new IOException("技能ACK输出失败fixture"));
        }
    }
}
