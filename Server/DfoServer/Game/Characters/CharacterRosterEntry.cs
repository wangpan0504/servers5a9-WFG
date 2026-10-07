using System.Collections.Immutable;
using DfoServer.Game.Inventory.Models;

namespace DfoServer.Game.Characters;

// 名册只包含身份与穿戴外观，不能作为可提交的完整角色快照。
internal sealed record CharacterRosterEntry(ushort Id, ushort Slot, string Name, byte Job, byte GrowType,
    byte Level, ImmutableArray<InventoryItem> Equipment)
{
    public static CharacterRosterEntry FromCharacter(Character character) => new(character.Id, character.Slot,
        character.Name, character.Job, character.GrowType, character.Level,
        character.Inventory.Items.Where(item => item.Position.List == InventoryListType.Equipment)
            .OrderBy(item => item.Position.Index).ToImmutableArray());
}
