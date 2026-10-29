using ProbablyStolenStockMarket;
using ProbablyStolenStockMarket.Game;
using ProbablyStolenStockMarket.Localization;
using ProbablyStolenStockMarket.Market;

int checks = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
    checks++;
}
void Throws(Action action, string message)
{
    bool threw = false;
    try { action(); } catch (Exception ex) when (ex is InvalidOperationException or IOException) { threw = true; }
    Check(threw, message);
}
PlayerStore NewStore(int day = 5, int cash = 777)
{
    StoreStation.Day = day;
    var store = new PlayerStore { startType = NewGameData.StartType.MODDED, startScenarioId = StartingDebtScenario.Id, playerCash = cash };
    PlayerStore.Instance = store;
    return store;
}
void FinishNativeSetup(PlayerStore store)
{
    store.saveSlotId = 3; store.runID = "final-run-identity"; store.runNumber = 2;
    // 模拟原版在 onStart 之后应用天赋资金；实际适配必须等到这里之后再设置最终余额。
    store.playerCash += 400;
}

Localizer.Language = ModLanguage.Chinese;
StartingDebtScenario.Register();
var scenario = StartingScenario.Get(StartingDebtScenario.Id)!;
Check(scenario != null && scenario.source == ModInfo.Id, "未注册原版模组开局身份。");
Check(scenario!.id == ModInfo.Id + ".indebted_shopkeeper" && scenario.skipIntro, "未使用原版互斥开局标识或教程跳过开关。");
Check(scenario.tagTone == StartingScenario.TagTone.Danger && scenario.name() == "负债店主", "开局名称或挑战标记错误。");
Check(scenario.effects.Length == 4 && scenario.description().Contains("28"), "创建存档的规则说明不完整。");

// 原版回调发生在最终身份、天赋资金尚未就绪时，只能记下待处理标记。
{
    var store = NewStore();
    var bridge = new GameBridge();
    scenario.onStart();
    Check(store.GetCash() == 777 && store.modData!.Count == 1 && !bridge.IsAttached, "onStart 过早改钱或写入债务。");
    Check(store.modData![StartingDebtScenario.PendingKey] == "5", "首个营业日未记录。");
    Throws(bridge.PrepareSave, "没有最终存档身份却允许保存债务开局。");
    Check(store.GetCash() == 777 && store.modData.ContainsKey(StartingDebtScenario.PendingKey), "身份未就绪时改变了开局数据。");
    FinishNativeSetup(store);
    bridge.PrepareSave();
    var contract = bridge.Engine!.State.Loan.Contract!;
    Check(store.GetCash() == 1500 && contract.AmountToClose == 5000, "最终初始现金或固定欠款不符。");
    Check(contract.Source == LoanSource.StartingDebt && contract.StartDay == 5 && contract.DueDay == 32, "首日或期限未沿用 onStart 标记。");
    Check(bridge.Identity == "3:final-run-identity:2", "初始化未绑定最终存档身份。");
    Check(!store.modData.ContainsKey(StartingDebtScenario.PendingKey) && store.modData.ContainsKey(GameBridge.MarketSaveKey), "完成后未移除 pending 或未保存债务。");
    Check(bridge.Engine.State.Loan.ContactUnlocked && bridge.Engine.State.Loan.HasMetLender, "开局贷款联系人未解锁。");
    Check(!bridge.MonetaryOperation, "初始化后未释放现金事务标记。");

    string contractId = contract.Id;
    store.playerCash = 1300;
    bridge.PrepareSave();
    Check(store.GetCash() == 1300 && bridge.Engine.State.Loan.Contract!.Id == contractId, "重复保存重置现金或重新建债。");
    // 保存重试可能留下 pending；即使跨日，也必须保留已有还款结果与原期限。
    var payment = bridge.Engine.RepayLoan(100, bridge, () => { });
    Check(payment.Success, "测试前置部分还款失败。");
    store.modData[StartingDebtScenario.PendingKey] = "5";
    StoreStation.Day = 6;
    bridge.PrepareSave();
    Check(store.GetCash() == 1200 && bridge.Engine.State.Loan.Contract!.AmountToClose == 4900, "恢复 pending 重置资金或还款进度。");
    Check(bridge.Engine.State.Loan.Contract!.DueDay == 32 && !store.modData.ContainsKey(StartingDebtScenario.PendingKey), "重复初始化延长期限或未清理 pending。");
    var reloaded = MarketEngine.Deserialize(store.modData[GameBridge.MarketSaveKey], bridge.Engine.Config);
    Check(reloaded.Loan.Contract!.AmountToClose == 4900 && reloaded.Loan.Contract.DueDay == 32, "保存未保留还款结果或期限。");

    var normal = NewStore(cash: 222);
    normal.startType = NewGameData.StartType.CLASSIC; normal.startScenarioId = "";
    bridge.PrepareSave();
    Check(normal.GetCash() == 222 && normal.modData!.Count == 0, "切换普通存档时额外发钱或加债。");
}

// 回调重复进入时不能把首日改成较晚日期，也不能为其他职业留下待初始化标记。
{
    var store = NewStore();
    scenario.onStart(); StoreStation.Day = 6; scenario.onStart();
    Check(store.modData![StartingDebtScenario.PendingKey] == "5", "重复 onStart 延长了债务期限。");
    FinishNativeSetup(store);
    var bridge = new GameBridge();
    Throws(bridge.PrepareSave, "错过首日仍重置现金建立开局债务。");
    Check(store.GetCash() == 1177 && !store.modData.ContainsKey(GameBridge.MarketSaveKey), "错过首日的拒绝破坏了现金或存档。");
    var other = NewStore(); other.startScenarioId = "another.scenario";
    Throws(scenario.onStart, "其他模组开局可误用负债回调。");
    Check(other.modData!.Count == 0 && other.GetCash() == 777, "错误开局回调改变了存档。");
}

// 附加基线、建立债务与移除 pending 各写回一次；任一阶段失败均需恢复全部宿主数据。
foreach (int failedCall in new[] { 1, 2, 3 })
foreach (bool previousMarket in new[] { false, true })
{
    var store = NewStore(cash: 888); scenario.onStart(); FinishNativeSetup(store);
    string? prior = null;
    if (previousMarket)
    {
        var config = MarketConfig.CreateDefault(); config.EnableStockEvents = false;
        // 本测试只替代保存字符串边界，不模拟真实 envelope 的序列化。
        prior = new MarketEngine(config, new MarketState { LastDay = 5 }).Serialize();
        store.modData![GameBridge.MarketSaveKey] = prior;
    }
    store.modData!["unrelated.mod"] = "keep-me";
    var bridge = new GameBridge { FailPersistCall = failedCall };
    Throws(bridge.PrepareSave, "写回失败未阻止原版保存不完整开局。");
    Check(store.GetCash() == 1288, "写回失败未恢复天赋完成后的原现金。");
    Check(store.modData[StartingDebtScenario.PendingKey] == "5", "写回失败丢失 pending 或改写首日。");
    Check(previousMarket ? store.modData[GameBridge.MarketSaveKey] == prior : !store.modData.ContainsKey(GameBridge.MarketSaveKey), "写回失败未恢复原始股市保存字符串或键缺失状态。");
    Check(store.modData["unrelated.mod"] == "keep-me" && !bridge.IsAttached && !bridge.MonetaryOperation, "回滚破坏其他模组数据或遗留活动宿主。");
    Check(bridge.LastFault.Length > 0, "失败未记录诊断。");
    var retry = new GameBridge();
    retry.PrepareSave();
    Check(store.GetCash() == 1500 && retry.Engine!.State.Loan.Contract!.AmountToClose == 5000
        && !store.modData.ContainsKey(StartingDebtScenario.PendingKey), "恢复后的数据无法重新完成同一开局。");
}
StartingDebtScenario.Unregister();
Check(StartingScenario.Get(StartingDebtScenario.Id) == null, "卸载未移除所属开局。");
Console.WriteLine($"负债开局适配边界验证通过：{checks} 项。");
