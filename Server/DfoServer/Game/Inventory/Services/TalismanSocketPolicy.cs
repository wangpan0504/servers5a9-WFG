using DfoServer.Game.Inventory.Models;

namespace DfoServer.Game.Inventory.Services;

internal static class TalismanSocketPolicy
{
    // PVF没有孔色生成权重；服务端创建和重锻统一五色等权、不重复。
    // 这不是旧实例合法性的附加限制，同色旧孔仍完整保存。
    public static TalismanSocketColors Generate(Func<int, int>? random = null)
    {
        var draw = random ?? Random.Shared.Next;
        Span<byte> colors = [1, 2, 3, 4, 5];
        for (int i = 0; i < 3; i++)
        {
            int offset = draw(colors.Length - i);
            if (offset < 0 || offset >= colors.Length - i)
                throw new InvalidDataException("护石孔色随机结果越界");
            int selected = i + offset;
            (colors[i], colors[selected]) = (colors[selected], colors[i]);
        }
        return new(colors[0], colors[1], colors[2]);
    }
}
