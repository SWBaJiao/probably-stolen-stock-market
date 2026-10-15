using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using HarmonyLib;
using Il2Cpp;
using MelonLoader;
using MelonModTemplate.Contracts;
using MelonModTemplate.Market;

namespace MelonModTemplate.Game;

public sealed class GameBridge : IMarketHost, IWallet, IDisposable
{
    private const string SaveKey = "probably_stolen.stock_market.v1";
    private const string TerminalName = "股市面板";
    private readonly MelonLogger.Instance _log;
    private readonly MarketConfig _config;
    private readonly TerminalVisual _terminalVisual;
    private IntelBrokerAdapter? _broker;
    private IntelDeliveryAdapter? _intelDelivery;
    private IntelCalendarAdapter? _intelCalendar;
    private IntelPhoneAdapter? _intelPhone;
    private readonly int _threadId = Environment.CurrentManagedThreadId;
    private HarmonyLib.Harmony? _harmony;
    private PlayerStore? _store;
    private AdapterSave _save = new();
    private string _identity = "";
    private string _fault = "";
    private bool _disposed;
    private bool _monetaryOperation;
    private bool _loadedPending;
    private bool _loading;
    private bool _roundTripVerified;
    private PlayerStore.StoreState? _observedStoreState;
    private bool _inMenu = true;
    private IntPtr _loadDictionaryPointer;
    private IntPtr _dismissPending;
    private IntPtr _clientPointer;
    private long _nextPoll;
    private long _nextTerminalScan;
    private TerminalGrantPolicy _terminalGrant = new();
    private GameItem? _trackedTerminal;
    private string _lastInventoryProbe = "";
    private RumorView? _offer;
    internal static GameBridge? Current { get; private set; }
    public MarketEngine? Engine { get; private set; }
    public event Action? OpenPanelRequested;
    public Func<bool>? IsPanelVisible { get; set; }
    internal bool BlocksGameInput => IsPanelVisible?.Invoke() == true || _intelPhone?.BlocksGameInput == true;
    public int Cash => _store?.GetCash() ?? 0;
    public bool CanTrade => !_disposed && !_inMenu && !_loading && Engine != null && Engine.State.StorageVerified && !Engine.State.TradingFrozen && _fault.Length == 0 && IsCurrentStore();
    public string Status => _fault.Length > 0 ? _fault : Engine == null ? "请进入游戏存档。" : Engine.State.TradingFrozen ? Engine.State.FreezeReason : !Engine.State.StorageVerified ? "已启用严格存档验证：请正常保存并重新载入此存档，验证后开放现金交易。" : _config.RequireSaveReloadVerification ? "原版存档读回及资金一致性已通过；可交易。" : "现货买卖已开放；使用原版资金，投资记录随游戏存档保存。";
    public string SellerDescription { get; private set; } = "当前没有行情掮客";
    public string SellerReputation => _offer == null ? "暂无可验证情报记录" : $"{_offer.Identity} · {(_offer.IsGroupIdentity ? "该类来源" : "个人")}记录：属实 {_offer.Correct} / 失实 {_offer.Incorrect} · 参考可信度 {_offer.Reliability:P0}";
    public string OfferDescription => _offer == null ? "持有股市面板后，请用游戏电话预约行情掮客，次日开门后上门售卖。" : _offer.Purchased ? _offer.Content : $"消息来源：{_offer.Identity}。请关闭股市面板，与行情掮客交谈购买；付款前仅展示商品类别。";
    public string BrokerContactDescription
    {
        get
        {
            if (!OwnsStockTerminal()) return "持有本存档的股市面板道具后，解锁掮客电话预约。";
            var booking = Engine?.GetBrokerPhoneBooking();
            string status = booking == null ? "暂无预约。" : booking.Status switch
            {
                BrokerPhoneBookingStatus.Booked => $"已约第 {booking.ArrivalDay} 日到访，开门后在队尾等待。",
                BrokerPhoneBookingStatus.Arrived => "已预约的掮客正在来访。",
                BrokerPhoneBookingStatus.Completed => "本次情报接待结束后，可再拨电话预约次日。",
                BrokerPhoneBookingStatus.Cancelled => "本次预约已取消，可拨电话查询。",
                _ => "之前的预约已过期，可再拨电话查询。"
            };
            return $"闻舟 · 行情掮客：{IntelPhoneAdapter.PhoneNumber}（电话簿可快速拨号）\n{status}";
        }
    }
    public int? OfferCost => _offer?.Price;
    public string? OfferId => _offer?.Id;
    public bool CanBuyRumor => false;
    internal bool BrokerReady => !_disposed && !_inMenu && !_loading && Engine != null && IsCurrentStore();
    internal bool CanBuyBrokerIntel => CanTrade && _roundTripVerified;
    internal string BrokerTradeStatus => !_roundTripVerified ? "股市内幕付款仍需正常保存并重新载入，确认此存档恢复后开放。" : Status;
    internal string BrokerSaveIdentity => _identity;
    internal StoreClient? CurrentClient => Client;
    internal PlayerStore? CurrentStore => _store;
    internal IReadOnlyList<ScheduledEvent> GetBrokerScheduledEvents() => ScheduledEvents();
    internal void PersistBrokerState() => Persist();
    internal void GrantPurchasedTipOnce(string offerId) => _intelDelivery?.GrantPurchasedTipOnce(offerId);

    public GameBridge(MelonLogger.Instance logger, MarketConfig config) { _log = logger; _config = config; _terminalVisual = new TerminalVisual(logger); }

    public void Initialize(HarmonyLib.Harmony harmony)
    {
        if (Current != null) throw new InvalidOperationException("游戏适配器已初始化。");
        Current = this; _harmony = harmony;
        try
        {
            GamePatches.Install(harmony, _log);
            IconRenderPatches.Install(harmony, _log);
            _intelDelivery = new IntelDeliveryAdapter(this, _log);
            _intelCalendar = new IntelCalendarAdapter(this, _intelDelivery, _log);
            _broker = new IntelBrokerAdapter(this, _log);
            _intelPhone = new IntelPhoneAdapter(this, _log);
            _broker.Initialize(harmony);
            _intelCalendar.Initialize(harmony);
            _intelPhone.Initialize(harmony);
        }
        catch
        {
            _intelPhone?.Dispose(); _broker?.Dispose(); _intelCalendar?.Dispose(); _intelDelivery?.Dispose();
            IconRenderPatches.Uninstall(harmony);
            GamePatches.Uninstall(harmony); Current = null; throw;
        }
        _log.Msg("[股市探针] 已安装适配钩子；未运行游戏验收前不视为适配成功。");
    }

    public void Tick()
    {
        if (_disposed || Environment.CurrentManagedThreadId != _threadId || Environment.TickCount64 < _nextPoll) return;
        _nextPoll = Environment.TickCount64 + 1000;
        try
        {
            if (_inMenu || _loading || !PlayerStore.IsInstanceExist()) return;
            var store = PlayerStore.Instance;
            if (store == null) return;
            string identity = $"{store.saveSlotId}:{store.runID}:{store.runNumber}";
            if (_store == null || _identity != identity || _loadedPending)
            {
                bool actualLoad = _loadedPending; _loadedPending = false;
                Attach(store, identity, actualLoad);
            }
            ObserveStoreState();
            RefreshCustomer();
            if (Engine != null)
            {
                if (Engine.AdvanceDay(StoreStation.GetDayCounter())) Persist();
                Engine.ResolveRumors(ScheduledEvents());
                _broker?.Tick();
                _intelDelivery?.Tick();
                Persist();
                if (Environment.TickCount64 >= _nextTerminalScan)
                { _nextTerminalScan = Environment.TickCount64 + 10000; EnsureTerminal(false); }
            }
        }
        catch (Exception ex) { Fault("游戏适配异常", ex); }
    }

    private void Attach(PlayerStore store, string identity, bool actualLoad)
    {
        ResetBrokerRuntime();
        _observedStoreState = null;
        _store = store; _identity = identity; _fault = ""; _offer = null;
        _terminalGrant = new TerminalGrantPolicy(); _trackedTerminal = null; _lastInventoryProbe = ""; _nextTerminalScan = 0;
        bool restored = false;
        if (store.modData != null && store.modData.ContainsKey(SaveKey))
        {
            var serialized = store.modData[SaveKey];
            _save = JsonSerializer.Deserialize<AdapterSave>(serialized) ?? throw new InvalidOperationException("股市存档为空。");
            if (_save.Version != 1 || _save.SaveIdentity != identity) throw new InvalidOperationException("股市存档版本或身份不匹配，已关闭交易。");
            Engine = new MarketEngine(MarketConfig.Deserialize(_config.Serialize()), MarketEngine.Deserialize(_save.MarketJson, _config));
            bool freshDictionary = _loadDictionaryPointer == IntPtr.Zero || store.modData.Pointer != _loadDictionaryPointer;
            restored = actualLoad && freshDictionary && _save.WrittenBySave && !string.IsNullOrWhiteSpace(_save.ProbeNonce) && _save.SavedCash == store.GetCash();
        }
        else
        {
            _save = new AdapterSave { SaveIdentity = identity };
            Engine = new MarketEngine(MarketConfig.Deserialize(_config.Serialize()));
            Engine.State.SaveIdentity = identity;
        }
        // 每次附加按本机配置决定访问权限，不信任存档中的旧解锁布尔值。
        Engine.Diagnostic += message => _log.Warning("[股市映射] " + message);
        _roundTripVerified = restored;
        Engine.ApplyStorageAccess(restored);
        if (actualLoad && _save.WrittenBySave && _save.SavedCash != store.GetCash())
            _log.Warning($"[股市探针] 读回现金不匹配：记录 {_save.SavedCash}，当前 {store.GetCash()}，严格验证={_config.RequireSaveReloadVerification}。");
        Engine.AdvanceDay(StoreStation.GetDayCounter());
        var manager = StoreStation.Instance?.storeEventManager;
        if (manager != null) foreach (var active in manager.GetActiveEvents()) EventActivated(manager, active, false);
        _log.Msg("[股市背包探针] 发放入口改为 EmporiumEntry.Instance.invElement；PlayerStore.gridInv 不再用于发放。");
        _log.Msg($"[股市探针] 存档 {identity}，原版 LoadGame={actualLoad}，nonce={_save.ProbeNonce}，读回验证={restored}，cash={store.GetCash()}");
        Persist();
        _log.Msg($"[股市交易] 严格读档验证={_config.RequireSaveReloadVerification}，可交易={CanTrade}，状态={Status}");
    }

    private void ResetBrokerRuntime()
    {
        _intelPhone?.Reset(); _broker?.Reset(); _intelCalendar?.Reset(); _intelDelivery?.Reset();
    }

    internal void BeginLoad()
    {
        ResetBrokerRuntime();
        _terminalVisual.ResetTracking();
        _trackedTerminal = null; _terminalGrant = new TerminalGrantPolicy();
        _loadDictionaryPointer = _store?.modData?.Pointer ?? IntPtr.Zero;
        _loading = true; _roundTripVerified = false; _offer = null; _store = null; Engine = null; _clientPointer = IntPtr.Zero; _dismissPending = IntPtr.Zero;
    }
    public void OnSceneChanged(string sceneName)
    {
        _inMenu = sceneName.IndexOf("Menu", StringComparison.OrdinalIgnoreCase) >= 0;
        if (_inMenu) { ResetBrokerRuntime(); _terminalVisual.ResetTracking(); _trackedTerminal = null; _terminalGrant = new TerminalGrantPolicy(); _store = null; Engine = null; _offer = null; _clientPointer = IntPtr.Zero; _dismissPending = IntPtr.Zero; }
        _nextPoll = 0;
    }
    internal void EndLoad(bool success)
    {
        _loading = false; _loadedPending = success; _nextPoll = 0;
        if (!success) _fault = "原版载入异常，股市未启用。";
    }
    internal void BeforeSave()
    {
        try
        {
            if (_store == null || Engine == null || _loading) return;
            _terminalVisual.RestoreForSave();
            _save.WrittenBySave = true; _save.SavedCash = Cash;
            Persist();
            _log.Msg($"[股市探针] 原版保存前写入 nonce={_save.ProbeNonce}，cash={Cash}，terminal={_save.TerminalItemId}");
        }
        catch (Exception ex) { Fault("股市保存准备失败", ex); }
    }
    internal void SaveCompleted(bool success)
    {
        _terminalVisual.ResumeAfterSave();
        if (!success) { _save.WrittenBySave = false; _log.Error("[股市探针] 原版 SaveGame 抛出异常，本次保存不能用于解锁。"); }
        else _log.Msg(_config.RequireSaveReloadVerification ? "[股市探针] 原版 SaveGame 无异常返回；随后 LoadGame 读回新的字典与相同现金可完成严格验证。" : "[股市探针] 原版 SaveGame 无异常返回；现货交易已默认开放，仍记录读档验证结果。");
    }

    private void Persist()
    {
        if (_store == null || Engine == null) throw new InvalidOperationException("未附加存档。");
        if (_store.modData == null) _store.modData = new Il2CppSystem.Collections.Generic.Dictionary<string, string>();
        _save.MarketJson = Engine.Serialize();
        string json = JsonSerializer.Serialize(_save);
        _store.modData[SaveKey] = json;
        if (_store.modData[SaveKey] != json) throw new InvalidOperationException("modData 内存写回失败。");
    }

    public string PlaceOrder(string companyId, int quantity, bool buy)
    {
        string result = Monetary(() => Engine!.PlaceOrder(companyId, buy ? OrderSide.Buy : OrderSide.Sell, quantity, this, Persist).Message);
        _log.Msg($"[股市委托] {(buy ? "买入" : "卖出")} company={companyId} quantity={quantity} result={result}");
        return result;
    }
    public string BuyRumor(string? expectedOfferId = null) => "请与行情掮客交谈，在原版情报界面购买。";

    internal string BookBrokerByPhone()
    {
        if (!BrokerReady) return "请先进入游戏存档。";
        if (Environment.CurrentManagedThreadId != _threadId || _monetaryOperation || BlocksGameInput) return "请先关闭股市面板，再使用游戏电话。";
        if (!OwnsStockTerminal()) return "你需要持有本存档的股市面板道具，才能预约内幕掮客。";
        if (Client?.identifier == IntelBrokerAdapter.NpcId) return "行情掮客正在接待中，请完成并遣散后再预约。";
        try
        {
            _log.Msg("[股市电话预约] 请求开始 " + BrokerQueueSnapshot());
            Engine!.AdvanceDay(StoreStation.GetDayCounter());
            var result = Engine.BookBrokerByPhone(ScheduledEvents());
            Persist();
            _log.Msg("[股市电话预约] 请求结束 " + BrokerQueueSnapshot() + " result=" + result.Message);
            return result.Message;
        }
        catch (Exception ex) { _log.Warning("[股市电话预约] 预约保存尚未确认：" + ex); return "预约尚未确认，请稍后重拨重试；已有报价不会重新抽取。"; }
    }

    internal bool OwnsStockTerminal()
    {
        if (!BrokerReady || _save.TerminalItemId < 0 || _save.TerminalPlacementPending) return false;
        try
        {
            var entry = EmporiumEntry.Instance;
            var owned = entry?.GetAllOwnedItems();
            if (owned != null) foreach (var item in owned) if (IsTerminal(item) && item.parentInventory != null) return true;
            var inventory = entry?.invElement;
            if (inventory != null) foreach (var item in inventory.childItems)
                if (IsTerminal(item) && item.parentInventory?.Pointer == inventory.Pointer) return true;
        }
        catch (Exception ex) { _log.Warning("[股市电话预约] 道具持有状态查询失败：" + ex.Message); }
        return false;
    }

    private string BrokerQueueSnapshot() => $"day={StoreStation.GetDayCounter()} state={_store?.storeState} queue={_store?.storeClientManager?.clientStack?.Count ?? -1} current={(_store?.currentClientInstance != null)} panel={BlocksGameInput}";
    internal void TraceStoreAction(string action, bool blocked)
    {
        try { _log.Msg($"[股市输入隔离] action={action} blocked={blocked} " + BrokerQueueSnapshot()); }
        catch (Exception ex) { _log.Warning("[股市输入隔离] 状态记录失败：" + ex.Message); }
    }

    private void ObserveStoreState()
    {
        if (_store == null || _observedStoreState == _store.storeState) return;
        _log.Msg($"[股市营业状态] {_observedStoreState?.ToString() ?? "接入"}->{_store.storeState} " + BrokerQueueSnapshot());
        _observedStoreState = _store.storeState;
    }

    internal OperationResult PurchaseBrokerOffer(string visitId, string offerId)
    {
        if (!CanBuyBrokerIntel) return OperationResult.Fail(BrokerTradeStatus);
        if (Environment.CurrentManagedThreadId != _threadId || _monetaryOperation) return OperationResult.Fail("操作繁忙，请稍后再试。");
        if (Client?.identifier != IntelBrokerAdapter.NpcId) return OperationResult.Fail("当前接待对象已变化。");
        var visit = Engine!.GetBrokerVisit();
        if (visit == null || visit.VisitId != visitId || visit.OfferId != offerId || !visit.HasArrived || visit.Departed)
            return OperationResult.Fail("当前内幕报价已变化。");
        var engine = Engine;
        _monetaryOperation = true;
        int before = Cash;
        try
        {
            var result = engine.BuyBrokerOffer(offerId, ScheduledEvents(), this, Persist);
            _log.Msg($"[内幕购买探针] success={result.Success} visit={visitId} cash={before}->{Cash}");
            RefreshOffer();
            return result;
        }
        catch (Exception ex)
        {
            Fault("内幕资金操作异常，交易已冻结", ex);
            return OperationResult.Fail(Status);
        }
        finally { _monetaryOperation = false; }
    }
    private string Monetary(Func<string> action)
    {
        if (!CanTrade) return Status;
        if (Environment.CurrentManagedThreadId != _threadId || _monetaryOperation) return "操作繁忙，请稍后再试。";
        _monetaryOperation = true;
        try { return action(); }
        catch (Exception ex) { Fault("现金操作异常，交易已冻结", ex); return Status; }
        finally { _monetaryOperation = false; }
    }
    private bool IsCurrentStore()
    {
        if (_store == null || !PlayerStore.IsInstanceExist()) return false;
        var current = PlayerStore.Instance;
        return current != null && current.Pointer == _store.Pointer && $"{current.saveSlotId}:{current.runID}:{current.runNumber}" == _identity;
    }
    public void ChangeCash(int delta)
    {
        if (!_monetaryOperation || _store == null) throw new InvalidOperationException("禁止在订单之外修改现金。");
        int before = _store.GetCash();
        int expected = checked(before + delta);
        if (expected < 0) throw new InvalidOperationException("余额不足。");
        _store.ModCash(delta, false);
        if (_store.GetCash() != expected) throw new InvalidOperationException("原版资金接口返回金额与预期不一致。");
        _log.Msg($"[股市资金] {before} -> {expected}，delta={delta}");
    }

    private StoreClient? Client => _store?.currentClientInstance?.GetClientBlueprint();
    private void RefreshCustomer()
    {
        var client = Client;
        var pointer = _store?.currentClientInstance?.Pointer ?? IntPtr.Zero;
        if (_dismissPending != IntPtr.Zero && pointer != _dismissPending) { SettleSession(); _dismissPending = IntPtr.Zero; }
        if (_clientPointer != IntPtr.Zero && pointer != _clientPointer && !_save.SessionClosed) SettleSession();
        if (client == null) { SellerDescription = "当前没有行情掮客"; _offer = null; _clientPointer = IntPtr.Zero; return; }
        string key = $"{client.identifier}|{client.clientFaction}";
        if (_save.SessionId.Length == 0 || _save.ClientKey != key || _save.SessionClosed)
        {
            _save.SessionSequence++; _save.ClientKey = key; _save.SessionClosed = false;
            _save.SessionId = $"{_identity}:{StoreStation.GetDayCounter()}:{_save.SessionSequence}";
            _log.Msg($"[股市顾客] session={_save.SessionId} id={client.identifier} faction={client.clientFaction} tags={string.Join(",", Strings(client.tag))}");
            Persist();
        }
        SellerDescription = client.identifier == IntelBrokerAdapter.NpcId ? "闻舟 · 行情掮客正在来访" : "当前没有行情掮客；普通顾客不再出售股市内幕。";
        _clientPointer = pointer;
        RefreshOffer();
    }
    private void RefreshOffer()
    {
        if (Engine == null || Client?.identifier != IntelBrokerAdapter.NpcId || _save.SessionClosed)
        { _offer = null; return; }
        var visit = Engine.GetBrokerVisit();
        _offer = visit != null && visit.HasArrived && !visit.Departed
            ? Engine.GetRumors().FirstOrDefault(r => r.Id == visit.OfferId) : null;
        if (_offer != null) SellerDescription = "闻舟 · 行情掮客 · 消息来源：" + _offer.Identity;
    }
    private IReadOnlyList<ScheduledEvent> ScheduledEvents()
    {
        var result = new List<ScheduledEvent>();
        var manager = StoreStation.Instance?.storeEventManager;
        if (manager?.futurEvents == null || manager.futurEventsDay == null) return result;
        for (int i = 0; i < Math.Min(manager.futurEvents.Count, manager.futurEventsDay.Count); i++)
        {
            var e = manager.futurEvents[i]; int day = e != null && e.startDay > 0 ? e.startDay : StoreStation.GetDayCounter() + manager.futurEventsDay[i];
            if (e != null)
            {
                EnsureEventRule(e);
                result.Add(new ScheduledEvent { EventId = e.identifier, Day = day, OccurrenceKey = Occurrence(e.identifier, day) });
            }
        }
        return result;
    }
    private static string Occurrence(string eventId, int day) => $"{eventId}:{day}";
    private void EnsureEventRule(StoreEvent ev)
    {
        if (Engine == null || ev.negociationDatas == null) return;
        var modifiers = new List<CommodityPriceModifier>();
        foreach (var modifier in ev.negociationDatas)
            if (modifier != null)
                modifiers.Add(new CommodityPriceModifier { ItemType = modifier.itemType, Percent = modifier.value });
        EventImpactResolver.EnsureRule(Engine, ev.identifier, modifiers);
    }
    internal void EventActivated(StoreEventManager manager, StoreEvent ev, bool wasActive)
    {
        try
        {
            if (Engine == null || wasActive || !manager.IsEventActive(ev)) return;
            int day = StoreStation.GetDayCounter();
            Engine.AdvanceDay(day);
            EnsureEventRule(ev);
            // 隐藏事件可以影响价格，但公开界面仅显示市场异动。
            bool isPublic = !ev.isHidden && ev.IsKnown && !ev.isHiddenFromNewsPaper;
            bool processed = Engine.ActivateEvent(ev.identifier, Occurrence(ev.identifier, ev.startDay > 0 ? ev.startDay : day), day, isPublic);
            int impacts = Engine.Config.Events.FirstOrDefault(r => r.EventId == ev.identifier)?.Impacts.Count(p => p.Value != 0) ?? 0;
            _log.Msg($"[股市事件] id={ev.identifier} day={day} processed={processed} mappedCompanies={impacts}"); Persist();
        }
        catch (Exception ex) { Fault("事件适配失败", ex); }
    }

    public string RecoverTerminal() => EnsureTerminal(true);

    private string EnsureTerminal(bool explicitRecovery)
    {
        if (_store == null || Engine == null || !IsCurrentStore() || _loading || _inMenu) return "请先进入存档。";
        var policy = _terminalGrant;
        GameItem? created = null;
        GameGridInventory? target = null;
        var grantSave = _save;
        var grantEngine = Engine;
        int previousId = _save.TerminalItemId;
        bool previousPending = _save.TerminalPlacementPending;
        bool previousGranted = Engine.State.TerminalGranted;
        string previousPath = _save.TerminalInventoryPath;
        bool attempted = false;
        bool confirmed = false;
        bool uncertain = false;
        try
        {
            var entry = EmporiumEntry.Instance;
            target = entry?.invElement;
            var windowChild = entry?.invWindow?.childElement;
            bool ready = entry != null && target != null && windowChild != null
                && windowChild.Pointer == target.Pointer && target.widthPixels > 0 && target.heightPixels > 0;
            string probe = ready ? $"EmporiumEntry.invElement pointer={target!.Pointer} identifier='{target.identifier}' windowChild={windowChild!.Pointer} size={target.widthPixels}x{target.heightPixels} directItems={target.childItems.Count}"
                : "EmporiumEntry.invElement 或主背包窗口尚未完成绑定，延后发放。";
            if (_lastInventoryProbe != probe) { _lastInventoryProbe = probe; _log.Msg("[股市背包探针] " + probe); }
            if (!ready || entry == null || target == null) return "主背包尚未就绪，稍后重试。";
            if (policy.AttemptInProgress) return "道具发放正在处理中。";

            // 已成功放入本次背包时保留自有引用；不因其他查询器暂时返回空而重新创建。
            if (policy.GrantedThisSession)
            {
                if (_trackedTerminal != null && _trackedTerminal.parentInventory?.Pointer == target!.Pointer)
                    _terminalVisual.Apply(_trackedTerminal);
                return "本次会话已确认股市面板，未重复发放；若仍找不到，请保存并重新载入后显式补领。";
            }

            var all = entry!.GetAllItems();
            var owned = entry.GetAllOwnedItems();
            if (all == null || owned == null) return "背包完整查询尚未就绪，未创建道具。";
            var allItems = new Dictionary<IntPtr, GameItem>();
            foreach (var item in all) if (item != null) allItems[item.Pointer] = item;
            var ownedItems = new Dictionary<IntPtr, GameItem>();
            foreach (var item in owned) if (item != null) ownedItems[item.Pointer] = item;
            bool complete = true;
            foreach (var item in target!.childItems)
            {
                if (item == null) continue;
                // 完整入口必须能够覆盖直接背包；若不一致，缺失不能被解释为道具丢失。
                if (!allItems.ContainsKey(item.Pointer)) complete = false;
                ownedItems[item.Pointer] = item;
            }
            if (!complete) return "背包查询与实际容器不一致，已暂停补领以避免重复创建。";
            GameItem? existing = ownedItems.Values.FirstOrDefault(item => item.identifier == "postit" && item.uniqueId == _save.TerminalItemId);
            if (existing == null)
                existing = ownedItems.Values.Where(item => item.identifier == "postit" && item.GetDisplayName(true) == TerminalName)
                    .OrderByDescending(item => item.uniqueId).FirstOrDefault();
            var action = policy.Decide(true, existing != null, _save.TerminalItemId >= 0 || Engine.State.TerminalGranted || _save.TerminalPlacementPending, explicitRecovery);
            if (action == TerminalGrantAction.UseExisting)
            {
                _trackedTerminal = existing!;
                _save.TerminalItemId = existing!.uniqueId; _save.TerminalPlacementPending = false;
                _save.TerminalInventoryPath = "EmporiumEntry.GetAllOwnedItems / invElement";
                Engine.State.TerminalGranted = true;
                Persist();
                existing.SetName(TerminalName); _terminalVisual.Apply(existing);
                _log.Msg($"[股市道具] 采用已有面板 uniqueId={existing.uniqueId}，未删除其他便笺，完整扫描={allItems.Count}。");
                return "已采用现有股市面板。";
            }
            if (action == TerminalGrantAction.KeepGranted)
                return "存档已记录面板，自动发放已停止。确认丢失后可按 Ctrl+F8 显式补领。";
            if (action != TerminalGrantAction.Create || !policy.BeginAttempt()) return "道具尚未就绪，稍后重试。";
            attempted = true;
            created = DirectoryMaster.Item("postit", true);
            if (created == null) return "原版便笺工厂不可用，稍后重试。";
            created.SetName(TerminalName); created.SetUnitCount(1);
            if (!target.MayHaveValidInventorySlot(created) || target.TryFindOneValidInventorySlot(created) == null)
                return "主背包空间不足，稍后重试。";

            // 先记录发放事务，再执行原生插入；重入时 policy 会阻止创建第二个道具。
            _save.TerminalItemId = created.uniqueId; _save.TerminalPlacementPending = true;
            _save.TerminalInventoryPath = "EmporiumEntry.Instance.invElement";
            Persist();
            GraphUtils.TryAcceptAllMid(target, created);
            bool directMember = false;
            foreach (var item in target.childItems) if (item != null && item.Pointer == created.Pointer) directMember = true;
            confirmed = created.parentInventory?.Pointer == target.Pointer && directMember;
            uncertain = !confirmed && created.parentInventory != null;
            if (!ReferenceEquals(grantSave, _save) || !ReferenceEquals(grantEngine, Engine) || !IsCurrentStore())
            { uncertain = created.parentInventory != null; return "存档发生变化，本次发放停止，请重新检查背包。"; }
            if (!confirmed)
            {
                if (uncertain) _log.Error($"[股市道具] 插入结果不确定 uniqueId={created.uniqueId}，保留事务并停止本次会话自动创建。");
                return uncertain ? "道具位置尚未确认，已停止重复发放。" : "主背包未接收道具，稍后重试。";
            }
            _trackedTerminal = created;
            _save.TerminalPlacementPending = false; Engine.State.TerminalGranted = true;
            Persist();
            _terminalVisual.Apply(created);
            _log.Msg($"[股市道具] 主背包直接成员已确认 uniqueId={created.uniqueId} parent={target.Pointer} path={_save.TerminalInventoryPath}；本次会话不会再次自动创建。");
            return "已将股市面板放入主背包。";
        }
        catch (Exception ex)
        {
            if (created != null)
                try { if (created.parentInventory != null) uncertain = true; } catch { uncertain = true; }
            _log.Warning("[股市道具] 发放暂停：" + ex.Message);
            return "道具发放尚未确认，已保留状态以避免重复。";
        }
        finally
        {
            if (attempted)
            {
                // 只销毁本方法新建且明确未放入任何容器的临时物品。
                if (created != null && !confirmed && !uncertain)
                    try { if (created.parentInventory == null) created.Destroy(); else uncertain = true; }
                    catch (Exception ex) { uncertain = true; _log.Warning("[股市道具] 临时便笺清理失败，停止再次创建：" + ex.Message); }
                policy.FinishAttempt(confirmed, uncertain);
                if (!confirmed && !uncertain && ReferenceEquals(grantSave, _save))
                {
                    _save.TerminalItemId = previousId; _save.TerminalPlacementPending = previousPending;
                    _save.TerminalInventoryPath = previousPath;
                    if (Engine != null) Engine.State.TerminalGranted = previousGranted;
                    try { Persist(); } catch (Exception ex) { _log.Error("[股市道具] 发放事务回滚失败：" + ex.Message); }
                }
            }
        }
    }
    internal bool IsTerminal(GameItem? item) => _store != null && item != null && _save.TerminalItemId >= 0 && item.identifier == "postit" && item.uniqueId == _save.TerminalItemId && !_save.TerminalPlacementPending;
    internal bool OpenTerminal(GameItem item)
    {
        if (!IsTerminal(item)) return false;
        OpenPanelRequested?.Invoke(); return true;
    }
    internal void Dismissed(IntPtr previousPointer)
    {
        try
        {
            if (_store == null || Engine == null || previousPointer == IntPtr.Zero || _save.SessionClosed) return;
            if (_store.currentClientInstance?.Pointer == previousPointer) { _dismissPending = previousPointer; _log.Msg("[股市探针] 遣散后实例尚未移除，延后至下一次顾客变化结算。"); return; }
            SettleSession();
        }
        catch (Exception ex) { Fault("遣散结算失败", ex); }
    }
    private void SettleSession()
    {
        if (Engine == null || _save.SessionClosed || _save.SessionId.Length == 0) return;
        Engine.AdvanceDay(StoreStation.GetDayCounter());
        Engine.CloseCustomer(_save.SessionId); _save.SessionClosed = true; _offer = null; Persist();
        _log.Msg($"[股市遣散] 已结算 session={_save.SessionId}");
    }
    internal TradeCapture? BeginTrade(string method, GameItem? item, bool? buy)
    {
        try
        {
            if (_store == null || Engine == null || _monetaryOperation || !IsCurrentStore()) return null;
            RefreshCustomer();
            if (Client == null || _save.SessionClosed) return null;
            var capture = new TradeCapture { Method = method, CashBefore = Cash, Buy = buy, Session = _save.SessionId };
            if (method == "OnWholesaleClick") capture.ExpectedWholesaleCost = _store.GetWholesalePrice();
            if (item != null && !IsTerminal(item)) capture.Items.Add(ItemCapture.Of(item));
            return capture;
        }
        catch (Exception ex) { _log.Warning($"[股市探针] 成交快照失败：{ex.Message}"); return null; }
    }
    internal void CompleteTrade(TradeCapture? capture, Exception? exception)
    {
        if (capture == null || exception != null || Engine == null || _store == null) return;
        try
        {
            int delta = Cash - capture.CashBefore;
            if (capture.Items.Count == 0 || delta == 0 || (capture.Buy == true && delta >= 0) || (capture.Buy == false && delta <= 0))
            { _log.Msg($"[股市探针] {capture.Method} 未确认成交，现金变化={delta}，items={capture.Items.Count}"); return; }
            Dictionary<int, decimal>? callbackAllocations = null;
            if (capture.CallbackAmounts.Count > 0)
            {
                if (!TradeAllocationPolicy.TryAllocate(delta, capture.CallbackAmounts, capture.ExpectedWholesaleCost, out var allocated))
                { _log.Warning($"[股市探针] {capture.Method} 回调金额合计 {capture.CallbackAmounts.Values.Sum()} 与现金变化 {delta} 无法核对；本次不计行情。"); return; }
                callbackAllocations = allocated;
                if (capture.CallbackAmounts.Values.Sum() != Math.Abs((decimal)delta))
                    _log.Msg($"[股市探针] 批发整单与单件折扣取整差已核对；按实际现金变化 {delta} 分配商品信号。");
            }
            // 用原版实际现金变化确定方向和总额，仅在明确商品交易调用范围内记账。
            decimal total = capture.Items.Sum(x => Math.Max(1m, x.Estimate));
            foreach (var item in capture.Items.Where(i => capture.CallbackAmounts.Count == 0 || capture.CallbackAmounts.ContainsKey(i.Id)))
            {
                string tradeId = $"{capture.Session}:{++_save.TradeSequence}:{item.Id}:{delta}";
                decimal signed = callbackAllocations != null ? callbackAllocations[item.Id] : delta * Math.Max(1m, item.Estimate) / total;
                bool recorded = Engine.RecordTrade(capture.Session, tradeId, item.Types, signed);
                _log.Msg($"[股市成交] {capture.Method} id={item.Id} types={string.Join(",",item.Types)} cashDelta={delta} allocated={signed} recorded={recorded}");
            }
            Persist();
        }
        catch (Exception ex) { Fault("商品成交记录失败", ex); }
    }
    internal void LogTradeCallback(string method, GameItem item, int amount) => _log.Msg($"[股市回调探针] {method} id={item.uniqueId} amount={amount} cash={Cash} types={string.Join(",",Strings(item.GetGameItemType()))}");
    private void Fault(string text, Exception ex)
    {
        _fault = $"{text}：{ex.Message}";
        if (Engine != null) { Engine.State.TradingFrozen = true; Engine.State.FreezeReason = _fault; }
        _log.Error(_fault);
    }
    internal static List<string> Strings(Il2CppSystem.Collections.Generic.List<string>? values)
    {
        var result = new List<string>(); if (values != null) foreach (var v in values) if (!string.IsNullOrEmpty(v)) result.Add(v); return result;
    }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        if (Current == this) Current = null;
        _intelPhone?.Dispose(); _broker?.Dispose(); _intelCalendar?.Dispose(); _intelDelivery?.Dispose();
        _intelPhone = null; _broker = null; _intelCalendar = null; _intelDelivery = null;
        GamePatches.Uninstall(_harmony); _terminalVisual.Dispose(); IconRenderPatches.Uninstall(_harmony);
        _trackedTerminal = null; _store = null; Engine = null; _offer = null; OpenPanelRequested = null;
    }
}

internal sealed class TradeCapture
{
    public string Method = "";
    public string Session = "";
    public int CashBefore;
    public int? ExpectedWholesaleCost;
    public bool? Buy;
    public List<ItemCapture> Items = new();
    public Dictionary<int, decimal> CallbackAmounts = new();
}
internal sealed class ItemCapture
{
    public int Id;
    public decimal Estimate;
    public List<string> Types = new();
    public static ItemCapture Of(GameItem item) => new() { Id = item.uniqueId, Estimate = Math.Max(1L, item.GetNegociatedValue()), Types = GameBridge.Strings(item.GetGameItemType()) };
}
