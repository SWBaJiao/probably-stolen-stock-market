using MelonModTemplate.Game;

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
Console.WriteLine($"终端发放回归通过，共 {count} 项。");
