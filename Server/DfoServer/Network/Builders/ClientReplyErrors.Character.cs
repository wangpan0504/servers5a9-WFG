namespace DfoServer.Network.Builders;

// 值为所属业务 ACK 的错误码，不是 dstr 索引；由 ClientFailurePackets 绑定 opcode 和完整失败正文。
// 原文以 JSON 字符串表示；同一码的不同文本仍由客户端模式及当前状态选择，不能指定其中某一句。

// CMD692，匹配消费者 0233B8F0。
internal enum ClientCharacterNameError : ushort
{
    FatalDiagnostic = 2, // dstr 33194: "发生致命错误"
        // 此分支仅写客户端诊断，不保证可见提示。
    DuplicateLegacy = 20, // dstr 33195: "名称重复。 "
    InvalidSpecialCharacters = 23, // dstr 1122: "含有无法使用的特殊字符"
    Duplicate = 24, // dstr 33195: "名称重复。 "
    UnusableText = 159 // dstr 33196: "包含无法使用的文字"
}
