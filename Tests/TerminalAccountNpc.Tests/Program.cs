using System.Reflection;
using ProbablyStolenStockMarket;
using ProbablyStolenStockMarket.Game;

int checks = 0;
void Check(bool value, string message)
{ if (!value) throw new Exception(message); checks++; }

// 来访只进入下一位队列，不替换当前顾客或已有排队者。
using (var f = new Fixture())
{
    var existing = new StoreClient { identifier = "original-story" };
    var queued = new StoreClient { identifier = "ordinary-queued" };
    f.Host.CurrentClient = existing;
    f.Host.CurrentStore!.storeClientManager.clientStack.Add(queued);
    f.SetDay(1); f.Adapter.Tick();
    f.SetDay(2); f.Adapter.Tick();
    Check(f.Queue.Count == 1 && f.Queue[0] == queued, "第 3 天前插入了开户专员。");
    f.SetDay(3); f.Host.CurrentStore.storeState = PlayerStore.StoreState.CLOSED; f.Adapter.Tick();
    Check(f.Queue.Count == 1, "打烊时仍插入了专员。");
    f.Host.CurrentStore.storeState = PlayerStore.StoreState.OPEN; f.Adapter.Tick();
    Check(f.Queue.Count == 2 && TerminalAccountNpcAdapter.IsAgent(f.Queue[0]) && f.Queue[1] == queued
        && f.Host.CurrentClient == existing, "第 3 天没有插到下一位或改变了原顾客。");
    var agent = f.Queue[0]; f.Adapter.Tick(); f.Adapter.Tick();
    Check(f.Queue.Count == 2 && f.Queue[0] == agent, "重复轮询重复排入专员。");
    Check(agent.serviceCost == 0 && agent.isRegularSellDisable && agent.isMultiBuyDisabled && agent.CanReturn == false,
        "专员仍可触发原版收费或普通商品交易。");
    Check(f.Log.Warnings.Count == 0, "正常来访记录了适配错误。");
}

// 对话结束展示明确的开户与延期选择，付款票据只消费一次。
using (var f = new Fixture())
{
    var agent = f.Arrive(); f.FinishIntroduction(agent);
    var ui = GenericConfirmationUIManager.Instance;
    Check(ui.panel.activeSelf && ui.ConfirmText == "支付 50 元开户" && ui.CancelText == "三天后再来"
        && ui.Body.Contains("Ctrl+F8") && f.Adapter.BlocksGameInput, "对话没有展示完整开户选择。");
    var callback = ui.Confirm!;
    callback(); callback();
    Check(f.Host.Account.Opened && f.Host.Cash == 50 && f.Host.OpenCalls == 1 && f.Host.Saves == 1,
        "重复确认重复扣款或没有完成开户保存。");
    Check(agent.BusinessCompleted && !f.Adapter.BlocksGameInput && DialogUIManager.Instance.Current!.Body.Contains("开户成功"),
        "付款成功没有释放输入并显示结果。");
    f.Host.CurrentStore!.DismissCurrentClient(); f.Adapter.Tick();
    Check(f.Queue.Count == 0 && f.Host.Account.NextVisitDay == 3, "开户后仍排队或安排了新的来访日期。");
}

// 延期按钮与直接遣散都约定三天后，重复遣散不会无限延期。
foreach (bool useChoice in new[] { false, true })
using (var f = new Fixture())
{
    var agent = f.Arrive();
    if (useChoice)
    {
        f.FinishIntroduction(agent);
        GenericConfirmationUIManager.Instance.CancelButtonPressed();
        Check(DialogUIManager.Instance.Current!.Body.Contains("三天后"), "延期没有显示约定结果。");
        DialogUIManager.Instance.Current.endAction!();
    }
    else f.Host.CurrentStore!.DismissCurrentClient();
    Check(!f.Host.Account.Opened && f.Host.Cash == 100 && f.Host.Account.NextVisitDay == 6 && f.Host.Saves == 1,
        "延期误扣款或没有保存第 6 天。");
    agent.OnDismiss?.Invoke();
    f.Adapter.Tick(); f.SetDay(5); f.Adapter.Tick();
    Check(f.Queue.Count == 0 && f.Host.Account.NextVisitDay == 6 && f.Host.Saves == 1,
        "约定未到期仍排队或重复遣散继续延期。");
    f.SetDay(6); f.Adapter.Tick();
    Check(f.Queue.Count == 1 && TerminalAccountNpcAdapter.IsAgent(f.Queue[0]), "第 6 天没有再次来访。");
}

// 余额不足后保留来访，可再次对话办理或延期。
using (var f = new Fixture())
{
    var agent = f.Arrive(); f.Host.Cash = 49; f.FinishIntroduction(agent);
    GenericConfirmationUIManager.Instance.ConfirmButtonPressed();
    Check(!f.Host.Account.Opened && f.Host.Cash == 49 && !agent.BusinessCompleted
        && DialogUIManager.Instance.Current!.Body.Contains("余额不足"), "余额不足仍扣款或结束了业务。");
    f.Host.Cash = 50; agent.repeatDialogue.endAction!();
    Check(GenericConfirmationUIManager.Instance.panel.activeSelf, "余额不足后无法再次打开开户选择。");
    GenericConfirmationUIManager.Instance.ConfirmButtonPressed();
    Check(f.Host.Account.Opened && f.Host.Cash == 0 && f.Host.OpenCalls == 2 && f.Host.Saves == 1,
        "再次对话没有按当前余额办理一次开户。");
}

// 切档、同档重新加载、过日和客人切换都使旧付款及延期回调失效。
foreach (string mode in new[] { "identity", "reload", "market-day", "native-day", "client", "not-ready" })
foreach (bool pay in new[] { false, true })
using (var f = new Fixture())
{
    var agent = f.Arrive(); f.FinishIntroduction(agent);
    var old = pay ? GenericConfirmationUIManager.Instance.Confirm! : GenericConfirmationUIManager.Instance.Cancel!;
    switch (mode)
    {
        case "identity": f.Host.BrokerSaveIdentity = "slot-b"; break;
        case "reload": f.Adapter.Reset(); break;
        case "market-day": f.Host.Engine!.State.LastDay = 4; break;
        case "native-day": StoreStation.Day = 4; break;
        case "client": f.Host.CurrentClient = new StoreClient(); break;
        case "not-ready": f.Host.BrokerReady = false; break;
    }
    old();
    Check(f.Host.OpenCalls == 0 && f.Host.PostponeCalls == 0 && f.Host.Cash == 100 && !f.Host.Account.Opened
        && f.Host.Account.NextVisitDay == 3 && f.Host.Saves == 0, "失效的 " + mode + " 回调仍操作了账户。");
}

// 已失效的离店回调也不能推迟新存档来访。
using (var f = new Fixture())
{
    var first = f.Arrive(); var oldDismiss = first.OnDismiss!;
    f.Adapter.Reset(); f.Host.BrokerSaveIdentity = "slot-b"; f.Host.CurrentClient = null;
    f.Adapter.Tick(); oldDismiss();
    Check(f.Host.Account.NextVisitDay == 3 && f.Host.PostponeCalls == 0 && f.Host.Saves == 0,
        "旧存档离店回调推迟了新存档来访。");
}

// 旧票据不能关闭新存档刚显示的开户窗口。
using (var f = new Fixture())
{
    var first = f.Arrive(); f.FinishIntroduction(first);
    var oldPay = GenericConfirmationUIManager.Instance.Confirm!;
    var oldCancel = GenericConfirmationUIManager.Instance.Cancel!;
    f.Adapter.Reset(); f.Host.CurrentClient = null; f.Host.BrokerSaveIdentity = "slot-b";
    var second = f.Arrive(); f.FinishIntroduction(second);
    var currentPay = GenericConfirmationUIManager.Instance.Confirm;
    oldPay(); oldCancel();
    Check(GenericConfirmationUIManager.Instance.panel.activeSelf && ReferenceEquals(GenericConfirmationUIManager.Instance.Confirm, currentPay)
        && f.Host.OpenCalls == 0 && f.Host.PostponeCalls == 0, "旧票据关闭或替换了新开户窗口。");
    currentPay!();
    Check(f.Host.Account.Opened && f.Host.Cash == 50 && f.Host.OpenCalls == 1, "新票据受旧票据干扰而无法付款。");
}

// 已开户在轮询时清除尚未到访的专员。
using (var f = new Fixture())
{
    f.SetDay(3); f.Adapter.Tick(); Check(f.Queue.Count == 1, "测试前未安排专员。");
    f.Host.Account.Opened = true; f.Adapter.Tick();
    Check(f.Queue.Count == 0, "已开户仍保留尚未到访的专员。");
    f.SetDay(20); f.Adapter.Tick(); Check(f.Queue.Count == 0, "已开户之后再次排入专员。");
}

// 原版补丁仅截流专员，普通顾客继续正常交易。
using (var f = new Fixture())
{
    bool Invoke(string method, params object[] args) => (bool)typeof(TerminalAccountNpcAdapter)
        .GetMethod(method, BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, args)!;
    var agent = f.Arrive();
    Check(!Invoke("TrackPrefix", agent) && !Invoke("TradePrefix") && !Invoke("IntroducedPrefix", agent) && agent.isIntroduced,
        "专员仍调用原版追踪、收费或交易。");
    var ordinary = new StoreClient(); f.Host.CurrentClient = ordinary;
    Check(Invoke("TrackPrefix", ordinary) && Invoke("TradePrefix") && Invoke("IntroducedPrefix", ordinary),
        "开户补丁改变了普通顾客流程。");
}
Console.WriteLine($"证券开户 NPC 边界验证通过：{checks} 项。");

internal sealed class Fixture : IDisposable
{
    internal readonly GameBridge Host = new();
    internal readonly ModLogger Log = new();
    internal readonly TerminalAccountNpcAdapter Adapter;
    internal List<StoreClient> Queue => Host.CurrentStore!.storeClientManager.clientStack;
    internal Fixture()
    {
        StoreClientListDict.storeClientDict = new(); GenericConfirmationUIManager.Instance = new(); DialogUIManager.Instance = new();
        StoreStation.Day = 1;
        Adapter = new TerminalAccountNpcAdapter(Host, Log); Host.Adapter = Adapter;
        Adapter.Initialize(new HarmonyLib.Harmony());
    }
    internal void SetDay(int day) { Host.Engine!.State.LastDay = day; StoreStation.Day = day; }
    internal StoreClient Arrive()
    {
        SetDay(3); Adapter.Tick();
        var agent = Queue.Single(TerminalAccountNpcAdapter.IsAgent); Queue.Remove(agent); Host.CurrentClient = agent; return agent;
    }
    internal void FinishIntroduction(StoreClient client) => client.mainDialogue.endAction!();
    public void Dispose() => Adapter.Dispose();
}
