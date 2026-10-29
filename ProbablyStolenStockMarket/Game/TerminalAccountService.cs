using System;

namespace ProbablyStolenStockMarket.Game;

internal sealed class TerminalAccountState
{
    public bool Opened { get; set; }
    public int NextVisitDay { get; set; } = TerminalAccountService.FirstVisitDay;
}

// 开户只管理来访日期和现金事务，道具交付由主机在开户后重试。
internal sealed class TerminalAccountService
{
    internal const int OpeningFee = 50;
    internal const int ReturnDelayDays = 3;
    internal const int FirstVisitDay = 3;
    private readonly TerminalAccountState _state;

    internal TerminalAccountService(TerminalAccountState state)
    { _state = state ?? throw new ArgumentNullException(nameof(state)); }

    internal bool IsVisitDue(int day) => !_state.Opened && day >= FirstVisitDay && day >= _state.NextVisitDay;

    internal void Postpone(int day, Action persist)
    {
        if (!IsVisitDue(day)) return;
        int previousDay = _state.NextVisitDay;
        _state.NextVisitDay = checked(day + ReturnDelayDays);
        try { persist(); }
        catch { _state.NextVisitDay = previousDay; throw; }
    }

    internal string Open(int day, Func<int> cash, Action<int> changeCash, Action persist)
    {
        if (_state.Opened) return "已开通证券账户，无需重复付费。";
        if (!IsVisitDue(day)) return "证券开户专员尚未到访，请等到约定日期。";
        int previousCash = cash();
        if (previousCash < OpeningFee) return "开户需要 50 元，当前余额不足。可以选择三天后再来。";
        // 扣款前标记开户，防止原生现金回调重入时再次扣款。
        _state.Opened = true;
        try
        {
            changeCash(-OpeningFee);
            if (cash() != previousCash - OpeningFee) throw new InvalidOperationException("证券开户扣款金额与预期不一致。");
            persist();
            return "开户成功，已支付 50 元。";
        }
        catch (Exception failure)
        {
            try
            {
                int difference = checked(previousCash - cash());
                if (difference != 0) changeCash(difference);
                if (cash() != previousCash) throw new InvalidOperationException("证券开户现金回滚金额与预期不一致。");
            }
            catch (Exception rollbackFailure)
            {
                throw new AggregateException("证券开户失败且现金回滚失败，已停止本次办理。", failure, rollbackFailure);
            }
            finally { _state.Opened = false; }
            throw;
        }
    }
}
