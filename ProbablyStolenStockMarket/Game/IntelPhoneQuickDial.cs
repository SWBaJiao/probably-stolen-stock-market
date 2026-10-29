using System;
using System.Collections.Generic;
using ProbablyStolenStockMarket.Localization;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace ProbablyStolenStockMarket.Game;

// 临时电话簿条目只复制原版联系人视图，不登记联系人，也不调用查询存档的 OnInit。
internal sealed class IntelPhoneQuickDial : IDisposable
{
    private readonly ModLogger _log;
    private readonly Func<bool> _canShow;
    private readonly Action<PhoneUIManager> _dial;
    private readonly string _name;
    private readonly long _number;
    private readonly string _status;
    private GameObject? _view;
    private Button? _button;
    private Button.ButtonClickedEvent? _event;
    private Action? _managed;
    private UnityAction? _native;
    private bool _disposed;

    internal IntelPhoneQuickDial(ModLogger log, string name, long number, Func<bool> canShow, Action<PhoneUIManager> dial, string status = "预约明日到访")
    { _log = log; _name = name; _number = number; _canShow = canShow; _dial = dial; _status = status; }

    internal void Ensure(PhoneBookUIManager book)
    {
        Reset();
        if (_disposed || !_canShow()) return;
        var phone = PhoneUIManager.Instance;
        if (phone == null || phone.phoneBookUIManager == null || phone.phoneBookUIManager != book
            || book.contactElementPrefab == null || book.layoutGroup == null) return;
        var view = UnityEngine.Object.Instantiate(book.contactElementPrefab, book.layoutGroup.transform);
        _view = view; view.name = "StockMarket.IntelBrokerQuickDial." + _number;
        try
        {
            var contact = view.GetComponent<ContactElement>()
                ?? throw new InvalidOperationException("原版联系人视图缺少 ContactElement。");
            var buttons = new List<Button>();
            foreach (var component in view.GetComponentsInChildren<Button>(true))
                if (component is { } button) buttons.Add(button);
            if (buttons.Count != 1) throw new InvalidOperationException("联系人视图未提供唯一快捷按钮，已跳过。");
            if (contact.titleTMP == null || contact.statusTMP == null) throw new InvalidOperationException("联系人视图缺少原版姓名或状态文本。");
            contact.titleTMP.text = Localizer.Text(_name);
            contact.statusTMP.text = Localizer.Text($"{_number} · {_status}");
            contact.number = _number;
            _button = buttons[0];
            // 替换整个事件对象，清除序列化的 OnQuickDial 监听，不能仅 RemoveAllListeners。
            _event = new Button.ButtonClickedEvent(); _button.onClick = _event;
            var phonePointer = phone;
            _managed = () =>
            {
                if (_disposed || !_canShow()) return;
                var current = PhoneUIManager.Instance;
                if (current == null || current != phonePointer) return;
                _dial(current);
            };
            _native = new UnityAction(_managed);
            _event.AddListener(_native);
            // ContactElement 没有 Awake、OnEnable 或 Update；移除后仅保留原生纯 UI 子树。
            UnityEngine.Object.Destroy(contact);
            view.SetActive(true); view.transform.SetAsLastSibling();
            _log.Msg($"[股市预约电话探针] 已追加{_name}临时快捷联系人；原版电话簿存档未改写。");
        }
        catch { Reset(); throw; }
    }

    internal void Reset()
    {
        try { if (_event != null && _native != null) _event.RemoveListener(_native); }
        catch (Exception ex) { _log.Warning("[股市预约电话探针] 快捷监听解绑失败：" + ex.Message); }
        if (_view != null) { _view.SetActive(false); UnityEngine.Object.Destroy(_view); }
        _view = null; _button = null; _event = null; _native = null; _managed = null;
    }
    public void Dispose() { if (_disposed) return; Reset(); _disposed = true; }
}
