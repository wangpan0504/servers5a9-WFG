using System.Collections.Immutable;
using System.Text.Json;
using DfoServer.Game.Mailbox;
using DfoServer.Game.Raids;
using DfoServer.GameWorld.Items;
using DfoServer.Game.Session;
using DfoServer.Network.Management;
using DfoServer.Persistence;

namespace DfoServer.Infrastructure;

// GM 站点的数据实现：在线信息取自协议层共用的 OnlineSessionDirectory，账号与角色经 game.db 只读查询；
// 写命令经服务进程内的开发 GM 执行器（command），小退走在线目录，金币邮件走 MailboxService.Deliver。
internal sealed class GmWebData(ServerOptions options, DateTimeOffset startedAt, OnlineSessionDirectory online, GmAccountDirectory accounts,
    Func<string, string> command, MailboxService mailbox, GoldLimitCatalog goldLimits, GmItemExtras items, GmAudit audit,
    RaidEntryStore? sharedRaidWindows = null, GameWorld.Events.EventResourceCatalog? eventResources = null,
    Func<CashShopPromotionCatalog>? promotions = null, CashShopPromotionConfiguration? promotionConfiguration = null) : IGmWebData
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

    // 与入城投影共用同一实例：GM 写入后，下一次入城/换频道的 EVENT_INFO 即按新表下发。
    readonly RaidEntryStore raidWindows = sharedRaidWindows ?? new(options.DataDirectory);
    public string ReadRaidWindows() => raidWindows.ReadJson();
    public void WriteRaidWindows(string json) => raidWindows.Replace(json);

    public byte[]? ItemIcon(int id) => items.Icon(id);
    public object? ItemPreview(int id) => items.Preview(id);

    public object? EventResources(int offset, int limit, string? query, string kind) => eventResources?.Page(offset, limit, query, kind);

    public object EventBusinessStatus() => new
    {
        promotions = PromotionActivities(),
        raidControl = new { name = "四个团本开放时段", purpose = "安徒恩、卢克、超时空漩涡与普雷沿已有时段表独立开放。",
            endpoint = "/api/raid-windows", relatedTab = "Raid", supportsControl = false },
        businesses = new[]
        {
            new { key = "spring-treasure", name = "春节宝藏关联掉落", purpose = "个人八件时装、普通推荐地下城合格领主独立10%掉落。", supportsControl = false, controlReason = "随对应礼包配置启停，不是单独活动开关。" },
            new { key = "vending", name = "投币售货机", purpose = "机器1与3沿真实投币、奖品选择和库存事务工作。", supportsControl = false, controlReason = "属于常驻业务，不等同于活动增益售货机资源。" },
            new { key = "rental", name = "装备租赁与幸运星", purpose = "沿真实租赁等级、价格、租赁点与幸运星规则工作。", supportsControl = false, controlReason = "属于常驻业务，没有通用活动开关。" },
            new { key = "guild-attendance", name = "公会每日出勤", purpose = "保存并查询真实公会出勤记录。", supportsControl = false, controlReason = "不是活动签到领奖，没有通用活动开关。" }
        }
    };

    public object? PromotionActivities()
    {
        var catalog = promotions?.Invoke();
        return catalog?.Activities.Select(activity => new
        {
            key = JsonSerializer.Serialize(activity.Configuration.Rules.Scope), scope = activity.Configuration.Rules.Scope,
            name = activity.Configuration.Rules.Scope == Game.CashShopPromotions.CashShopPromotionPageService.Scope
                ? "众神降世礼包累计购买与宝藏掉落" : "礼包累计购买奖励：" + activity.Configuration.Rules.Scope.Campaign,
            purpose = "按真实礼包配置控制新购买累计奖励与对应关联掉落；已提交订单和历史累计保留。",
            implemented = true, enabled = activity.Configuration.Enabled,
            active = activity.Availability == PromotionAvailability.Active, revision = catalog.Revision,
            statusText = activity.Availability == PromotionAvailability.Active ? "已开启" :
                activity.Availability == PromotionAvailability.Disabled ? "配置已关闭" : "配置开启，资源或奖励校验未通过",
            reason = activity.Availability == PromotionAvailability.Active ? "" : activity.Error ?? activity.Configuration.DisabledReason,
            supportsControl = promotionConfiguration is not null, supportsSchedule = false,
            endpoint = promotionConfiguration is null ? null : "/api/events/promotions",
            controlReason = "启用前重新校验真实商品、奖励及消费者，不提供定时或自动换期。"
        }).ToArray();
    }

    public object SetPromotionEnabled(PromotionScope scope, long expectedRevision, bool enabled)
    {
        if (promotionConfiguration is null) throw new InvalidOperationException("礼包配置控制尚未装配");
        promotionConfiguration.SetEnabled(scope, expectedRevision, enabled);
        return PromotionActivities()!;
    }

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
