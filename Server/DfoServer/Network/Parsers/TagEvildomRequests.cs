using System.Buffers.Binary;
using System.Collections.Immutable;
using DfoServer.Game.TagEvildom;

namespace DfoServer.Network.Parsers;

// 015475F1..015476EC 按完整双角色队填3×2×9B；未选项由0153A3D0写(0,-1,0)。
// 75B是请求正文：03BDF610另写13B帧头，03BDF2C0累加75B，03BE0FF0最后再加帧头。
internal sealed record TagEvildomSettingRequest(ImmutableArray<TagEvildomMemberRequest> Members, int SelectedTeam);

internal static class TagEvildomRequests
{
    internal static uint Entry(ReadOnlySpan<byte> body)
    {
        // 0153A355..379：CMD15与普通4B不同，u32目标后还有u16=0；后续同栈直接发CMD16。
        if (body.Length != 6 || BinaryPrimitives.ReadUInt16LittleEndian(body[4..]) != 0)
            throw new InvalidDataException("再战魔界选图入口正文必须是目标加零u16");
        uint dungeon = BinaryPrimitives.ReadUInt32LittleEndian(body);
        if (dungeon is not (9608 or 400000095)) throw new InvalidDataException("再战魔界选图目标不匹配");
        return dungeon;
    }

    internal static TagEvildomSettingRequest Setting(ReadOnlySpan<byte> body)
    {
        if (body.Length != 75) throw new InvalidDataException("再战魔界编队请求正文必须是75字节");
        var members = ImmutableArray.CreateBuilder<TagEvildomMemberRequest>(6);
        for (int index = 0; index < 6; index++)
        {
            var row = body.Slice(13 + index * 9, 9);
            members.Add(new(row[0], BinaryPrimitives.ReadInt32LittleEndian(row[1..]), BinaryPrimitives.ReadUInt32LittleEndian(row[5..])));
        }
        // 原生构造不初始化前13B，末4B语义也未确认；它们既不授权角色，也不提供通关事实。
        return new(members.MoveToImmutable(), BinaryPrimitives.ReadInt32LittleEndian(body[67..]));
    }

    internal static void Initialize(ReadOnlySpan<byte> body)
    {
        // 0176C01C写opcode1663后直接结束包，没有正文。
        if (!body.IsEmpty) throw new InvalidDataException("再战魔界队伍初始化正文必须为空");
    }
}
