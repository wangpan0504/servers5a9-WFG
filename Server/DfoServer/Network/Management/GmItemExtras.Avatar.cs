using DfoServer.Network.Management.Avatar;
using DfoServer.Network.Management.ImagePack;

namespace DfoServer.Network.Management;

// 纸娃娃：用同一份已加载 PVF 与客户端 ImagePacks2 合成站立帧 PNG。只读，试穿只替换内存中的部位。
internal sealed partial class GmItemExtras
{
    // 资源缺失、层名无法解析时的日志出口（服务进程内接 server.jsonl），也会随诊断 JSON 返回。
    public Action<string>? AvatarLog { get; init; }

    sealed class LibraryImages(ImagePackLibrary library) : IAvatarImages
    {
        public bool TryReadFrame(string imgPath, int frame, int palette, out ImgFrame? result)
        {
            bool ok = library.TryReadFrame(imgPath, frame, palette, out var read);
            result = read;
            return ok;
        }

        public int PaletteCount(string imgPath) => library.PaletteCount(imgPath);
    }

    string? ReadPvf(string path)
    {
        try { return pvf.ReadText(path); }
        catch (Exception ex) when (ex is FileNotFoundException or KeyNotFoundException or InvalidDataException) { return null; }
    }

    string? EquipmentScriptPath(int id) => paths.Value.TryGetValue(id, out var entry) && entry.Equipment ? "equipment/" + entry.Path : null;

    // ImagePacks2 不可用时返回 null。
    public AvatarPortrait? Portrait(AvatarLookInput input, IReadOnlyList<int> tryOn)
    {
        if (images.Value is not { } library) return null;
        return new AvatarPortraitBuilder(ReadPvf, EquipmentScriptPath, new LibraryImages(library), AvatarLog).Build(input, tryOn);
    }
}
