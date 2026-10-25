using System.Collections.Immutable;
using System.Text;
using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Quests;

namespace DfoServer.Game.Quests;

internal sealed record QuestMailDeclaration(int Time, string Title, string Contents,
    ImmutableArray<InventoryReward> Enclosures)
{
    internal string Body => Title + "\n" + Contents;
}

// 本服兼容规则：0为首次取得完整接取资格，1为完成；原字段保留，未知值拒绝。
// NOTI97只有发件人和正文，没有标题字段；标题放正文，不将NPC姓名冒充玩家发件人。
internal sealed record QuestMailPlan(int QuestId, string ResourcePath, QuestMailDeclaration Declaration,
    ImmutableArray<InventoryItem> Attachments, DateTimeOffset FrozenAt)
{
    internal const string Sender = "系统任务";
    internal static bool HasMail(StoryQuest quest) => quest.Definition?.Fields.Any(field => IsField(field.Name)) == true;
    internal static bool IsField(string name) => name is "mail time" or "mail title" or "mail contents" or "mail enclose";

    internal static bool CompletionDeclaration(StoryQuest quest)
    {
        if (!HasMail(quest) || quest.MainQuest is not null) return false;
        try { return Read(quest).Time == 1; }
        catch (NotSupportedException) { return false; }
    }

    internal static QuestMailDeclaration Read(StoryQuest quest)
    {
        var fields = quest.Definition?.Fields.Where(field => IsField(field.Name)).ToArray() ?? [];
        if (fields.Length == 0 || fields.GroupBy(field => field.Name).Any(group => group.Count() != 1) ||
            fields.Any(field => field.Children.Length != 0))
            throw new NotSupportedException("任务邮件声明缺失、重复或含未知子段");
        var time = fields.SingleOrDefault(field => field.Name == "mail time");
        var title = fields.SingleOrDefault(field => field.Name == "mail title");
        var contents = fields.SingleOrDefault(field => field.Name == "mail contents");
        if (time?.Values is not [{ Type: 0, Text: null, Value: 0 or 1 } whenValue] ||
            title?.Values is not [{ Type: 6, Text: not null } titleValue] ||
            contents?.Values is not [{ Type: 6, Text: not null } contentsValue])
            throw new NotSupportedException("任务邮件需要明确0/1时机及标题正文");
        string body = titleValue.Text + "\n" + contentsValue.Text;
        try
        {
            if (body.Contains('\0') || CharacterName.Encoding.GetByteCount(body) > 511)
                throw new NotSupportedException("任务邮件正文超出本版编码或长度合同");
        }
        catch (EncoderFallbackException error) { throw new NotSupportedException("任务邮件含本版不能表示的字符", error); }
        var values = fields.SingleOrDefault(field => field.Name == "mail enclose")?.Values ?? [];
        if (values.Length % 2 != 0 || values.Length > 20 || values.Any(value => value is not { Type: 0, Text: null, Value: > 0 }))
            throw new NotSupportedException("任务邮件附件需要正整数物品数量对");
        var rewards = ImmutableArray.CreateBuilder<InventoryReward>();
        for (int index = 0; index < values.Length; index += 2)
            rewards.Add(new(values[index].Value, values[index + 1].Value));
        return new(whenValue.Value, titleValue.Text, contentsValue.Text, rewards.ToImmutable());
    }

    internal static QuestMailPlan Prepare(StoryQuest quest, ItemCatalog items, EpicPieceCatalog pieces, DateTimeOffset now)
    {
        var declaration = Read(quest);
        var attachments = ImmutableArray.CreateBuilder<InventoryItem>();
        foreach (var reward in declaration.Enclosures)
        {
            var template = items.Get(reward.ItemId);
            if (CurrencyWallet.RewardKind(reward.ItemId) is not null || pieces.ByPiece.ContainsKey(reward.ItemId) ||
                template.AcquisitionContract is not null || template.IsPremiumService || !template.Expiration.SupportsCreation)
                throw new NotSupportedException("任务邮件附件必须属于已实现的实体持有域");
            int maximum = InventoryRewardGrantService.Create(template,utcNow:now).IsStackable ? template.StackLimit : 1;
            int remaining = reward.Count;
            while (remaining > 0)
            {
                if (attachments.Count == 10) throw new NotSupportedException("任务邮件附件超过本版十项容量");
                int count = Math.Min(remaining, maximum);
                if (count <= 0) throw new InvalidDataException("任务邮件物品堆叠上限无效");
                var core = InventoryRewardGrantService.Create(template, count, now);
                attachments.Add(InventoryRewardGrantService.CreateItem(template, new(InventoryListType.Main, 0), core));
                remaining -= count;
            }
        }
        return new(quest.Id, quest.Path, declaration, attachments.ToImmutable(), now);
    }
}
