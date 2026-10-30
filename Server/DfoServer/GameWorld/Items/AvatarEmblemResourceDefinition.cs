using System.Collections.Immutable;
using PvfLib;

namespace DfoServer.GameWorld.Items;

// 只解释已绑定的direct声明；原生import覆盖规则和材料非空L2来源另有证据边界。
internal static class AvatarEmblemResourceDefinition
{
    static readonly string[] JobLabels =
    [
        "[swordman]", "[fighter]", "[gunner]", "[mage]", "[priest]", "[at gunner]", "[thief]", "[at fighter]",
        "[at mage]", "[demonic swordman]", "[creator mage]", "[at swordman]", "[knight]", "[demonic lancer]",
        "[at priest]", "[gun blader]"
    ];

    public static AvatarEmblemTargetDescriptor ParseDirectTarget(int itemId,
        PvfTokenDocument document, Func<int, string> resolve)
    {
        RequireDirect(document);
        string type = TypeLabel(document, "equipment type", resolve);
        if (!type.EndsWith(" avatar]", StringComparison.Ordinal))
            throw new InvalidDataException("目标不在direct时装资源解析范围：" + itemId);

        ushort flags = 0;
        foreach (var section in document.Sections.Where(section => section.Name == "usable job"))
            foreach (var value in section.Values)
            {
                if (value.Type != 6)
                    throw new InvalidDataException("目标usable job不是字符串声明：" + itemId);
                int index = JobIndex(resolve(value.Value));
                flags = index == 16 ? ushort.MaxValue : (ushort)(flags | 1 << index);
            }
        return new(itemId, ItemKind.Avatar, flags);
    }

    public static AvatarEmblemMaterialDescriptor ParseDirectMaterial(int itemId,
        PvfTokenDocument document, Func<int, string> resolve)
    {
        RequireDirect(document);
        if (TypeLabel(document, "stackable type", resolve) != "[avatar emblem]")
            throw new InvalidDataException("材料不在direct徽章资源解析范围：" + itemId);
        // 尚未绑定stackable的非空L2生产；不能将装备buff option规则跨类型套用。
        if (document.Sections.Any(section => section.Name == "buff option"))
            throw new InvalidDataException("direct徽章包含尚未绑定的buff option来源：" + itemId);

        bool hasMetadata35 = false;
        ImmutableArray<int> primary = [];
        ushort socketMask = 0;
        foreach (var section in document.Sections)
        {
            if (section.Name == "enchant")
            {
                hasMetadata35 = true;
                foreach (var child in section.Children.Where(child => child.Name == "skill levelup"))
                {
                    if (child.Values.Length % 3 != 0 || !child.Children.IsEmpty)
                        throw new InvalidDataException("徽章skill levelup行不完整：" + itemId);
                    var rows = ImmutableArray.CreateBuilder<int>();
                    for (int at = 0; at < child.Values.Length; at += 3)
                    {
                        if (child.Values[at].Type != 6 || child.Values[at + 1].Type != 0 || child.Values[at + 2].Type != 0)
                            throw new InvalidDataException("徽章skill levelup行类型无效：" + itemId);
                        rows.Add(JobIndex(resolve(child.Values[at].Value)));
                    }
                    // 每个skill levelup handler先清L1；后续enchant若无此段则保留原L1。
                    primary = rows.ToImmutable();
                }
            }
            else if (section.Name == "avatar emblem target type")
            {
                foreach (var value in section.Values)
                {
                    if (value.Type != 6)
                        throw new InvalidDataException("徽章孔型不是字符串声明：" + itemId);
                    socketMask |= resolve(value.Value) switch
                    {
                        "[A socket]" => (ushort)1,
                        "[B socket]" => (ushort)2,
                        "[C socket]" => (ushort)4,
                        "[D socket]" => (ushort)8,
                        "[S socket]" => (ushort)16,
                        "[M socket]" => (ushort)239,
                        _ => throw new InvalidDataException("徽章孔型声明尚未绑定：" + itemId)
                    };
                }
            }
        }
        return new(itemId, ItemKind.Emblem, socketMask, hasMetadata35, primary, []);
    }

    static int JobIndex(string label)
    {
        int index = Array.IndexOf(JobLabels, label);
        return index < 0 ? 16 : index;
    }

    static string TypeLabel(PvfTokenDocument document, string name, Func<int, string> resolve)
    {
        var declaration = document.Sections.LastOrDefault(section => section.Name == name);
        if (declaration is null || declaration.Values.Length == 0 || declaration.Values[0].Type != 6)
            throw new InvalidDataException("缺少direct资源类型声明：" + name);
        return resolve(declaration.Values[0].Value);
    }

    static void RequireDirect(PvfTokenDocument document)
    {
        if (document.Sections.Any(section => section.Name is "import" or "import script"))
            throw new InvalidDataException("尚未绑定原生import合并或覆盖规则，不能作为direct资源解析");
    }
}
