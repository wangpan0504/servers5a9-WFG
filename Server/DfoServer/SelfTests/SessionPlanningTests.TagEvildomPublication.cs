using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.Game.Session;
using DfoServer.Game.TagEvildom;
using DfoServer.GameWorld.Characters;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Protocol;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class SessionPlanningTests
{
    static async Task TagEvildomPublicationRacesAsync()
    {
        foreach (string mutation in new[] { "standby", "primary", "before-primary", "wallet", "expiry", "diamond-expiry", "stamina", "name-tag", "equipment-expiry", "rune-expiry", "primary-diamond", "primary-stamina", "continuous", "epoch" })
        {
            using var fixture = new Fixture();
            fixture.Sql("UPDATE characters SET level=95");
            var first = fixture.Read();
            var second = fixture.Repository.Create(fixture.Owner, new("TagRace", fixture.Towns.Room, fixture.Towns.InitialReturn)
                { Level = 95, InitialLearning = first.Learning,
                    Inventory = new(mutation is "name-tag" or "equipment-expiry" or "rune-expiry" ?
                        [new(0, new(mutation == "rune-expiry" ? Game.Inventory.Models.InventoryListType.Talisman : Game.Inventory.Models.InventoryListType.Equipment,
                            mutation == "rune-expiry" ? (short)210 : mutation == "name-tag" ? (short)30 : (short)20),
                            new(12345, mutation == "rune-expiry" ? GameWorld.Items.ItemKind.Rune : GameWorld.Items.ItemKind.Equipment, 1) { ExpireTime = 1700000010 })] : []) });
            // 合成角色只需初始CHR属性，资源成长与非空装备另由真实PVF TCP覆盖。
            fixture.Sql("DELETE FROM character_dungeon_state");
            var clock = new TagPublicationClock(DateTimeOffset.FromUnixTimeSeconds(1700000000));
            if (mutation == "expiry")
                fixture.Sql("INSERT INTO account_wallets(account,revision,premium_contracts) VALUES('planning',1,'{\"27\":1700000010}')");
            if (mutation is "diamond-expiry" or "primary-diamond")
            {
                fixture.Sql("INSERT INTO account_wallets(account,revision,premium_contracts) VALUES('planning',1,'{\"56\":1700000010}')");
                fixture.Sql("UPDATE characters SET stamina_until=1700000600 WHERE id=" + (mutation == "primary-diamond" ? first.Id : second.Id));
            }
            if (mutation is "stamina" or "primary-stamina")
                fixture.Sql("UPDATE characters SET stamina_until=1700000120 WHERE id=" + (mutation == "primary-stamina" ? first.Id : second.Id));
            var session = new GameSession(fixture.Repository, fixture.Towns, fixture.Owner,
                entry: new(new(2, 46), 53, "[evildom_gathering]"));
            session.MarkRosterPublished(session.PrepareRoster()); session.SelectFromRosterIndex(0); session.MarkReady();
            ImmutableArray<TagEvildomMemberRequest> members = [new(2, 0, 0), new(2, 1, 0), new(0, -1, 0), new(0, -1, 0), new(0, -1, 0), new(0, -1, 0)];
            var saved = session.ConfigureTagEvildom(fixture.Repository, members, 0, DateTimeOffset.UtcNow).Projection!;
            using var log = new ServerLog(Path.GetDirectoryName(fixture.Database)!);
            using var stop = new CancellationTokenSource();
            using var stream = new TagMetadataPausedStream();
            var frames = new ConcurrentQueue<ushort>();
            byte[] lastCharacterInfo = [];
            await using var sender = new GameConnectionSender(fixture.Owner, stream, stop, 0, (_, opcode, _, body, _) =>
            { frames.Enqueue(opcode); if (opcode == 1394) lastCharacterInfo = body.ToArray(); });
            var context = new GameConnectionContext(new(1, System.Net.IPAddress.Loopback, stream, stop), fixture.Owner, session, sender);
            var stats = new CharacterStatComputer(new CharacterCatalog(new() { [0] = Profile }));
            var town = new Network.Projection.TownProjection(fixture.Towns, stats, null,
                new GameWorld.Items.EpicPieceCatalog("[piece list]\n1 2\n[/piece list]\n[equipment recipe info]\n[/equipment recipe info]"));
            var projection = new Network.Projection.TagEvildomProjection(fixture.Repository, stats, log, clock, town: town);
            if (mutation == "continuous") stream.OnSecondMetadata = () => session.UpdateProgress(new(CharacterProgressField.Skill, "planning", second.Name, 5, 3));
            var projectedPrimary = new Network.Projection.TownCharacterProjection(session.Selected!, clock.Now);
            if (mutation == "before-primary") session.UpdateProgress(new(CharacterProgressField.Skill, "planning", first.Name, 5, 2));
            var publication = projection.DeliverAsync(context, saved, primarySource: projectedPrimary);
            try
            {
                if (mutation != "before-primary") await stream.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                long before = fixture.Repository.LoadCharacter(fixture.Owner, second.Slot).Revision;
                if (mutation is "standby" or "continuous") session.UpdateProgress(new(CharacterProgressField.Skill, "planning", second.Name, 5, 2));
                else if (mutation == "primary") session.UpdateProgress(new(CharacterProgressField.Skill, "planning", first.Name, 5, 2));
                else if (mutation == "before-primary") { }
                else if (mutation is "expiry" or "diamond-expiry" or "primary-diamond" or "name-tag" or "equipment-expiry" or "rune-expiry") clock.Now = DateTimeOffset.FromUnixTimeSeconds(1700000010);
                else if (mutation is "stamina" or "primary-stamina") clock.Now = DateTimeOffset.FromUnixTimeSeconds(1700000061);
                else if (mutation == "epoch")
                { session.ReturnToRoster(); session.MarkRosterPublished(session.PrepareRoster()); session.SelectFromRosterIndex(1); session.MarkReady(); }
                else fixture.Sql("INSERT INTO account_wallets(account,revision,premium_contracts) VALUES('planning',1,'{\"56\":2000000000}') " +
                    "ON CONFLICT(account) DO UPDATE SET revision=revision+1,premium_contracts=excluded.premium_contracts");
                That(fixture.Repository.LoadCharacter(fixture.Owner, second.Slot).Revision == before,
                    "交错输入保留备用revision，不能靠常规递增revision侥幸挡住");
            }
            finally { stream.Release.TrySetResult(); }
            await publication.WaitAsync(TimeSpan.FromSeconds(5));
            if (mutation is "primary" or "before-primary" or "wallet" or "expiry" or "primary-diamond" or "primary-stamina" or "equipment-expiry" or "rune-expiry")
                That(frames.SequenceEqual(mutation == "before-primary" ? new ushort[] { 1677, 1678, 1709, 7, 2 } :
                    new ushort[] { 1677, 1678, 1393, 1709, 7, 2 }) && session.Selected is null && !session.Ready,
                    "主角普通Data源的技能/共享钱包/达人/实际时间派生改变，不能仅重发pair冒充主角刷新，必须完整小退");
            else if (mutation == "continuous")
                That(frames.SequenceEqual(new ushort[] { 1677, 1678, 1393, 1393, 1709, 7, 2 }) && session.Selected is null && !session.Ready,
                    "持续变更最多重试一次后准确提示并走真实小退/新名册，不无限重试或留下半pair");
            else if (mutation == "epoch")
                That(frames.SequenceEqual(new ushort[] { 1677, 1678, 1393 }) && session.Selected?.Id == second.Id && session.Ready,
                    "旧1393后已重选新epoch，不向新角色补旧1394或主动小退误清新会话");
            else
                That(frames.SequenceEqual(new ushort[] { 1677, 1678, 1393, 1393, 1394 }) && !stop.IsCancellationRequested,
                    "一次生产Deliver在1393阻塞期间变化后跳过旧1394，并有界自动重捕获完整1393/1394，不依靠测试手动再调用");
            if (mutation is "diamond-expiry" or "stamina")
                That(lastCharacterInfo[107 + CharacterName.Encoding.GetByteCount(second.Name)] == (mutation == "stamina" ? 91 : 10),
                    "没有任何DBrevision变化，黑钻56自然到期/虚弱分钟跨界仍重捕获真实百分比，不发旧100或82");
            if (mutation == "name-tag")
                That(BinaryPrimitives.ReadUInt32LittleEndian(lastCharacterInfo.AsSpan(lastCharacterInfo.Length - 16)) == 0,
                    "1393等待跨名称装饰期限后按新时点发空名称装饰，不复活过期穿戴");
            session.End();
        }
    }

    sealed class TagPublicationClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    sealed class TagMetadataPausedStream : MemoryStream
    {
        int metadataCount;
        public Action? OnSecondMetadata { get; set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (buffer.Length >= 3 && BinaryPrimitives.ReadUInt16LittleEndian(buffer.Span[1..]) == 1393)
            {
                if (++metadataCount == 2) OnSecondMetadata?.Invoke();
                Started.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }
            await base.WriteAsync(buffer, cancellationToken);
        }
    }
}
