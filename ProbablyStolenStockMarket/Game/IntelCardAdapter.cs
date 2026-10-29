using System;
using System.Collections.Generic;
using System.Linq;
using ProbablyStolenStockMarket.Localization;
using ProbablyStolenStockMarket.Market;

namespace ProbablyStolenStockMarket.Game;

// 名片只说明预约方式，电话权限继续以本存档实际持有的股市面板为准。
internal sealed class IntelCardAdapter : IDisposable
{
    internal const string CardName = "股市情报名片";
    internal const string ContactName = "行情消息专线";
    internal const string CardBody = "预约电话：731206\n\n持有本存档的股市面板后，可在电话簿选择“行情消息专线”，或手动拨打 731206。确认预约后，闻舟或剑清酱会在次日营业时随机来访，每次只安排一人。\n\n情报需要付费购买，消息可能属实，也可能失实。名片仅提供联系方式，不替代股市面板，也不会直接创建预约。";
    private readonly GameBridge _host;
    private readonly ModLogger _log;
    private readonly BusinessCardVisual _visual;
    private string _identity = "";
    private long _nextPoll;
    private bool _busy, _persistenceFault, _disposed;

    internal IntelCardAdapter(GameBridge host, ModLogger log)
    {
        _host = host; _log = log;
        _visual = new BusinessCardVisual(log, "ProbablyStolenStockMarket/intel_card", "ProbablyStolenStockMarket/intel_card_legacy",
            "intel_business_card", "intel_business_card.png", "股市情报名片图像");
    }

    internal void Tick()
    {
        if (_disposed || !_host.BrokerReady) return;
        if (_identity != _host.BrokerSaveIdentity) { Reset(); _identity = _host.BrokerSaveIdentity; }
        if (NativeGameAccess.Milliseconds < _nextPoll) return;
        _nextPoll = NativeGameAccess.Milliseconds + 5000;
        TryGrant();
        RefreshVisual();
    }

    internal void TryGrant()
    {
        if (_disposed || !_host.BrokerReady || _busy) return;
        if (_identity != _host.BrokerSaveIdentity) { Reset(); _identity = _host.BrokerSaveIdentity; }
        var engine = _host.Engine;
        if (engine == null || _host.IntelCardDelivered || _persistenceFault || !_host.OwnsStockTerminal()) return;
        _busy = true;
        string identity = _identity;
        GameItem? created = null;
        int itemId = -1;
        bool insertStarted = false, confirmed = false, uncertain = false;
        try
        {
            var entry = EmporiumEntry.Instance;
            var target = entry?.invElement;
            if (entry == null || target == null || entry.invWindow?.childElement != target || target.widthPixels <= 0 || target.heightPixels <= 0) return;
            var all = entry.GetAllItems();
            var owned = entry.GetAllOwnedItems();
            if (all == null || owned == null) return;
            var candidates = new Dictionary<object, GameItem>(NativeReferenceComparer.Instance);
            foreach (var item in all) if (item != null) candidates[item] = item;
            // 完整查询必须覆盖直接背包，不能把初始化中的空查询当成物品缺失。
            foreach (var item in target.childItems) if (item != null && !candidates.ContainsKey(item)) return;
            foreach (var item in owned) if (item != null) candidates[item] = item;
            var existing = candidates.Values.FirstOrDefault(item => item.identifier == "handnote" && HasMarker(item, identity)
                && (_host.IntelCardItemId < 0 || item.uniqueId == _host.IntelCardItemId));
            if (existing != null)
            {
                if (!SameSave(identity, engine)) return;
                SetState(true, existing.uniqueId, false); Persist();
                WriteContent(existing, identity);
                _visual.Apply(existing);
                return;
            }
            if (_host.IntelCardUncertain || _host.IntelCardItemId >= 0) return;
            created = Create(identity); itemId = created.uniqueId;
            if (!target.MayHaveValidInventorySlot(created) || target.TryFindOneValidInventorySlot(created) == null) return;
            if (!SameSave(identity, engine)) return;
            // 先保存准确物品编号，插入、重入或保存异常都不能重新生成第二张。
            SetState(false, itemId, true); Persist();
            insertStarted = true;
            GraphUtils.TryAcceptAllMid(target, created);
            confirmed = created.parentInventory == target && DirectMember(target, created);
            uncertain = !confirmed && created.parentInventory != null;
            if (!SameSave(identity, engine)) { uncertain = insertStarted; return; }
            if (!confirmed) return;
            SetState(true, itemId, false); Persist();
            _visual.Apply(created);
            _log.Msg($"[股市情报名片] 主背包直接成员已确认，item={itemId}；预约仍需持有股市面板并通过电话确认。");
        }
        catch (Exception ex)
        {
            uncertain = insertStarted || _persistenceFault;
            _log.Warning("[股市情报名片] 发放尚未确认：" + ex.Message);
        }
        finally
        {
            // 满包或明确插入失败时只销毁本次未进入容器的临时纸张，之后可以安全重试。
            if (created != null && !confirmed && (!insertStarted || !uncertain))
            {
                try
                {
                    if (created.parentInventory == null)
                    {
                        created.Destroy();
                        if (!uncertain && SameSave(identity, engine) && _host.IntelCardItemId == itemId)
                        { SetState(false, -1, false); Persist(); }
                    }
                    else uncertain = true;
                }
                catch (Exception ex) { uncertain = true; _log.Warning("[股市情报名片] 临时物品清理未确认：" + ex.Message); }
            }
            if (uncertain && SameSave(identity, engine))
            {
                try
                {
                    if (!_host.IntelCardDelivered) SetState(false, itemId, true);
                    Persist();
                }
                catch (Exception ex) { _log.Warning("[股市情报名片] 不确定交付状态保存失败：" + ex.Message); }
            }
            _busy = false;
        }
    }

    private static GameItem Create(string identity)
    {
        var item = DirectoryMaster.Item("handnote", true) ?? throw new InvalidOperationException("原生情报名片纸张创建失败。");
        try
        {
            item.SetName(CardName); item.SetUnitCount(1); item.unitValue = 0;
            WriteContent(item, identity);
            return item;
        }
        catch { if (item.parentInventory == null) item.Destroy(); throw; }
    }

    private static void WriteContent(GameItem item, string identity)
    {
        SetText(item, "HANDNOTE_TITLE", CardName);
        SetText(item, "HANDNOTE_BODY", CardBody);
        SetText(item, "HANDNOTE_SIGN", ContactName + Marker(identity));
    }

    internal bool IsOwnCard(GameItem item)
    {
        if (_disposed || !_host.BrokerReady || item == null || item.identifier != "handnote") return false;
        try { return _host.IntelCardItemId >= 0 && item.uniqueId == _host.IntelCardItemId && HasMarker(item, _host.BrokerSaveIdentity); }
        catch { return false; }
    }

    internal void PrepareRead(GameItem item)
    {
        if (!IsOwnCard(item)) return;
        try { WriteContent(item, _host.BrokerSaveIdentity); }
        catch (Exception ex) { _log.Warning("[股市情报名片] 阅读前更新语言失败，保留已有文档：" + ex.Message); }
    }

    private static string Marker(string identity) => "\nPS_INTEL_CARD_V1:" + NativeGameAccess.HashPrefix(identity, 16);
    private static bool HasMarker(GameItem item, string identity) => item.GetTagReadonly("HANDNOTE_SIGN")?.GetString()?.EndsWith(Marker(identity), StringComparison.Ordinal) == true;
    private static bool DirectMember(GameGridInventory target, GameItem item)
    { foreach (var child in target.childItems) if (child == item) return true; return false; }
    private void SetState(bool delivered, int itemId, bool uncertain)
    { _host.IntelCardDelivered = delivered; _host.IntelCardItemId = itemId; _host.IntelCardUncertain = uncertain; }
    private bool SameSave(string identity, MarketEngine engine) => _host.BrokerReady && _host.BrokerSaveIdentity == identity && ReferenceEquals(_host.Engine, engine);
    private void Persist()
    { try { _host.PersistBrokerState(); } catch { _persistenceFault = true; throw; } }

    private static void SetText(GameItem item, string key, string text)
    {
        string value = Localizer.Text(text);
        Action<TagState> write = state => state.Enable().SetString(value);
        item.ModifyTag(key, write);
        GC.KeepAlive(write);
        if (item.GetTagReadonly(key)?.GetString() != value) throw new InvalidOperationException("情报名片标签读回不一致。");
    }

    private void RefreshVisual()
    {
        if (!_host.BrokerReady || _host.IntelCardItemId < 0) return;
        try
        {
            var items = EmporiumEntry.Instance?.GetAllItems();
            if (items == null) return;
            foreach (var item in items) if (IsOwnCard(item)) _visual.Apply(item);
        }
        catch (Exception ex) { _log.Warning("[股市情报名片] 图像刷新暂未完成：" + ex.Message); }
    }

    internal void BeforeSave() => _visual.RestoreForSave();
    internal void AfterSave() => _visual.ResumeAfterSave();
    internal void Reset() { _identity = ""; _nextPoll = 0; _persistenceFault = false; _visual.ResetTracking(); }
    public void Dispose() { if (_disposed) return; _disposed = true; _visual.Dispose(); Reset(); }
}
