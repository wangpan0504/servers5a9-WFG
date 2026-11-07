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
            That(f.Session.HellLegendEpicDropPotionBonus(f.Session.Selected!, f.Clock.GetUtcNow()) == 0 &&
                f.Session.EpicDropPotionBonus(f.Session.Selected!, f.Clock.GetUtcNow()) == 0,
                "未成功使用的星空药剂不因持有物品产生掉率加成");
            var used = f.Use();
            var saved = f.Read();
            long until = saved.Inventory.ConsumableStates.Single(row => row.Kind == ConsumableStateKind.Effect).UntilMilliseconds;
            That(used.Outcome == ConsumableUseOutcome.Committed && f.Count == 2 &&
                f.Session.HellLegendEpicDropPotionBonus(saved, f.Clock.GetUtcNow()) == 6520 &&
                f.Session.EpicDropPotionBonus(saved, f.Clock.GetUtcNow()) == 0 && f.Session.CanPublishConsumable(used.Guard!),
                "星空药剂在城镇正规消费后与持续状态同事务落库，1136 掉落输入仍为零");
            f.Clock.Advance(TimeSpan.FromMilliseconds(1));
            f.Session.ReturnToRoster(); f.Select();
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
        foreach (bool allowed in new[] { false, true })
        using (var f = new Fixture(towns, characters, hp: ConsumableUseTests.Hp with
            { ItemId = 10312384, AllowedDungeons = ImmutableHashSet.Create(allowed ? 3U : 5350U) }))
        {
            f.Select();
            var before = f.Read();
            That(f.Use().Outcome == ConsumableUseOutcome.Rejected && Same(before, f.Read()) && f.Count == 3,
                "副本专用补给在城镇拒绝，不扣数量、不启动冷却或改变持久revision");
            That(f.Session.LeaveRoom() && f.Session.Travel(new(38, 2, 1085, 249, 5, 38, 0, 0)),
                "副本专用补给回归沿真实入口旅行");
            f.Session.BeginDungeonSelection();
            That(f.Use().Outcome == ConsumableUseOutcome.Rejected && f.Count == 3,
                "选图界面没有正式副本身份时仍不能消费专用药");
            That(f.Session.EnterFormalDungeon(3, 0, 0, global::DfoServer.Testing.SelfTestCatalogs.Dungeons(archive),
                global::DfoServer.Testing.SelfTestCatalogs.Rooms(archive)) is not null &&
                f.Session.CompleteFormalDungeonLoading() is not null, "专用药建立真实正式副本");
            before = f.Read();
            var result = f.Use();
            if (!allowed)
                That(result.Outcome == ConsumableUseOutcome.Rejected && Same(before, f.Read()) && f.Count == 3,
                    "真实副本不在原允许名单时完整拒绝，不以处于Dungeon代替副本资格");
            else
            {
                That(result.Outcome == ConsumableUseOutcome.Committed && f.Count == 2 && f.Session.CanPublishConsumable(result.Guard!),
                    "名单内真实副本沿原事务消费一次，前面的场景拒绝不占用冷却");
                That(f.Use(1).Outcome == ConsumableUseOutcome.Rejected && f.Count == 2,
                    "专用品仍使用原冷却组，不能因名单允许而重复消费");
            }
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
            That(f.Session.ReturnFromFormalDungeon() is not null && f.Read().Inventory.ConsumableStates.IsEmpty,
                "离开免费玩法销毁原实例效果，不保存或带出免费资格");
            if (!freeDefinition.RevivalBlessing)
                That(f.Use(1).Outcome == ConsumableUseOutcome.Committed && f.Count == 2,
                    "免费实例的临时冷却不封锁出图后新的普通有偿使用，旧完整token仍单独保护");
        }
    }

    public static void RunDisguiseInventoryLifetime(TownCatalog towns, CharacterCatalog characters,
        RecoveryConsumableCatalog consumables)
    {
        const int itemId = 490013028, formId = 56673;
        That(consumables.TryGet(itemId, out var definition) && definition is
            { Unlimited: true, Disguise: { Type: 2, Seconds: 300 } },
            "变身跨库存回归读取正式028的免耗九形态与300秒定义");
        using var f = new Fixture(towns, characters, hp: definition, consumables: consumables, level: 95);
        f.Select();
        var source = f.Session.Selected!.Inventory.Find(f.Request.Position)!;
        uint originalGold = f.Session.Selected.Inventory.Gold;
        long started = f.Clock.GetUtcNow().ToUnixTimeMilliseconds();
        var activated = f.Session.ApplyUnlimitedRandomDisguise(2, formId, itemId, f.Service);
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
            var inventory = new InventoryService(resources.Items, resources.GoldLimits, resources.EpicPieces);
            That(f.Session.MutateInventory(c => new(true, 0, new(c.Inventory, c.Inventory with
                { AccountCargo = c.Inventory.AccountCargo with { Capacity = 8 } }, "portable_cargo_fixture"))).Accepted,
                "准备已有账号仓库的真实库存快照");
            InventoryMoveRequest deposit = new(new(InventoryListType.Main, 66), 1114, 1,
                new(InventoryListType.PersonalCargo, 0), 0, 0, -1, 0, 0);
            InventoryMoveRequest withdraw = new(new(InventoryListType.PersonalCargo, 0), 1114, 1,
                new(InventoryListType.Main, 66), 1114, 0, -1, 0, 0);
            That(f.Session.MoveInventory(deposit, inventory).Accepted &&
                f.Session.MoveInventory(withdraw, inventory).Accepted, "城镇NPC仓库存取保持原行为");
            That(f.Session.LeaveRoom() && f.Session.Travel(new(38, 2, 1085, 249, 5, 38, 0, 0)), "猫头鹰测试进入真实副本入口");
            Enter();
            var accountDeposit = deposit with { Destination = new(InventoryListType.AccountCargo, 0) };
            var accountWithdraw = withdraw with { Source = new(InventoryListType.AccountCargo, 0) };
            if (owl.Unlimited)
            {
                // 客户端 CStackableItem::CheckUse 对无限猫头鹰返回 2 并直接本地打开仓库 UI，不发送 CMD 44。
                That(f.Session.MoveInventory(deposit, inventory).Accepted &&
                    f.Session.MoveInventory(withdraw, inventory).Accepted &&
                    f.Session.MoveInventory(accountDeposit, inventory).Accepted &&
                    f.Session.MoveInventory(accountWithdraw, inventory).Accepted,
                    "持有可用无限猫头鹰在副本内无需先发CMD44即可存取个人与账号仓库 " + id);
                SetOwlLock(1);
            }
            That(f.Session.MoveInventory(deposit, inventory) is { Accepted: false, Error: 1 }, "未使用或已锁定猫头鹰仍拒绝副本仓库移动");
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
                f.Session.MoveInventory(deposit, inventory).Accepted &&
                !f.Session.CanPublishHelperCargo(helperCargo) && f.Session.MoveInventory(withdraw, inventory).Accepted,
                "助手授权不消耗道具且支持个人仓库双向提交，过期资产快照不发布");
            var helperAccountDeposit = deposit with { Destination = new(InventoryListType.AccountCargo, 0) };
            var helperAccountWithdraw = withdraw with { Source = new(InventoryListType.AccountCargo, 0) };
            That(!f.Session.MoveInventory(helperAccountDeposit, inventory).Accepted &&
                f.Session.SetHelperPortableCargo(characterId, InventoryListType.AccountCargo) is { Accepted: true } &&
                f.Session.MoveInventory(helperAccountDeposit, inventory).Accepted && f.Session.MoveInventory(helperAccountWithdraw, inventory).Accepted,
                "个人授权不越权账号仓库，账号单独授权后支持双向提交");
            That(f.Session.MutateInventory(c => new(true, 0, new(c.Inventory, c.Inventory with
                { CargoCapacity = 24 }, "helper_cargo_upgrade_boundary")), requireTown: false).Accepted &&
                !f.Session.MoveInventory(deposit, inventory).Accepted && !f.Session.MoveInventory(helperAccountDeposit, inventory).Accepted,
                "任一仓库实际容量变化同时废止本实例助手旧资格");
            if (owl.Unlimited) SetOwlLock(0);
            That(f.Use().Outcome == ConsumableUseOutcome.Committed, "猫头鹰经原消耗品提交成功 " + id);
            That(f.Session.MoveInventory(deposit, inventory).Accepted &&
                f.Session.MoveInventory(withdraw, inventory).Accepted, "副本使用猫头鹰后支持双向真实库存提交 " + id);
            That(f.Session.MoveInventory(accountDeposit, inventory).Accepted &&
                f.Session.MoveInventory(accountWithdraw, inventory).Accepted, "同一资格支持账号仓库双向存取");
            That(!f.Session.MutateInventory(c => inventory.Sort(c, new(InventoryListType.PersonalCargo, ItemKind.Consumable, 0))).Accepted,
                "猫头鹰不放宽仓库整理等其他Town门禁");
            if (owl.Unlimited) SetOwlLock(1);
            That(f.Session.MutateInventory(c => new(true, 0, new(c.Inventory, c.Inventory with
                { CargoCapacity = 16 }, "portable_cargo_upgrade_boundary")), requireTown: false).Accepted &&
                f.Session.MoveInventory(deposit, inventory) is { Accepted: false, Error: 1 },
                "已提交仓库容量变化使旧猫头鹰资格失效");
            That(f.Session.ReturnFromFormalDungeon() is not null, "猫头鹰测试经正式回城");
            Enter();
            That(f.Session.MoveInventory(deposit, inventory) is { Accepted: false, Error: 1 }, "新副本不继承回城前猫头鹰资格");
            That(f.Session.SetHelperPortableCargo(characterId, InventoryListType.PersonalCargo) is { Accepted: true },
                "新实例只能重新建立本实例助手授权");
            f.Clock.Advance(TimeSpan.FromSeconds(4));
            if (owl.Unlimited) SetOwlLock(0);
            That(f.Use(1).Outcome == ConsumableUseOutcome.Committed, "新副本可重新使用猫头鹰");
            f.Session.ReturnToRoster(); f.Select();
            if (owl.Unlimited) SetOwlLock(1);
            That(f.Session.LeaveRoom() && f.Session.Travel(new(38, 2, 1085, 249, 5, 38, 0, 0)), "重新选角进入入口");
            Enter();
            That(f.Session.MoveInventory(deposit, inventory) is { Accepted: false, Error: 1 }, "同角色重选不继承旧资格");
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
                timeProvider: hp.EffectMilliseconds > 0 || hp.BossSelectionBuff is not null ? Clock : null, consumables: consumables, entry: entry);
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
