using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using ProbablyStolenStockMarket.Localization;

namespace ProbablyStolenStockMarket.Game;

internal static class GamePatches
{
    private static readonly List<MethodBase> Patched = new();
    [ThreadStatic] private static TradeCapture? _trade;
    [ThreadStatic] private static int _tradeDepth;
    internal static void Install(HarmonyLib.Harmony harmony, ModLogger log)
    {
        Patch(harmony, typeof(PlayerStore), "LoadGame", prefix: nameof(LoadPrefix), finalizer: nameof(LoadFinalizer));
        Patch(harmony, typeof(PlayerStore), "SaveGame", prefix: nameof(SavePrefix), finalizer: nameof(SaveFinalizer));
        Patch(harmony, typeof(HandnoteUIManager), "OpenPanel", prefix: nameof(IntelCardReadPrefix));
        Patch(harmony, typeof(PlayerStore), "DismissCurrentClient", prefix: nameof(DismissPrefix), postfix: nameof(DismissPostfix));
        foreach (string name in new[] { "BuyItem", "SellItem", "BuyItemFromTable", "SellItemFromTable", "OnItemsSold" })
            Patch(harmony, typeof(PlayerStore), name, prefix: nameof(TradePrefix), finalizer: nameof(TradeFinalizer));
        // 原版批发直接扣总款并逐件回调，绕过 PlayerStore.BuyItem；以整个批发方法捕捉现金差。
        Patch(harmony, typeof(NegociationUIManager), "OnWholesaleClick", prefix: nameof(TradePrefix), finalizer: nameof(TradeFinalizer));
        Patch(harmony, typeof(PlayerStore), "OnItemBought", prefix: nameof(ObserveCallback));
        Patch(harmony, typeof(StoreClient), "OnItemBought", prefix: nameof(ObserveCallback));
        Patch(harmony, typeof(StoreClient), "OnItemSold", prefix: nameof(ObserveCallback));
        Patch(harmony, typeof(StoreEventManager), "ActivateEvent", prefix: nameof(EventPrefix), postfix: nameof(EventPostfix));
        Patch(harmony, typeof(StoreEventManager), "DeactivateEvent", prefix: nameof(EventDeactivatePrefix), finalizer: nameof(EventDeactivateFinalizer));
        Patch(harmony, typeof(StoreEventManager), "OnDayStart", prefix: nameof(EventDayStartPrefix), finalizer: nameof(EventDayStartFinalizer));
        Patch(harmony, typeof(StoreEventManager), "OnDayEnd", prefix: nameof(EventDayEndPrefix), finalizer: nameof(EventDayEndFinalizer));
        Patch(harmony, typeof(ItemMouseDoubleClickHandler), "DoubleClickAction", prefix: nameof(DoubleClickPrefix));
        Patch(harmony, typeof(PlayerStore), "CanSellThisItem", prefix: nameof(CanSellPrefix));
        Patch(harmony, typeof(PlayerStore), "PlacedItemForSelling", prefix: nameof(ProtectItemPrefix));
        Patch(harmony, typeof(GameItem), "ActivateSlotItem", prefix: nameof(ActivatePrefix));
        Patch(harmony, typeof(GameItem), "Target", prefix: nameof(ProtectTargetPrefix));
        // 面板允许按原版规则移动，便于从桌面或窗外收回；出售与道具激活仍由各自入口保护。
        Patch(harmony, typeof(GameItem), "GetDisplayName", postfix: nameof(ItemDisplayNamePostfix));
        Patch(harmony, typeof(ItemMouseDragHandler), "StartDrag", prefix: nameof(InputPrefix));
        // 原版独立输入分发不受 UGUI 窗口遮挡影响，模态期间在入口暂停，EventSystem 仍处理股市控件。
        Patch(harmony, typeof(InputActionManager), "Update", prefix: nameof(InputPrefix));
        foreach (string name in new[] { "OnClick", "CloseShutter", "EndAndClose" })
            Patch(harmony, typeof(StoreShutterButton), name, prefix: nameof(ShutterInputPrefix));
        Patch(harmony, typeof(StoreBell), "RingBell", prefix: nameof(InputPrefix));
        Patch(harmony, typeof(PlayerStore), "EndDay", prefix: nameof(DayEndPrefix));
        foreach (string name in new[] { "OpenShutter", "TryCallNextClient", "AcceptBarter", "PlacedItemForBuying", "PlacedItemForBarter" })
            Patch(harmony, typeof(PlayerStore), name, prefix: nameof(InputPrefix));
        log.Msg($"[股市面板] 成功安装 {Patched.Count} 个原生方法补丁。");
    }
    private static void Patch(HarmonyLib.Harmony harmony, Type type, string method, string? prefix = null, string? postfix = null, string? finalizer = null)
    {
        var original = AccessTools.Method(type, method) ?? throw new MissingMethodException(type.FullName, method);
        harmony.Patch(original, H(prefix), H(postfix), finalizer: H(finalizer)); Patched.Add(original);
    }
    private static HarmonyMethod? H(string? name) => name == null ? null : new HarmonyMethod(typeof(GamePatches), name);
    internal static void Uninstall(HarmonyLib.Harmony? harmony)
    {
        if (harmony != null) foreach (var method in Patched) harmony.Unpatch(method, HarmonyPatchType.All, harmony.Id);
        Patched.Clear(); _trade = null; _tradeDepth = 0;
    }
    private static void LoadPrefix() => GameBridge.Current?.BeginLoad();
    private static Exception? LoadFinalizer(Exception? __exception) { GameBridge.Current?.EndLoad(__exception == null); return __exception; }
    private static void SavePrefix() => GameBridge.Current?.BeforeSave();
    private static void IntelCardReadPrefix(GameItem __0)
    {
        try { if (__0 != null) GameBridge.Current?.PrepareIntelCardRead(__0); }
        catch (Exception ex) { UnityEngine.Debug.LogWarning("[股市情报名片] 阅读前更新失败：" + ex); }
    }
    private static Exception? SaveFinalizer(Exception? __exception) { GameBridge.Current?.SaveCompleted(__exception == null); return __exception; }
    private static bool DismissPrefix(PlayerStore __instance, out StoreClientInstance? __state)
    {
        __state = GameBridge.Current?.BlocksGameInput == true ? null : __instance.currentClientInstance;
        return GameBridge.Current?.BlocksGameInput != true;
    }
    private static void DismissPostfix(StoreClientInstance? __state) => GameBridge.Current?.Dismissed(__state);
    private static bool TradePrefix(MethodBase __originalMethod, object[] __args, out bool __state)
    {
        __state = false;
        var bridge = GameBridge.Current;
        if (bridge?.BlocksGameInput == true) return false;
        var items = new List<GameItem>();
        foreach (object arg in __args)
        {
            if (arg is GameItem item) items.Add(item);
            else if (arg is System.Collections.Generic.List<GameItem> list) foreach (var i in list) items.Add(i);
        }
        if (items.Any(i => bridge?.IsTerminal(i) == true)) return false;
        if (_tradeDepth == 0)
        {
            bool? buy = __originalMethod.Name == "OnWholesaleClick" || __originalMethod.Name.StartsWith("Buy", StringComparison.Ordinal) ? true : __originalMethod.Name.StartsWith("Sell", StringComparison.Ordinal) ? false : null;
            var store = PlayerStore.IsInstanceExist() ? PlayerStore.Instance : null;
            var snapshotItem = __originalMethod.Name == "OnWholesaleClick" ? null : items.FirstOrDefault() ?? store?.currentNegociatedItem;
            _trade = bridge?.BeginTrade(__originalMethod.Name, snapshotItem, buy);
        }
        if (_trade != null)
        {
            foreach (var item in items) if (_trade.Items.All(i => i.Id != item.uniqueId)) _trade.Items.Add(ItemCapture.Of(item));
            _tradeDepth++; __state = true;
        }
        return true;
    }
    private static Exception? TradeFinalizer(bool __state, Exception? __exception)
    {
        if (!__state) return __exception;
        if (--_tradeDepth == 0)
        {
            var trade = _trade; _trade = null;
            GameBridge.Current?.CompleteTrade(trade, __exception);
        }
        return __exception;
    }
    private static void ObserveCallback(MethodBase __originalMethod, GameItem __0, int __1)
    {
        GameBridge.Current?.LogTradeCallback($"{__originalMethod.DeclaringType?.Name}.{__originalMethod.Name}", __0, __1);
        if (_trade == null || __1 <= 0 || __originalMethod.DeclaringType != typeof(StoreClient)) return;
        _trade.CallbackAmounts[__0.uniqueId] = __1;
        if (_trade.Items.All(i => i.Id != __0.uniqueId)) _trade.Items.Add(ItemCapture.Of(__0));
    }
    private static void EventPrefix(StoreEventManager __instance, StoreEvent __0, out bool __state) => __state = __instance.IsEventActive(__0);
    private static void EventPostfix(StoreEventManager __instance, StoreEvent __0, bool __state) => GameBridge.Current?.EventActivated(__instance, __0, __state);
    private static void EventDeactivatePrefix(StoreEventManager __instance, StoreEvent __0, out EventLifecycleCapture? __state)
        => __state = GameBridge.Current?.BeforeEventDeactivated(__instance, __0);
    private static Exception? EventDeactivateFinalizer(EventLifecycleCapture? __state, Exception? __exception)
    { GameBridge.Current?.AfterEventDeactivated(__state, __exception == null); return __exception; }
    private static void EventDayStartPrefix(StoreEventManager __instance, out bool __state)
        => __state = GameBridge.Current?.BeginEventDayStart(__instance) == true;
    private static Exception? EventDayStartFinalizer(StoreEventManager __instance, bool __state, Exception? __exception)
    { GameBridge.Current?.CompleteEventDayStart(__instance, __state, __exception == null); return __exception; }
    private static void EventDayEndPrefix(StoreEventManager __instance) => GameBridge.Current?.PrepareEventDayEnd(__instance);
    private static Exception? EventDayEndFinalizer(StoreEventManager __instance, Exception? __exception)
    { GameBridge.Current?.CompleteEventDayEnd(__instance, __exception == null); return __exception; }
    private static bool DoubleClickPrefix(GameItem __0)
    {
        if (GameBridge.Current?.BlocksGameInput == true) return false;
        return GameBridge.Current?.OpenTerminal(__0) != true;
    }
    private static bool CanSellPrefix(GameItem __0, ref bool __result)
    {
        if (GameBridge.Current?.IsTerminal(__0) != true) return true;
        __result = false; return false;
    }
    private static void ItemDisplayNamePostfix(GameItem __instance, ref string __result)
    {
        // 只翻译专用入口与已认领情报纸条，名称标记仍以原文随存档保存。
        if (GameBridge.Current?.IsTerminal(__instance) == true) __result = Localizer.Text("股市面板");
        else if (GameBridge.Current?.IsLoanCard(__instance) == true) __result = Localizer.Text("沈衡的贷款名片");
        else if (GameBridge.Current?.IsIntelCard(__instance) == true) __result = Localizer.Text("股市情报名片");
        else if (IntelDeliveryAdapter.IsOwnPaper(__instance)) __result = Localizer.Text("股市情报纸条");
    }
    private static bool InputPrefix() => GameBridge.Current?.BlocksGameInput != true;
    private static bool ShutterInputPrefix(StoreShutterButton __instance, MethodBase __originalMethod)
    {
        bool allowed = InputPrefix();
        if (allowed && __originalMethod.Name == "EndAndClose")
            allowed = GameBridge.Current?.ConfirmStartingDebtDayEnd(__instance.EndAndClose) ?? true;
        GameBridge.Current?.TraceStoreAction("StoreShutterButton." + __originalMethod.Name, !allowed);
        return allowed;
    }
    private static bool DayEndPrefix(PlayerStore __instance)
    {
        bool allowed = InputPrefix();
        if (allowed) allowed = GameBridge.Current?.ConfirmStartingDebtDayEnd(__instance.EndDay) ?? true;
        GameBridge.Current?.TraceStoreAction("PlayerStore.EndDay", !allowed);
        return allowed;
    }
    private static bool ProtectItemPrefix(GameItem __0) => InputPrefix() && GameBridge.Current?.IsTerminal(__0) != true;
    private static bool ActivatePrefix(GameItem __instance) => InputPrefix() && GameBridge.Current?.IsTerminal(__instance) != true;
    private static bool ProtectTargetPrefix(GameItem __instance, GameItem __0) => InputPrefix() && GameBridge.Current?.IsTerminal(__instance) != true && GameBridge.Current?.IsTerminal(__0) != true;
}
