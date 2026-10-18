using System.Buffers.Binary;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Items;
using DfoServer.Network.Builders;
using DfoServer.Persistence;
using DfoServer.Development;
using DfoServer.Game.Settings;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class BlackDiamondTests
{
    public static void RunCore()
    {
        int before = Count;
        Appearance();
        Fatigue();
        Stamina();
        Repository();
        Console.WriteLine($"PASS {Count - before} 黑钻外观、疲劳、虚弱及持久化断言");
    }

    static void Appearance()
    {
        const long now = 1700000000;
        var creature = new InventoryItem(91, new(InventoryListType.Equipment, 26), new(63000, ItemKind.Creature, 91))
        { Creature = new() { Name = "小龙", Level = 9, Satiety = 77 } };
        foreach (string name in new[] { "Black", "黑钻测试" })
        foreach (bool worn in new[] { false, true })
        {
            var c = new Character(1, 0, name, 1, new(1, 0, 1, 1), null)
            { Inventory = worn ? new([creature]) : InventorySnapshot.Empty };
            byte[] ordinary = TownPackets.Actor(c, utcSeconds: now);
            int count = 133 + CharacterName.Encoding.GetByteCount(name);
            int pet = count + 1 + ordinary[count] * 25 + 22;
            int black = pet + 10 + checked((int)BinaryPrimitives.ReadUInt32LittleEndian(ordinary.AsSpan(pet + 4)));
            foreach (bool diamond in new[] { false, true })
            foreach (bool pcRoom in new[] { false, true })
            {
                var actor = c with { Inventory = c.Inventory with
                    { AccountWallet = c.Inventory.AccountWallet.SetContractExpiry(56, diamond ? now + 1 : now) } };
                byte[] active = TownPackets.Actor(actor, utcSeconds: now, premiumPcRoom: pcRoom);
                That(active[black] == (diamond ? 1 : 0) && active[black - 1] == (pcRoom ? 1 : 0) &&
                    ordinary.AsSpan(0, black - 1).SequenceEqual(active.AsSpan(0, black - 1)) &&
                    ordinary.AsSpan(black + 1).SequenceEqual(active.AsSpan(black + 1)),
                    "四种资格组合独立发送网吧index2和黑钻index3，名字/穿戴/宠物及其他字段保持");
                Bytes(TownPackets.Actor(actor, utcSeconds: now + 1, premiumPcRoom: pcRoom),
                    TownPackets.Actor(c, utcSeconds: now + 1, premiumPcRoom: pcRoom),
                    "黑钻到期关闭index3但保留当前全局网吧身份，不把两位同时清零");
            }
        }
    }

    static void Fatigue()
    {
        var raw = new CharacterFatigueState(40, 156, 7, 116, 9, 11);
        var granted = raw.GrantBlackDiamondCapacity();
        That(granted is { Used: 40, Maximum: 188, Remaining: 148, UsedMaximum: 7, ContextAuxiliary: 9, SelectionAuxiliary: 11 },
            "黑钻补32点总容量与剩余量，不复用第三疲劳字段或覆盖辅助owner");
        That(granted.GrantBlackDiamondCapacity() == granted && granted.ForBlackDiamond(true) == granted,
            "重复开通、查询和重登不重复补32点");
        var spent = granted.ConsumeOrdinaryRoom();
        That(spent is { Used: 41, Maximum: 188, Remaining: 147 } &&
            spent.ForBlackDiamond(false) is { Used: 41, Maximum: 156, Remaining: 115 },
            "黑钻额外疲劳进入真实扣点，关闭只撤回当前可见附加池，不回退已消耗事实");
        That((granted with { Used = 156, Remaining = 32 }).ForBlackDiamond(false).Remaining == 0 &&
            (granted with { Used = 188, Remaining = 0 }).ForBlackDiamond(false) is { Used: 156, Remaining: 0 },
            "只剩附加池且黑钻到期时不能用隐藏的32点重新入场");
        That(spent.GrantBlackDiamondCapacity() == spent, "续期恢复原附加池，不退款已经消耗的点数");
        byte[] body = FatiguePackets.Current(granted);
        That(BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(2)) == 188 &&
            BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(4)) == 7 &&
            BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(6)) == 148,
            "NOTI36发送实际188上限，不能复制旧版156/32第三字段格式");
    }

    static void Repository()
    {
        string root = Path.Combine(Path.GetTempPath(), "S5A9-black-diamond-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var owners = new AccountOwners();
        using var owner = owners.Acquire("black", () => { });
        using var other = owners.Acquire("ordinary", () => { });
        try
        {
            string path = Path.Combine(root, "game.db");
            var repo = new CharacterRepository(path);
            var settings = new GameDebugSettings();
            string Switch(string command) => DevelopmentGm.Answer(command, "black", repo, owners,
                _ => throw new InvalidOperationException("全局开关不应查找或接管账号"), debugSettings: settings);
            That(!settings.PremiumPcRoom && Switch("pc-room") == "OK {\"premiumPcRoom\":false}" &&
                !DevelopmentRequest.Parse("pc-room", "black").Writes, "网吧身份默认关闭，只读查询不写玩家状态");
            foreach (bool enabled in new[] { true, true, false, false })
                That(Switch("account ordinary pc-room " + (enabled ? "on" : "off")) ==
                    "OK {\"premiumPcRoom\":" + (enabled ? "true}" : "false}") && settings.PremiumPcRoom == enabled &&
                    DevelopmentRequest.Parse("pc-room on", "black").Writes,
                    "GM网吧开关为同一实例全局设置，重复操作幂等，不按account前缀分配会员");
            That(Switch("pc-room auto").StartsWith("ERR ", StringComparison.Ordinal) &&
                Switch("pc-room on extra").StartsWith("ERR ", StringComparison.Ordinal) && !settings.PremiumPcRoom,
                "非法网吧开关不改变当前状态");
            That(DevelopmentGm.Answer("pc-room on", "black", repo, owners, _ => null).StartsWith("ERR ", StringComparison.Ordinal) &&
                !new GameDebugSettings().PremiumPcRoom, "离线入口拒绝网吧设置，新实例恢复关闭，不写入账号期限");
            Character Create(AccountOwners.Lease lease, string name) => repo.Create(lease,
                new(name, new(1, 0, 1, 1), null) { DungeonState = CharacterDungeonState.NewOrdinary });
            var first = Create(owner, "BlackFirst");
            var second = Create(owner, "BlackSecond");
            var stranger = Create(other, "Other");
            var now = DateTimeOffset.UtcNow;
            Character Weaken(AccountOwners.Lease lease, Character character) => InventoryService.Commit(repo, lease, character,
                new(true, 0, new(character.Inventory, character.Inventory with { StaminaUntil = now.ToUnixTimeSeconds() + 600 }, "test_weakness")));
            first = Weaken(owner, first);
            second = Weaken(owner, second);
            stranger = Weaken(other, stranger);
            long expires = now.AddDays(30).ToUnixTimeSeconds();
            repo.SetCardMembership(owner, first.Name, CardMembershipKind.GoldCard, expires);
            var beforeBlack = repo.LoadCharacter(owner, first.Slot);
            That(beforeBlack.Inventory.StaminaUntil == first.Inventory.StaminaUntil, "魔王契约不是黑钻，不自动清除虚弱");
            repo.SetCardMembership(owner, first.Name, CardMembershipKind.BlackDiamond, expires);
            var a = repo.LoadCharacter(owner, first.Slot);
            var b = repo.LoadCharacter(owner, second.Slot);
            That(a.BlackDiamondExpiresAt == expires && b.BlackDiamondExpiresAt == expires &&
                a.Fatigue is { Maximum: 188, Remaining: 188 } && b.Fatigue == a.Fatigue &&
                a.Inventory.StaminaUntil == 0 && b.Inventory.StaminaUntil == 0 &&
                a.Revision == beforeBlack.Revision + 1 && b.Revision == second.Revision + 1,
                "账号期限、全部已知疲劳owner及已有虚弱同事务处理，角色revision只推进一次");
            That(repo.LoadCharacter(other, stranger.Slot).Fatigue == stranger.Fatigue &&
                repo.LoadCharacter(other, stranger.Slot).BlackDiamondExpiresAt == 0 &&
                repo.LoadCharacter(other, stranger.Slot).Inventory.StaminaUntil == stranger.Inventory.StaminaUntil &&
                JsonSerializer.Serialize(a.Inventory with { AccountWallet = first.Inventory.AccountWallet,
                    StaminaUntil = first.Inventory.StaminaUntil }) == JsonSerializer.Serialize(first.Inventory),
                "其他账号、金币和物品不受黑钻开通影响");
            repo.SetCardMembership(owner, first.Name, CardMembershipKind.BlackDiamond, expires + 1);
            That(repo.LoadCharacter(owner, first.Slot).Revision == a.Revision + 1 &&
                repo.LoadCharacter(owner, first.Slot).Inventory.AccountWallet.Revision == a.Inventory.AccountWallet.Revision + 1 &&
                repo.LoadCharacter(owner, first.Slot).Fatigue == a.Fatigue,
                "实际续期沿统一钱包推进账号和提交角色revision，但不额外补点");
            var renewed = repo.LoadCharacter(owner, first.Slot);
            repo.SetCardMembership(owner, first.Name, CardMembershipKind.BlackDiamond, expires + 1);
            That(repo.LoadCharacter(owner, first.Slot).Revision == renewed.Revision &&
                repo.LoadCharacter(owner, first.Slot).Inventory.AccountWallet.Revision == renewed.Inventory.AccountWallet.Revision,
                "同值契约设置无资产变化，不申请多余写入或推进版本");
            repo.SetCardMembership(owner, first.Name, CardMembershipKind.BlackDiamond, 0);
            a = repo.LoadCharacter(owner, first.Slot);
            That(a.BlackDiamondExpiresAt == 0 && a.Fatigue!.ForBlackDiamond(false).Maximum == 156 &&
                repo.LoadCardMembership(owner, first.Id, now) is { GoldCardExpires: > 0 },
                "关闭黑钻保留原魔王期限及已分配疲劳事实");
            var late = Create(owner, "LateBlack");
            using var db = new SqliteConnection("Data Source=" + path + ";Pooling=False");
            db.Open();
            void Sql(string text)
            {
                using var cmd = db.CreateCommand();
                cmd.CommandText = text;
                cmd.ExecuteNonQuery();
            }
            Sql("ALTER TABLE account_card_memberships ADD COLUMN returning_player_expires INTEGER NOT NULL DEFAULT 0 CHECK(returning_player_expires>=0)");
            Sql($"INSERT INTO account_card_memberships(account,returning_player_expires) VALUES('black',{expires})");
            That(repo.LoadCardMembership(owner, first.Id, now).LegacyReturningPlayerExpires == 0,
                "旧本地库中回归字段只归档保留，正式会员读取不装载回归资格");
            a = Weaken(owner, repo.LoadCharacter(owner, first.Slot));
            b = Weaken(owner, repo.LoadCharacter(owner, second.Slot));
            Sql("CREATE TRIGGER black_fail BEFORE UPDATE ON character_fatigue BEGIN SELECT RAISE(ABORT,'black rollback'); END");
            Reject<SqliteException>(() => repo.SetCardMembership(owner, first.Name, CardMembershipKind.BlackDiamond, expires),
                "账号写入后疲劳SQL失败整笔回滚");
            That(repo.LoadCardMembership(owner, first.Id, now).BlackDiamondExpires == 0 &&
                repo.LoadCharacter(owner, late.Slot).Revision == late.Revision &&
                repo.LoadCharacter(owner, late.Slot).Fatigue == late.Fatigue &&
                repo.LoadCharacter(owner, first.Slot).Inventory.StaminaUntil == a.Inventory.StaminaUntil &&
                repo.LoadCharacter(owner, second.Slot).Inventory.StaminaUntil == b.Inventory.StaminaUntil &&
                repo.LoadCharacter(owner, second.Slot).Revision == b.Revision,
                "晚期失败回滚黑钻期限、容量、同账号虚弱及revision");
            Sql("DROP TRIGGER black_fail");
            // 旧字段仅作为迁移历史；正式资格始终由统一钱包提供。
            Sql($"UPDATE account_card_memberships SET black_diamond_expires={expires} WHERE account='black'");
            That(repo.LoadCharacter(owner, late.Slot).BlackDiamondExpiresAt == 0, "迁移后的旧会员字段不能重启已关闭的正式契约");
            Sql($"UPDATE account_wallets SET premium_contracts=json_set(premium_contracts,'$.56',{expires}) WHERE account='black'");
            late = repo.LoadCharacter(owner, late.Slot);
            late = Weaken(owner, late);
            var upgraded = repo.GrantBlackDiamondFatigue(owner, late, now);
            That(upgraded.Fatigue is { Maximum: 188, Remaining: 188 } && upgraded.Inventory.StaminaUntil == 0 && upgraded.Revision == late.Revision + 1 &&
                ReferenceEquals(repo.GrantBlackDiamondFatigue(owner, upgraded, now), upgraded),
                "旧资格在选角显式补齐一次，后续查询和重登无额外写入");
            Reject<InvalidOperationException>(() => repo.GrantBlackDiamondFatigue(owner, late, now), "旧revision不能重复补点");
            var oldWeak = repo.LoadCharacter(owner, first.Slot);
            var repaired = repo.GrantBlackDiamondFatigue(owner, oldWeak, now);
            That(repaired.Inventory.StaminaUntil == 0 && repaired.Fatigue == oldWeak.Fatigue && repaired.Revision == oldWeak.Revision + 1 &&
                ReferenceEquals(repo.GrantBlackDiamondFatigue(owner, repaired, now), repaired),
                "已补齐疲劳的存量黑钻也显式修复误罚虚弱，不额外补点或重复写库");
            repo.SetCardMembership(owner, first.Name, CardMembershipKind.BlackDiamond, expires);
            using (var writer = db.BeginTransaction(deferred: false))
            {
                That(repo.SetCardMembership(owner, first.Name, CardMembershipKind.BlackDiamond, expires).BlackDiamondExpires == expires,
                    "零变化黑钻设置不申请writer，不等待其他账号已持有的SQLite写事务");
            }
            var newborn = Create(owner, "NewBlack");
            That(newborn.Fatigue is { Maximum: 188, Remaining: 188 } && newborn.BlackDiamondExpiresAt == expires,
                "有效黑钻账号新建角色直接取得同一容量和外观资格");
            That(new CharacterRepository(path).LoadCharacter(owner, upgraded.Slot).Fatigue == upgraded.Fatigue,
                "仓储重建保留黑钻已分配容量，不因重启清零消耗");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    static void Stamina()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1800000000);
        var character = new Character(1, 0, "Retreat", 1, new(1, 0, 1, 1), null) { Level = 20 };
        var map = new DungeonMap("boss",new(0,0),1,"map/retreat-fixture.map");
        var maze = new DungeonMaze(0,1,1,"BB",[new(0,0)],[new(0,0)],[map],[]);
        var entry = new DungeonEntryPlan(character.Id,character.Revision,3,0,byte.MaxValue,0,maze,map,map);
        InventorySnapshot Retreat(Character actor, DateTimeOffset time) => StaminaRecoveryService.ApplyDungeonRetreat(
            actor,entry,SinglePlayerDungeonPhase.Dungeon,time);
        var punished = Retreat(character, now);
        That(punished.StaminaUntil == now.ToUnixTimeSeconds() + 600 &&
            ReferenceEquals(Retreat(character with { Level = 19 }, now), character.Inventory),
            "普通角色20级起罚，19级及以下退出不制造虚弱");
        var weak = character with { Inventory = punished };
        That(Retreat(weak, now.AddMinutes(1)).StaminaUntil == now.ToUnixTimeSeconds() + 660,
            "非豁免普通角色重退保留原有十分钟惩罚规则");
        foreach (var actor in new[] { character, weak })
            That(ReferenceEquals(StaminaRecoveryService.ApplyDungeonRetreat(actor, entry, SinglePlayerDungeonPhase.Dungeon, now,
                premiumPcRoom: true), actor.Inventory), "仅网吧身份免除本次退图惩罚，不改写原有虚弱及资产");
        foreach (long expiry in new[] { 0L, now.ToUnixTimeSeconds(), now.ToUnixTimeSeconds() + 1 })
        {
            var inventory = punished with { AccountWallet = punished.AccountWallet.SetContractExpiry(CardMembership.BlackDiamondType, expiry) };
            bool active = expiry > now.ToUnixTimeSeconds();
            That(StaminaRecoveryService.Percent(inventory, now) == (active ? 100 : 10) &&
                Retreat(character with { Inventory = inventory }, now).StaminaUntil ==
                    (active ? 0 : inventory.StaminaUntil), "黑钻豁免使用统一56期限，到期等号不保留资格");
        }
        var devil = character with { Inventory = character.Inventory with
            { AccountWallet = character.Inventory.AccountWallet.SetContractExpiry(CardMembership.DevilContractType, now.ToUnixTimeSeconds() + 60) } };
        That(Retreat(devil, now).StaminaUntil > 0,
            "免费恢复的魔王契约不冒充黑钻自动免罚资格");
        That(StaminaRecoveryService.ApplyDungeonRetreat(character,entry with { DisableFatigueConsumption = true },
            SinglePlayerDungeonPhase.Dungeon,now).StaminaUntil == punished.StaminaUntil,
            "免扣Fatigue疲劳值不等于免除Stamina退图虚弱");
        var diamondWeak = weak with { Inventory = weak.Inventory with
            { AccountWallet = weak.Inventory.AccountWallet.SetContractExpiry(CardMembership.BlackDiamondType,now.ToUnixTimeSeconds() + 60) } };
        foreach (var exempt in new[] { entry with { DisableGiveupPenalty = true },entry with { DungeonId = 5000 },
            entry with { IsTutorial = true },entry with { DungeonId = 7142, Maze = maze with { IsClassExperience = true } },
            entry with { Maze = maze with { IsTournament = true } },entry with { IsPractice = true } })
        foreach (var actor in new[] { character,weak,diamondWeak })
            That(ReferenceEquals(StaminaRecoveryService.ApplyDungeonRetreat(actor,exempt,SinglePlayerDungeonPhase.Dungeon,now),actor.Inventory),
                "资源豁免、5000、教程、职业体验、比赛及练习退出不制造惩罚，也不改写原资产或已有虚弱");
        foreach (var phase in new[] { SinglePlayerDungeonPhase.Loading,SinglePlayerDungeonPhase.Settlement })
            That(ReferenceEquals(StaminaRecoveryService.ApplyDungeonRetreat(weak,entry,phase,now),weak.Inventory),
                "加载及已结算退出保持原资产，不新增或清除虚弱");
    }
}
