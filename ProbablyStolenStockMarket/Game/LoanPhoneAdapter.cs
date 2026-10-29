using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using ProbablyStolenStockMarket.Contracts;
using ProbablyStolenStockMarket.Localization;
using ProbablyStolenStockMarket.UI;

namespace ProbablyStolenStockMarket.Game;

// 专号只打开贷款业务窗口，放款必须经过窗口中的合同确认。
internal sealed class LoanPhoneAdapter : IDisposable
{
    internal const long PhoneNumber = 731207L;
    private readonly ILoanHost _host;
    private readonly ModLogger _log;
    private readonly NativeLoanPanel _panel;
    private readonly IntelPhoneQuickDial _quickDial;
    private readonly List<(MethodBase Original, MethodInfo Patch)> _patches = new();
    private HarmonyLib.Harmony? _harmony;
    private string _reportedConflictIdentity = "";
    private bool _disposed;
    private static LoanPhoneAdapter? Current;

    internal LoanPhoneAdapter(ILoanHost host, ModLogger log)
    {
        _host = host; _log = log; _panel = new NativeLoanPanel(host);
        _quickDial = new IntelPhoneQuickDial(log, "周转贷款专线", PhoneNumber, CanShowQuickDial, StartQuickDial, "借款、查询与还款");
    }

    internal bool BlocksGameInput => _panel.IsVisible;
    internal void Tick() { if (!_disposed) _panel.Tick(); }

    internal void Initialize(HarmonyLib.Harmony harmony)
    {
        if (Current != null) throw new InvalidOperationException("贷款电话适配器已初始化。");
        Current = this; _harmony = harmony;
        try
        {
            Patch(typeof(PhoneUIManager), "HandleCall", nameof(HandleCallPrefix), true, new[] { typeof(long) });
            Patch(typeof(PhoneUIManager), "AutoCall", nameof(AutoCallPrefix), true, new[] { typeof(long) });
            Patch(typeof(PhoneBookUIManager), "OpenUI", nameof(PhoneBookOpenedPostfix), false);
            _log.Msg($"[贷款电话] 已接入专号 {PhoneNumber}；认识贷款专员后解锁电话簿入口。");
        }
        catch { Dispose(); throw; }
    }

    internal void ShowLoanPanel(bool contractDocument = false)
    {
        if (_disposed || !_host.LoanContactUnlocked || _host.Engine == null) return;
        _panel.Show(contractDocument);
    }

    private bool CanShowQuickDial() => !_disposed && _host.Engine != null && _host.LoanContactUnlocked && !HasNumberConflict();

    private bool HasNumberConflict()
    {
        var store = PlayerStore.IsInstanceExist() ? PlayerStore.Instance : null;
        if (store?.PhoneClientDict == null) return true;
        if (!store.PhoneClientDict.ContainsKey(PhoneNumber)) return false;
        var identity = _host.LoanSaveIdentity;
        if (_reportedConflictIdentity != identity)
        {
            _reportedConflictIdentity = identity;
            _log.Warning($"[贷款电话] 号码 {PhoneNumber} 已由原版联系人占用，保留原版处理。");
        }
        return true;
    }

    private void StartQuickDial(PhoneUIManager phone)
    {
        if (!CanShowQuickDial()) return;
        phone.StopCall(); phone.phoneBookUIManager?.CloseUI();
        NativeGameAccess.SetPhoneNumber(phone, PhoneNumber); phone.StartCalling();
    }

    private bool HandleCall(PhoneUIManager phone, long number)
    {
        if (_disposed || number != PhoneNumber || HasNumberConflict()) return true;
        phone.StopCall();
        if (_host.Engine == null) { Feedback(phone, "请先进入游戏存档。"); return false; }
        if (!_host.LoanContactUnlocked) { Feedback(phone, "你还没有认识贷款专员，请等待他上门介绍。"); return false; }
        phone.phoneBookUIManager?.CloseUI();
        Feedback(phone, _host.Engine.State.Loan.Contract != null
            ? "你当前还有一笔贷款未结清，暂时无法办理新的贷款。可以查询账单或还款。"
            : "周转贷款专线：请选择贷款类型、金额和期限。");
        ShowLoanPanel();
        return false;
    }

    private static void Feedback(PhoneUIManager phone, string text)
    {
        // 错误在电话状态区直接可见，合同和详细账单由独立业务窗口展示。
        if (phone.statusDisplay != null) { phone.statusDisplay.richText = false; phone.statusDisplay.text = Localizer.Text(text); }
        Current?._log.Msg("[贷款电话] " + text);
    }

    internal void Reset()
    {
        _panel.Hide(); _quickDial.Reset(); _reportedConflictIdentity = "";
    }

    private void Patch(Type type, string name, string handler, bool prefix, Type[]? arguments = null)
    {
        var original = arguments == null ? AccessTools.Method(type, name) : AccessTools.Method(type, name, arguments);
        if (original == null) throw new MissingMethodException(type.FullName, name);
        var patch = AccessTools.Method(typeof(LoanPhoneAdapter), handler) ?? throw new MissingMethodException(handler);
        _harmony!.Patch(original, prefix ? new HarmonyMethod(patch) : null, prefix ? null : new HarmonyMethod(patch));
        _patches.Add((original, patch));
    }

    private static bool HandleCallPrefix(PhoneUIManager __instance, long __0)
    {
        var adapter = Current;
        if (adapter == null) return true;
        try { return adapter.HandleCall(__instance, __0); }
        catch (Exception ex)
        {
            adapter._log.Warning("[贷款电话] 打开贷款服务失败：" + ex);
            if (__0 == PhoneNumber) Feedback(__instance, "贷款服务暂不可用，请稍后重新拨号。");
            return __0 != PhoneNumber;
        }
    }

    private static bool AutoCallPrefix(PhoneUIManager __instance, long __0)
    {
        var adapter = Current;
        if (adapter == null || __0 != PhoneNumber) return true;
        try
        {
            if (adapter.HasNumberConflict()) return true;
            if (!adapter.CanShowQuickDial())
            { __instance.StopCall(); Feedback(__instance, "你还没有认识贷款专员，请等待他上门介绍。"); return false; }
            adapter.StartQuickDial(__instance); return false;
        }
        catch (Exception ex)
        {
            adapter._log.Warning("[贷款电话] 快速拨号失败：" + ex);
            Feedback(__instance, "贷款服务暂不可用，请稍后重新拨号。"); return false;
        }
    }

    private static void PhoneBookOpenedPostfix(PhoneBookUIManager __instance)
    {
        try { Current?._quickDial.Ensure(__instance); }
        catch (Exception ex) { Current?._log.Warning("[贷款电话] 联系人显示失败，仍可手动拨打 731207：" + ex); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; Reset();
        if (_harmony != null)
            foreach (var patch in _patches) _harmony.Unpatch(patch.Original, patch.Patch);
        _patches.Clear(); _quickDial.Dispose(); _panel.Dispose(); _harmony = null;
        if (Current == this) Current = null;
    }
}
