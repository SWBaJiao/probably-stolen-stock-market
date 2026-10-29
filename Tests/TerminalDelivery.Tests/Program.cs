using ProbablyStolenStockMarket.Game;

int checks = 0;
void Check(bool value, string message)
{ if (!value) throw new Exception(message); checks++; }
GameBridge NewHost(bool opened = true)
{
    EmporiumEntry.Instance = new(); DirectoryMaster.Created.Clear(); DirectoryMaster.ConfigureCreated = null;
    GraphUtils.AcceptCalls = 0; GraphUtils.OverrideAccept = null; StoreUIManager.Instance = new();
    ItemMouseDragHandler.current = new(); ItemMultiSelectHandler.current = new(); ItemSelectHandler.current = new();
    var host = new GameBridge(); host.Save.TerminalAccount.Opened = opened; return host;
}
int LiveCount() => DirectoryMaster.Created.Count(item => !item.Destroyed);

// 未开户的轮询与显式补领都不能免费领取。
{
    var host = NewHost(false);
    host.Poll(); string result = host.RecoverTerminal();
    Check(DirectoryMaster.Created.Count == 0 && GraphUtils.AcceptCalls == 0 && !host.Save.TerminalAccount.Opened,
        "未开户仍创建终端或被补领自动开户。");
    Check(result.Contains("支付 50 元") && StoreUIManager.Instance!.Message == result, "快捷补领没有提示开户要求。");
    var service = new TerminalAccountService(host.Save.TerminalAccount);
    for (int day = 1; day <= 2; day++)
        Check(service.Open(day, () => 100, _ => throw new Exception("提前扣款"), () => { }).Contains("尚未到访"), "第 3 天前可开户。");
}

// 三个落点均运行生产选择算法，验证所有权、保存编号和防重发。
for (int destination = 0; destination < 3; destination++)
{
    var host = NewHost(); var entry = EmporiumEntry.Instance!;
    var inventories = new[] { entry.invElement, entry.backInvinvElement, entry.frontInvinvElement };
    for (int i = 0; i < destination; i++) inventories[i].HasSpace = false;
    string result = host.Poll(); var terminal = DirectoryMaster.Created.Single();
    Check(terminal.parentInventory == inventories[destination] && inventories[destination].childItems.Single() == terminal,
        "终端没有按背包、桌面、窗外顺序选择空位。");
    Check(terminal.Owned && terminal.Name == "股市面板" && terminal.Count == 1 && host.Save.TerminalItemId == terminal.uniqueId
        && !host.Save.TerminalPlacementPending && host.Engine!.State.TerminalGranted, "发放后物品归属或状态错误。");
    Check(result.Contains(destination == 0 ? "主背包" : destination == 1 ? "桌面" : "窗外"), "实际落点没有体现在提示中。");
    host.Poll(); host.RecoverTerminal();
    Check(DirectoryMaster.Created.Count == 1 && GraphUtils.AcceptCalls == 1, "已有面板时轮询或补领重复创建。");
}

// 全满时保留已付费资格，清理临时道具后可安全自动重试。
{
    var host = NewHost(); var entry = EmporiumEntry.Instance!;
    entry.invElement.HasSpace = entry.backInvinvElement.HasSpace = entry.frontInvinvElement.HasSpace = false;
    Check(host.Poll().Contains("都已满") && host.Save.TerminalAccount.Opened && host.Save.TerminalItemId == -1
        && !host.Engine!.State.TerminalGranted && DirectoryMaster.Created.Single().Destroyed && GraphUtils.AcceptCalls == 0,
        "全满导致丢失开户资格、提前记为已发放或泄漏临时道具。");
    entry.backInvinvElement.HasSpace = true; host.Poll();
    Check(LiveCount() == 1 && entry.backInvinvElement.childItems.Count == 1 && host.Save.TerminalAccount.Opened,
        "腾出桌面空间后不能自动交付。");
}

// 保存失败前后均不能留下多个存活物品；插入完成写回失败仍保持会话锁。
foreach (int failure in new[] { 1, 2 })
{
    var host = NewHost(); host.FailPersistCall = failure; host.Poll();
    Check(GraphUtils.AcceptCalls == (failure == 1 ? 0 : 1), "保存失败仍执行了不应执行的插入。");
    Check(failure == 1 ? DirectoryMaster.Created.Single().Destroyed && host.Save.TerminalItemId == -1
        : LiveCount() == 1 && host.Engine!.State.TerminalGranted, "保存失败未清理明确未插入物品或遗失已插入状态。");
    host.Poll(); host.RecoverTerminal();
    Check(LiveCount() == 1 && EmporiumEntry.Instance!.invElement.childItems.Count == 1, "保存异常重试制造了多个存活终端。");
}

// 插入前异常可重试，插入后异常只能采用旧物品。
foreach (bool placed in new[] { false, true })
{
    var host = NewHost();
    GraphUtils.OverrideAccept = (target, item) =>
    { if (placed) GraphUtils.Place(target, item); throw new IOException("模拟原生插入失败。"); };
    host.Poll();
    Check(placed ? LiveCount() == 1 && host.Save.TerminalPlacementPending : LiveCount() == 0 && host.Save.TerminalItemId == -1,
        "插入异常错误处理了物品位置和事务。");
    GraphUtils.OverrideAccept = null; host.Poll(); host.RecoverTerminal();
    Check(LiveCount() == 1 && EmporiumEntry.Instance!.invElement.childItems.Count == 1 && !host.Save.TerminalPlacementPending,
        "插入异常后不能恢复或重复发放。");
}

// 原有桌面、窗外及嵌套物品优先认领；兼容旧档已领取入口。
for (int location = 0; location < 3; location++)
{
    var host = NewHost(false); var entry = EmporiumEntry.Instance!;
    var item = new GameItemElement { uniqueId = 50, Name = "股市面板", Owned = false };
    if (location == 0) GraphUtils.Place(entry.backInvinvElement, item);
    else if (location == 1) GraphUtils.Place(entry.frontInvinvElement, item);
    else { item.parentInventory = new(); entry.OtherItems.Add(item); }
    host.Save.TerminalItemId = item.uniqueId; host.Poll();
    Check(DirectoryMaster.Created.Count == 0 && host.Save.TerminalAccount.Opened && host.IsTerminal(item) && item.Owned
        && host.Engine!.State.TerminalGranted, "未采用旧档其他容器中的终端。");
    int opens = 0; host.OpenPanelRequested += () => opens++;
    Check(host.OpenTerminal(item) && opens == 1 && !host.OpenTerminal(new GameItemElement { uniqueId = 51 }),
        "面板识别或打开错误。");
}

// 明确销毁可在本会话免费恢复，活物品临时脱离容器不能推断丢失。
{
    var host = NewHost(); host.Poll(); var item = DirectoryMaster.Created.Single();
    item.parentInventory!.childItems.Remove(item); item.parentInventory = null;
    Check(host.RecoverTerminal().Contains("仍存在或正在拖动") && DirectoryMaster.Created.Count == 1,
        "拖拽中的终端被重复补发。");
    item.Destroy(); host.RecoverTerminal(); host.RecoverTerminal();
    Check(DirectoryMaster.Created.Count == 2 && LiveCount() == 1 && host.Save.TerminalAccount.Opened,
        "明确销毁后不能同会话补领或重复造物。");
}

// 新会话的已付费旧编号缺失可恢复，不完整查询时必须阻止恢复。
{
    var host = NewHost(); host.Save.TerminalItemId = 25; host.Engine!.State.TerminalGranted = true;
    host.Poll(); Check(DirectoryMaster.Created.Count == 0, "旧档缺失终端自动重复发放。");
    var entry = EmporiumEntry.Instance!;
    var other = new GameItemElement { identifier = "other", uniqueId = 42 }; GraphUtils.Place(entry.frontInvinvElement, other);
    entry.CompleteQueries = false;
    Check(host.RecoverTerminal().Contains("查询与实际容器不一致") && DirectoryMaster.Created.Count == 0, "不完整查询仍补领。");
    entry.CompleteQueries = true; host.RecoverTerminal();
    Check(LiveCount() == 1 && host.Save.TerminalItemId != 25, "完整查询后无法恢复旧档遗失终端。");
}

// 保存或插入期间发生重入，生产会话锁必须挡住第二次创建。
{
    var host = NewHost(); host.BeforePersist = () => { host.Poll(); host.RecoverTerminal(); };
    host.Poll();
    Check(DirectoryMaster.Created.Count == 1 && GraphUtils.AcceptCalls == 1, "保存回调重入制造了第二个面板。");
}

// 新会话没有对象引用时，原生单件、批量拖拽及装备状态仍足以阻止补领。
for (int interaction = 0; interaction < 3; interaction++)
{
    var host = NewHost(); host.Save.TerminalItemId = 25; host.Engine!.State.TerminalGranted = true;
    if (interaction == 0) ItemMouseDragHandler.current!.IsDraggingItem = true;
    else if (interaction == 1) ItemMultiSelectHandler.current!.IsGroupDragging = true;
    else ItemSelectHandler.current!.IsEquipped = true;
    Check(host.RecoverTerminal().Contains("仍存在或正在拖动") && DirectoryMaster.Created.Count == 0,
        "新会话原生拖拽或装备中仍允许补领。");
    ItemMouseDragHandler.current!.IsDraggingItem = false;
    ItemMultiSelectHandler.current!.IsGroupDragging = false;
    ItemSelectHandler.current!.IsEquipped = false;
    host.RecoverTerminal();
    Check(LiveCount() == 1 && GraphUtils.AcceptCalls == 1, "停止拖拽和装备后无法补领。");
}
{
    var host = NewHost(); host.BlocksGameInput = true;
    Check(host.RecoverTerminal() == "操作繁忙，请稍后再试。" && DirectoryMaster.Created.Count == 0,
        "其他面板阻断输入时仍响应补领快捷键。");
    host.BlocksGameInput = false; host.RecoverTerminal();
    Check(LiveCount() == 1, "关闭其他面板后无法补领。");
}

// 进入未知容器或清理失败时没有明确丢失证据，不允许快捷补领解除锁。
foreach (bool cleanupFailure in new[] { false, true })
{
    var host = NewHost(); var unexpected = new GameGridInventory();
    if (cleanupFailure)
    { DirectoryMaster.ConfigureCreated = item => item.ThrowOnDestroy = true; GraphUtils.OverrideAccept = (_, _) => { }; }
    else GraphUtils.OverrideAccept = (_, item) => GraphUtils.Place(unexpected, item);
    host.Poll(); host.Poll(); host.RecoverTerminal();
    Check(DirectoryMaster.Created.Count == 1 && LiveCount() == 1, "不确定交付或清理失败后显式补领制造第二个面板。");
}
Console.WriteLine($"股市面板真实发放边界验证通过：{checks} 项。");
