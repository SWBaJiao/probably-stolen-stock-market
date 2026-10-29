using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using ProbablyStolenStockMarket.Localization;
using ProbablyStolenStockMarket.Market;

namespace ProbablyStolenStockMarket.Game;

// 联系方式与实物名片分开保存，柜台满或名片遗失都不影响电话办理。
internal sealed class LoanCardAdapter : IDisposable
{
    private readonly GameBridge _host;
    private readonly ModLogger _log;
    private readonly LoanCardVisual _visual;
    private string _identity = "";
    private long _nextPoll;
    private bool _busy;
    private bool _persistenceFault;
    private bool _disposed;

    internal LoanCardAdapter(GameBridge host, ModLogger log) { _host = host; _log = log; _visual = new LoanCardVisual(log); }

    internal void Tick()
    {
        if (_disposed || !_host.BrokerReady || NativeGameAccess.Milliseconds < _nextPoll) return;
        _nextPoll = NativeGameAccess.Milliseconds + 5000;
        TryGrant();
        RefreshVisual();
    }

    internal void TryGrant()
    {
        if (_disposed || !_host.BrokerReady || _busy) return;
        if (_identity != _host.BrokerSaveIdentity) { Reset(); _identity = _host.BrokerSaveIdentity; }
        var engine = _host.Engine;
        if (engine == null || !engine.State.Loan.HasMetLender || engine.State.Loan.CardDelivered || _persistenceFault) return;
        _busy = true;
        string identity = _identity;
        GameItem? created = null;
        bool insertStarted = false, confirmed = false, uncertain = false;
        int itemId = -1;
        try
        {
            var entry = EmporiumEntry.Instance;
            var table = entry?.frontInvinvElement;
            if (entry == null || table == null || entry.invElement == null || entry.frontInvWindow?.childElement != table
                || table.widthPixels <= 0 || table.heightPixels <= 0) return;
            var owned = entry.GetAllOwnedItems();
            var all = entry.GetAllItems();
            if (owned == null || all == null) return;
            var allPointers = new HashSet<object>(NativeReferenceComparer.Instance);
            foreach (var item in all) if (item != null) allPointers.Add(item);
            foreach (var item in entry.invElement.childItems) if (item != null && !allPointers.Contains(item)) return;
            foreach (var item in table.childItems) if (item != null && !allPointers.Contains(item)) return;
            var candidates = new Dictionary<object, GameItem>(NativeReferenceComparer.Instance);
            foreach (var item in owned) if (item != null) candidates[item] = item;
            foreach (var item in table.childItems) if (item != null) candidates[item] = item;
            var existing = candidates.Values.FirstOrDefault(item => item.identifier == "handnote" && HasMarker(item, identity)
                && (engine.State.Loan.CardItemId < 0 || engine.State.Loan.CardItemId == item.uniqueId));
            if (existing != null)
            {
                SetState(engine, true, existing.uniqueId, false); Persist();
                _visual.Apply(existing);
                return;
            }
            if (engine.State.Loan.CardUncertain || engine.State.Loan.CardItemId >= 0) return;
            created = Create(identity); itemId = created.uniqueId;
            if (!table.MayHaveValidInventorySlot(created) || table.TryFindOneValidInventorySlot(created) == null) return;
            if (!SameSave(identity, engine)) return;
            SetState(engine, false, itemId, true); Persist();
            insertStarted = true;
            _host.CurrentStore!.AddDirectSellingItemToTable(created, true, false, true, 0);
            confirmed = created.parentInventory == table && DirectMember(table, created);
            if (!confirmed && created.parentInventory != null)
            {
                var afterOwned = entry.GetAllOwnedItems();
                if (afterOwned != null) foreach (var item in afterOwned) if (item == created) confirmed = true;
            }
            uncertain = !confirmed && created.parentInventory != null;
            if (!SameSave(identity, engine)) { uncertain = true; return; }
            if (confirmed)
            {
                SetState(engine, true, itemId, false); Persist();
                _visual.Apply(created);
                _log.Msg($"[贷款名片] 已确认柜台交付，item={itemId}；电话联系方式独立保留。");
            }
        }
        catch (Exception ex)
        {
            uncertain = insertStarted || _persistenceFault;
            _log.Warning("[贷款名片] 交付暂未完成：" + ex.Message);
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
                        if (!uncertain && SameSave(identity, engine) && engine.State.Loan.CardItemId == itemId)
                        { SetState(engine, false, -1, false); Persist(); }
                    }
                    else uncertain = true;
                }
                catch (Exception ex) { uncertain = true; _log.Warning("[贷款名片] 临时名片清理未确认：" + ex.Message); }
            }
            if (uncertain && SameSave(identity, engine))
            {
                try
                {
                    if (!engine.State.Loan.CardDelivered) SetState(engine, false, itemId, true);
                    Persist();
                }
                catch (Exception ex) { _log.Warning("[贷款名片] 不确定交付状态保存失败：" + ex.Message); }
            }
            _busy = false;
        }
    }

    private GameItem Create(string identity)
    {
        var item = DirectoryMaster.Item("handnote", true) ?? throw new InvalidOperationException("原生名片纸张创建失败。");
        try
        {
            item.SetName("沈衡的贷款名片"); item.SetUnitCount(1); item.unitValue = 0;
            WriteContent(item, identity);
            return item;
        }
        catch { if (item.parentInventory == null) item.Destroy(); throw; }
    }

    private void WriteContent(GameItem item, string identity)
    {
        SetText(item, "HANDNOTE_TITLE", "沈衡 · 贷款专员");
        string body = _host.Engine?.State.Loan.Contract?.Source == LoanSource.StartingDebt
            ? "联系电话：731207\n\n负债店主\n固定旧债 5000 元，无利息或罚息\n28 个营业日内还清，无逾期宽限\n\n" + StartingDebtScenario.Rules + "\n\n电话 731207 可查询或还款。"
            : "联系电话：731207\n\n提供无抵押贷款与股票抵押贷款，同一时间只能持有一笔。贷款期限可选 1～28 个游戏日，最长四周，期限越长日利率越高。电话可办理贷款、查询账单、部分还款或提前结清。\n\n抵押股份在结清前不能卖出。到期后前两天为宽限期，第三天开始罚息或处置抵押物；部分还款不会推迟期限。到期后第八天仍有欠款，将执行店铺清算并结束本局。\n\n具体额度、利率和到期日以签约时的合同为准。请确认条款后再借款。";
        SetText(item, "HANDNOTE_BODY", body);
        // 可见签名随语言转换，认领用的 ASCII 后缀始终保持稳定。
        SetText(item, "HANDNOTE_SIGN", "沈衡 · 贷款专员" + Marker(identity));
    }

    internal bool IsOwnCard(GameItem item)
    {
        if (_disposed || !_host.BrokerReady || item == null || item.identifier != "handnote") return false;
        try
        {
            var loan = _host.Engine?.State.Loan;
            return loan != null && loan.CardItemId >= 0 && item.uniqueId == loan.CardItemId && HasMarker(item, _host.BrokerSaveIdentity);
        }
        catch { return false; }
    }

    internal void PrepareRead(GameItem item)
    {
        if (!IsOwnCard(item)) return;
        try { WriteContent(item, _host.BrokerSaveIdentity); }
        catch (Exception ex) { _log.Warning("[贷款名片] 阅读前更新语言失败，保留已有文档：" + ex.Message); }
    }

    private static string Marker(string identity) => "\nPS_LOAN_CARD_V1:" + NativeGameAccess.HashPrefix(identity, 16);
    private static bool HasMarker(GameItem item, string identity) => item.GetTagReadonly("HANDNOTE_SIGN")?.GetString()?.EndsWith(Marker(identity), StringComparison.Ordinal) == true;
    private static bool DirectMember(GameGridInventory table, GameItem item)
    { foreach (var child in table.childItems) if (child == item) return true; return false; }
    private static void SetState(MarketEngine engine, bool delivered, int itemId, bool uncertain)
    { engine.State.Loan.CardDelivered = delivered; engine.State.Loan.CardItemId = itemId; engine.State.Loan.CardUncertain = uncertain; }
    private bool SameSave(string identity, MarketEngine engine) => _host.BrokerReady && _host.BrokerSaveIdentity == identity && ReferenceEquals(_host.Engine, engine);
    private void Persist()
    { try { _host.PersistBrokerState(); } catch { _persistenceFault = true; throw; } }
    private static void SetText(GameItem item, string key, string text)
    {
        string value = Localizer.Text(text);
        Action<TagState> write = state => state.Enable().SetString(value);
        var native = write;
        item.ModifyTag(key, native);
        GC.KeepAlive(native); GC.KeepAlive(write);
        if (item.GetTagReadonly(key)?.GetString() != value) throw new InvalidOperationException("名片标签读回不一致。");
    }
    private void RefreshVisual()
    {
        var loan = _host.Engine?.State.Loan;
        if (loan == null || loan.CardItemId < 0 || !_host.BrokerReady) return;
        try
        {
            var items = EmporiumEntry.Instance?.GetAllItems();
            if (items == null) return;
            foreach (var item in items)
                if (item != null && item.identifier == "handnote" && item.uniqueId == loan.CardItemId && HasMarker(item, _host.BrokerSaveIdentity))
                    _visual.Apply(item);
        }
        catch (Exception ex) { _log.Warning("[贷款名片] 图像刷新暂未完成：" + ex.Message); }
    }
    internal void BeforeSave() => _visual.RestoreForSave();
    internal void AfterSave() => _visual.ResumeAfterSave();
    internal void Reset() { _identity = ""; _nextPoll = 0; _persistenceFault = false; _visual.ResetTracking(); }
    public void Dispose() { _disposed = true; _visual.Dispose(); Reset(); }
}
