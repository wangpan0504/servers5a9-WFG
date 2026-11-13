using DfoServer.Game.Accounts;
using DfoServer.GameWorld.Towns;

namespace DfoServer.Game.Characters;

// 只有引导终局回候战使用。普通存档仍走会等待的 Save。
// 实现必须在拿不到写锁时立刻失败，不能借用生产 30 秒命令超时。
internal interface IImmediateTownSave
{
    Character SaveTownImmediate(AccountOwners.Lease owner, Character expected, Location position, Location? returnPoint);
    Character LoadCharacterImmediate(AccountOwners.Lease owner, ushort slot);
}
