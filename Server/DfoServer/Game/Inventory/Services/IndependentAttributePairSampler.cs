using System.Security.Cryptography;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Inventory.Services;

internal static class IndependentAttributePairSampler
{
    public static (int ItemId, byte Ordinal) Next(IndependentAttributePairPolicy policy, Func<long, long> draw)
    {
        int first = Draw();
        int second = Draw();
        return (policy.ResultItem(first, second), checked((byte)(11 * first + second)));

        int Draw()
        {
            long value = draw(IndependentAttributePairPolicy.Bound);
            if (value is < 0 or >= IndependentAttributePairPolicy.Bound)
                throw new InvalidDataException("独立属性抽样值越界");
            return (int)value;
        }
    }

    public static AttributePairSelectionSummary Describe(IndependentAttributePairPolicy policy, byte[] trace) =>
        new("independent-attribute-pair", policy.Identity, trace.Length, IndependentAttributePairPolicy.Bound,
            checked(2 * trace.Length), trace.Length, Convert.ToHexString(SHA256.HashData(trace)));
}
