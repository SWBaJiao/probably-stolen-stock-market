using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using ProbablyStolenStockMarket.Localization;
using ProbablyStolenStockMarket.Market;
using UnityEngine.EventSystems;
using NativeEvents = System.Collections.Generic.List<StoreEvent>;

namespace ProbablyStolenStockMarket.Game;

// 仅扩充日历控件的显示列表，不向原版世界事件队列登记情报或贷款。
internal sealed class IntelCalendarAdapter : IDisposable
{
    private static IntelCalendarAdapter? _current;
    private readonly GameBridge _host;
    private readonly IntelDeliveryAdapter _delivery;
    private readonly ModLogger _log;
    private readonly Dictionary<string, StoreEvent> _displayEvents = new();
    private readonly Dictionary<object, string> _offerByPointer = new(NativeReferenceComparer.Instance);
    private readonly HashSet<int> _loggedDays = new();
    private const string LoanEventPrefix = "stock_loan_display:";
    private readonly Dictionary<string, StoreEvent> _loanDisplayEvents = new();
    private readonly Dictionary<object, string> _loanByPointer = new(NativeReferenceComparer.Instance);
    private HarmonyLib.Harmony? _harmony;
    private MethodInfo? _setElement;
    private MethodInfo? _click;
    private bool _disposed;

    internal IntelCalendarAdapter(GameBridge host, IntelDeliveryAdapter delivery, ModLogger logger)
    { _host = host; _delivery = delivery; _log = logger; }

    public void Initialize(HarmonyLib.Harmony harmony)
    {
        if (_current != null) throw new InvalidOperationException("情报日历适配器已经存在。");
        _current = this; _harmony = harmony;
        try
        {
            _setElement = AccessTools.Method(typeof(CalendarDayElement), "SetElement", new[] { typeof(int), typeof(NativeEvents), typeof(CalendarDayElement) })
                ?? throw new MissingMethodException("CalendarDayElement.SetElement");
            _click = AccessTools.Method(typeof(CalendarEventElement), "OnPointerClick", new[] { typeof(PointerEventData) })
                ?? throw new MissingMethodException("CalendarEventElement.OnPointerClick");
            harmony.Patch(_setElement, prefix: new HarmonyMethod(typeof(IntelCalendarAdapter), nameof(SetDayPrefix)));
            harmony.Patch(_click, prefix: new HarmonyMethod(typeof(IntelCalendarAdapter), nameof(ClickPrefix)));
            _log.Msg("[股市情报日历] 已安装显示层探针；不修改 activeEvents/futurEvents，实际日历显示等待游戏验收。");
        }
        catch { Dispose(); throw; }
    }

    private static void SetDayPrefix(int __0, ref NativeEvents __1)
    {
        var adapter = _current;
        if (adapter == null || adapter._disposed || !adapter._host.BrokerReady) return;
        try
        {
            var records = adapter._host.Engine?.GetIntelRecords()
                .Where(r => r.CalendarEligible && __0 >= r.ObservationStartDay && __0 <= r.ObservationEndDay).ToArray();
            var contract = adapter._host.Engine?.State.Loan.Contract;
            var loanEvent = contract == null ? null : adapter.GetLoanDisplayEvent(contract, __0);
            var copy = new NativeEvents();
            // 日历反复刷新时先移除旧贷款提示，结清或换合同时不会残留旧日程。
            if (__1 != null)
                foreach (var ev in __1)
                    if (ev != null && !(ev.identifier ?? "").StartsWith(LoanEventPrefix, StringComparison.Ordinal)) copy.Add(ev);
            if (records != null)
                foreach (var record in records)
                {
                    var ev = adapter.GetDisplayEvent(record);
                    bool duplicate = false;
                    foreach (var item in copy) if (item != null && item == ev) duplicate = true;
                    if (!duplicate) copy.Add(ev);
                }
            if (loanEvent != null) copy.Add(loanEvent);
            // 直到副本构建完成才替换参数，异常时继续使用原生原列表。
            __1 = copy;
            if (((records?.Length ?? 0) > 0 || loanEvent != null) && adapter._loggedDays.Add(__0))
                adapter._log.Msg($"[股市日历] 第 {__0} 日控件追加 {records?.Length ?? 0} 条情报、{(loanEvent == null ? 0 : 1)} 条贷款提示，仅显示列表副本。");
        }
        catch (Exception ex) { adapter._log.Warning("[股市情报日历] 显示列表扩充失败，保留原日历：" + ex); }
    }

    private StoreEvent GetDisplayEvent(IntelRecord record)
    {
        if (_displayEvents.TryGetValue(record.OfferId, out var found))
        {
            found.displayName = Localizer.Text(IntelDeliveryAdapter.StatusLabel(record.Status) + " · " + record.Industry);
            found.newsName = found.displayName;
            found.newsDescription = Localizer.Text(IntelDeliveryAdapter.BuildBody(record));
            return found;
        }
        int span = Math.Max(1, record.ObservationEndDay - record.ObservationStartDay + 1);
        var display = new StoreEvent
        {
            identifier = "stock_intel_display:" + record.OfferId,
            eventType = StoreEvent.EventType.NORMALE,
            eventArea = StoreEvent.EventArea.ALL,
            displayName = Localizer.Text(IntelDeliveryAdapter.StatusLabel(record.Status) + " · " + record.Industry),
            newsName = Localizer.Text(IntelDeliveryAdapter.StatusLabel(record.Status) + " · " + record.Industry),
            newsDescription = Localizer.Text(IntelDeliveryAdapter.BuildBody(record)),
            startDay = record.ObservationStartDay,
            duration = span,
            totalDuration = span,
            isDurationVisible = true,
            IsKnown = true,
            negociationDatas = new System.Collections.Generic.List<NegociationData>()
        };
        // 此实例只有控件持有，既没有真实事件编号，也没有价格修正或事件行动。
        _displayEvents.Add(record.OfferId, display);
        _offerByPointer.Add(display, record.OfferId);
        return display;
    }

    private StoreEvent? GetLoanDisplayEvent(LoanContract contract, int day)
    {
        bool startingDebt = contract.Source == LoanSource.StartingDebt;
        string label;
        if (startingDebt)
        {
            if (day != contract.DueDay) return null;
            label = "旧债最后还款日";
        }
        else if (day == contract.StartDay) label = "贷款到账";
        else if (day == contract.DueDay) label = "贷款到期";
        else if (day == contract.EndingDay - 1) label = "贷款最后还款日";
        else return null;
        string key = contract.Id + ":" + day;
        if (!_loanDisplayEvents.TryGetValue(key, out var display))
        {
            display = new StoreEvent
            {
                identifier = LoanEventPrefix + key,
                eventType = StoreEvent.EventType.NORMALE,
                eventArea = StoreEvent.EventArea.ALL,
                startDay = day, duration = 1, totalDuration = 1,
                isDurationVisible = false, IsKnown = true,
                negociationDatas = new System.Collections.Generic.List<NegociationData>()
            };
            _loanDisplayEvents.Add(key, display);
            _loanByPointer.Add(display, contract.Id);
        }
        display.displayName = Localizer.Text(label);
        display.newsName = display.displayName;
        display.newsDescription = Localizer.Text($"原始本金 {contract.OriginalPrincipal}\n剩余欠款 {contract.AmountToClose}\n最后还款日：第 {contract.EndingDay - 1} 日\n电话 731207 可查询或还款。");
        return display;
    }

    private static bool ClickPrefix(CalendarEventElement __instance)
    {
        var adapter = _current;
        if (adapter == null || adapter._disposed || __instance.storeEvent == null) return true;
        if (adapter._loanByPointer.TryGetValue(__instance.storeEvent, out var contractId))
        {
            // 旧日历格仍显示时也核对当前合同，避免已结清提示打开下一笔贷款。
            if (adapter._host.Engine?.State.Loan.Contract?.Id == contractId) adapter._host.OpenLoanContract();
            return false;
        }
        if (!adapter._offerByPointer.TryGetValue(__instance.storeEvent, out var id)) return true;
        // 专属提示一律截流，原生 GetEventPriceEffect 不参与情报展示。
        adapter._delivery.OpenPurchasedTip(id);
        return false;
    }
    public void Reset()
    {
        _displayEvents.Clear(); _offerByPointer.Clear(); _loggedDays.Clear();
        _loanDisplayEvents.Clear(); _loanByPointer.Clear();
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_harmony != null)
        {
            if (_setElement != null) _harmony.Unpatch(_setElement, HarmonyPatchType.Prefix, _harmony.Id);
            if (_click != null) _harmony.Unpatch(_click, HarmonyPatchType.Prefix, _harmony.Id);
        }
        Reset(); if (ReferenceEquals(_current, this)) _current = null;
    }
}
