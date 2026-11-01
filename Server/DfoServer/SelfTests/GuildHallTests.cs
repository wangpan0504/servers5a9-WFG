using DfoServer.Game.Accounts;
using DfoServer.Game.Session;
using DfoServer.Game.Town;
using DfoServer.GameWorld.Towns;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class GuildHallTests
{
    public static void RunCore()
    {
        int before = Count;
        var room = new Location(38, 1, 100, 100);
        var outside = new Location(38, 0, 100, 100);
        var hall = new Location(8, 0, 100, 100);
        TownArea Area(Location point, string path, bool isRoom, params Location[] destinations) =>
            new(point.Town, point.Area, 1, [], [], [], point,
                new(path, [new(new(0, 0, 3000, 1000), [], [])],
                    destinations.Select(p => new Link(new(90, 90, 20, 20), p.Town, p.Area, [], [])).ToArray(), isRoom, []), [], []);
        var towns = new TownCatalog(new()
        {
            [(38, 1)] = Area(room, "map/village/gate_seria.map", true, outside),
            [(38, 0)] = Area(outside, "map/village/street.map", false, room, hall),
            [(8, 0)] = Area(hall, "map/guildagit/guildagit.map", false, outside)
        }, room, outside, new(0, [], []), [], new Dictionary<uint, uint[]>());
        var scene = towns.Scene(hall, new HashSet<int>());
        That(scene.OwnerKey("a", 1, 51) == scene.OwnerKey("b", 2, 51) &&
            scene.OwnerKey("a", 1, 51) != scene.OwnerKey("c", 3, 52) &&
            scene.OwnerKey("a", 1) != scene.OwnerKey("b", 2),
            "同公会大厅共享场景，不同公会及无会籍残留各自隔离");
        string path = Path.Combine(Path.GetTempPath(), "guild-hall-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            var repository = new CharacterRepository(path);
            var owners = new AccountOwners();
            using var memberOwner = owners.Acquire("hall-member", static () => { });
            using var guestOwner = owners.Acquire("hall-guest", static () => { });
            var member = repository.Create(memberOwner, new("HallMember", room, outside));
            repository.Create(guestOwner, new("HallGuest", room, outside));
            using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false, ForeignKeys = true }.ToString());
            db.Open();
            void Sql(string sql)
            {
                using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
            }
            Sql($"INSERT INTO guilds(guild_id,name,address,master_character_id,created_at_unix) VALUES(51,'Hall guild','hall',{member.Id},1600000000);" +
                $"INSERT INTO guild_members(guild_id,character_id,grade,joined_at_unix) VALUES(51,{member.Id},0,1600000000)");
            var memberSession = new GameSession(repository, towns, memberOwner);
            var guestSession = new GameSession(repository, towns, guestOwner);
            memberSession.Select(0); memberSession.MarkReady();
            guestSession.Select(0); guestSession.MarkReady();
            var menuHall = new Location(8, 0, 2110, 270);
            var menu = new DfoServer.Game.Town.AreaRequest(8, 0, 2110, 270, 5, 38, 1, 0);
            That(!TownTravel.Plan(memberSession.Selected!, menu with { X = 2111 }, towns, new HashSet<int>()).Accepted &&
                TownTravel.Plan(memberSession.Selected!, menu with { PreviousArea = 0 }, towns, new HashSet<int>()).Reason == "source-area-mismatch",
                "菜单例外仅认可本版精确形状与当前来源，不扩成任意跨城传送");
            // 本版大厅地形步行区止于 x≈1040，客户端菜单仍发旧版 2110/270：必须改用地图自己的落点，而不是拒绝。
            var tight = new TownCatalog(new()
            {
                [(38, 1)] = Area(room, "map/village/gate_seria.map", true, outside),
                [(38, 0)] = Area(outside, "map/village/street.map", false, room, hall),
                [(8, 0)] = new TownArea(8, 0, 1, [], [], [], hall,
                    new("map/guildagit/guildagit.map", [new(new(200, 200, 800, 400), [], [])],
                        [new Link(new(90, 90, 20, 20), 38, 0, [], [])], false, []), [], [])
            }, room, outside, new(0, [], []), [], new Dictionary<uint, uint[]>());
            var shortened = TownTravel.Plan(memberSession.Selected!, menu, tight, new HashSet<int>());
            That(shortened is { Reason: "ready", Position: { X: 600, Y: 400 } },
                "旧版菜单坐标落在现版步行区外时改用地形首块可走矩形中心，不再按不可站拒绝");
            long beforeMenu = guestSession.Selected!.Revision;
            That(!guestSession.Travel(menu) && guestSession.TravelReason == "guild-membership-required" &&
                guestSession.Selected.Revision == beforeMenu,
                "菜单直达仍在保存事务复核会籍，无会籍不写位置");
            That(memberSession.Travel(menu) && memberSession.Selected!.Position == menuHall &&
                memberSession.Selected.Return == outside,
                "本版固定公会菜单可从房间直达，保留普通街区返回点");
            var returnToStreet = new DfoServer.Game.Town.AreaRequest(38, 0, 100, 100, 5, 8, 0, 0);
            That(memberSession.Travel(returnToStreet), "菜单进厅后仍可按普通有向门回街道");
            var toRoom = new DfoServer.Game.Town.AreaRequest(38, 1, 100, 100, 5, 38, 0, 0);
            That(memberSession.Travel(toRoom), "回街道后仍可正常进房间");
            That(memberSession.LeaveRoom() && guestSession.LeaveRoom(), "真实Session从房间经合法有向门出街");
            var request = new DfoServer.Game.Town.AreaRequest(hall.Town, hall.Area, hall.X, hall.Y, 5, outside.Town, outside.Area, 0);
            var deniedBefore = guestSession.Selected!;
            That(!guestSession.Travel(request) && guestSession.TravelReason == "guild-membership-required" &&
                ReferenceEquals(guestSession.Selected, deniedBefore) && repository.LoadCharacter(guestOwner, 0).Revision == deniedBefore.Revision,
                "无会籍主动进大厅被拒，Selected及持久位置版本不变化");
            That(memberSession.Travel(request) && memberSession.Selected!.Position == hall,
                "当前会籍在真实writer事务内确认后允许主动进入大厅");
            long revision = memberSession.Selected!.Revision;
            Sql($"DELETE FROM guild_members WHERE character_id={member.Id}");
            That(!memberSession.Move(new(101, 100, 5, 100, 0, 0)) &&
                memberSession.Selected.Revision == revision && repository.LoadCharacter(memberOwner, 0).Revision == revision,
                "会籍撤销后的旧会话不能靠大厅内行走重新保存位置");
            var evacuated = memberSession.RefreshGuildManagement(repository, 51);
            That(evacuated is { EvacuatedHall: true, State: null } && memberSession.Selected!.Position == outside &&
                memberSession.Selected.Guild is null && repository.LoadCharacter(memberOwner, 0).Position == outside,
                "离会通知安装真实空会籍并持久迁回普通返回地区");
            That(!memberSession.Travel(request) && memberSession.TravelReason == "guild-membership-required",
                "被迁出后旧入场参数不能重新进入大厅");
        }
        finally
        {
            SqliteConnection.ClearAllPools(); TestFiles.DeleteFile(path); TestFiles.DeleteFile(path + "-wal"); TestFiles.DeleteFile(path + "-shm");
        }
        Console.WriteLine($"PASS {Count - before} 公会大厅会籍准入、撤销与持久迁出核心断言");
    }
}
