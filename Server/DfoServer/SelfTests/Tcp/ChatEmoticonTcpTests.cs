using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static class ChatEmoticonTcpTests
{
    public static async Task RunAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        var r = resources.ItemResources;
        var clock = new ConsumableUseSessionTests.FakeClock();
        long now = clock.GetUtcNow().ToUnixTimeSeconds();
        await using var fixture = new TcpTestFixture(source,Path.Combine(root,"chat-emoticon"),resources:resources,clock:clock);
        var repository = new CharacterRepository(fixture.Database);
        var owners = new AccountOwners();
        using (var owner = owners.Acquire("10000",()=>{}))
        {
            var candidate = CharacterCreation.Prepare("EmoteA",resources.Characters.Get(0),resources.Towns.InitialReturn,
                resources.Towns.InitialReturn,resources.Learning.Get);
            var inventory = candidate.Inventory with
            {
                Items = candidate.Inventory.Items.Add(Item(65,490013423,2)).Add(Item(66,490010306,1)).Add(Item(67,2660543,1))
            };
            var created = repository.Create(owner,candidate with {Inventory=inventory});
            created = repository.CommitInventory(owner,created,new(created.Inventory,created.Inventory with
                {Skins=SkinStorageState.Empty with {Emoticons=[new(410000009,checked((int)(now+30)))]}},"test_emoticon_owned"));
            var planned = InventoryRewardGrantService.TryGrantBatch(created,[new(2660543,1),new(2660546,1)],r.Items,r.GoldLimits,r.EpicPieces,
                clock.GetUtcNow(),contracts:r.Contracts);
            That(planned.Accepted,$"表情契约获取必须接受，错误={planned.Error}，原因={planned.RejectReason}");
            That(planned.Plan!.After.AccountWallet.ContractExpiry(88)==now+259200 && planned.Plan.After.AccountWallet.ContractExpiry(92)==now+86400,
                $"表情契约按PVF父子期限入账，实际88={planned.Plan.After.AccountWallet.ContractExpiry(88)}，92={planned.Plan.After.AccountWallet.ContractExpiry(92)}");
            That(planned.Plan.After.Items.OrderBy(item=>item.Uid).SequenceEqual(created.Inventory.Items.OrderBy(item=>item.Uid)),
                "表情契约获取不新增实体或消费旧背包对象");
            created=repository.CommitInventory(owner,created,planned.Plan);
            That(repository.LoadCharacter(owner,created.Slot).Inventory.AccountWallet.ContractExpiry(88)==now+259200,
                "表情契约通过统一资产事务持久化，重新装载保留资格");
            repository.Create(owner,candidate with {Name="EmoteSibling"});
        }
        foreach (var (account,name) in new[]{("emote-peer","EmoteB"),("emote-channel","EmoteC")})
        {
            using var owner = owners.Acquire(account,()=>{});
            repository.Create(owner,CharacterCreation.Prepare(name,resources.Characters.Get(0),resources.Towns.InitialReturn,
                resources.Towns.InitialReturn,resources.Learning.Get));
        }
        await fixture.StartAsync();
        var entries = ChannelTcpTests.Endpoints(fixture);
        using (var alpha=Open(fixture))
        using (var beta=Open(fixture,"emote-peer"))
        using (var other=Open(fixture,"emote-channel",gamePort:entries[11],channelNumber:11))
        {
            alpha.Select(resumeTown:true,room:false,emoticons:(new[]{new SkinOwnership(410000009,checked((int)(now+30)))},new[]{0,0,0,0}));
            beta.Select(resumeTown:true,room:false);Drain(alpha);
            other.Select(resumeTown:true,room:false);Drain(alpha);Drain(beta);
            That(alpha.PremiumServices.GetValueOrDefault((byte)88)>0 && alpha.PremiumServices.GetValueOrDefault((byte)92)>0 &&
                !beta.PremiumServices.ContainsKey(88),"1760恢复表情契约期限且其他账号不继承");
            Use(alpha,65);
            alpha.Expect(1,515,Build(w=>{w.Write((byte)1);w.Write((ushort)65);w.Write((byte)0);w.Write(199);}));
            ExpectStorage(alpha,[410000046]);
            alpha.Expect(0,1559,Build(w=>{w.Write((byte)2);w.Write((byte)3);w.Write(410000046);w.Write((byte)3);w.Write(410000009);}));
            alpha.Expect(0,1558,Build(w=>{w.Write((byte)3);w.Write(new byte[16]);}));alpha.Barrier();
            That(Count(65)==1 && AccountState().Emoticons is [{SkinId:410000046,ExpiresAt:0}],"单ID解锁券只扣一张并保存账号所有权");
            string before=fixture.Snapshot();Use(alpha,65);alpha.Expect(1,515);alpha.Barrier();
            That(before==fixture.Snapshot(),"永久重复解锁不扣源、不修改任何资产");

            Use(alpha,66);alpha.Expect(1,515);
            ExpectStorage(alpha,[410000004,410000005,410000006,410000007,410000046]);
            alpha.Expect(0,1559,Build(w=>{w.Write((byte)6);foreach(int id in new[]{410000004,410000005,410000006,410000007,410000046,410000009}){w.Write((byte)3);w.Write(id);}}));
            alpha.Expect(0,1558,Build(w=>{w.Write((byte)3);w.Write(new byte[16]);}));alpha.Barrier();
            That(Count(66)==0 && AccountState().Emoticons.Length==5,"多ID表情券整批入库，源只消费一次");

            int[] fullSlots = [410000007,410000004,410000005,410000006];
            byte[] fullSelection = Build(w=>{w.Write(3);foreach(int id in fullSlots)w.Write(id);w.Write(new byte[24]);});
            alpha.Send(1573,fullSelection);
            alpha.Expect(1,1573,Build(w=>{w.Write((byte)1);w.Write(fullSelection);}));alpha.Barrier();
            That(State(1).SelectedEmoticons.SequenceEqual(fullSlots),"四栏全填按原位置保存，不受单选皮肤校验拦截");
            string selectedBefore = fixture.Snapshot();
            foreach(int[] rejected in new[]{new[]{410000004,410000004,0,0,0},new[]{410000004,410000005,410000006,410000007,410000046}})
            {
                alpha.Send(1573,Build(w=>{w.Write(3);foreach(int id in rejected)w.Write(id);w.Write(new byte[20]);}));
                alpha.Expect(1,1573,new byte[]{0,19,0});alpha.Barrier();
                That(selectedBefore==fixture.Snapshot(),"重复表情或第五栏非空整单拒绝且保留已保存四栏");
            }

            alpha.Send(1573,Build(w=>{w.Write(3);w.Write(410000046);w.Write(new byte[36]);}));
            alpha.Expect(1,1573);alpha.Barrier();
            alpha.Send(1559,Build(w=>w.Write(410000046)));
            alpha.Expect(0,1535,Effect(alpha.Id,410000046));beta.Expect(0,1535,Effect(alpha.Id,410000046));
            alpha.Barrier();other.Barrier();
            alpha.Send(1559,Build(w=>w.Write(410000046)));
            alpha.Expect(0,1535,Effect(alpha.Id,410000046));beta.Expect(0,1535,Effect(alpha.Id,410000046));
            alpha.Barrier();beta.Barrier();
            beta.Send(1559,Build(w=>w.Write(410000046)));beta.Barrier();alpha.Barrier();

            clock.Advance(TimeSpan.FromSeconds(31));
            alpha.Send(1559,Build(w=>w.Write(410000009)));alpha.Barrier();beta.Barrier();
            alpha.Send(1559,Build(w=>w.Write(410000046)));
            alpha.Expect(0,1535,Effect(alpha.Id,410000046));beta.Expect(0,1535,Effect(alpha.Id,410000046));other.Barrier();
            That(Count(67)==1,"上号和发送表情不处理遗留背包契约");
            byte[] inline = [0x7f,(byte)'A',0x1e,0x20,1,0x1f,0x1e,0x20,45,0x1f,(byte)'B'];
            alpha.Send(17,Build(w=>{w.Write((byte)3);w.Write(alpha.Id);w.Write(0);w.Write((uint)inline.Length);w.Write(inline);}));
            byte[] expected=Build(w=>
            {
                w.Write((byte)3);w.Write(alpha.Id);w.Write((byte)2);
                w.Write(7u);w.Write(new byte[]{0x7f,(byte)'A',0x1e,0x20,1,0x1f,(byte)'B'});
                w.Write(6u);w.Write("EmoteA"u8);
            });
            alpha.Expect(0,12,expected);beta.Expect(0,12,expected);other.Barrier();
            That(fixture.Query("SELECT count(*) FROM account_chat_emoticons") is [[0L]],"聊天内嵌表情不隐式授予或写入指令配置");
            alpha.Send(7);alpha.Expect(0,6,Build(w=>w.Write(beta.Id)));alpha.Expect(1,7,[1]);alpha.Expect(0,2);Drain(beta);
            alpha.Select(1,accountSkins:AccountState());Drain(alpha);Drain(beta);
            int[] siblingSlots = [410000004,410000007,410000005,410000006];
            byte[] siblingSelection = Build(w=>{w.Write(3);foreach(int id in siblingSlots)w.Write(id);w.Write(new byte[24]);});
            alpha.Send(1573,siblingSelection);
            alpha.Expect(1,1573,Build(w=>{w.Write((byte)1);w.Write(siblingSelection);}));alpha.Barrier();
            alpha.Send(1559,Build(w=>w.Write(410000004)));
            alpha.Expect(0,1535,Effect(alpha.Id,410000004));beta.Barrier();other.Barrier();
            That(State(2).SelectedEmoticons.SequenceEqual(siblingSlots) && State(1).SelectedEmoticons.SequenceEqual(new[]{410000046,0,0,0}),
                "同账号其他角色可选择并发送已解锁表情，四栏独立保存");
            alpha.Send(17,Build(w=>
            {
                w.Write((byte)41);w.Write(alpha.Id);w.Write(0);w.Write((uint)inline.Length);w.Write(inline);
                w.Write(6u);w.Write("EmoteB"u8);w.Write((byte)0);
            }));
            using(var textReader=new BinaryReader(new MemoryStream(beta.Expect(0,65).Body)))
            {
                That(textReader.ReadByte()==41 && textReader.ReadByte()==0,"含表情的具名私聊保持原生类型和成功状态");
                string ReadText()=>DfoServer.Game.Chat.ChatText.Encoding.GetString(textReader.ReadBytes(textReader.ReadInt32()));
                That(ReadText()=="EmoteSibling" && textReader.ReadByte()>0,"具名私聊使用当前角色而非旧角色发言人");
                Bytes(DfoServer.Game.Chat.ChatText.Encoding.GetBytes(ReadText()),new byte[]{0x7f,(byte)'A',0x1e,0x20,1,0x1f,(byte)'B'},
                    "具名私聊同样保留合法表情、剔除未到等级的表情");
                _=ReadText();That(textReader.BaseStream.Position==textReader.BaseStream.Length,"具名私聊保留完整必读尾串");
            }
            alpha.Barrier();other.Barrier();
        }
        await fixture.StopAsync();await fixture.StartAsync("emoticon-restart");
        using(var peer=Open(fixture))
        {
            peer.Select(1,emoticons:([],new[]{410000004,410000007,410000005,410000006}),accountSkins:AccountState());
            peer.Send(1559,Build(w=>w.Write(410000004)));peer.Expect(0,1535,Effect(peer.Id,410000004));peer.Barrier();
        }
        await fixture.StopAsync();
        That(fixture.Query("PRAGMA integrity_check") is [["ok"]] && fixture.Query("PRAGMA foreign_key_check").Length==0,
            "表情解锁和广播不破坏存档完整性");

        InventoryItem Item(short slot,int id,int count)=>new(0,new(InventoryListType.Main,slot),InventoryRewardGrantService.Create(r.Items.Get(id),count,clock.GetUtcNow(),shopExpiration:true));
        int Count(short slot)=>Convert.ToInt32(fixture.Query("SELECT COALESCE(json_extract(i.item_core,'$.Value'),0) FROM character_inventory_slots s JOIN character_inventory_items i ON i.item_uid=s.item_uid WHERE s.character_id=1 AND s.list_type=0 AND s.slot_index=$p0",slot).FirstOrDefault()?[0]??0);
        SkinStorageState AccountState()=>JsonSerializer.Deserialize<SkinStorageState>((string)fixture.Query("SELECT state_json FROM account_skin_storage WHERE account='10000'")[0][0])!;
        SkinStorageState State(int id)=>JsonSerializer.Deserialize<SkinStorageState>((string)fixture.Query("SELECT weapon_skin_state FROM characters WHERE id=$p0",id)[0][0])!;
        void ExpectStorage(TcpPeer peer,int[] ids)=>peer.Expect(0,1557,Build(w=>
        {
            w.Write((byte)3);w.Write((ushort)1);w.Write(410000009);w.Write((int)(now+30));
            w.Write((ushort)ids.Length);foreach(int id in ids){w.Write(id);w.Write(0);}
        }));
    }
    static void Use(TcpPeer peer,ushort slot)=>peer.Send(515,Build(w=>{w.Write(slot);w.Write((byte)0);w.Write(0);w.Write(199);w.Write(0);w.Write(0);}));
    static byte[] Effect(ushort actor,int id)=>Build(w=>{w.Write(actor);w.Write(id);});
    static void Drain(TcpPeer peer)=>peer.Barrier(packet=>
    {
        if(packet is {Kind:0,Opcode:6})
            That(packet.Body.Length==2 && U16(packet.Body,0)>0,"同屏离场通知包含完整角色编号");
        else That(packet.Kind==0 && packet.Opcode is 2 or 3 or 22 or 23 or 24,"同屏入场收尾只接受已知场景通知");
    });
}
