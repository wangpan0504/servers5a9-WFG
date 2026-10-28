using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Guild;
using DfoServer.Game.Party;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Towns;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class GuildTeleportTests
{
    static readonly GuildTeleportRequest Destination = new(new(2, 0, 20, 20, 5), 0);
    static string Key(int value) => value.ToString("x64");

    public static void RunCore()
    {
        int before = Count;
        That(GuildTeleportRequests.TryBegin([2, 0, 20, 0, 20, 0, 5, 0], out var parsed) && parsed == Destination &&
            !GuildTeleportRequests.TryBegin([2, 0, 20, 0, 20, 0, 5, 1], out _) &&
            !GuildTeleportRequests.TryBegin([2, 0, 0, 128, 20, 0, 5, 0], out _) &&
            !GuildTeleportRequests.TryBegin([2, 0, 20, 0, 20, 0, 5, 0, 0], out _),
            "434公会来源0与光环来源1严格区分，坐标保留有符号16位边界且消费完整包体");
        Bytes(GuildTeleportPackets.Rejected(2), [0, 7, 0, 2, 0, 0, 0, 0, 0],
            "95公会传送拒绝使用u16错误及完整434失败尾部");
        SoloTransaction();
        PartyConfirmation();
        ChangedIdentity();
        Console.WriteLine($"PASS {Count - before} 公会传送确认、超时、全员原子提交与耐久回执断言");
    }

    static void SoloTransaction()
    {
        using var f = new Fixture(1);
        var session = f.Sessions[0];
        string original = f.Assets();
        That(!f.Service.Begin(session, Destination with { Source = 1 }, Key(1)).Success &&
            !f.Service.Begin(session, Destination with { Target = new(2, 0, 500, 500, 5) }, Key(2)).Success &&
            !f.Service.Begin(session, Destination with { Target = new(3, 0, 20, 20, 5) }, Key(3)).Success &&
            original == f.Assets(), "非法来源、不可走位置及缺前置任务均不扣次数或改角色");
        f.Sql("CREATE TRIGGER guild_tp_fail BEFORE INSERT ON guild_teleport_receipts BEGIN SELECT RAISE(ABORT,'teleport rollback'); END");
        var rollback = f.Service.Begin(session, Destination, Key(4));
        That(rollback.Success && !f.Service.Complete(rollback.Offer!).Success && original == f.Assets() &&
            f.Remaining == 3 && f.Number("SELECT count(*) FROM guild_teleport_receipts") == 0 && f.Errors.Count == 1,
            "末段回执SQL失败时次数、位置、revision及在线快照整体回滚");
        That(!f.Service.Begin(session, Destination, Key(4)).Success, "失败窗口不能用相同请求身份返回假成功");
        f.Sql("DROP TRIGGER guild_tp_fail");
        var offer = f.Service.Begin(session, Destination, Key(5)).Offer!;
        var expected = session.Selected!;
        var success = f.Service.Complete(offer);
        That(success.Success && success.Travels?.Length == 1 && f.Remaining == 2 &&
            session.Selected!.Position == Destination.Target && session.Selected.Revision == expected.Revision + 1 &&
            f.Repository.LoadCharacter(f.Owners[0], 0).Position == Destination.Target,
            "单人公会传送只提交一人位置和一次公会计数，不要求装备光环");
        f.Service.PublishAsync(success).GetAwaiter().GetResult();
        That(f.Travels.Count == 1 && f.Statuses.Count == 0, "单人直接发送实际旅行回执，不制造不存在的确认窗口");
        var replay = f.Service.Begin(session, Destination, Key(5));
        f.Service.PublishAsync(replay).GetAwaiter().GetResult();
        That(replay.Replayed && f.Travels.Count == 1 && f.Remaining == 2 && !f.Service.Complete(offer).Success,
            "重复434与重复完成不能再移动、再次扣数或重复发布旅行");
        var plan = new GuildTeleportCommitPlan(Key(5), offer.Entitlement, offer.Leader, offer.Positions!, () => true);
        That(f.Repository.CommitGuildTeleport(plan).Replayed && f.Remaining == 2 &&
            !f.Repository.CommitGuildTeleport(plan with { Positions = offer.Positions!.Select(position =>
                position with { Destination = new(2, 0, 21, 20, 5) }).ToArray() }).Success,
            "持久回执在仓储重试仍幂等，身份相同而目的地不同必须拒绝");
        var actual = session.Selected!;
        That(actual.Inventory.Gold == expected.Inventory.Gold &&
            JsonSerializer.Serialize(actual.Inventory) == JsonSerializer.Serialize(expected.Inventory) &&
            actual.TeleportCooldowns == expected.TeleportCooldowns,
            "公会传送不消费个人资产或组队光环冷却");
    }

    static void PartyConfirmation()
    {
        using var f = new Fixture(4);
        var leader = f.Sessions[0];
        string original = f.Assets();
        That(!f.Service.Begin(f.Sessions[1], Destination, Key(10)).Success,
            "真实队伍只有当前队长能发起公会传送");
        var declined = f.Service.Begin(leader, Destination, Key(11)).Offer!;
        f.Service.Activate(declined);
        var packet = GuildTeleportPackets.Status(f.Statuses[0]);
        That(packet.Length == 30 && packet[0] == 2 && packet[1] == 0 && packet[2] == 0 &&
            BitConverter.ToUInt16(packet, 3) == 0 && Enumerable.Range(0, 4).All(index =>
                BitConverter.ToUInt16(packet, 5 + index * 3) == f.Sessions[index].Selected!.Id &&
                packet[7 + index * 3] == (index == 0 ? 1 : 0)) &&
            Enumerable.Range(4, 4).All(index => BitConverter.ToUInt16(packet, 5 + index * 3) == ushort.MaxValue),
            "377完整八行使用真实队伍槽，队长默认同意，队友等待，空槽FFFF");
        f.Service.Confirm(f.Sessions[1], 0, 1);
        That(declined.Phase == 2 && original == f.Assets() && f.Remaining == 3 &&
            !f.Service.Begin(leader, Destination, Key(11)).Success,
            "任意队员拒绝取消全队窗口，不扣数，旧434重放不能假成功");
        var expired = f.Service.Begin(leader, Destination, Key(12)).Offer!;
        f.Service.Activate(expired); f.Clock.Advance(30000);
        That(expired.Phase == 3 && f.Statuses.Last().Phase == 3 && f.Remaining == 3 && original == f.Assets(),
            "真正30秒timer取消窗口，未回复的全队保持原位置与计数");
        f.Sessions[1].ObserveGuildRequest(50);
        var accepted = f.Service.Begin(leader, Destination, Key(13)).Offer!;
        f.Service.Activate(accepted);
        That(!f.Service.Confirm(f.Sessions[1], 1, 50).Success && accepted.Answers.Count == 1,
            "offer之前的确认序号不能接受当前窗口");
        f.Service.Confirm(f.Sessions[1], 1, 51); f.Service.Confirm(f.Sessions[2], 1, 1);
        That(original == f.Assets() && f.Remaining == 3, "尚有一人未同意不移动任何人也不提前扣数");
        var final = f.Service.Confirm(f.Sessions[3], 1, 1);
        That(final.Success && final.Travels?.Length == 4 && accepted.Phase == 1 && f.Remaining == 2 &&
            f.Sessions.All(session => session.Selected!.Position == Destination.Target) &&
            f.Owners.All(owner => f.Repository.LoadCharacter(owner, 0).Position == Destination.Target),
            "四人全同意后全员持久位置与一次公会计数原子提交，队友不要求同公会");
        f.Service.PublishAsync(final).GetAwaiter().GetResult();
        That(f.Travels.Count == 4 && f.Statuses.Last().Phase == 1 &&
            f.Parties.Current(leader)?.Members.Length == 4 && f.Number("SELECT count(*) FROM guild_teleport_receipts") == 1,
            "提交后每人收到实际旅行回执，真实四人队伍保留，只有一份耐久消费回执");
    }

    static void ChangedIdentity()
    {
        using var f = new Fixture(2);
        var leader = f.Sessions[0];
        var offer = f.Service.Begin(leader, Destination, Key(20)).Offer!;
        f.Service.Activate(offer);
        f.Sql("UPDATE guild_members SET joined_at_unix=joined_at_unix+1 WHERE guild_id=1");
        That(!f.Service.Confirm(f.Sessions[1], 1, 1).Success && f.Remaining == 3 && f.Travels.Count == 0,
            "退会重入造成joinedAt变化时旧窗口不能消费新会籍权益");
        var changedParty = f.Service.Begin(leader, Destination, Key(21)).Offer!;
        f.Service.Activate(changedParty); f.Parties.Leave(f.Sessions[1]);
        That(!f.Service.Confirm(f.Sessions[1], 1, 2).Success && f.Remaining == 3,
            "等待期间退队后原队伍授权不能继续提交全队位置");
        f.Service.Close(leader);
        var solo = f.Service.Begin(leader, Destination, Key(22)).Offer!;
        f.Service.Dispose();
        That(!f.Service.Complete(solo).Success && f.Remaining == 3, "退出清理后迟到完成不能写库");
    }

    sealed class Fixture : IDisposable
    {
        readonly string root = Path.Combine(Path.GetTempPath(), "guild-teleport-" + Guid.NewGuid().ToString("N"));
        readonly GuildInvitations invitations = new();
        public CharacterRepository Repository { get; }
        public AccountOwners.Lease[] Owners { get; }
        public GameSession[] Sessions { get; }
        public PartyService Parties { get; }
        public GuildTeleportService Service { get; }
        public Clock Clock { get; } = new();
        public List<GuildTeleportStatus> Statuses { get; } = [];
        public List<ushort> Travels { get; } = [];
        public List<Exception> Errors { get; } = [];
        string Database => Path.Combine(root, "game.db");
        public long Remaining => Number("SELECT value FROM guild_benefits WHERE guild_id=1 AND type=7");

        public Fixture(int count)
        {
            Directory.CreateDirectory(root); Repository = new(Database);
            var accounts = new AccountOwners();
            Owners = Enumerable.Range(0, count).Select(index => accounts.Acquire("tp-" + index, static () => { })).ToArray();
            var room = new Location(1, 0, 10, 10);
            TownArea Area(byte id, bool isRoom, int[] quests) => new(id, 0, 1, quests, [], [], new(id, 0, 10, 10),
                new("teleport.map", [new(new(0, 0, 100, 100), [], [])], [], isRoom, []), [], []);
            var towns = new TownCatalog(new() { [(1, 0)] = Area(1, true, []), [(2, 0)] = Area(2, false, []),
                [(3, 0)] = Area(3, false, [42]) }, room, new(2, 0, 10, 10), new(0, [], []), [], new Dictionary<uint, uint[]>());
            var roles = Owners.Select((owner, index) => Repository.Create(owner,
                new("Tp" + index, room, towns.InitialReturn) { Level = 60 })).ToArray();
            Sql($"""
                INSERT INTO guilds(guild_id,name,address,master_character_id,created_at_unix)
                VALUES(1,'传送公会','teleport',{roles[0].Id},1000);
                INSERT INTO guild_members(guild_id,character_id,grade,joined_at_unix) VALUES(1,{roles[0].Id},1,1000);
                INSERT INTO guild_benefits(guild_id,type,value,expires_unix) VALUES(1,7,3,0);
                """);
            Parties = new(Repository);
            Sessions = Owners.Select(owner => new GameSession(Repository, towns, owner)).ToArray();
            foreach (var session in Sessions)
            {
                session.Select(0); session.MarkReady(); session.CurrentParty = () => Parties.Current(session);
                session.GuildTeleportStatusArrived = Statuses.Add;
                session.GuildTeleportTravelArrived = receipt => { Travels.Add(receipt.Character.Id); return Task.CompletedTask; };
            }
            if (count > 1)
                foreach (var member in Sessions.Skip(1))
                    That(Parties.Invite(Sessions[0], member, 1).Accepted &&
                        Parties.Respond(member, roles[0].Id, 2, true).Accepted, "传送夹具使用真实队伍邀请及接受");
            var management = new GuildManagementService(Repository, new(), invitations, new());
            Service = new(Repository, Parties, towns, management, Clock, Errors.Add);
        }
        public string Assets() => JsonSerializer.Serialize(Owners.Select(owner => Repository.LoadCharacter(owner, 0))) +
            JsonSerializer.Serialize(Sessions.Select(session => session.Selected));
        public void Sql(string sql)
        {
            using var db = new SqliteConnection("Data Source=" + Database + ";Pooling=False"); db.Open();
            using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
        }
        public long Number(string sql)
        {
            using var db = new SqliteConnection("Data Source=" + Database + ";Pooling=False"); db.Open();
            using var command = db.CreateCommand(); command.CommandText = sql; return Convert.ToInt64(command.ExecuteScalar());
        }
        public void Dispose()
        {
            Service.Dispose(); invitations.Dispose();
            foreach (var session in Sessions) { Parties.Detach(session); session.End(); }
            foreach (var owner in Owners) owner.Dispose();
            Directory.Delete(root, true);
        }
    }

    sealed class Clock : TimeProvider
    {
        long milliseconds = 1700000000000;
        readonly List<ManualTimer> timers = [];
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(callback, state, milliseconds + (long)dueTime.TotalMilliseconds); timers.Add(timer); return timer;
        }
        public void Advance(long delta)
        {
            milliseconds += delta;
            foreach (var timer in timers.ToArray()) if (!timer.Disposed && timer.Deadline <= milliseconds) timer.Fire();
        }
        sealed class ManualTimer(TimerCallback callback, object? state, long deadline) : ITimer
        {
            public long Deadline { get; } = deadline;
            public bool Disposed { get; private set; }
            public void Fire() { if (!Disposed) callback(state); }
            public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException();
            public void Dispose() => Disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
