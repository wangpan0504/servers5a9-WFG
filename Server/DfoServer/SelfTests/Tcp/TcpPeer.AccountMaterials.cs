namespace DfoServer.Testing;

internal sealed partial class TcpPeer
{
    public Dictionary<int, int> AccountMaterials { get; } = [];
    public byte[] RaidAccountMaterialLimits { get; private set; } = [];
    public byte[] RaidCharacterMaterialLimits { get; private set; } = [];

    void ReadAccountMaterialInitialization()
    {
        AccountMaterials.Clear();
        var packet = Expect(0, 563);
        int frames = 0;
        do
        {
            var body = packet.Body;
            TestCheck.That(body.Length >= 2 && body[0] == (frames == 0 ? 0 : 1) &&
                body.Length == 2 + body[1] * 8 && (frames == 0 || body[1] != 0),
                "563完整初始化先清旧账号，后续u8分片仅增量");
            for (int i = 0; i < body[1]; i++)
            {
                int id = checked((int)U32(body, 2 + i * 8));
                int balance = checked((int)U32(body, 6 + i * 8));
                TestCheck.That(id > 0 && balance > 0 && AccountMaterials.TryAdd(id, balance), "563全量余额真实且无重复模板");
            }
            frames++;
            packet = Read();
        } while (packet is { Kind: 0, Opcode: 563 });
        TestCheck.That(packet is { Kind: 0, Opcode: 1460 }, "账号材料全量之后独立初始化账号攻坚周额度");
        RaidAccountMaterialLimits = ValidateLimits(packet.Body);
        RaidCharacterMaterialLimits = ValidateLimits(Expect(0, 1461).Body);
        if (fixture is null) return;
        var expected = fixture.Query("SELECT m.item_id,m.balance FROM account_material_balances m JOIN characters c ON c.account=m.account WHERE c.id=$p0 AND m.balance>0 ORDER BY m.item_id", Id);
        TestCheck.That(expected.Length == AccountMaterials.Count && expected.All(row =>
            AccountMaterials.GetValueOrDefault(Convert.ToInt32(row[0]), -1) == Convert.ToInt32(row[1])),
            "563读取同一账号实际已提交余额，不复制角色实体或读取其他账号");

        static byte[] ValidateLimits(byte[] body)
        {
            TestCheck.That(body.Length >= 2 && body.Length == 2 + U16(body, 0) * 12, "1460/1461完整u16条数与三u32行");
            var keys = new HashSet<uint>();
            for (int i = 0; i < U16(body, 0); i++)
                TestCheck.That(U32(body, 2 + i * 12) > 0 && keys.Add(U32(body, 2 + i * 12)) &&
                    U32(body, 10 + i * 12) > 0, "1460/1461各模板有真实正上限且不重复");
            return body;
        }
    }
}
