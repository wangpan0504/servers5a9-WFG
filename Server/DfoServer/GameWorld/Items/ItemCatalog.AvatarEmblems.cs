using System.Collections.Concurrent;
using PvfLib;

namespace DfoServer.GameWorld.Items;

internal sealed partial class ItemCatalog
{
    readonly ConcurrentDictionary<int, Lazy<AvatarEmblemTargetDescriptor?>> avatarEmblemTargets = new();
    readonly ConcurrentDictionary<int, Lazy<AvatarEmblemMaterialDescriptor?>> avatarEmblemMaterials = new();

    public AvatarEmblemTargetDescriptor? AvatarEmblemTarget(int id)
    {
        if (!equipment.TryGetValue(id, out string? path)) return null;
        return avatarEmblemTargets.GetOrAdd(id, key => new(() => ReadAvatarEmblemTarget(key, path))).Value;
    }

    public AvatarEmblemMaterialDescriptor? AvatarEmblemMaterial(int id)
    {
        if (!stackable.TryGetValue(id, out string? path)) return null;
        return avatarEmblemMaterials.GetOrAdd(id, key => new(() => ReadAvatarEmblemMaterial(key, path))).Value;
    }

    AvatarEmblemTargetDescriptor? ReadAvatarEmblemTarget(int id, string path)
    {
        var document = new PvfTokenDocument(pvf.ReadTokens("equipment/" + path), pvf.ResolveString);
        if (!AvatarEmblemDeclaredType(document, "equipment type", id).EndsWith(" avatar]", StringComparison.Ordinal))
            return null;
        // 直接读取同份原始声明，不经过ItemTemplate.Jobs的缺省all或未知标签异常。
        return AvatarEmblemResourceDefinition.ParseDirectTarget(id, document, pvf.ResolveString);
    }

    AvatarEmblemMaterialDescriptor? ReadAvatarEmblemMaterial(int id, string path)
    {
        var document = new PvfTokenDocument(pvf.ReadTokens("stackable/" + path), pvf.ResolveString);
        if (AvatarEmblemDeclaredType(document, "stackable type", id) != "[avatar emblem]")
            return null;
        return AvatarEmblemResourceDefinition.ParseDirectMaterial(id, document, pvf.ResolveString);
    }

    string AvatarEmblemDeclaredType(PvfTokenDocument document, string name, int id)
    {
        var declaration = document.Sections.LastOrDefault(section => section.Name == name);
        if (declaration is null || declaration.Values.Length == 0 || declaration.Values[0].Type != 6)
            throw new InvalidDataException("C201资源类型声明无效：" + id + " / " + name);
        return pvf.ResolveString(declaration.Values[0].Value);
    }
}
