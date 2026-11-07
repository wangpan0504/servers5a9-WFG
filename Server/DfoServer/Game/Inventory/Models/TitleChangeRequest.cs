namespace DfoServer.Game.Inventory.Models;

// CMD970 USE_TITLE_CHANGE_ITEM：客户端 022EA7A0 依次写入变更道具主背包槽位 (i16) 与目标称号主背包槽位 (i16)。
internal readonly record struct TitleChangeRequest(short ThreadSlot, short TitleSlot);

// ACK970 成功回包所需的判定分支、变换后称号编号与消耗的变更道具编号。
internal readonly record struct TitleChangeOutcome(bool IsSuccess, int ResultTitleItemId, int UsedThreadItemId);
