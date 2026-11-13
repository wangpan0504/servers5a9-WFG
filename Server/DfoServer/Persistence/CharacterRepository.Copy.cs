using System.Collections.Immutable;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository : ICharacterCopyStore
{
    public Character CopyCharacter(AccountOwners.Lease owner, CharacterCopyRequest request) => owner.Execute(() =>
    {
        if (!CharacterName.IsValid(request.NewName)) throw new InvalidDataException("新角色名不符合游戏命名规则");
        using var db = database.Open();
        using var write = db.BeginWrite("CharacterRepository.CopyCharacter");
        using var tx = write.Transaction;
        CheckCopySchema(db, tx);
        var source = Require(db, tx, owner.Account, request.SourceName);
        if (source.Id != request.SourceId || source.Revision != request.Revision)
            throw new InvalidOperationException("原角色存档已变化，请刷新后重试");
        if (source.IsPvpPrivate) throw new InvalidDataException("专用决斗角色不能在同账号复制为第二个相同职业的专用角色");
        source.Inventory.Validate();
        var slots = new bool[source.Inventory.AccountWallet.CharacterSlotCapacity];
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.Parameters.AddWithValue("$account", owner.Account);
        command.Parameters.AddWithValue("$name", request.NewName);
        command.CommandText = "SELECT count(*) FROM characters WHERE name=$name";
        if ((long)command.ExecuteScalar()! != 0) throw new InvalidDataException("这个角色名已被使用，请更换名字");
        command.CommandText = "SELECT slot FROM characters WHERE account=$account";
        using (var reader = command.ExecuteReader())
            while (reader.Read()) slots[reader.GetInt32(0)] = true;
        int slot = Array.FindIndex(slots, occupied => !occupied);
        if (slot < 0) throw new InvalidDataException("本账号没有空角色位置，请先扩展角色栏或腾出位置");
        command.Parameters.AddWithValue("$source", source.Id);
        command.Parameters.AddWithValue("$slot", slot);
        Bind(command, "copy", request.Room);
        Bind(command, "copyReturn", request.Return);
        var overrides = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["name"] = "$name", ["slot"] = "$slot", ["blacklist_count"] = "0", ["inventory_items_revision"] = "0",
            ["town"] = "$copytown", ["area"] = "$copyarea", ["x"] = "$copyx", ["y"] = "$copyy", ["direction"] = "$copydirection",
            ["return_town"] = "$copyReturntown", ["return_area"] = "$copyReturnarea", ["return_x"] = "$copyReturnx",
            ["return_y"] = "$copyReturny", ["return_direction"] = "$copyReturndirection"
        };
        var columns = CopyCharacterColumns.Where(column => column != "id").ToArray();
        command.CommandText = $"INSERT INTO characters({string.Join(',', columns)}) SELECT {string.Join(',', columns.Select(column => overrides.GetValueOrDefault(column, column)))} FROM characters WHERE id=$source AND account=$account RETURNING id";
        ushort id = checked((ushort)(long)(command.ExecuteScalar() ?? throw new InvalidOperationException("未创建新角色")));

        // 账号共享容器仍使用原实例；仅为本角色实际持有的物品分配全新 UID。
        var privateItems = source.Inventory.Items.Where(item => item.Position.List is not (InventoryListType.AccountCargo or InventoryListType.AccountCommon)).ToArray();
        var before = ReadInventory(db, id, tx);
        var allocating = source.Inventory with
        {
            Items = privateItems.Select(item => item with
            {
                Uid = 0, Core = item.Avatar is not null || item.Creature is not null ? item.Core with { Value = 0 } : item.Core
            }).Concat(before.Items.Where(item => item.Position.List is InventoryListType.AccountCargo or InventoryListType.AccountCommon)).ToImmutableArray(),
            AvatarPresets = AvatarPresetState.Empty,
            AccountWallet = before.AccountWallet, AccountCargo = before.AccountCargo,
            AccountSkins = before.AccountSkins, EpicPieces = before.EpicPieces,
            ShopPurchases = before.ShopPurchases
        };
        allocating.Validate(allocating: true);
        var allocated = WriteInventory(db, tx, owner, id, new(before, allocating, "gm_character_copy"), allocating);
        var uidMap = privateItems.ToDictionary(item => item.Uid, item => allocated.Find(item.Position)!.Uid);
        long Remap(long uid) => uid == 0 ? 0 : uidMap.GetValueOrDefault(uid);
        var presets = source.Inventory.AvatarPresets with
        {
            Presets = source.Inventory.AvatarPresets.Presets.Select(preset => preset with
            {
                Parts = preset.Parts.Where(part => Remap(part.ItemUid) > 0).Select(part => part with
                {
                    ItemUid = Remap(part.ItemUid), ClearAvatarSourceUid = Remap(part.ClearAvatarSourceUid)
                }).ToImmutableArray()
            }).ToImmutableArray()
        };
        var final = allocated with
        {
            AvatarPresets = presets,
            Items = allocated.Items.Select(item => item.Position.List is InventoryListType.AccountCargo or InventoryListType.AccountCommon || item.Avatar is null ? item : item with
            {
                Avatar = item.Avatar with { ClearAvatarSourceUid = Remap(item.Avatar.ClearAvatarSourceUid) }
            }).ToImmutableArray()
        };
        final = WriteInventory(db, tx, owner, id, new(allocated, final, "gm_character_copy_references"), final);
        var emptyWallet = before with { Gold = 0, ReviveCoins = 0, WinPoints = 0 };
        final = WriteWallet(db, tx, owner, id, new(emptyWallet, final, "gm_character_copy"), final);
        // 此处只写新角色的可见状态；不触发共享钱包、账号材料或旧角色 revision。
        command.Parameters.AddWithValue("$target", id);
        command.Parameters.AddWithValue("$presets", JsonSerializer.Serialize(final.AvatarPresets));
        command.CommandText = "UPDATE characters SET avatar_preset_state=$presets WHERE id=$target AND account=$account";
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("新角色所有权发生变化");
        CopyCharacterRows(db, tx, source, id, owner.Account, uidMap);
        var copied = Require(db, tx, owner.Account, request.NewName);
        copied.Inventory.Validate();
        if (copied.Id != id || copied.Slot != slot || copied.Guild is not null || copied.Marriage is not null ||
            copied.Inventory.Items.Where(item => item.Position.List is not (InventoryListType.AccountCargo or InventoryListType.AccountCommon)).Any(item => !uidMap.ContainsValue(item.Uid)))
            throw new InvalidDataException("复制后的角色身份或物品引用未通过验证，已取消本次复制");
        tx.Commit();
        return copied;
    });
}
