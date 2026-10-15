using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Il2Cpp;
using Il2CppInterop.Runtime;
using MelonLoader;
using MelonModTemplate.Market;

namespace MelonModTemplate.Game;

// 专属掮客沿用原版来访和情报商界面，资金只交由股市领域统一处理。
internal sealed class IntelBrokerAdapter : IDisposable
{
    internal const string NpcId = "probably_stolen_stock.intel_broker";
    private const string NpcName = "股市情报掮客";
    private readonly GameBridge _host;
    private readonly MelonLogger.Instance _log;
    private readonly List<object> _factoryRoots = new();
    private readonly List<object> _visitRoots = new();
    private readonly List<(MethodBase Original, MethodInfo Patch)> _patches = new();
    private readonly List<StoreClient> _ownedClients = new();
    private HarmonyLib.Harmony? _harmony;
    private Il2CppSystem.Func<StoreClient>? _factory;
    private StoreClientManager? _queue;
    private string _identity = "";
    private string _visitId = "";
    private string _pendingArrivalVisit = "";
    private bool _registered;
    private bool _disposed;
    private bool _purchasing;
    private int _epoch;
    private string _lastFault = "";
    private static IntelBrokerAdapter? Current;

    internal IntelBrokerAdapter(GameBridge host, MelonLogger.Instance log) { _host = host; _log = log; }

    internal void Initialize(HarmonyLib.Harmony harmony)
    {
        if (Current != null) throw new InvalidOperationException("情报掮客适配器已初始化。");
        Current = this; _harmony = harmony;
        try
        {
            Patch(typeof(StoreClientManager), "GenerateClient", nameof(Generated), false);
            Patch(typeof(StoreClientManager), "TrackClient", nameof(TrackPrefix), true);
            Patch(typeof(StoreClient), "OnIntroduced", nameof(IntroducedPrefix), true);
            Patch(typeof(StoreClient), "OnIntroduced", nameof(IntroducedPostfix), false);
            Patch(typeof(NegociationUIManager), "InitUIWithItemInformationDealerMode", nameof(InformationUiPostfix), false);
            Patch(typeof(NegociationUIManager), "OnOfferAcceptClick", nameof(PurchasePrefix), true);
            Patch(typeof(NegociationUIManager), "OnAcceptMultipleButtonClick", nameof(OtherOfferPrefix), true);
            Patch(typeof(NegociationUIManager), "OnWholesaleClick", nameof(OtherOfferPrefix), true);
            Patch(typeof(StoreClient), "IsClientBuyingThisItem", nameof(NoMerchandisePrefix), true);
            _log.Msg("[股市掮客探针] 已安装独立工厂、队尾来访和原版情报商购买钩子，等待游戏内验证。");
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
            var store = PlayerStore.Instance;
            if (store == null || store.storeClientManager == null || store.storeState != PlayerStore.StoreState.OPEN) return;
            var engine = _host.Engine;
            if (engine == null) return;
            FlushArrivalPersistence();
            var manager = store.storeClientManager;
            // BrokerReady 和上方存档身份检查已确认当前存档，保留其队列引用以清理过期来访。
            if (_queue != null && _queue.Pointer != manager.Pointer) RemoveQueued();
            _queue = manager;
            var visit = engine.TryPlanBrokerVisit(_host.GetBrokerScheduledEvents());
            _host.PersistBrokerState();
            if (visit == null || visit.Purchased || visit.Departed || visit.Expired) { RemoveQueued(); return; }
            if (_visitId.Length > 0 && _visitId != visit.VisitId) { RemoveQueued(); ClearVisitCallbacks(); }
            _visitId = visit.VisitId;
            if (IsBroker(_host.CurrentClient)) { Arrived(_host.CurrentClient!, visit.VisitId, _identity); UpdateUi(); return; }
            if (manager.clientStack == null) return;
            _queue = manager;
            foreach (var client in manager.clientStack) if (IsBroker(client)) return;
            var created = StoreClientListDict.CreateStoreClient(NpcId);
            if (!IsBroker(created)) throw new InvalidOperationException("专属情报商工厂返回了其他顾客，已停止本次排队。");
            int before = manager.clientStack.Count;
            manager.AddClient(created);
            _queue = manager;
            _log.Msg($"[股市掮客探针] 队尾追加 visit={visit.VisitId}，原队列={before}，现队列={manager.clientStack.Count}；未替换剧情顾客。");
            _lastFault = "";
        }
        catch (Exception ex) { Report(ex); }
    }

    private void Register()
    {
        var registry = StoreClientListDict.storeClientDict;
        if (registry == null) throw new InvalidOperationException("原版顾客目录尚未初始化。");
        if (_registered && registry.ContainsKey(NpcId) && registry[NpcId]?.Pointer == _factory?.Pointer) return;
        if (registry.ContainsKey(NpcId)) throw new InvalidOperationException("专属顾客标识已被其他工厂占用，未覆盖原值。");
        if (_factory == null)
        {
            Func<StoreClient> managed = () =>
            {
                try { return Create(); }
                catch (Exception ex) { Report(ex); return null!; }
            };
            _factory = DelegateSupport.ConvertDelegate<Il2CppSystem.Func<StoreClient>>(managed)
                ?? throw new InvalidOperationException("顾客工厂委托转换失败。");
            _factoryRoots.Add(managed); _factoryRoots.Add(_factory);
        }
        registry.Add(NpcId, _factory); _registered = true;
        _log.Msg("[股市掮客探针] 已注册独立NPC标识；原版 shadyCustomerInformation 保持独立。");
    }

    private StoreClient Create()
    {
        if (_disposed || !_host.BrokerReady) throw new InvalidOperationException("存档正在切换，无法创建情报掮客。");
        var visit = _host.Engine?.GetBrokerVisit() ?? throw new InvalidOperationException("没有有效情报来访计划。");
        string identity = _host.BrokerSaveIdentity, visitId = visit.VisitId;
        int epoch = _epoch;
        var client = new StoreClient();
        StoreClientFactionSetup.InitBlackMarket(client);
        client.identifier = NpcId; client.displayName = NpcName;
        client.spriteName = ResolveAppearance(visit.AppearanceKey);
        if (_host.Engine!.SetBrokerAppearance(visitId, client.spriteName)) _host.PersistBrokerState();
        client.clientIntent = StoreClient.ClientIntent.INFORMATION_DEALER;
        client.serviceCost = visit.Price;
        client.CompleteClientCreation(true);
        // 完成原版通用初始化后，仅重置此专属顾客的交易和对话行为。
        client.identifier = NpcId; client.displayName = NpcName;
        client.clientIntent = StoreClient.ClientIntent.INFORMATION_DEALER;
        client.serviceCost = visit.Price; client.dismissable = true;
        client.arrestable = false; client.nonDismissableArrestable = false; client.nonShootable = true;
        client.isRegularSellDisable = true; client.isMultiBuyDisabled = true;
        client.CanReturn = false; client.CanReturnAsBuyer = false;
        client.clientBuyingIdList.Clear(); client.clientBuyingTagList.Clear();
        client.onDealAccepted = null; client.onItemBought = null; client.onItemSold = null;
        client.OnArrival = RootAction(() => { if (epoch == _epoch && Valid(identity, visitId)) Arrived(client, visitId, identity); });
        client.OnDismiss = RootAction(() => { if (epoch == _epoch) Departed(identity, visitId); });
        string publicText = PublicOffer(visit);
        client.mainDialogue = Text(publicText);
        client.repeatDialogue = Text(publicText);
        client.mainDialogue.endAction = RootAction(() => OpenOffer(client, identity, visitId, epoch));
        client.repeatDialogue.endAction = RootAction(() => OpenOffer(client, identity, visitId, epoch));
        _visitRoots.Add(client.mainDialogue); _visitRoots.Add(client.repeatDialogue);
        client.allDoneDialogue = Text("这笔股市情报交易已经结束。已购线索会保存在你的情报记录中。");
        client.acceptDealDialogue = null; client.acceptDealDialogueLastLine = null;
        client.isMainDialogueStarted = false;
        _ownedClients.Add(client);
        _log.Msg($"[股市掮客探针] 工厂完成 visit={visitId}，intent={(int)client.clientIntent}，外观={client.spriteName}；未输出未购内容。");
        return client;
    }

    private static string PublicOffer(BrokerVisitView visit)
    {
        string source = visit.SourceName == visit.SourceIdentity || string.IsNullOrWhiteSpace(visit.SourceIdentity)
            ? visit.SourceName : $"{visit.SourceName}（{visit.SourceIdentity}）";
        return $"我替行业消息来源转售股市线索。\n消息来源：{source}\n情报类型：{visit.ProductKind}\n售价：{visit.Price} 游戏币。消息可能有真有假，购买后才能查看内容。";
    }

    private void OpenOffer(StoreClient client, string identity, string visitId, int epoch)
    {
        if (!CurrentVisit(client, identity, visitId, epoch)) return;
        var visit = _host.Engine?.GetBrokerVisit();
        if (visit == null || visit.Purchased || visit.Departed || visit.Expired) return;
        if (!client.isIntroduced) client.OnIntroduced();
        if (!CurrentVisit(client, identity, visitId, epoch)) return;
        NegociationUIManager.Instance?.OpenUI();
        UpdateUi();
        _log.Msg($"[股市掮客探针] 介绍末句进入原版购买界面 visit={visitId}，introduced={client.isIntroduced}。");
    }

    private string ResolveAppearance(string key)
    {
        var dict = SpriteDict.Instance ?? throw new InvalidOperationException("原版人物图像目录尚未就绪。");
        var names = new List<string>();
        if (dict.bmMaleEntries != null) foreach (var entry in dict.bmMaleEntries) if (entry != null && !string.IsNullOrWhiteSpace(entry.key) && entry.sprite != null) names.Add(entry.key);
        if (dict.bmFemaleEntries != null) foreach (var entry in dict.bmFemaleEntries) if (entry != null && !string.IsNullOrWhiteSpace(entry.key) && entry.sprite != null) names.Add(entry.key);
        names = names.Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToList();
        if (names.Count == 0) throw new InvalidOperationException("没有可用的原版掮客人物素材，本次来访延后。");
        if (names.Contains(key, StringComparer.Ordinal)) return key;
        uint hash = 2166136261;
        foreach (char c in key) hash = unchecked((hash ^ c) * 16777619);
        return names[(int)(hash % (uint)names.Count)];
    }

    private void Arrived(StoreClient client, string visitId, string identity)
    {
        if (!Valid(identity, visitId) || !IsBroker(client) || _host.CurrentClient?.Pointer != client.Pointer) return;
        var engine = _host.Engine!;
        bool wasArrived = engine.GetBrokerVisit()?.HasArrived == true;
        bool changed = engine.MarkBrokerArrived(visitId);
        if (changed && !wasArrived)
        {
            // 先标记待保存，失败后保留标记并在 Tick 重试，不能只相信内存里的 HasArrived。
            _pendingArrivalVisit = visitId;
            FlushArrivalPersistence();
            _log.Msg($"[股市掮客探针] 实际到访 visit={visitId}，三日来访冷却已记录。");
        }
    }

    private void FlushArrivalPersistence()
    {
        if (_pendingArrivalVisit.Length == 0) return;
        _host.PersistBrokerState();
        _pendingArrivalVisit = "";
    }

    private void UpdateUi()
    {
        if (_disposed || !IsBroker(_host.CurrentClient)) return;
        var view = _host.Engine?.GetBrokerVisit();
        var ui = NegociationUIManager.Instance;
        if (ui == null || view == null) return;
        if (ui.titleTMP != null) ui.titleTMP.text = "股市内幕交易";
        if (ui.itemName != null) ui.itemName.text = view.ProductKind;
        if (ui.bodyTMP != null) ui.bodyTMP.text = PublicOffer(view) + (_pendingArrivalVisit.Length > 0 ? "\n\n到访状态暂未保存，正在重试；保存成功后才能购买。" : !_host.CanBuyBrokerIntel ? "\n\n" + _host.BrokerTradeStatus : _host.Cash < view.Price ? "\n\n余额不足，暂时无法购买。" : "");
        if (ui.costTMP != null) ui.costTMP.text = $"{view.Price} 游戏币";
        if (ui.acceptOfferButtonButtonTMP != null) ui.acceptOfferButtonButtonTMP.text = view.Purchased ? "已购买" : "购买股市情报";
        if (ui.acceptOfferButtonButton != null) ui.acceptOfferButtonButton.interactable = _pendingArrivalVisit.Length == 0 && _host.CanBuyBrokerIntel && !_host.BlocksGameInput && !view.Purchased && !view.Expired && !view.Departed && _host.Cash >= view.Price;
        if (ui.acceptMultipleButton != null) ui.acceptMultipleButton.gameObject.SetActive(false);
        if (ui.wholesaleButton != null) ui.wholesaleButton.gameObject.SetActive(false);
    }

    private void Purchase()
    {
        // 此方法的调用者对专属顾客始终跳过原版购买，失败分支也不能落回原版扣款。
        if (_purchasing || _disposed || !_host.BrokerReady || !IsBroker(_host.CurrentClient)) return;
        var visit = _host.Engine?.GetBrokerVisit();
        if (visit == null) { ShowMessage("这条报价已失效，请稍后再来。", null); return; }
        string identity = _host.BrokerSaveIdentity, visitId = visit.VisitId, offerId = visit.OfferId;
        int epoch = _epoch;
        if (!Valid(identity, visitId) || _pendingArrivalVisit.Length > 0 || !_host.CanBuyBrokerIntel || _host.BlocksGameInput) { UpdateUi(); return; }
        _purchasing = true;
        try
        {
            _log.Msg($"[股市掮客探针] 截流原版购买 visit={visitId}，offer={offerId}，cashBefore={_host.Cash}。");
            var result = _host.PurchaseBrokerOffer(visitId, offerId);
            _log.Msg($"[股市掮客探针] 领域购买返回 success={result.Success}，cashAfter={_host.Cash}；原版购买已跳过。");
            if (!result.Success) { ShowMessage(result.Message, null); UpdateUi(); return; }
            var client = _host.CurrentClient;
            if (!IsBroker(client) || !Valid(identity, visitId)) return;
            var record = _host.Engine!.GetIntelRecords().FirstOrDefault(r => r.OfferId == offerId);
            string prediction = record?.Content ?? "这份已购情报已记入股市情报簿，请在记录中查看。";
            var predictionLine = Text("成交。你买到的股市线索是：\n" + prediction);
            var receiptLine = Text("我会把这份预测写成情报便签交给你。消息是否属实，要等后续事件验证；你也能在股市面板中查看这份已购记录。");
            receiptLine.endAction = RootAction(() => { if (CurrentVisit(client!, identity, visitId, epoch)) _host.GrantPurchasedTipOnce(offerId); });
            predictionLine.SetNextDialogue(receiptLine);
            client!.acceptDealDialogue = predictionLine;
            client.acceptDealDialogueLastLine = receiptLine;
            _visitRoots.Add(predictionLine); _visitRoots.Add(receiptLine);
            client!.CompleteBusiness();
            NegociationUIManager.Instance?.CloseUI();
            ShowDialogue(predictionLine);
        }
        catch (Exception ex) { Report(ex); }
        finally { _purchasing = false; }
    }

    private void ShowMessage(string text, Action? end)
    {
        var dialogUi = DialogUIManager.Instance;
        if (dialogUi == null) return;
        var dialog = Text(text);
        if (end != null) dialog.endAction = RootAction(end);
        _visitRoots.Add(dialog);
        ShowDialogue(dialog);
    }

    private static void ShowDialogue(Dialogue dialog)
    {
        var dialogUi = DialogUIManager.Instance;
        if (dialogUi == null) return;
        dialogUi.InterruptDialog();
        dialogUi.StartDialogue(dialog, true);
    }

    private void Departed(string identity, string visitId)
    {
        if (!Valid(identity, visitId)) return;
        var visit = _host.Engine!.GetBrokerVisit();
        if (visit?.Purchased == true) _host.GrantPurchasedTipOnce(visit.OfferId);
        if (_host.Engine.CloseBrokerVisit(visitId)) _host.PersistBrokerState();
        _log.Msg($"[股市掮客探针] 离店 visit={visitId}；已处理便签发放兜底。");
    }

    private bool Valid(string identity, string visitId) => !_disposed && _host.BrokerReady && identity == _host.BrokerSaveIdentity && _host.Engine?.GetBrokerVisit()?.VisitId == visitId;
    private bool CurrentVisit(StoreClient client, string identity, string visitId, int epoch) => epoch == _epoch && Valid(identity, visitId) && _host.CurrentClient?.Pointer == client.Pointer;
    private static bool IsBroker(StoreClient? client) => client != null && client.identifier == NpcId;
    private static Dialogue Text(string value) => new Dialogue().SetText(NpcName, value);
    private Il2CppSystem.Action RootAction(Action managed)
    {
        Action guarded = () => { try { managed(); } catch (Exception ex) { Report(ex); } };
        var native = DelegateSupport.ConvertDelegate<Il2CppSystem.Action>(guarded) ?? throw new InvalidOperationException("原生情报回调转换失败。");
        _visitRoots.Add(managed); _visitRoots.Add(guarded); _visitRoots.Add(native); return native;
    }

    private void RemoveQueued()
    {
        if (_queue?.clientStack == null) return;
        for (int i = _queue.clientStack.Count - 1; i >= 0; i--) if (IsBroker(_queue.clientStack[i])) _queue.clientStack.RemoveAt(i);
        _queue = null;
    }
    private void ClearVisitCallbacks()
    {
        foreach (var client in _ownedClients) { client.OnArrival = null; client.OnDismiss = null; }
        foreach (var value in _visitRoots) if (value is Dialogue dialog) { dialog.startAction = null; dialog.endAction = null; }
        _ownedClients.Clear(); _visitRoots.Clear();
    }
    internal void Reset()
    {
        _epoch++;
        RemoveQueued(); ClearVisitCallbacks(); _identity = ""; _visitId = ""; _pendingArrivalVisit = ""; _purchasing = false; _lastFault = "";
    }
    public void Dispose()
    {
        if (_disposed) return;
        Reset(); _disposed = true;
        if (_harmony != null) foreach (var item in _patches) _harmony.Unpatch(item.Original, item.Patch);
        _patches.Clear();
        try
        {
            var registry = StoreClientListDict.storeClientDict;
            if (_registered && registry != null && registry.ContainsKey(NpcId) && registry[NpcId]?.Pointer == _factory?.Pointer) registry.Remove(NpcId);
        }
        catch (Exception ex) { _log.Warning("[股市掮客] 工厂清理失败：" + ex.Message); }
        _factoryRoots.Clear(); _factory = null; _registered = false;
        if (Current == this) Current = null;
    }
    private void Report(Exception ex)
    {
        string message = ex.GetType().Name + ": " + ex.Message;
        if (_lastFault == message) return;
        _lastFault = message; _log.Warning("[股市掮客探针] " + message);
    }
    private void Patch(Type type, string name, string patch, bool prefix)
    {
        var original = AccessTools.Method(type, name) ?? throw new MissingMethodException(type.FullName, name);
        var handler = AccessTools.Method(typeof(IntelBrokerAdapter), patch) ?? throw new MissingMethodException(patch);
        _harmony!.Patch(original, prefix ? new HarmonyMethod(handler) : null, prefix ? null : new HarmonyMethod(handler));
        _patches.Add((original, handler));
    }
    private static void Generated() => Current?.Tick();
    private static bool TrackPrefix(StoreClient __0) => !IsBroker(__0);
    private static void IntroducedPrefix(StoreClient __instance)
    {
        var adapter = Current;
        if (adapter == null || !IsBroker(__instance)) return;
        try
        {
            var visit = adapter._host.Engine?.GetBrokerVisit();
            if (visit != null) adapter.Arrived(__instance, visit.VisitId, adapter._host.BrokerSaveIdentity);
        }
        catch (Exception ex) { adapter.Report(ex); }
    }
    private static void IntroducedPostfix(StoreClient __instance) { if (IsBroker(__instance)) InformationUiPostfix(); }
    private static void InformationUiPostfix()
    {
        var adapter = Current;
        try { adapter?.UpdateUi(); }
        catch (Exception ex) { adapter?.Report(ex); }
    }
    private static bool PurchasePrefix()
    {
        var adapter = Current;
        if (!IsBroker(adapter?._host.CurrentClient)) return true;
        try { adapter?.Purchase(); }
        catch (Exception ex) { adapter?.Report(ex); }
        return false;
    }
    private static bool OtherOfferPrefix() => !IsBroker(Current?._host.CurrentClient);
    private static bool NoMerchandisePrefix(StoreClient __instance, ref bool __result)
    {
        if (!IsBroker(__instance)) return true;
        __result = false; return false;
    }
}
