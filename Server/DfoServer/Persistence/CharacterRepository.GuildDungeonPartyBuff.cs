using DfoServer.Game.Accounts;
using DfoServer.Game.Guild;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository
{
    public int ReadGuildDungeonPartyCount(AccountOwners.Lease owner, ushort characterId,
        IReadOnlyList<GuildDungeonMembership> frozen, IReadOnlyCollection<ushort> active) => owner.Execute(() =>
    {
        if (frozen.Count is < 2 or > 4 || active.Count is < 2 or > 4 || !active.Contains(characterId) ||
            frozen.Select(row => row.CharacterId).Distinct().Count() != frozen.Count) return 0;
        using var db = database.Open(); using var tx = db.BeginRead();
        using var query = ManagementCommand(db, tx, "SELECT 1 FROM characters WHERE id=$id AND account=$account",
            ("$id", characterId), ("$account", owner.Account));
        if (query.ExecuteScalar() is null) return 0;
        var own = frozen.SingleOrDefault(row => row.CharacterId == characterId);
        if (own is null || own.GuildId == 0 || ReadDungeonGuildMembership(db, tx, characterId) != own) return 0;
        // 本服只计仍在当前实例内、仍为原入会代次的同会成员，不借离队者或重入会籍保留高档。
        return frozen.Count(row => row.GuildId == own.GuildId && active.Contains(row.CharacterId) &&
            ReadDungeonGuildMembership(db, tx, row.CharacterId) == row);
    });
}
