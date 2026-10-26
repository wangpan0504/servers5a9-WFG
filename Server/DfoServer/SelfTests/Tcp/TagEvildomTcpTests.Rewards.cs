using DfoServer.Game.TagEvildom;
using DfoServer.GameWorld.Dungeons;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static partial class TagEvildomTcpTests
{
    static void ResourceRewards(TcpTestResources resources)
    {
        var catalog = new TagEvildomRewardCatalog(resources.Archive, resources.ItemResources.Boosters);
        That(catalog.WeeklyLimit == 3 && catalog.Hard.Select(row => (row.DefinitionId, row.Weight)).SequenceEqual(new[]
        {
            (490012775,9000), (490012776,400), (490012777,100), (490012778,100),
            (490012779,100), (490012780,100), (490012781,100), (490012799,100)
        }), "本版再战困难奖励完整原始顺序与权重，不把定义误当实物");
        var easy = TagEvildomRewardPolicy.Prepare(catalog, 400000095, _ => 9999);
        That(easy.DefinitionId == 490012774 && easy.Items.SequenceEqual(new TagEvildomRewardItem[] { new(10309367,48), new(10309368,48) }),
            "简单奖励展开两种材料各48");
        int[] starts = [0,9000,9400,9500,9600,9700,9800,9900];
        int[] extra = [0,0,490012783,490012785,490012787,490012789,490012791,490012793];
        for (int index = 0; index < starts.Length; index++)
        {
            var selected = TagEvildomRewardPolicy.Prepare(catalog, 9608, _ => starts[index]);
            var expected = new List<TagEvildomRewardItem> { new(10309367,index == 1 ? 135 : 81), new(10309368,index == 1 ? 135 : 81) };
            if (extra[index] != 0) expected.Add(new(extra[index],1));
            That(selected.DefinitionId == catalog.Hard[index].DefinitionId && selected.Items.SequenceEqual(expected),
                "真实困难奖励确定产物保留数量/附加项，不丢第二材料或稀有项");
            var attachments = TagEvildomRewardPolicy.Materialize(selected, resources.ItemResources.Items, DateTimeOffset.UnixEpoch.AddSeconds(1_000));
            That(attachments.Select(item => (item.Core.ItemId, item.Core.Count)).SequenceEqual(expected.Select(item => (item.ItemId, item.Count))) &&
                attachments.All(item => item.Uid == 0 && item.Core.ExpireTime == 0),
                "真实PVF材料和宝珠生成准确永久核心，不发结算定义、金币或契约替代品");
        }
    }
}
