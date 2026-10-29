using System;
using System.Collections.Generic;
using System.Globalization;
using ProbablyStolenStockMarket.Localization;
using ProbablyStolenStockMarket.Market;

namespace ProbablyStolenStockMarket.Game;

// 使用正式版开局目录；教程、职业互斥和预览名称由原版统一处理。
internal static class StartingDebtScenario
{
    internal const string Id = ModInfo.Id + ".indebted_shopkeeper";
    internal const string PendingKey = "probably_stolen.starting_debt.pending.v1";
    internal const int InitialCash = 1500;
    internal const string Rules = "到期日结先支付普通服务费用，再用现金自动结清剩余旧债；不足则抵债停业。";

    internal static void Register()
    {
        ModHelper.AddStartingScenario(ModInfo.Id, Id,
            () => Text("负债店主"),
            () => Text("你曾想靠炒股赚一笔，却在行情暴跌中亏掉积蓄，还背上了 5000 元债务。如今手头只剩 1500 元，你决定重新经营店铺，在 28 个营业日内还清欠款。跳过部分基础教程，直接开始经营。") + "\n\n" + Text(Rules),
            new Func<string>[]
            {
                () => Text("初始现金 1500 元"),
                () => Text("固定旧债 5000 元，无利息或罚息"),
                () => Text("28 个营业日内还清，无逾期宽限"),
                () => Text("跳过部分基础教程")
            }, MarkNewGame, tag: () => Text("挑战开局"), tagTone: StartingScenario.TagTone.Danger, skipIntro: true);
        if (StartingScenario.Get(Id)?.source != ModInfo.Id)
            throw new InvalidOperationException("负债店主开局未能注册到原版开局列表。");
    }

    private static string Text(string source)
    {
        GameLanguageAdapter.Update();
        return Localizer.Text(source);
    }

    private static void MarkNewGame()
    {
        var store = PlayerStore.Instance ?? throw new InvalidOperationException("新存档尚未创建店铺。");
        if (store.startType != NewGameData.StartType.MODDED || store.startScenarioId != Id)
            throw new InvalidOperationException("当前选择的开局不是负债店主。");
        store.modData ??= new Dictionary<string, string>();
        // 此时原版尚未分配 runID，且后续天赋仍会加钱，只记录首日，首次保存前再初始化。
        if (!store.modData.ContainsKey(PendingKey))
            store.modData.Add(PendingKey, StoreStation.GetDayCounter().ToString(CultureInfo.InvariantCulture));
    }

    internal static void Unregister() => ModHelper.RemoveStartingScenarios(ModInfo.Id);
}

public sealed partial class GameBridge
{
    private GenericConfirmationUIManager? _startingDebtCloseUi;
    private Action? _startingDebtCloseConfirm, _startingDebtCloseCancel;
    private int _startingDebtCloseApprovedDay = -1;
    private bool StartingDebtClosePromptVisible => OwnsStartingDebtClosePrompt() && _startingDebtCloseUi!.panel.activeInHierarchy;

    private void PrepareStartingDebtForSave()
    {
        var store = PlayerStore.IsInstanceExist() ? PlayerStore.Instance : null;
        if (store?.modData == null || !store.modData.TryGetValue(StartingDebtScenario.PendingKey, out var pending)) return;
        if (_loading || store.saveSlotId < 0 || string.IsNullOrWhiteSpace(store.runID)
            || store.startType != NewGameData.StartType.MODDED || store.startScenarioId != StartingDebtScenario.Id
            || !int.TryParse(pending, NumberStyles.None, CultureInfo.InvariantCulture, out int firstDay) || firstDay < 0)
            throw new InvalidOperationException("负债店主的首次保存尚未具备有效的开局标记或存档身份。");

        int beforeCash = store.GetCash();
        bool hadMarket = store.modData.TryGetValue(SaveKey, out var previousMarket);
        try
        {
            string identity = $"{store.saveSlotId}:{store.runID}:{store.runNumber}";
            if (_store != store || _identity != identity || Engine == null) Attach(store, identity, false);
            var loan = Engine!.State.Loan;
            if (loan.StartingDebtStartDay >= 0)
            {
                if (loan.StartingDebtStartDay != firstDay) throw new InvalidOperationException("开局债务的首日标记不一致。");
                // 若保存重试留下待处理标记，已有合同绝不能再次重置现金。
                store.modData.Remove(StartingDebtScenario.PendingKey);
                Persist();
                return;
            }
            if (StoreStation.GetDayCounter() != firstDay)
                throw new InvalidOperationException("开局债务初始化已错过首个营业日，不能重置现金或延长期限。");
            if (_monetaryOperation) throw new InvalidOperationException("开局初始化期间存在其他现金操作。");
            _monetaryOperation = true;
            ChangeCash(checked(StartingDebtScenario.InitialCash - beforeCash));
            var result = Engine.InitializeStartingDebt(firstDay, this, Persist, LoanBorrowerIdentity.ReadName());
            if (!result.Success) throw new InvalidOperationException(result.Message);
            store.modData.Remove(StartingDebtScenario.PendingKey);
            Persist();
            _log.Msg($"[负债店主] 存档 {identity} 已初始化，现金 {Cash}，固定旧债 5000，最后营业日 {loan.Contract!.DueDay}。");
        }
        catch (Exception ex)
        {
            store.playerCash = beforeCash;
            store.modData[StartingDebtScenario.PendingKey] = pending;
            if (hadMarket) store.modData[SaveKey] = previousMarket!;
            else store.modData.Remove(SaveKey);
            _store = null; Engine = null; _identity = "";
            Fault("负债店主初始化失败，已恢复开局资金与存档数据", ex);
            // 阻止原版保存一个只有现金或只有债务的不完整开局。
            throw;
        }
        finally { _monetaryOperation = false; }
    }

    internal bool ConfirmStartingDebtDayEnd(Action endDay)
    {
        var contract = Engine?.State.Loan.Contract;
        if (!BrokerReady || contract?.Source != LoanSource.StartingDebt || Engine!.State.LastDay != contract.DueDay
            || _store!.storeState != PlayerStore.StoreState.OPEN || _startingDebtCloseApprovedDay == contract.DueDay) return true;
        if (StartingDebtClosePromptVisible) return false;
        var ui = GenericConfirmationUIManager.Instance;
        if (ui?.panel == null || ui.panel.activeSelf) return false;
        string identity = _identity;
        var engine = Engine;
        int day = engine.State.LastDay;
        string contractId = contract.Id;
        _startingDebtCloseUi = ui;
        _startingDebtCloseConfirm = () =>
        {
            bool valid = BrokerReady && _identity == identity && ReferenceEquals(Engine, engine)
                && engine.State.LastDay == day && engine.State.Loan.Contract?.Id == contractId;
            ResetStartingDebtClosePrompt();
            if (!valid) return;
            _startingDebtCloseApprovedDay = day;
            endDay();
        };
        _startingDebtCloseCancel = ResetStartingDebtClosePrompt;
        string text = "今天是旧债的最后还款日。结束营业后仍可在夜间还款；日结先扣普通服务费用，再自动偿还剩余旧债，现金不足将抵债停业。"
            + $"\n\n剩余旧债 {contract.AmountToClose} · 可用现金 {Cash}";
        ui.OpenPanel(Localizer.Text(text), Localizer.Text("继续结束营业"), Localizer.Text("返回经营"), _startingDebtCloseConfirm, _startingDebtCloseCancel);
        return false;
    }

    private bool OwnsStartingDebtClosePrompt() => _startingDebtCloseUi != null
        && NativeGameAccess.OwnsConfirmation(_startingDebtCloseUi, _startingDebtCloseConfirm, _startingDebtCloseCancel);

    private void ResetStartingDebtClosePrompt()
    {
        if (OwnsStartingDebtClosePrompt())
        {
            NativeGameAccess.ClearConfirmation(_startingDebtCloseUi!);
            if (_startingDebtCloseUi!.panel != null && _startingDebtCloseUi.panel.activeSelf) _startingDebtCloseUi.ClosePanel();
        }
        _startingDebtCloseUi = null; _startingDebtCloseConfirm = null; _startingDebtCloseCancel = null;
    }
}
