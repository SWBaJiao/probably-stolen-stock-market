using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using ProbablyStolenStockMarket.Localization;

namespace ProbablyStolenStockMarket.Game;

// 贷款专员只复用原版顾客排队和对话，不参与原版情报收费或商品交易。
internal sealed class LoanNpcAdapter : IDisposable
{
    internal const string NpcId = "probably_stolen_stock.loan_agent";
    internal const string NpcName = "沈衡";
    private static LoanNpcAdapter? Current;
    private readonly GameBridge _host;
    private readonly ModLogger _log;
    private readonly LoanCardAdapter _card;
    private readonly LoanCharacterVisual _characterVisual;
    private readonly List<(MethodBase Original, MethodInfo Patch)> _patches = new();
    private readonly List<object> _factoryRoots = new();
    private readonly List<object> _visitRoots = new();
    private readonly List<StoreClient> _ownedClients = new();
    private readonly List<(Dialogue Dialog, string Text)> _dialogues = new();
    private HarmonyLib.Harmony? _harmony;
    private System.Func<StoreClient>? _factory;
    private StoreClientManager? _queue;
    private string _identity = "";
    private string _lastFault = "";
    private int _epoch;
    private int _languageRevision = -1;
    private bool _disposed;
    private bool _registered;

    internal LoanNpcAdapter(GameBridge host, ModLogger log)
    { _host = host; _log = log; _card = new LoanCardAdapter(host, log); _characterVisual = new LoanCharacterVisual(log); }

    internal void Initialize(HarmonyLib.Harmony harmony)
    {
        if (Current != null) throw new InvalidOperationException("贷款专员已经初始化。");
        Current = this; _harmony = harmony;
        try
        {
            Patch(typeof(StoreClientManager), "GenerateClient", nameof(Generated), false);
            Patch(typeof(StoreClientManager), "TrackClient", nameof(TrackPrefix), true);
            Patch(typeof(StoreClient), "OnIntroduced", nameof(IntroducedPrefix), true);
            Patch(typeof(StoreClient), "IsClientBuyingThisItem", nameof(NoMerchandisePrefix), true);
            Patch(typeof(NegociationUIManager), "OpenUI", nameof(TradingUiPrefix), true);
            foreach (string method in new[] { "OnOfferAcceptClick", "OnBuyAllClick", "OnSellAllClick", "OnWholesaleClick" })
                Patch(typeof(NegociationUIManager), method, nameof(TradePrefix), true);
        }
        catch { Dispose(); throw; }
    }

    internal void Tick()
    {
        if (_disposed || !_host.BrokerReady) return;
        try
        {
            if (_identity != _host.BrokerSaveIdentity) { Reset(); _identity = _host.BrokerSaveIdentity; }
            Register();
            RefreshLanguage();
            _card.Tick();
            QueueVisit();
        }
        catch (Exception ex) { Report(ex); }
    }

    private void QueueVisit()
    {
        var engine = _host.Engine;
        var store = _host.CurrentStore;
        var manager = store?.storeClientManager;
        if (engine == null || store == null || manager?.clientStack == null) return;
        if (_queue != null && _queue != manager) RemoveQueued();
        _queue = manager;
        var loan = engine.State.Loan;
        int day = engine.State.LastDay;
        bool introduction = !loan.HasMetLender && loan.FirstFailureDay >= 0 && day >= loan.VisitDay;
        var contract = loan.Contract;
        bool collection = loan.HasMetLender && contract != null && contract.HasDefaulted && !loan.EndingPending && !loan.EndingExecuted
            && (loan.LastCollectionDay < 0 || day - loan.LastCollectionDay >= 2
                || (day == contract.DueDay + 5 || day == contract.DueDay + 7) && loan.LastCollectionDay != day);
        if (!introduction && !collection) { RemoveQueued(); return; }
        // 插入队首只影响下一位顾客；不遣散或替换当前顾客和正在进行的剧情。
        if (store.storeState != PlayerStore.StoreState.OPEN || IsLender(_host.CurrentClient)) return;
        foreach (var client in manager.clientStack) if (IsLender(client)) return;
        var created = StoreClientListDict.CreateStoreClient(NpcId);
        if (!IsLender(created)) throw new InvalidOperationException("贷款专员工厂返回了其他顾客。");
        manager.AddNextClient(created);
        bool inserted = false;
        foreach (var client in manager.clientStack) if (client == created) inserted = true;
        if (!inserted) throw new InvalidOperationException("贷款专员入队未能读回确认。");
        _log.Msg($"[贷款来访] 第 {day} 日安排{(introduction ? "首次介绍" : "欠款催收")}；下一位到访，不中断当前接待。");
    }

    private void Register()
    {
        var registry = StoreClientListDict.storeClientDict;
        if (registry == null) return;
        if (_registered && registry.ContainsKey(NpcId) && registry[NpcId] == _factory) return;
        if (registry.ContainsKey(NpcId)) throw new InvalidOperationException("贷款专员标识已被其他工厂占用。");
        if (_factory == null)
        {
            Func<StoreClient> create = () => { try { return Create(); } catch (Exception ex) { Report(ex); return null!; } };
            _factory = create;
            _factoryRoots.Add(create); _factoryRoots.Add(_factory);
        }
        registry.Add(NpcId, _factory); _registered = true;
    }

    private StoreClient Create()
    {
        if (_disposed || !_host.BrokerReady || _host.Engine == null) throw new InvalidOperationException("贷款来访的存档尚未就绪。");
        string identity = _host.BrokerSaveIdentity;
        int epoch = _epoch;
        bool firstMeeting = !_host.Engine.State.Loan.HasMetLender;
        var client = new StoreClient();
        _ownedClients.Add(client);
        StoreClientFactionSetup.InitBlackMarket(client);
        client.identifier = NpcId; client.displayName = Localizer.Text("沈衡 · 贷款专员");
        client.spriteName = _characterVisual.EnsureRegistered() ? LoanCharacterVisual.SpriteKey : Appearance();
        client.clientIntent = StoreClient.ClientIntent.INFORMATION_DEALER;
        client.serviceCost = 0;
        client.CompleteClientCreation(true);
        client.identifier = NpcId; client.displayName = Localizer.Text("沈衡 · 贷款专员");
        client.clientIntent = StoreClient.ClientIntent.INFORMATION_DEALER;
        client.serviceCost = 0; client.dismissable = true;
        client.arrestable = false; client.nonDismissableArrestable = false; client.nonShootable = true;
        client.isRegularSellDisable = true; client.isMultiBuyDisabled = true;
        client.CanReturn = false; client.CanReturnAsBuyer = false;
        client.clientBuyingIdList.Clear(); client.clientBuyingTagList.Clear();
        client.onDealAccepted = null; client.onItemBought = null; client.onItemSold = null;
        client.OnArrival = RootAction(() =>
        {
            if (!Valid(identity, epoch)) return;
            if (!firstMeeting)
            {
                _host.Engine!.State.Loan.LastCollectionDay = _host.Engine.State.LastDay;
                _host.PersistBrokerState();
            }
        });
        client.OnDismiss = RootAction(() =>
        {
            if (!Valid(identity, epoch)) return;
            // 玩家直接请人离开时仍留下联系方式，避免首次救急入口永久丢失。
            if (firstMeeting) FinishIntroduction(client, identity, epoch, false);
            _card.TryGrant();
        });
        var first = Text(firstMeeting
            ? "我叫沈衡，做资金周转。听说你最近连一项付费服务都负担不起，所以过来看看。无抵押贷款额度较小、利息较高；股票抵押贷款额度较高、利息较低。期限由你选择，最长四周。"
            : CollectionText());
        var last = Text(firstMeeting
            ? "这是我的名片，电话 731207。现在可以办理，也可以以后打电话联系。同一时间只能有一笔贷款；到期后超过七天仍有欠款，店铺将被清算。借不借由你决定，联系方式先留给你。"
            : "电话 731207 可以查看账单、部分还款或全部结清。部分还款不会推迟期限；如果今天能处理，就打开账单看看。"
        );
        first.SetNextDialogue(last);
        last.endAction = RootAction(() => FinishIntroduction(client, identity, epoch, true));
        client.mainDialogue = first;
        client.repeatDialogue = Text(firstMeeting ? "名片上的号码是 731207。你可以现在查看贷款条件，也可以以后通过电话办理。" : CollectionText());
        client.repeatDialogue.endAction = RootAction(() => FinishIntroduction(client, identity, epoch, true));
        client.allDoneDialogue = Text("贷款和还款记录都可以通过电话 731207 查询。记住合同上的到期日。");
        client.acceptDealDialogue = null; client.acceptDealDialogueLastLine = null;
        client.isMainDialogueStarted = false;
        return client;
    }

    private string CollectionText()
    {
        var engine = _host.Engine;
        var contract = engine?.State.Loan.Contract;
        if (engine == null || contract == null) return "你的贷款已经结清，名片上的电话仍然有效。";
        int day = engine.State.LastDay;
        string warning = day >= contract.DueDay + 7 ? "今天是最后还款日。今天结束后仍有欠款，清算程序就会启动。"
            : day >= contract.DueDay + 5 ? "这是最后通牒，请在最终期限前结清欠款。"
            : "宽限期已经结束，我来提醒你处理这笔逾期贷款。";
        return $"{warning}\n目前全部结清需要 {contract.AmountToClose} 游戏币。最后还款日是第 {contract.EndingDay - 1} 日。";
    }

    private void FinishIntroduction(StoreClient client, string identity, int epoch, bool openPanel)
    {
        if (!Valid(identity, epoch)) return;
        var engine = _host.Engine!;
        client.isIntroduced = true;
        engine.MarkLoanContactMet();
        _host.PersistBrokerState();
        _card.TryGrant();
        if (openPanel && _host.CurrentClient == client) _host.ShowLoans();
    }

    private static string Appearance()
    {
        var dict = SpriteDict.Instance ?? throw new InvalidOperationException("原版人物图像尚未就绪。");
        var names = new List<string>();
        if (NativeGameAccess.GetBrokerPortraits(dict) != null)
            foreach (var entry in NativeGameAccess.GetBrokerPortraits(dict))
                if (entry != null && !string.IsNullOrWhiteSpace(entry.key) && entry.sprite != null) names.Add(entry.key);
        if (names.Count == 0) throw new InvalidOperationException("没有可用的贷款专员人物素材。");
        return names.OrderBy(x => x, StringComparer.Ordinal).First();
    }

    private Dialogue Text(string text)
    {
        var dialogue = new Dialogue().SetText(Localizer.Text(NpcName), Localizer.Text(text));
        _dialogues.Add((dialogue, text)); _visitRoots.Add(dialogue);
        return dialogue;
    }

    private void RefreshLanguage()
    {
        if (_languageRevision == Localizer.Revision) return;
        _languageRevision = Localizer.Revision;
        foreach (var item in _dialogues) item.Dialog.SetText(Localizer.Text(NpcName), Localizer.Text(item.Text));
        foreach (var client in _ownedClients) client.displayName = Localizer.Text("沈衡 · 贷款专员");
    }

    private System.Action RootAction(Action action)
    {
        Action guarded = () => { try { action(); } catch (Exception ex) { Report(ex); } };
        var native = guarded;
        _visitRoots.Add(action); _visitRoots.Add(guarded); _visitRoots.Add(native);
        return native;
    }

    private bool Valid(string identity, int epoch) => !_disposed && epoch == _epoch && _host.BrokerReady && _host.BrokerSaveIdentity == identity;
    private static bool IsLender(StoreClient? client) => client != null && client.identifier == NpcId;
    private void Patch(Type type, string name, string handler, bool prefix)
    {
        var original = AccessTools.Method(type, name) ?? throw new MissingMethodException(type.FullName, name);
        var patch = AccessTools.Method(typeof(LoanNpcAdapter), handler) ?? throw new MissingMethodException(handler);
        _harmony!.Patch(original, prefix ? new HarmonyMethod(patch) : null, prefix ? null : new HarmonyMethod(patch));
        _patches.Add((original, patch));
    }
    private static void Generated() => Current?.Tick();
    private static bool TrackPrefix(StoreClient __0) => !IsLender(__0);
    private static bool TradePrefix() => !IsLender(Current?._host.CurrentClient);
    private static bool TradingUiPrefix() => !IsLender(Current?._host.CurrentClient);
    private static bool IntroducedPrefix(StoreClient __instance)
    {
        if (!IsLender(__instance)) return true;
        // 原版情报商的介绍完成会打开收费界面，本专员由自己的末句进入贷款窗口。
        __instance.isIntroduced = true;
        return false;
    }
    private static bool NoMerchandisePrefix(StoreClient __instance, ref bool __result)
    {
        if (!IsLender(__instance)) return true;
        __result = false; return false;
    }

    private void RemoveQueued()
    {
        if (_queue?.clientStack == null) return;
        for (int i = _queue.clientStack.Count - 1; i >= 0; i--)
            if (IsLender(_queue.clientStack[i])) _queue.clientStack.RemoveAt(i);
        _queue = null;
    }

    internal void Reset()
    {
        _epoch++;
        try { RemoveQueued(); } catch (Exception ex) { Report(ex); }
        foreach (var client in _ownedClients)
            try { client.OnArrival = null; client.OnDismiss = null; } catch (Exception ex) { Report(ex); }
        foreach (var item in _dialogues)
            try { item.Dialog.startAction = null; item.Dialog.endAction = null; } catch (Exception ex) { Report(ex); }
        _ownedClients.Clear(); _dialogues.Clear(); _visitRoots.Clear();
        _card.Reset(); _identity = ""; _lastFault = ""; _languageRevision = -1;
    }

    internal void BeforeSave() => _card.BeforeSave();
    internal void AfterSave() => _card.AfterSave();
    internal void PrepareCardRead(GameItem item) => _card.PrepareRead(item);
    internal bool IsOwnCard(GameItem item) => _card.IsOwnCard(item);

    public void Dispose()
    {
        if (_disposed) return;
        Reset(); _disposed = true;
        if (_harmony != null) foreach (var item in _patches) _harmony.Unpatch(item.Original, item.Patch);
        _patches.Clear();
        try
        {
            var registry = StoreClientListDict.storeClientDict;
            if (_registered && registry != null && registry.ContainsKey(NpcId) && registry[NpcId] == _factory) registry.Remove(NpcId);
        }
        catch (Exception ex) { Report(ex); }
        _card.Dispose(); _characterVisual.Dispose(); _factoryRoots.Clear(); _factory = null; _registered = false;
        if (Current == this) Current = null;
    }

    private void Report(Exception ex)
    {
        string message = ex.GetType().Name + ": " + ex.Message;
        if (_lastFault == message) return;
        _lastFault = message; _log.Warning("[贷款专员] " + message);
    }
}
