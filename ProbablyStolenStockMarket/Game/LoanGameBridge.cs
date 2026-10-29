using System;
using System.Collections.Generic;
using ProbablyStolenStockMarket.Localization;
using ProbablyStolenStockMarket.Market;

namespace ProbablyStolenStockMarket.Game;

public sealed partial class GameBridge
{
    private LoanPhoneAdapter? _loanPhone;
    private LoanNpcAdapter? _loanNpc;
    private LoanGameHooks? _loanHooks;
    private LoanReminderAdapter? _loanReminder;
    private bool _loanWindowVisible;
    private bool _loanEndingCall;
    private bool _loanEndingShown;
    private string _loanDayFeedback = "";
    private bool LoanHasEnded => Engine?.State.Loan is { EndingPending: true } or { EndingExecuted: true };
    public string LoanSaveIdentity => _identity;
    public int LoanCash => Cash;
    public bool LoanContactUnlocked => BrokerReady && Engine!.State.Loan.ContactUnlocked;
    // 开局旧债必须独立于股市交易开关结算；仍由同一资金事务核对现金并处理回滚。
    private bool CanProcessLoans => CanTrade || BrokerReady && !LoanHasEnded && Engine!.State.Loan.Contract?.Source == LoanSource.StartingDebt;
    public bool CanUseLoans => CanProcessLoans && IsPanelVisible?.Invoke() != true && _intelPhone?.BlocksGameInput != true && _loanReminder?.BlocksGameInput != true && !StartingDebtClosePromptVisible;
    public string LoanStatus => LoanHasEnded ? "贷款已超过最后期限，本局进入债务清算。" : IsPanelVisible?.Invoke() == true
        ? "请先关闭股市窗口，再办理贷款。" : _intelPhone?.BlocksGameInput == true ? "请先处理当前电话预约确认。" : !CanProcessLoans ? Status : _loanDayFeedback;

    private void InitializeLoans(HarmonyLib.Harmony harmony)
    {
        _loanPhone = new LoanPhoneAdapter(this, _log);
        _loanNpc = new LoanNpcAdapter(this, _log);
        _loanHooks = new LoanGameHooks(this, _log);
        _loanReminder = new LoanReminderAdapter(this);
        _loanPhone.Initialize(harmony); _loanNpc.Initialize(harmony); _loanHooks.Initialize(harmony);
    }

    public void LoanWindowChanged(bool visible) => _loanWindowVisible = visible;

    internal void ShowLoans()
    {
        if (!LoanContactUnlocked || LoanHasEnded || !CanUseLoans) return;
        _loanPhone?.ShowLoanPanel();
    }

    internal void OpenLoanContract()
    {
        if (!LoanContactUnlocked || LoanHasEnded || !CanUseLoans) return;
        _loanPhone?.ShowLoanPanel(true);
    }

    internal bool IsLoanCard(GameItem item) => _loanNpc?.IsOwnCard(item) == true;
    internal void PrepareLoanCardRead(GameItem item) => _loanNpc?.PrepareCardRead(item);

    public string TakeLoan(LoanKind kind, int amount, int days, Dictionary<string, int> collateral)
    {
        if (!CanUseLoans) return LoanStatus;
        // 保留点击时的报价，让日历同步后的领域校验能够拒绝过期合同。
        var quote = Engine!.QuoteLoan(kind, amount, days, collateral);
        return Monetary(() => Engine!.TakeLoan(quote, this, Persist, LoanBorrowerIdentity.ReadName()).Message);
    }

    public string RepayLoan(int amount)
    {
        if (!CanUseLoans) return LoanStatus;
        return LoanMonetary(() => Engine!.RepayLoan(amount, this, Persist).Message);
    }

    private string LoanMonetary(Func<string> action)
    {
        if (!CanProcessLoans) return LoanStatus;
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

    private void TickLoans()
    {
        if (!BrokerReady) return;
        var engine = Engine!;
        if (!LoanHasEnded && CanProcessLoans && engine.State.Loan.LastSettledDay < engine.State.LastDay)
        {
            string result = LoanMonetary(() => engine.ProcessLoanDay(engine.State.LastDay, this, Persist).Message);
            if (_loanDayFeedback != result) { _loanDayFeedback = result; _log.Msg("[贷款日结] " + result); }
        }
        if (LoanHasEnded) { ShowLoanEnding(); return; }
        _loanNpc?.Tick();
        _loanReminder?.Tick();
    }

    internal void ObserveLoanServiceDemand(PlayerStore store, bool failedOnly)
    {
        if (!BrokerReady || store != _store! || !_config.Loans.Enabled || _monetaryOperation || LoanHasEnded) return;
        if (EnsureMarketDay()) Persist();
        int due = SelectedServiceCost(store);
        var availableCosts = new List<int>();
        if (store.storeService != null)
            foreach (var service in store.storeService)
                if (service != null && service.unlocked && !service.IsOnCooldown()) availableCosts.Add(service.cost);
        bool failed = LoanServicePolicy.AllPaidServicesUnaffordable(availableCosts, store.GetCash());
        if (failedOnly && !failed) return;
        if (Engine!.RecordServiceSettlement(StoreStation.GetDayCounter(), due, failed))
        {
            Persist();
            if (failed) _log.Msg($"[贷款触发] 第 {StoreStation.GetDayCounter()} 日所有可开通付费服务单价均超过现金 {store.GetCash()}；次日安排专员。");
        }
    }

    internal LoanNightCapture? CaptureLoanNight(PlayerStore store)
    {
        if (!BrokerReady || store != _store! || LoanHasEnded) return null;
        if (EnsureMarketDay()) Persist();
        return new LoanNightCapture(_identity, Engine!, StoreStation.GetDayCounter(), SelectedServiceCost(store));
    }

    internal void CompleteLoanNight(LoanNightCapture? capture, bool success)
    {
        if (!success || capture == null || !BrokerReady || capture.Identity != _identity || !ReferenceEquals(capture.Engine, Engine)) return;
        // 原版夜间服务处理可能直接修改现金，贷款自动还款留到次日，不插入原版扣费中途。
        if (Engine!.RecordServiceSettlement(capture.Day, capture.TotalDue, false)) Persist();
        if (Engine.State.Loan.Contract?.Source == LoanSource.StartingDebt)
        {
            _loanDayFeedback = LoanMonetary(() => Engine!.CompleteStartingDebtDay(capture.Day, this, Persist).Message);
            _log.Msg("[开局债务日结] " + _loanDayFeedback);
        }
    }

    private static int SelectedServiceCost(PlayerStore store)
    {
        if (store.storeService == null) throw new InvalidOperationException("每日服务列表尚未就绪。");
        long total = 0;
        foreach (var service in store.storeService)
            if (service != null && service.unlocked && service.isUsing && service.cost > 0) total += service.cost;
        if (total > int.MaxValue) throw new InvalidOperationException("每日服务总费用超出资金范围。");
        return (int)total;
    }

    private void ShowLoanEnding()
    {
        if (_loanEndingShown || !BrokerReady || GameOverUIManager.Instance?.uiPanel == null) return;
        if (Engine!.State.Loan.Contract?.Source != LoanSource.StartingDebt && (Engine.State.TradingFrozen || _fault.Length > 0)) return;
        _loanPhone?.Reset();
        _loanReminder?.Reset();
        ClosePanelRequested?.Invoke();
        _loanEndingCall = true;
        try
        {
            _store!.ExecuteGameOver("probably_stolen_loan_default");
            if (!GameOverUIManager.Instance.uiPanel.activeInHierarchy) throw new InvalidOperationException("债务清算界面未能显示。");
            Engine.State.Loan.EndingExecuted = true;
            Persist();
            _loanEndingShown = true;
            _log.Msg("[贷款结局] 逾期超过最终期限，已调用原版结束本局流程。");
        }
        finally { _loanEndingCall = false; }
    }

    internal void OverrideLoanEnding(ref string reason, ref string description, ref bool allowContinue)
    {
        if (!_loanEndingCall) return;
        reason = Localizer.Text("抵债停业");
        description = Localizer.Text("你错过了贷款的最后还款期限。债权人带走了店铺里最后一批有价值的东西。卷帘门落下，你的生意也到此为止。");
        allowContinue = false;
    }

    private void ResetLoans()
    {
        ResetStartingDebtClosePrompt(); _startingDebtCloseApprovedDay = -1;
        _loanPhone?.Reset(); _loanNpc?.Reset(); _loanReminder?.Reset();
        _loanWindowVisible = false; _loanEndingCall = false; _loanEndingShown = false; _loanDayFeedback = "";
    }

    private void DisposeLoans()
    {
        ResetStartingDebtClosePrompt();
        _loanHooks?.Dispose(); _loanNpc?.Dispose(); _loanPhone?.Dispose(); _loanReminder?.Dispose();
        _loanHooks = null; _loanNpc = null; _loanPhone = null; _loanReminder = null; _loanWindowVisible = false;
    }
}

internal sealed class LoanNightCapture
{
    internal readonly string Identity;
    internal readonly MarketEngine Engine;
    internal readonly int Day;
    internal readonly int TotalDue;
    internal LoanNightCapture(string identity, MarketEngine engine, int day, int totalDue)
    { Identity = identity; Engine = engine; Day = day; TotalDue = totalDue; }
}
