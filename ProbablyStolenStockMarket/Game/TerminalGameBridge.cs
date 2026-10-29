using System;
using System.Collections.Generic;
using System.Linq;

namespace ProbablyStolenStockMarket.Game;

public sealed partial class GameBridge
{
    public string RecoverTerminal()
    {
        string result = BlocksGameInput ? "操作繁忙，请稍后再试。" : EnsureTerminal(true);
        StoreUIManager.Instance?.Notify(ProbablyStolenStockMarket.Localization.Localizer.Text(result));
        return result;
    }

    private string EnsureTerminal(bool explicitRecovery)
    {
        if (_store == null || Engine == null || !IsCurrentStore() || _loading || _inMenu) return "请先进入存档。";
        var policy = _terminalGrant;
        GameItem? created = null;
        GameGridInventory? target = null;
        var grantSave = _save;
        var grantEngine = Engine;
        int previousId = _save.TerminalItemId;
        bool previousPending = _save.TerminalPlacementPending;
        bool previousGranted = Engine.State.TerminalGranted;
        string previousPath = _save.TerminalInventoryPath;
        bool attempted = false;
        bool confirmed = false;
        bool uncertain = false;
        try
        {
            var entry = EmporiumEntry.Instance;
            if (explicitRecovery && (ItemMouseDragHandler.current?.IsDraggingItem == true
                || ItemMultiSelectHandler.current?.IsGroupDragging == true || ItemSelectHandler.current?.IsEquipped == true))
                return "原股市面板仍存在或正在拖动，请先收好物品后再尝试补领。";
            target = entry?.invElement;
            var windowChild = entry?.invWindow?.childElement;
            bool ready = entry != null && target != null && windowChild != null
                && windowChild == target && target.widthPixels > 0 && target.heightPixels > 0;
            string probe = ready ? $"EmporiumEntry.invElement pointer={target!} identifier='{target!.identifier}' windowChild={windowChild!} size={target.widthPixels}x{target.heightPixels} directItems={target.childItems.Count}"
                : "EmporiumEntry.invElement 或主背包窗口尚未完成绑定，延后发放。";
            if (_lastInventoryProbe != probe) { _lastInventoryProbe = probe; _log.Msg("[股市背包探针] " + probe); }
            if (!ready || entry == null || target == null) return "主背包尚未就绪，稍后重试。";
            if (policy.AttemptInProgress) return "道具发放正在处理中。";
            var destinations = new[]
            {
                (Inventory: target, Window: entry.invWindow, Path: "EmporiumEntry.Instance.invElement", Message: "已将股市面板放入主背包。"),
                (Inventory: entry.backInvinvElement, Window: entry.backInvWindow, Path: "EmporiumEntry.Instance.backInvinvElement", Message: "背包已满，股市面板已放在桌面上。"),
                (Inventory: entry.frontInvinvElement, Window: entry.frontInvWindow, Path: "EmporiumEntry.Instance.frontInvinvElement", Message: "背包和桌面已满，股市面板已放在窗外。请在保存或结束营业前收好。")
            };
            if (destinations.Any(d => d.Inventory == null || d.Window?.childElement != d.Inventory
                || d.Inventory.widthPixels <= 0 || d.Inventory.heightPixels <= 0))
                return "物品放置区域尚未就绪，稍后重试。";

            // 自动发放始终保留会话锁，显式补领才执行完整扫描。
            if (policy.GrantedThisSession && !explicitRecovery)
            {
                if (_trackedTerminal != null && _trackedTerminal.parentInventory != null)
                    _terminalVisual.Apply(_trackedTerminal);
                return "已发放股市面板；丢失后可按 Ctrl+F8 免费补领。";
            }

            var all = entry!.GetAllItems();
            var owned = entry.GetAllOwnedItems();
            if (all == null || owned == null) return "背包完整查询尚未就绪，未创建道具。";
            var allItems = new Dictionary<object, GameItem>(NativeReferenceComparer.Instance);
            foreach (var item in all) if (item != null) allItems[item] = item;
            var ownedItems = new Dictionary<object, GameItem>(NativeReferenceComparer.Instance);
            foreach (var item in owned) if (item != null) ownedItems[item] = item;
            bool complete = true;
            foreach (var destination in destinations)
            foreach (var item in destination.Inventory.childItems)
            {
                if (item == null) continue;
                // 查询必须覆盖背包、桌面和窗外，缓存不一致不能被解释为丢失。
                if (!allItems.ContainsKey(item)) complete = false;
                ownedItems[item] = item;
            }
            if (!complete) return "背包查询与实际容器不一致，已暂停补领以避免重复创建。";
            // 全量扫描还包含嵌套容器、垃圾桶、隐藏区及夜间背包；不能漏认移走的旧面板。
            foreach (var item in allItems.Values) ownedItems[item] = item;
            GameItem? existing = ownedItems.Values.FirstOrDefault(item => item.identifier == "postit" && item.uniqueId == _save.TerminalItemId);
            if (existing == null)
                existing = ownedItems.Values.Where(item => item.identifier == "postit" && item.GetDisplayName(true) == TerminalName)
                    .OrderByDescending(item => item.uniqueId).FirstOrDefault();
            if (explicitRecovery && existing == null && _save.TerminalAccount.Opened)
            {
                if (policy.GrantedThisSession && _trackedTerminal == null)
                    return "上次交付位置尚未确认，请保存并重新载入后再按 Ctrl+F8 补领。";
                if (_trackedTerminal != null && !(_trackedTerminal is GameItemElement element && element.IsDestroyed()))
                    return "原股市面板仍存在或正在拖动，请先收好物品后再尝试补领。";
                if (!policy.ConfirmLossForRecovery()) return "道具发放正在处理中。";
            }
            var action = policy.Decide(true, existing != null, _save.TerminalItemId >= 0 || Engine.State.TerminalGranted || _save.TerminalPlacementPending, explicitRecovery);
            if (action == TerminalGrantAction.UseExisting)
            {
                _save.TerminalAccount.Opened = true;
                _trackedTerminal = existing!;
                _save.TerminalItemId = existing!.uniqueId; _save.TerminalPlacementPending = false;
                _save.TerminalInventoryPath = "EmporiumEntry.GetAllItems / GetAllOwnedItems";
                Engine.State.TerminalGranted = true;
                GeneralHelper.SetItemOwned(existing, true);
                Persist();
                existing.SetName(TerminalName); _terminalVisual.Apply(existing);
                _log.Msg($"[股市道具] 采用已有面板 uniqueId={existing.uniqueId}，未删除其他便笺，完整扫描={allItems.Count}。");
                return "已找到现有股市面板，请检查背包、桌面、窗外及其他容器，未重复发放。";
            }
            if (action == TerminalGrantAction.KeepGranted)
                return "存档已记录面板，自动发放已停止。确认丢失后可按 Ctrl+F8 显式补领。";
            if (!_save.TerminalAccount.Opened) return "请等待证券开户专员来访，支付 50 元开户后领取股市面板。";
            if (action != TerminalGrantAction.Create || !policy.BeginAttempt()) return "道具尚未就绪，稍后重试。";
            attempted = true;
            created = DirectoryMaster.Item("postit", true);
            if (created == null) return "原版便笺工厂不可用，稍后重试。";
            created.SetName(TerminalName); created.SetUnitCount(1);
            GeneralHelper.SetItemOwned(created, true);
            var destinationIndex = Array.FindIndex(destinations, d => d.Inventory.MayHaveValidInventorySlot(created)
                && d.Inventory.TryFindOneValidInventorySlot(created) != null);
            if (destinationIndex < 0) return "背包、桌面和窗外都已满。开户资格已保留，腾出空间后自动发放，也可按 Ctrl+F8 免费补领。";
            var chosen = destinations[destinationIndex];
            target = chosen.Inventory;

            // 先记录发放事务，再执行原生插入；重入时 policy 会阻止创建第二个道具。
            _save.TerminalItemId = created.uniqueId; _save.TerminalPlacementPending = true;
            _save.TerminalInventoryPath = chosen.Path;
            Persist();
            GraphUtils.TryAcceptAllMid(target, created);
            bool directMember = false;
            foreach (var item in target.childItems) if (item != null && item == created) directMember = true;
            confirmed = created.parentInventory == target && directMember;
            uncertain = !confirmed && created.parentInventory != null;
            if (!ReferenceEquals(grantSave, _save) || !ReferenceEquals(grantEngine, Engine) || !IsCurrentStore())
            { uncertain = created.parentInventory != null; return "存档发生变化，本次发放停止，请重新检查背包。"; }
            if (!confirmed)
            {
                if (uncertain) _log.Error($"[股市道具] 插入结果不确定 uniqueId={created.uniqueId}，保留事务并停止本次会话自动创建。");
                return uncertain ? "道具位置尚未确认，已停止重复发放。" : "主背包未接收道具，稍后重试。";
            }
            _trackedTerminal = created;
            _save.TerminalPlacementPending = false; Engine.State.TerminalGranted = true;
            Persist();
            _terminalVisual.Apply(created);
            _log.Msg($"[股市道具] 放置直接成员已确认 uniqueId={created.uniqueId} parent={target} path={_save.TerminalInventoryPath}；本次会话不会再次自动创建。");
            return chosen.Message + "\n丢失后可按 Ctrl+F8 免费补领，无需再次开户。";
        }
        catch (Exception ex)
        {
            if (created != null)
                try { if (created.parentInventory != null) uncertain = true; } catch { uncertain = true; }
            _log.Warning("[股市道具] 发放暂停：" + ex.Message);
            return "道具发放尚未确认，已保留状态以避免重复。";
        }
        finally
        {
            if (attempted)
            {
                // 只销毁本方法新建且明确未放入任何容器的临时物品。
                if (created != null && !confirmed && !uncertain)
                    try { if (created.parentInventory == null) created.Destroy(); else uncertain = true; }
                    catch (Exception ex) { uncertain = true; _log.Warning("[股市道具] 临时便笺清理失败，停止再次创建：" + ex.Message); }
                policy.FinishAttempt(confirmed, uncertain);
                if (uncertain && created != null && ReferenceEquals(grantSave, _save)) _trackedTerminal = created;
                if (!confirmed && !uncertain && ReferenceEquals(grantSave, _save))
                {
                    _save.TerminalItemId = previousId; _save.TerminalPlacementPending = previousPending;
                    _save.TerminalInventoryPath = previousPath;
                    if (Engine != null) Engine.State.TerminalGranted = previousGranted;
                    try { Persist(); } catch (Exception ex) { _log.Error("[股市道具] 发放事务回滚失败：" + ex.Message); }
                }
            }
        }
    }
    internal bool IsTerminal(GameItem? item) => _store != null && item != null && _save.TerminalItemId >= 0 && item.identifier == "postit" && item.uniqueId == _save.TerminalItemId && !_save.TerminalPlacementPending;
    internal bool OpenTerminal(GameItem item)
    {
        if (!IsTerminal(item)) return false;
        OpenPanelRequested?.Invoke(); return true;
    }
}
