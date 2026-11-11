using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.GameWorld.Towns;
using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository : IImmediateTownSave
{
    // 调用方已经持有 owner。这里重新进入只复核租约，不等待别的账号，也不等待 SQLite。
    // 婚房仍走原来的 Save：引导候战区不是 101。COMMIT 之前不改调用方的内存。
    public Character SaveTownImmediate(AccountOwners.Lease owner, Character expected, Location position, Location? returnPoint)
    {
        if (position.Town == 101) return Save(owner, expected, position, returnPoint);
        return owner.Execute(() =>
        {
            using var db = database.OpenImmediate();
            if (!SqliteWriteGate.TryTake("CharacterRepository.SaveTownImmediate", out var gate))
                throw Busy("write gate");
            using (gate)
            {
                bool began = false;
                try
                {
                    SqliteImmediate.BeginImmediate(db.Connection);
                    began = true;
                    if (SqliteImmediate.UpdateTown(db.Connection, owner, expected, position, returnPoint) != 1)
                        throw new InvalidOperationException("Character revision or owner changed");
                    SqliteImmediate.Commit(db.Connection);
                    began = false;
                }
                finally
                {
                    if (began) SqliteImmediate.Rollback(db.Connection);
                }
            }
            return expected with { Revision = expected.Revision + 1, Position = position, Return = returnPoint };
        });
    }

    // 先用 0 毫秒预算探读。探到忙就抛 SQLITE_BUSY，不再调用会按生产超时重试的 LoadCharacter。
    public Character LoadCharacterImmediate(AccountOwners.Lease owner, ushort slot)
    {
        using (var db = database.OpenImmediate())
            SqliteImmediate.ProbeRead(db.Connection);
        return LoadCharacter(owner, slot);
    }

    static SqliteException Busy(string detail) => new("SQLite Error 5: '" + detail + "'.", 5);
}
