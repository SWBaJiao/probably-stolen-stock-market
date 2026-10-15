using System;
using System.Collections.Generic;
using Il2Cpp;
using Il2CppInterop.Runtime;
using MelonLoader;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace MelonModTemplate.Game;

// 临时电话簿条目只复制原版联系人视图，不登记联系人，也不调用查询存档的 OnInit。
internal sealed class IntelPhoneQuickDial : IDisposable
{
    private readonly MelonLogger.Instance _log;
    private readonly Func<bool> _canShow;
    private readonly Action<PhoneUIManager> _dial;
    private GameObject? _view;
    private Button? _button;
    private Button.ButtonClickedEvent? _event;
    private Action? _managed;
    private UnityAction? _native;
    private bool _disposed;

    internal IntelPhoneQuickDial(MelonLogger.Instance log, Func<bool> canShow, Action<PhoneUIManager> dial)
    { _log = log; _canShow = canShow; _dial = dial; }

    internal void Ensure(PhoneBookUIManager book)
    {
        Reset();
        if (_disposed || !_canShow()) return;
        var phone = PhoneUIManager.Instance;
        if (phone == null || phone.phoneBookUIManager == null || phone.phoneBookUIManager.Pointer != book.Pointer
            || book.contactElementPrefab == null || book.layoutGroup == null) return;
        var view = UnityEngine.Object.Instantiate(book.contactElementPrefab, book.layoutGroup.transform).Cast<GameObject>();
        _view = view; view.name = "StockMarket.IntelBrokerQuickDial";
        try
        {
            var contact = view.GetComponent(Il2CppType.Of<ContactElement>())?.TryCast<ContactElement>()
                ?? throw new InvalidOperationException("原版联系人视图缺少 ContactElement。");
            var buttons = new List<Button>();
            foreach (var component in view.GetComponentsInChildren(Il2CppType.Of<Button>(), true))
                if (component?.TryCast<Button>() is { } button) buttons.Add(button);
            if (buttons.Count != 1) throw new InvalidOperationException("联系人视图未提供唯一快捷按钮，已跳过。");
            if (contact.titleTMP == null || contact.statusTMP == null) throw new InvalidOperationException("联系人视图缺少原版姓名或状态文本。");
            contact.titleTMP.text = "闻舟";
            contact.statusTMP.text = $"{IntelPhoneAdapter.PhoneNumber} · 预约明日到访";
            contact.number = IntelPhoneAdapter.PhoneNumber;
            _button = buttons[0];
            // 替换整个事件对象，清除序列化的 OnQuickDial 监听，不能仅 RemoveAllListeners。
            _event = new Button.ButtonClickedEvent(); _button.onClick = _event;
            var phonePointer = phone.Pointer;
            _managed = () =>
            {
                if (_disposed || !_canShow()) return;
                var current = PhoneUIManager.Instance;
                if (current == null || current.Pointer != phonePointer) return;
                _dial(current);
            };
            _native = DelegateSupport.ConvertDelegate<UnityAction>(_managed) ?? throw new InvalidOperationException("快捷拨号委托转换失败。");
            _event.AddListener(_native);
            // ContactElement 没有 Awake、OnEnable 或 Update；移除后仅保留原生纯 UI 子树。
            UnityEngine.Object.Destroy(contact);
            view.SetActive(true); view.transform.SetAsLastSibling();
            _log.Msg("[股市预约电话探针] 已追加闻舟临时快捷联系人；原版电话簿存档未改写。");
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
