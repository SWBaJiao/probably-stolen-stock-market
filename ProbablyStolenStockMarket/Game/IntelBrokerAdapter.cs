using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Il2Cpp;
using Il2CppInterop.Runtime;
using Il2CppTMPro;
using MelonLoader;
using ProbablyStolenStockMarket.Market;

namespace ProbablyStolenStockMarket.Game;

// 专属掮客沿用原版来访和情报商界面，资金只交由股市领域统一处理。
internal sealed class IntelBrokerAdapter : IDisposable
{
    internal const string NpcId = "probably_stolen_stock.intel_broker";
    internal const string JianqingNpcId = "probably_stolen_stock.jianqing_broker";
    private readonly GameBridge _host;
    private readonly MelonLogger.Instance _log;
    private readonly JianqingVisual _visual;
    private readonly WenzhouVisual _wenzhouVisual;
    private readonly BrokerSession[] _sessions;
    private readonly List<(MethodBase Original, MethodInfo Patch)> _patches = new();
    private HarmonyLib.Harmony? _harmony;
    private bool _disposed;
    private string _lastFault = "";
    private static IntelBrokerAdapter? Current;

    internal IntelBrokerAdapter(GameBridge host, MelonLogger.Instance log)
    {
        _host = host; _log = log; _visual = new JianqingVisual(log); _wenzhouVisual = new WenzhouVisual(log);
        _sessions = new[] {
            new BrokerSession(host, log, BrokerIds.Wenzhou, NpcId, "闻舟", () => _wenzhouVisual.EnsureRegistered() ? WenzhouVisual.SpriteKey : null),
            new BrokerSession(host, log, BrokerIds.Jianqing, JianqingNpcId, "剑清酱", () => _visual.EnsureRegistered() ? JianqingVisual.SpriteKey : null)
        };
    }
    internal static string? GetBrokerId(StoreClient? client) => client?.identifier switch
    { NpcId => BrokerIds.Wenzhou, JianqingNpcId => BrokerIds.Jianqing, _ => null };
    internal static string GetBrokerName(string brokerId) => brokerId == BrokerIds.Jianqing ? "剑清酱" : "闻舟";
    private BrokerSession? Session(StoreClient? client) => _sessions.FirstOrDefault(s => s.IsBroker(client));
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
            Patch(typeof(NegociationUIManager), "RefreshUI", nameof(InformationUiPostfix), false);
            Patch(typeof(NegociationUIManager), "Update", nameof(InformationUiPostfix), false);
            Patch(typeof(NegociationUIManager), "OnOfferAcceptClick", nameof(PurchasePrefix), true);
            Patch(typeof(NegociationUIManager), "OnAcceptMultipleButtonClick", nameof(OtherOfferPrefix), true);
            Patch(typeof(NegociationUIManager), "OnWholesaleClick", nameof(OtherOfferPrefix), true);
            Patch(typeof(StoreClient), "IsClientBuyingThisItem", nameof(NoMerchandisePrefix), true);
            _log.Msg("[股市掮客探针] 已安装闻舟与剑清酱独立工厂、队尾来访和原版情报商购买钩子，等待游戏内验证。");
        }
        catch { Dispose(); throw; }
    }

    internal void Tick() { if (!_disposed) foreach (var session in _sessions) session.Tick(); }
    internal void Reset()
    {
        foreach (var session in _sessions)
            try { session.Reset(); } catch (Exception ex) { Report(ex); }
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var session in _sessions)
            try { session.Dispose(); } catch (Exception ex) { Report(ex); }
        if (_harmony != null) foreach (var item in _patches) _harmony.Unpatch(item.Original, item.Patch);
        _patches.Clear();
        try { _visual.Dispose(); } catch (Exception ex) { Report(ex); }
        try { _wenzhouVisual.Dispose(); } catch (Exception ex) { Report(ex); }
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
    private static bool TrackPrefix(StoreClient __0) => GetBrokerId(__0) == null;
    private static void IntroducedPrefix(StoreClient __instance) => HandleIntroduced(__instance, false);
    private static void IntroducedPostfix(StoreClient __instance) => HandleIntroduced(__instance, true);
    private static void HandleIntroduced(StoreClient client, bool after)
    {
        var adapter = Current;
        try { adapter?.Session(client)?.Introduced(client, after); }
        catch (Exception ex) { adapter?.Report(ex); }
    }
    private static void InformationUiPostfix(NegociationUIManager __instance)
    {
        var adapter = Current;
        try { adapter?.Session(adapter._host.CurrentClient)?.UpdateUi(__instance); }
        catch (Exception ex) { adapter?.Report(ex); }
    }
    private static bool PurchasePrefix()
    {
        var adapter = Current;
        if (GetBrokerId(adapter?._host.CurrentClient) == null) return true;
        try { adapter?.Session(adapter._host.CurrentClient)?.Purchase(); }
        catch (Exception ex) { adapter?.Report(ex); }
        return false;
    }
    private static bool OtherOfferPrefix() => GetBrokerId(Current?._host.CurrentClient) == null;
    private static bool NoMerchandisePrefix(StoreClient __instance, ref bool __result)
    {
        if (GetBrokerId(__instance) == null) return true;
        __result = false; return false;
    }

    private sealed class BrokerSession : IDisposable
    {
        private readonly GameBridge _host;
        private readonly MelonLogger.Instance _log;
        private readonly List<object> _factoryRoots = new();
        private readonly List<object> _visitRoots = new();
        private readonly List<StoreClient> _ownedClients = new();
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

        private readonly string _brokerId, _npcId, _npcName;
        private readonly Func<string?> _resolveVisual;
        private bool IsJianqing => _brokerId == BrokerIds.Jianqing;
        internal BrokerSession(GameBridge host, MelonLogger.Instance log, string brokerId, string npcId, string npcName, Func<string?> resolveVisual)
        { _host = host; _log = log; _brokerId = brokerId; _npcId = npcId; _npcName = npcName; _resolveVisual = resolveVisual; }

        internal void Tick()
        {
            if (_disposed || !_host.BrokerReady) return;
            try { QueueDueVisit(); }
            catch (Exception ex) { Report(ex); }
        }

        private void QueueDueVisit()
        {
            if (_identity != _host.BrokerSaveIdentity) { Reset(); _identity = _host.BrokerSaveIdentity; }
            Register();
            var store = PlayerStore.Instance;
            if (store == null || store.storeClientManager == null) return;
            var engine = _host.Engine;
            if (engine == null) return;
            FlushArrivalPersistence();
            var manager = store.storeClientManager;
            // BrokerReady 和上方存档身份检查已确认当前存档，保留其队列引用以清理过期来访。
            if (_queue != null && _queue.Pointer != manager.Pointer) RemoveQueued();
            _queue = manager;
            var oldVisit = engine.GetBrokerVisit(_brokerId);
            if (oldVisit is { HasArrived: true, Departed: false } && (!oldVisit.IsPhoneBooking || oldVisit.Purchased)
                && !IsBroker(_host.CurrentClient) && manager.clientStack != null)
            {
                bool queued = false;
                foreach (var client in manager.clientStack) if (IsBroker(client)) queued = true;
                // 顾客实例不随存档恢复；已付款来访缺少实际顾客时结束占位，未付款电话预约继续正常复建。
                if (!queued && engine.CloseBrokerVisit(oldVisit.VisitId, _brokerId))
                {
                    _host.PersistBrokerState();
                    _log.Msg($"[股市电话预约] {_npcName}已结束没有实际顾客的来访记录，可重新电话预约。");
                }
            }
            var visit = engine.GetDueBrokerPhoneVisit(_host.GetBrokerScheduledEvents(), _brokerId);
            _host.PersistBrokerState();
            if (visit == null || visit.Purchased || visit.Departed || visit.Expired)
            {
                RemoveQueued();
                return;
            }
            // 电话只记录次日预约，直到次日正常开门才追加客户，不触碰卷帘或营业状态。
            if (store.storeState != PlayerStore.StoreState.OPEN) return;
            if (_visitId.Length > 0 && _visitId != visit.VisitId) { RemoveQueued(); ClearVisitCallbacks(); }
            _visitId = visit.VisitId;
            if (IsBroker(_host.CurrentClient)) { Arrived(_host.CurrentClient!, visit.VisitId, _identity); UpdateUi(); return; }
            if (manager.clientStack == null) return;
            _queue = manager;
            foreach (var client in manager.clientStack) if (IsBroker(client)) return;
            var created = StoreClientListDict.CreateStoreClient(_npcId);
            if (!IsBroker(created)) throw new InvalidOperationException("专属情报商工厂返回了其他顾客，已停止本次排队。");
            int before = manager.clientStack.Count;
            manager.AddClient(created);
            _queue = manager;
            bool inserted = false;
            foreach (var client in manager.clientStack) if (client?.Pointer == created.Pointer) inserted = true;
            if (!inserted) throw new InvalidOperationException("原版队列没有读回新掮客，请稍后重试。");
            _log.Msg($"[股市掮客探针] 电话预约到期，队尾追加 broker={_brokerId} visit={visit.VisitId}，原队列={before}，现队列={manager.clientStack.Count}；未替换剧情顾客。");
            _lastFault = "";
        }

        private void Register()
        {
            var registry = StoreClientListDict.storeClientDict;
            if (registry == null) throw new InvalidOperationException("原版顾客目录尚未初始化。");
            if (_registered && registry.ContainsKey(_npcId) && registry[_npcId]?.Pointer == _factory?.Pointer) return;
            if (registry.ContainsKey(_npcId)) throw new InvalidOperationException("专属顾客标识已被其他工厂占用，未覆盖原值。");
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
            registry.Add(_npcId, _factory); _registered = true;
            _log.Msg($"[股市掮客探针] 已注册独立NPC {_npcId}；原版 shadyCustomerInformation 保持独立。");
        }

        private StoreClient Create()
        {
            if (_disposed || !_host.BrokerReady) throw new InvalidOperationException("存档正在切换，无法创建情报掮客。");
            var visit = _host.Engine?.GetBrokerVisit(_brokerId) ?? throw new InvalidOperationException("没有有效情报来访计划。");
            string identity = _host.BrokerSaveIdentity, visitId = visit.VisitId;
            int epoch = _epoch;
            var client = new StoreClient();
            _ownedClients.Add(client);
            StoreClientFactionSetup.InitBlackMarket(client);
            client.identifier = _npcId; client.displayName = _npcName + " · 行情掮客";
            var customSprite = _resolveVisual();
            client.spriteName = customSprite ?? ResolveAppearance(visit.AppearanceKey);
            if (customSprite == null) _log.Warning($"[{_npcName}] 专属人物图像暂未就绪，本次使用原版人物素材。");
            if (_host.Engine!.SetBrokerAppearance(visitId, client.spriteName, _brokerId)) _host.PersistBrokerState();
            client.clientIntent = StoreClient.ClientIntent.INFORMATION_DEALER;
            client.serviceCost = visit.Price;
            client.CompleteClientCreation(true);
            // 完成原版通用初始化后，仅重置此专属顾客的交易和对话行为。
            client.identifier = _npcId; client.displayName = _npcName + " · 行情掮客";
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
            var offerDialogue = Text(publicText);
            offerDialogue.endAction = RootAction(() => OpenOffer(client, identity, visitId, epoch));
            if (_host.Engine.GetBrokerState(_brokerId)?.HasMetBroker != true)
            {
                var introduction = Text(IsJianqing
                    ? "锵锵！剑清酱到啦！电话里说的就是我。我的消息遍布各行各业，行情里那些弯弯绕绕，我可有自己的独门眼光。\n先说好，风声归风声，最后怎么投资当然得你自己拿主意。要听听我这次带来的消息吗？"
                    : "电话里联系过了，正式认识一下。我叫闻舟，做的是行情消息的生意。药剂师、矿工、维修工——各行都有我的消息来源。\n我只替他们传话，风声有真有假，消息值不值钱，还得你自己判断。");
                introduction.SetNextDialogue(offerDialogue);
                client.mainDialogue = introduction;
                _visitRoots.Add(introduction);
            }
            else client.mainDialogue = offerDialogue;
            client.repeatDialogue = Text(IsJianqing ? "又见面啦！剑清酱的独家风声已经备好了。\n" + publicText : publicText);
            client.repeatDialogue.endAction = RootAction(() => OpenOffer(client, identity, visitId, epoch));
            _visitRoots.Add(offerDialogue); _visitRoots.Add(client.repeatDialogue);
            client.allDoneDialogue = Text("这笔股市情报交易已经结束。已购线索会保存在你的情报记录中。");
            client.acceptDealDialogue = null; client.acceptDealDialogueLastLine = null;
            client.isMainDialogueStarted = false;
            _log.Msg($"[股市掮客探针] 工厂完成 broker={_brokerId} visit={visitId}，intent={(int)client.clientIntent}，外观={client.spriteName}；未输出未购内容。");
            return client;
        }

        private string PublicOffer(BrokerVisitView visit)
        {
            string source = visit.SourceName == visit.SourceIdentity || string.IsNullOrWhiteSpace(visit.SourceIdentity)
                ? visit.SourceName : $"{visit.SourceName}（{visit.SourceIdentity}）";
            return (IsJianqing ? "剑清酱亲自带来的行情消息，我的眼光可是很独到的！\n" : "我替行业消息来源转售股市线索。\n") + $"消息来源：{source}\n情报类型：{visit.ProductKind}\n售价：{visit.Price} 游戏币。消息可能有真有假，购买后才能查看内容。";
        }

        private void OpenOffer(StoreClient client, string identity, string visitId, int epoch)
        {
            if (!CurrentVisit(client, identity, visitId, epoch)) return;
            var visit = _host.Engine?.GetBrokerVisit(_brokerId);
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
            bool wasArrived = engine.GetBrokerVisit(_brokerId)?.HasArrived == true;
            bool changed = engine.MarkBrokerArrived(visitId, _brokerId);
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

        internal void UpdateUi(NegociationUIManager? source = null)
        {
            if (_disposed || !_host.BrokerReady || !IsBroker(_host.CurrentClient)) return;
            var view = _host.Engine?.GetBrokerVisit(_brokerId);
            var ui = source ?? NegociationUIManager.Instance;
            if (ui == null || view == null || view.VisitId != _visitId || _identity != _host.BrokerSaveIdentity
                || ui.uiObject == null || !ui.uiObject.activeInHierarchy) return;
            // 原版周期刷新会在初始化之后重写标题，必须在原生刷新返回后统一应用专属文案。
            SetText(ui.titleTMP, IsJianqing ? "剑清酱 · 股市内幕交易" : "股市内幕交易");
            SetText(ui.itemName, view.ProductKind);
            SetText(ui.bodyTMP, PublicOffer(view) + (_pendingArrivalVisit.Length > 0 ? "\n\n到访状态暂未保存，正在重试；保存成功后才能购买。" : !_host.CanBuyBrokerIntel ? "\n\n" + _host.BrokerTradeStatus : _host.Cash < view.Price ? "\n\n余额不足，暂时无法购买。" : ""));
            SetText(ui.costTMP, $"{view.Price} 游戏币");
            SetText(ui.acceptOfferButtonButtonTMP, view.Purchased ? "已购买" : "购买股市情报");
            bool canPurchase = !_purchasing && _pendingArrivalVisit.Length == 0 && _host.CanBuyBrokerIntel && !_host.BlocksGameInput && !view.Purchased && !view.Expired && !view.Departed && _host.Cash >= view.Price;
            if (ui.acceptOfferButtonButton != null && ui.acceptOfferButtonButton.interactable != canPurchase) ui.acceptOfferButtonButton.interactable = canPurchase;
            if (ui.acceptMultipleButton != null && ui.acceptMultipleButton.gameObject.activeSelf) ui.acceptMultipleButton.gameObject.SetActive(false);
            if (ui.wholesaleButton != null && ui.wholesaleButton.gameObject.activeSelf) ui.wholesaleButton.gameObject.SetActive(false);
        }

        private static void SetText(TextMeshProUGUI? label, string text)
        {
            if (label != null && label.text != text) label.text = text;
        }

        internal void Purchase()
        {
            // 此方法的调用者对专属顾客始终跳过原版购买，失败分支也不能落回原版扣款。
            if (_purchasing || _disposed || !_host.BrokerReady || !IsBroker(_host.CurrentClient)) return;
            var visit = _host.Engine?.GetBrokerVisit(_brokerId);
            if (visit == null) { ShowMessage("这条报价已失效，请稍后再来。", null); return; }
            string identity = _host.BrokerSaveIdentity, visitId = visit.VisitId, offerId = visit.OfferId;
            int epoch = _epoch;
            if (!Valid(identity, visitId) || _pendingArrivalVisit.Length > 0 || !_host.CanBuyBrokerIntel || _host.BlocksGameInput) { UpdateUi(); return; }
            _purchasing = true;
            try
            {
                _log.Msg($"[股市掮客探针] 截流原版购买 visit={visitId}，offer={offerId}，cashBefore={_host.Cash}。");
                var result = _host.PurchaseBrokerOffer(visitId, offerId, _brokerId);
                _log.Msg($"[股市掮客探针] 领域购买返回 success={result.Success}，cashAfter={_host.Cash}；原版购买已跳过。");
                if (!result.Success) { ShowMessage(result.Message, null); UpdateUi(); return; }
                var client = _host.CurrentClient;
                if (!IsBroker(client) || !Valid(identity, visitId)) return;
                var record = _host.Engine!.GetIntelRecords().FirstOrDefault(r => r.OfferId == offerId);
                string prediction = record?.Content ?? "这份已购情报已记入股市情报簿，请在记录中查看。";
                var predictionLine = Text((IsJianqing ? "成交！剑清酱的消息可要听好啦：\n" : "成交。你买到的股市线索是：\n") + prediction);
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
            var visit = _host.Engine!.GetBrokerVisit(_brokerId);
            if (visit?.Purchased == true) _host.GrantPurchasedTipOnce(visit.OfferId);
            if (_host.Engine.CloseBrokerVisit(visitId, _brokerId)) _host.PersistBrokerState();
            _log.Msg($"[股市掮客探针] 离店 visit={visitId}；已处理便签发放兜底。");
        }

        private bool Valid(string identity, string visitId) => !_disposed && _host.BrokerReady && identity == _host.BrokerSaveIdentity && _host.Engine?.GetBrokerVisit(_brokerId)?.VisitId == visitId;
        private bool CurrentVisit(StoreClient client, string identity, string visitId, int epoch) => epoch == _epoch && Valid(identity, visitId) && _host.CurrentClient?.Pointer == client.Pointer;
        internal bool IsBroker(StoreClient? client) => client != null && client.identifier == _npcId;
        private Dialogue Text(string value) => new Dialogue().SetText(_npcName, value);
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
            foreach (var client in _ownedClients)
                try { client.OnArrival = null; client.OnDismiss = null; } catch (Exception ex) { Report(ex); }
            foreach (var value in _visitRoots) if (value is Dialogue dialog)
                try { dialog.startAction = null; dialog.endAction = null; } catch (Exception ex) { Report(ex); }
            _ownedClients.Clear(); _visitRoots.Clear();
        }
        internal void Reset()
        {
            _epoch++;
            try { RemoveQueued(); } catch (Exception ex) { Report(ex); }
            ClearVisitCallbacks(); _identity = ""; _visitId = ""; _pendingArrivalVisit = ""; _purchasing = false; _lastFault = "";
        }
        public void Dispose()
        {
            if (_disposed) return;
            Reset(); _disposed = true;
            try
            {
                var registry = StoreClientListDict.storeClientDict;
                if (_registered && registry != null && registry.ContainsKey(_npcId) && registry[_npcId]?.Pointer == _factory?.Pointer) registry.Remove(_npcId);
            }
            catch (Exception ex) { _log.Warning("[股市掮客] 工厂清理失败：" + ex.Message); }
            _factoryRoots.Clear(); _factory = null; _registered = false;
        }
        private void Report(Exception ex)
        {
            string message = ex.GetType().Name + ": " + ex.Message;
            if (_lastFault == message) return;
            _lastFault = message; _log.Warning($"[股市掮客探针:{_npcName}] " + message);
        }
        internal void Introduced(StoreClient client, bool after)
        {
            if (!IsBroker(client)) return;
            var engine = _host.Engine;
            var visit = engine?.GetBrokerVisit(_brokerId);
            if (visit == null) return;
            if (!after) { Arrived(client, visit.VisitId, _host.BrokerSaveIdentity); return; }
            if (client.isIntroduced && _host.CurrentClient?.Pointer == client.Pointer && engine != null
                && engine.GetBrokerState(_brokerId)?.HasMetBroker != true && engine.MarkBrokerIntroduced(visit.VisitId, _brokerId))
            {
                _host.PersistBrokerState();
                _log.Msg($"[股市掮客] {_npcName}首次自我介绍已完成并记录。");
            }
            UpdateUi();
        }
    }
}
