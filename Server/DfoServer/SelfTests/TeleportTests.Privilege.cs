using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Text.Json;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.PrivilegeServices;
using DfoServer.Game.Town;
using DfoServer.GameWorld.Channels;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class TeleportTests
{
    static void PrivilegeTeleport()
    {
        var dirty = Enumerable.Repeat((byte)0xff, 19).ToArray();
        dirty[13] = 2; dirty[14] = 0;
        BinaryPrimitives.WriteUInt16LittleEndian(dirty.AsSpan(15), 513);
        BinaryPrimitives.WriteUInt16LittleEndian(dirty.AsSpan(17), 65535);
        var clean = (byte[])dirty.Clone(); Array.Clear(clean, 0, 13);
        That(PrivilegeTeleportRequests.Parse(dirty) == new PrivilegeTeleportRequest(new(2, 0, 513, 65535)) &&
            PrivilegeTeleportRequests.Parse(dirty) == PrivilegeTeleportRequests.Parse(clean),
            "669只取末六字节u8/u8/u16/u16，13字节脏栈不能成为角色或资格输入");
        foreach (int width in new[] { 0, 18, 20, 21 })
            Reject<InvalidDataException>(() => PrivilegeTeleportRequests.Parse(new byte[width]), "669拒绝截断、追加或套用领取正文");
        Bytes(PrivilegeTeleportPackets.Committed(), Convert.FromHexString("010000000000"), "669成功公共信封加5字节无语义零占位");
        Bytes(PrivilegeTeleportPackets.Rejected(true), Convert.FromHexString("0002000000000000"), "669地区失败公共error2后仍有5字节");
        Bytes(PrivilegeTeleportPackets.Rejected(false), Convert.FromHexString("0001000000000000"), "669其他失败公共error1不触发成功冷却");
        That(ReadPrivilegeTeleportAck(PrivilegeTeleportPackets.Committed()) == (true, (ushort)0) &&
            ReadPrivilegeTeleportAck(PrivilegeTeleportPackets.Rejected(true)) == (false, (ushort)2) &&
            ReadPrivilegeTeleportAck(PrivilegeTeleportPackets.Rejected(false)) == (false, (ushort)1),
            "公共派发和978D00额外5字节消费者均有完整正文");
        Reject<EndOfStreamException>(() => ReadPrivilegeTeleportAck([1]), "旧通用成功缺少私有5字节，不能作为完整ACK");
        Reject<EndOfStreamException>(() => ReadPrivilegeTeleportAck([0, 2, 0]), "失败也必须满足私有读取");

        using var fixture = new Fixture(channel: new(new(2, 10), 22, "[normal]"));
        var session = fixture.Session;
        var request = new PrivilegeTeleportRequest(new(2, 0, 30, 40, 9));
        That(!session.TeleportPrivilege(request, fixture.Service).Accepted, "未选角不能礼遇传送");
        long at = fixture.Clock.Now / 1000;
        int day = PrivilegeServicePolicy.Day(fixture.Clock.GetUtcNow());
        var active = new PrivilegeServiceState(at, at + 60, 1, 1, day, 1, 0, 0);
        var stored = fixture.Read();
        fixture.Repository.CommitInventory(fixture.Owner, stored, new(stored.Inventory,
            stored.Inventory with { PrivilegeService = active }, "fixture_privilege_teleport"));
        fixture.Sql("INSERT INTO character_teleport_cooldowns(character_id,account,potion_until,aura_until,upgrade_aura_until,party_aura_until,privilege_until) " +
            "VALUES(1,'teleport',10,20,30,40,0)");
        session.Select(0);
        That(!session.TeleportPrivilege(request, fixture.Service).Accepted, "选角未Ready不发布传送成功");
        session.MarkReady();
        var before = session.Selected!;
        var plan = fixture.Service.Privilege(before with { Position = before.Position with { Direction = 7 } }, request,
            session.ChannelEntry, fixture.Clock.Now).Plan;
        That(plan is not null && plan.Position == (request.Target with { Direction = 7 }) &&
            plan.Cooldowns == new TeleportCooldowns(10, 20, 30, 40, fixture.Clock.Now + 5000) &&
            plan.Inventory.After.PrivilegeService == active,
            "恰开始资格可传送，冷却来自5秒资源，朝向沿角色且不改变资格账本");
        foreach (var state in new[] { PrivilegeServiceState.Empty, active with { StartedAt = at + 1 }, active with { StartedAt = at - 60, ExpiresAt = at } })
            That(fixture.Service.Privilege(before with { Inventory = before.Inventory with { PrivilegeService = state } }, request,
                session.ChannelEntry, fixture.Clock.Now).Plan is null, "未购买、未来开始、恰到期资格均不能传送");
        foreach (int type in new[] { 4, 5, 8, 13, 24, 55, 40, 27, 39, 23, 32, 25, 45, 50, 62, 63, 20, 31, 41, 59, 60 })
            That(fixture.Service.Privilege(before, request, new(new(2, 10), type, "[test]"), fixture.Clock.Now).Reason ==
                "privilege-teleport-area-unavailable", "原生专用/团队频道拒绝礼遇传送：" + type);
        That(fixture.Service.Privilege(before, request, null, fixture.Clock.Now).Plan is null,
            "缺少真实频道上下文不能假定为普通频道");
        foreach (int type in new[] { 11, 17, 22, 56 })
            That(fixture.Service.Privilege(before, request, new(new(2, 10), type, "[test]"), fixture.Clock.Now).Plan is not null,
                "已知拒绝集外的合法城镇频道不被缩窄成仅22：" + type);
        foreach (var target in new[] { new DfoServer.GameWorld.Towns.Location(1, 0, 10, 10), new(3, 0, 10, 10),
            new(2, 0, 101, 20), new(255, 0, 10, 10), new(57, 2, 10, 10) })
            That(!session.TeleportPrivilege(new(target), fixture.Service).Accepted,
                "私有房间、未完成任务、越界、未知和团队目的地拒绝");
        That(!session.TeleportPrivilege(request, fixture.Service, blocked: true).Accepted &&
            ReferenceEquals(session.Selected, before) && JsonSerializer.Serialize(fixture.Read()) == JsonSerializer.Serialize(before),
            "被占用操作和非法目的地均不修改位置、资格、冷却或revision");

        fixture.Sql("CREATE TRIGGER privilege_teleport_fail BEFORE UPDATE ON character_teleport_cooldowns BEGIN SELECT RAISE(ABORT,'privilege rollback'); END");
        Reject<SqliteException>(() => session.TeleportPrivilege(request, fixture.Service), "礼遇位置写入后冷却SQL失败");
        That(ReferenceEquals(session.Selected, before) && JsonSerializer.Serialize(fixture.Read()) == JsonSerializer.Serialize(before),
            "末段SQL失败同时回滚位置、全部五类冷却和在线快照");
        fixture.Sql("DROP TRIGGER privilege_teleport_fail");
        var committed = session.TeleportPrivilege(request, fixture.Service);
        long until = fixture.Clock.Now + 5000;
        That(committed.Accepted && session.CanPublishTravel(committed.Receipt!.Travel) &&
            fixture.Read().Position == (request.Target with { Direction = before.Position.Direction }) &&
            fixture.Read().TeleportCooldowns == new TeleportCooldowns(10, 20, 30, 40, until) &&
            fixture.Read().Revision == before.Revision + 1 && fixture.Read().Inventory.PrivilegeService == active,
            "成功前完成真实位置和独立冷却原子提交，回执可发布且资格不被重置");
        That(!session.TeleportPrivilege(request, fixture.Service).Accepted, "重复请求冷却内不再移动或延长截止");

        fixture.OnAuraRead = () => fixture.Clock.Now += 123;
        var aura = session.TeleportSolo(new(-1, -1, new(2, 0, 31, 41)), fixture.Service, null);
        fixture.OnAuraRead = null;
        That(aura.Accepted && fixture.Read().TeleportCooldowns.PrivilegeUntil == until &&
            fixture.Read().TeleportCooldowns.AuraUntil == fixture.Clock.Now + 420000,
            "光环规划耗时只平移本次新光环冷却，不清零或延长已有礼遇冷却");
        That(session.TeleportPotion(fixture.Request, fixture.Service).Accepted && fixture.Read().TeleportCooldowns.PrivilegeUntil == until,
            "药剂传送提交也保留第五礼遇冷却");
        foreach (int id in new[] { 101590011, 65051, 65052 })
        {
            var selected = session.Selected!;
            var oldAura = selected.Inventory.Find(new(InventoryListType.Equipment, 9))!;
            var role = selected with { TeleportCooldowns = new(10, 20, 30, 40, until),
                Inventory = selected.Inventory with { Items = selected.Inventory.Items.Replace(oldAura, oldAura with { Core = oldAura.Core with { ItemId = id } }) } };
            var other = id == 65052 ? fixture.Service.Party(role, new(new(2, 0, 20, 20), 1), fixture.Clock.Now) :
                fixture.Service.Solo(role, new(-1, -1, new(2, 0, 20, 20)), null, fixture.Clock.Now);
            That(other.Plan?.Cooldowns.PrivilegeUntil == until, "普通、高级和组队光环规划均保留礼遇截止：" + id);
        }
        session.ReturnToRoster(); session.Select(0); session.MarkReady();
        That(!session.CanPublishTravel(committed.Receipt!.Travel) && session.Selected!.TeleportCooldowns.PrivilegeUntil == until &&
            !session.TeleportPrivilege(request, fixture.Service).Accepted, "重选使旧回执失效，持久礼遇冷却继续生效");
        fixture.Clock.Now = until - 1;
        That(!session.TeleportPrivilege(request, fixture.Service).Accepted, "礼遇冷却截止前1毫秒拒绝");
        fixture.Clock.Now = until;
        That(session.TeleportPrivilege(request, fixture.Service).Accepted, "恰好截止时再次允许，不借光环冷却拦礼遇");

        var expected = fixture.Read();
        var stalePlan = fixture.Service.Privilege(expected, request, session.ChannelEntry, fixture.Clock.Now + 5000).Plan!;
        fixture.Repository.CommitInventory(fixture.Owner, expected, new(expected.Inventory,
            expected.Inventory with { Gold = expected.Inventory.Gold + 1 }, "fixture_new_revision"));
        Reject<InvalidOperationException>(() => fixture.Repository.CommitTeleport(fixture.Owner, stalePlan), "旧revision礼遇计划不可覆盖后续角色资产");
        var fresh = fixture.Read();
        var expiresPlan = fixture.Service.Privilege(fresh, request, session.ChannelEntry, fixture.Clock.Now + 5000).Plan!;
        fixture.Clock.Now = active.ExpiresAt * 1000;
        Reject<InventoryQualificationExpiredException>(() => fixture.Repository.CommitTeleport(fixture.Owner, expiresPlan), "规划后到期必须在原位置事务中复验期限");
        That(JsonSerializer.Serialize(fixture.Read()) == JsonSerializer.Serialize(fresh), "过期计划不写位置、冷却或revision");
        using var replacement = fixture.Owners.Acquire(fixture.Owner.Account, () => { });
        Reject<InvalidOperationException>(() => session.TeleportPrivilege(request, fixture.Service), "旧owner不能执行礼遇传送");
        That(new CharacterRepository(fixture.Database).LoadCharacter(replacement, 0).TeleportCooldowns.PrivilegeUntil == fresh.TeleportCooldowns.PrivilegeUntil,
            "新owner和新仓储恢复已提交礼遇截止，不因换连接清零");
    }

    static (bool Success, ushort Error) ReadPrivilegeTeleportAck(byte[] body)
    {
        using var reader = new BinaryReader(new MemoryStream(body));
        bool success = reader.ReadByte() != 0;
        ushort error = success ? (ushort)0 : reader.ReadUInt16();
        if (reader.ReadBytes(5).Length != 5) throw new EndOfStreamException();
        That(reader.BaseStream.Position == body.Length, "669公共游标与私有读取后恰好到帧尾");
        return (success, error);
    }
}
