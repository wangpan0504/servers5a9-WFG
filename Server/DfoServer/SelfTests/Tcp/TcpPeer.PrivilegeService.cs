using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal sealed partial class TcpPeer
{
    internal byte[] PrivilegeServiceInformation { get; private set; } = [];

    void ReadPrivilegeServiceInitialization()
    {
        byte[] roster = Expect(0, 1643).Body;
        That(roster.Length == 62 * 8, "礼遇名册严格初始化62个8字节角色槽");
        for (int index = 0; index < 62; index++)
            That((U32(roster, index * 8) == uint.MaxValue || U32(roster, index * 8) == index) &&
                U32(roster, index * 8 + 4) <= 2, "礼遇名册不冒用角色UID或附加未知状态");
        PrivilegeServiceInformation = Expect(0, 1644).Body;
        That(PrivilegeServiceInformation.Length == 21 && U32(PrivilegeServiceInformation, 0) <= 2 &&
            PrivilegeServiceInformation[8] <= 1 &&
            (U32(PrivilegeServiceInformation, 0) == 0 || U32(PrivilegeServiceInformation, 4) == 0),
            "礼遇本人状态完整21字节，未购买或过期不残留有效期限");
    }
}
