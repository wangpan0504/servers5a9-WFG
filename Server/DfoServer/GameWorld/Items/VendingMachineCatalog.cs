using System.Collections.Immutable;
using PvfLib;

namespace DfoServer.GameWorld.Items;

internal sealed record VendingPrize(int ItemId, int Weight, int Count, bool Broadcast);
internal sealed record VendingMachine(int Id, int NpcId, int Group, int CoinId, int CoinCount,
    ImmutableArray<VendingPrize> Prizes)
{
    public long TotalWeight => Prizes.Sum(row => (long)row.Weight);
}

internal sealed class VendingMachineCatalog
{
    readonly ImmutableDictionary<int, VendingMachine> machines;
    public IEnumerable<VendingMachine> All => machines.Values;
    public VendingMachine? Find(int id) => machines.GetValueOrDefault(id);

    public VendingMachineCatalog(PvfArchive archive)
    {
        var index = PvfScript.List(archive.ReadText("etc/vendingmachine.lst"));
        machines = new[] { (Id: 1, Npc: 80), (Id: 3, Npc: 112) }.Select(row =>
            Parse(row.Id, row.Npc, archive.ReadText("etc/" + index[row.Id]))).ToImmutableDictionary(row => row.Id);
    }

    internal static VendingMachine Parse(int id, int npcId, string text)
    {
        string group = PvfScript.Block(text, "item group");
        int[] number = PvfScript.Integers(PvfScript.Section(group, "group num"));
        int[] material = PvfScript.Integers(PvfScript.Section(group, "material"));
        int[] output = PvfScript.Integers(PvfScript.Block(group, "output"));
        if (number is not [1] || material is not [> 0, > 0] || output.Length == 0 || output.Length % 4 != 0)
            throw new InvalidDataException("售货机奖组、费用或奖品行宽无效");
        var prizes = ImmutableArray.CreateBuilder<VendingPrize>();
        for (int i = 0; i < output.Length; i += 4)
        {
            if (output[i] <= 0 || output[i + 1] < 0 || output[i + 2] <= 0 || output[i + 3] is not (0 or 1))
                throw new InvalidDataException("售货机奖品字段无效");
            // 同名奖品的不同数量、权重及零权重行均保留原序。
            prizes.Add(new(output[i], output[i + 1], output[i + 2], output[i + 3] == 1));
        }
        var machine = new VendingMachine(id, npcId, number[0], material[0], material[1], prizes.ToImmutable());
        if (machine.TotalWeight <= 0) throw new InvalidDataException("售货机没有可抽取奖品");
        return machine;
    }
}
