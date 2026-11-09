using System.Buffers.Binary;
using System.Collections.Immutable;
using DfoServer.Game.Settings;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    public ClientSettingsReceipt? UpdateNpcBookmark(ClientSettingsService settings, bool add, int id) => owner.Execute(() =>
    {
        if (!Ready || Selected is not { } character || !settings.CanBookmark(id) ||
            pendingRental is not null || HasPendingTownQuestCommit) return null;
        var saved = settings.Load(owner, character.Id);
        var entries = saved.NpcBookmarks.IsDefault ? ImmutableArray<byte>.Empty : saved.NpcBookmarks;
        var ids = new List<int>();
        for (int at = 0; at < entries.Length; at += 4)
            ids.Add(BinaryPrimitives.ReadInt32LittleEndian(entries.AsSpan()[at..]));
        if (add && !ids.Contains(id))
        {
            if (ids.Count >= 1024) return null;
            ids.Add(id);
        }
        if (!add) ids.Remove(id);
        byte[] payload = new byte[checked(ids.Count * 4)];
        for (int at = 0; at < ids.Count; at++) BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(at * 4), ids[at]);
        settings.SaveNpcBookmarks(owner, character.Id, [.. payload]);
        return LoadClientSettings(settings);
    });
}
