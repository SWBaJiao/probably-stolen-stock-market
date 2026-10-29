using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using HarmonyLib;
using ProbablyStolenStockMarket.Contracts;
using ProbablyStolenStockMarket.Market;

namespace ProbablyStolenStockMarket.Game;

public sealed partial class GameBridge : IMarketHost, ILoanHost, IWallet, IDisposable
{
    private const string SaveKey = "probably_stolen.stock_market.v1";
    private const string TerminalName = "股市面板";
    private readonly ModLogger _log;
    private readonly MarketConfig _config;
    private readonly TerminalVisual _terminalVisual;
    private TerminalAccountNpcAdapter? _accountNpc;
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
    private PlayerStore.StoreState? _observedStoreState;
    private bool _inMenu = true;
    private Dictionary<string, string>? _loadDictionary;
    private StoreClientInstance? _dismissPending;
    private StoreClientInstance? _clientInstance;
    private long _nextPoll;
    private long _nextTerminalScan;
    private TerminalGrantPolicy _terminalGrant = new();
    private GameItem? _trackedTerminal;
    private string _lastInventoryProbe = "";
    private StoreEventManager? _eventManager;
    private bool _eventManagerTrusted;
    private int _eventDayStartDepth;
    private string _lastEventSnapshotWarning = "";
    private readonly List<ActiveMarketEvent> _pendingEventActivations = new();
    private readonly List<EventLifecycleCapture> _pendingEventEndings = new();
    private RumorView? _offer;
    internal static GameBridge? Current { get; private set; }
    public MarketEngine? Engine { get; private set; }
    public event Action? OpenPanelRequested;
    public event Action? ClosePanelRequested;
    public Func<bool>? IsPanelVisible { get; set; }
    internal bool BlocksGameInput => IsPanelVisible?.Invoke() == true || _accountNpc?.BlocksGameInput == true || _intelPhone?.BlocksGameInput == true || _loanReminder?.BlocksGameInput == true || _loanWindowVisible || StartingDebtClosePromptVisible || LoanHasEnded;
    public int Cash => _store?.GetCash() ?? 0;
    public bool CanTrade => !_disposed && !_inMenu && !_loading && Engine != null && Engine.State.StorageVerified && !Engine.State.TradingFrozen && _fault.Length == 0 && !LoanHasEnded && IsCurrentStore();
    public string Status => _fault.Length > 0 ? _fault : Engine == null ? "请进入游戏存档。" : Engine.State.TradingFrozen ? Engine.State.FreezeReason : !Engine.State.StorageVerified ? "已启用严格存档验证：请正常保存并重新载入此存档，验证后开放现金交易。" : _config.RequireSaveReloadVerification ? "原版存档读回及资金一致性已通过；可交易。" : "现货买卖已开放；使用原版资金，投资记录随游戏存档保存。";
    public string SellerDescription { get; private set; } = "当前没有行情掮客";
    public string SellerReputation => _offer == null ? "暂无可验证情报记录" : $"{_offer.Identity} · {(_offer.IsGroupIdentity ? "该类来源" : "个人")}记录：属实 {_offer.Correct} / 失实 {_offer.Incorrect} · 参考可信度 {_offer.Reliability:P0}";
    public string OfferDescription => _offer == null ? "持有股市面板后，请用游戏电话预约行情掮客，次日开门后上门售卖。" : _offer.Purchased ? _offer.Content : $"消息来源：{_offer.Identity}。请关闭股市面板，与行情掮客交谈购买；付款前仅展示商品类别。";
    public string BrokerContactDescription
    {
        get
        {
            if (!OwnsStockTerminal()) return "持有本存档的股市面板道具后，解锁掮客电话预约。";
            var booking = Engine?.GetSharedBrokerPhoneBooking();
            string status = booking == null ? "暂无预约。" : booking.Status switch
            {
                BrokerPhoneBookingStatus.Booked => $"已约第 {booking.ArrivalDay} 日到访，开门后在队尾等待。",
                BrokerPhoneBookingStatus.Arrived => "已预约的掮客正在来访。",
                BrokerPhoneBookingStatus.Completed => "本次情报接待结束后，可再拨电话预约次日。",
                BrokerPhoneBookingStatus.Cancelled => "本次预约已取消，可拨电话查询。",
                _ => "之前的预约已过期，可再拨电话查询。"
            };
            return $"行情消息专线：{IntelPhoneAdapter.PhoneNumber}（电话簿可快速拨号）\n{status}";
        }
    }
    public int? OfferCost => _offer?.Price;
    public string? OfferId => _offer?.Id;
    public bool CanBuyRumor => false;
    internal bool BrokerReady => !_disposed && !_inMenu && !_loading && Engine != null && IsCurrentStore();
    internal bool CanBuyBrokerIntel => CanTrade;
    internal string BrokerTradeStatus => Status;
    internal string BrokerSaveIdentity => _identity;
    internal StoreClient? CurrentClient => Client;
    internal PlayerStore? CurrentStore => _store;
    internal IReadOnlyList<ScheduledEvent> GetBrokerScheduledEvents() => ScheduledEvents();
    internal void PersistBrokerState() => Persist();
    internal void GrantPurchasedTipOnce(string offerId) => _intelDelivery?.GrantPurchasedTipOnce(offerId);

    public GameBridge(ModLogger logger, MarketConfig config) { _log = logger; _config = config; _terminalVisual = new TerminalVisual(logger); }

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
            _intelCard = new IntelCardAdapter(this, _log);
            _intelDelivery.Initialize(harmony);
            _broker.Initialize(harmony);
            _intelCalendar.Initialize(harmony);
            _intelPhone.Initialize(harmony);
            _accountNpc = new TerminalAccountNpcAdapter(this, _log);
            _accountNpc.Initialize(harmony);
            InitializeLoans(harmony);
            StartingDebtScenario.Register();
        }
        catch
        {
            StartingDebtScenario.Unregister();
            DisposeLoans();
            _accountNpc?.Dispose(); _accountNpc = null;
            _intelCard?.Dispose(); _intelCard = null;
            _intelPhone?.Dispose(); _broker?.Dispose(); _intelCalendar?.Dispose(); _intelDelivery?.Dispose();
            IconRenderPatches.Uninstall(harmony);
            GamePatches.Uninstall(harmony); Current = null; throw;
        }
        _log.Msg("[股市面板] 已安装适配钩子；未运行游戏验收前不视为适配成功。");
    }

    public void Tick()
    {
        _loanPhone?.Tick();
        if (_disposed || Environment.CurrentManagedThreadId != _threadId || NativeGameAccess.Milliseconds < _nextPoll) return;
        _nextPoll = NativeGameAccess.Milliseconds + 1000;
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
            if (Engine != null && EnsureMarketDay()) Persist();
            TickLoans();
            RefreshCustomer();
            if (Engine != null)
            {
                Engine.ResolveRumors(ScheduledEvents());
                _accountNpc?.Tick();
                _broker?.Tick();
                _intelDelivery?.Tick();
                Persist();
                if (NativeGameAccess.Milliseconds >= _nextTerminalScan)
                { _nextTerminalScan = NativeGameAccess.Milliseconds + 10000; EnsureTerminal(false); }
                _intelCard?.Tick();
            }
        }
        catch (Exception ex) { Fault("游戏适配异常", ex); }
    }

    private void Attach(PlayerStore store, string identity, bool actualLoad)
    {
        ResetBrokerRuntime();
        ResetEventRuntime();
        _observedStoreState = null;
        _store = store; _identity = identity; _fault = ""; _offer = null;
        _terminalGrant = new TerminalGrantPolicy(); _trackedTerminal = null; _lastInventoryProbe = ""; _nextTerminalScan = 0;
        bool restored = false;
        bool hasSavedMarketState = false;
        if (store.modData != null && store.modData.ContainsKey(SaveKey))
        {
            var serialized = store.modData[SaveKey];
            _save = JsonConvert.DeserializeObject<AdapterSave>(serialized, MarketEngine.JsonOptions) ?? throw new InvalidOperationException("股市存档为空。");
            if (_save.Version != 1 || _save.SaveIdentity != identity) throw new InvalidOperationException("股市存档版本或身份不匹配，已关闭交易。");
            Engine = new MarketEngine(MarketConfig.Deserialize(_config.Serialize()), MarketEngine.Deserialize(_save.MarketJson, _config));
            hasSavedMarketState = true;
            bool freshDictionary = _loadDictionary == null || store.modData != _loadDictionary;
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
        Engine.ApplyStorageAccess(restored);
        _save.TerminalAccount ??= new TerminalAccountState();
        // 已获面板的旧存档保留开户资格，不再收取开户费。
        if (_save.TerminalItemId >= 0 || _save.TerminalPlacementPending || Engine.State.TerminalGranted)
            _save.TerminalAccount.Opened = true;
        if (actualLoad && _save.WrittenBySave && _save.SavedCash != store.GetCash())
            _log.Warning($"[股市面板] 读回现金不匹配：记录 {_save.SavedCash}，当前 {store.GetCash()}，严格验证={_config.RequireSaveReloadVerification}。");
        // 恢复旧行情但未观察到原版载入完成时，不采信可能尚未填充的事件空列表。
        EnsureMarketDay(authorizeManager: actualLoad || !hasSavedMarketState, initialLoad: true);
        _log.Msg("[股市背包探针] 发放入口改为 EmporiumEntry.Instance.invElement；PlayerStore.gridInv 不再用于发放。");
        _log.Msg($"[股市面板] 存档 {identity}，原版 LoadGame={actualLoad}，nonce={_save.ProbeNonce}，读回验证={restored}，cash={store.GetCash()}");
        Persist();
        _log.Msg($"[股市交易] 严格读档验证={_config.RequireSaveReloadVerification}，可交易={CanTrade}，状态={Status}");
    }

    private void ResetBrokerRuntime()
    {
        _accountNpc?.Reset();
        ResetLoans();
        _intelCard?.Reset();
        _intelPhone?.Reset(); _broker?.Reset(); _intelCalendar?.Reset(); _intelDelivery?.Reset();
    }

    internal void BeginLoad()
    {
        ResetBrokerRuntime();
        ResetEventRuntime();
        _terminalVisual.ResetTracking();
        _trackedTerminal = null; _terminalGrant = new TerminalGrantPolicy();
        _loadDictionary = _store?.modData;
        _loading = true; _offer = null; _store = null; Engine = null; _clientInstance = null; _dismissPending = null;
    }
    public void OnSceneChanged(string sceneName)
    {
        _inMenu = sceneName.IndexOf("Menu", StringComparison.OrdinalIgnoreCase) >= 0;
        if (_inMenu) { ResetBrokerRuntime(); ResetEventRuntime(); _terminalVisual.ResetTracking(); _trackedTerminal = null; _terminalGrant = new TerminalGrantPolicy(); _store = null; Engine = null; _offer = null; _clientInstance = null; _dismissPending = null; }
        _nextPoll = 0;
    }
    internal void EndLoad(bool success)
    {
        _loading = false; _loadedPending = success; _nextPoll = 0;
        if (!success) _fault = "原版载入异常，股市未启用。";
    }
    internal void BeforeSave()
    {
        PrepareStartingDebtForSave();
        try
        {
            if (_store == null || Engine == null || _loading) return;
            _terminalVisual.RestoreForSave();
            _loanNpc?.BeforeSave();
            _intelCard?.BeforeSave();
            _save.WrittenBySave = true; _save.SavedCash = Cash;
            Persist();
            _log.Msg($"[股市面板] 原版保存前写入 nonce={_save.ProbeNonce}，cash={Cash}，terminal={_save.TerminalItemId}");
        }
        catch (Exception ex) { Fault("股市保存准备失败", ex); }
    }
    internal void SaveCompleted(bool success)
    {
        _terminalVisual.ResumeAfterSave();
        _loanNpc?.AfterSave();
        _intelCard?.AfterSave();
        if (!success) { _save.WrittenBySave = false; _log.Error("[股市面板] 原版 SaveGame 抛出异常，本次保存不能用于解锁。"); }
        else _log.Msg(_config.RequireSaveReloadVerification ? "[股市面板] 原版 SaveGame 无异常返回；随后 LoadGame 读回新的字典与相同现金可完成严格验证。" : "[股市面板] 原版 SaveGame 无异常返回；现货交易已默认开放，仍记录读档验证结果。");
    }

    private void Persist()
    {
        if (_store == null || Engine == null) throw new InvalidOperationException("未附加存档。");
        if (_store.modData == null) _store.modData = new System.Collections.Generic.Dictionary<string, string>();
        _save.MarketJson = Engine.Serialize();
        string json = JsonConvert.SerializeObject(_save, MarketEngine.JsonOptions);
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
        if (IntelBrokerAdapter.GetBrokerId(Client) != null) return "行情掮客正在接待中，请完成并遣散后再预约。";
        try
        {
            _log.Msg("[股市电话预约] 请求开始 " + BrokerQueueSnapshot());
            EnsureMarketDay();
            var result = Engine!.BookRandomBrokerByPhone(ScheduledEvents());
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
                if (IsTerminal(item) && item.parentInventory == inventory) return true;
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

    internal OperationResult PurchaseBrokerOffer(string visitId, string offerId, string brokerId = BrokerIds.Wenzhou)
    {
        if (!CanBuyBrokerIntel) return OperationResult.Fail(BrokerTradeStatus);
        if (Environment.CurrentManagedThreadId != _threadId || _monetaryOperation) return OperationResult.Fail("操作繁忙，请稍后再试。");
        if (IntelBrokerAdapter.GetBrokerId(Client) != brokerId) return OperationResult.Fail("当前接待对象已变化。");
        var visit = Engine!.GetBrokerVisit(brokerId);
        if (visit == null || visit.VisitId != visitId || visit.OfferId != offerId || !visit.HasArrived || visit.Departed)
            return OperationResult.Fail("当前内幕报价已变化。");
        var engine = Engine;
        _monetaryOperation = true;
        int before = Cash;
        try
        {
            var result = engine.BuyBrokerOffer(offerId, ScheduledEvents(), this, Persist, brokerId);
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
        try
        {
            if (EnsureMarketDay()) Persist();
            return action();
        }
        catch (Exception ex) { Fault("现金操作异常，交易已冻结", ex); return Status; }
        finally { _monetaryOperation = false; }
    }
    private bool IsCurrentStore()
    {
        if (_store == null || !PlayerStore.IsInstanceExist()) return false;
        var current = PlayerStore.Instance;
        return current != null && current == _store && $"{current.saveSlotId}:{current.runID}:{current.runNumber}" == _identity;
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
        var instance = _store?.currentClientInstance;
        if (_dismissPending != null && instance != _dismissPending) { SettleSession(); _dismissPending = null; }
        if (_clientInstance != null && instance != _clientInstance && !_save.SessionClosed) SettleSession();
        if (client == null) { SellerDescription = "当前没有行情掮客"; _offer = null; _clientInstance = null; return; }
        string key = $"{client.identifier}|{client.clientFaction}";
        if (_save.SessionId.Length == 0 || _save.ClientKey != key || _save.SessionClosed)
        {
            _save.SessionSequence++; _save.ClientKey = key; _save.SessionClosed = false;
            _save.SessionId = $"{_identity}:{StoreStation.GetDayCounter()}:{_save.SessionSequence}";
            _log.Msg($"[股市顾客] session={_save.SessionId} id={client.identifier} faction={client.clientFaction} tags={string.Join(",", Strings(client.tag))}");
            Persist();
        }
        var brokerId = IntelBrokerAdapter.GetBrokerId(client);
        SellerDescription = brokerId != null ? IntelBrokerAdapter.GetBrokerName(brokerId) + " · 行情掮客正在来访" : "当前没有行情掮客；普通顾客不再出售股市内幕。";
        _clientInstance = instance;
        RefreshOffer();
    }
    private void RefreshOffer()
    {
        var brokerId = IntelBrokerAdapter.GetBrokerId(Client);
        if (Engine == null || brokerId == null || _save.SessionClosed)
        { _offer = null; return; }
        var visit = Engine.GetBrokerVisit(brokerId);
        _offer = visit != null && visit.HasArrived && !visit.Departed
            ? Engine.GetRumors().FirstOrDefault(r => r.Id == visit.OfferId) : null;
        if (_offer != null) SellerDescription = IntelBrokerAdapter.GetBrokerName(brokerId) + " · 行情掮客 · 消息来源：" + _offer.Identity;
    }
    private IReadOnlyList<ScheduledEvent> ScheduledEvents()
    {
        var result = new List<ScheduledEvent>();
        // 专用事件由正常日期推进生成，此处只读取既定排程，电话和购买不会临时造事件。
        if (Engine != null) result.AddRange(Engine.GetStockScheduledEvents());
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
        return result.GroupBy(e => (e.EventId, e.OccurrenceKey, e.Day)).Select(g => g.First()).ToList();
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
            if (!CanObserveEventManager(manager) || Engine == null || wasActive || !manager.IsEventActive(ev)) return;
            EnsureEventRule(ev);
            if (!TryDescribeActiveEvent(ev, out var observation)) return;
            if (_eventDayStartDepth > 0)
            {
                _pendingEventActivations.Add(observation);
                return;
            }
            int day = StoreStation.GetDayCounter();
            EnsureMarketDay(manager, authorizeManager: true);
            bool processed = Engine.ActivateEvent(observation.EventId, observation.OccurrenceKey, day, observation.IsPublic,
                observation.ExpectedEndDayExclusive, observation.IsPermanent);
            int impacts = Engine.Config.Events.FirstOrDefault(r => r.EventId == ev.identifier)?.Impacts.Count(p => p.Value != 0) ?? 0;
            _log.Msg($"[股市事件] id={ev.identifier} day={day} lifecycleSynced=true directRegistered={processed} mappedCompanies={impacts}"); Persist();
        }
        catch (Exception ex) { Fault("事件适配失败", ex); }
    }

    // 有效快照先校正已结束事件，再由领域推进日期及接入新事件。
    private bool EnsureMarketDay(StoreEventManager? expectedManager = null, bool authorizeManager = false, bool initialLoad = false)
    {
        if (Engine == null || _eventDayStartDepth > 0) return false;
        int day = StoreStation.GetDayCounter();
        if (TryReadActiveEventSnapshot(expectedManager, authorizeManager, out var active))
            return Engine.SyncActiveEvents(active, day, initialLoad);
        // 尚未就绪或无效的数据不能冒充空集；保留已知生命周期，允许领域按已知截止日处理。
        return Engine.AdvanceDay(day);
    }

    private bool CanObserveEventManager(StoreEventManager? manager)
    {
        if (_disposed || _inMenu || _loading || Engine == null || manager == null || !IsCurrentStore()) return false;
        var native = StoreStation.Instance?.storeEventManager;
        return native != null && native == manager;
    }

    private bool TryReadActiveEventSnapshot(StoreEventManager? expectedManager, bool authorizeManager, out List<ActiveMarketEvent> observations)
    {
        observations = new List<ActiveMarketEvent>();
        try
        {
            var manager = StoreStation.Instance?.storeEventManager;
            if (manager == null || !CanObserveEventManager(manager) || expectedManager != null && expectedManager != manager) return false;

            if (_eventManager != manager)
            {
                _eventManager = manager;
                _eventManagerTrusted = authorizeManager;
            }
            else if (authorizeManager) _eventManagerTrusted = true;
            if (!_eventManagerTrusted) { EventSnapshotWarning("原版事件管理器已更换，等待载入或原生事件流程完成后再采信快照。"); return false; }
            var events = manager.GetActiveEvents();
            if (events == null) return false;
            foreach (var ev in events)
            {
                if (ev == null)
                { observations.Clear(); EventSnapshotWarning("原版生效事件列表含缺失或无效条目，本次不以空集清除事件。"); return false; }
                if (!TryDescribeActiveEvent(ev, out var observation)) { observations.Clear(); return false; }
                observations.Add(observation);
            }
            foreach (var ev in events) EnsureEventRule(ev);
            _lastEventSnapshotWarning = "";
            return true;
        }
        catch (Exception ex)
        {
            observations.Clear();
            EventSnapshotWarning("原版生效事件快照暂不可读，保留已有生命周期：" + ex.Message);
            return false;
        }
    }

    private bool TryDescribeActiveEvent(StoreEvent ev, out ActiveMarketEvent observation)
    {
        observation = new ActiveMarketEvent();
        // 原版激活函数写入实际游戏日，0 是合法首日；负值不能改用每次观察日构造新 key。
        if (string.IsNullOrWhiteSpace(ev.identifier) || ev.startDay < 0)
        { EventSnapshotWarning("生效事件缺少有效标识或 startDay 为负，本次不新造发生键、不登记行情计划，等待原生数据恢复。"); return false; }
        int? end = ev.totalDuration > 0 && ev.startDay <= int.MaxValue - ev.totalDuration
            ? ev.startDay + ev.totalDuration : null;
        observation = new ActiveMarketEvent
        {
            EventId = ev.identifier, OccurrenceKey = Occurrence(ev.identifier, ev.startDay), StartDay = ev.startDay,
            ExpectedEndDayExclusive = end, IsPermanent = ev.isPermanent,
            IsPublic = !ev.isHidden && ev.IsKnown && !ev.isHiddenFromNewsPaper
        };
        return true;
    }

    private void EventSnapshotWarning(string message)
    {
        if (_lastEventSnapshotWarning == message) return;
        _lastEventSnapshotWarning = message; _log.Warning("[股市事件探针] " + message);
    }

    internal EventLifecycleCapture? BeforeEventDeactivated(StoreEventManager manager, StoreEvent ev)
    {
        try
        {
            if (!CanObserveEventManager(manager) || !manager.IsEventActive(ev) || !TryDescribeActiveEvent(ev, out var observation)) return null;
            if (_eventDayStartDepth == 0 && EnsureMarketDay(manager, authorizeManager: true)) Persist();
            int day = StoreStation.GetDayCounter();
            // 日开始替换旧事件时，新日尚未结算；旧事件最后生效日为前一日。
            // 同批刚激活又移除的事件保留当日，避免抹掉其实际发生记录。
            int lastActiveDay = _eventDayStartDepth > 0 && observation.StartDay < day ? day - 1 : day;
            return new EventLifecycleCapture(manager, ev, observation, lastActiveDay, Engine!, _identity);
        }
        catch (Exception ex) { Fault("事件结束观察失败", ex); return null; }
    }

    internal void AfterEventDeactivated(EventLifecycleCapture? capture, bool success)
    {
        try
        {
            if (!success || capture == null || !ReferenceEquals(capture.Engine, Engine) || capture.SaveIdentity != _identity
                || !CanObserveEventManager(capture.Manager) || capture.Manager.IsEventActive(capture.Event)) return;
            if (_eventDayStartDepth > 0) { _pendingEventEndings.Add(capture); return; }
            var ev = capture.Observation;
            // 日末原生计数尚未进入下一日，D 日移除从 D+1 才开始缓慢回归。
            Engine!.EndEvent(ev.EventId, ev.OccurrenceKey, capture.Day);
            EnsureMarketDay(capture.Manager, authorizeManager: true); Persist();
            _log.Msg($"[股市事件探针] 原生结束已确认 id={ev.EventId} lastActiveDay={capture.Day}，下一日起逐步回归。");
        }
        catch (Exception ex) { Fault("事件结束同步失败", ex); }
    }

    internal bool BeginEventDayStart(StoreEventManager manager)
    {
        try
        {
            if (!CanObserveEventManager(manager)) return false;
            _eventDayStartDepth++; return true;
        }
        catch (Exception ex) { Fault("原生日开始观察失败", ex); return false; }
    }

    internal void CompleteEventDayStart(StoreEventManager manager, bool entered, bool success)
    {
        if (!entered) return;
        if (_eventDayStartDepth > 0) _eventDayStartDepth--;
        if (_eventDayStartDepth > 0) return;
        try
        {
            if (!success || !CanObserveEventManager(manager)) { _eventManagerTrusted = false; return; }
            foreach (var ending in _pendingEventEndings)
                Engine!.EndEvent(ending.Observation.EventId, ending.Observation.OccurrenceKey, ending.Day);
            EnsureMarketDay(manager, authorizeManager: true);
            int day = StoreStation.GetDayCounter();
            foreach (var active in _pendingEventActivations)
                Engine!.ActivateEvent(active.EventId, active.OccurrenceKey, day, active.IsPublic, active.ExpectedEndDayExclusive, active.IsPermanent);
            // 同一次日开始流程中激活又结束的事件仍登记事实与真实结束日，未发生的大跳由领域取消。
            foreach (var ending in _pendingEventEndings)
                Engine!.EndEvent(ending.Observation.EventId, ending.Observation.OccurrenceKey, ending.Day);
            Persist();
        }
        catch (Exception ex) { Fault("原生日开始事件同步失败", ex); }
        finally { _pendingEventActivations.Clear(); _pendingEventEndings.Clear(); }
    }

    internal void PrepareEventDayEnd(StoreEventManager manager)
    {
        try { if (CanObserveEventManager(manager) && EnsureMarketDay(manager, authorizeManager: true)) Persist(); }
        catch (Exception ex) { Fault("原生日末事件同步失败", ex); }
    }

    internal void CompleteEventDayEnd(StoreEventManager manager, bool success)
    {
        try
        {
            if (!success || !CanObserveEventManager(manager)) return;
            if (EnsureMarketDay(manager, authorizeManager: true)) Persist();
        }
        catch (Exception ex) { Fault("原生日末事件快照失败", ex); }
    }

    private void ResetEventRuntime()
    {
        _eventManager = null; _eventManagerTrusted = false; _eventDayStartDepth = 0;
        _lastEventSnapshotWarning = ""; _pendingEventActivations.Clear(); _pendingEventEndings.Clear();
    }

    internal void Dismissed(StoreClientInstance? previousClient)
    {
        try
        {
            if (_store == null || Engine == null || previousClient == null || _save.SessionClosed) return;
            if (_store.currentClientInstance == previousClient) { _dismissPending = previousClient; _log.Msg("[股市面板] 遣散后实例尚未移除，延后至下一次顾客变化封存会话。"); return; }
            SettleSession();
        }
        catch (Exception ex) { Fault("遣散会话封存失败", ex); }
    }
    private void SettleSession()
    {
        if (Engine == null || _save.SessionClosed || _save.SessionId.Length == 0) return;
        EnsureMarketDay();
        Engine.CloseCustomer(_save.SessionId); _save.SessionClosed = true; _offer = null; Persist();
        _log.Msg($"[股市遣散] 已封存会话，成交按原成交日汇总，不即时变价 session={_save.SessionId}");
    }
    internal TradeCapture? BeginTrade(string method, GameItem? item, bool? buy)
    {
        try
        {
            if (_store == null || Engine == null || _monetaryOperation || !IsCurrentStore()) return null;
            if (EnsureMarketDay()) Persist();
            RefreshCustomer();
            if (Client == null || _save.SessionClosed) return null;
            var capture = new TradeCapture { Method = method, CashBefore = Cash, Buy = buy, Session = _save.SessionId, Day = Engine.State.LastDay };
            if (method == "OnWholesaleClick") capture.ExpectedWholesaleCost = _store.GetWholesalePrice();
            if (item != null && !IsTerminal(item)) capture.Items.Add(ItemCapture.Of(item));
            return capture;
        }
        catch (Exception ex) { _log.Warning($"[股市面板] 成交快照失败：{ex.Message}"); return null; }
    }
    internal void CompleteTrade(TradeCapture? capture, Exception? exception)
    {
        if (capture == null || exception != null || Engine == null || _store == null) return;
        try
        {
            // 原版交易应同步完成；异常跨日的快照不能误记入另一日汇总。
            if (capture.Day != Engine.State.LastDay || capture.Day != StoreStation.GetDayCounter())
            { _log.Warning($"[股市面板] {capture.Method} 成交快照跨日，无法确认归属，未记行情：captureDay={capture.Day} marketDay={Engine.State.LastDay} gameDay={StoreStation.GetDayCounter()}"); return; }
            int delta = Cash - capture.CashBefore;
            if (capture.Items.Count == 0 || delta == 0 || (capture.Buy == true && delta >= 0) || (capture.Buy == false && delta <= 0))
            { _log.Msg($"[股市面板] {capture.Method} 未确认成交，现金变化={delta}，items={capture.Items.Count}"); return; }
            Dictionary<int, decimal>? callbackAllocations = null;
            if (capture.CallbackAmounts.Count > 0)
            {
                if (!TradeAllocationPolicy.TryAllocate(delta, capture.CallbackAmounts, capture.ExpectedWholesaleCost, out var allocated))
                { _log.Warning($"[股市面板] {capture.Method} 回调金额合计 {capture.CallbackAmounts.Values.Sum()} 与现金变化 {delta} 无法核对；本次不计行情。"); return; }
                callbackAllocations = allocated;
                if (capture.CallbackAmounts.Values.Sum() != Math.Abs((decimal)delta))
                    _log.Msg($"[股市面板] 批发整单与单件折扣取整差已核对；按实际现金变化 {delta} 分配商品信号。");
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
    internal static List<string> Strings(System.Collections.Generic.List<string>? values)
    {
        var result = new List<string>(); if (values != null) foreach (var v in values) if (!string.IsNullOrEmpty(v)) result.Add(v); return result;
    }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        StartingDebtScenario.Unregister();
        DisposeLoans();
        _accountNpc?.Dispose(); _accountNpc = null;
        _intelCard?.Dispose(); _intelCard = null;
        ResetEventRuntime();
        if (Current == this) Current = null;
        _intelPhone?.Dispose(); _broker?.Dispose(); _intelCalendar?.Dispose(); _intelDelivery?.Dispose();
        _intelPhone = null; _broker = null; _intelCalendar = null; _intelDelivery = null;
        GamePatches.Uninstall(_harmony); _terminalVisual.Dispose(); IconRenderPatches.Uninstall(_harmony);
        _trackedTerminal = null; _store = null; Engine = null; _offer = null; OpenPanelRequested = null; ClosePanelRequested = null;
    }
}

internal sealed class EventLifecycleCapture
{
    internal readonly StoreEventManager Manager;
    internal readonly StoreEvent Event;
    internal readonly ActiveMarketEvent Observation;
    internal readonly int Day;
    internal readonly MarketEngine Engine;
    internal readonly string SaveIdentity;
    internal EventLifecycleCapture(StoreEventManager manager, StoreEvent ev, ActiveMarketEvent observation, int day, MarketEngine engine, string saveIdentity)
    { Manager = manager; Event = ev; Observation = observation; Day = day; Engine = engine; SaveIdentity = saveIdentity; }
}

internal sealed class TradeCapture
{
    public string Method = "";
    public string Session = "";
    public int CashBefore;
    public int Day;
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
