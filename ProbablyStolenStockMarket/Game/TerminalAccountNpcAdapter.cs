using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using ProbablyStolenStockMarket.Localization;

namespace ProbablyStolenStockMarket.Game;

// 开户专员沿用原版顾客和对话，现金及面板交付由本模组处理。
internal sealed class TerminalAccountNpcAdapter : IDisposable
{
    internal const string NpcId = "probably_stolen_stock.account_agent";
    private const string NpcName = "林澈";
    private const string DisplayName = "林澈 · 证券开户专员";
    private const string OfferText = "开通证券账户需要一次性支付 50 元，开户后会发放股市面板。双击面板即可查看行情和买卖股票。优先放入背包，背包满则放桌面，桌面也满则放窗外。丢失后可按 Ctrl+F8 免费补领，无需再次开户。\n\n现在开户，还是三天后再来？";
    private static TerminalAccountNpcAdapter? Current;
    private readonly GameBridge _host;
    private readonly ModLogger _log;
    private readonly List<(MethodBase Original, MethodInfo Patch)> _patches = new();
    private readonly List<object> _roots = new();
    private readonly List<StoreClient> _clients = new();
    private readonly List<(Dialogue Dialog, string Text)> _dialogues = new();
    private Harmony? _harmony;
    private Func<StoreClient>? _factory;
    private StoreClientManager? _queue;
    private GenericConfirmationUIManager? _prompt;
    private Action? _confirm, _cancel;
    private object? _promptToken;
    private StoreClient? _offerClient;
    private string _identity = "";
    private string _lastFault = "";
    private int _epoch, _languageRevision = -1;
    private int _promptDay = -1;
    private StoreClient? _promptClient;
    private bool _registered, _disposed;

    internal TerminalAccountNpcAdapter(GameBridge host, ModLogger log) { _host = host; _log = log; }
    internal bool BlocksGameInput => OwnsPrompt() && _prompt!.panel != null && _prompt.panel.activeInHierarchy;
    internal static bool IsAgent(StoreClient? client) => client != null && client.identifier == NpcId;

    internal void Initialize(Harmony harmony)
    {
        if (Current != null) throw new InvalidOperationException("证券开户专员已经初始化。");
        Current = this; _harmony = harmony;
        try
        {
            Patch(typeof(StoreClientManager), "GenerateClient", nameof(Generated), false);
            Patch(typeof(StoreClientManager), "TrackClient", nameof(TrackPrefix), true);
            Patch(typeof(StoreClient), "OnIntroduced", nameof(IntroducedPrefix), true);
            Patch(typeof(StoreClient), "IsClientBuyingThisItem", nameof(NoMerchandisePrefix), true);
            Patch(typeof(NegociationUIManager), "OpenUI", nameof(TradePrefix), true);
            foreach (var method in new[] { "OnOfferAcceptClick", "OnBuyAllClick", "OnSellAllClick", "OnWholesaleClick" })
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
            Register(); RefreshLanguage();
            if (_prompt != null && (!BlocksGameInput || _host.CurrentClient != _promptClient
                || _host.Engine!.State.LastDay != _promptDay || !_host.TerminalAccountVisitDue)) ClearPrompt();
            QueueVisit();
            if (_offerClient != null && _host.CurrentClient == _offerClient) ShowOffer(_offerClient);
            else _offerClient = null;
        }
        catch (Exception ex) { Report(ex); }
    }

    private void QueueVisit()
    {
        var store = _host.CurrentStore;
        var manager = store?.storeClientManager;
        if (store == null || manager?.clientStack == null) return;
        if (_queue != null && _queue != manager) RemoveQueued();
        _queue = manager;
        if (!_host.TerminalAccountVisitDue) { RemoveQueued(); return; }
        if (store.storeState != PlayerStore.StoreState.OPEN || IsAgent(_host.CurrentClient)) return;
        foreach (var client in manager.clientStack) if (IsAgent(client)) return;
        var created = StoreClientListDict.CreateStoreClient(NpcId);
        if (!IsAgent(created)) throw new InvalidOperationException("证券开户专员工厂返回了其他顾客。");
        // 安排下一位到访，不打断正在接待的剧情或顾客。
        manager.AddNextClient(created);
        if (!manager.clientStack.Any(client => client == created)) throw new InvalidOperationException("证券开户专员入队未能读回确认。");
        _log.Msg($"[证券开户] 第 {_host.Engine!.State.LastDay} 日安排专员来访。");
    }

    private void Register()
    {
        var registry = StoreClientListDict.storeClientDict;
        if (registry == null) return;
        if (_registered && registry.ContainsKey(NpcId) && registry[NpcId] == _factory) return;
        if (registry.ContainsKey(NpcId)) throw new InvalidOperationException("证券开户专员标识已被其他工厂占用。");
        _factory ??= () => { try { return Create(); } catch (Exception ex) { Report(ex); return null!; } };
        registry.Add(NpcId, _factory); _registered = true;
    }

    private StoreClient Create()
    {
        if (!_host.TerminalAccountVisitDue || _disposed) throw new InvalidOperationException("当前没有待处理的证券开户来访。");
        string identity = _host.BrokerSaveIdentity;
        int epoch = _epoch;
        var client = new StoreClient();
        _clients.Add(client);
        StoreClientFactionSetup.InitBlackMarket(client);
        client.identifier = NpcId; client.displayName = Localizer.Text(DisplayName);
        var portraits = NativeGameAccess.GetBrokerPortraits(SpriteDict.Instance);
        client.spriteName = portraits.Where(x => x != null && x.sprite != null && !string.IsNullOrWhiteSpace(x.key))
            .Select(x => x.key).OrderBy(x => x, StringComparer.Ordinal).First();
        client.clientIntent = StoreClient.ClientIntent.INFORMATION_DEALER; client.serviceCost = 0;
        client.CompleteClientCreation(true);
        client.identifier = NpcId; client.displayName = Localizer.Text(DisplayName);
        client.clientIntent = StoreClient.ClientIntent.INFORMATION_DEALER; client.serviceCost = 0;
        client.dismissable = true; client.arrestable = false; client.nonDismissableArrestable = false; client.nonShootable = true;
        client.isRegularSellDisable = true; client.isMultiBuyDisabled = true;
        client.CanReturn = false; client.CanReturnAsBuyer = false;
        client.clientBuyingIdList.Clear(); client.clientBuyingTagList.Clear();
        client.onDealAccepted = null; client.onItemBought = null; client.onItemSold = null; client.OnArrival = null;
        client.OnDismiss = RootAction(() =>
        {
            if (!Valid(identity, epoch)) return;
            ClearPrompt(); _offerClient = null;
            _host.PostponeTerminalAccount();
        });
        client.mainDialogue = Text("你好，我叫林澈，是证券开户专员。今天来介绍证券账户，开户费 50 元，办理后就能领取股市面板。暂时不方便的话，我可以三天后再来。");
        client.repeatDialogue = Text("开户费只收一次，共 50 元。你可以现在办理，也可以让我三天后再来。");
        Action offer = () =>
        {
            if (!Valid(identity, epoch) || _host.CurrentClient != client) return;
            client.isIntroduced = true;
            _offerClient = client; ShowOffer(client);
        };
        client.mainDialogue.endAction = RootAction(offer);
        client.repeatDialogue.endAction = RootAction(offer);
        client.allDoneDialogue = Text("开户安排已经处理。祝你经营顺利，投资时记得留足日常周转资金。");
        client.acceptDealDialogue = null; client.acceptDealDialogueLastLine = null; client.isMainDialogueStarted = false;
        return client;
    }

    private void ShowOffer(StoreClient client)
    {
        if (!_host.TerminalAccountVisitDue || _host.BlocksGameInput || _host.CurrentClient != client) { _offerClient = null; return; }
        var ui = GenericConfirmationUIManager.Instance;
        if (ui?.panel == null || ui.panel.activeSelf) return;
        string identity = _identity;
        int epoch = _epoch, day = _host.Engine!.State.LastDay;
        var token = new object();
        bool consumed = false;
        void Resolve(bool pay)
        {
            if (consumed) return;
            // 旧窗口的回调不能清理后来新建的确认窗口。
            if (!ReferenceEquals(_promptToken, token)) { consumed = true; return; }
            bool valid = Valid(identity, epoch) && _host.CurrentClient == client && _host.Engine!.State.LastDay == day
                && StoreStation.GetDayCounter() == day && BlocksGameInput;
            consumed = true;
            ClearPrompt();
            if (!valid) return;
            string result;
            bool done;
            if (pay)
            {
                result = _host.OpenTerminalAccount();
                done = _host.TerminalAccountOpened;
            }
            else
            {
                _host.PostponeTerminalAccount();
                result = "好的，我三天后再来。到时仍然只需支付 50 元开户。";
                done = true;
            }
            if (done) client.CompleteBusiness();
            var response = Text(result);
            if (!pay) response.endAction = RootAction(() =>
            {
                if (Valid(identity, epoch) && _host.CurrentClient == client) _host.CurrentStore!.DismissCurrentClient();
            });
            DialogUIManager.Instance?.InterruptDialog();
            DialogUIManager.Instance?.StartDialogue(response, true);
        }
        _confirm = RootAction(() => Resolve(true)); _cancel = RootAction(() => Resolve(false));
        _prompt = ui; _promptDay = day; _promptClient = client; _offerClient = null; _promptToken = token;
        ui.OpenPanel(Localizer.Text(OfferText), Localizer.Text("支付 50 元开户"), Localizer.Text("三天后再来"), _confirm, _cancel);
    }

    private bool OwnsPrompt() => _prompt != null && NativeGameAccess.OwnsConfirmation(_prompt, _confirm, _cancel);
    private void ClearPrompt()
    {
        try
        {
            if (OwnsPrompt())
            {
                NativeGameAccess.ClearConfirmation(_prompt!);
                if (_prompt!.panel != null && _prompt.panel.activeSelf) _prompt.ClosePanel();
            }
        }
        finally { _prompt = null; _confirm = null; _cancel = null; _promptClient = null; _promptDay = -1; _promptToken = null; }
    }

    private Dialogue Text(string text)
    {
        var dialog = new Dialogue().SetText(Localizer.Text(NpcName), Localizer.Text(text));
        _dialogues.Add((dialog, text)); _roots.Add(dialog); return dialog;
    }
    private Action RootAction(Action action)
    {
        Action guarded = () => { try { action(); } catch (Exception ex) { Report(ex); } };
        _roots.Add(action); _roots.Add(guarded); return guarded;
    }
    private void RefreshLanguage()
    {
        if (_languageRevision == Localizer.Revision) return;
        _languageRevision = Localizer.Revision;
        foreach (var item in _dialogues) item.Dialog.SetText(Localizer.Text(NpcName), Localizer.Text(item.Text));
        foreach (var client in _clients) client.displayName = Localizer.Text(DisplayName);
    }
    private bool Valid(string identity, int epoch) => !_disposed && epoch == _epoch && _host.BrokerReady && _host.BrokerSaveIdentity == identity;
    private void Patch(Type type, string name, string handler, bool prefix)
    {
        var original = AccessTools.Method(type, name) ?? throw new MissingMethodException(type.FullName, name);
        var patch = AccessTools.Method(typeof(TerminalAccountNpcAdapter), handler) ?? throw new MissingMethodException(handler);
        _harmony!.Patch(original, prefix ? new HarmonyMethod(patch) : null, prefix ? null : new HarmonyMethod(patch));
        _patches.Add((original, patch));
    }
    private static void Generated() => Current?.Tick();
    private static bool TrackPrefix(StoreClient __0) => !IsAgent(__0);
    private static bool TradePrefix() => !IsAgent(Current?._host.CurrentClient);
    private static bool IntroducedPrefix(StoreClient __instance)
    {
        if (!IsAgent(__instance)) return true;
        __instance.isIntroduced = true; return false;
    }
    private static bool NoMerchandisePrefix(StoreClient __instance, ref bool __result)
    {
        if (!IsAgent(__instance)) return true;
        __result = false; return false;
    }
    private void RemoveQueued()
    {
        if (_queue?.clientStack == null) return;
        for (int i = _queue.clientStack.Count - 1; i >= 0; i--)
            if (IsAgent(_queue.clientStack[i])) _queue.clientStack.RemoveAt(i);
        _queue = null;
    }
    internal void Reset()
    {
        _epoch++; ClearPrompt(); _offerClient = null;
        try { RemoveQueued(); } catch (Exception ex) { Report(ex); }
        foreach (var client in _clients) { client.OnArrival = null; client.OnDismiss = null; }
        foreach (var item in _dialogues) { item.Dialog.startAction = null; item.Dialog.endAction = null; }
        _clients.Clear(); _dialogues.Clear(); _roots.Clear(); _identity = ""; _lastFault = ""; _languageRevision = -1;
    }
    public void Dispose()
    {
        if (_disposed) return;
        Reset(); _disposed = true;
        if (_harmony != null) foreach (var item in _patches) _harmony.Unpatch(item.Original, item.Patch);
        _patches.Clear();
        var registry = StoreClientListDict.storeClientDict;
        if (_registered && registry != null && registry.ContainsKey(NpcId) && registry[NpcId] == _factory) registry.Remove(NpcId);
        _factory = null; _registered = false;
        if (Current == this) Current = null;
    }
    private void Report(Exception ex)
    {
        string message = ex.GetType().Name + ": " + ex.Message;
        if (_lastFault == message) return;
        _lastFault = message; _log.Warning("[证券开户专员] " + message);
    }
}
