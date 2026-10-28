using System.Buffers.Binary;
using System.Net;
using DfoServer.Game.Accounts;
using DfoServer.Game.Guild;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Towns;
using DfoServer.Network;
using DfoServer.Network.Projection;
using DfoServer.Network.Protocol;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class GuildDungeonProgressTests
{
    static async Task RunPublicationAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), "guild-progress-publication-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "game.db");
            var repository = new CharacterRepository(path);
            var owners = new AccountOwners();
            using var owner = owners.Acquire("guild-progress-publication", static () => { });
            var room = new Location(38, 1, 450, 234, 0);
            var character = repository.Create(owner, new("ProgressWire", room, room));
            void Sql(string sql)
            {
                using var db = new SqliteConnection("Data Source=" + path + ";Pooling=False");
                db.Open(); using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
            }
            Sql($"""
                INSERT INTO guilds(guild_id,name,address,master_character_id,created_at_unix) VALUES(1,'进度','progress',{character.Id},1000);
                INSERT INTO guild_members(guild_id,character_id,grade,joined_at_unix) VALUES(1,{character.Id},1,1000);
                INSERT INTO guild_dungeon_progress(guild_id,dungeon_id,clear_count,last_clear_unix) VALUES(1,101,1,1000);
                """);
            var towns = new TownCatalog([], room, room, new(0, [], []), [], new Dictionary<uint, uint[]>());
            var session = new GameSession(repository, towns, owner);
            session.Select(character.Slot); session.MarkReady();
            var online = new OnlineSessionDirectory();
            using var invitations = new GuildInvitations();
            var management = new GuildManagementService(repository, online, invitations, new GuildPermitRegistry());
            var projection = new GuildManagementProjection(management, new(repository, online))
                { Dungeons = new(Fixture()) };
            using var stop = new CancellationTokenSource();
            using var output = new HeldOutput();
            await using var sender = new GameConnectionSender(owner, output, stop, 0, (_, _, _, _, _) => { });
            var context = new GameConnectionContext(new(1, IPAddress.Loopback, output, stop), owner, session, sender);
            sender.Post(0, 0, [], "test_barrier");
            await output.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            try
            {
                projection.Post(context, new(GuildManagementNoticeKind.Refresh, 1));
                Sql("UPDATE guild_dungeon_progress SET clear_count=2");
                projection.Post(context, new(GuildManagementNoticeKind.Refresh, 1));
            }
            finally { output.Release.TrySetResult(); }
            await sender.DrainAsync().WaitAsync(TimeSpan.FromSeconds(5));
            var progress = output.Frames.Where(frame => BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(1)) == 744).ToArray();
            That(progress.Length == 1 && BinaryPrimitives.ReadUInt32LittleEndian(progress[0].AsSpan(16 + 21)) == 2,
                "发送泵阻塞期间连续刷新使旧回执失效，最后一次普通贡献刷新仍发布最新真实744计数");
            session.ReturnToRoster();
            projection.Post(context, new(GuildManagementNoticeKind.Refresh, 1));
            await sender.DrainAsync().WaitAsync(TimeSpan.FromSeconds(5));
            That(output.Frames.Count(frame => BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(1)) == 744) == 1,
                "退回选角后不能继续投递公会副本进度");
        }
        finally { Directory.Delete(root, true); }
    }

    sealed class HeldOutput : MemoryStream
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<byte[]> Frames { get; } = [];
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Frames.Count == 0)
            {
                Started.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }
            Frames.Add(buffer.ToArray());
        }
    }
}
