using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.Persistence;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

// 已实收CMD160与明确命名的合成兼容用例分别保留；后者不冒客户端请求证据。
internal static class MixedAvatarPackageTcpTests
{
    public static async Task RunAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        const int boxId = 10164558, avatarId = 101590348, equipmentId = 100331604;
        const short sourceSlot = 66, sentinelSlot = 67, equipmentSlot = 9;
        byte[] body = Convert.FromHexString("42000100000000004C250E0654F0FA050000");
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "mixed-observed-gift"), gm: true, resources: resources);
        var repo = new CharacterRepository(fixture.Database);
        var sourceCore = InventoryRewardGrantService.Create(resources.ItemResources.Items.Get(boxId));
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
            repo.Create(owner, new("MixedGift", resources.Towns.Room, resources.Towns.InitialReturn)
            {
                Job = 0, Level = 4, AcceptedStoryLevel = 1, Skills = resources.Characters.Get(0).Skills,
                Inventory = new([new(0, new(InventoryListType.Main, sourceSlot), sourceCore),
                    new(0, new(InventoryListType.Main, sentinelSlot), sourceCore)], Gold: 321)
            });
        await fixture.StartAsync();
        BoosterTcpTests.SetExpirationDebug(fixture, false);
        long sourceUid = Convert.ToInt64(fixture.Query("SELECT item_uid FROM character_inventory_slots WHERE list_type=0 AND slot_index=66").Single().Single());
        InventoryItem sentinel;
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
            sentinel = repo.List(owner).Single().Inventory.Items.Single(item => item.Position == new InventorySlot(InventoryListType.Main, sentinelSlot));

        // WriteInventory在审计循环前写完全部变更核心、详情与槽。
        // 触发器区分双奖已写后的指定晚故障与任一奖励尚未写入的故障。
        fixture.Sql("""
            CREATE TRIGGER fail_mixed_observed BEFORE INSERT ON inventory_audit_log BEGIN
              SELECT CASE WHEN
                (SELECT count(*) FROM character_inventory_items WHERE json_extract(item_core,'$.ItemId') IN (101590348,100331604))=2 AND
                (SELECT count(*) FROM inventory_avatar_details a JOIN character_inventory_items i USING(item_uid) WHERE json_extract(i.item_core,'$.ItemId')=101590348)=1 AND
                (SELECT count(*) FROM character_inventory_slots s JOIN character_inventory_items i USING(item_uid)
                 WHERE (s.list_type=0 AND s.slot_index=9 AND json_extract(i.item_core,'$.ItemId')=100331604)
                    OR (s.list_type=1 AND s.slot_index=0 AND json_extract(i.item_core,'$.ItemId')=101590348))=2 AND
                (SELECT count(*) FROM character_inventory_slots WHERE list_type=0 AND slot_index=66)=0
              THEN RAISE(ABORT,'mixed observed late rollback')
              ELSE RAISE(ABORT,'mixed observed late prerequisite missing') END;
            END
            """);
        string before;
        using (var peer = Open(fixture))
        {
            peer.Select(); peer.Barrier(); before = State();
            peer.Send(160, body); peer.Closed();
        }
        That(State() == before, "真实混合请求双奖核心、详情和双容器槽写入后的晚审计故障，源、金币、revision、UID序列及全部资产整笔回滚，无成功ACK");
        fixture.Sql("DROP TRIGGER fail_mixed_observed");

        byte[] committedEquipment, committedAvatar;
        long openEarliest, openLatest;
        using (var peer = Open(fixture))
        {
            peer.Select(); peer.Barrier();
            long revision = Convert.ToInt64(fixture.Query("SELECT revision FROM characters").Single().Single());
            openEarliest = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            peer.Send(160, body);
            BoosterTcpTests.ExpectAck(peer, boxId, sourceSlot, [(avatarId, 1), (equipmentId, 1)]);
            byte[] main = peer.Expect(0, 14).Body;
            BoosterTcpTests.Entry(main, sourceSlot, -1, 0);
            BoosterTcpTests.Entry(main, equipmentSlot, equipmentId, InventoryRewardGrantService.InitialQuality);
            That(U16(main, 1) == 2, "一次主背包差量包含源清空及真实完整称号装备，不把品级种子当数量1");
            committedEquipment = BoosterSelectionTcpTests.EquipmentEntry(main, equipmentSlot);
            byte[] avatarPage = peer.Expect(0, 14).Body;
            That(avatarPage[0] == 1 && U16(avatarPage, 1) == 1 && avatarPage.Length == 168, "真实混合单次成功后发送一件165B完整装扮差量");
            committedAvatar = AvatarRow(avatarPage);
            openLatest = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            CheckAvatar(committedAvatar, openEarliest, openLatest);
            peer.Barrier();
            That(Convert.ToInt64(fixture.Query("SELECT revision FROM characters").Single().Single()) == revision + 1,
                "完整双奖仅一次inventory提交，不分容器扣源或提交");
            before = State();
            peer.Send(160, body); peer.Expect(1, 160, [0, 19, 0]); peer.Barrier();
            That(State() == before, "原始18B耗尽请求重放拒绝，不消费同ID另一源，不重复奖励或变更UID/revision");
        }

        InventoryItem committedAura, committedTitle;
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            var role = repo.List(owner).Single();
            var inventory = role.Inventory;
            committedAura = inventory.Items.Single(item => item.Core.ItemId == avatarId);
            committedTitle = inventory.Items.Single(item => item.Core.ItemId == equipmentId);
            That(role.Level == 4 && inventory.Gold == 321 && inventory.Items.Length == 3 &&
                inventory.Items.Single(item => item.Position == sentinel.Position) == sentinel &&
                inventory.Items.All(item => item.Uid != sourceUid) && committedAura.Uid > 0 && committedTitle.Uid > 0 &&
                committedAura.Uid != committedTitle.Uid && committedAura.Uid != sentinel.Uid && committedTitle.Uid != sentinel.Uid &&
                committedAura.Position == new InventorySlot(InventoryListType.Avatar, 0) &&
                committedTitle.Position == new InventorySlot(InventoryListType.Main, equipmentSlot) &&
                committedTitle.Core.Kind == ItemKind.Equipment && committedTitle.Core.Count == 1 && committedTitle.Avatar is null &&
                committedAura.Core.Kind == ItemKind.Avatar && committedAura.Core.Value == committedAura.Uid &&
                committedAura.Core.Durability == 0 && committedAura.Core.ExpireTime == 0 &&
                committedAura.Avatar is { Socket0: { Type: 239, EmblemId: -1 }, Socket1: { Type: 239, EmblemId: -1 },
                    Socket2: { Type: 0, EmblemId: 0 }, Socket3: { Type: 0, EmblemId: 0 }, Socket4: { Type: 0, EmblemId: 0 },
                    ClearAvatarId: 0, ClearAvatarSourceUid: 0, Color1: 0, Color2: 0 } &&
                committedAura.ExpireTime >= openEarliest + 364 * 86400L && committedAura.ExpireTime <= openLatest + 364 * 86400L,
                "4级角色正常持有90级双奖：原源只消费一次，另一源/金币不变，两唯一UID、两正确容器及364天无能力默认双M详情完整保存");
        }
        using (var peer = Open(fixture))
            CheckReconnect(peer);
        await fixture.StopAsync();
        That(File.ReadLines(Path.Combine(fixture.Options.LogDirectory, "server.jsonl")).Count(line =>
        {
            using var entry = JsonDocument.Parse(line); var data = entry.RootElement;
            return data.GetProperty("Kind").GetString() == "closed" &&
                data.GetProperty("Detail").GetProperty("Reason").GetString() == "SqliteException" &&
                data.GetProperty("Detail").GetProperty("Message").GetString() == "SQLite Error 19: 'mixed observed late rollback'.";
        }) == 1, "唯一故障断线精确来自双奖已写入后的指定晚审计触发器，而非缺少前提或其他SQL故障");
        string persisted = State();
        await fixture.StartAsync("reopened");
        using (var peer = Open(fixture))
            CheckReconnect(peer);
        await fixture.StopAsync();
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            var inventory = repo.List(owner).Single().Inventory;
            That(inventory.Items.Single(item => item.Uid == committedAura.Uid) == committedAura &&
                inventory.Items.Single(item => item.Uid == committedTitle.Uid) == committedTitle &&
                inventory.Items.Single(item => item.Uid == sentinel.Uid) == sentinel,
                "断线重连和服务重建后恢复原双UID、完整核心、槽位、期限与装扮详情，不重新创建奖励");
        }
        That(State() == persisted && fixture.Query("SELECT count(*) FROM mailbox_messages") is [[0L]] &&
            fixture.Query("SELECT count(*) FROM mailbox_attachments") is [[0L]] &&
            fixture.Query("SELECT count(*) FROM inventory_avatar_details") is [[1L]] &&
            fixture.Query("SELECT count(*),count(DISTINCT item_uid) FROM character_inventory_items") is [[3L, 3L]] &&
            fixture.Query("PRAGMA integrity_check") is [["ok"]] && fixture.Query("PRAGMA foreign_key_check").Length == 0,
            "混合双奖服务重建不重复、不溢出邮寄，数据库与外键完整");
        await RunMaterialObservedAsync(fixture, repo, resources);
        await RunTypedSyntheticAsync(fixture, repo, resources);

        void CheckReconnect(TcpPeer peer)
        {
            byte[] main = peer.Select()[4].Body;
            Bytes(BoosterSelectionTcpTests.EquipmentEntry(main, equipmentSlot, full: true), committedEquipment,
                "重连及服务重建恢复完整123B称号核心");
            byte[] page = peer.SpecialContainers.Single(packet => packet.Opcode == 13 && packet.Body[0] == 1).Body;
            That(U16(page, 3) == 1 && page.Length == 170, "重连及服务重建仅恢复一件完整原生装扮");
            byte[] row = AvatarRow(page, full: true);
            // 协议剩余秒数随时间递减，持久化的原绝对期限应保持不变。
            Bytes(row.AsSpan(0, 6), committedAvatar.AsSpan(0, 6), "装扮重连保留槽和模板");
            Bytes(row.AsSpan(10), committedAvatar.AsSpan(10), "装扮重连保留余下155B核心、孔、颜色详情");
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            uint remaining = U32(row, 6);
            That(remaining > 0 && Math.Abs((long)remaining - (committedAura.ExpireTime - now)) <= 2,
                "装扮重连倒计时仍对应原提交364天绝对期限，容许读取跨秒");
            peer.Barrier();
        }

        static byte[] AvatarRow(byte[] page, bool full = false)
        {
            int header = full ? 5 : 3;
            int at = Enumerable.Range(0, U16(page, full ? 3 : 1)).Select(index => header + index * 165)
                .Single(at => U32(page, at + 2) == avatarId);
            return page.AsSpan(at, 165).ToArray();
        }
        static void CheckAvatar(byte[] row, long earliest, long latest)
        {
            const long duration = 364L * 86400;
            That(U16(row, 0) == 0 && U32(row, 2) == avatarId && U16(row, 11) == 0 && row[10] == 0 &&
                U32(row, 6) >= earliest + duration - latest && U32(row, 6) <= duration &&
                U32(row, 123) == 30 && U32(row, 157) == 4 && U16(row, 161) == 0 && U16(row, 163) == 0,
                "真实光环165B投影保364天倒计时、无能力0及完整RAW颜色块");
            for (int i = 0; i < 5; i++)
                That(U16(row, 127 + i * 6) == (i < 2 ? 0xFFEF : 0) &&
                    U32(row, 129 + i * 6) == (i < 2 ? uint.MaxValue : 0),
                    "模板默认双M原生FFEF及空徽章FFFFFFFF，其余三孔关闭");
        }
        string State() => JsonSerializer.Serialize(new[]
        {
            fixture.Query("SELECT revision,gold FROM characters"),
            fixture.Query("SELECT item_uid,item_core FROM character_inventory_items ORDER BY item_uid"),
            fixture.Query("SELECT list_type,slot_index,item_uid FROM character_inventory_slots ORDER BY list_type,slot_index"),
            fixture.Query("SELECT item_uid,detail FROM inventory_avatar_details ORDER BY item_uid"),
            fixture.Query("SELECT count(*) FROM inventory_audit_log"), fixture.Query("SELECT count(*) FROM mailbox_messages"),
            fixture.Query("SELECT count(*) FROM mailbox_attachments"),
            fixture.Query("SELECT seq FROM sqlite_sequence WHERE name='character_inventory_items'")
        });
    }

    // #976沿本组原fixture追加，不创建第二组/数据库/大启动器；原双奖全部断言先完成。
    static async Task RunMaterialObservedAsync(TcpTestFixture fixture, CharacterRepository repo, TcpTestResources resources)
    {
        const int boxId = 10150804, avatarId = 101521039, titleId = 100331476, materialId = 3330;
        const short sourceSlot = 66, titleSlot = 10, materialSlot = 121, avatarSlot = 1;
        byte[] body = Convert.FromHexString("42000100000000008F160D06D4EFFA05020D0000018F160D060200");
        InventoryItem[] retained;
        InventorySnapshot seeded;
        long sourceUid, materialUid;
        // 原fixture此时已停止；通过既有Repository正常事务种隔离测试输入，不写运行玩家库。
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            var role = repo.List(owner).Single();
            retained = role.Inventory.Items.ToArray();
            That(role.Inventory.Find(new(InventoryListType.Main, sourceSlot)) is null &&
                role.Inventory.Find(new(InventoryListType.Main, materialSlot)) is null &&
                role.Inventory.Find(new(InventoryListType.Main, titleSlot)) is null &&
                role.Inventory.Find(new(InventoryListType.Avatar, avatarSlot)) is null,
                "#976复用原夹具时精确源和三奖目标槽空闲，不覆盖原双奖或哨兵");
            var source = new InventoryItem(0, new(InventoryListType.Main, sourceSlot),
                InventoryRewardGrantService.Create(resources.ItemResources.Items.Get(boxId)));
            var material = new InventoryItem(0, new(InventoryListType.Main, materialSlot),
                InventoryRewardGrantService.Create(resources.ItemResources.Items.Get(materialId), 17));
            var updated = role.Inventory with { Items = [.. role.Inventory.Items, source, material] };
            var seededRole = repo.CommitInventory(owner, role, new(role.Inventory, updated, "material_avatar_test_seed"));
            seeded = seededRole.Inventory;
            sourceUid = seeded.Find(source.Position)!.Uid;
            materialUid = seeded.Find(material.Position)!.Uid;
            That(sourceUid > 0 && materialUid > 0 && sourceUid != materialUid &&
                retained.All(item => seeded.Items.Single(saved => saved.Uid == item.Uid) == item),
                "#976新增一个真实源及材料17份有独立UID，原三资产逐项保留");
        }
        await fixture.StartAsync("material-observed");
        BoosterTcpTests.SetExpirationDebug(fixture, false);
        // 确认三Kind全部写完：装扮核心/165B详情/槽、称号核心/槽、同UID材料17→117及源槽已删。
        fixture.Sql($"""
            CREATE TRIGGER fail_material_observed BEFORE INSERT ON inventory_audit_log BEGIN
              SELECT CASE WHEN
                (SELECT count(*) FROM character_inventory_items WHERE json_extract(item_core,'$.ItemId')=101521039)=1 AND
                (SELECT count(*) FROM character_inventory_items WHERE json_extract(item_core,'$.ItemId')=100331476)=1 AND
                (SELECT count(*) FROM inventory_avatar_details a JOIN character_inventory_items i USING(item_uid)
                 WHERE json_extract(i.item_core,'$.ItemId')=101521039)=1 AND
                (SELECT count(*) FROM character_inventory_slots s JOIN character_inventory_items i USING(item_uid)
                 WHERE (s.list_type=1 AND s.slot_index=1 AND json_extract(i.item_core,'$.ItemId')=101521039)
                    OR (s.list_type=0 AND s.slot_index=10 AND json_extract(i.item_core,'$.ItemId')=100331476)
                    OR (s.list_type=0 AND s.slot_index=121 AND i.item_uid={materialUid} AND json_extract(i.item_core,'$.ItemId')=3330 AND json_extract(i.item_core,'$.Value')=117))=3 AND
                (SELECT count(*) FROM character_inventory_slots WHERE list_type=0 AND slot_index=66)=0 AND
                (SELECT count(*) FROM character_inventory_items WHERE item_uid={sourceUid})=0
              THEN RAISE(ABORT,'material observed late rollback')
              ELSE RAISE(ABORT,'material observed late prerequisite missing') END;
            END
            """);
        string before;
        using (var peer = Open(fixture))
        {
            peer.Select(); peer.Barrier(); before = State();
            peer.Send(160, body); peer.Closed();
        }
        That(State() == before,
            "#976真实27B三Kind写入后的晚审计故障无成功ACK，源UID/count、原材料堆、全部核心详情槽、钱包、revision和审计整笔回滚");
        fixture.Sql("DROP TRIGGER fail_material_observed");

        byte[] committedTitle, committedMaterial, committedAvatar;
        using (var peer = Open(fixture))
        {
            peer.Select(); peer.Barrier();
            long revision = Convert.ToInt64(fixture.Query("SELECT revision FROM characters").Single().Single());
            peer.Send(160, body);
            BoosterTcpTests.ExpectAck(peer, boxId, sourceSlot, [(avatarId, 1), (titleId, 1), (materialId, 100)]);
            byte[] main = peer.Expect(0, 14).Body;
            That(main[0] == 0 && U16(main, 1) == 3, "#976一次Main差量完整含源清空、称号及材料同堆17→117");
            BoosterTcpTests.Entry(main, sourceSlot, -1, 0);
            BoosterTcpTests.Entry(main, titleSlot, titleId, InventoryRewardGrantService.InitialQuality);
            BoosterTcpTests.Entry(main, materialSlot, materialId, 117);
            committedTitle = BoosterSelectionTcpTests.EquipmentEntry(main, titleSlot);
            committedMaterial = BoosterSelectionTcpTests.EquipmentEntry(main, materialSlot);
            byte[] avatar = peer.Expect(0, 14).Body;
            That(avatar[0] == 1 && U16(avatar, 1) == 1 && avatar.Length == 168,
                "#976第三Kind装扮同一成功事务发送一件165B完整差量");
            committedAvatar = AvatarRow(avatar);
            CheckAvatar(committedAvatar);
            peer.Barrier();
            That(Convert.ToInt64(fixture.Query("SELECT revision FROM characters").Single().Single()) == revision + 1,
                "#976三奖只一次revision提交，无先发两奖后补材料事务");
            before = State();
            peer.Send(160, body); peer.Expect(1, 160, [0, 19, 0]); peer.Barrier();
            That(State() == before, "#976实收27B耗尽重放不重发任一奖，不改材料/审计/UID/revision及原哨兵");
        }
        InventoryItem committedAura, committedEquipment, committedStack;
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            var inventory = repo.List(owner).Single().Inventory;
            committedAura = inventory.Items.Single(item => item.Core.ItemId == avatarId);
            committedEquipment = inventory.Items.Single(item => item.Core.ItemId == titleId);
            committedStack = inventory.Items.Single(item => item.Core.ItemId == materialId);
            That(inventory.Items.Length == retained.Length + 3 && inventory.Items.All(item => item.Uid != sourceUid) &&
                inventory.Items.All(item => item.Position != new InventorySlot(InventoryListType.Main, sourceSlot)) &&
                retained.All(item => inventory.Items.Single(saved => saved.Uid == item.Uid) == item) &&
                committedAura.Uid > 0 && committedEquipment.Uid > 0 && committedAura.Uid != committedEquipment.Uid &&
                committedStack.Uid == materialUid && committedStack.Core.Count == 117 && committedStack.Position == new InventorySlot(InventoryListType.Main, materialSlot) &&
                committedEquipment.Position == new InventorySlot(InventoryListType.Main, titleSlot) && committedEquipment.Core.Kind == ItemKind.Equipment &&
                resources.ItemResources.Items.Get(committedEquipment.Core.ItemId).DisjointForbidden &&
                committedAura.Position == new InventorySlot(InventoryListType.Avatar, avatarSlot) && committedAura.Core.Durability == 2 &&
                committedAura.Core.Value == committedAura.Uid && committedAura.ExpireTime == 0 && committedAura.Core.ExpireTime == 0 &&
                committedAura.Avatar is { Socket0: { Type: 2, EmblemId: -1 }, Socket1: { Type: 2, EmblemId: -1 },
                    Socket2.Type: 0, Socket3.Type: 0, Socket4.Type: 0 } &&
                inventory.Gold == seeded.Gold && inventory.ReviveCoins == seeded.ReviveCoins && inventory.WinPoints == seeded.WinPoints &&
                JsonSerializer.Serialize(inventory.AccountWallet) == JsonSerializer.Serialize(seeded.AccountWallet) &&
                JsonSerializer.Serialize(inventory.EpicPieces) == JsonSerializer.Serialize(seeded.EpicPieces),
                "#976原生成功后保全部原资产与钱包，源只消耗一次，永久双B能力2、禁分解称号及同UID材料117完整持久化");
        }
        using (var peer = Open(fixture)) CheckReconnect(peer);
        await fixture.StopAsync();
        That(File.ReadLines(Path.Combine(fixture.Options.LogDirectory, "server.jsonl")).Count(line =>
        {
            using var entry = JsonDocument.Parse(line); var data = entry.RootElement;
            return data.GetProperty("Kind").GetString() == "closed" &&
                data.GetProperty("Detail").GetProperty("Reason").GetString() == "SqliteException" &&
                data.GetProperty("Detail").GetProperty("Message").GetString() == "SQLite Error 19: 'material observed late rollback'.";
        }) == 1, "#976唯一注入断线精确来自三Kind已写后的指定晚审计触发器，非前提缺失或其他SQL失败");
        string persisted = State();
        await fixture.StartAsync("material-reopened");
        using (var peer = Open(fixture)) CheckReconnect(peer);
        await fixture.StopAsync();
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            var inventory = repo.List(owner).Single().Inventory;
            That(inventory.Items.Single(item => item.Uid == committedAura.Uid) == committedAura &&
                inventory.Items.Single(item => item.Uid == committedEquipment.Uid) == committedEquipment &&
                inventory.Items.Single(item => item.Uid == materialUid) == committedStack &&
                retained.All(item => inventory.Items.Single(saved => saved.Uid == item.Uid) == item),
                "#976断线重连及同进程服务重建恢复原三Kind UID/core/detail/槽/材料117和旧资产，未冒跨进程恢复");
        }
        That(State() == persisted && fixture.Query("SELECT count(*) FROM mailbox_messages") is [[0L]] &&
            fixture.Query("SELECT count(*) FROM mailbox_attachments") is [[0L]] &&
            fixture.Query("PRAGMA integrity_check") is [["ok"]] && fixture.Query("PRAGMA foreign_key_check").Length == 0,
            "#976服务重建不重复三奖或溢出邮寄，数据库及外键完整");

        void CheckReconnect(TcpPeer peer)
        {
            byte[] main = peer.Select()[4].Body;
            Bytes(BoosterSelectionTcpTests.EquipmentEntry(main, titleSlot, full: true), committedTitle,
                "#976重连/服务重建称号完整123B逐字节恢复");
            Bytes(BoosterSelectionTcpTests.EquipmentEntry(main, materialSlot, full: true), committedMaterial,
                "#976重连/服务重建材料117同槽完整123B恢复");
            byte[] page = peer.SpecialContainers.Single(packet => packet.Opcode == 13 && packet.Body[0] == 1).Body;
            That(U16(page, 3) == 2 && page.Length == 335, "#976重连仅恢复原装扮与新增装扮各一件165B");
            Bytes(AvatarRow(page, full: true), committedAvatar, "#976永久装扮165B含能力2双B详情完整恢复，无动态期限差异");
            peer.Barrier();
        }
        static byte[] AvatarRow(byte[] page, bool full = false)
        {
            int header = full ? 5 : 3;
            int at = Enumerable.Range(0, U16(page, full ? 3 : 1)).Select(index => header + index * 165)
                .Single(at => U32(page, at + 2) == avatarId);
            return page.AsSpan(at, 165).ToArray();
        }
        static void CheckAvatar(byte[] row)
        {
            That(U16(row, 0) == avatarSlot && U32(row, 2) == avatarId && U32(row, 6) == 0 && U16(row, 11) == 2,
                "#976原生165B装扮差量保正确槽/模板、永久期限及真实能力2");
            for (int index = 0; index < 5; index++)
                That(U16(row, 127 + index * 6) == (index < 2 ? 2 : 0) &&
                    U32(row, 129 + index * 6) == (index < 2 ? uint.MaxValue : 0),
                    "#976双B原生0002与空徽章FFFFFFFF，其余孔关闭（只有M孔特殊映射FFEF）");
        }
        string State() => JsonSerializer.Serialize(new[]
        {
            fixture.Query("SELECT revision,gold,revive_coins,win_points,gold_carry_limit,auction_gold_limit FROM characters"),
            fixture.Query("SELECT * FROM account_wallets ORDER BY account"),
            fixture.Query("SELECT * FROM account_currency_balances ORDER BY account,currency_kind"),
            fixture.Query("SELECT item_uid,item_core FROM character_inventory_items ORDER BY item_uid"),
            fixture.Query("SELECT list_type,slot_index,item_uid FROM character_inventory_slots ORDER BY list_type,slot_index"),
            fixture.Query("SELECT item_uid,detail FROM inventory_avatar_details ORDER BY item_uid"),
            fixture.Query("SELECT * FROM inventory_audit_log ORDER BY rowid"),
            fixture.Query("SELECT count(*) FROM mailbox_messages"), fixture.Query("SELECT count(*) FROM mailbox_attachments"),
            fixture.Query("SELECT name,seq FROM sqlite_sequence WHERE name IN ('character_inventory_items','inventory_audit_log') ORDER BY name")
        });
    }

    // #981仅合成TCP兼容回归；五主ID/options合同未实收，不把这两个30B当客户端请求证据。
    // 复用同一已停fixture和资源，只补Handler/ACK/105+三容器投影；SQL事务/恢复沿既有专项复用。
    static async Task RunTypedSyntheticAsync(TcpTestFixture fixture, CharacterRepository repo, TcpTestResources resources)
    {
        const int boxId = 10310044, avatarId = 101590523;
        short[] sourceSlots = [66, 68];
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            var role = repo.List(owner).Single();
            That(sourceSlots.All(slot => role.Inventory.Find(new(InventoryListType.Main, slot)) is null),
                "#981合成TCP复用已停夹具，两个源槽原为空，不覆盖旧实收资产");
            var core = InventoryRewardGrantService.Create(resources.ItemResources.Items.Get(boxId));
            var sources = sourceSlots.Select(slot => new InventoryItem(0, new(InventoryListType.Main, slot), core));
            repo.CommitInventory(owner, role, new(role.Inventory,
                role.Inventory with { Items = [.. role.Inventory.Items, .. sources] }, "typed_synthetic_tcp_seed"));
        }
        await fixture.StartAsync("typed-synthetic");
        BoosterTcpTests.SetExpirationDebug(fixture, false);
        using (var peer = Open(fixture))
        {
            peer.Select(); peer.Barrier();
            for (byte category = 0; category < 2; category++)
            {
                int[] ids = category == 0 ? [avatarId, 100331839, 100990228, 10309981, 10310266] :
                    [avatarId, 100331840, 100990229, 10309982, 10310048];
                short sourceSlot = sourceSlots[category];
                var previous = Saved();
                var source = previous.Inventory.Find(new(InventoryListType.Main, sourceSlot))!;
                That(source is { Uid: > 0 } && source.Core.ItemId == boxId,
                    "#981合成TCP每分类绑定独立持久源UID，不将前一新奖励当下一源");
                if (category == 0)
                {
                    string before = JsonSerializer.Serialize(previous);
                    byte[] missingPet = SyntheticTypedBody(sourceSlot, category, ids.Where(id => id != ids[2]).ToArray());
                    That(missingPet.Length == 26, "#981缺Pet的四主ID26B是明确合成负例");
                    peer.Send(160, missingPet); peer.Expect(1, 160, [0, 19, 0]); peer.Barrier();
                    That(JsonSerializer.Serialize(Saved()) == before,
                        "#981 Handler完整RowCount5拒合成四ID，无资产或revision变化且连接仍可用");
                }
                byte[] synthetic = SyntheticTypedBody(sourceSlot, category, ids);
                That(synthetic.Length == 30, "#981五主ID/零options/尾0的30B仅测试服务器既有语法，不冒实收");
                peer.Send(160, synthetic);
                BoosterTcpTests.ExpectAck(peer, boxId, sourceSlot, ids.Select((id, index) =>
                    (id, category == 1 && index == 4 ? 3 : 1)).ToArray());
                var committed = Saved();
                var oldUids = previous.Inventory.Items.Select(item => item.Uid).ToHashSet();
                var rewards = committed.Inventory.Items.Where(item => !oldUids.Contains(item.Uid)).ToArray();
                That(committed.Revision == previous.Revision + 1 && rewards.Length == 5 &&
                    rewards.All(item => item.Uid > 0) && rewards.Select(item => item.Uid).Distinct().Count() == 5 &&
                    rewards.Select(item => item.Core.ItemId).Order().SequenceEqual(ids.Order()) &&
                    committed.Inventory.Items.Length == previous.Inventory.Items.Length + 4 &&
                    committed.Inventory.Items.All(item => item.Uid != source.Uid) &&
                    previous.Inventory.Items.Where(item => item.Uid != source.Uid).All(item =>
                        committed.Inventory.Items.Single(saved => saved.Uid == item.Uid) == item),
                    "#981合成TCP五奖同一revision，按新UID差集区分两次同模板光环，原资产及另一源完整保留");

                // 新Pet详情必须在任何引用其UID的14之前抵达。独立解码，不用生产builder回造期待值。
                CheckCreatureDetails(peer.Expect(0, 105).Body, committed.Inventory, category + 1);
                var mainRewards = rewards.Where(item => item.Position.List == InventoryListType.Main).ToArray();
                var mainSlots = mainRewards.Select(item => item.Position.Index).Append(sourceSlot).Distinct().Order().ToArray();
                var replaced = mainSlots.Where(slot => previous.Inventory.Find(new(InventoryListType.Main, slot)) is { } old &&
                    committed.Inventory.Find(new(InventoryListType.Main, slot)) is { } current &&
                    old.Core.ItemId != current.Core.ItemId).ToArray();
                if (replaced.Length != 0)
                    CheckMain(peer.Expect(0, 14).Body, replaced, InventorySnapshot.Empty,
                        "#981合成TCP源槽合法被消耗品复用，先逐槽清旧模板");
                byte[] main = peer.Expect(0, 14).Body;
                CheckMain(main, mainSlots, committed.Inventory, "#981合成TCP完整Main差量与已提交源和三Main奖励一致");
                var title = rewards.Single(item => item.Core.ItemId == ids[1]);
                BoosterTcpTests.Entry(main, title.Position.Index, ids[1], InventoryRewardGrantService.InitialQuality);
                foreach (var item in mainRewards.Where(item => item.Core.ItemId != ids[1]))
                    That(item.Core.Kind == ItemKind.Consumable && item.Core.Count ==
                        (category == 1 && item.Core.ItemId == ids[4] ? 3 : 1),
                        "#981合成TCP两个普通消耗品保原数量，不把三份拆作三个主ID");

                var aura = rewards.Single(item => item.Core.ItemId == avatarId);
                byte[] avatar = peer.Expect(0, 14).Body;
                That(avatar[0] == 1 && U16(avatar, 1) == 1 && avatar.Length == 168,
                    "#981合成TCP每次只有新光环的一条完整165B差量");
                var row = avatar.AsSpan(3, 165).ToArray();
                That(U16(row, 0) == aura.Position.Index && U32(row, 2) == avatarId && U32(row, 6) == 0 &&
                    U16(row, 11) == 0 && U32(row, 123) == 30 && U32(row, 157) == 4 &&
                    U16(row, 161) == 0 && U16(row, 163) == 0,
                    "#981合成TCP光环永久/无能力/RAW详情完整，第二次相同模板仍定位新UID对应槽");
                for (int index = 0; index < 5; index++)
                    That(U16(row, 127 + index * 6) == (index < 2 ? 0xFFEF : 0) &&
                        U32(row, 129 + index * 6) == (index < 2 ? uint.MaxValue : 0),
                        "#981合成TCP双M映射FFEF/空徽章，其余孔关闭");

                var pet = rewards.Single(item => item.Core.ItemId == ids[2]);
                byte[] petPage = peer.Expect(0, 14).Body;
                That(petPage[0] == 7 && U16(petPage, 1) == 1 && petPage.Length == 126,
                    "#981合成TCP新Pet只一条完整123B差量，不混入Avatar RAW");
                byte[] petRow = BoosterSelectionTcpTests.EquipmentEntry(petPage, pet.Position.Index);
                That(U32(petRow, 2) == ids[2] && U32(petRow, 6) == pet.Uid && U32(petRow, 22) == 0 &&
                    petRow[13] == 0 && pet.Core.Value == pet.Uid && pet.Creature is { Mode: 0, ExpireTime: 0 },
                    "#981合成TCP Pet记录引用刚到达105的真实UID/永久非蛋核心，不以数量1替UID");
                peer.Barrier();
            }
        }
        await fixture.StopAsync();
        Console.WriteLine("#981 synthetic typed TCP: two categories; generated30B only, client producer/request not observed.");

        DfoServer.Game.Characters.Character Saved()
        {
            using var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { });
            return repo.List(owner).Single();
        }
        static byte[] SyntheticTypedBody(short slot, byte category, int[] ids) => Build(writer =>
        {
            writer.Write(slot); writer.Write(1); writer.Write(category); writer.Write((byte)0);
            foreach (int id in ids) writer.Write(id);
            writer.Write((byte)0); writer.Write((byte)0);
        });
        static void CheckMain(byte[] body, short[] slots, InventorySnapshot inventory, string message)
        {
            That(body[0] == 0 && U16(body, 1) == slots.Length && body.Length == 3 + slots.Length * 123, message);
            foreach (short slot in slots)
            {
                var item = inventory.Find(new(InventoryListType.Main, slot));
                BoosterTcpTests.Entry(body, slot, item?.Core.ItemId ?? -1, item?.Core.Value ?? 0);
            }
        }
        static void CheckCreatureDetails(byte[] body, InventorySnapshot inventory, int count)
        {
            var pets = inventory.Items.Where(item => item.Creature is not null).OrderBy(item => item.Uid).ToArray();
            using var stream = new MemoryStream(body);
            using var reader = new BinaryReader(stream);
            That(reader.ReadByte() == count && pets.Length == count,
                "#981合成TCP 105完整列出一/两Pet详情，不因第二类覆盖第一类");
            foreach (var pet in pets)
            {
                int uid = reader.ReadInt32(); byte satiety = reader.ReadByte(), mode = reader.ReadByte();
                int experience = reader.ReadInt32(); byte level = reader.ReadByte();
                string expectedName = pet.Creature?.Name ??
                    throw new InvalidDataException("typed Pet detail has no species name");
                byte[] expectedNameBytes = DfoServer.Game.Characters.CharacterName.Encoding.GetBytes(expectedName);
                int length = reader.ReadInt32();
                That(uid == pet.Uid && satiety == 100 && mode == 0 && experience == 0 && level == 1 &&
                    length == expectedNameBytes.Length && length <= stream.Length - stream.Position - 1,
                    "#981合成TCP独立解码105 UID/初始详情/各species原名字节宽度");
                Bytes(reader.ReadBytes(length), expectedNameBytes,
                    "#981合成TCP各species使用自身原名，不复制其它Pet或装备名");
                That(reader.ReadByte() == 0, "#981合成TCP 105原尾标志保持0");
            }
            That(stream.Position == stream.Length, "#981合成TCP 105两行完整消费，无遗漏或多余尾段");
        }
    }
}
