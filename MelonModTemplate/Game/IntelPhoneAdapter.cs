using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Il2Cpp;
using Il2CppInterop.Runtime;
using MelonLoader;
using MelonModTemplate.Market;

namespace MelonModTemplate.Game;

// 电话只创建确认票据；用户确认后才交由存档领域创建预约。
internal sealed class IntelPhoneAdapter : IDisposable
{
    internal const long PhoneNumber = 731206L;
    private readonly GameBridge _host;
    private readonly MelonLogger.Instance _log;
    private readonly List<(MethodBase Original, MethodInfo Patch)> _patches = new();
    private readonly List<ConfirmationTicket> _roots = new();
    private readonly IntelPhoneQuickDial _quickDial;
    private HarmonyLib.Harmony? _harmony;
    private ConfirmationTicket? _pending;
    private bool _disposed;
    private bool _opening;
    private string _reportedConflictIdentity = "";
    private int _epoch;
    private static IntelPhoneAdapter? Current;

    internal IntelPhoneAdapter(GameBridge host, MelonLogger.Instance log)
    {
        _host = host; _log = log;
        _quickDial = new IntelPhoneQuickDial(log, "行情消息专线", PhoneNumber, CanShowQuickDial, StartQuickDial);
    }

    internal bool BlocksGameInput
    {
        get
        {
            try
            {
                if (_pending is not { Consumed: false } ticket) return false;
                if (OwnsVisiblePrompt(ticket)) return true;
                if (!_opening) Invalidate(false);
                return false;
            }
            catch
            {
                if (_pending is { } ticket) ticket.Consumed = true;
                _pending = null;
                return false;
            }
        }
    }

    internal void Initialize(HarmonyLib.Harmony harmony)
    {
        if (Current != null) throw new InvalidOperationException("股市预约电话适配器已初始化。");
        _harmony = harmony; Current = this;
        try
        {
            Patch(typeof(PhoneUIManager), "HandleCall", nameof(HandleCallPrefix), true, new[] { typeof(long) });
            Patch(typeof(PhoneUIManager), "AutoCall", nameof(AutoCallPrefix), true, new[] { typeof(long) });
            foreach (string method in new[] { "StopCall", "PressNumber", "StartCalling", "OpenUI", "CloseUI", "OnPhoneDialogEnd" })
                Patch(typeof(PhoneUIManager), method, nameof(PhoneChangedPrefix), true);
            foreach (string method in new[] { "OpenPanel", "OpenPanelSingleButton", "ClosePanel" })
                Patch(typeof(GenericConfirmationUIManager), method, nameof(ConfirmationChangedPrefix), true);
            Patch(typeof(PhoneBookUIManager), "OpenUI", nameof(PhoneBookOpenedPostfix), false);
            _log.Msg($"[股市预约电话探针] 专号={PhoneNumber}，已安装确认后预约和电话簿临时快捷条目；未写原版电话簿存档。");
        }
        catch { Dispose(); throw; }
    }

    private bool HasNumberConflict(long number)
    {
        var store = PlayerStore.IsInstanceExist() ? PlayerStore.Instance : null;
        if (store?.PhoneClientDict == null) return true;
        if (!store.PhoneClientDict.ContainsKey(number)) return false;
        string identity = $"{store.saveSlotId}:{store.runID}:{store.runNumber}:{number}";
        if (_reportedConflictIdentity != identity)
        {
            _reportedConflictIdentity = identity;
            _log.Warning($"[股市预约电话探针] 号码 {number} 已有联系人，保留原版处理，不覆盖号码。");
        }
        return true;
    }

    private bool CanShowQuickDial() => !_disposed && _host.BrokerReady && _host.OwnsStockTerminal() && !HasNumberConflict(PhoneNumber);

    private void StartQuickDial(PhoneUIManager phone)
    {
        try
        {
            if (!CanShowQuickDial()) return;
            Invalidate(true);
            phone.StopCall();
            phone.phoneBookUIManager?.CloseUI();
            phone.currentNumber = PhoneNumber;
            phone.StartCalling();
        }
        catch (Exception ex) { Report("快捷拨号失败", ex); }
    }

    private bool HandleCall(PhoneUIManager phone, long number)
    {
        Invalidate(true);
        if (_disposed || number != PhoneNumber || HasNumberConflict(number)) return true;
        phone.StopCall();
        if (!_host.BrokerReady) { Feedback(phone, "请先进入游戏存档。"); return false; }
        if (!_host.OwnsStockTerminal()) { Feedback(phone, "持有本存档的股市面板道具后，才能预约消息商。"); return false; }
        var ui = GenericConfirmationUIManager.Instance;
        if (ui == null || ui.panel == null) { Feedback(phone, "预约确认界面暂未就绪，请稍后重拨。"); return false; }
        if (ui.panel.activeSelf) { Feedback(phone, "请先处理当前确认窗口，再重新拨号。"); return false; }
        var engine = _host.Engine;
        int day = StoreStation.GetDayCounter();
        if (engine == null || engine.State.LastDay != day) { Feedback(phone, "行情正在更新，请稍后重拨。"); return false; }
        phone.phoneBookUIManager?.CloseUI();
        var ticket = new ConfirmationTicket(_epoch, _host.BrokerSaveIdentity, engine, day, phone, ui);
        ticket.ManagedConfirm = () => Resolve(ticket, true);
        ticket.ManagedCancel = () => Resolve(ticket, false);
        ticket.Confirm = DelegateSupport.ConvertDelegate<Il2CppSystem.Action>(ticket.ManagedConfirm)
            ?? throw new InvalidOperationException("预约确认委托转换失败。");
        ticket.Cancel = DelegateSupport.ConvertDelegate<Il2CppSystem.Action>(ticket.ManagedCancel)
            ?? throw new InvalidOperationException("预约取消委托转换失败。");
        _pending = ticket; _roots.Add(ticket);
        try
        {
            _opening = true;
            ui.OpenPanel("这里是行情消息专线，闻舟和剑清酱共用这个号码。是否预约一位消息商明天到店？\n\n确认后随机安排一人；重复拨号只查询已有预约，不重新抽取。", "预约明日到访", "暂不预约", ticket.Confirm, ticket.Cancel);
            Feedback(phone, "行情消息专线：请确认是否预约明日到访。");
            _log.Msg("[股市预约电话探针] 已显示确认，尚未调用预约领域。");
        }
        catch { Invalidate(true); throw; }
        finally { _opening = false; }
        return false;
    }

    private void Resolve(ConfirmationTicket ticket, bool confirm)
    {
        try
        {
            bool wasPending = ReferenceEquals(_pending, ticket);
            bool ownedFields = FieldsOwned(ticket);
            bool valid = wasPending && !ticket.Consumed && !_disposed && ticket.Epoch == _epoch
                && ticket.Identity == _host.BrokerSaveIdentity && ReferenceEquals(ticket.Engine, _host.Engine)
                && _host.BrokerReady && ticket.Day == ticket.Engine.State.LastDay && ticket.Day == StoreStation.GetDayCounter()
                && SamePhone(ticket) && OwnsVisiblePrompt(ticket);
            // 必须先消费票据并解绑，使输入锁释放，随后才能调用预约入口。
            Consume(ticket, false);
            if (!valid) { if (wasPending && ownedFields && ticket.Epoch == _epoch && ticket.Identity == _host.BrokerSaveIdentity && SamePhone(ticket)) Feedback(ticket.Phone, "预约确认已失效，请重新拨号。"); return; }
            if (!confirm) { Feedback(ticket.Phone, "已取消本次预约确认，未创建新预约。"); return; }
            string feedback = _host.BookBrokerByPhone();
            if (!_disposed && ticket.Epoch == _epoch && ticket.Identity == _host.BrokerSaveIdentity && ReferenceEquals(ticket.Engine, _host.Engine) && SamePhone(ticket))
                Feedback(ticket.Phone, feedback);
            _log.Msg("[股市预约电话探针] 用户确认已消费，已调用一次预约领域。");
        }
        catch (Exception ex)
        {
            Consume(ticket, false); Report("确认预约失败", ex);
            if (ticket.Epoch == _epoch && ticket.Identity == _host.BrokerSaveIdentity && SamePhone(ticket)) Feedback(ticket.Phone, "预约结果暂未确认，请重新拨号查询。");
        }
    }

    private static bool FieldsOwned(ConfirmationTicket ticket) => ticket.Manager != null
        && ticket.Manager.onConfirm?.Pointer == ticket.Confirm?.Pointer && ticket.Manager.onCancel?.Pointer == ticket.Cancel?.Pointer;
    private static bool OwnsVisiblePrompt(ConfirmationTicket ticket) => FieldsOwned(ticket) && ticket.Manager.panel != null && ticket.Manager.panel.activeSelf;
    private static bool SamePhone(ConfirmationTicket ticket) => ticket.Phone != null && PhoneUIManager.Instance != null
        && PhoneUIManager.Instance.Pointer == ticket.Phone.Pointer && ticket.Phone.phonePanel != null && ticket.Phone.phonePanel.activeInHierarchy
        && ticket.Phone.currentNumber == 0 && !ticket.Phone.isCalling && !ticket.Phone.isTalking;

    private void Consume(ConfirmationTicket ticket, bool close)
    {
        ticket.Consumed = true;
        if (ReferenceEquals(_pending, ticket)) _pending = null;
        try
        {
            if (!FieldsOwned(ticket)) return;
            ticket.Manager.onConfirm = null; ticket.Manager.onCancel = null;
            if (close && ticket.Manager.panel != null && ticket.Manager.panel.activeSelf) ticket.Manager.ClosePanel();
        }
        catch (Exception ex) { Report("确认回调清理失败", ex); }
    }
    private void Invalidate(bool close)
    {
        var ticket = _pending;
        if (ticket != null) Consume(ticket, close);
    }
    private static void Feedback(PhoneUIManager phone, string text)
    {
        // 原版听筒状态区只容纳短状态，详细结果保留日志，预约状态可在内幕档案查看。
        if (phone != null && phone.statusDisplay != null) phone.statusDisplay.text = "通话中";
        Current?._log.Msg("[股市预约电话] " + text);
    }

    internal void Reset()
    {
        _epoch++; Invalidate(true); _quickDial.Reset(); _roots.Clear(); _reportedConflictIdentity = "";
    }
    public void Dispose()
    {
        if (_disposed) return;
        Reset(); _disposed = true;
        if (_harmony != null) foreach (var patch in _patches) _harmony.Unpatch(patch.Original, patch.Patch);
        _patches.Clear(); _quickDial.Dispose(); _harmony = null;
        if (Current == this) Current = null;
    }
    private void Report(string stage, Exception ex) => _log.Warning("[股市预约电话探针] " + stage + "：" + ex.Message);
    private void Patch(Type type, string name, string handler, bool prefix, Type[]? arguments = null)
    {
        var original = arguments == null ? AccessTools.Method(type, name) : AccessTools.Method(type, name, arguments);
        if (original == null) throw new MissingMethodException(type.FullName, name);
        var patch = AccessTools.Method(typeof(IntelPhoneAdapter), handler) ?? throw new MissingMethodException(handler);
        _harmony!.Patch(original, prefix ? new HarmonyMethod(patch) : null, prefix ? null : new HarmonyMethod(patch));
        _patches.Add((original, patch));
    }
    private static bool HandleCallPrefix(PhoneUIManager __instance, long __0)
    {
        var adapter = Current;
        if (adapter == null) return true;
        try { return adapter.HandleCall(__instance, __0); }
        catch (Exception ex) { adapter.Invalidate(true); adapter.Report("拨号确认失败", ex); return __0 != PhoneNumber; }
    }
    private static bool AutoCallPrefix(PhoneUIManager __instance, long __0)
    {
        var adapter = Current;
        if (adapter == null) return true;
        try
        {
            adapter.Invalidate(true);
            if (__0 != PhoneNumber || adapter.HasNumberConflict(__0)) return true;
            if (!adapter._host.BrokerReady || !adapter._host.OwnsStockTerminal()) { __instance.StopCall(); Feedback(__instance, "持有本存档的股市面板后，才能预约消息商。"); return false; }
            adapter.StartQuickDial(__instance); return false;
        }
        catch (Exception ex) { adapter.Report("快速拨号检查失败", ex); return __0 != PhoneNumber; }
    }
    private static void PhoneChangedPrefix(PhoneUIManager __instance)
    {
        var adapter = Current;
        if (adapter?._pending is not { } ticket || ticket.Phone.Pointer != __instance.Pointer) return;
        adapter.Invalidate(true);
    }
    private static void ConfirmationChangedPrefix(GenericConfirmationUIManager __instance)
    {
        var adapter = Current;
        if (adapter == null || adapter._opening || adapter._pending is not { } ticket || ticket.Manager.Pointer != __instance.Pointer) return;
        adapter.Invalidate(false);
    }
    private static void PhoneBookOpenedPostfix(PhoneBookUIManager __instance)
    {
        var adapter = Current;
        try { adapter?._quickDial.Ensure(__instance); }
        catch (Exception ex) { adapter?.Report("快捷联系人创建失败，仍可手动拨号", ex); }
    }

    private sealed class ConfirmationTicket
    {
        internal readonly int Epoch;
        internal readonly string Identity;
        internal readonly MarketEngine Engine;
        internal readonly int Day;
        internal readonly PhoneUIManager Phone;
        internal readonly GenericConfirmationUIManager Manager;
        internal Action? ManagedConfirm;
        internal Action? ManagedCancel;
        internal Il2CppSystem.Action? Confirm;
        internal Il2CppSystem.Action? Cancel;
        internal bool Consumed;
        internal ConfirmationTicket(int epoch, string identity, MarketEngine engine, int day, PhoneUIManager phone, GenericConfirmationUIManager manager)
        { Epoch = epoch; Identity = identity; Engine = engine; Day = day; Phone = phone; Manager = manager; }
    }
}
