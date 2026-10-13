using System.Buffers.Binary;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;
using DfoServer.Network.Builders;
using DfoServer.Persistence;
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
        Repository();
        Console.WriteLine($"PASS {Count - before} black diamond appearance/fatigue/persistence assertions");
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
            byte[] active = TownPackets.Actor(c with { Inventory = c.Inventory with
                { AccountWallet = c.Inventory.AccountWallet.SetContractExpiry(56, now + 1) } }, utcSeconds: now);
            int count = 133 + CharacterName.Encoding.GetByteCount(name);
            int pet = count + 1 + ordinary[count] * 25 + 22;
            int black = pet + 10 + checked((int)BinaryPrimitives.ReadUInt32LittleEndian(ordinary.AsSpan(pet + 4)));
            That(ordinary[black] == 0 && active[black] == 1 && active[black - 1] == 0 &&
                ordinary.AsSpan(0, black).SequenceEqual(active.AsSpan(0, black)) &&
                ordinary.AsSpan(black + 1).SequenceEqual(active.AsSpan(black + 1)),
                "黑钻只改变02677430的u8，名字/穿戴/宠物长度变化不移动其他字段，不启用PC房效果");
            Bytes(TownPackets.Actor(c with { Inventory = c.Inventory with
                { AccountWallet = c.Inventory.AccountWallet.SetContractExpiry(56, now) } }, utcSeconds: now), ordinary,
                "到期端点关闭原生黄名和index3图标，不保留旧角色标志");
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
            Character Create(AccountOwners.Lease lease, string name) => repo.Create(lease,
                new(name, new(1, 0, 1, 1), null) { DungeonState = CharacterDungeonState.NewOrdinary });
            var first = Create(owner, "BlackFirst");
            var second = Create(owner, "BlackSecond");
            var stranger = Create(other, "Other");
            var now = DateTimeOffset.UtcNow;
            long expires = now.AddDays(30).ToUnixTimeSeconds();
            repo.SetCardMembership(owner, first.Name, CardMembershipKind.GoldCard, expires);
            var beforeBlack = repo.LoadCharacter(owner, first.Slot);
            repo.SetCardMembership(owner, first.Name, CardMembershipKind.BlackDiamond, expires);
            var a = repo.LoadCharacter(owner, first.Slot);
            var b = repo.LoadCharacter(owner, second.Slot);
            That(a.BlackDiamondExpiresAt == expires && b.BlackDiamondExpiresAt == expires &&
                a.Fatigue is { Maximum: 188, Remaining: 188 } && b.Fatigue == a.Fatigue &&
                a.Revision == beforeBlack.Revision + 1 && b.Revision == second.Revision + 1,
                "账号期限与全部已知疲劳owner同事务补齐，角色快照来自实际提交值");
            That(repo.LoadCharacter(other, stranger.Slot).Fatigue == stranger.Fatigue &&
                repo.LoadCharacter(other, stranger.Slot).BlackDiamondExpiresAt == 0 &&
                JsonSerializer.Serialize(a.Inventory with { AccountWallet = first.Inventory.AccountWallet }) == JsonSerializer.Serialize(first.Inventory),
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
            Sql("CREATE TRIGGER black_fail BEFORE UPDATE ON character_fatigue BEGIN SELECT RAISE(ABORT,'black rollback'); END");
            Reject<SqliteException>(() => repo.SetCardMembership(owner, first.Name, CardMembershipKind.BlackDiamond, expires),
                "账号写入后疲劳SQL失败整笔回滚");
            That(repo.LoadCardMembership(owner, first.Id, now).BlackDiamondExpires == 0 &&
                repo.LoadCharacter(owner, late.Slot).Revision == late.Revision &&
                repo.LoadCharacter(owner, late.Slot).Fatigue == late.Fatigue,
                "晚期失败不能留下半份黑钻期限、容量或revision");
            Sql("DROP TRIGGER black_fail");
            // 旧字段仅作为迁移历史；正式资格始终由统一钱包提供。
            Sql($"UPDATE account_card_memberships SET black_diamond_expires={expires} WHERE account='black'");
            That(repo.LoadCharacter(owner, late.Slot).BlackDiamondExpiresAt == 0, "迁移后的旧会员字段不能重启已关闭的正式契约");
            Sql($"UPDATE account_wallets SET premium_contracts=json_set(premium_contracts,'$.56',{expires}) WHERE account='black'");
            late = repo.LoadCharacter(owner, late.Slot);
            var upgraded = repo.GrantBlackDiamondFatigue(owner, late, now);
            That(upgraded.Fatigue is { Maximum: 188, Remaining: 188 } && upgraded.Revision == late.Revision + 1 &&
                ReferenceEquals(repo.GrantBlackDiamondFatigue(owner, upgraded, now), upgraded),
                "旧资格在选角显式补齐一次，后续查询和重登无额外写入");
            Reject<InvalidOperationException>(() => repo.GrantBlackDiamondFatigue(owner, late, now), "旧revision不能重复补点");
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
}
