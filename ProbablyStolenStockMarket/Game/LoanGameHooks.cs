using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace ProbablyStolenStockMarket.Game;

// 只在原版可见日结预检与夜间结算边界观察服务，普通购物余额不足不触发贷款。
internal sealed class LoanGameHooks : IDisposable
{
    private static LoanGameHooks? Current;
    private readonly GameBridge _host;
    private readonly ModLogger _log;
    private readonly List<(MethodBase Original, MethodInfo Patch)> _patches = new();
    private HarmonyLib.Harmony? _harmony;
    private string _lastError = "";
    private int _initializingServiceUi;

    internal LoanGameHooks(GameBridge host, ModLogger log) { _host = host; _log = log; }
    internal void Initialize(HarmonyLib.Harmony harmony)
    {
        if (Current != null) throw new InvalidOperationException("贷款日结监听已经初始化。");
        Current = this; _harmony = harmony;
        try
        {
            Patch(typeof(EndOfDayUIManager), "RefreshUI", nameof(ServiceUiPostfix), 1);
            Patch(typeof(EndOfDayUIManager), "InitUI", nameof(ServiceUiInitPrefix), 0);
            Patch(typeof(EndOfDayUIManager), "InitUI", nameof(ServiceUiInitFinalizer), 2);
            Patch(typeof(PlayerStore), "EndNight", nameof(NightPrefix), 0);
            Patch(typeof(PlayerStore), "EndNight", nameof(NightFinalizer), 2);
            Patch(typeof(GameOverUIManager), "DisplayUI", nameof(EndingUiPrefix), 0);
            Patch(typeof(HandnoteUIManager), "OpenPanel", nameof(CardReadPrefix), 0);
        }
        catch { Dispose(); throw; }
    }

    private void Patch(Type type, string method, string handler, int kind)
    {
        var original = AccessTools.Method(type, method) ?? throw new MissingMethodException(type.FullName, method);
        var patch = AccessTools.Method(typeof(LoanGameHooks), handler) ?? throw new MissingMethodException(handler);
        _harmony!.Patch(original, kind == 0 ? new HarmonyMethod(patch) : null, kind == 1 ? new HarmonyMethod(patch) : null,
            finalizer: kind == 2 ? new HarmonyMethod(patch) : null);
        _patches.Add((original, patch));
    }

    private static void ServiceUiPostfix(EndOfDayUIManager __instance)
    {
        var self = Current;
        if (self == null || self._initializingServiceUi > 0 || __instance.UI == null || !__instance.UI.activeInHierarchy) return;
        try
        {
            if (PlayerStore.IsInstanceExist()) self._host.ObserveLoanServiceDemand(PlayerStore.Instance, true);
        }
        catch (Exception ex) { self.Report(ex); }
    }

    private static void ServiceUiInitPrefix() { if (Current != null) Current._initializingServiceUi++; }
    private static Exception? ServiceUiInitFinalizer(EndOfDayUIManager __instance, Exception? __exception)
    {
        if (Current != null && Current._initializingServiceUi > 0) Current._initializingServiceUi--;
        // 服务条目初始化会逐项刷新，等待强制项和冷却项全部归位后才判断费用。
        if (__exception == null) ServiceUiPostfix(__instance);
        return __exception;
    }

    private static void NightPrefix(PlayerStore __instance, out LoanNightCapture? __state)
    {
        __state = null;
        try { __state = Current?._host.CaptureLoanNight(__instance); }
        catch (Exception ex) { Current?.Report(ex); }
    }

    private static Exception? NightFinalizer(LoanNightCapture? __state, Exception? __exception)
    {
        try { Current?._host.CompleteLoanNight(__state, __exception == null); }
        catch (Exception ex) { Current?.Report(ex); }
        return __exception;
    }

    private static void EndingUiPrefix(ref string __0, ref string __1, ref bool __2)
        => Current?._host.OverrideLoanEnding(ref __0, ref __1, ref __2);

    private static void CardReadPrefix(GameItem __0)
    {
        try { if (__0 != null) Current?._host.PrepareLoanCardRead(__0); }
        catch (Exception ex) { Current?.Report(ex); }
    }

    private void Report(Exception ex)
    {
        string text = ex.GetType().Name + ": " + ex.Message;
        if (_lastError == text) return;
        _lastError = text; _log.Warning("[贷款服务监听] " + text);
    }

    public void Dispose()
    {
        if (_harmony != null) foreach (var entry in _patches) _harmony.Unpatch(entry.Original, entry.Patch);
        _patches.Clear(); _harmony = null;
        if (Current == this) Current = null;
    }
}
