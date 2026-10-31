using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace DfoServer.Network.Management;

internal static partial class GmWebServer
{
    static void MapClientPages(WebApplication app)
    {
        string page = Assets.Text("client.includeImg.htm");
        byte[] background = Assets.Bytes("client.includeImg_bg.png");
        byte[] banner = Assets.Bytes("client.banner_top.jpg");
        app.MapGet("/favicon.ico", () => Results.NoContent());
        app.MapGet("/client/includeImg.htm", () => Results.Text(page, "text/html; charset=utf-8"));
        app.MapGet("/client/includeImg_bg.png", () => Results.Bytes(background, "image/png"));
        app.MapGet("/client/banner_top.jpg", () => Results.Bytes(banner, "image/jpeg"));
    }
}
