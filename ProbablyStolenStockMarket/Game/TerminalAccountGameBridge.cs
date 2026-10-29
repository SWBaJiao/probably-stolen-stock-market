using System;

namespace ProbablyStolenStockMarket.Game;

public sealed partial class GameBridge
{
    internal bool TerminalAccountOpened => BrokerReady && _save.TerminalAccount.Opened;
    internal bool CanReceiveAccountVisit => BrokerReady && !LoanHasEnded && _fault.Length == 0 && !Engine!.State.TradingFrozen;
    internal bool TerminalAccountVisitDue => CanReceiveAccountVisit
        && new TerminalAccountService(_save.TerminalAccount).IsVisitDue(Engine!.State.LastDay);

    internal string OpenTerminalAccount()
    {
        if (!CanReceiveAccountVisit || !TerminalAccountNpcAdapter.IsAgent(CurrentClient)) return "证券开户专员尚未到访，请等到约定日期。";
        if (Environment.CurrentManagedThreadId != _threadId || _monetaryOperation || BlocksGameInput) return "操作繁忙，请稍后再试。";
        _monetaryOperation = true;
        try
        {
            if (EnsureMarketDay()) Persist();
            if (_store!.storeState != PlayerStore.StoreState.OPEN) return "证券开户专员尚未到访，请等到约定日期。";
            string result = new TerminalAccountService(_save.TerminalAccount).Open(Engine!.State.LastDay, () => Cash, ChangeCash, Persist);
            if (!_save.TerminalAccount.Opened) return result;
            _log.Msg("[证券开户] " + result);
            return result + "\n" + EnsureTerminal(false);
        }
        catch (Exception ex) { Fault("证券开户资金操作异常", ex); return Status; }
        finally { _monetaryOperation = false; }
    }

    internal void PostponeTerminalAccount()
    {
        if (!CanReceiveAccountVisit || _save.TerminalAccount.Opened) return;
        if (EnsureMarketDay()) Persist();
        if (!new TerminalAccountService(_save.TerminalAccount).IsVisitDue(Engine!.State.LastDay)) return;
        new TerminalAccountService(_save.TerminalAccount).Postpone(Engine.State.LastDay, Persist);
        _log.Msg($"[证券开户] 下次来访：第 {_save.TerminalAccount.NextVisitDay} 日。");
    }
}
