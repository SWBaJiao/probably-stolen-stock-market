using ProbablyStolenStockMarket.Game;

var count = 0;
void Check(bool condition, string description)
{
    if (!condition) throw new Exception("失败：" + description);
    count++; Console.WriteLine("通过：" + description);
}

var policy = new TerminalGrantPolicy();
Check(policy.Decide(false, false, false, false) == TerminalGrantAction.WaitForInventory, "主背包窗口未绑定时不能发放");
Check(policy.Decide(true, false, false, false) == TerminalGrantAction.Create, "首次进入已就绪背包允许发放");
Check(policy.BeginAttempt() && !policy.BeginAttempt(), "原生插入期间重入不能再次创建");
Check(policy.Decide(true, false, false, true) == TerminalGrantAction.WaitForInventory, "发放过程中显式补领也被拦截");
policy.FinishAttempt(false);
Check(policy.Decide(true, false, false, false) == TerminalGrantAction.Create, "背包满且临时物品已清理后允许安全重试");
Check(policy.BeginAttempt(), "清理完成后的第二次尝试可以开始");
policy.FinishAttempt(true);
for (int i = 0; i < 20; i++)
    Check(policy.Decide(true, false, false, false) == TerminalGrantAction.KeepGranted, "成功发放后库存查询持续为空仍不重复创建 " + i);
Check(policy.Decide(true, false, true, true) == TerminalGrantAction.KeepGranted, "本次会话成功后显式补领也不能再造第二个");

var existing = new TerminalGrantPolicy();
Check(existing.Decide(true, true, true, false) == TerminalGrantAction.UseExisting, "已知编号可见时采用旧道具");
Check(existing.Decide(true, false, true, true) == TerminalGrantAction.KeepGranted, "采用旧道具后查询短暂丢失不会触发补领");

var loaded = new TerminalGrantPolicy();
Check(loaded.Decide(true, false, true, false) == TerminalGrantAction.KeepGranted, "读档有发放记录但查询未找到时不自动重发");
Check(loaded.Decide(true, false, true, true) == TerminalGrantAction.Create, "新的会话完成可信扫描后允许显式恢复旧孤儿编号");

var uncertain = new TerminalGrantPolicy();
Check(uncertain.BeginAttempt(), "不确定结果测试开始尝试");
uncertain.FinishAttempt(false, true);
Check(uncertain.Decide(true, false, false, false) == TerminalGrantAction.KeepGranted && !uncertain.BeginAttempt(), "父容器异常或清理失败时锁住创建，避免不断制造孤儿物品");
var accountState = new TerminalAccountState();
var account = new TerminalAccountService(accountState);
int money = 100, changes = 0, saves = 0;
int Cash() => money;
void Change(int delta) { changes++; money += delta; }
void Save() => saves++;
Check(!account.IsVisitDue(1) && !account.IsVisitDue(2) && account.IsVisitDue(3), "开户专员第 3 天首次到期");
Check(account.Open(2, Cash, Change, Save) == "证券开户专员尚未到访，请等到约定日期。"
    && money == 100 && changes == 0 && saves == 0 && !accountState.Opened, "提前开户不扣款也不保存");
account.Postpone(3, Save);
Check(accountState.NextVisitDay == 6 && saves == 1 && !account.IsVisitDue(3) && !account.IsVisitDue(5)
    && account.IsVisitDue(6), "第 3 天延期后第 6 天再次来访");
Check(account.Open(5, Cash, Change, Save) == "证券开户专员尚未到访，请等到约定日期。"
    && money == 100 && changes == 0 && saves == 1, "延期未到期不能付款");
account.Postpone(5, Save);
Check(accountState.NextVisitDay == 6 && saves == 1, "同日重复遣散不会继续延后日期");
money = 49;
Check(account.Open(6, Cash, Change, Save) == "开户需要 50 元，当前余额不足。可以选择三天后再来。"
    && !accountState.Opened && money == 49 && changes == 0 && saves == 1 && account.IsVisitDue(6), "余额不足不会扣费或自动改变约定");
account.Postpone(6, Save);
Check(accountState.NextVisitDay == 9 && account.IsVisitDue(9), "余额不足仍可约定三天后再来");
money = 50;
Check(account.Open(9, Cash, Change, Save) == "开户成功，已支付 50 元。"
    && accountState.Opened && money == 0 && changes == 1 && saves == 3, "余额恰好 50 元开户成功且保存一次");
Check(account.Open(9, Cash, Change, Save) == "已开通证券账户，无需重复付费。"
    && money == 0 && changes == 1 && saves == 3 && !account.IsVisitDue(99), "重复点击和之后日期均不重复收费或来访");
account.Postpone(9, Save);
Check(accountState.NextVisitDay == 9 && saves == 3, "已开户不能再安排来访");

var restoredState = new TerminalAccountState { NextVisitDay = 12 };
var restoredAccount = new TerminalAccountService(restoredState);
Check(!restoredAccount.IsVisitDue(11) && restoredAccount.IsVisitDue(12) && restoredAccount.IsVisitDue(15), "恢复延期日期后仍按约定或错过后的日期来访");
var legacyDate = new TerminalAccountService(new TerminalAccountState { NextVisitDay = 0 });
Check(!legacyDate.IsVisitDue(2) && legacyDate.IsVisitDue(3), "异常过早约定不能绕过第 3 天门槛");

var rollbackState = new TerminalAccountState();
var rollbackAccount = new TerminalAccountService(rollbackState);
var expectedFailure = new InvalidOperationException("模拟保存失败");
Exception? observed = null;
try { rollbackAccount.Postpone(3, () => throw expectedFailure); } catch (Exception ex) { observed = ex; }
Check(ReferenceEquals(observed, expectedFailure) && rollbackState.NextVisitDay == 3 && rollbackAccount.IsVisitDue(3), "延期保存失败恢复原来日期并上抛");
money = 100; changes = 0; observed = null;
try { rollbackAccount.Open(3, Cash, Change, () => throw expectedFailure); } catch (Exception ex) { observed = ex; }
Check(ReferenceEquals(observed, expectedFailure) && !rollbackState.Opened && money == 100 && changes == 2, "开户保存失败恢复开户状态与现金并上抛");
Check(rollbackAccount.Open(3, Cash, Change, () => { }) == "开户成功，已支付 50 元。"
    && rollbackState.Opened && money == 50, "回滚成功后重试只收取一次费用");

var cashFailureState = new TerminalAccountState();
var cashFailureAccount = new TerminalAccountService(cashFailureState);
money = 100; observed = null; saves = 0;
try { cashFailureAccount.Open(3, Cash, _ => throw expectedFailure, Save); } catch (Exception ex) { observed = ex; }
Check(ReferenceEquals(observed, expectedFailure) && !cashFailureState.Opened && money == 100 && saves == 0, "扣款未执行便失败时不额外退款");
observed = null;
try
{
    cashFailureAccount.Open(3, Cash, delta => { money += delta; if (delta < 0) throw expectedFailure; }, Save);
}
catch (Exception ex) { observed = ex; }
Check(ReferenceEquals(observed, expectedFailure) && !cashFailureState.Opened && money == 100 && saves == 0, "原生扣款执行后抛错仍能恢复现金");
observed = null;
try
{
    cashFailureAccount.Open(3, Cash, delta => { if (delta > 0) throw new InvalidOperationException("模拟退款失败"); money += delta; },
        () => throw expectedFailure);
}
catch (Exception ex) { observed = ex; }
Check(observed is AggregateException aggregate && aggregate.InnerExceptions.Contains(expectedFailure)
    && !cashFailureState.Opened && money == 50, "退款失败明确上抛综合错误供主机冻结处理");

var reentryState = new TerminalAccountState();
var reentryAccount = new TerminalAccountService(reentryState);
money = 100; string reentry = "";
Check(reentryAccount.Open(3, Cash, delta =>
    { money += delta; reentry = reentryAccount.Open(3, Cash, Change, Save); }, () => { }) == "开户成功，已支付 50 元。"
    && reentry == "已开通证券账户，无需重复付费。" && money == 50, "原生现金回调重入不会重复收费");
var refundState = new TerminalAccountState();
var refundAccount = new TerminalAccountService(refundState);
money = 100; reentry = ""; observed = null;
try
{
    refundAccount.Open(3, Cash, delta =>
        { money += delta; if (delta > 0) reentry = refundAccount.Open(3, Cash, Change, Save); },
        () => throw expectedFailure);
}
catch (Exception ex) { observed = ex; }
Check(ReferenceEquals(observed, expectedFailure) && !refundState.Opened && money == 100
    && reentry == "已开通证券账户，无需重复付费。", "退款回调重入不会重新开户或改变最终回滚状态");
var recoveredPolicy = new TerminalGrantPolicy();
Check(recoveredPolicy.BeginAttempt() && !recoveredPolicy.ConfirmLossForRecovery(), "发放进行中不能解除会话锁");
recoveredPolicy.FinishAttempt(true);
Check(recoveredPolicy.ConfirmLossForRecovery() && recoveredPolicy.Decide(true, false, true, true) == TerminalGrantAction.Create
    && recoveredPolicy.BeginAttempt(), "明确丢失后允许同会话显式恢复");
Console.WriteLine($"终端发放和开户回归通过，共 {count} 项。");
