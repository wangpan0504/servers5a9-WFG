using System.Collections.Immutable;
using DfoServer.Game.Settings;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    public (bool Accepted, bool Changed) SaveQuestNotify(ClientSettingsService settings, ImmutableArray<byte> entries) => owner.Execute(() =>
    {
        // 退出选角后的迟到保存不写入其他角色，也不因缺少角色而断线。
        if (!Ready || Selected is not { } character) return (false, false);
        return (true, settings.SaveQuestNotify(owner, character.Id, entries));
    });
}
