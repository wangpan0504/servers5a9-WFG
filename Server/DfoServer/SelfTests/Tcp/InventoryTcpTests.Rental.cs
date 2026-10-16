using DfoServer.Game.Accounts;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Session;
using DfoServer.Game.Characters;
using DfoServer.Game.Quests;
using DfoServer.GameWorld.Characters;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static partial class InventoryTcpTests
{
    static async Task VerifyRentalAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        await using var fixture = new TcpTestFixture(source, root, resources: resources);
        var repository = new CharacterRepository(fixture.Database);
        ushort questSlot;
        ushort alternateId;
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            var character = new GameSession(repository, resources.Towns, owner).CreateCharacter("RentLv1", resources.Characters.Get(14));
            repository.CommitInventory(owner, character, new(character.Inventory, character.Inventory with
            {
                AccountWallet = character.Inventory.AccountWallet.Set(CurrencyKind.RentalPoint, 3)
            }, "fixture_rental_points"));
            // 共享本服务与资源，保留原1级领取回归，再沿真实Q8581接取/租赁/领奖。
            var quests = resources.Quests;
            That(QuestRentalProgress.Supports(quests.Get(QuestRentalProgress.QuestId)), "真实PVF租赁任务合同与已绑定event17匹配");
            var growth = SelfTestCatalogs.Growth(resources.Archive, resources.Learning);
            var reset = new CharacterProgressReset(resources.Characters,resources.Learning,growth);
            var role = Prepare("RentQuest");
            questSlot = role.Slot;
            var alternate = Prepare("RentOther");
            alternateId = alternate.Id;
            var otherSession = new GameSession(repository,resources.Towns,owner,questCatalog:quests);
            otherSession.Select(alternate.Slot);otherSession.MarkReady();
            That(otherSession.ChangeQuest(QuestRentalProgress.QuestId,QuestLifecycleAction.Accept,quests,
                resources.ItemResources.Items,resources.ItemResources.GoldLimits,resources.ItemResources.EpicPieces)?.Commit is not null,
                "同账号另一角色真实接取，账号交付必须原子关闭其原槽");
            otherSession.End();

            Character Prepare(string name)
            {
                var session = new GameSession(repository,resources.Towns,owner);
                var created = session.CreateCharacter(name,resources.Characters.Get(14),resources.Learning);
                var leveled = repository.ResetProgress(owner,reset.Prepare(created,
                    new(CharacterProgressField.Level,owner.Account,name,10,0)));
                uint chargeGold = checked((uint)((long)resources.ItemResources.Rental.GoldPerPoint * 3));
                return repository.CommitInventory(owner,leveled,new(leveled.Inventory,leveled.Inventory with { Gold=chargeGold },"fixture_rental_gold"));
            }
        }
        await fixture.StartAsync(packetCapture: true);
        long rentedUid;
        long expiresAt;
        uint initialRemaining;
        using (var peer = Open(fixture))
        {
            var entry = peer.Select();
            Bytes(entry[5].Body, [3, 0, 0, 0, 0, 0, 0, 0], "租赁初始化只发送余额与已租数量");
            byte[] request = Convert.FromHexString("F5733BD78025074D18EB476300470B52180E000000");
            byte[] mismatched = (byte[])request.Clone();
            mismatched[13] = 0x4E; // 408030030存在于服务端PVF第7档，不能按客户端上报的第0档领取。
            string before = Snapshot(fixture);
            peer.Send(1001, mismatched);
            peer.Expect(1, 1001, [0, 1, 0, 0, 0]);
            peer.Barrier();
            That(Snapshot(fixture) == before && fixture.Query("SELECT rental_points FROM account_wallets") is [[3L]],
                "客户端商品与服务端PVF档位不一致时拒绝，余额和库存保持");
            long sentAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            peer.Send(1001, request);
            peer.Expect(1, 1001, [1, 0, 0, 0, 0]);
            var state = peer.Expect(0, 988).Body;
            That(state.Length == 16 && U32(state, 0) == 0 && U32(state, 4) == 1 && U32(state, 8) == 408030023 &&
                U32(state, 12) is >= 86390 and <= 86400,
                "1级真实请求租到10级模板，扣3星并发布24小时剩余秒状态");
            initialRemaining = U32(state, 12);
            var delta = peer.Expect(0, 14).Body;
            That(delta.Length == 126 && U16(delta, 3) == 9 && U32(delta, 5) == 408030023 &&
                U32(delta, 3 + 0x38) is >= 86390 and <= 86400, "低等级租赁发布原Main单槽完整差量与剩余租期");
            peer.Barrier();
            That(fixture.Query("SELECT level FROM characters WHERE id=$p0", peer.Id) is [[1L]] &&
                fixture.Query("SELECT rental_points FROM account_wallets") is [[0L]], "租赁不升级角色，余额已提交");
            rentedUid = (long)fixture.Query("SELECT item_uid FROM character_inventory_slots WHERE character_id=$p0 AND list_type=0 AND slot_index=9", peer.Id)[0][0]!;
            expiresAt = (long)fixture.Query("SELECT json_extract(item_core,'$.ExpireTime') FROM character_inventory_items WHERE item_uid=$p0", rentedUid)[0][0]!;
            long receivedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            That(expiresAt >= sentAt + 86400 && expiresAt <= receivedAt + 86400 &&
                initialRemaining >= expiresAt - receivedAt && initialRemaining <= expiresAt - sentAt &&
                U32(delta, 3 + 0x38) >= expiresAt - receivedAt && U32(delta, 3 + 0x38) <= expiresAt - sentAt,
                "真实988与Main差量期限均为同一持久截止减发送时间，数据库仍保存绝对截止");
            string committed = Snapshot(fixture);
            peer.Send(1001, request);
            peer.Expect(1, 1001, [0, 1, 0, 0, 0]);
            peer.Barrier();
            That(Snapshot(fixture) == committed && fixture.Query("SELECT rental_points FROM account_wallets") is [[0L]],
                "余额不足拒绝不二扣、不新增租赁实例且连接保持");
        }
        // 跨过至少一整秒后重连，验证真实发送路径会倒计时，而非只改初始化断言。
        await Task.Delay(1100);
        using (var peer = Open(fixture))
        {
            long selectAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var entry = peer.Select();
            long receivedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            uint remaining = U32(entry[5].Body, 12);
            var main = entry[4].Body;
            int rentalOffset = Enumerable.Range(0, U16(main, 3)).Select(i => 5 + i * 123)
                .Single(offset => U16(main, offset) == 9);
            That(U32(entry[5].Body, 0) == 0 && U32(entry[5].Body, 4) == 1 && U32(entry[5].Body, 8) == 408030023 &&
                remaining < initialRemaining && remaining >= expiresAt - receivedAt && remaining <= expiresAt - selectAt &&
                U32(main, rentalOffset + 2) == 408030023 &&
                U32(main, rentalOffset + 0x38) >= expiresAt - receivedAt && U32(main, rentalOffset + 0x38) <= expiresAt - selectAt &&
                fixture.Query("SELECT json_extract(item_core,'$.ExpireTime') FROM character_inventory_items WHERE item_uid=$p0", rentedUid) is [[var savedExpiry]] &&
                Equals(savedExpiry, expiresAt) &&
                fixture.Query("SELECT item_uid FROM character_inventory_slots WHERE character_id=$p0 AND list_type=0 AND slot_index=9", peer.Id) is [[var uid]] &&
                Equals(uid, rentedUid), "重连988与Main全表同步倒计时，保留同一租赁实例、余额和持久绝对截止");
            Console.WriteLine($"PASS rental countdown: uid={rentedUid} expireUnix={expiresAt} initial988={initialRemaining} reconnect988={remaining} reconnectMain={U32(main, rentalOffset + 0x38)}");
        }
        using (var peer = Open(fixture))
        {
            peer.Select(questSlot);
            peer.Send(31,Convert.FromHexString("1F008521"));
            peer.Expect(1,31,Convert.FromHexString("0185210100000000"));
            peer.Expect(0,21);peer.Barrier();
            var acceptance = fixture.Query("SELECT acceptance_id FROM character_active_main_quests WHERE character_id=$p0 AND quest_id=8581",peer.Id)[0][0];
            string before = Snapshot(fixture);
            peer.Send(33,Convert.FromHexString("210085210000"));peer.Expect(1,33,[0,19,0]);peer.Barrier();
            That(Snapshot(fixture)==before,"真实未租赁33中立拒绝，不能从其他角色租赁实例推断当前事件");
            byte[] charge = new byte[21];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(charge.AsSpan(13),1);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(charge.AsSpan(17),3);
            peer.Send(1002,charge);peer.Expect(1,1002,Convert.FromHexString("01010000000000000003000000"));
            peer.Expect(0,988);peer.Expect(0,14);peer.Barrier();
            That(fixture.Query("SELECT condition_state FROM character_active_main_quests WHERE character_id=$p0 AND quest_id=8581",peer.Id) is [[1L]],
                "充值1002/余额988不会冒充租赁成功推进任务");
            byte[] request = Convert.FromHexString("F5733BD78025074D18EB476300470B52180E000000");
            peer.Send(1001,request);
            peer.Expect(1,1001,[1,0,0,0,0]);peer.Expect(0,988);peer.Expect(0,14);peer.Expect(0,574);peer.Barrier();
            That(fixture.Query("SELECT condition_state,acceptance_id FROM character_active_main_quests WHERE character_id=$p0 AND quest_id=8581",peer.Id)
                is [[0L,var savedAcceptance]] && Equals(savedAcceptance,acceptance) &&
                fixture.Query("SELECT quest_id,action FROM character_quest_lifecycle_receipts WHERE character_id=$p0 ORDER BY rowid DESC LIMIT 1",peer.Id)
                is [[8581L,"Condition"]],"1001原包序后归零当前原Acceptance，并保存生命周期receipt");
            string committed = Snapshot(fixture);
            for(int i=0;i<2;i++)
            {
                peer.Send(33,Convert.FromHexString("210085210000"));
                peer.Expect(1,33,Convert.FromHexString("01852100000000"));peer.Barrier();
            }
            That(Snapshot(fixture)==committed,"客户端event17补发/重复33只读确认，没有新revision/receipt");
            peer.Send(34,Convert.FromHexString("22008521FFFF0100FFFF"));
            var finished = peer.Expect(1,34).Body;
            That(finished[0]==1 && U16(finished,1)==8581 && finished[3]==19,"普通ACK34保留原生custom type19");
            var notices = new List<ushort>();
            for (int i=0;i<16;i++)
            {
                var notice = peer.Read();
                That(notice.Kind==0 && notice.Opcode is 2 or 14 or 19 or 21 or 37 or 574 or 356,
                    "租赁领奖只使用既有资产/成长/任务通知");
                notices.Add(notice.Opcode);
                if (notice.Opcode==356) break;
            }
            That(notices.Contains(21) && notices.Last()==356,"租赁领奖最后356回放真实完成位，不被空574清除");
            peer.Barrier();
            That(fixture.Query("SELECT condition_type,event_id IS NULL,clear_acceptance_id IS NULL FROM character_quest_completion_receipts WHERE quest_id=8581")
                is [[19L,1L,1L]] && fixture.Query("SELECT quest_id,character_id FROM account_completed_quests WHERE quest_id=8581")
                is [[8581L,var sourceId]] && Equals(sourceId,(long)peer.Id) &&
                fixture.Query("SELECT count(*) FROM character_active_main_quests WHERE character_id=$p0 AND quest_id=8581",alternateId) is [[0L]],
                "租赁34同事务保留账号完成来源并关闭其他角色原槽，无假清图");
            string after = Snapshot(fixture);
            peer.Send(34,Convert.FromHexString("22008521FFFF0100FFFF"));peer.Expect(1,34,[0,19,0]);peer.Barrier();
            That(Snapshot(fixture)==after,"重复租赁领奖不重复奖励或写账号完成");
        }
        using (var peer = Open(fixture))
        {
            peer.Select(questSlot,prepared:true);
            That(peer.CompletedQuestHistory[4+8581]==1,"重登回放真实租赁账号完成位");
            peer.Send(31,Convert.FromHexString("1F008521"));peer.Expect(1,31,[0,18,0]);peer.Barrier();
            That(fixture.Query("SELECT count(*) FROM character_quest_completion_receipts WHERE quest_id=8581") is [[1L]],
                "账号任务完成后不能重接或再次交付");
        }
        await fixture.StopAsync();
    }
}
