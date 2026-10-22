using System.Collections.Immutable;
using System.Text.Json;
using DfoServer.Game.Mailbox;
using DfoServer.GameWorld.Items;
using DfoServer.Game.Session;
using DfoServer.Network.Management;
using DfoServer.Persistence;

namespace DfoServer.Infrastructure;

// GM 站点的数据实现：在线信息取自协议层共用的 OnlineSessionDirectory，账号与角色经 game.db 只读查询；
// 写命令经服务进程内的开发 GM 执行器（command），小退走在线目录，金币邮件走 MailboxService.Deliver。
internal sealed class GmWebData(ServerOptions options, DateTimeOffset startedAt, OnlineSessionDirectory online, GmAccountDirectory accounts,
    Func<string, string> command, MailboxService mailbox, GoldLimitCatalog goldLimits, GmItemExtras items, GmAudit audit) : IGmWebData
{
    public GmAudit Audit => audit;

    public GmWebStatus Status() => new(Environment.ProcessId, startedAt, options.ChannelPort, options.GamePort, options.GmPort, options.GmWebPort,
        options.PvfPath, options.DataDirectory, Online().Length);

    public OnlineCharacterView[] Online() => online.Snapshot().Select(character => new OnlineCharacterView(character.Account, character.CharacterId,
        character.Name, character.Ready, character.Phase.ToString(), character.State.ToString())).ToArray();

    public GmWebAccount[] Accounts() => accounts.ListAccounts().Select(row => new GmWebAccount(row.Account, row.Disabled, row.Revision, row.Characters)).ToArray();

    public GmWebCharacter[]? Characters(string account) => accounts.ListCharacters(account)?.Select(row =>
        new GmWebCharacter(row.Id, row.Slot, row.Name, row.Job, row.GrowType, row.Level, row.Gold, row.Revision)).ToArray();

    public string Command(string line) => command(line);

    public Task<bool> ForceReturnAsync(string account, string? name) => online.ForceReturnToSelectionAsync(account, name);

    public bool Disconnect(string account) => online.Disconnect(account);

    string QuickPath => Path.Combine(options.DataDirectory, "gm-quick-items.json");
    readonly object quickGate = new();

    public string ReadQuickItems()
    {
        lock (quickGate)
        {
            try
            {
                string text = File.Exists(QuickPath) ? File.ReadAllText(QuickPath) : "[]";
                using var document = System.Text.Json.JsonDocument.Parse(text);
                return document.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array ? text : "[]";
            }
            catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or UnauthorizedAccessException) { return "[]"; }
        }
    }

    // 先写临时文件再原子替换，避免写到一半断电留下坏文件。
    public void WriteQuickItems(string json)
    {
        lock (quickGate)
        {
            Directory.CreateDirectory(options.DataDirectory);
            string temp = QuickPath + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, QuickPath, true);
        }
    }

    public byte[]? ItemIcon(int id) => items.Icon(id);
    public object? ItemPreview(int id) => items.Preview(id);

    // 外观来源经开发 GM 只读命令 appearance 取得（在线走原 owner，离线取租约），合成在本进程内完成，不改库。
    public GmAvatarResult AvatarPortrait(string account, string name, IReadOnlyList<int> tryOn)
    {
        string reply = command($"account {account} appearance {name}");
        if (!reply.StartsWith("OK ", StringComparison.Ordinal)) return new(null, reply);
        Network.Management.Avatar.AvatarLookInput input;
        try
        {
            using var document = JsonDocument.Parse(reply[3..]);
            var root = document.RootElement;
            input = new(root.GetProperty("name").GetString() ?? name, root.GetProperty("job").GetByte(), root.GetProperty("growType").GetByte(),
                root.GetProperty("equipment").EnumerateArray().Select(row => new Network.Management.Avatar.AvatarWorn(
                    row.GetProperty("slot").GetInt32(), row.GetProperty("id").GetInt32(), row.GetProperty("clear").GetInt32())).ToArray(),
                root.GetProperty("weaponSkin").GetInt32());
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return new(null, "ERR 外观数据格式无效");
        }
        var portrait = items.Portrait(input, tryOn);
        return portrait is null ? new(null, "ERR 找不到客户端 ImagePacks2（可用 DFO_IMAGEPACKS2 指定）") : new(portrait, null);
    }

    public void NotifyMailArrived(string account, string name)
    {
        if (mailbox.FindRecipient(name) is { } recipient && recipient.Account == account)
            online.Find(account)?.NotifyMailbox(recipient.Id);
    }

    public string DeliverGoldMail(string account, string name, uint gold, string text)
    {
        var recipient = mailbox.FindRecipient(name);
        if (recipient is null || recipient.Account != account)
            return "ERR 角色不存在或不属于该账号";
        // 与寄送、领取一致：邮件金币不得超过收件人等级的携带上限；领取时还要求背包金币加上它不超过上限，否则邮件保留不被领取。
        long limit = goldLimits.CarryLimit(recipient.Level);
        if (gold > limit)
            return "ERR 超过 Lv" + recipient.Level + " 的金币携带上限 " + limit;
        long created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var result = mailbox.Deliver(new("gm:" + Guid.NewGuid().ToString("N"), recipient, "开发GM", text, gold, ImmutableArray<Game.Inventory.Models.InventoryItem>.Empty,
            created, created + 15 * 86400));
        return result.Accepted
            ? "OK " + JsonSerializer.Serialize(new { target = "mail", name = recipient.Name, mail = result.MailId, gold })
            : "ERR 无法发放金币邮件：" + result.Error;
    }
}