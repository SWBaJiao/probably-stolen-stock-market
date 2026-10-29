using System;
using System.Collections.Generic;
using System.IO;
using ProbablyStolenStockMarket.Features;
using UnityEngine;

namespace ProbablyStolenStockMarket.Game;

// 名片保留原生手写纸条形状和阅读功能，仅为已认领物品绑定独立运行时图像。
internal sealed class BusinessCardVisual : IDisposable
{
    private readonly string _atlasKey;
    private readonly string _legacyAtlasKey;
    private readonly string _spriteKey;
    private readonly string _fileName;
    private readonly string _logLabel;
    private readonly ModLogger _log;
    private readonly Dictionary<object, OriginalVisual> _originals = new(NativeReferenceComparer.Instance);
    private readonly HashSet<object> _rendererLogged = new(NativeReferenceComparer.Instance);
    private readonly HashSet<object> _resolverLogged = new(NativeReferenceComparer.Instance);
    private static readonly HashSet<BusinessCardVisual> Active = new();
    private Texture2D? _texture;
    private Sprite? _sprite;
    private readonly HashSet<string> _warnedStages = new(StringComparer.Ordinal);
    private bool _loadAttempted;
    private bool _disposed;
    private bool _saving;

    public BusinessCardVisual(ModLogger log, string atlasKey, string legacyAtlasKey, string spriteKey, string fileName, string logLabel)
    {
        _log = log;
        _atlasKey = atlasKey;
        _legacyAtlasKey = legacyAtlasKey;
        _spriteKey = spriteKey;
        _fileName = fileName;
        _logLabel = logLabel;
        Active.Add(this);
    }

    public void Apply(GameItem item)
    {
        if (_disposed || _saving) return;
        string stage = "track native item";
        try
        {
            // 只追踪当前存档已认领的名片，不改动其他手写纸条。
            bool savedCustom = item.spriteAtlasPath == _atlasKey || item.spriteAtlasPath == _legacyAtlasKey;
            var element = item as GameItemElement;
            if (!_originals.ContainsKey(item))
            {
                var nativeFields = NativeFields(item, savedCustom);
                _originals.Add(item, new OriginalVisual(item, nativeFields.Atlas, nativeFields.Sprite,
                    NativeGameAccess.GetImageOverride(element?.background?.image), NativeGameAccess.GetImageOverride(element?.fakeBackground?.image)));
            }
            // 载入失败后本次 Mod 生命周期内不再分配纹理，避免自动补领轮询不断重试。
            if (_sprite == null && (_loadAttempted || !Load(item))) { Restore(item); return; }
            stage = "atlas registration";
            var cache = NativeGameAccess.AtlasCache;
            if (cache == null) { Warn("atlas cache", "原版图集缓存尚未初始化，将保留手写纸条图像。"); Restore(item); return; }
            if (!cache.ContainsKey(_atlasKey)) cache[_atlasKey] = new System.Collections.Generic.Dictionary<string, Sprite>();
            if (cache[_atlasKey].ContainsKey(_spriteKey) && cache[_atlasKey][_spriteKey] != _sprite)
            { Warn("atlas key", "专用图集键已被其他资源占用，保留手写纸条图像。"); Restore(item); return; }
            cache[_atlasKey][_spriteKey] = _sprite!;
            stage = "item sprite apply";
            if (item.spriteAtlasPath != _atlasKey || item.spritePath != _spriteKey) item.SetSprite(_atlasKey, _spriteKey);
            stage = "native renderer refresh";
            if (element == null || element.IsDestroyed()) throw new InvalidOperationException("专用道具的 GameItemElement 渲染对象未就绪。");
            // 原生 SetSprite 仅写路径和 spriteChanged 标记；以下入口才实际更新两个 Image。
            element.ValidatePartialSprite();
            element.ApplyAnimationFrame(_sprite!);
            var real = element.background?.image;
            var fake = element.fakeBackground?.image;
            if (real == null || fake == null) throw new InvalidOperationException("道具真实或拖拽图像节点未就绪。");
            // 原生刷新只更新 Image.sprite，已有 overrideSprite 仍可能遮住它。
            real.overrideSprite = _sprite!;
            fake.overrideSprite = _sprite!;
            stage = "renderer readback";
            bool realMatches = real.sprite == _sprite! && real.overrideSprite == _sprite;
            bool fakeMatches = fake.sprite == _sprite && fake.overrideSprite == _sprite;
            if (!realMatches || !fakeMatches) throw new InvalidOperationException($"实际 Image 读回不匹配：real={real.sprite}/{real.overrideSprite} fake={fake.sprite}/{fake.overrideSprite} expected={_sprite}");
            if (_rendererLogged.Add(item))
                _log.Msg($"[{_logLabel}] stage=renderer readback uniqueId={item.uniqueId} real={real.sprite} fake={fake.sprite} expected={_sprite}，真实与拖拽 Image 均已绑定；视觉显示仍待游戏截图确认。");
        }
        catch (Exception ex) { Warn(stage, "专用图像应用失败，保留原版手写纸条图像。", ex); Restore(item); }
    }

    internal static bool TryResolve(GameItemElement item, string name, out Sprite? sprite)
    {
        sprite = null;
        // 多类名片共享解析入口，但只有持有当前物品引用的实例才有权覆盖图像。
        foreach (var active in Active)
        {
            if (active._disposed || active._saving || active._sprite == null || !active._originals.ContainsKey(item)
                || item.identifier != "handnote" || item.spriteAtlasPath != active._atlasKey
                || item.spritePath != active._spriteKey || name != active._spriteKey) continue;
            sprite = active._sprite;
            if (active._resolverLogged.Add(item)) active._log.Msg($"[{active._logLabel}] stage=resolve override uniqueId={item.uniqueId}，原生解析返回专用精灵。");
            return true;
        }
        return false;
    }

    private static (string Atlas, string Sprite) NativeFields(GameItem item, bool savedCustom)
    {
        if (!savedCustom) return (item.spriteAtlasPath, item.spritePath);
        // 正常保存会恢复原字段；兼容异常遗留的自定义路径时从原生工厂取得准确图集。
        var reference = DirectoryMaster.Item("handnote", true) ?? throw new InvalidOperationException("原生名片参考纸张创建失败。");
        try { return (reference.spriteAtlasPath, reference.spritePath); }
        finally { if (reference.parentInventory == null) reference.Destroy(); }
    }

    private bool Load(GameItem item)
    {
        _loadAttempted = true;
        Texture2D? source = null;
        string stage = "read PNG";
        try
        {
            string path = ModPaths.AssetPath("Textures", "Items", _fileName);
            if (!ModPaths.AssetExists("Textures", "Items", _fileName)) throw new FileNotFoundException("找不到专用图像，保留原版手写纸条图像。", path);
            var png = ModPaths.ReadAssetBytes("Textures", "Items", _fileName);
            stage = "native atlas";
            bool savedCustom = item.spriteAtlasPath == _atlasKey || item.spriteAtlasPath == _legacyAtlasKey;
            var fields = _originals.TryGetValue(item, out var tracked)
                ? (Atlas: tracked.Atlas, Sprite: tracked.Sprite) : NativeFields(item, savedCustom);
            var native = RenderHandler.LoadFromAtlas(fields.Atlas, fields.Sprite);
            // 只改变运行时图像，目标尺寸沿用原版精灵，原图及背包占位均不在此修改。
            int width = native != null ? Math.Clamp((int)native.rect.width, 8, 128) : 16;
            int height = native != null ? Math.Clamp((int)native.rect.height, 8, 128) : 16;
            stage = "source texture allocation";
            source = new Texture2D(2, 2, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
            stage = "native PNG decode";
            // 当前游戏的该重载是原生调用；保持可读以便在 CPU 上完成缩放。
            if (!ImageConversion.LoadImage(source, png, false)) throw new InvalidDataException("PNG 解码失败。");
            stage = "native pixel readback";
            var pixels = source.GetPixels32();
            if (pixels == null || pixels.Length != checked(source.width * source.height)) throw new InvalidDataException("PNG 像素数量与尺寸不符。");
            stage = "CPU nearest sampling";
            // 在 CPU 上采样并上传像素，保持原有像素画风和透明边界。
            var packed = new uint[pixels.Length];
            for (int index = 0; index < packed.Length; index++)
            {
                var pixel = pixels[index];
                packed[index] = pixel.r | (uint)pixel.g << 8 | (uint)pixel.b << 16 | (uint)pixel.a << 24;
            }
            // Unity 像素数组以左下为原点，采样器保持行方向并居中留透明边。
            var sampled = NearestImageSampler.Fit(packed, source.width, source.height, width, height);
            var resized = new Color32[sampled.Pixels.Length];
            for (int index = 0; index < resized.Length; index++)
            {
                uint pixel = sampled.Pixels[index];
                resized[index] = new Color32 { r = (byte)pixel, g = (byte)(pixel >> 8), b = (byte)(pixel >> 16), a = (byte)(pixel >> 24) };
            }
            stage = "target texture allocation";
            _texture = new Texture2D(width, height, TextureFormat.RGBA32, false) { name = _spriteKey, filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
            stage = "native texture upload";
            _texture.SetPixels32(resized);
            // 原版 Image 的透明像素命中检测仍会读取纹理；不能在上传后丢弃 CPU 像素。
            _texture.Apply(false, false);
            stage = "native sprite creation";
            _sprite = Sprite.Create(_texture, new Rect(0, 0, width, height), new Vector2(.5f, .5f), native != null ? native.pixelsPerUnit : 100f);
            if (_sprite == null) throw new InvalidOperationException("原生精灵创建返回空值。");
            _sprite.name = _spriteKey;
            _log.Msg($"[{_logLabel}] stage=asset ready：PNG 解码与 CPU 采样完成，已创建 {width}×{height} 纹理和精灵；实际 Image 绑定尚待核对。");
            return true;
        }
        catch (Exception ex)
        {
            Warn(stage, "独立 PNG 载入失败，保留原版手写纸条图像；本次运行不再重试分配纹理。", ex);
            DestroyRuntimeResources();
            return false;
        }
        finally
        {
            if (source != null)
                try { UnityEngine.Object.Destroy(source); }
                catch (Exception ex) { Warn("source texture cleanup", "源纹理清理失败。", ex); }
        }
    }

    private void Restore(GameItem item)
    {
        if (!_originals.TryGetValue(item, out var original)) return;
        try
        {
            var element = item as GameItemElement;
            if (element != null && element.IsDestroyed()) return;
            // 先恢复可序列化字段；即使渲染节点已销毁，存档仍保留原版图集。
            item.spriteAtlasPath = original.Atlas;
            item.spritePath = original.Sprite;
            if (element?.background?.image != null) element.background.image.overrideSprite = original.RealOverride!;
            if (element?.fakeBackground?.image != null) element.fakeBackground.image.overrideSprite = original.FakeOverride!;
            element?.ValidatePartialSprite();
            item.Validate();
        }
        catch (Exception ex) { Warn("restore native atlas", "原版图集字段已尝试恢复，渲染刷新失败。", ex); }
    }
    private void Warn(string stage, string message, Exception? exception = null)
    {
        if (!_warnedStages.Add(stage)) return;
        _log.Warning($"[{_logLabel}] stage=" + stage + "：" + message + (exception == null ? "" : Environment.NewLine + exception));
    }
    public void RestoreForSave()
    {
        if (_disposed || _saving) return;
        _saving = true;
        foreach (var original in _originals.Values) Restore(original.Item);
    }
    public void ResumeAfterSave()
    {
        if (_disposed || !_saving) return;
        _saving = false;
        // 快照遍历，避免重应用时字典变化影响枚举。
        foreach (var original in new List<OriginalVisual>(_originals.Values)) Apply(original.Item);
    }
    public void ResetTracking()
    {
        // 场景已经卸载时不触碰旧原生对象，只清理包装引用。
        _originals.Clear(); _rendererLogged.Clear(); _resolverLogged.Clear(); _saving = false;
    }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        Active.Remove(this);
        foreach (var original in _originals.Values) Restore(original.Item);
        _originals.Clear();
        try
        {
            var cache = NativeGameAccess.AtlasCache;
            if (cache != null && cache.ContainsKey(_atlasKey) && cache[_atlasKey].ContainsKey(_spriteKey) && cache[_atlasKey][_spriteKey] == _sprite)
            { cache[_atlasKey].Remove(_spriteKey); if (cache[_atlasKey].Count == 0) cache.Remove(_atlasKey); }
        }
        catch (Exception ex) { Warn("atlas cache cleanup", "专用图集缓存清理失败。", ex); }
        DestroyRuntimeResources();
    }
    private void DestroyRuntimeResources()
    {
        try { if (_sprite != null) UnityEngine.Object.Destroy(_sprite); }
        catch (Exception ex) { Warn("sprite cleanup", "精灵清理失败。", ex); }
        try { if (_texture != null) UnityEngine.Object.Destroy(_texture); }
        catch (Exception ex) { Warn("target texture cleanup", "目标纹理清理失败。", ex); }
        _sprite = null; _texture = null;
    }
    private sealed record OriginalVisual(GameItem Item, string Atlas, string Sprite, Sprite? RealOverride, Sprite? FakeOverride);
}
