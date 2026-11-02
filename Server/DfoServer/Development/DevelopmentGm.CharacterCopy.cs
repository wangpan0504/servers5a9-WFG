using System.Globalization;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Session;

namespace DfoServer.Development;

internal static partial class DevelopmentGm
{
    static string CopyCharacter(ICharacterRepository repository, AccountOwners owners,
        Func<string, GameSession?> find, DevelopmentRequest request, DevelopmentCatalog? catalog)
    {
        if (request.Args.Length != 3 || !ushort.TryParse(request.Args[0], NumberStyles.None, CultureInfo.InvariantCulture, out ushort sourceId) || sourceId == 0 ||
            !long.TryParse(request.Args[1], NumberStyles.None, CultureInfo.InvariantCulture, out long revision) || !CharacterName.IsValid(request.Args[2]))
            throw new InvalidDataException("复制参数无效，请刷新原角色并填写合法的新角色名");
        if (find(request.Account) is not null) throw new InvalidOperationException("复制角色前请退出整个账号");
        var store = repository as ICharacterCopyStore ?? throw new InvalidOperationException("当前服务端不支持复制角色");
        var spawn = (catalog ?? throw new InvalidDataException("角色出生点资源未装配")).ReadCopySpawn();
        try
        {
            return WithLease(owners, request.Account, owner =>
            {
                var copied = store.CopyCharacter(owner, new(request.Name, sourceId, revision, request.Args[2], spawn.Room, spawn.Return));
                return Ok(new { sourceCharacterId = sourceId, characterId = copied.Id, name = copied.Name, slot = copied.Slot,
                    rev = copied.Revision, job = copied.Job, growType = copied.GrowType, level = copied.Level,
                    privateItems = copied.Inventory.Items.Count(item => item.Position.List != InventoryListType.AccountCargo),
                    text = "已创建本账号下的独立副本；请重新登录查看新角色。" });
            });
        }
        catch (OverflowException ex) { throw new InvalidDataException("新角色或物品身份超过客户端支持范围，本次复制已回滚", ex); }
        catch (JsonException ex) { throw new InvalidDataException("原角色关联数据格式异常，本次复制已回滚", ex); }
    }
}
