using System.Text.Json;
using System.Collections.Immutable;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Channels;
using PvfLib;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class ConsumableUseSessionTests
{
    public static void Run(TownCatalog towns, CharacterCatalog characters, PvfArchive archive)
    {
        RunUsePlaces(towns, characters, archive);
        RunDungeonRestrictions(towns, characters, archive);
        RunTimedStates(towns, characters, archive);
        RunStaminaVariant(towns, characters, archive);
        RunEpicDropStates(towns, characters, archive);
        RunLanternEpicDropStates(towns, characters);
        RunDungeonEpicDropStates(towns, characters, archive);
        RunCompositeDropStates(towns, characters, archive);
        RunVariousAssociations(towns, characters, archive);
        RunHellLegendEpicDropStates(towns, characters, archive);
        RunVillageMoveSpeedStates(towns, characters, archive);
        RunRevivalTimeouts(towns, characters, archive);
        var bossDefinition = ConsumableUseTests.Hp with { ItemId = 490705256,
            BossSelectionBuff = new(300, 7191, 5, [new(92689, 40), new(92690, 40), new(92697, 20)]) };
        using (var boss = new Fixture(towns, characters, hp: bossDefinition))
        {
            boss.Select();
            boss.Sql("CREATE TRIGGER fail_boss_potion BEFORE INSERT ON character_boss_selection_buff BEGIN SELECT RAISE(ABORT,'boss potion rollback'); END");
            Reject<Microsoft.Data.Sqlite.SqliteException>(() => boss.Use(), "药效写入失败不先扣药或发布成功");
            That(boss.Count == 3 && boss.Read().Inventory.BossSelectionBuff is null, "药效和源消耗同事务回滚");
        }
        using (var boss = new Fixture(towns, characters, hp: bossDefinition))
        {
            boss.Select();
            That(boss.Use().Outcome == ConsumableUseOutcome.Committed && boss.Count == 2 &&
                boss.Read().Inventory.BossSelectionBuff is { Remaining: 5 }, "城镇使用后源与五次效果一起落库");
            var saved = boss.Read().Inventory.BossSelectionBuff;
            var projection = boss.Session.PlanBossSelectionProjection();
            That(projection is { BuffId: 300, Remaining: 5, RemovedBuffId: 0 } && projection.IsCurrent() &&
                boss.Session.PlanBossSelectionProjection() is null, "已提交五次只投影一次，普通请求不重复发BUFF");
            var replay = boss.Session.PlanBossSelectionProjection(force: true);
            That(replay is { BuffId: 300, Remaining: 5 } && replay.IsCurrent() && !projection!.IsCurrent(),
                "Actor重建重投并使旧投影回执失效");
            boss.Session.ReturnToRoster(); boss.Select();
            That(!replay!.IsCurrent() && boss.Session.PlanBossSelectionProjection() is { BuffId: 300, Remaining: 5 },
                "重选只从持久库存重投并拒绝旧epoch发布");
            That(BossSelectionBuff.Same(saved, boss.Session.Selected!.Inventory.BossSelectionBuff), "重选只重读原次数与日截止，不补造药水");
            boss.Clock.Advance(TimeSpan.FromMilliseconds(saved!.UntilMilliseconds - boss.Clock.GetUtcNow().ToUnixTimeMilliseconds()));
            That(!boss.Read().Inventory.BossSelectionBuff!.Active(boss.Clock.GetUtcNow().ToUnixTimeMilliseconds()), "每日06:00等号使旧药效失效");
            var expired = boss.Session.PlanBossSelectionProjection();
            That(expired is { RemovedBuffId: 300, BuffId: 0, Remaining: 0 } && expired.IsCurrent() &&
                boss.Session.PlanBossSelectionProjection() is null, "每日06:00仅移除原图标一次，不发零次487或续期");
        }
        using (var f = new Fixture(towns, characters))
        {
            That(f.Use().Outcome == ConsumableUseOutcome.Rejected, "未选角不消费药剂且返回原状态拒绝");
            f.Session.Select(0);
            That(f.Use().Outcome == ConsumableUseOutcome.Rejected, "选角未Ready不消费药剂");
            f.Session.MarkReady();
            var first = f.Use();
            That(first.Outcome == ConsumableUseOutcome.Committed && f.Read().Inventory.Find(new(InventoryListType.Main, 65))!.Core.Count == 2 &&
                Same(first.Inventory!.Receipt!.Character, f.Read()) && f.Session.CanPublishConsumable(first.Guard!),
                "实际消费提交后安装库存，冷却开始且回执为真实UID/revision/phase");
            That(f.Use().Outcome == ConsumableUseOutcome.CloseConnection && f.Count == 2,
                "原完整请求在成功冷却窗口重复不重发成功或失败ACK，不二次扣件");
            That(f.Session.MustHandleConsumableReplay(f.Request) && !f.Session.MustHandleConsumableReplay(f.Request with { OpaqueCheck = 7 }),
                "统一地点分发不得抢先应答原成功token的重放歧义，其他请求仍继续地点检查");
            That(f.Session.UseConsumable(f.Request, f.Service, activityBlocked: true).Outcome == ConsumableUseOutcome.CloseConnection &&
                f.Session.UseConsumable(f.Request with { OpaqueCheck = 7 }, f.Service, activityBlocked: true).Outcome == ConsumableUseOutcome.Rejected,
                "活动门禁不能解除已消费token歧义，不同请求仍按非法场景完整拒绝");
            That(f.Use(1).Outcome == ConsumableUseOutcome.Rejected && f.Count == 2, "同组新请求冷却拒绝不扣件");
            That(f.Session.UseConsumable(new(new(InventoryListType.Main, 66), 0, 1114, 0), f.Service).Outcome == ConsumableUseOutcome.Committed,
                "本服组0与组1独立，另一药组仍能合法消费");
            f.Clock.Advance(TimeSpan.FromMilliseconds(9999));
            That(f.Use(2).Outcome == ConsumableUseOutcome.Rejected && f.Count == 2, "本服10秒间隔未到不能提前消费");
            f.Clock.Advance(TimeSpan.FromMilliseconds(1));
            That(f.Use().Outcome == ConsumableUseOutcome.Committed && f.Count == 1, "间隔到期后opaque0可合法复用，不能永久去重");
            f.Session.ReturnToRoster(); f.Select();
            That(f.Use().Outcome == ConsumableUseOutcome.Committed && f.Count == 0, "换角重置单会话冷却，最后一件真实清槽");
            That(f.Use().Outcome == ConsumableUseOutcome.CloseConnection, "源槽已耗尽时仍识别刚成功原请求，不能盲回拒绝ACK");
            f.Session.ReturnToRoster(); f.Select();
            That(f.Use(4).Outcome == ConsumableUseOutcome.Rejected && f.Count == 0, "重选后空源合法拒绝，不补药或自动重做前次消费");
            using var replacement = f.Owners.Acquire(f.Owner.Account, () => { });
            Reject<InvalidOperationException>(() => f.Use(), "旧generation不能继续使用或发布");
            That(f.Repository.LoadCharacter(replacement, 0).Inventory.Find(new(InventoryListType.Main, 65)) is null,
                "重开持久库存保留消费至空且无旧owner写入");
        }
        using (var f = new Fixture(towns, characters))
        {
            f.Select(); var before = f.Read();
            f.Sql("CREATE TRIGGER potion_final_abort AFTER INSERT ON inventory_audit_log WHEN NEW.action_name='use_recovery_consumable' BEGIN SELECT RAISE(ABORT,'potion rollback'); END");
            Reject<Microsoft.Data.Sqlite.SqliteException>(() => f.Use(), "库存审计末段真实SQL失败");
            That(Same(before, f.Read()) && Same(before, f.Session.Selected!) && f.Count == 3,
                "失败回滚原UID/Core/数量/revision，未提前发布或推进冷却");
            f.Sql("DROP TRIGGER potion_final_abort");
            That(f.Use(3).Outcome == ConsumableUseOutcome.CloseConnection, "未知提交结果原会话不自动重试SQL或继续消费");
            f.Session.ReturnToRoster(); f.Select();
            That(f.Use(3).Outcome == ConsumableUseOutcome.Committed && f.Count == 2, "重新装载可从实际回滚存档继续新操作");
        }
        using (var f = new Fixture(towns, characters, lost: true))
        {
            f.Select(); var before = f.Session.Selected!;
            Reject<IOException>(() => f.Use(), "库存实际提交后返回结果丢失");
            That(f.Count == 2 && ReferenceEquals(before, f.Session.Selected) && f.Use().Outcome == ConsumableUseOutcome.CloseConnection,
                "已提交事实保留但不安装规划状态，不自动重做SQL或用旧快照继续");
            f.Session.ReturnToRoster(); f.Select();
            That(f.Count == 2 && f.Use(2).Outcome == ConsumableUseOutcome.Committed && f.Count == 1,
                "重选只读实际提交数量后新请求消费一次，不回放旧ACK");
        }
        using (var f = new Fixture(towns, characters))
        {
            f.Select(); var before = f.Read();
            var plan = f.Service.Plan(before, f.Request, f.Clock.GetUtcNow().ToUnixTimeSeconds()).Plan!;
            f.Session.SetCharacterGold(before.Name, 1);
            Reject<InvalidOperationException>(() => f.Repository.CommitInventory(f.Owner, before, plan), "旧角色revision不能提交药剂消费");
            That(f.Count == 3, "资格/版本冲突不扣源数量");
            var receipt = f.Use().Guard!;
            f.Session.ReturnToRoster(); f.Select();
            That(!f.Session.CanPublishConsumable(receipt), "同角色重选epoch使旧消费结果发布失效");
        }
        using (var f = new Fixture(towns, characters))
        {
            f.Select(); f.Use();
            var old = f.Read().Inventory.Find(f.Request.Position)!;
            f.Session.MutateInventory(character => new(true, 0, new(character.Inventory, character.Inventory with
            {
                Items = character.Inventory.Items.Replace(old, new(0, old.Position, old.Core with { Value = 4 }))
            }, "potion_fixture_replace")));
            var replaced = f.Read().Inventory.Find(f.Request.Position)!;
            That(replaced.Uid != old.Uid && f.Use().Outcome == ConsumableUseOutcome.CloseConnection && f.Count == 4,
                "同槽新UID不能解除旧完整请求歧义，冷却窗内无ACK且不消费替换物品");
            f.Clock.Advance(TimeSpan.FromSeconds(10));
            That(f.Use().Outcome == ConsumableUseOutcome.Committed && f.Count == 3, "窗口后按新源身份重新验证，同值token可合法使用");
        }
        using (var f = new Fixture(towns, characters))
        {
            f.Select();
            f.Session.MutateInventory(character => new(true, 0, new(character.Inventory, character.Inventory with
            {
                Items = character.Inventory.Items.Select(item => item.Position == f.Request.Position ? item with
                { Core = item.Core with { ExpireTime = 1900000002 } } : item).ToImmutableArray()
            }, "potion_fixture_expiry")));
            f.Clock.BeforeUtcRead = () => { if (f.Clock.Reads == 2) f.Clock.Advance(TimeSpan.FromSeconds(3)); };
            That(f.Use().Outcome == ConsumableUseOutcome.Rejected && f.Count == 3, "锁外规划后到期在最终提交前重验，不扣过期药");
        }
        using (var f = new Fixture(towns, characters))
        {
            f.Select(); f.Clock.BeforeUtcRead = () =>
            {
                if (f.Clock.Reads == 1) f.Session.UpdateProgress(new(CharacterProgressField.Level, f.Owner.Account, "Potion", 15, 0));
            };
            That(f.Use().Outcome == ConsumableUseOutcome.CloseConnection && f.Count == 3 && f.Read().Level == 15,
                "锁外规划中GM同revision替换等级快照时关闭原请求，不用旧资格消费");
        }
        using (var f = new Fixture(towns, characters))
        {
            var catalog = global::DfoServer.Testing.SelfTestCatalogs.Dungeons(archive); var rooms = global::DfoServer.Testing.SelfTestCatalogs.Rooms(archive);
            f.Select();
            That(f.Session.LeaveRoom() && f.Session.Travel(new(38, 2, 1085, 249, 5, 38, 0, 0)), "药剂房间guard用例经合法入口旅行");
            f.Session.BeginDungeonSelection();
            That(f.Session.EnterFormalDungeon(3, 0, 0, catalog, rooms) is not null && f.Session.CompleteFormalDungeonLoading() is not null,
                "药剂guard建立真实普通正式实例与房间，不借开发诊断状态");
            var result = f.Use();
            That(result.Outcome == ConsumableUseOutcome.Committed && result.Guard?.DungeonIdentity is not null,
                "正式实例消费回执携带原房间epoch");
            var old = result.Guard!;
            var forged = old with { DungeonIdentity = old.DungeonIdentity! with { Epoch = old.DungeonIdentity!.Epoch + 1 } };
            That(!f.Session.CanPublishConsumable(forged), "同角色快照和同phase仍拒绝另一房间epoch，不能仅按库存phase发布");
            f.Session.ReturnFromFormalDungeon(); f.Session.BeginDungeonSelection();
            That(f.Use().Outcome == ConsumableUseOutcome.CloseConnection &&
                f.Use(4).Outcome == ConsumableUseOutcome.Rejected && f.Count == 2,
                "成功后进入MapSelect不能让旧完整请求改发failureACK，新非法场景请求仍正常拒绝");
            That(!f.Session.CanPublishConsumable(old), "正式回城使旧实例消费回执失效");
        }
    }

    static void RunUsePlaces(TownCatalog towns, CharacterCatalog characters, PvfArchive archive)
    {
        foreach (int type in new[] { 4, 20 })
        {
            using var pvp = new Fixture(towns, characters, entry: new(new(2, 101), type, "[pvp]"),
                hp: ConsumableUseTests.Hp with { Places = [type == 20 ? "[tournament]" : "[pvp]"] });
            pvp.Select();
            var pvpRole = pvp.Session.Selected!;
            bool Allows(string place) => pvp.Session.CanUseStackablePlace(pvpRole, pvp.Session.SelectionEpoch, pvp.Session.Phase, [place]);
            That(pvp.Session.GetState() == UserState.PvpTown && Allows(type == 20 ? "[tournament]" : "[pvp]") &&
                !Allows(type == 20 ? "[pvp]" : "[tournament]") && !Allows("[village]") && !Allows("[dont open]"),
                "真实PvP会话以UserState和原频道区分普通决斗/竞技大赛，不借角色城镇位置放行：" + type);
            That(pvp.Use().Outcome == ConsumableUseOutcome.Committed && pvp.Count == 2,
                "统一地点放行后实际消费成功，不被下游城镇/副本两态判断再次阻止：" + type);
        }
        using var f = new Fixture(towns, characters, hp: ConsumableUseTests.Hp with
            { Places = ["[dungeon]", "[dont use exercise]", "[dont use assult]"] });
        f.Select();
        var roleInRoom = f.Session.Selected!;
        long epoch = f.Session.SelectionEpoch;
        That(f.Session.CanUseStackablePlace(roleInRoom, epoch, SessionPhase.Town, ["[seria room]"]) &&
            f.Use().Outcome == ConsumableUseOutcome.Rejected && f.Count == 3,
            "房间地点来自实际城镇目录，副本药的排除项不产生房间许可");
        That(f.Session.LeaveRoom() && !f.Session.CanUseStackablePlace(f.Session.Selected!, epoch,
            SessionPhase.Town, ["[seria room]"]) && f.Session.Travel(new(38, 2, 1085, 249, 5, 38, 0, 0)),
            "离开房间后仅房间许可随原UserState和真实位置失效");
        That(f.Session.BeginDungeonSelection() is not null &&
            !f.Session.CanUseStackablePlace(f.Session.Selected!, epoch, SessionPhase.MapSelect, ["[dungeon]"]),
            "选图UserState不是正在地下城中，不能仅凭非Town分支授予副本许可");
        That(f.Session.EnterFormalDungeon(3, 0, 0, global::DfoServer.Testing.SelfTestCatalogs.Dungeons(archive),
            global::DfoServer.Testing.SelfTestCatalogs.Rooms(archive), practice: true) is not null &&
            f.Session.CompleteFormalDungeonLoading() is not null, "地点回归通过正式入口建立练习实例");
        var role = f.Session.Selected!;
        string before = JsonSerializer.Serialize(f.Read());
        That(f.Session.GetState() == UserState.Dungeon &&
            f.Session.CanUseStackablePlace(role, epoch, SessionPhase.Dungeon, ["[dungeon]"]) &&
            !f.Session.CanUseStackablePlace(role, epoch, SessionPhase.Dungeon, ["[dungeon]", "[dont use exercise]"]) &&
            !f.Session.CanUseStackablePlace(role, epoch + 1, SessionPhase.Dungeon, ["[dungeon]"]) &&
            f.Use().Outcome == ConsumableUseOutcome.Rejected && JsonSerializer.Serialize(f.Read()) == before,
            "原练习实例触发通用排除，不扣源、不设冷却且旧epoch不能借地点许可使用");
        bool planned = false;
        InventoryResult Noop(Character current) { planned = true; return new(true, 0, new(current.Inventory, current.Inventory, "place_probe")); }
        That(!f.Session.PlanAndCommitBooster(Noop, BoosterCatalog.ParseUsePlaces(["[dungeon]", "[dont use exercise]"])).Accepted &&
            !planned && f.Session.PlanAndCommitBooster(Noop, BoosterCatalog.ParseUsePlaces(["[dungeon]"])).Accepted,
            "礼包和普通药共用练习排除，拒绝在规划前发生，普通副本许可仍保留");
        That(f.Session.UseConsumable(new(new(InventoryListType.Main, 66), 3, 1114, 0), f.Service).Outcome == ConsumableUseOutcome.Committed,
            "地点拒绝后同会话仍能使用无此排除的合法物品，不把整个练习实例禁用");
    }

    public static void RunQualificationExpiry(TownCatalog towns, CharacterCatalog characters, PvfArchive archive)
    {
        using var f = new Fixture(towns, characters);
        f.Select();
        f.Session.MutateInventory(character => new(true, 0, new(character.Inventory, character.Inventory with
        {
            Items = character.Inventory.Items.Select(item => item.Position == f.Request.Position ? item with
                { Core = item.Core with { ExpireTime = 1900000002 } } : item).ToImmutableArray()
        }, "potion_free_expiry_fixture")));
        That(f.Session.LeaveRoom() && f.Session.Travel(new(38, 2, 1085, 249, 5, 38, 0, 0)), "免费源期限回归经真实城镇入口旅行");
        f.Session.BeginDungeonSelection();
        That(f.Session.EnterFormalDungeon(3, 0, 0, global::DfoServer.Testing.SelfTestCatalogs.Dungeons(archive),
            global::DfoServer.Testing.SelfTestCatalogs.Rooms(archive), practice: true) is not null &&
            f.Session.CompleteFormalDungeonLoading() is not null, "免费源期限回归建立真实练习实例");
        string before = JsonSerializer.Serialize(f.Read());
        f.Clock.BeforeUtcRead = () => { if (f.Clock.Reads == 4) f.Clock.Advance(TimeSpan.FromSeconds(3)); };
        That(f.Use().Outcome == ConsumableUseOutcome.Rejected && JsonSerializer.Serialize(f.Read()) == before,
            "免费副本零变化计划在Repository只读复验才跨截止，正常拒绝且原库存和revision不变");
        f.Clock.BeforeUtcRead = null;
        f.Session.MutateInventory(character => new(true, 0, new(character.Inventory, character.Inventory with
        {
            Items = character.Inventory.Items.Select(item => item.Position == f.Request.Position ? item with
                { Core = item.Core with { ExpireTime = 0 } } : item).ToImmutableArray()
        }, "potion_free_expiry_fixture_restore")), requireTown: false);
        var retry = f.Use(8);
        That(retry.Outcome == ConsumableUseOutcome.Committed && retry.PreserveSource && f.Count == 3,
            "正常期限失败不写冷却或成功token，同连接修复合法源后可继续免费使用");
        InventoryResult Noop(Character character) => new(true, 0, new(character.Inventory, character.Inventory, "booster_scene_probe"));
        That(!f.Session.PlanAndCommitBooster(Noop, new(Village: true, SeriaRoom: true)).Accepted &&
            f.Session.PlanAndCommitBooster(Noop, new()).Accepted,
            "真实副本拒绝带城镇/房间并集声明的盒，而无声明保留既有副本合法地点");
    }

    static void RunDungeonEpicDropStates(TownCatalog towns, CharacterCatalog characters, PvfArchive archive)
    {
        var definitions = DungeonEpicDropBuffTests.Definitions();
        foreach (var (item,buff) in new[] { (10304461,280),(10305166,284) })
        {
            var definition = new RecoveryConsumableDefinition(item,1,60,-item) { DungeonEpicDropBuff=definitions[buff] };
            using var f=new Fixture(towns,characters,hp:definition,consumables:new([definition]));
            f.Select();
            That(f.Use().Outcome==ConsumableUseOutcome.Committed && f.Count==2 && f.Read().Inventory.ConsumableStates.IsEmpty &&
                f.Session.CurrentDungeonEpicBuffId==buff,"次数型药剂真实消费后仅安装当前角色内存资格，不造持久截止时间");
            var projection=f.Session.PlanDungeonEpicBuffProjection()!;
            That(projection.Added==buff && projection.IsCurrent(),"一次副本资格投影使用当前owner和选角身份");
            projection.MarkSent();
            That(f.Session.LeaveRoom() && f.Session.Travel(new(38,2,1085,249,5,38,0,0)),"次数型药剂沿实际城镇入口准备");
            Enter(practice:true);
            That(f.Session.CurrentDungeonEpicBuffId==buff && f.Session.ReturnFromFormalDungeon() is not null &&
                f.Session.CurrentDungeonEpicBuffId==buff,"练习不消耗已在城镇取得的一次资格，回城仍保留");
            Enter(practice:false);
            That(f.Session.CurrentDungeonEpicBuffId==buff && f.Session.ReturnFromFormalDungeon() is not null &&
                f.Session.CurrentDungeonEpicBuffId==0,"实际实例中资格生效，撤退结束后不延续到下一局");
            var removed=f.Session.PlanDungeonEpicBuffProjection()!;
            That(removed.Removed==buff && removed.Added==0 && removed.IsCurrent(),"回城通过原488移除本局次数Buff");
            removed.MarkSent();
            Enter(practice:false);
            That(f.Session.CurrentDungeonEpicBuffId==0 && f.Session.ReturnFromFormalDungeon() is not null,
                "同一副本再次进入不从已消耗的准备状态重新激活");
            f.Clock.Advance(TimeSpan.FromMilliseconds(61));
            That(f.Use(1).Outcome==ConsumableUseOutcome.Committed && f.Count==1,"新消费可以重新取得一次资格");
            f.Session.ReturnToRoster();f.Select();
            That(f.Session.CurrentDungeonEpicBuffId==0 && f.Count==1 && f.Read().Inventory.ConsumableStates.IsEmpty &&
                !projection.IsCurrent(),"无is-save声明的资格重选后清理，不补源或沿用旧投影");
            void Enter(bool practice)
            {
                f.Session.BeginDungeonSelection();
                That(f.Session.EnterFormalDungeon(3,0,0,global::DfoServer.Testing.SelfTestCatalogs.Dungeons(archive),
                    global::DfoServer.Testing.SelfTestCatalogs.Rooms(archive),practice:practice) is not null &&
                    f.Session.CompleteFormalDungeonLoading() is not null,"建立次数型药剂的真实普通/练习实例");
            }
        }
        var match=new RecoveryConsumableDefinition(10304461,1,60,-10304461) { DungeonEpicDropBuff=definitions[280] };
        var starry=new RecoveryConsumableDefinition(490703675,1,300000,253)
        { HellLegendEpicDropBuff=new(1271,6520) { ItemIds=ImmutableHashSet.Create(490703675) },
            EffectMilliseconds=300000,EffectMaintenance=7,CooltimeMaintenance=7 };
        foreach (bool rollback in new[] { false, true })
        {
            using var f=new Fixture(towns,characters,hp:match,consumables:new([match,starry]));
            f.Select();
            f.Session.MutateInventory(c=>new(true,0,new(c.Inventory,c.Inventory with
            {
                Items=[.. c.Inventory.Items.Where(item=>item.Position!=new InventorySlot(InventoryListType.Main,66)),
                    new(0,new(InventoryListType.Main,66),new(starry.ItemId,ItemKind.Consumable,3))],
                ConsumableStates=[new(ConsumableStateKind.Effect,starry.ItemId,f.Clock.GetUtcNow().ToUnixTimeMilliseconds()+300000,7,253)]
            },"starry_before_match_fixture")));
            var before=f.Read();
            if (rollback)
            {
                f.Sql("CREATE TRIGGER fail_match_replace AFTER DELETE ON character_consumable_states BEGIN SELECT RAISE(ABORT,'match rollback'); END");
                Reject<Microsoft.Data.Sqlite.SqliteException>(()=>f.Use(),"同组1271移除失败与新道具源消费一起回滚");
                That(Same(before,f.Read()) && f.Session.CurrentDungeonEpicBuffId==0,"SQL失败不能提前安装次数资格或清除旧星空账本");
            }
            else
            {
                That(f.Use().Outcome==ConsumableUseOutcome.Committed && f.Session.CurrentDungeonEpicBuffId==280 &&
                    f.Read().Inventory.ConsumableStates.IsEmpty,"280成功使用在同一库存事务内移除同组1271效果");
                var replaced=f.Session.UseConsumable(new(new(InventoryListType.Main,66),1,starry.ItemId,0),f.Service);
                That(replaced.Outcome==ConsumableUseOutcome.Committed && f.Session.CurrentDungeonEpicBuffId==0 &&
                    f.Session.HellLegendEpicDropPotionBonus(f.Read(),f.Clock.GetUtcNow())==6520,
                    "1271反向使用清除待入场次数资格，不能让同组星空与280两份倍率同时生效");
            }
        }
    }

    static void RunVariousAssociations(TownCatalog towns, CharacterCatalog characters, PvfArchive archive)
    {
        var choiceA = VariousBuffAssociationTests.Definition(91001);
        var choiceB = VariousBuffAssociationTests.Definition(91002);
        var random = new RecoveryConsumableDefinition(10008219, 1, 0, -10008219)
        {
            VariousBuff = VariousBuffAssociationTests.Binding(10008219, [choiceA, choiceB], random: true),
            VariousDurationMilliseconds = 600000, EffectMaintenance = 7
        };
        using (var f = new Fixture(towns, characters, hp: random, consumables: new([random])))
        {
            f.Select();
            That(f.Use().Outcome == ConsumableUseOutcome.Committed && f.Count == 2, "概率来源在真实库存事务消费原源一次");
            var chosen = f.Read().Inventory.VariousBuffs.Single();
            That(chosen.BuffId is 91001 or 91002 && chosen.UntilMilliseconds == f.Clock.GetUtcNow().ToUnixTimeMilliseconds() + 600000 &&
                f.Read().Inventory.ConsumableStates.IsEmpty, "实际抽选身份独立保存，不用普通STK恢复在重登时重新抽选");
            var initial = f.Session.PlanDungeonEpicBuffProjection()!;
            That(initial.AddedBuffs is [{ } display] && display.BuffId == chosen.BuffId && display.Value == chosen.UntilMilliseconds / 1000,
                "概率来源只投影本次已提交选择，不广播全部候选");
            initial.MarkSent();
            var before = f.Read();
            f.Clock.Advance(TimeSpan.FromSeconds(1));
            f.Sql("CREATE TRIGGER fail_various_buff BEFORE UPDATE ON character_various_buffs BEGIN SELECT RAISE(ABORT,'various rollback'); END");
            Reject<Microsoft.Data.Sqlite.SqliteException>(() => f.Use(1), "通用Buff写入失败必须回滚源与新抽选资格");
            That(Same(before, f.Read()) && f.Count == 2 && f.Session.PlanDungeonEpicBuffProjection() is null,
                "失败不发布新选择、不覆盖原期限或提前删除原客户端资格");
            f.Sql("DROP TRIGGER fail_various_buff");
            f.Session.ReturnToRoster(); f.Select();
            That(f.Read().Inventory.VariousBuffs.Single() == chosen && f.Count == 2 &&
                f.Session.PlanDungeonEpicBuffProjection(force: true)!.AddedBuffs.Single().BuffId == chosen.BuffId,
                "新选角恢复完全相同的抽选结果和截止，不补源或重抽");
            f.Clock.Advance(TimeSpan.FromMinutes(10));
            That(f.Session.AdvanceVariousBuffs() is { Accepted: true } && f.Read().Inventory.VariousBuffs.IsEmpty && f.Count == 2,
                "计时资格到点从真实保存状态回收，不再次消费物品");
        }
        var saved = VariousBuffAssociationTests.Definition(92001, "saved_count", "dungeon_one_match", 3, save: true);
        var transient = VariousBuffAssociationTests.Definition(92002, "session_count", "dungeon_one_match", 2, save: false);
        var multiple = new RecoveryConsumableDefinition(10304998, 1, 0, -10304998)
            { VariousBuff = VariousBuffAssociationTests.Binding(10304998, [saved, transient]) };
        using (var f = new Fixture(towns, characters, hp: multiple, consumables: new([multiple])))
        {
            f.Select();
            That(f.Use().Outcome == ConsumableUseOutcome.Committed && f.Read().Inventory.VariousBuffs is [{ BuffId: 92001, Remaining: 3 }],
                "多Buff整批授予，仅将声明保存的次数写入角色账本");
            var both = f.Session.PlanDungeonEpicBuffProjection()!;
            That(both.AddedBuffs.Select(row => (row.BuffId, row.Value)).SequenceEqual(new[] { (92001, 3), (92002, 2) }),
                "多Buff各自发布真实初始次数，不固定为一次");
            both.MarkSent();
            That(f.Session.LeaveRoom() && f.Session.Travel(new(38, 2, 1085, 249, 5, 38, 0, 0)), "次数资格经真实城镇路径准备");
            Enter(practice: true);
            That(f.Session.ReturnFromFormalDungeon() is not null && f.Read().Inventory.VariousBuffs.Single().Remaining == 3,
                "练习实例不消耗任何保存或非保存的次数");
            Enter(practice: false);
            That(f.Read().Inventory.VariousBuffs.Single().Remaining == 2 &&
                f.Session.PlanDungeonEpicBuffProjection(force: true)!.AddedBuffs.Select(row => row.Value).SequenceEqual(new[] { 3, 2 }),
                "入场同事务预扣下一局次数，当前实例仍持有本局有效资格");
            That(f.Session.ReturnFromFormalDungeon() is not null &&
                f.Session.PlanDungeonEpicBuffProjection(force: true)!.AddedBuffs.Select(row => row.Value).SequenceEqual(new[] { 2, 1 }),
                "离开实例后发布剩余次数，不重置或重复扣减");
            f.Session.ReturnToRoster(); f.Select();
            That(f.Read().Inventory.VariousBuffs is [{ BuffId: 92001, Remaining: 2 }] &&
                f.Session.PlanDungeonEpicBuffProjection(force: true)!.AddedBuffs is [{ BuffId: 92001, Value: 2 }],
                "重选仅恢复保存型剩余次数，非保存资格不重载");
            void Enter(bool practice)
            {
                f.Session.BeginDungeonSelection();
                That(f.Session.EnterFormalDungeon(3, 0, 0, global::DfoServer.Testing.SelfTestCatalogs.Dungeons(archive),
                    global::DfoServer.Testing.SelfTestCatalogs.Rooms(archive), practice: practice) is not null &&
                    f.Session.CompleteFormalDungeonLoading() is not null, "建立通用次数资格的真实副本实例");
            }
        }
    }

    static void RunCompositeDropStates(TownCatalog towns, CharacterCatalog characters, PvfArchive archive)
    {
        var count = DungeonEpicDropBuffTests.Definitions()[280];
        var other = count with { BuffId = 90002, SystemGroup = "other_drop_group",
            Resource = count.Resource! with { BuffId = 90002, SystemGroup = "other_drop_group" } };
        var timed = count with { BuffId = 90003, BossDropPercent = 100,
            Resource = count.Resource! with { BuffId = 90003, ApplyType = "expire_time", ApplyParameters = [600, 0] } };
        var countItem = new RecoveryConsumableDefinition(10304461, 1, 0, -10304461) { DungeonEpicDropBuff = count };
        var otherItem = new RecoveryConsumableDefinition(10305166, 1, 0, -10305166) { DungeonEpicDropBuff = other };
        var timedItem = new RecoveryConsumableDefinition(10304999, 1, 0, -10304999)
            { DungeonEpicDropBuff = timed, EffectMilliseconds = 600000, EffectMaintenance = 7 };
        var catalog = new RecoveryConsumableCatalog([countItem, otherItem, timedItem]);
        using var f = new Fixture(towns, characters, hp: countItem, consumables: catalog);
        f.Select();
        f.Session.MutateInventory(character => new(true, 0, new(character.Inventory, character.Inventory with
        {
            Items = character.Inventory.Items.Where(item => item.Position != new InventorySlot(InventoryListType.Main, 66))
                .Append(new(0, new(InventoryListType.Main, 66), new(otherItem.ItemId, ItemKind.Consumable, 3)))
                .Append(new(0, new(InventoryListType.Main, 67), new(timedItem.ItemId, ItemKind.Consumable, 3))).ToImmutableArray()
        }, "composite_drop_buff_fixture")));
        ConsumableUseResult Use(short slot, RecoveryConsumableDefinition definition) => f.Session.UseConsumable(
            new(new(InventoryListType.Main, slot), 0, definition.ItemId, 0), f.Service);
        That(f.Use().Outcome == ConsumableUseOutcome.Committed && Use(66, otherItem).Outcome == ConsumableUseOutcome.Committed,
            "两个不同资源组的次数Buff分别消费成功，不因共享模块互相覆盖");
        var both = f.Session.PlanDungeonEpicBuffProjection()!;
        That(both.AddedBuffs.Select(buff => buff.BuffId).SequenceEqual(new[] { 280, 90002 }) &&
            both.AddedBuffs.All(buff => buff.Value == 1) && f.Read().Inventory.ConsumableStates.IsEmpty,
            "不同组完整投影两项计数，不写伪造截止或覆盖其它组");
        both.MarkSent();
        That(Use(67, timedItem).Outcome == ConsumableUseOutcome.Committed &&
            f.Read().Inventory.VariousBuffs.Single().ItemId == timedItem.ItemId,
            "多效果计时Buff沿原库存事务写实际持续状态");
        var timeDisplay = f.Session.PlanDungeonEpicBuffProjection()!;
        That(timeDisplay.RemovedIds.SequenceEqual(new[] { 280, 90003 }) && timeDisplay.AddedBuffs is [{ BuffId: 90003 } display] &&
            display.Value == f.Clock.GetUtcNow().ToUnixTimeSeconds() + 600 && !both.IsCurrent(),
            "同组计时效果覆盖次数资格，仅移除原组编号，保留其它组并投影真实截止");
        timeDisplay.MarkSent();
        That(f.Use(1).Outcome == ConsumableUseOutcome.Committed && f.Read().Inventory.ConsumableStates.IsEmpty,
            "反向次数消费与清除同组计时账本共同提交，不清除其它组");
        var restored = f.Session.PlanDungeonEpicBuffProjection()!;
        That(restored.RemovedIds.SequenceEqual(new[] { 280, 90003 }) && restored.AddedBuffs is [{ BuffId: 280, Value: 1 }],
            "计时转次数只重投该组，不重新计时或重发其它组");
        restored.MarkSent();
        That(f.Session.LeaveRoom() && f.Session.Travel(new(38, 2, 1085, 249, 5, 38, 0, 0)), "组合Buff沿真实城镇入口准备入场");
        f.Session.BeginDungeonSelection();
        That(f.Session.EnterFormalDungeon(3, 0, 0, global::DfoServer.Testing.SelfTestCatalogs.Dungeons(archive),
            global::DfoServer.Testing.SelfTestCatalogs.Rooms(archive)) is not null && f.Session.CompleteFormalDungeonLoading() is not null,
            "同一正式实例接收全部适用的不同组资格");
        That(f.Session.PlanDungeonEpicBuffProjection(force: true)!.AddedBuffs.Length == 2 &&
            f.Session.ReturnFromFormalDungeon() is not null, "本局保留两组，实例结束统一清理");
        var removed = f.Session.PlanDungeonEpicBuffProjection()!;
        That(removed.RemovedIds.Order().SequenceEqual(new[] { 280, 90002 }) && removed.AddedBuffs.IsEmpty,
            "结束时移除两组原资格，不转移到下一个实例");
    }

    static void RunLanternEpicDropStates(TownCatalog towns, CharacterCatalog characters)
    {
        var family = ImmutableHashSet.Create(490005358, 490005359, 490000413);
        foreach (var (id, buff, percent) in new[] { (490005358, 1239, 10), (490005359, 1240, 5) })
        {
            var definition = new RecoveryConsumableDefinition(id, 1, 600000, 251)
            {
                EpicDropBuff = new(buff, percent) { ItemIds = family, HellRewardOnly = true }, EffectMilliseconds = 600000,
                EffectMaintenance = 15, CooltimeMaintenance = 7
            };
            using var f = new Fixture(towns, characters, hp: definition, consumables: new([definition]));
            f.Select();
            var used = f.Use();
            var saved = f.Read();
            long until = saved.Inventory.ConsumableStates.Single(row => row.Kind == ConsumableStateKind.Effect).UntilMilliseconds;
            That(used.Outcome == ConsumableUseOutcome.Committed && f.Count == 2 &&
                f.Session.EpicDropPotionBonus(saved, f.Clock.GetUtcNow()) == 0 &&
                f.Session.EpicDropPotionBonus(saved, f.Clock.GetUtcNow(), hellRewardOnly: true) == percent,
                "小灯笼真实消费只登记深渊专用倍率，不借普通史诗药入口扩大适用范围");
            var display = f.Session.PlanDungeonEpicBuffProjection()!;
            That(display.AddedBuffs is [{ BuffId: var displayedId, Value: var value }] && displayedId == buff && value == until / 1000 &&
                display.IsCurrent() && f.Session.NextVariousBuffWake(f.Clock.GetUtcNow()).ToUnixTimeMilliseconds() == until &&
                saved.Inventory.VariousBuffs.IsEmpty,
                "旧小灯笼账本发布原生Buff及真实截止，纳入到期唤醒但不复制药效资格");
            // 同一药效在发送完成前被命令尾部和周期刷新各观察一次，不能互相废弃写帧回执。
            var queuedAgain = f.Session.PlanDungeonEpicBuffProjection()!;
            That(display.IsCurrent() && queuedAgain.IsCurrent(),
                "相同未完成药效投影保留原写帧回执，未写成功时仍可重试");
            display.MarkSent();
            That(!queuedAgain.IsCurrent(), "首批药效图标写成功后，队列中的同状态投影不得再次发送");
            queuedAgain.MarkSent();
            That(f.Session.PlanDungeonEpicBuffProjection() is null,
                "相同小灯笼截止无需重复补图标，只有成功写帧才确认显示水位");
            f.Session.ReturnToRoster(); f.Select();
            That(f.Read().Inventory.ConsumableStates.Single(row => row.Kind == ConsumableStateKind.Effect).UntilMilliseconds == until &&
                f.Session.EpicDropPotionBonus(f.Session.Selected!, f.Clock.GetUtcNow(), hellRewardOnly: true) == percent &&
                !f.Session.CanPublishConsumable(used.Guard!), "重选恢复原小灯笼截止及倍率，旧投影失效且不续期");
            var restored = f.Session.PlanDungeonEpicBuffProjection()!;
            That(!display.IsCurrent() && restored.AddedBuffs is [{ BuffId: var restoredId, Value: var restoredUntil }] &&
                restoredId == buff && restoredUntil == until / 1000,
                "小灯笼重选恢复同一Buff身份和原截止，旧显示回执不能确认新角色状态");
            restored.MarkSent();
            f.Clock.Advance(TimeSpan.FromMinutes(10));
            That(f.Session.EpicDropPotionBonus(f.Session.Selected!, f.Clock.GetUtcNow(), hellRewardOnly: true) == 0,
                "小灯笼恰到期不再加成，不把Buff配置param10当成十秒或额外抽样次数");
            var expired = f.Session.PlanDungeonEpicBuffProjection()!;
            That(expired.RemovedIds.SequenceEqual(new[] { buff }) && expired.AddedBuffs.IsEmpty && expired.IsCurrent() &&
                f.Session.NextVariousBuffWake(f.Clock.GetUtcNow()) == DateTimeOffset.MaxValue,
                "旧计时药效到期沿488清除图标，过期账本不重复唤醒或补建永久图标");
        }
    }

    static void RunEpicDropStates(TownCatalog towns, CharacterCatalog characters, PvfArchive archive)
    {
        var effect = new EpicDropBuffDefinition(1136, 100) { ItemIds = ImmutableHashSet.Create(490000413, 490002458) };
        var definition = new RecoveryConsumableDefinition(490000413, 1, 600000, -490000413)
            { EpicDropBuff = effect, EffectMilliseconds = 600000, EffectMaintenance = 15, CooltimeMaintenance = 7 };
        var catalog = new RecoveryConsumableCatalog([definition, definition with { ItemId = 490002458 }, ConsumableUseTests.Mp]);
        using (var f = new Fixture(towns, characters, hp: definition, consumables: catalog))
        {
            f.Select();
            That(f.Session.EpicDropPotionBonus(f.Session.Selected!, f.Clock.GetUtcNow()) == 0,
                "未成功使用的固定史诗药剂不因持有物品产生掉率加成");
            var used = f.Use();
            var saved = f.Read();
            long until = saved.Inventory.ConsumableStates.Single(row => row.Kind == ConsumableStateKind.Effect).UntilMilliseconds;
            That(used.Outcome == ConsumableUseOutcome.Committed && f.Count == 2 &&
                f.Session.EpicDropPotionBonus(saved, f.Clock.GetUtcNow()) == 100 && f.Session.CanPublishConsumable(used.Guard!),
                "固定史诗在城镇正规消费后与持续状态同事务落库，实际掉落输入获得百分百加成");
            That(!f.Session.CanPublishConsumable(used.Guard! with { Generation = used.Guard!.Generation + 1 }) &&
                !f.Session.CanPublishConsumable(used.Guard! with { SelectionEpoch = used.Guard!.SelectionEpoch + 1 }),
                "固定药剂消费结果仍核对账号generation和选角epoch");
            f.Clock.Advance(TimeSpan.FromMilliseconds(1));
            f.Session.ReturnToRoster(); f.Select();
            That(!f.Session.CanPublishConsumable(used.Guard!) && f.Count == 2 &&
                f.Read().Inventory.ConsumableStates.Single(row => row.Kind == ConsumableStateKind.Effect).UntilMilliseconds == until &&
                f.Session.EpicDropPotionBonus(f.Session.Selected!, f.Clock.GetUtcNow()) == 100,
                "同角色重选仅恢复原药效截止，不续期或二次消费，旧消费投影失效");
            f.Clock.Advance(TimeSpan.FromMilliseconds(599998));
            var beforeExpiry = f.Read();
            That(f.Session.EpicDropPotionBonus(beforeExpiry, f.Clock.GetUtcNow()) == 100,
                "固定史诗截止前一毫秒仍参与真实掉落规划，不按UI秒数提前失效");
            f.Clock.Advance(TimeSpan.FromMilliseconds(1));
            That(f.Session.EpicDropPotionBonus(beforeExpiry, f.Clock.GetUtcNow()) == 0 && Same(beforeExpiry, f.Read()),
                "固定史诗恰到期不再加成，纯资格检查不改库、不补药或延长截止");
            var now = f.Clock.GetUtcNow();
            var contaminated = beforeExpiry with { Inventory = beforeExpiry.Inventory with { ConsumableStates = [
                new(ConsumableStateKind.Effect, 490000413, now.ToUnixTimeMilliseconds() + 1000, 15, 251),
                new(ConsumableStateKind.Effect, 490002458, now.ToUnixTimeMilliseconds() + 2000, 15, 252),
                new(ConsumableStateKind.Cooldown, 490000413, now.ToUnixTimeMilliseconds() + 999999, 7, 251),
                new(ConsumableStateKind.Effect, 490703675, now.ToUnixTimeMilliseconds() + 999999, 15, 253)] } };
            That(f.Session.EpicDropPotionBonus(contaminated, now) == 100 &&
                f.Session.EpicDropPotionBonus(contaminated, now.AddMilliseconds(1999)) == 100 &&
                f.Session.EpicDropPotionBonus(contaminated, now.AddMilliseconds(2000)) == 0,
                "旧存档多份同族取最大而不相加，未知星空效果和未到期冷却不能冒充掉率效果");
            using var replacement = f.Owners.Acquire(f.Owner.Account, () => { });
            Reject<InvalidOperationException>(() => f.Session.CanPublishConsumable(used.Guard!),
                "接管后旧generation不能发布固定药剂消费结果");
            That(f.Repository.LoadCharacter(replacement, 0).Inventory.Find(f.Request.Position)!.Core.Count == 2,
                "旧owner失效不重跑固定药剂消费或破坏已提交数量");
        }
        using (var f = new Fixture(towns, characters, hp: definition, consumables: catalog))
        {
            f.Select();
            That(f.Session.LeaveRoom() && f.Session.Travel(new(38, 2, 1085, 249, 5, 38, 0, 0)),
                "固定史诗城镇限制回归沿真实入口旅行");
            f.Session.BeginDungeonSelection();
            var before = f.Read();
            That(f.Use().Outcome == ConsumableUseOutcome.Rejected && Same(before, f.Read()),
                "选图阶段不能消费仅城镇固定史诗药剂");
            That(f.Session.EnterFormalDungeon(3, 0, 0, global::DfoServer.Testing.SelfTestCatalogs.Dungeons(archive),
                    global::DfoServer.Testing.SelfTestCatalogs.Rooms(archive)) is not null &&
                f.Session.CompleteFormalDungeonLoading() is not null, "固定史诗限制回归建立实际正式副本");
            before = f.Read();
            That(f.Use(1).Outcome == ConsumableUseOutcome.Rejected && Same(before, f.Read()) &&
                f.Session.EpicDropPotionBonus(f.Session.Selected!, f.Clock.GetUtcNow()) == 0,
                "副本里拒绝城镇药剂且不扣源、改revision或创建效果");
        }
        using (var f = new Fixture(towns, characters, hp: definition, consumables: catalog))
        {
            f.Select();
            var before = f.Read();
            f.Sql("CREATE TRIGGER fail_epic_potion AFTER INSERT ON character_consumable_states WHEN NEW.kind=0 BEGIN SELECT RAISE(ABORT,'epic potion rollback'); END");
            Reject<Microsoft.Data.Sqlite.SqliteException>(() => f.Use(), "固定史诗效果账本写入失败不先提交消耗");
            That(Same(before, f.Read()) && Same(before, f.Session.Selected!) && f.Count == 3 &&
                f.Session.EpicDropPotionBonus(f.Session.Selected!, f.Clock.GetUtcNow()) == 0,
                "固定史诗SQL失败回滚源、revision和药效，掉落输入没有未提交加成");
        }
    }

    static void RunHellLegendEpicDropStates(TownCatalog towns, CharacterCatalog characters, PvfArchive archive)
    {
        var hellEffect = new HellLegendEpicDropBuffDefinition(1271, 6520)
            { ItemIds = ImmutableHashSet.Create(490704393, 490703675) };
        var fixedEffect = new EpicDropBuffDefinition(1136, 100) { ItemIds = ImmutableHashSet.Create(490000413) };
        var definition = new RecoveryConsumableDefinition(490704393, 95, 300000, 253)
            { HellLegendEpicDropBuff = hellEffect, EffectMilliseconds = 300000, EffectMaintenance = 7, CooltimeMaintenance = 7 };
        var catalog = new RecoveryConsumableCatalog(
        [
            definition,
            definition with { ItemId = 490703675, MinimumLevel = 1 },
            new RecoveryConsumableDefinition(490000413, 1, 600000, -490000413)
                { EpicDropBuff = fixedEffect, EffectMilliseconds = 600000, EffectMaintenance = 15, CooltimeMaintenance = 7 },
            ConsumableUseTests.Mp
        ]);
        using (var f = new Fixture(towns, characters, hp: definition, consumables: catalog, level: 95))
        {
            f.Select();
            That(f.Session.PlanHellLegendBuffProjection() is null, "持有未使用的星空药剂不创建状态栏buff");
            That(f.Session.HellLegendEpicDropPotionBonus(f.Session.Selected!, f.Clock.GetUtcNow()) == 0 &&
                f.Session.EpicDropPotionBonus(f.Session.Selected!, f.Clock.GetUtcNow()) == 0,
                "未成功使用的星空药剂不因持有物品产生掉率加成");
            var used = f.Use();
            var saved = f.Read();
            long until = saved.Inventory.ConsumableStates.Single(row => row.Kind == ConsumableStateKind.Effect).UntilMilliseconds;
            var display = f.Session.PlanHellLegendBuffProjection();
            var retry = f.Session.PlanHellLegendBuffProjection();
            That(display is not null && retry is not null && !display.IsCurrent() && retry.IsCurrent(),
                "未写帧的星空显示允许重规划，旧回执不能确认新计划");
            display!.MarkSent();
            display = f.Session.PlanHellLegendBuffProjection();
            That(display is not null && !retry!.IsCurrent(), "旧版本确认不安装去重水位，同owner仍可重发");
            display!.MarkSent();
            That(display is not null && display.UntilSeconds == until / 1000 && display.IsCurrent() &&
                f.Session.PlanHellLegendBuffProjection() is null,
                "星空图标只取已提交截止秒，同阶段不重复发送完整时长");
            That(used.Outcome == ConsumableUseOutcome.Committed && f.Count == 2 &&
                f.Session.HellLegendEpicDropPotionBonus(saved, f.Clock.GetUtcNow()) == 6520 &&
                f.Session.EpicDropPotionBonus(saved, f.Clock.GetUtcNow()) == 0 && f.Session.CanPublishConsumable(used.Guard!),
                "星空药剂在城镇正规消费后与持续状态同事务落库，1136 掉落输入仍为零");
            f.Clock.Advance(TimeSpan.FromMilliseconds(1));
            f.Session.ReturnToRoster(); f.Select();
            var restoredDisplay = f.Session.PlanHellLegendBuffProjection(force: true);
            That(!display!.IsCurrent() && restoredDisplay is not null && restoredDisplay.UntilSeconds == until / 1000 &&
                restoredDisplay.IsCurrent(), "重选使旧显示回执失效，恢复原截止而不续期");
            restoredDisplay!.MarkSent();
            That(f.Count == 2 &&
                f.Read().Inventory.ConsumableStates.Single(row => row.Kind == ConsumableStateKind.Effect).UntilMilliseconds == until &&
                f.Session.HellLegendEpicDropPotionBonus(f.Session.Selected!, f.Clock.GetUtcNow()) == 6520,
                "同角色重选仅恢复原星空药效截止，不续期或二次消费");
            var now = f.Clock.GetUtcNow();
            var contaminated = f.Read() with { Inventory = f.Read().Inventory with { ConsumableStates = [
                new(ConsumableStateKind.Effect, 490704393, now.ToUnixTimeMilliseconds() + 1000, 7, 253),
                new(ConsumableStateKind.Effect, 490000413, now.ToUnixTimeMilliseconds() + 999999, 15, 251),
                new(ConsumableStateKind.Effect, 490703675, now.ToUnixTimeMilliseconds() + 2000, 7, 253)] } };
            That(f.Session.HellLegendEpicDropPotionBonus(contaminated, now) == 6520 &&
                f.Session.EpicDropPotionBonus(contaminated, now) == 100 &&
                f.Session.HellLegendEpicDropPotionBonus(contaminated, now.AddMilliseconds(2000)) == 0 &&
                f.Session.EpicDropPotionBonus(contaminated, now.AddMilliseconds(2000)) == 100,
                "星空同族取最大且不与固定1136相加或互相冒充");
            f.Clock.Advance(TimeSpan.FromMilliseconds(299999));
            var expiredDisplay = f.Session.PlanHellLegendBuffProjection();
            expiredDisplay?.MarkSent();
            That(!restoredDisplay!.IsCurrent() && expiredDisplay is { UntilSeconds: 0 } && expiredDisplay.IsCurrent() &&
                f.Session.PlanHellLegendBuffProjection() is null && f.Count == 2,
                "到期等号撤下星空状态，旧添加回执失效，不再次消费药剂");
        }
        using (var f = new Fixture(towns, characters, hp: definition, consumables: catalog, level: 95))
        {
            f.Select();
            That(f.Session.LeaveRoom() && f.Session.Travel(new(38, 2, 1085, 249, 5, 38, 0, 0)),
                "星空药剂城镇限制回归沿真实入口旅行");
            f.Session.BeginDungeonSelection();
            var before = f.Read();
            That(f.Use().Outcome == ConsumableUseOutcome.Rejected && Same(before, f.Read()),
                "选图阶段不能消费仅城镇星空药剂");
            That(f.Session.EnterFormalDungeon(3, 0, 0, global::DfoServer.Testing.SelfTestCatalogs.Dungeons(archive),
                    global::DfoServer.Testing.SelfTestCatalogs.Rooms(archive)) is not null &&
                f.Session.CompleteFormalDungeonLoading() is not null, "星空药剂限制回归建立实际正式副本");
            before = f.Read();
            That(f.Use(1).Outcome == ConsumableUseOutcome.Rejected && Same(before, f.Read()) &&
                f.Session.HellLegendEpicDropPotionBonus(f.Session.Selected!, f.Clock.GetUtcNow()) == 0,
                "副本里拒绝星空城镇药剂且不扣源、改revision或创建效果");
        }
        using (var f = new Fixture(towns, characters, hp: definition, consumables: catalog, level: 95))
        {
            f.Select();
            var before = f.Read();
            f.Sql("CREATE TRIGGER fail_hell_legend_potion AFTER INSERT ON character_consumable_states WHEN NEW.kind=0 BEGIN SELECT RAISE(ABORT,'hell legend potion rollback'); END");
            Reject<Microsoft.Data.Sqlite.SqliteException>(() => f.Use(), "星空效果账本写入失败不先提交消耗");
            That(Same(before, f.Read()) && Same(before, f.Session.Selected!) && f.Count == 3 &&
                f.Session.HellLegendEpicDropPotionBonus(f.Session.Selected!, f.Clock.GetUtcNow()) == 0,
                "星空药剂SQL失败回滚源、revision和药效");
        }
    }

    static void RunVillageMoveSpeedStates(TownCatalog towns, CharacterCatalog characters, PvfArchive archive)
    {
        var speed = new VillageMoveSpeedBuffDefinition(1244, 20)
            { ItemIds = ImmutableHashSet.Create(490006630, 10306776) };
        var definition = new RecoveryConsumableDefinition(490006630, 1, 300000, 11)
            { VillageMoveSpeedBuff = speed, EffectMilliseconds = 300000, EffectMaintenance = 7, CooltimeMaintenance = 7 };
        var catalog = new RecoveryConsumableCatalog(
        [
            definition,
            definition with { ItemId = 10306776, CoolTime = 1200000, CoolGroup = -10306776, EffectMilliseconds = 1200000,
                VillageMoveSpeedBuff = speed },
            ConsumableUseTests.Mp
        ]);
        using (var f = new Fixture(towns, characters, hp: definition, consumables: catalog))
        {
            f.Select();
            var used = f.Use();
            var saved = f.Read();
            long until = saved.Inventory.ConsumableStates.Single(row => row.Kind == ConsumableStateKind.Effect).UntilMilliseconds;
            That(used.Outcome == ConsumableUseOutcome.Committed && f.Count == 2 &&
                saved.Inventory.ConsumableStates.Single(row => row.Kind == ConsumableStateKind.Effect) is
                    { ItemId: 490006630, Maintenance: 7 } && f.Session.CanPublishConsumable(used.Guard!),
                "城镇移速药剂在城镇正规消费后与持续状态同事务落库，走速仍由客户端启动");
            That(f.Session.PlanDungeonEpicBuffProjection()!.AddedBuffs is [{ BuffId: 1244, Value: var speedUntil }] &&
                speedUntil == until / 1000,
                "同路径的旧城镇移速药剂也以原生487启用客户端效果，不只保存服务端计时");
            f.Clock.Advance(TimeSpan.FromMilliseconds(1));
            f.Session.ReturnToRoster(); f.Select();
            That(f.Count == 2 &&
                f.Read().Inventory.ConsumableStates.Single(row => row.Kind == ConsumableStateKind.Effect).UntilMilliseconds == until,
                "同角色重选仅恢复原城镇移速截止，不续期或二次消费");
        }
        using (var f = new Fixture(towns, characters, hp: definition, consumables: catalog))
        {
            f.Select();
            That(f.Session.LeaveRoom() && f.Session.Travel(new(38, 2, 1085, 249, 5, 38, 0, 0)),
                "城镇移速限制回归沿真实入口旅行");
            f.Session.BeginDungeonSelection();
            var before = f.Read();
            That(f.Use().Outcome == ConsumableUseOutcome.Rejected && Same(before, f.Read()),
                "选图阶段不能消费仅城镇移速药剂");
            That(f.Session.EnterFormalDungeon(3, 0, 0, global::DfoServer.Testing.SelfTestCatalogs.Dungeons(archive),
                    global::DfoServer.Testing.SelfTestCatalogs.Rooms(archive)) is not null &&
                f.Session.CompleteFormalDungeonLoading() is not null, "城镇移速限制回归建立实际正式副本");
            before = f.Read();
            That(f.Use(1).Outcome == ConsumableUseOutcome.Rejected && Same(before, f.Read()),
                "副本里拒绝城镇移速药剂且不扣源、改revision或创建效果");
        }
        using (var f = new Fixture(towns, characters, hp: definition, consumables: catalog))
        {
            f.Select();
            var before = f.Read();
            f.Sql("CREATE TRIGGER fail_village_speed_potion AFTER INSERT ON character_consumable_states WHEN NEW.kind=0 BEGIN SELECT RAISE(ABORT,'village speed potion rollback'); END");
            Reject<Microsoft.Data.Sqlite.SqliteException>(() => f.Use(), "城镇移速效果账本写入失败不先提交消耗");
            That(Same(before, f.Read()) && Same(before, f.Session.Selected!) && f.Count == 3,
                "城镇移速SQL失败回滚源、revision和药效");
        }
    }

    static void RunStaminaVariant(TownCatalog towns, CharacterCatalog characters, PvfArchive archive)
    {
        var definition = new RecoveryConsumableDefinition(490003621, 1, 600000, -490003621)
            { EffectMilliseconds = 600000, EffectMaintenance = 7, CooltimeMaintenance = 7 };
        using var f = new Fixture(towns, characters, hp: definition, level: 40);
        f.Select();
        f.Session.MutateInventory(character => new(true, 0, new(character.Inventory,
            character.Inventory with { StaminaUntil = f.Clock.GetUtcNow().ToUnixTimeSeconds() + 600 }, "variant_weakness_fixture")));
        var used = f.Use();
        long until = f.Clock.GetUtcNow().ToUnixTimeMilliseconds() + 600000;
        That(used.Outcome == ConsumableUseOutcome.Committed && f.Count == 2 && f.Read().Inventory.StaminaUntil == 0 &&
            f.Read().Inventory.ConsumableStates.Single(row => row.Kind == ConsumableStateKind.Effect) is
                { ItemId: 490003621, Maintenance: 7 } state && state.UntilMilliseconds == until,
            "十分钟活动防虚弱真实消费立即解除既有虚弱，与自身十分钟截止原子落库");
        That(f.Session.LeaveRoom() && f.Session.Travel(new(38, 2, 1085, 249, 5, 38, 0, 0)),
            "活动防虚弱用例沿真实副本入口旅行");
        EnterAndReturn();
        That(f.Read().Inventory.StaminaUntil == 0 && f.Count == 2,
            "活动防虚弱实际正式副本撤退仍免虚弱，不仅在使用瞬间恢复一次");
        f.Clock.Advance(TimeSpan.FromMilliseconds(600000));
        EnterAndReturn();
        That(f.Read().Inventory.StaminaUntil > f.Clock.GetUtcNow().ToUnixTimeSeconds() && f.Count == 2,
            "活动药剂恰满自身十分钟后真实撤退恢复虚弱惩罚，不沿用标准三十分钟期限或再扣药");

        void EnterAndReturn()
        {
            f.Session.BeginDungeonSelection();
            That(f.Session.EnterFormalDungeon(3, 0, 0, global::DfoServer.Testing.SelfTestCatalogs.Dungeons(archive),
                    global::DfoServer.Testing.SelfTestCatalogs.Rooms(archive)) is not null &&
                f.Session.CompleteFormalDungeonLoading() is not null && f.Session.ReturnFromFormalDungeon() is not null,
                "活动防虚弱用例真实入图完成加载再撤退，不直接调用虚弱纯函数");
        }
    }

    static void RunDungeonRestrictions(TownCatalog towns, CharacterCatalog characters, PvfArchive archive)
    {
        foreach (bool inDungeon in new[] { false, true })
        foreach (bool allowed in new[] { false, true })
        using (var f = new Fixture(towns, characters, hp: ConsumableUseTests.Hp with
            { ItemId = 10312384, Jobs = 1 << 3, AllowedDungeons = ImmutableHashSet.Create(allowed ? 3U : 5350U) }))
        {
            f.Select();
            if (inDungeon)
            {
                That(f.Session.LeaveRoom() && f.Session.Travel(new(38, 2, 1085, 249, 5, 38, 0, 0)),
                    "药剂资格回归沿真实入口旅行");
                f.Session.BeginDungeonSelection();
                var before = f.Read();
                That(f.Use().Outcome == ConsumableUseOutcome.Rejected && Same(before, f.Read()) && f.Count == 3,
                    "选图未就绪仍完整拒绝，资格委派不取消实际业务阶段门禁");
                That(f.Session.EnterFormalDungeon(3, 0, 0, global::DfoServer.Testing.SelfTestCatalogs.Dungeons(archive),
                    global::DfoServer.Testing.SelfTestCatalogs.Rooms(archive)) is not null &&
                    f.Session.CompleteFormalDungeonLoading() is not null, "药剂资格建立真实正式副本");
            }
            var result = f.Use();
            That(result.Outcome == ConsumableUseOutcome.Committed && f.Count == 2 && f.Session.CanPublishConsumable(result.Guard!),
                "职业及模板副本匹配交原生客户端，城镇或真实副本均复用同一消费事务");
            var committed = f.Read();
            That(f.Use(1).Outcome == ConsumableUseOutcome.Rejected && Same(committed, f.Read()) && f.Count == 2,
                "资格委派仍保留原冷却组，不重复扣源或刷新状态");
        }
    }

    static void RunTimedStates(TownCatalog towns, CharacterCatalog characters, PvfArchive archive)
    {
        var definition = new RecoveryConsumableDefinition(10087825, 1, 1800000, 5)
            { EffectMilliseconds = 1800000, EffectMaintenance = 7, CooltimeMaintenance = 7 };
        using (var f = new Fixture(towns, characters, hp: definition))
        {
            f.Select();
            f.Session.MutateInventory(c => new(true, 0, new(c.Inventory, c.Inventory with
                { StaminaUntil = f.Clock.GetUtcNow().ToUnixTimeSeconds() + 600 }, "potion_weakness_fixture")));
            var used = f.Use();
            That(used.Outcome == ConsumableUseOutcome.Committed && f.Count == 2 && f.Read().Inventory.StaminaUntil == 0 &&
                f.Read().Inventory.ConsumableStates.Length == 2 && Same(f.Read(), f.Session.Selected!),
                "扣源、效果/冷却账本与虚弱恢复原子落库，在线状态安装真实提交结果");
            var states = f.Read().Inventory.ConsumableStates;
            f.Session.SetCharacterGold("Potion", 123);
            f.Session.ReturnToRoster(); f.Select();
            That(f.Read().Inventory.ConsumableStates.SequenceEqual(states) && f.Read().Inventory.Gold == 123 &&
                f.Use(1).Outcome == ConsumableUseOutcome.Rejected && f.Count == 2,
                "无关库存提交保留药效，重选不续期且不能绕过持久冷却再次消费");
            f.Clock.Advance(TimeSpan.FromMilliseconds(1799999));
            That(f.Use(2).Outcome == ConsumableUseOutcome.Rejected, "冷却到期前一毫秒仍拒绝");
            f.Clock.Advance(TimeSpan.FromMilliseconds(1));
            That(f.Use(3).Outcome == ConsumableUseOutcome.Committed && f.Count == 1 && f.Read().Inventory.ConsumableStates.Length == 2,
                "冷却恰到期可重新使用，替换同ID状态而不堆积历史行");
            f.Sql("PRAGMA foreign_keys=OFF; UPDATE character_consumable_states SET account='another-account' WHERE kind=0");
            Reject<InvalidDataException>(() => f.Read(), "存量持续状态owner损坏不能作为当前角色药效装载");
            f.Sql("PRAGMA foreign_keys=OFF; UPDATE character_consumable_states SET account='potion-session'");
        }
        using (var f = new Fixture(towns, characters, hp: definition))
        {
            f.Select();
            f.Session.MutateInventory(c => new(true, 0, new(c.Inventory, c.Inventory with
                { StaminaUntil = f.Clock.GetUtcNow().ToUnixTimeSeconds()+600 }, "potion_rollback_weakness_fixture")));
            var before = f.Read();
            f.Sql("CREATE TRIGGER effect_abort AFTER INSERT ON character_consumable_states BEGIN SELECT RAISE(ABORT,'effect rollback'); END");
            Reject<Microsoft.Data.Sqlite.SqliteException>(() => f.Use(), "持续效果写入阶段真实SQL失败");
            That(Same(before, f.Read()) && Same(before, f.Session.Selected!) && f.Count == 3,
                "晚期效果写入失败回滚先前扣物、虚弱、revision和审计，不发布成功或推进冷却");
        }
        foreach (var freeDefinition in new[] { definition, new RecoveryConsumableDefinition(2600020,1,1200000,-2600020)
            { EffectMilliseconds=1200000,RevivalBlessing=true,RequiresDungeon=true } })
        using (var f = new Fixture(towns, characters, hp: freeDefinition))
        {
            f.Select();
            That(f.Session.LeaveRoom() && f.Session.Travel(new(38, 2, 1085, 249, 5, 38, 0, 0)), "限时药效免耗回归经真实城镇入口");
            f.Session.BeginDungeonSelection();
            That(f.Session.EnterFormalDungeon(3, 0, 0, global::DfoServer.Testing.SelfTestCatalogs.Dungeons(archive),
                global::DfoServer.Testing.SelfTestCatalogs.Rooms(archive), practice: true) is not null &&
                f.Session.CompleteFormalDungeonLoading() is not null, "限时药效建立真实练习实例");
            That(f.Use().Outcome == ConsumableUseOutcome.Committed && f.Count == 3 && f.Read().Inventory.ConsumableStates.IsEmpty,
                "练习使用仅本地生效，既不扣药也不把免费效果或冷却持久化带出副本");
            if (freeDefinition.RevivalBlessing)
            {
                var timeout = f.Session.CurrentRevivalBlessingTimeout()!;
                That(f.Session.ReportFormalCharacterDeath() is { AutomaticRevival: true } && f.Count == 3 &&
                    !f.Session.CanPublishRevivalBlessingTimeout(timeout, timeout.UntilMilliseconds) &&
                    f.Session.ReportFormalCharacterDeath() is { AutomaticRevival: false },
                    "免费神圣祝福保存在原实例内，实际免币复活一次后清除，不与持久库存建立平行快照");
            }
            var returned = f.Session.ReturnFromFormalDungeon();
            That(returned is not null && f.Read().Inventory.ConsumableStates.IsEmpty &&
                (freeDefinition.RevivalBlessing ? returned.ConsumableCooldowns.IsEmpty :
                    returned.ConsumableCooldowns is [{ RemainingSeconds: 0 }]),
                "离开免费玩法销毁原实例效果，不保存或带出免费资格");
            if (!freeDefinition.RevivalBlessing)
                That(f.Use(1).Outcome == ConsumableUseOutcome.Committed && f.Count == 2,
                    "免费实例的临时冷却不封锁出图后新的普通有偿使用，旧完整token仍单独保护");
        }
        using (var f = new Fixture(towns, characters, hp: definition with { CoolTime = 10000 }))
        {
            f.Select();
            That(f.Use().Outcome == ConsumableUseOutcome.Committed, "普通模式先建立真实已扣源药效与冷却");
            var real = f.Read();
            That(f.Session.LeaveRoom() && f.Session.Travel(new(38,2,1085,249,5,38,0,0)), "已有普通药效仍可前往练习入口");
            f.Session.BeginDungeonSelection();
            That(f.Session.EnterFormalDungeon(3,0,0,global::DfoServer.Testing.SelfTestCatalogs.Dungeons(archive),
                global::DfoServer.Testing.SelfTestCatalogs.Rooms(archive),practice:true) is not null &&
                f.Session.CompleteFormalDungeonLoading() is not null, "建立独立练习冷却的真实实例");
            That(f.Use(1).Outcome == ConsumableUseOutcome.Committed && f.Count == 2 &&
                f.Read().Inventory.ConsumableStates.SequenceEqual(real.Inventory.ConsumableStates),
                "普通冷却不封锁免费练习，同组练习使用也不改真实药效及截止");
            That(f.Use(2).Outcome == ConsumableUseOutcome.Rejected, "练习自身冷却在原实例内仍然执行");
            f.Clock.Advance(TimeSpan.FromSeconds(10));
            var repeated = f.Use(3);
            That(repeated.Outcome == ConsumableUseOutcome.Committed && repeated.EffectUpdates is [{ RemainingSeconds: 1800 }],
                "练习冷却到期后合法续药，按新截止刷新客户端剩余时间");
            var returned = f.Session.ReturnFromFormalDungeon()!;
            That(returned.ConsumableCooldowns is [{ RemainingSeconds: 0 }] &&
                returned.ConsumableEffects.Any(row => row.ItemId == definition.ItemId && row.RemainingSeconds == 1790) &&
                f.Read().Inventory.ConsumableStates.SequenceEqual(real.Inventory.ConsumableStates.Where(row => row.Active(f.Clock.GetUtcNow().ToUnixTimeMilliseconds()))),
                "退出练习只结束临时计时并恢复普通药效原剩余时间，不把免费续时保存到普通模式");
            var paid = f.Use(4);
            That(paid.Outcome == ConsumableUseOutcome.Committed && f.Count == 1 && paid.EffectUpdates.IsEmpty &&
                f.Read().Inventory.ConsumableStates.Single(row=>row.Kind==ConsumableStateKind.Effect).UntilMilliseconds==
                    f.Clock.GetUtcNow().ToUnixTimeMilliseconds()+1800000,
                "普通冷却自然到期即可真实消费，原练习冷却不能封锁它");
        }
        using (var f = new Fixture(towns, characters, hp: definition with { CoolTime = 10000, EffectMaintenance = 6, CooltimeMaintenance = 6 }))
        {
            f.Select();
            That(f.Use().Outcome == ConsumableUseOutcome.Committed,"免费死亡隔离先保存普通计时");
            var real = f.Read().Inventory.ConsumableStates;
            That(f.Session.LeaveRoom() && f.Session.Travel(new(38,2,1085,249,5,38,0,0)),"免费死亡隔离走实际入口");
            f.Session.BeginDungeonSelection();
            That(f.Session.EnterFormalDungeon(3,0,0,global::DfoServer.Testing.SelfTestCatalogs.Dungeons(archive),
                global::DfoServer.Testing.SelfTestCatalogs.Rooms(archive),practice:true) is not null &&
                f.Session.CompleteFormalDungeonLoading() is not null && f.Use(1).Outcome == ConsumableUseOutcome.Committed,
                "死亡前建立只属于本练习的同组计时");
            That(f.Session.ReportFormalCharacterDeath() is not null && f.Read().Inventory.ConsumableStates.SequenceEqual(real),
                "练习死亡不消除普通冷却或药效，只按维护位结束免费实例状态");
            var returned = f.Session.ReturnFromFormalDungeon()!;
            That(returned.ConsumableCooldowns is [{ RemainingSeconds: 10 }] &&
                returned.ConsumableEffects is [{ RemainingSeconds: 1800 }] && f.Use(2).Outcome == ConsumableUseOutcome.Rejected,
                "免费状态已因死亡消失仍恢复被临时覆盖的普通计时，不能漏发或绕过真实冷却");
        }
    }

    public static void RunDisguiseInventoryLifetime(TownCatalog towns, CharacterCatalog characters,
        RecoveryConsumableCatalog consumables)
    {
        DisguiseSourceQualifications(towns,characters,consumables);
        const int itemId = 490013028, formId = 56673;
        That(consumables.TryGet(itemId, out var definition) && definition is
            { Unlimited: true, Disguise: { Type: 2, Seconds: 300 } },
            "变身跨库存回归读取正式028的免耗九形态与300秒定义");
        using var f = new Fixture(towns, characters, hp: definition, consumables: consumables, level: 95);
        f.Select();
        var source = f.Session.Selected!.Inventory.Find(f.Request.Position)!;
        uint originalGold = f.Session.Selected.Inventory.Gold;
        long started = f.Clock.GetUtcNow().ToUnixTimeMilliseconds();
        var activated = f.Session.ApplyUnlimitedDisguise(2, formId, itemId, f.Service);
        That(activated is not null && f.Session.CurrentConsumableDisguise() is not null,
            "真实028持有源经直接211用例激活，不预发44或伪造在线Disguise");
        var original = f.Session.CurrentConsumableDisguise()!.Value;
        That(original.Appearance is { Type: 2, FormId: formId, SourceItemId: itemId } &&
            original.Appearance.UntilMilliseconds == started + 300000,
            "028原始身份、来源和截止由现有变身状态持有");

        f.Clock.Advance(TimeSpan.FromSeconds(60));
        var committed = f.Session.PlanAndCommitInventory(character => new(true, 0,
            new(character.Inventory, character.Inventory with { Gold = character.Inventory.Gold + 1 },
                "disguise_inventory_lifetime_probe", new(character))));
        That(committed.Accepted && committed.Receipt is not null && f.Read().Inventory.Gold == originalGold + 1,
            "普通库存规划真实提交并安装新的角色快照");
        That(f.Session.CurrentConsumableDisguise() is { } retained && retained.Epoch == original.Epoch &&
            ReferenceEquals(retained.Appearance, original.Appearance) &&
            ReferenceEquals(f.Session.Selected!.Disguise, original.Appearance) &&
            original.Appearance.UntilMilliseconds == started + 300000 &&
            f.Session.Selected.Inventory.Find(f.Request.Position) == source,
            "普通库存提交保留exact变身对象、身份和原截止，不消耗或替换028来源");
        That(f.Read().Disguise is null && !JsonSerializer.Serialize(f.Session.Selected).Contains("\"Disguise\"", StringComparison.Ordinal),
            "保留的是当前选角临时状态，不写库或序列化为重登效果");

        f.Clock.Advance(TimeSpan.FromMilliseconds(239999));
        That(f.Session.RefreshConsumableDisguise(f.Clock.GetUtcNow().ToUnixTimeMilliseconds()) is null &&
            f.Session.CurrentConsumableDisguise() == original,
            "跨库存提交后仍在原300秒截止前一毫秒保持同一变身");
        f.Clock.Advance(TimeSpan.FromMilliseconds(1));
        That(f.Session.RefreshConsumableDisguise(f.Clock.GetUtcNow().ToUnixTimeMilliseconds()) is { Character.Disguise: null } &&
            f.Session.CurrentConsumableDisguise() is null &&
            f.Session.RefreshConsumableDisguise(f.Clock.GetUtcNow().ToUnixTimeMilliseconds()) is null,
            "跨库存提交不续期，原到期刷新在300秒等号只清理一次");
    }

    static void RunRevivalTimeouts(TownCatalog towns, CharacterCatalog characters, PvfArchive archive)
    {
        using var f = new Fixture(towns, characters, hp: new(2600020, 1, 1200000, -2600020)
            { EffectMilliseconds = 1200000, RevivalBlessing = true, RequiresDungeon = true });
        f.Select();
        That(f.Session.LeaveRoom() && f.Session.Travel(new(38, 2, 1085, 249, 5, 38, 0, 0)), "祝福计时回归经真实城镇入口");
        Enter();
        That(f.Use().Outcome == ConsumableUseOutcome.Committed, "祝福计时绑定成功使用，不为失败消费建立定时资格");
        var first = f.Session.CurrentRevivalBlessingTimeout()!;
        f.Session.SetCharacterGold("Potion", 123);
        f.Clock.Advance(TimeSpan.FromMilliseconds(1199999));
        That(!f.Session.CanPublishRevivalBlessingTimeout(first, f.Clock.GetUtcNow().ToUnixTimeMilliseconds()),
            "祝福到期前一毫秒不能发送清理通知，普通库存revision不重置截止");
        var before = f.Read();
        f.Clock.Advance(TimeSpan.FromMilliseconds(1));
        That(f.Session.CanPublishRevivalBlessingTimeout(first, f.Clock.GetUtcNow().ToUnixTimeMilliseconds()) && Same(before, f.Read()),
            "原生到期通知仅清理客户端效果，不重复扣药、不扣复活币或改写角色资产");
        That(f.Use(1).Outcome == ConsumableUseOutcome.Committed && f.Count == 1, "冷却到期后再次喝药替换原截止");
        var second = f.Session.CurrentRevivalBlessingTimeout()!;
        That(second.UntilMilliseconds == first.UntilMilliseconds + 1200000 &&
            !f.Session.CanPublishRevivalBlessingTimeout(first, second.UntilMilliseconds),
            "旧到期回调即便延迟执行也不能清除续药后的新资格");
        That(f.Session.ReturnFromFormalDungeon() is not null && f.Session.CurrentRevivalBlessingTimeout() is null &&
            !f.Session.CanPublishRevivalBlessingTimeout(second, second.UntilMilliseconds),
            "回城清除祝福后原定时通知失效，不发送到城镇或下一副本");
        Enter();
        That(f.Use(2).Outcome == ConsumableUseOutcome.Committed, "新实例可合法建立新的祝福资格");
        var third = f.Session.CurrentRevivalBlessingTimeout()!;
        That(!f.Session.CanPublishRevivalBlessingTimeout(second, third.UntilMilliseconds), "同角色新实例拒绝旧实例到期回调");
        f.Session.ReturnToRoster(); f.Select();
        That(f.Session.CurrentRevivalBlessingTimeout() is null &&
            !f.Session.CanPublishRevivalBlessingTimeout(third, third.UntilMilliseconds),
            "同连接重选同角色仍由选角epoch隔离，不恢复旧祝福或发送旧清理包");
        using var replacement = f.Owners.Acquire(f.Owner.Account, () => { });
        Reject<InvalidOperationException>(() => f.Session.CanPublishRevivalBlessingTimeout(third, third.UntilMilliseconds),
            "旧generation不能为接管后的新连接发布祝福到期通知");

        void Enter()
        {
            f.Session.BeginDungeonSelection();
            That(f.Session.EnterFormalDungeon(3, 0, 0, global::DfoServer.Testing.SelfTestCatalogs.Dungeons(archive),
                global::DfoServer.Testing.SelfTestCatalogs.Rooms(archive)) is not null &&
                f.Session.CompleteFormalDungeonLoading() is not null, "祝福计时建立真实普通正式副本");
        }
    }

    public static void RunPortableCargo(TownCatalog towns, CharacterCatalog characters, PvfArchive archive,
        DfoServer.Infrastructure.ItemResources resources)
    {
        foreach (int id in new[] { 10, 51, 2660234 })
        {
            That(resources.RecoveryConsumables.TryGet(id, out var owl), "猫头鹰资源可经现有CMD44解析 " + id);
            using var f = new Fixture(towns, characters, hp: owl, consumables: resources.RecoveryConsumables);
            f.Select();
            var inventory = new InventoryService(resources.Items, resources.GoldLimits, resources.EpicPieces, clock: f.Clock);
            That(f.Session.MutateInventory(c => new(true, 0, new(c.Inventory, c.Inventory with
                { AccountCargo = c.Inventory.AccountCargo with { Capacity = 8 } }, "portable_cargo_fixture"))).Accepted,
                "准备已有账号仓库的真实库存快照");
            InventoryMoveRequest deposit = new(new(InventoryListType.Main, 66), 1114, 1,
                new(InventoryListType.PersonalCargo, 0), 0, 0, -1, 0, 0);
            InventoryMoveRequest withdraw = new(new(InventoryListType.PersonalCargo, 0), 1114, 1,
                new(InventoryListType.Main, 66), 1114, 0, -1, 0, 0);
            InventorySortRequest personalSort = new(InventoryListType.PersonalCargo, (ItemKind)20, 0);
            InventorySortRequest accountSort = new(InventoryListType.AccountCargo, 0, 0);
            var depositSlot3 = deposit with { Destination = new(InventoryListType.PersonalCargo, 3) };
            var accountDepositSlot3 = deposit with { Destination = new(InventoryListType.AccountCargo, 3) };
            That(f.Session.MoveInventory(depositSlot3, inventory).Accepted &&
                f.Session.SortInventory(personalSort, inventory).Accepted &&
                f.Session.Selected!.Inventory.Find(new(InventoryListType.PersonalCargo, 0))?.Core.ItemId == 1114 &&
                f.Session.MoveInventory(withdraw, inventory).Accepted, "城镇NPC仓库存取与整理保持原行为");
            That(f.Session.LeaveRoom() && f.Session.Travel(new(38, 2, 1085, 249, 5, 38, 0, 0)), "猫头鹰测试进入真实副本入口");
            Enter();
            var accountDeposit = deposit with { Destination = new(InventoryListType.AccountCargo, 0) };
            var accountWithdraw = withdraw with { Source = new(InventoryListType.AccountCargo, 0) };
            if (owl.Unlimited)
            {
                // 客户端 CStackableItem::CheckUse 对无限猫头鹰返回 2 并直接本地打开仓库 UI，不发送 CMD 44。
                That(f.Session.MoveInventory(depositSlot3, inventory).Accepted &&
                    f.Session.SortInventory(personalSort, inventory).Accepted &&
                    f.Session.Selected!.Inventory.Find(new(InventoryListType.PersonalCargo, 0))?.Core.ItemId == 1114 &&
                    f.Session.MoveInventory(withdraw, inventory).Accepted &&
                    f.Session.MoveInventory(accountDepositSlot3, inventory).Accepted &&
                    f.Session.SortInventory(accountSort, inventory).Accepted &&
                    f.Session.Selected.Inventory.Find(new(InventoryListType.AccountCargo, 0))?.Core.ItemId == 1114 &&
                    f.Session.MoveInventory(accountWithdraw, inventory).Accepted,
                    "持有可用无限猫头鹰在副本内无需先发CMD44即可存取和整理个人与账号仓库 " + id);
                SetOwlLock(1);
            }
            That(f.Session.MoveInventory(deposit, inventory) is { Accepted: false, Error: 1 } &&
                f.Session.SortInventory(personalSort, inventory) is { Accepted: false, Error: 1 } &&
                f.Session.SortInventory(accountSort, inventory) is { Accepted: false, Error: 1 },
                "未使用或已锁定猫头鹰仍拒绝副本仓库移动和整理");
            ushort characterId = f.Session.Selected!.Id;
            var capabilities = f.Session.ReadHelperCapabilities(characterId);
            That(capabilities is not null && f.Session.CanPublishHelperCapabilities(capabilities) &&
                f.Session.ReadHelperCapabilities((ushort)(characterId + 1)) is null,
                "助手能力仅绑定当前就绪角色和选角epoch");
            That(f.Session.SetHelperPortableCargo((ushort)(characterId + 1), InventoryListType.PersonalCargo) is null &&
                f.Session.SetHelperPortableCargo(characterId, InventoryListType.Main) is { Accepted: false },
                "错误角色和非仓库容器不建立助手资格");
            var helperCargo = f.Session.SetHelperPortableCargo(characterId, InventoryListType.PersonalCargo);
            That(helperCargo is { Accepted: true } && f.Session.CanPublishHelperCargo(helperCargo) &&
                f.Session.MoveInventory(depositSlot3, inventory).Accepted &&
                !f.Session.CanPublishHelperCargo(helperCargo) &&
                f.Session.SortInventory(personalSort, inventory).Accepted &&
                f.Session.Selected.Inventory.Find(new(InventoryListType.PersonalCargo, 0))?.Core.ItemId == 1114 &&
                f.Session.MoveInventory(withdraw, inventory).Accepted,
                "助手授权不消耗道具且支持个人仓库双向存取与整理，过期资产快照不发布");
            var helperAccountDeposit = deposit with { Destination = new(InventoryListType.AccountCargo, 0) };
            var helperAccountWithdraw = withdraw with { Source = new(InventoryListType.AccountCargo, 0) };
            That(!f.Session.MoveInventory(helperAccountDeposit, inventory).Accepted &&
                !f.Session.SortInventory(accountSort, inventory).Accepted &&
                f.Session.SetHelperPortableCargo(characterId, InventoryListType.AccountCargo) is { Accepted: true } &&
                f.Session.MoveInventory(accountDepositSlot3, inventory).Accepted &&
                f.Session.SortInventory(accountSort, inventory).Accepted &&
                f.Session.Selected.Inventory.Find(new(InventoryListType.AccountCargo, 0))?.Core.ItemId == 1114 &&
                f.Session.MoveInventory(helperAccountWithdraw, inventory).Accepted,
                "个人授权不越权账号仓库存取或整理，账号单独授权后支持双向存取与整理");
            That(f.Session.MutateInventory(c => new(true, 0, new(c.Inventory, c.Inventory with
                { CargoCapacity = 24 }, "helper_cargo_upgrade_boundary")), requireTown: false).Accepted &&
                !f.Session.MoveInventory(deposit, inventory).Accepted && !f.Session.MoveInventory(helperAccountDeposit, inventory).Accepted &&
                !f.Session.SortInventory(personalSort, inventory).Accepted && !f.Session.SortInventory(accountSort, inventory).Accepted,
                "任一仓库实际容量变化同时废止本实例助手旧存取与整理资格");
            if (owl.Unlimited) SetOwlLock(0);
            That(f.Use().Outcome == ConsumableUseOutcome.Committed, "猫头鹰经原消耗品提交成功 " + id);
            That(f.Session.MoveInventory(depositSlot3, inventory).Accepted &&
                f.Session.SortInventory(personalSort, inventory) is { Accepted: true, Receipt: { } personalReceipt } &&
                f.Session.CanPublishInventory(personalReceipt) &&
                f.Session.Selected.Inventory.Find(new(InventoryListType.PersonalCargo, 0))?.Core.ItemId == 1114 &&
                f.Session.MoveInventory(withdraw, inventory).Accepted, "副本使用猫头鹰后支持个人仓库真实存取与整理提交 " + id);
            That(f.Session.MoveInventory(accountDepositSlot3, inventory).Accepted &&
                f.Session.SortInventory(accountSort, inventory) is { Accepted: true, Receipt: { } accountReceipt } &&
                f.Session.CanPublishInventory(accountReceipt) &&
                f.Session.Selected.Inventory.Find(new(InventoryListType.AccountCargo, 0))?.Core.ItemId == 1114 &&
                f.Session.MoveInventory(accountWithdraw, inventory).Accepted, "同一资格支持账号仓库双向存取与整理");
            That(!f.Session.DeleteInventory(new(InventoryListType.PersonalCargo, [new(0, 0, 1, 1)], 0), inventory).Accepted,
                "猫头鹰不放宽仓库丢弃删除等其他Town门禁");
            if (owl.Unlimited) SetOwlLock(1);
            That(f.Session.MutateInventory(c => new(true, 0, new(c.Inventory, c.Inventory with
                { CargoCapacity = 16 }, "portable_cargo_upgrade_boundary")), requireTown: false).Accepted &&
                f.Session.MoveInventory(deposit, inventory) is { Accepted: false, Error: 1 } &&
                f.Session.SortInventory(personalSort, inventory) is { Accepted: false, Error: 1 } &&
                f.Session.SortInventory(accountSort, inventory) is { Accepted: false, Error: 1 },
                "已提交仓库容量变化使旧猫头鹰存取与整理资格失效");
            That(f.Session.ReturnFromFormalDungeon() is not null, "猫头鹰测试经正式回城");
            Enter();
            That(f.Session.MoveInventory(deposit, inventory) is { Accepted: false, Error: 1 } &&
                f.Session.SortInventory(personalSort, inventory) is { Accepted: false, Error: 1 } &&
                f.Session.SortInventory(accountSort, inventory) is { Accepted: false, Error: 1 }, "新副本不继承回城前猫头鹰资格");
            That(f.Session.SetHelperPortableCargo(characterId, InventoryListType.PersonalCargo) is { Accepted: true },
                "新实例只能重新建立本实例助手授权");
            f.Clock.Advance(TimeSpan.FromSeconds(4));
            if (owl.Unlimited) SetOwlLock(0);
            That(f.Use(1).Outcome == ConsumableUseOutcome.Committed, "新副本可重新使用猫头鹰");
            f.Session.ReturnToRoster(); f.Select();
            if (owl.Unlimited) SetOwlLock(1);
            That(f.Session.LeaveRoom() && f.Session.Travel(new(38, 2, 1085, 249, 5, 38, 0, 0)), "重新选角进入入口");
            Enter();
            That(f.Session.MoveInventory(deposit, inventory) is { Accepted: false, Error: 1 } &&
                f.Session.SortInventory(personalSort, inventory) is { Accepted: false, Error: 1 } &&
                f.Session.SortInventory(accountSort, inventory) is { Accepted: false, Error: 1 }, "同角色重选不继承旧资格");
            That(capabilities is not null && !f.Session.CanPublishHelperCapabilities(capabilities), "同角色重选后旧能力回执也失效");

            void SetOwlLock(byte lockId) => That(f.Session.MutateInventory(c =>
            {
                var source = c.Inventory.Find(new(InventoryListType.Main, 65))!;
                return new(true, 0, new(c.Inventory, c.Inventory with
                {
                    Items = c.Inventory.Items.Replace(source, source with { Core = source.Core with { EquipmentLockId = lockId } })
                }, "portable_cargo_owl_lock"));
            }, requireTown: false).Accepted, "切换无限猫头鹰锁定状态");

            void Enter()
            {
                f.Session.BeginDungeonSelection();
                That(f.Session.EnterFormalDungeon(3, 0, 0, global::DfoServer.Testing.SelfTestCatalogs.Dungeons(archive),
                    global::DfoServer.Testing.SelfTestCatalogs.Rooms(archive)) is not null &&
                    f.Session.CompleteFormalDungeonLoading() is not null, "猫头鹰测试使用正式实例");
            }
        }
    }

    static bool Same(Character first, Character second) => JsonSerializer.Serialize(first) == JsonSerializer.Serialize(second);

    internal sealed class FakeClock : TimeProvider
    {
        long milliseconds;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => milliseconds;
        public int Reads { get; private set; }
        public Action? BeforeUtcRead { get; set; }
        public override DateTimeOffset GetUtcNow()
        {
            Reads++; BeforeUtcRead?.Invoke();
            return DateTimeOffset.FromUnixTimeSeconds(1900000000).AddMilliseconds(milliseconds);
        }
        public void Advance(TimeSpan amount) => milliseconds += checked((long)amount.TotalMilliseconds);
    }

    sealed class Fixture : IDisposable
    {
        readonly string root = Path.Combine(Path.GetTempPath(), "S5A9-potion-session-" + Guid.NewGuid().ToString("N"));
        public string DatabasePath => Path.Combine(root, "game.db");
        public AccountOwners Owners { get; } = new();
        public AccountOwners.Lease Owner { get; }
        public CharacterRepository Repository { get; }
        public GameSession Session { get; }
        public FakeClock Clock { get; } = new();
        public InventoryConsumableUseService Service { get; }
        public ConsumableUseRequest Request { get; }
        public Fixture(TownCatalog towns, CharacterCatalog characters, bool lost = false, RecoveryConsumableDefinition? hp = null,
            RecoveryConsumableCatalog? consumables = null, byte level = 16, ChannelEntry? entry = null)
        {
            hp ??= ConsumableUseTests.Hp;
            Request = new(new(InventoryListType.Main, 65), 0, hp.ItemId, 0);
            Directory.CreateDirectory(root); Repository = new(Path.Combine(root, "game.db")); Owner = Owners.Acquire("potion-session", () => { });
            var candidate = CharacterCreation.Prepare("Potion", characters.Get(0), towns.Room, towns.InitialReturn, null);
            Repository.Create(Owner, candidate with { Level = level, Inventory = candidate.Inventory with { Items = candidate.Inventory.Items
                .Add(new(0, new(InventoryListType.Main, 65), new(hp.ItemId, ItemKind.Consumable, 3) { Marker16 = 44, SortLockFlag = 1 }))
                .Add(new(0, new(InventoryListType.Main, 66), new(1114, ItemKind.Consumable, 2))) } });
            var store = lost ? new QuestCompletionRepositoryTests.LostResultRepository(Repository) { LoseInventoryResult = true } : null;
            consumables ??= new([hp, ConsumableUseTests.Mp]);
            _ = consumables.EpicDropBuffs;
            _ = consumables.HellLegendEpicDropBuffs;
            _ = consumables.VillageMoveSpeedBuffs;
            Session = new(store is null ? Repository : store, towns, Owner,
                timeProvider: hp.EffectMilliseconds > 0 || hp.BossSelectionBuff is not null || hp.DungeonEpicDropBuff is not null || hp.VariousBuff is not null ? Clock : null,
                consumables: consumables, entry: entry);
            Service = new(consumables, Clock);
        }
        public void Select() { Session.Select(0); Session.MarkReady(); }
        public Character Read() => Repository.LoadCharacter(Owner, 0);
        public int Count => Read().Inventory.Find(new(InventoryListType.Main, 65))?.Core.Count ?? 0;
        public ConsumableUseResult Use(uint check = 0) => Session.UseConsumable(Request with { OpaqueCheck = check }, Service);
        public void Sql(string sql)
        {
            using var db = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + Path.Combine(root, "game.db") + ";Pooling=False");
            db.Open(); using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
        }
        public void Dispose() { Session.End(); Owner.Dispose(); TestFiles.DeleteDirectory(root, true); }
    }
}
