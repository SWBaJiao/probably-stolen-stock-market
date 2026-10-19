using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using HarmonyLib;
using Il2Cpp;
using Il2CppInterop.Runtime;
using MelonLoader;
using ProbablyStolenStockMarket.Market;

namespace ProbablyStolenStockMarket.Game;

// 纸条使用原生文档和柜台；领域记录先保存事务，再核对真实父容器。
internal sealed class IntelDeliveryAdapter : IDisposable
{
    private static IntelDeliveryAdapter? _current;
    private readonly GameBridge _host;
    private readonly MelonLogger.Instance _log;
    private readonly Dictionary<string, GameItem> _previewItems = new();
    private readonly HashSet<string> _busy = new();
    private readonly HashSet<string> _reported = new();
    private readonly HashSet<string> _persistenceFaults = new();
    private string _identity = "";
    private long _nextPoll;
    private bool _disposed;
    private HarmonyLib.Harmony? _harmony;
    private MethodInfo? _openPanel, _openPrefix;

    internal IntelDeliveryAdapter(GameBridge host, MelonLogger.Instance log) { _host = host; _log = log; }

    internal void Initialize(HarmonyLib.Harmony harmony)
    {
        if (_current != null) throw new InvalidOperationException("情报纸条适配器已经存在。");
        _current = this; _harmony = harmony;
        try
        {
            _openPanel = AccessTools.Method(typeof(HandnoteUIManager), "OpenPanel", new[] { typeof(GameItem) })
                ?? throw new MissingMethodException("HandnoteUIManager.OpenPanel(GameItem)");
            _openPrefix = AccessTools.Method(typeof(IntelDeliveryAdapter), nameof(OpenPaperPrefix))
                ?? throw new MissingMethodException(nameof(OpenPaperPrefix));
            harmony.Patch(_openPanel, prefix: new HarmonyMethod(_openPrefix));
        }
        catch { Dispose(); throw; }
    }

    private static void OpenPaperPrefix(GameItem __0)
    {
        var adapter = _current;
        if (adapter == null || adapter._disposed || !adapter._host.BrokerReady || __0 == null || __0.identifier != "handnote") return;
        try
        {
            var record = adapter._host.Engine?.GetIntelRecords().FirstOrDefault(r =>
                HasOfferMarker(__0, r.OfferId) && (r.PaperItemId >= 0 && r.PaperItemId == __0.uniqueId
                    || adapter._previewItems.TryGetValue(r.OfferId, out var preview) && preview.Pointer == __0.Pointer));
            // 必须同时命中本模组标记与已保存编号或自有预览实例，普通纸条不进入刷新流程。
            if (record != null) FillDocument(__0, record);
        }
        catch (Exception ex) { adapter._log.Warning("[股市情报纸条] 阅读前刷新失败，保留已有纸条正文：" + ex.Message); }
    }

    public void Tick()
    {
        if (_disposed || !_host.BrokerReady || Environment.TickCount64 < _nextPoll) return;
        _nextPoll = Environment.TickCount64 + 5000;
        if (_identity != _host.BrokerSaveIdentity) { Reset(); _identity = _host.BrokerSaveIdentity; }
        var records = _host.Engine?.GetIntelRecords();
        if (records == null) return;
        foreach (var record in records)
            if (record.PaperState != IntelPaperState.Delivered) GrantPurchasedTipOnce(record.OfferId);
    }

    public void GrantPurchasedTipOnce(string offerId)
    {
        if (_disposed || !_host.BrokerReady) return;
        if (_identity != _host.BrokerSaveIdentity) { Reset(); _identity = _host.BrokerSaveIdentity; }
        if (_persistenceFaults.Contains(offerId) || !_busy.Add(offerId)) return;
        GameItem? created = null;
        int createdId = -1;
        bool insertStarted = false;
        bool confirmed = false;
        bool uncertain = false;
        string identity = _host.BrokerSaveIdentity;
        var engine = _host.Engine;
        try
        {
            var record = engine?.GetIntelRecords().FirstOrDefault(r => r.OfferId == offerId);
            if (record == null || record.PaperState == IntelPaperState.Delivered) return;
            var entry = EmporiumEntry.Instance;
            var table = entry?.frontInvinvElement;
            // 原生 AddDirectSellingItemToTable 在 RVA 0x586932 使用此柜台入口。
            if (entry == null || table == null || entry.frontInvWindow?.childElement?.Pointer != table.Pointer
                || table.widthPixels <= 0 || table.heightPixels <= 0 || entry.invElement == null) return;
            var owned = entry.GetAllOwnedItems();
            var all = entry.GetAllItems();
            if (owned == null || all == null) return;
            var ownedPointers = new HashSet<IntPtr>();
            var candidates = new Dictionary<IntPtr, GameItem>();
            foreach (var item in owned) if (item != null) { candidates[item.Pointer] = item; ownedPointers.Add(item.Pointer); }
            foreach (var item in table.childItems) if (item != null) candidates[item.Pointer] = item;
            var allPointers = new HashSet<IntPtr>();
            foreach (var item in all) if (item != null) allPointers.Add(item.Pointer);
            // 查询必须覆盖实际主背包和柜台，空缓存不能被当成纸条丢失。
            foreach (var item in entry.invElement.childItems) if (item != null && !allPointers.Contains(item.Pointer)) return;
            foreach (var item in table.childItems) if (item != null && !allPointers.Contains(item.Pointer)) return;
            var existing = candidates.Values.FirstOrDefault(item => item.identifier == "handnote"
                && (item.uniqueId == record.PaperItemId || HasOfferMarker(item, offerId)));
            if (existing != null && (ownedPointers.Contains(existing.Pointer) || IsDirectMember(table, existing)))
            {
                SetState(engine!, offerId, IntelPaperState.Delivered, existing.uniqueId);
                PersistState(offerId);
                Once(offerId + ":adopt", $"已认领情报纸条 offer={offerId} item={existing.uniqueId} parent={existing.parentInventory?.Pointer}。");
                return;
            }
            // 记录过物品编号或存在不确定插入时，缺失查询绝不能触发第二次创建。
            if (record.PaperState == IntelPaperState.Uncertain || record.PaperItemId >= 0)
            { Once(offerId + ":uncertain", $"纸条位置待核实，停止再次创建 offer={offerId} item={record.PaperItemId}。"); return; }
            created = CreateDocument(record);
            createdId = created.uniqueId;
            if (!table.MayHaveValidInventorySlot(created) || table.TryFindOneValidInventorySlot(created) == null)
            { Once(offerId + ":full", $"柜台无可用位置，纸条保持待交付 offer={offerId}。"); return; }
            if (!SameSave(identity, engine)) return;
            SetState(engine!, offerId, IntelPaperState.Uncertain, created.uniqueId);
            PersistState(offerId);
            Once(offerId + ":probe", $"发放探针 offer={offerId} item={created.uniqueId} target=EmporiumEntry.frontInvinvElement pointer={table.Pointer} size={table.widthPixels}x{table.heightPixels}。");
            insertStarted = true;
            _host.CurrentStore!.AddDirectSellingItemToTable(created, true, false, true, 0);
            confirmed = created.parentInventory?.Pointer == table.Pointer && IsDirectMember(table, created);
            if (!confirmed)
            {
                var afterOwned = entry.GetAllOwnedItems();
                if (created.parentInventory != null && afterOwned != null)
                    foreach (var item in afterOwned) if (item != null && item.Pointer == created.Pointer) confirmed = true;
            }
            uncertain = !confirmed && created.parentInventory != null;
            if (!SameSave(identity, engine)) { uncertain = true; return; }
            if (confirmed)
            {
                SetState(engine!, offerId, IntelPaperState.Delivered, created.uniqueId);
                PersistState(offerId);
                _log.Msg($"[股市情报纸条] 柜台或已拥有父容器已确认 offer={offerId} item={created.uniqueId} parent={created.parentInventory?.Pointer}。");
            }
        }
        catch (Exception ex)
        {
            // 插入调用抛错后的结果无法可靠判定，保留编号供读档认领。
            uncertain = insertStarted || _persistenceFaults.Contains(offerId);
            _log.Warning($"[股市情报纸条] offer={offerId} stage={(insertStarted ? "插入确认" : "创建准备")}：{ex}");
        }
        finally
        {
            if (created != null && !confirmed && (!insertStarted || !uncertain))
            {
                try
                {
                    if (created.parentInventory == null)
                    {
                        created.Destroy();
                        if (!uncertain && SameSave(identity, engine))
                        {
                            var latest = engine!.GetIntelRecords().FirstOrDefault(r => r.OfferId == offerId);
                            // 只有原生插入明确未发生且临时对象销毁完成，才允许撤销这次物品编号。
                            if (latest?.PaperState == IntelPaperState.Uncertain)
                            {
                                if (!engine.ResetUndeliveredPaper(offerId, createdId))
                                    throw new InvalidOperationException("未交付纸条事务复位被拒绝，保持锁定。");
                                PersistState(offerId);
                            }
                            else if (latest == null || latest.PaperState != IntelPaperState.Pending || latest.PaperItemId >= 0)
                                throw new InvalidOperationException("临时纸条清理后的领域状态不匹配，保持锁定。");
                        }
                    }
                    else uncertain = true;
                }
                catch (Exception ex) { uncertain = true; _log.Warning("[股市情报纸条] 临时文档清理未确认：" + ex.Message); }
            }
            if (uncertain && SameSave(identity, engine))
            {
                try
                {
                    // 已确认交付后保存失败，领域层保留 Delivered，适配层另加会话锁，禁止降级重发。
                    var latest = engine!.GetIntelRecords().FirstOrDefault(r => r.OfferId == offerId);
                    if (latest?.PaperState != IntelPaperState.Delivered) SetState(engine, offerId, IntelPaperState.Uncertain, createdId);
                    PersistState(offerId);
                }
                catch (Exception ex) { _log.Error("[股市情报纸条] 不确定状态保存失败：" + ex.Message); }
            }
            _busy.Remove(offerId);
        }
    }

    internal bool OpenPurchasedTip(string offerId)
    {
        if (_disposed || !_host.BrokerReady) return false;
        var record = _host.Engine?.GetIntelRecords().FirstOrDefault(r => r.OfferId == offerId);
        if (record == null) return false;
        try
        {
            var viewer = HandnoteUIManager.Instance;
            if (viewer == null) throw new InvalidOperationException("原生 HandnoteUIManager 尚未就绪。");
            if (!_previewItems.TryGetValue(offerId, out var document))
            { document = CreateDocument(record); _previewItems.Add(offerId, document); }
            // 核实结果会变化，每次点击都重新写安全投影，不读取原版未来事件。
            FillDocument(document, record);
            viewer.OpenPanel(document);
            Once(offerId + ":view", $"原生手写文档接口已调用 offer={offerId}；实际显示等待游戏验收。");
            return true;
        }
        catch (Exception ex) { _log.Warning("[股市情报纸条] 原生阅读窗口未打开：" + ex); return false; }
    }

    private static GameItem CreateDocument(IntelRecord record)
    {
        var item = DirectoryMaster.Item("handnote", true) ?? throw new InvalidOperationException("原生 handnote 工厂返回空。");
        try { item.SetUnitCount(1); item.unitValue = 0; FillDocument(item, record); return item; }
        catch { if (item.parentInventory == null) item.Destroy(); throw; }
    }
    private static void FillDocument(GameItem item, IntelRecord record)
    {
        item.SetName("股市情报纸条");
        SetText(item, "HANDNOTE_TITLE", StatusLabel(record.Status) + " · " + record.Industry);
        SetText(item, "HANDNOTE_BODY", BuildBody(record));
        string source = record.SourceName == record.SourceIdentity ? record.SourceName : record.SourceName + " · " + record.SourceIdentity;
        SetText(item, "HANDNOTE_SIGN", source + Marker(record.OfferId));
        // HANDNOTE_FONT 保留原生 handnote 工厂默认值。
    }
    internal static string BuildBody(IntelRecord record)
    {
        string source = record.SourceName == record.SourceIdentity ? record.SourceName : record.SourceName + " · " + record.SourceIdentity;
        var body = new StringBuilder($"消息来源：{source}\n购买于第 {record.PurchaseDay} 日\n观察窗口：第 {record.ObservationStartDay}～{record.ObservationEndDay} 日\n来源记录：{(record.IsGroupIdentity ? "角色类型与阵营合并记录" : "个人记录")}\n参考可信度：{record.Reliability:P0}；属实 {record.Correct} / 失实 {record.Incorrect}\n\n{record.Content}\n\n记录状态：{StatusLabel(record.Status)}");
        // 核验字段由领域层安全投影；待核实时不展示排程、事件编号或内部真假结果。
        if (record.Status != RumorResolution.Pending)
        {
            if (!string.IsNullOrWhiteSpace(record.PublicNews)) body.Append("\n\n已公开消息：\n").Append(record.PublicNews);
            if (!string.IsNullOrWhiteSpace(record.VerificationSummary)) body.Append("\n\n核验对照：\n").Append(record.VerificationSummary);
        }
        return body.ToString();
    }
    private static void SetState(MarketEngine engine, string id, IntelPaperState state, int itemId = -1)
    { if (!engine.SetIntelPaperState(id, state, itemId)) throw new InvalidOperationException("情报纸条状态转换被拒绝：" + state); }
    internal static string StatusLabel(RumorResolution status) => status switch
    { RumorResolution.True => "已核实属实", RumorResolution.False => "已核实失实", RumorResolution.Unverified => "无法核实", _ => "未核实股市线索" };
    private static string Marker(string id) => "\n档案号：" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id)))[..12];
    private static bool HasOfferMarker(GameItem item, string id) => item.GetTagReadonly("HANDNOTE_SIGN")?.GetString()?.EndsWith(Marker(id), StringComparison.Ordinal) == true;
    private static void SetText(GameItem item, string key, string value)
    {
        Action<TagState> write = state => state.Enable().SetString(value);
        var native = DelegateSupport.ConvertDelegate<Il2CppSystem.Action<TagState>>(write)
            ?? throw new InvalidOperationException("纸条标签委托转换失败：" + key);
        item.ModifyTag(key, native);
        GC.KeepAlive(native); GC.KeepAlive(write);
        if (item.GetTagReadonly(key)?.GetString() != value) throw new InvalidOperationException("纸条标签读回不一致：" + key);
    }
    private static bool IsDirectMember(GameGridInventory table, GameItem item)
    { foreach (var child in table.childItems) if (child != null && child.Pointer == item.Pointer) return true; return false; }
    private bool SameSave(string identity, MarketEngine? engine) => _host.BrokerReady && _host.BrokerSaveIdentity == identity && ReferenceEquals(_host.Engine, engine);
    private void PersistState(string offerId)
    {
        try { _host.PersistBrokerState(); }
        catch { _persistenceFaults.Add(offerId); throw; }
    }
    private void Once(string key, string text) { if (_reported.Add(key)) _log.Msg("[股市情报纸条] " + text); }
    public void Reset()
    {
        // 预览文档从未插入游戏容器，只有这些自有临时对象可以销毁。
        foreach (var item in _previewItems.Values) try { if (item.parentInventory == null) item.Destroy(); } catch { }
        _previewItems.Clear(); _busy.Clear(); _reported.Clear(); _persistenceFaults.Clear(); _identity = ""; _nextPoll = 0;
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            if (_harmony != null && _openPanel != null && _openPrefix != null) _harmony.Unpatch(_openPanel, _openPrefix);
        }
        catch (Exception ex) { _log.Warning("[股市情报纸条] 阅读钩子解绑失败，继续释放资源：" + ex.Message); }
        finally
        {
            try { Reset(); }
            finally { if (ReferenceEquals(_current, this)) _current = null; }
        }
    }
}
