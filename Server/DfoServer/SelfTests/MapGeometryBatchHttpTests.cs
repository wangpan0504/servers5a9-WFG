using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using DfoServer.Network.Management;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class MapGeometryBatchHttpTests
{
    static byte[] Request(byte[] version, params string[] paths)
    {
        using var bytes = new MemoryStream(); using var writer = new BinaryWriter(bytes, Encoding.UTF8, true);
        writer.Write("DGB1"u8); writer.Write(version); writer.Write(checked((ushort)paths.Length));
        foreach (string path in paths) { byte[] text = Encoding.UTF8.GetBytes(path); writer.Write(checked((ushort)text.Length)); writer.Write(text); }
        return bytes.ToArray();
    }
    static Task<HttpResponseMessage> Post(HttpClient client, byte[] body, string type = "application/octet-stream")
    {
        var content = new ByteArrayContent(body); content.Headers.ContentType = new MediaTypeHeaderValue(type);
        return client.PostAsync(GmWebServer.GeometryBatchRoute, content);
    }
    public static async Task RunCoreAsync()
    {
        int checks = Count;
        byte[] a = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray(), b = a.Reverse().ToArray();
        var source = new Dictionary<string, byte[]> { ["map/b.map"] = b, ["map/a.map"] = a };
        var first = new MapGeometryHttpSnapshot(source);
        That(first.Version.SequenceEqual(new MapGeometryHttpSnapshot(source.Reverse()).Version), "快照版本不依赖字典枚举顺序");
        byte[] version = first.Version; source["map/a.map"][0] ^= 1;
        That(first.Version.SequenceEqual(version) && first.Signature("map/a.map")![0] == 0, "快照克隆输入签名");
        byte[] outside = first.Signature("map/a.map")!; outside[0] = 99;
        That(first.Signature("map/a.map")![0] == 0, "查询结果不可改写快照");
        var library = new MapGeometryHttpSnapshot(Enumerable.Range(0, 32769)
            .Select(i => new KeyValuePair<string, byte[]>($"map/library{i}.map", b)));
        That(library.Version.Length == 32 && library.Signature("map/library32768.map") is { Length: 32 }, "全库可超过单依赖图32768节点预算");
        using (var expected = new MemoryStream())
        {
            using var writer = new BinaryWriter(expected, Encoding.UTF8, true);
            writer.Write("DGV1"u8); writer.Write(2);
            foreach (var row in new[] { ("map/a.map", Enumerable.Range(0, 32).Select(i => (byte)i).ToArray()), ("map/b.map", b) })
            { byte[] path = Encoding.UTF8.GetBytes(row.Item1); writer.Write((ushort)path.Length); writer.Write(path); writer.Write(row.Item2); }
            writer.Flush(); That(version.SequenceEqual(SHA256.HashData(expected.ToArray())), "版本摘要包含长度前缀路径和32字节签名");
        }
        MapGeometryHttpSnapshot? current = first;
        var builder = WebApplication.CreateSlimBuilder(); builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        GmWebServer.MapClientGeometry(app, path => first.Signature(path));
        GmWebServer.MapClientGeometryBatch(app, () => current);
        await app.StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
            That((await client.GetByteArrayAsync("/client/displaycompat-world?map=map/a.map")).SequenceEqual(first.Signature("map/a.map")!), "旧GET原32字节合同不变");
            That((await client.GetByteArrayAsync(GmWebServer.GeometryVersionRoute)).SequenceEqual(version), "版本探测返回真实快照摘要");
            using var response = await Post(client, Request(version, "map/b.map", "map/a.map"));
            That(response.StatusCode == HttpStatusCode.OK, "批量只读请求成功");
            using (var reader = new BinaryReader(new MemoryStream(await response.Content.ReadAsByteArrayAsync()), Encoding.UTF8))
            {
                That(reader.ReadBytes(4).SequenceEqual("DGS1"u8.ToArray()) && reader.ReadBytes(32).SequenceEqual(version) && reader.ReadUInt16() == 2, "批量响应头版本和数量");
                foreach (var row in new[] { ("map/b.map", b), ("map/a.map", first.Signature("map/a.map")!) })
                    That(Encoding.UTF8.GetString(reader.ReadBytes(reader.ReadUInt16())) == row.Item1 && reader.ReadBytes(32).SequenceEqual(row.Item2), "响应路径与签名严格保留请求顺序");
                That(reader.BaseStream.Position == reader.BaseStream.Length, "响应没有尾随数据");
            }
            byte[] badUtf8 = Request(version, "map/a.map"); badUtf8[^1] = 0xc0;
            foreach (byte[] malformed in new[]
            {
                Array.Empty<byte>(), Request(version), Request(version, "map/a.map", "map/a.map"), Request(version, "map/../bad.map"),
                Request(version, "map/a.map").Concat(new byte[] { 0 }).ToArray(), Request(version, "map/a.map")[..^1],
                Request(version, Enumerable.Repeat("map/a.map", 65).ToArray()), badUtf8
            })
            { using var rejected = await Post(client, malformed); That(rejected.StatusCode == HttpStatusCode.BadRequest, "非法/重复/超条数/尾随/截断请求拒绝"); }
            using (var missing = await Post(client, Request(version, "map/a.map", "map/missing.map")))
                That(missing.StatusCode == HttpStatusCode.NotFound, "任何缺项拒绝整批，不返回部分许可");
            using (var wrongType = await Post(client, Request(version, "map/a.map"), "text/plain"))
                That((int)wrongType.StatusCode == 415, "批量请求仅接受二进制内容类型");
            using (var oversized = await Post(client, new byte[GmWebServer.GeometryBatchRequestLimit + 1]))
                That((int)oversized.StatusCode == 413, "请求体预算在解析前拒绝");
            var changed = new MapGeometryHttpSnapshot(source); current = changed;
            using (var stale = await Post(client, Request(version, "map/a.map")))
                That(stale.StatusCode == HttpStatusCode.Conflict, "探测到发送之间快照换代拒绝旧版本");
            That(!(await client.GetByteArrayAsync(GmWebServer.GeometryVersionRoute)).SequenceEqual(version), "版本探测实际反映换代");
            current = null;
            That((int)(await client.GetAsync(GmWebServer.GeometryVersionRoute)).StatusCode == 503, "无快照不伪造版本");
            using (var unavailable = await Post(client, Request(version, "map/a.map"))) That((int)unavailable.StatusCode == 503, "无快照不复用旧批量许可");
            string unicode = "map/" + new string('图', 400) + ".map";
            That(MapGeometryHttpSnapshot.ValidPath(unicode), "路径预算保留480字符的合法UTF8路径");
            current = new MapGeometryHttpSnapshot(new[] { new KeyValuePair<string, byte[]>(unicode, b) });
            using (var utf8 = await Post(client, Request(current.Version, unicode)))
                That(utf8.StatusCode == HttpStatusCode.OK, "合法多字节路径通过实际HTTP批量接口");
            That(!MapGeometryHttpSnapshot.ValidPath("map/" + new string('a', 481)), "字符预算超限拒绝");
        }
        finally { await app.StopAsync(); }
        Console.WriteLine($"PASS map geometry batch/version HTTP {Count - checks}");
    }
}
