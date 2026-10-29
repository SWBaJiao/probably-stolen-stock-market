using Newtonsoft.Json;
using ProbablyStolenStockMarket;
using ProbablyStolenStockMarket.Game;
using ProbablyStolenStockMarket.Localization;

int checks = 0;
void Check(bool value, string message)
{
    if (!value) throw new Exception(message);
    checks++;
}
GameBridge NewHost()
{
    EmporiumEntry.Instance = new EmporiumEntry();
    DirectoryMaster.Created.Clear(); GraphUtils.AcceptCalls = 0; GraphUtils.OverrideAccept = null;
    NativeGameAccess.Milliseconds = 0;
    return new GameBridge();
}
Localizer.Language = ModLanguage.Chinese;

// 满包前置检查不能留下存档中的物品编号，也不能把临时物品泄漏到场景。
{
    var host = NewHost(); using var adapter = new IntelCardAdapter(host, new ModLogger());
    var entry = EmporiumEntry.Instance!; entry.invElement.HasSpace = false;
    adapter.Tick();
    Check(!host.IntelCardDelivered && host.IntelCardItemId == -1 && !host.IntelCardUncertain && host.PersistCalls == 0, "满包改变了已发放或待交付状态。");
    Check(GraphUtils.AcceptCalls == 0 && DirectoryMaster.Created.Single().Destroyed, "满包仍插入物品或未销毁临时名片。");
    entry.invElement.HasSpace = true; NativeGameAccess.Milliseconds = 5001; adapter.Tick();
    var card = entry.invElement.childItems.Single();
    Check(host.IntelCardDelivered && !host.IntelCardUncertain && host.IntelCardItemId == card.uniqueId && GraphUtils.AcceptCalls == 1, "腾出空间后未确认主背包发放。");
    Check(card.Name == IntelCardAdapter.CardName && card.unitValue == 0 && card.Count == 1 && adapter.IsOwnCard(card), "物品名称、数量、价格或归属标记错误。");
    Check(card.GetTagReadonly("HANDNOTE_BODY")!.GetString() == IntelCardAdapter.CardBody && card.GetTagReadonly("HANDNOTE_SIGN")!.GetString().Contains("PS_INTEL_CARD_V1:"), "名片正文或独立标记缺失。");
    adapter.TryGrant(); NativeGameAccess.Milliseconds += 5001; adapter.Tick();
    Check(entry.invElement.childItems.Count == 1 && GraphUtils.AcceptCalls == 1 && DirectoryMaster.Created.Count == 2, "连续轮询重复生成或插入名片。");
    adapter.BeforeSave(); adapter.AfterSave(); host.Reload();
    using var restored = new IntelCardAdapter(host, new ModLogger()); restored.Tick();
    Check(entry.invElement.childItems.Count == 1 && GraphUtils.AcceptCalls == 1 && restored.IsOwnCard(card), "保存恢复后再次发放名片。");
    Localizer.Language = ModLanguage.English; restored.PrepareRead(card);
    Check(card.GetTagReadonly("HANDNOTE_BODY")!.GetString() == Localizer.Text(IntelCardAdapter.CardBody), "阅读时未按当前语言更新正文。");
    Check(card.GetTagReadonly("HANDNOTE_SIGN")!.GetString().EndsWith("PS_INTEL_CARD_V1:" + NativeGameAccess.HashPrefix(host.BrokerSaveIdentity, 16)), "切换语言改变了名片稳定标记。");
    Localizer.Language = ModLanguage.Chinese;
}

// 不持有本存档终端时不会提前发卡，也不会把名片当作解锁凭证。
{
    var host = NewHost(); host.HasTerminal = false;
    using var adapter = new IntelCardAdapter(host, new ModLogger()); adapter.TryGrant();
    Check(DirectoryMaster.Created.Count == 0 && GraphUtils.AcceptCalls == 0 && host.IntelCardItemId == -1, "没有终端仍发放名片。");
    host.HasTerminal = true; adapter.TryGrant();
    Check(EmporiumEntry.Instance!.invElement.childItems.Count == 1, "取得终端后未发放名片。");
    host.HasTerminal = false;
    Check(!host.OwnsStockTerminal(), "发卡意外改变了终端权限。");
}

// 预存失败不得接着插入；插入后的完成写回失败不得再次造物。
foreach (int failure in new[] { 1, 2 })
{
    var host = NewHost(); host.FailPersistCall = failure;
    using var adapter = new IntelCardAdapter(host, new ModLogger()); adapter.TryGrant();
    int calls = GraphUtils.AcceptCalls;
    Check(calls == (failure == 1 ? 0 : 1), "保存失败时插入次数错误。");
    Check(failure == 1 ? DirectoryMaster.Created.Single().Destroyed : EmporiumEntry.Instance!.invElement.childItems.Count == 1, "保存失败遗留了不明确的物品位置。");
    adapter.TryGrant(); NativeGameAccess.Milliseconds += 5001; adapter.Tick();
    Check(GraphUtils.AcceptCalls == calls && DirectoryMaster.Created.Count == 1, "保存失败后重复制造名片。");
    Check(failure == 1 ? host.IntelCardUncertain && !host.IntelCardDelivered : host.IntelCardDelivered, "保存失败没有保留保守交付状态。");
    host.Reload(); using var reloaded = new IntelCardAdapter(host, new ModLogger()); reloaded.TryGrant();
    Check(GraphUtils.AcceptCalls == calls && DirectoryMaster.Created.Count == 1, "读档恢复不确定或已交付状态后重复制造名片。");
}

// 明确插入失败可清理并重试；落入其他容器则保留待核实状态。
{
    var host = NewHost(); GraphUtils.OverrideAccept = (_, _) => { };
    using var adapter = new IntelCardAdapter(host, new ModLogger()); adapter.TryGrant();
    Check(DirectoryMaster.Created.Single().Destroyed && host.IntelCardItemId == -1 && !host.IntelCardUncertain, "明确未插入时未清理临时名片或复位状态。");
    GraphUtils.OverrideAccept = null; adapter.TryGrant();
    Check(host.IntelCardDelivered && EmporiumEntry.Instance!.invElement.childItems.Count == 1, "明确失败后无法安全重试。");
}
{
    var host = NewHost(); var unexpected = new GameGridInventory();
    GraphUtils.OverrideAccept = (_, item) => { item.parentInventory = unexpected; unexpected.childItems.Add(item); };
    using var adapter = new IntelCardAdapter(host, new ModLogger()); adapter.TryGrant(); adapter.TryGrant();
    Check(unexpected.childItems.Count == 1 && host.IntelCardUncertain && !host.IntelCardDelivered && !DirectoryMaster.Created.Single().Destroyed, "不明确插入结果导致销毁或重复发放。");
}

// 切档后即使物品编号被复用，也必须同时核对存档签名；不能修改旧档名片。
{
    var host = NewHost(); using var adapter = new IntelCardAdapter(host, new ModLogger()); adapter.TryGrant();
    var oldCard = EmporiumEntry.Instance!.invElement.childItems.Single();
    string oldBody = oldCard.GetTagReadonly("HANDNOTE_BODY")!.GetString();
    host.BrokerSaveIdentity = "save-B"; host.Engine = new(); host.Save = new AdapterSave();
    Check(!adapter.IsOwnCard(oldCard), "切档后认领了旧档名片。");
    Localizer.Language = ModLanguage.English; adapter.PrepareRead(oldCard);
    Check(oldCard.GetTagReadonly("HANDNOTE_BODY")!.GetString() == oldBody, "切档后改写了其他存档名片。");
    Localizer.Language = ModLanguage.Chinese;
    adapter.TryGrant();
    Check(EmporiumEntry.Instance.invElement.childItems.Count == 2 && host.IntelCardItemId != oldCard.uniqueId, "新档误认旧卡而不发新卡。");
    var current = EmporiumEntry.Instance.invElement.childItems.Single(item => item != oldCard);
    Check(adapter.IsOwnCard(current) && !adapter.IsOwnCard(oldCard), "切档后的名片归属不准确。");
}

// 完整查询暂时漏掉直接成员时不允许推断缺卡，也不能重复创建。
{
    var host = NewHost(); var entry = EmporiumEntry.Instance!;
    entry.invElement.childItems.Add(new GameItem()); entry.CompleteQueries = false;
    using var adapter = new IntelCardAdapter(host, new ModLogger()); adapter.TryGrant();
    Check(DirectoryMaster.Created.Count == 0 && host.PersistCalls == 0, "不完整物品查询仍创建了名片。");
}
var legacy = JsonConvert.DeserializeObject<AdapterSave>("{\"Version\":1}")!;
Check(!legacy.IntelCardDelivered && legacy.IntelCardItemId == -1 && !legacy.IntelCardUncertain, "旧存档缺少名片字段时未使用安全默认值。");
Console.WriteLine($"股市情报名片适配边界验证通过：{checks} 项。");
