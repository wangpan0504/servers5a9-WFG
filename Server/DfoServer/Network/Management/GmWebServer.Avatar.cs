using System.Text.RegularExpressions;
using DfoServer.Network.Management.Avatar;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace DfoServer.Network.Management;

// 纸娃娃结果：Portrait 为 null 时 Error 是 "ERR 原因"（角色不存在、ImagePacks2 不可用等）。
internal sealed record GmAvatarResult(AvatarPortrait? Portrait, string? Error);

// 纸娃娃（只读 GET，不改库、不审计）：
//   /api/characters/{account}/{name}/portrait[?tryOn=id,id]          → image/png；没有任何可画图层时 204
//   /api/characters/{account}/{name}/portrait/layers[?tryOn=id,id]   → JSON：尺寸、帧号、职业标签、逐层来源/IMG/调色板序号/是否画出、说明
// tryOn 为最多 16 个物品编号（逗号分隔），按各物品自己的部位在内存中替换，不写库。可试穿列表复用 /api/items/search（tag=coat avatar 等，job=职业）。
internal static partial class GmWebServer
{
    static readonly Regex TryOnList = new(@"^[0-9]{1,10}(,[0-9]{1,10}){0,15}$", RegexOptions.Compiled);

    static void MapAvatar(WebApplication app, IGmWebData data)
    {
        app.MapGet("/api/characters/{account}/{name}/portrait", (string account, string name, string? tryOn, HttpContext http) =>
        {
            if (Portrait(data, account, name, tryOn, out var result) is { } bad) return bad;
            if (result!.Png is null) return Results.NoContent();
            http.Response.Headers["X-Avatar-Layers"] = result.Layers.Count(layer => layer.Drawn) + "/" + result.Layers.Count;
            return Results.File(result.Png, "image/png");
        });
        app.MapGet("/api/characters/{account}/{name}/portrait/layers", (string account, string name, string? tryOn) =>
        {
            if (Portrait(data, account, name, tryOn, out var result) is { } bad) return bad;
            return Results.Json(new
            {
                ok = true,
                data = new
                {
                    width = result!.Width, height = result.Height, frame = result.Frame, jobTag = result.JobTag, drawn = result.Png is not null,
                    layers = result.Layers.Select(layer => new { slot = layer.Slot, itemId = layer.ItemId, source = layer.Source, layer = layer.Layer, z = layer.Z, img = layer.Img, palette = layer.Palette, frame = layer.Frame, drawn = layer.Drawn, note = layer.Note }),
                    notes = result.Notes
                }
            });
        });
    }

    static IResult? Portrait(IGmWebData data, string account, string name, string? tryOn, out AvatarPortrait? portrait)
    {
        portrait = null;
        if (!Token.IsMatch(account) || !Token.IsMatch(name)) return Bad("账号或角色名无效");
        if (tryOn is { Length: > 0 } && !TryOnList.IsMatch(tryOn)) return Bad("试穿编号须为逗号分隔的正整数，最多 16 个");
        int[] ids = string.IsNullOrEmpty(tryOn) ? [] : tryOn.Split(',').Select(value => int.TryParse(value, out int id) ? id : 0).ToArray();
        if (ids.Any(id => id <= 0)) return Bad("试穿编号须为正整数");
        var result = data.AvatarPortrait(account, name, ids);
        if (result.Portrait is null) return Reply(result.Error ?? "ERR 纸娃娃不可用");
        portrait = result.Portrait;
        return null;
    }
}
