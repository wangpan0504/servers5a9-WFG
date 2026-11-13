namespace DfoServer.Testing;

internal sealed partial class TcpPeer
{
    public Dictionary<int, int> AccountMaterials { get; } = [];
    public byte[] RaidAccountMaterialLimits { get; private set; } = [];
    public byte[] RaidCharacterMaterialLimits { get; private set; } = [];
    internal Action<TcpPeer>? BeforeAccountMaterialInitialization { get; set; }

    sealed record AccountMaterialInitializationExpectation(ushort Character, string Account, Dictionary<int, int> Balances);

    AccountMaterialInitializationExpectation? CaptureAccountMaterialInitialization(ushort rosterIndex)
    {
        if (fixture is null) return null;
        TestCheck.That(loginAccount is not null, "563预期必须绑定本连接实际登录账号");
        // C4参数是按slot排序的名册行索引；先选角色，再连接完整钱包，不能对材料行OFFSET。
        var rows = fixture.Query("""
            SELECT c.id,c.account,m.item_id,m.balance
            FROM (SELECT id,account FROM characters WHERE account=$p0 ORDER BY slot LIMIT 1 OFFSET $p1) c
            LEFT JOIN account_material_balances m ON m.account=c.account AND m.balance>0
            ORDER BY m.item_id
            """, loginAccount!, rosterIndex);
        TestCheck.That(rows.Length > 0 && rows.All(row => Equals(row[0], rows[0][0]) &&
            string.Equals((string)row[1], loginAccount, StringComparison.Ordinal)),
            "563预期只冻结实际登录账号在请求名册行的同一角色");
        var balances = rows.Where(row => row[2] is not DBNull)
            .ToDictionary(row => Convert.ToInt32(row[2]), row => Convert.ToInt32(row[3]));
        return new(Convert.ToUInt16(rows[0][0]), (string)rows[0][1], balances);
    }

    void ReadAccountMaterialInitialization(AccountMaterialInitializationExpectation? expected)
    {
        BeforeAccountMaterialInitialization?.Invoke(this);
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
        if (expected is null) return;
        TestCheck.That(expected.Character == Id && expected.Account == loginAccount,
            "563初始化角色必须对应发选角前冻结的登录账号与请求名册行");
        TestCheck.That(expected.Balances.Count == AccountMaterials.Count && expected.Balances.All(row =>
            AccountMaterials.GetValueOrDefault(row.Key, -1) == row.Value),
            "563精确读取选角前同一账号已提交余额，不复制角色实体或读取其他账号");

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
