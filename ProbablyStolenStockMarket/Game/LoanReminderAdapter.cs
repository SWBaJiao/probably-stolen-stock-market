using System;
using ProbablyStolenStockMarket.Localization;
using ProbablyStolenStockMarket.Market;

namespace ProbablyStolenStockMarket.Game;

// 提醒只在空闲时显示，按合同和日期保存去重，不改变原版日程。
internal sealed class LoanReminderAdapter : IDisposable
{
    private readonly GameBridge _host;
    private GenericConfirmationUIManager? _ui;
    private System.Action? _confirm, _cancel;
    private Action? _managedConfirm, _managedCancel;
    private bool _disposed;
    internal LoanReminderAdapter(GameBridge host) { _host = host; }
    internal bool BlocksGameInput => OwnsPrompt() && _ui!.panel.activeInHierarchy;

    internal void Tick()
    {
        if (_disposed || !_host.BrokerReady || _host.Engine == null) return;
        if (_ui != null && !BlocksGameInput) Reset();
        var loan = _host.Engine.State.Loan;
        var contract = loan.Contract;
        int day = _host.Engine.State.LastDay;
        if (loan.EndingPending || loan.EndingExecuted || _host.BlocksGameInput
            || _host.CurrentClient != null || !_host.CanUseLoans) return;
        // 结清通知独立于合同与当天催收标记，主动还款和夜间自动还款均可触发。
        if (loan.StartingDebtCompleted && !loan.StartingDebtCompletionNotified)
        {
            ShowCompletion(loan);
            return;
        }
        if (contract == null || loan.LastReminderDay == day) return;
        bool startingDebt = contract.Source == LoanSource.StartingDebt;
        bool introduction = startingDebt && loan.LastReminderDay < contract.StartDay;
        int daysToDeadline = contract.DueDay - day;
        bool remind = startingDebt
            ? introduction || daysToDeadline == 14 || daysToDeadline == 7 || daysToDeadline == 3 || daysToDeadline == 1 || daysToDeadline == 0
            : day == contract.DueDay - 1 || day == contract.DueDay + 5 || day == contract.DueDay + 7;
        if (!remind) return;
        var ui = GenericConfirmationUIManager.Instance;
        if (ui?.panel == null || ui.panel.activeSelf) return;
        string text;
        if (startingDebt)
        {
            text = introduction ? "负债店主：炒股亏损后，你只剩 1500 元现金，并背上了固定旧债 5000 元。你有完整 28 个游戏日还清欠款，零利息、零罚息，无宽限期。\n\n" : "负债店主 · 还款提醒\n\n";
            text += daysToDeadline <= 0 ? "今天是最后还款日。经营费用结算后，现金足够时自动结清；否则进入“抵债停业”结局。"
                : $"距最后还款日还有 {daysToDeadline} 天（含今天仍可继续营业）。";
            text += $"\n\n剩余欠款 {contract.AmountToClose}，最后还款日：第 {contract.DueDay} 日营业结束。\n可随时部分还款或提前结清，部分还款不延长期限。\n电话 731207 可查询或还款。";
        }
        else
        {
            text = day == contract.DueDay - 1 ? "贷款将在明天到期，请准备还款。"
                : day == contract.DueDay + 7 ? "今天是最后还款日，明天仍有欠款将触发抵债停业结局。" : "贷款已经逾期，这是最后通牒，请在最终期限前结清。";
            text += $"\n\n当前结清金额 {contract.AmountToClose}，到期日 {contract.DueDay}，最后还款日 {contract.EndingDay - 1}。\n电话 731207 可查询或还款。";
        }
        string identity = _host.BrokerSaveIdentity;
        var engine = _host.Engine;
        _managedConfirm = () =>
        {
            bool valid = _host.BrokerReady && _host.BrokerSaveIdentity == identity && ReferenceEquals(_host.Engine, engine);
            Reset();
            if (valid) _host.ShowLoans();
        };
        _managedCancel = Reset;
        _confirm = _managedConfirm;
        _cancel = _managedCancel;
        _ui = ui;
        ui.OpenPanel(Localizer.Text(text), Localizer.Text("查看贷款账单"), Localizer.Text("稍后处理"), _confirm, _cancel);
        loan.LastReminderDay = day;
        _host.PersistBrokerState();
    }

    private void ShowCompletion(LoanState loan)
    {
        var ui = GenericConfirmationUIManager.Instance;
        if (ui?.panel == null || ui.panel.activeSelf) return;
        _managedConfirm = Reset;
        _confirm = _managedConfirm;
        _managedCancel = null;
        _cancel = null;
        _ui = ui;
        ui.OpenPanelSingleButton(Localizer.Text("旧债已清，负债店主挑战完成，可以继续正常经营。"), Localizer.Text("继续经营"), _confirm);
        loan.StartingDebtCompletionNotified = true;
        _host.PersistBrokerState();
    }

    private bool OwnsPrompt() => _ui != null && NativeGameAccess.OwnsConfirmation(_ui, _confirm, _cancel);
    internal void Reset()
    {
        try
        {
            if (OwnsPrompt())
            {
                NativeGameAccess.ClearConfirmation(_ui!);
                if (_ui!.panel != null && _ui.panel.activeSelf) _ui.ClosePanel();
            }
        }
        finally
        {
            _ui = null; _confirm = null; _cancel = null; _managedConfirm = null; _managedCancel = null;
        }
    }
    public void Dispose() { Reset(); _disposed = true; }
}
