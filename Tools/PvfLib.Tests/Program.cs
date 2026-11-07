using PvfLib.Tests;

if (args is ["--pcroom-timepoint"])
{
    PcRoomTimePointFileTests.Run();
    Console.WriteLine($"PASS {TestCheck.Count} PvfLib pcroom timepoint assertions");
    return 0;
}
if (args.Length > 0)
{
    if (args.Length is not (2 or 3) || args[0] != "--benchmark" ||
        args.Length == 3 && (!int.TryParse(args[2], out int count) || count < 1))
    {
        Console.Error.WriteLine("用法：无参数运行合成测试；--benchmark <Script.pvf> [读取文件数，默认5000] 测量只读性能");
        return 2;
    }
    PvfBenchmark.Run(args[1], args.Length == 3 ? int.Parse(args[2]) : 5000);
    return 0;
}

PvfArchiveTests.Run();
ArchiveHeaderFormatTests.Run();
ArchiveIndexTests.Run();
ArchiveBoundaryTests.Run();
NumericTextTests.Run();
StringTextTests.Run();
DocumentReadDiagnosticsTests.Run();
ScriptTests.Run();
ItemModelTests.Run();
EquipmentAuraAbilityTests.Run();
EquipmentQuestDropBonusTests.Run();
EquipmentUpgradeFileTests.Run();
StackableActionTests.Run();
RandomOptionFileTests.Run();
AmplificationFileTests.Run();
AvatarTypeSelectionTests.Run();
MileageShopFileTests.Run();
TutorialFileTests.Run();
ChatEmoticonFileTests.Run();
RecoveryConsumableTests.Run();
RecipeFileTests.Run();
EquipmentEmancipationTests.Run();
ExpertRecipeFileTests.Run();
AlchemistPotionInfoTests.Run();
CreatureFileTests.Run();
PvpPrivateCharacterFileTests.Run();
TownEpisodeFileTests.Run();
OperationHopeFileTests.Run();
EvildomWarFileTests.Run();
CustomAbilityFileTests.Run();
TalismanFileTests.Run();
LukeCardSetFileTests.Run();
RaidObjectGeometryTests.Run();
PcRoomTimePointFileTests.Run();
Console.WriteLine($"PASS {TestCheck.Count} PvfLib assertions");
return 0;
