using System;
using System.Reflection;
using HarmonyLib;
using Il2Cpp;
using MelonLoader;

namespace MelonModTemplate.Game;

// 只接管专属预约号码，沿用原版电话状态栏反馈，不向原版电话簿写入联系人。
internal sealed class IntelPhoneAdapter : IDisposable
{
    internal const long PhoneNumber = 731206L;
    private readonly GameBridge _host;
    private readonly MelonLogger.Instance _log;
    private HarmonyLib.Harmony? _harmony;
    private MethodBase? _original;
    private MethodInfo? _prefix;
    private bool _disposed;
    private string _identity = "";
    private string _reportedConflictIdentity = "";
    private int _epoch;
    private static IntelPhoneAdapter? Current;

    internal IntelPhoneAdapter(GameBridge host, MelonLogger.Instance log) { _host = host; _log = log; }

    internal void Initialize(HarmonyLib.Harmony harmony)
    {
        if (Current != null) throw new InvalidOperationException("股市预约电话适配器已初始化。");
        _original = AccessTools.Method(typeof(PhoneUIManager), "HandleCall", new[] { typeof(long) })
            ?? throw new MissingMethodException(typeof(PhoneUIManager).FullName, "HandleCall(long)");
        _prefix = AccessTools.Method(typeof(IntelPhoneAdapter), nameof(HandleCallPrefix))
            ?? throw new MissingMethodException(nameof(HandleCallPrefix));
        _harmony = harmony;
        harmony.Patch(_original, prefix: new HarmonyMethod(_prefix));
        Current = this;
        _log.Msg($"[股市预约电话探针] 已安装原版拨号入口，专号={PhoneNumber}；每次拨号检查号码冲突，未写入原版电话簿。");
    }

    private bool HandleCall(PhoneUIManager phone, long number)
    {
        if (_disposed || number != PhoneNumber) return true;
        // 先查询实际存档里的原生号码，存在冲突时让原版完整处理，不能覆盖或占用原号。
        var store = PlayerStore.IsInstanceExist() ? PlayerStore.Instance : null;
        if (store?.PhoneClientDict == null) return true;
        if (store.PhoneClientDict.ContainsKey(PhoneNumber))
        {
            string conflictIdentity = $"{store.saveSlotId}:{store.runID}:{store.runNumber}";
            if (_reportedConflictIdentity != conflictIdentity)
            {
                _reportedConflictIdentity = conflictIdentity;
                _log.Warning($"[股市预约电话探针] 号码 {PhoneNumber} 已有原版或其他Mod联系人，本次使用原版处理，未覆盖。");
            }
            return true;
        }
        string identity = _host.BrokerSaveIdentity;
        if (_identity != identity) { Reset(); _identity = identity; }
        int epoch = _epoch;
        try
        {
            string feedback = _host.BookBrokerByPhone();
            // 预约已同步完成；切档或重置后的旧调用不能再修改新存档的电话界面。
            if (_disposed || epoch != _epoch || identity != _host.BrokerSaveIdentity) return false;
            phone.StopCall();
            if (phone.statusDisplay != null) phone.statusDisplay.text = feedback;
            _log.Msg($"[股市预约电话探针] 专号拨号已处理，号码={PhoneNumber}；预约结果已显示在原版电话状态栏。");
        }
        catch (Exception ex)
        {
            _log.Warning("[股市预约电话探针] 预约处理异常：" + ex.Message);
            if (!_disposed && epoch == _epoch && identity == _host.BrokerSaveIdentity)
            {
                try
                {
                    phone.StopCall();
                    if (phone.statusDisplay != null) phone.statusDisplay.text = "预约暂时失败，请稍后重拨。";
                }
                catch (Exception displayError) { _log.Warning("[股市预约电话探针] 电话反馈显示失败：" + displayError.Message); }
            }
        }
        return false;
    }

    internal void Reset()
    {
        // 不主动操作电话界面，避免载入和切档时打断原版通话。
        _epoch++; _identity = ""; _reportedConflictIdentity = "";
    }

    public void Dispose()
    {
        if (_disposed) return;
        Reset(); _disposed = true;
        if (_harmony != null && _original != null && _prefix != null) _harmony.Unpatch(_original, _prefix);
        _harmony = null; _original = null; _prefix = null;
        if (Current == this) Current = null;
    }

    private static bool HandleCallPrefix(PhoneUIManager __instance, long __0)
    {
        var adapter = Current;
        if (adapter == null || __0 != PhoneNumber) return true;
        try { return adapter.HandleCall(__instance, __0); }
        catch (Exception ex)
        {
            // 前置检查异常时不篡改原版电话状态，普通号码始终不经过此分支。
            adapter._log.Warning("[股市预约电话探针] 号码检查失败，保留原版处理：" + ex.Message);
            return true;
        }
    }
}
