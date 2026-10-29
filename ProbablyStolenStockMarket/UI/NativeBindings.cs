using System;
using TMPro;
using UnityEngine.Events;
using UnityEngine.UI;

namespace ProbablyStolenStockMarket.UI;

// 保存同一个 Unity 委托，关闭窗口时解绑对应的监听器。
internal sealed class NativeButtonBinding : IDisposable
{
    private readonly Button _button;
    private readonly NativeStockMarketPanel _owner;
    private readonly string _command;
    private readonly Action _managed;
    private readonly UnityAction _native;
    private bool _disposed;

    public NativeButtonBinding(CustomUIElement element, NativeStockMarketPanel owner, string command)
    {
        _button = element.button ?? throw new InvalidOperationException("原生按钮预制件缺少 Button 组件。");
        _owner = owner;
        _command = command;
        _managed = Invoke;
        _native = Invoke;
        _button.onClick.AddListener(_native);
    }

    private void Invoke() { if (!_disposed) _owner.HandleCommand(_command); }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _button.onClick.RemoveListener(_native);
        GC.KeepAlive(_managed);
    }
}

internal sealed class NativeQuantityBinding : IDisposable
{
    private readonly TMP_InputField _input;
    private readonly NativeStockMarketPanel _owner;
    private readonly Action<string> _managed;
    private readonly UnityAction<string> _native;
    private bool _disposed;

    public NativeQuantityBinding(CustomUIElement element, NativeStockMarketPanel owner)
    {
        _input = element.input ?? throw new InvalidOperationException("原生输入框预制件缺少 TMP_InputField 组件。");
        _owner = owner;
        _managed = OnChanged;
        _native = OnChanged;
        _input.characterLimit = 9;
        _input.contentType = TMP_InputField.ContentType.IntegerNumber;
        _input.onValueChanged.AddListener(_native);
    }

    private void OnChanged(string value) { if (!_disposed) _owner.HandleQuantityChanged(value); }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _input.onValueChanged.RemoveListener(_native);
        GC.KeepAlive(_managed);
    }
}
