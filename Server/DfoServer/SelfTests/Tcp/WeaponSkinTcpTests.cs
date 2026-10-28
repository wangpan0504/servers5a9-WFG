using System.Text.Json;
using System.Collections.Immutable;
using DfoServer.Game.Accounts;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Persistence;
using DfoServer.Testing.Inventory;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static class WeaponSkinTcpTests
{
    public static async Task RunAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        var assets = resources.ItemResources;
        That(assets.Skins.Recipes[1].ResultItemId == 490012391 && assets.Skins.Recipes[2].ResultItemId == 10308800 &&
            assets.Skins.Recipes[1].Materials is [{ ItemId: 490704427, Count: 1 }] &&
            assets.Skins.Recipes[2].Materials is [{ ItemId: 490704411, Count: 1 }] &&
            assets.Skins.Weapons.ContainsKey(32000) && assets.Skins.IsToken(10308800),
            "匹配PVF定义两种模具、动态来源外观券与测试步枪，不从服务器常量反推资源");
        That(assets.Skins.HasDamageFonts && assets.Skins.Token(10160911) is { Type: 2, SkinId: 3, Expiration.Days: 90 } &&
            assets.Skins.Token(10161605) is { Type: 2, SkinId: 3, Expiration.Days: 45 } &&
            assets.Skins.Token(10303302) is { Type: 2, SkinId: 8, Expiration: { AbsoluteTime: 0, Days: 0 } } &&
            assets.Skins.Token(490005168) is null,
            "新版字体按SKN读取，兼容不同期限，不将当前客户端未识别的旧add damage font动作冒充199");
        That(assets.Skins.HasCutscenes && assets.Skins.Token(10309379) is { Type: 1, SkinId: 30069, AccountWide: false } &&
            assets.Skins.Token(10309401) is { Type: 1, SkinId: 30068, Expiration.Days: 30, AccountWide: false } &&
            assets.Skins.Token(10303187) is { Type: 1, SkinId: 30024, AccountWide: true },
            "技能插画由真实SKN类型决定；[action target]为[account]的券保留账号归属，不压成单角色资产");
        That(assets.Skins.HasPartyFrames && assets.Skins.Token(490013498) is { Type: 0, SkinId: 420000002, AccountWide: true } &&
            assets.Skins.Token(490010773) is { Type: 0, SkinId: 420000001, AccountWide: true, Expiration: { AbsoluteTime: 0, Days: 0 } } &&
            assets.Skins.Token(490013423) is { Type: 3, SkinId: 410000046, Expiration.Days: 0, AccountWide: true } &&
            assets.Skins.Emoticons.Contains(410000046) && assets.Skins.PartyFrames.Contains(20000),
            "组队头像框与即时表情按0429E5F0的SKN[type]映射读到类别0和3，默认头像框20000在册而表情没有默认");
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "weapon-skin"), resources: resources);
        var repo = new CharacterRepository(fixture.Database);
        var owners = new AccountOwners();
        using (var owner = owners.Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            repo.Create(owner, new("SkinGunner", resources.Towns.Room, resources.Towns.InitialReturn)
            {
                Job = 2, Level = 75, AcceptedStoryLevel = 1, Skills = resources.Characters.Get(2).Skills,
                Inventory = new([Weapon(9), Weapon(35), Weapon(36), Weapon(37),
                    Material(121, 490704411, 2), Material(122, 490704427, 2),
                    Material(71, 10160911, 1), Material(72, 10161605, 1), Material(73, 10303302, 1),
                    Material(74, 10303302, 1), Material(75, 10160602, 1),
                    Material(76,10309379,1),Material(77,10309401,1),Material(80,10303187,1),Material(81,10301425,1),
                    Material(82,490013423,1),Material(83,490010773,1)], Gold: 54321)
            });
            repo.Create(owner, new("SkinSibling", resources.Towns.Room, resources.Towns.InitialReturn)
            {
                Level = 75, AcceptedStoryLevel = 1, Skills = resources.Characters.Get(0).Skills,
                Inventory = new([Weapon(9), Material(121, 490704411, 1), Material(122, 490704427, 1)])
            });
            repo.Create(owner, new("SkinNovice", resources.Towns.Room, resources.Towns.InitialReturn)
            {
                Job = 0, Level = 1, AcceptedStoryLevel = 1,
                Inventory = new([new(0, new(InventoryListType.Main, 65),
                    InventoryRewardGrantService.Create(assets.Items.Get(490012391)) with { EnchantCardId = 101040572 })])
            });
        }
        string beforeUpgrade = fixture.Snapshot("SELECT id,revision,job,level,gold FROM characters ORDER BY id") + Assets();
        DatabaseSchemaTests.RewindRaidLifecycle(fixture.Database);
        fixture.Sql(DatabaseSchemaTests.GuildExtensionFixtureRewindSql + "ALTER TABLE characters DROP COLUMN weapon_skin_state; ALTER TABLE characters DROP COLUMN selected_skill_tree; PRAGMA user_version=66;");
        await fixture.StartAsync();
        That(fixture.Query("PRAGMA user_version") is [[long upgradedVersion]] && upgradedVersion == DfoServer.Persistence.SqliteMigrations.CurrentVersion &&
            fixture.Snapshot("SELECT id,revision,job,level,gold FROM characters ORDER BY id") + Assets() == beforeUpgrade &&
            State().Same(SkinStorageState.Empty), "v66增量升级67只添加空皮肤仓库（其后主线v68永久属性、v69–v73冒险团与副本次数v74/v75只补列建表），不重写玩家资产、等级或revision");
        long firstOutputUid;
        using (var peer = Open(fixture))
        {
            peer.Send(1599, [4, 1, 0, 0, 0, 0, 9, 0]);
            peer.Expect(1, 1599, [0, 3, 0]);
            peer.Select();
            string before = Snapshot();
            peer.Send(1599, new byte[7]); peer.Expect(1, 1599, [0, 3, 0]);
            peer.Send(1599, [3, 1, 0, 0, 0, 0, 9, 0]); peer.Expect(1, 1599, [0, 3, 0]);
            Select(peer, 32000); peer.Expect(1, 1573, [0, 19, 0]);
            peer.Barrier();
            That(Snapshot() == before, "未选角、畸形正文、错误皮肤类别和未拥有选择均不改变资产，连接可继续");

            long sourceUid = Uid(9);
            Make(peer, 1, 9);
            byte[] delta = peer.Expect(0, 14).Body;
            Entry(delta, 9, -1, 0);
            Entry(delta, 65, 490012391, 1, 32000);
            Entry(delta, 122, 490704427, 1);
            peer.Expect(1, 1599, [1]);
            firstOutputUid = Uid(65);
            That(firstOutputUid != sourceUid && fixture.Query("SELECT item_uid FROM character_inventory_items WHERE item_uid=$p0", sourceUid).Length == 0,
                "秘传配方消耗原武器并创建独立外观券UID，附魔字段保存来源而不是复制装备属性");
            That(Core(65) is { ItemId: 490012391, EnchantCardId: 32000, Value: 1 } &&
                fixture.Query("SELECT gold FROM characters WHERE id=1") is [[54321L]], "制作保存完整来源核心，配方未声明金币时不额外收费");

            Make(peer, 2, 35);
            delta = peer.Expect(0, 14).Body;
            Entry(delta, 35, -1, 0); Entry(delta, 66, 10308800, 1, 32000); Entry(delta, 121, 490704411, 1);
            peer.Expect(1, 1599, [1]);
        }
        using (var peer = Open(fixture))
        {
            peer.Select();
            string before = Snapshot();
            fixture.Sql("CREATE TRIGGER weapon_skin_add_fail BEFORE UPDATE OF weapon_skin_state ON characters WHEN json_array_length(NEW.weapon_skin_state,'$.Owned')>json_array_length(OLD.weapon_skin_state,'$.Owned') BEGIN SELECT RAISE(ABORT,'weapon skin add rollback'); END");
            Use(peer, 66); peer.Closed();
            That(Snapshot() == before, "入库写状态失败时同时保留外观券和空所有权，不先发布成功或吞掉券");
            fixture.Sql("DROP TRIGGER weapon_skin_add_fail");
        }
        using (var peer = Open(fixture))
        {
            peer.Select();
            Use(peer, 66);
            peer.Expect(1, 515, [1, 66, 0, 0, 199, 0, 0, 0]);
            peer.Expect(0, 1557, [4, 1, 0, 0, 125, 0, 0]);
            peer.Expect(0, 1559, [1, 4, 0, 125, 0, 0]);
            peer.Barrier();
            That(State().Owned.SequenceEqual([32000]) && State().Selected == 0 &&
                fixture.Query("SELECT item_uid FROM character_inventory_slots WHERE character_id=1 AND list_type=0 AND slot_index=66").Length == 0,
                "右键入库消费一张券并持久保存角色所有权，ACK后只发布武器类别，不重复发送源获得差量");

            Select(peer, 32000);
            peer.Expect(1, 1573, SelectionAck(32000));
            That(U32(peer.Expect(0, 2).Body, 115) == 32000, "选择ACK后同步城镇Actor的原生幻化字段，不只刷新仓库UI和战斗缓存");
            peer.Barrier();
            That(State().Selected == 32000, "选用外观只改变独立选择，不替换普通武器、装扮或附魔");
            string before = Snapshot();
            Select(peer, 32000); peer.Expect(1, 1573, SelectionAck(32000));
            Use(peer, 65);
            peer.Expect(1, 515, [0, 3, 0, 65, 0, 0, 199]);
            Make(peer, 2, 36); peer.Expect(1, 1599, [0, 20, 0]);
            peer.Barrier();
            That(Snapshot() == before && Uid(65) == firstOutputUid,
                "相同选择是无写入回放；重复入库和制作同一不可交易外观均不扣券、模具或武器");

            before = Snapshot();
            fixture.Sql("CREATE TRIGGER weapon_skin_fail BEFORE INSERT ON inventory_audit_log WHEN NEW.action_name='weapon_skin_make' BEGIN SELECT RAISE(ABORT,'weapon skin rollback'); END");
            Make(peer, 1, 37); peer.Closed();
            That(Snapshot() == before, "制作末段SQL失败无成功ACK，回滚模具、原武器、产物、UID归属及revision");
            fixture.Sql("DROP TRIGGER weapon_skin_fail");
        }
        using (var peer = Open(fixture))
        {
            peer.Select(weaponSkins: ([32000], 32000));
            That(WeaponSkinTests.RosterWeaponSkin(peer.Roster, 0) == 32000 &&
                WeaponSkinTests.RosterWeaponSkin(peer.Roster, 1) == 0 &&
                WeaponSkinTests.RosterWeaponSkin(peer.Roster, 2) == 0,
                "登录名册 tail[42] 恢复已选幻化，无幻化角色保持0且不改包长语义");
            That(State().Selected == 32000 && Uid(65) == firstOutputUid, "重连恢复已保存选择及同一来源外观券UID");
            Select(peer, 0); peer.Expect(1, 1573, SelectionAck(0));
            That(U32(peer.Expect(0, 2).Body, 115) == 0, "取消选择同步实际场景Actor，不依赖重选角色恢复普通外观");
            That(State().Selected == 0 && State().Owned.SequenceEqual([32000]), "取消选择不删除已拥有外观或改变真实装备");
            Make(peer, 1, 37);
            var delta = peer.Expect(0, 14).Body;
            Entry(delta, 37, -1, 0); Entry(delta, 65, 490012391, 2, 32000);
            Entry(delta, 122, -1, 0);
            peer.Expect(1, 1599, [1]);
            string before = Snapshot();
            Make(peer, 1, 36); peer.Expect(1, 1599, [0, 3, 0]);
            peer.Barrier();
            That(Snapshot() == before && Core(65).Value == 2, "SQL恢复后可重试；相同来源券正常合堆，模具不足时不删除剩余武器");
            peer.Send(7); peer.Expect(1, 7, [1]);
            byte[] canceledRoster = peer.Expect(0, 2).Body;
            That(WeaponSkinTests.RosterWeaponSkin(canceledRoster, 0) == 0 &&
                WeaponSkinTests.RosterWeaponSkin(canceledRoster, 1) == 0,
                "取消幻化后返回选角，名册与入城字段同为0");
            peer.Select(1, weaponSkins: ([], 0));
            That(fixture.Query("SELECT weapon_skin_state FROM characters WHERE id=2") is [[string siblingState]] &&
                JsonSerializer.Deserialize<SkinStorageState>(siblingState)!.Owned.IsEmpty,
                "同连接换角清空客户端旧皮肤视图，但不复制角色所有权或选择");
            string siblingBefore = Snapshot();
            Make(peer, 2, 9); peer.Expect(1, 1599, [0, 106, 0]); peer.Barrier();
            That(Snapshot() == siblingBefore, "神奇模具保留当前职业可佩戴资格，不销毁其他职业武器");
            Make(peer, 1, 9);
            var crossJobDelta = peer.Expect(0, 14).Body;
            Entry(crossJobDelta, 9, -1, 0); Entry(crossJobDelta, 65, 490012391, 1, 32000);
            peer.Expect(1, 1599, [1]);
            Use(peer, 65);
            peer.Expect(1, 515, [0, 3, 0, 65, 0, 0, 199]);
            peer.Barrier();
            That(JsonSerializer.Deserialize<SkinStorageState>((string)fixture.Query("SELECT weapon_skin_state FROM characters WHERE id=2")[0][0])!.Owned.IsEmpty,
                "秘传模具可制作其他职业的账号绑定券，使用入库仍核对接收角色的职业");
        }
        using (var peer = Open(fixture))
        {
            peer.Select(2);
            That(WeaponSkinTests.RosterWeaponSkin(peer.Roster, 0) == 0 &&
                WeaponSkinTests.RosterWeaponSkin(peer.Roster, 2) == 0, "未选择幻化的1级角色登录名册保持0");
            Use(peer, 65);
            peer.Expect(1, 515, [1, 65, 0, 0, 199, 0, 0, 0]);
            peer.Expect(0, 1557, Build(w => { w.Write((byte)4); w.Write((ushort)1); w.Write(101040572); }));
            peer.Expect(0, 1559, Build(w => { w.Write((byte)1); w.Write((byte)4); w.Write(101040572); }));
            Select(peer, 101040572);
            peer.Expect(1, 1573, SelectionAck(101040572));
            That(U32(peer.Expect(0, 2).Body, 115) == 101040572, "1级剑士入库及选择65级光剑外观，ACK后立即投影Actor");
            peer.Barrier();
            That(State(3).Selected == 101040572, "低等级外观取得与选择沿原库存事务持久化");
        }
        using (var peer = Open(fixture))
        {
            peer.Select(weaponSkins: ([32000],0));
            string before = Snapshot();
            peer.Send(515, Build(w => { w.Write((ushort)71); w.Write((byte)0); w.Write(1); w.Write(199); w.Write(0); w.Write(0); }));
            peer.Expect(1,515,[0,3,0,71,0,0,199]);
            Use(peer,75); peer.Expect(1,515,[0,3,0,75,0,0,199]);
            peer.Barrier();
            That(Snapshot() == before, "动作199拒绝非零保留字段及已过期的效果日期，均保留消耗品");
            fixture.Sql("CREATE TRIGGER font_add_fail BEFORE UPDATE OF weapon_skin_state ON characters WHEN json_array_length(NEW.weapon_skin_state,'$.DamageFonts')>0 BEGIN SELECT RAISE(ABORT,'font rollback'); END");
            Use(peer,71); peer.Closed();
            That(Snapshot() == before, "字体与源道具在原库存事务中共同回滚，不提前消耗或发布成功");
            fixture.Sql("DROP TRIGGER font_add_fail");
        }
        SkinOwnership[] savedFonts;
        using (var peer = Open(fixture))
        {
            peer.Select(weaponSkins: ([32000],0));
            long started = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            AddFont(peer,71,3);
            var first = State().DamageFonts.Single();
            That(first.SkinId == 3 && first.ExpiresAt >= started + 90L*86400 &&
                first.ExpiresAt <= DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 90L*86400 && State().SelectedDamageFont == 0,
                "90天字体入库按使用时刻起算；不误用STK获得日期，也不擅自自动选用");
            AddFont(peer,72,3);
            That(State().DamageFonts.Single().ExpiresAt == first.ExpiresAt + 45*86400,
                "同一个SKN的45天券在有效期末续期，所有权不重复");
            AddFont(peer,73,8);
            That(State().DamageFonts.Single(row=>row.SkinId==8).ExpiresAt == 0, "unlimit字体保存真正永久期限");
            peer.Send(1573,Build(w=>{w.Write(2);w.Write(3);w.Write(new byte[36]);}));
            peer.Expect(1,1573,Build(w=>{w.Write((byte)1);w.Write(2);w.Write(3);w.Write(new byte[36]);}));
            string before = Snapshot();
            peer.Send(1573,Build(w=>{w.Write(2);w.Write(3);w.Write(new byte[36]);}));
            peer.Expect(1,1573,Build(w=>{w.Write((byte)1);w.Write(2);w.Write(3);w.Write(new byte[36]);}));
            Use(peer,74); peer.Expect(1,515,[0,3,0,74,0,0,199]);
            peer.Send(1573,Build(w=>{w.Write(2);w.Write(39);w.Write(new byte[36]);})); peer.Expect(1,1573,[0,19,0]);
            peer.Barrier();
            That(State().SelectedDamageFont == 3 && Snapshot() == before,
                "重复字体选择零写入；重复永久券与未拥有选择均保留资产，武器所有权不受影响");
            savedFonts = State().DamageFonts.ToArray();
        }
        using (var peer = Open(fixture))
        {
            peer.Select(weaponSkins: ([32000],0),damageFonts:(savedFonts,3));
            peer.Send(7); peer.Expect(1,7,[1]); peer.Expect(0,2);
            peer.Select(1);
            That(JsonSerializer.Deserialize<SkinStorageState>((string)fixture.Query("SELECT weapon_skin_state FROM characters WHERE id=2")[0][0])!.DamageFonts.IsEmpty,
                "同连接换角清除之前的字体目录和选择，不复制同账号角色所有权");
        }
        using (var peer = Open(fixture))
        {
            peer.Select(weaponSkins:([32000],0),damageFonts:(savedFonts,3));
            ChooseCutscenes(peer,[30000]);
            string before=Snapshot();
            ChooseCutscenes(peer,[30000]); peer.Barrier();
            That(Snapshot()==before, "真实失败请求type1/30000现在成功，重复默认选择零写入");
            fixture.Sql("CREATE TRIGGER cutscene_add_fail BEFORE UPDATE OF weapon_skin_state ON characters WHEN json_array_length(NEW.weapon_skin_state,'$.Cutscenes')>0 BEGIN SELECT RAISE(ABORT,'cutscene rollback'); END");
            Use(peer,76); peer.Closed();
            That(Snapshot()==before, "技能插画入库与消费共用库存事务，SQL失败保留券及旧选择");
            fixture.Sql("DROP TRIGGER cutscene_add_fail");
        }
        SkinOwnership[] savedCutscenes;
        using (var peer = Open(fixture))
        {
            peer.Select(weaponSkins:([32000],0),damageFonts:(savedFonts,3));
            AddCutscene(peer,76,30069);
            AddCutscene(peer,77,30068);
            ChooseCutscenes(peer,[30000,30069,30068]);
            string before=Snapshot();
            Use(peer,81);peer.Expect(1,515,[0,3,0,81,0,0,199]);
            peer.Send(1573,Build(w=>{w.Write(1);w.Write(30002);w.Write(new byte[36]);}));peer.Expect(1,1573,[0,19,0]);
            peer.Send(1573,Build(w=>{w.Write(1);w.Write(30069);w.Write(30069);w.Write(new byte[32]);}));peer.Expect(1,1573,[0,19,0]);
            peer.Barrier();
            That(State().SelectedCutscenes.SequenceEqual([30000,30069,30068]) && Snapshot()==before,
                "多选完整保存；未拥有、重复选择和职业不符的券拒绝，不损坏武器或字体状态");
            savedCutscenes=State().Cutscenes.ToArray();
        }
        using(var peer=Open(fixture))
        {
            peer.Select(weaponSkins:([32000],0),damageFonts:(savedFonts,3),cutscenes:(savedCutscenes,[30000,30069,30068]));
            peer.Send(7);peer.Expect(1,7,[1]);peer.Expect(0,2);peer.Select(1);
            That(JsonSerializer.Deserialize<SkinStorageState>((string)fixture.Query("SELECT weapon_skin_state FROM characters WHERE id=2")[0][0])!.Cutscenes.IsEmpty,
                "重连恢复插画目录和全部选择，同连接换角不继承另一角色的插画");
        }
        await fixture.StopAsync();
        var expired = State() with
        {
            DamageFonts = savedFonts.Select(row=>row.SkinId==3 ? row with
                { ExpiresAt=(int)DateTimeOffset.UtcNow.ToUnixTimeSeconds()-1 } : row).ToImmutableArray(),
            Cutscenes = savedCutscenes.Select(row=>row.SkinId==30068 ? row with
                { ExpiresAt=(int)DateTimeOffset.UtcNow.ToUnixTimeSeconds()-1 } : row).ToImmutableArray()
        };
        fixture.Sql("UPDATE characters SET weapon_skin_state=$p0 WHERE id=1",JsonSerializer.Serialize(expired));
        await fixture.StartAsync();
        using (var peer = Open(fixture))
        {
            peer.Select(2, weaponSkins: ([101040572], 101040572));
            peer.Barrier();
            That(State(3).Selected == 101040572, "服务重建与重选角色不因原武器等级或技能门槛清空已选外观");
        }
        using (var peer = Open(fixture))
        {
            peer.Select(weaponSkins: ([32000],0),damageFonts:([savedFonts.Single(row=>row.SkinId==8)],1),
                cutscenes:([savedCutscenes.Single(row=>row.SkinId==30069)],[30000,30069]));
            peer.Expect(0,1557,Build(w=>
            {
                w.Write((byte)2);w.Write((ushort)2);w.Write(1);w.Write(0);w.Write(8);w.Write(0);w.Write((ushort)0);
            }));
            peer.Expect(0,1558,[2,1,0,0,0]);
            ExpectCutscenes(peer,State());
            peer.Barrier();
            That(State().SelectedDamageFont==1 && State().DamageFonts.Any(row=>row.SkinId==3),
                "请求后到期刷新只把选择回退为默认字体，历史期限仍保留，不删除所有权或创建独立在线状态");
            That(State().SelectedCutscenes.SequenceEqual([30000,30069]) && State().Cutscenes.Length==2,
                "同一到期事务移除失效插画选择但保留永久选择及历史所有权，不清空整组");
            string before = Snapshot();
            peer.Send(1573,Build(w=>{w.Write(2);w.Write(3);w.Write(new byte[36]);}));peer.Expect(1,1573,[0,19,0]);peer.Barrier();
            That(Snapshot()==before, "重建服务后过期字体不可选择，回退完成后不反复写入或续期");
        }
        using (var peer = Open(fixture))
        {
            peer.Select(weaponSkins:([32000],0),damageFonts:([savedFonts.Single(row=>row.SkinId==8)],1),
                cutscenes:([savedCutscenes.Single(row=>row.SkinId==30069)],[30000,30069]));
            That(AccountState().Cutscenes.IsEmpty && AccountState().Emoticons.IsEmpty, "账号券入库前账号皮肤仓保持空表");

            Use(peer,80);
            peer.Expect(1,515,Build(w=>{w.Write((byte)1);w.Write((short)80);w.Write((byte)0);w.Write(199);}));
            // 账号归属的插画只进第二份列表，角色列表仍是默认30000与已拥有的30069。
            peer.Expect(0,1557,Build(w=>
            {
                w.Write((byte)1);w.Write((ushort)2);w.Write(30000);w.Write(0);w.Write(30069);w.Write(0);
                w.Write((ushort)1);w.Write(30024);w.Write(0);
            }));
            peer.Expect(0,1559,Build(w=>{w.Write((byte)1);w.Write((byte)1);w.Write(30024);}));
            peer.Expect(0,1558,Build(w=>{w.Write((byte)1);w.Write((ushort)2);w.Write(30000);w.Write(30069);}));

            Use(peer,82);
            peer.Expect(1,515,Build(w=>{w.Write((byte)1);w.Write((short)82);w.Write((byte)0);w.Write(199);}));
            peer.Expect(0,1557,Build(w=>
            {
                w.Write((byte)3);w.Write((ushort)0);w.Write((ushort)1);w.Write(410000046);w.Write(0);
            }));
            peer.Expect(0,1559,Build(w=>{w.Write((byte)1);w.Write((byte)3);w.Write(410000046);}));
            peer.Expect(0,1558,Build(w=>{w.Write((byte)3);w.Write(0);w.Write(0);w.Write(0);w.Write(0);}));
            peer.Barrier();
            That(AccountState().Cutscenes is [{SkinId:30024,ExpiresAt:0}] &&
                AccountState().Emoticons is [{SkinId:410000046,ExpiresAt:0}] &&
                State().Cutscenes.All(row=>row.SkinId!=30024) && State().Emoticons.IsEmpty,
                "账号券写入账号皮肤仓，角色皮肤仓不复制账号条目");

            peer.Send(1573,Build(w=>{w.Write(3);w.Write(410000046);w.Write(new byte[36]);}));
            peer.Expect(1,1573,Build(w=>{w.Write((byte)1);w.Write(3);w.Write(410000046);w.Write(new byte[36]);}));
            peer.Barrier();
            That(State().SelectedEmoticons.SequenceEqual([410000046,0,0,0]),
                "账号皮肤仓的即时表情可以成为本角色4栏选择，选择仍保存在角色存档");

            // 490010773是正式的[account]归属永久头像框券，用它验证账号仓入库；其余头像框券都带已过的[date]期限。
            That(State().SelectedPartyFrame == 0, "组队头像框未选择时角色存档保持0，服务端仍按原生默认20000下发");
            peer.Send(1573,Build(w=>{w.Write(0);w.Write(20000);w.Write(new byte[36]);}));
            peer.Expect(1,1573,Build(w=>{w.Write((byte)1);w.Write(0);w.Write(20000);w.Write(new byte[36]);}));
            peer.Barrier();
            That(State().SelectedPartyFrame == 20000, "组队头像框默认20000可以成为本角色选择并写入角色存档");

            Use(peer,83);
            peer.Expect(1,515,Build(w=>{w.Write((byte)1);w.Write((short)83);w.Write((byte)0);w.Write(199);}));
            peer.Expect(0,1557,Build(w=>
            {
                w.Write((byte)0);w.Write((ushort)1);w.Write(20000);w.Write(0);
                w.Write((ushort)1);w.Write(420000001);w.Write(0);
            }));
            peer.Expect(0,1559,Build(w=>{w.Write((byte)1);w.Write((byte)0);w.Write(420000001);}));
            peer.Expect(0,1558,Build(w=>{w.Write((byte)0);w.Write(20000);}));
            peer.Barrier();
            That(AccountState().PartyFrames is [{SkinId:420000001,ExpiresAt:0}] && State().PartyFrames.IsEmpty,
                "账号归属的头像框券写入账号皮肤仓，角色皮肤仓不复制条目");

            peer.Send(1573,Build(w=>{w.Write(0);w.Write(420000001);w.Write(new byte[36]);}));
            peer.Expect(1,1573,Build(w=>{w.Write((byte)1);w.Write(0);w.Write(420000001);w.Write(new byte[36]);}));
            peer.Barrier();
            That(State().SelectedPartyFrame == 420000001, "账号皮肤仓的头像框可以成为本角色选择，选择仍保存在角色存档");

            peer.Send(7);peer.Expect(1,7,[1]);peer.Expect(0,2);
            peer.Select(1, accountSkins: AccountState());
            peer.Send(1573,Build(w=>{w.Write(1);w.Write(30024);w.Write(new byte[36]);}));
            peer.Expect(1,1573,Build(w=>{w.Write((byte)1);w.Write(1);w.Write(30024);w.Write(new byte[36]);}));
            peer.Barrier();
            That(State(2).SelectedCutscenes.SequenceEqual([30024]) && State(2).Cutscenes.IsEmpty,
                "同账号其他角色看见并能选择账号皮肤仓的插画，自身角色仓仍为空");
        }
        await fixture.StopAsync();
        That(fixture.Query("PRAGMA integrity_check") is [["ok"]] && fixture.Query("PRAGMA foreign_key_check").Length == 0,
            "武器幻化存档保持SQLite及外键完整");
        fixture.Sql("ALTER TABLE characters DROP COLUMN weapon_skin_state;");
        Reject<SqliteException>(() => new CharacterRepository(fixture.Database), "同版本缺必需武器字段拒绝初始化，不静默补空或覆盖存档");

        InventoryItem Weapon(short slot) => new(0, new(InventoryListType.Main, slot),
            InventoryRewardGrantService.Create(assets.Items.Get(32000)) with { SealFlag = 0 });
        InventoryItem Material(short slot, int id, int count) => new(0, new(InventoryListType.Main, slot),
            // 正向样品隔离道具的历史绝对期限；解锁动作自身的期限仍按原PVF核验。
            InventoryRewardGrantService.Create(assets.Items.Get(id), count, shopExpiration: true));
        long Uid(short slot) => Convert.ToInt64(fixture.Query("SELECT item_uid FROM character_inventory_slots WHERE character_id=1 AND list_type=0 AND slot_index=$p0", slot)[0][0]);
        ItemCore Core(short slot) => JsonSerializer.Deserialize<ItemCore>((string)fixture.Query(
            "SELECT item_core FROM character_inventory_items WHERE item_uid=$p0", Uid(slot))[0][0])!;
        SkinStorageState State(int characterId = 1) => JsonSerializer.Deserialize<SkinStorageState>((string)fixture.Query(
            "SELECT weapon_skin_state FROM characters WHERE id=$p0", characterId)[0][0])!;
        SkinStorageState AccountState() => fixture.Query("SELECT state_json FROM account_skin_storage WHERE account=$p0",
            fixture.Options.DevelopmentAccount) is [[string accountState]]
            ? JsonSerializer.Deserialize<SkinStorageState>(accountState)! : SkinStorageState.Empty;
        string Snapshot() => fixture.Snapshot("SELECT revision,gold,weapon_skin_state FROM characters ORDER BY id") + Assets();
        string Assets() => fixture.Snapshot("SELECT item_uid,item_core FROM character_inventory_items ORDER BY item_uid") +
            fixture.Snapshot("SELECT * FROM character_inventory_slots ORDER BY item_uid") +
            fixture.Snapshot("SELECT * FROM inventory_audit_log ORDER BY audit_id");

        void AddFont(TcpPeer peer,short slot,int id)
        {
            Use(peer,slot);
            peer.Expect(1,515,Build(w=>{w.Write((byte)1);w.Write(slot);w.Write((byte)0);w.Write(199);}));
            var state = State();
            peer.Expect(0,1557,Build(w=>
            {
                w.Write((byte)2);w.Write((ushort)(state.DamageFonts.Length+1));w.Write(1);w.Write(0);
                foreach(var row in state.DamageFonts){w.Write(row.SkinId);w.Write(row.ExpiresAt);}
                w.Write((ushort)0);
            }));
            peer.Expect(0,1559,Build(w=>{w.Write((byte)1);w.Write((byte)2);w.Write(id);}));
            peer.Expect(0,1558,Build(w=>{w.Write((byte)2);w.Write(state.SelectedDamageFont is >1 ? state.SelectedDamageFont : 1);}));
            peer.Barrier();
            That(fixture.Query("SELECT item_uid FROM character_inventory_slots WHERE character_id=1 AND list_type=0 AND slot_index=$p0",slot).Length==0,
                "原生515成功ACK扣源后只同步仓库，不能追加获得数量或重复扣源");
        }

        void ChooseCutscenes(TcpPeer peer,int[] ids)
        {
            byte[] body=Build(w=>{w.Write(1);foreach(int id in ids)w.Write(id);w.Write(new byte[(10-ids.Length)*4]);});
            peer.Send(1573,body);peer.Expect(1,1573,[(byte)1,..body]);
        }
        void ExpectCutscenes(TcpPeer peer,SkinStorageState state)
        {
            var active=state.Cutscenes.Where(row=>row.Active(DateTimeOffset.UtcNow.ToUnixTimeSeconds())).ToArray();
            peer.Expect(0,1557,Build(w=>
            {
                w.Write((byte)1);w.Write((ushort)(active.Length+1));w.Write(30000);w.Write(0);
                foreach(var row in active){w.Write(row.SkinId);w.Write(row.ExpiresAt);}w.Write((ushort)0);
            }));
            peer.Expect(0,1558,Build(w=>
            {w.Write((byte)1);w.Write((ushort)state.SelectedCutscenes.Length);foreach(int id in state.SelectedCutscenes)w.Write(id);}));
        }
        void AddCutscene(TcpPeer peer,short slot,int id)
        {
            Use(peer,slot);peer.Expect(1,515,Build(w=>{w.Write((byte)1);w.Write(slot);w.Write((byte)0);w.Write(199);}));
            var state=State();
            peer.Expect(0,1557,Build(w=>
            {
                w.Write((byte)1);w.Write((ushort)(state.Cutscenes.Length+1));w.Write(30000);w.Write(0);
                foreach(var row in state.Cutscenes){w.Write(row.SkinId);w.Write(row.ExpiresAt);}w.Write((ushort)0);
            }));
            peer.Expect(0,1559,Build(w=>{w.Write((byte)1);w.Write((byte)1);w.Write(id);}));
            peer.Expect(0,1558,Build(w=>
            {w.Write((byte)1);w.Write((ushort)state.SelectedCutscenes.Length);foreach(int skin in state.SelectedCutscenes)w.Write(skin);}));
            peer.Barrier();
        }
    }

    static void Make(TcpPeer peer, int recipe, short slot) => peer.Send(1599, Build(w =>
    { w.Write((byte)4); w.Write(recipe); w.Write((byte)0); w.Write(slot); }));
    static void Select(TcpPeer peer, int id) => peer.Send(1573, Build(w =>
    { w.Write(4); w.Write(id); w.Write(new byte[36]); }));
    static byte[] SelectionAck(int id) => Build(w => { w.Write((byte)1); w.Write(4); w.Write(id); w.Write(new byte[36]); });
    static void Use(TcpPeer peer, short slot) => peer.Send(515, Build(w =>
    { w.Write(slot); w.Write((byte)0); w.Write(0); w.Write(199); w.Write(0); w.Write(0); }));
    static void Entry(byte[] delta, short slot, int id, int count, int? source = null)
    {
        That(delta.Length == 3 + 123 * U16(delta, 1), "幻化增量保持完整123B物品条目");
        int at = Enumerable.Range(0, U16(delta, 1)).Select(i => 3 + i * 123).Single(i => U16(delta, i) == slot);
        That(U32(delta, at + 2) == unchecked((uint)id) && U32(delta, at + 6) == count &&
            (source is null || U32(delta, at + 14) == source.Value), "幻化实际槽位、数量与来源匹配提交结果");
    }
}
