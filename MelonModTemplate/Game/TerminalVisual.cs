using System;
using System.Collections.Generic;
using System.IO;
using Il2Cpp;
using MelonLoader;
using MelonModTemplate.Features;
using UnityEngine;

namespace MelonModTemplate.Game;

// 仅给专用便笺注册运行时图集，不依赖受限版本已禁用的原生 ModLoader。
internal sealed class TerminalVisual : IDisposable
{
    private const string AtlasKey = "MelonModTemplate/stock_market";
    private const string SpriteKey = "stock_market_terminal";
    private readonly MelonLogger.Instance _log;
    private readonly Dictionary<IntPtr, OriginalVisual> _originals = new();
    private Texture2D? _texture;
    private Sprite? _sprite;
    private readonly HashSet<string> _warnedStages = new(StringComparer.Ordinal);
    private bool _loadAttempted;
    private bool _disposed;
    private bool _saving;

    public TerminalVisual(MelonLogger.Instance log) => _log = log;

    public void Apply(GameItem item)
    {
        if (_disposed || _saving) return;
        string stage = "track native item";
        try
        {
            // 每个存档仅保留当前入口，补领后立即释放旧道具包装引用。
            if (!_originals.ContainsKey(item.Pointer)) _originals.Clear();
            bool savedCustom = item.spriteAtlasPath == AtlasKey;
            _originals.TryAdd(item.Pointer, new OriginalVisual(item, savedCustom ? "Items/items_tool" : item.spriteAtlasPath, savedCustom ? "postit" : item.spritePath));
            // 载入失败后本次 Mod 生命周期内不再分配纹理，避免自动补领轮询不断重试。
            if (_sprite == null && (_loadAttempted || !Load(item))) { Restore(item); return; }
            stage = "atlas registration";
            var cache = RenderHandler.atlasCache;
            if (cache == null) { Warn("atlas cache", "原版图集缓存尚未初始化，将保留便笺图像。"); Restore(item); return; }
            if (!cache.ContainsKey(AtlasKey)) cache[AtlasKey] = new Il2CppSystem.Collections.Generic.Dictionary<string, Sprite>();
            if (cache[AtlasKey].ContainsKey(SpriteKey) && cache[AtlasKey][SpriteKey]?.Pointer != _sprite?.Pointer)
            { Warn("atlas key", "专用图集键已被其他资源占用，保留便笺图像。"); Restore(item); return; }
            cache[AtlasKey][SpriteKey] = _sprite!;
            if (item.spriteAtlasPath == AtlasKey && item.spritePath == SpriteKey) return;
            stage = "item sprite apply";
            item.SetSprite(AtlasKey, SpriteKey);
        }
        catch (Exception ex) { Warn(stage, "专用图像应用失败，保留原版便笺图像。", ex); Restore(item); }
    }

    private bool Load(GameItem item)
    {
        _loadAttempted = true;
        Texture2D? source = null;
        string stage = "read PNG";
        try
        {
            string path = Path.Combine(ModPaths.AssetsDirectory, "Textures", "Items", "stock_market_terminal.png");
            if (!File.Exists(path)) throw new FileNotFoundException("找不到专用图像，保留原版便笺图像。", path);
            var png = File.ReadAllBytes(path);
            stage = "native atlas";
            bool savedCustom = item.spriteAtlasPath == AtlasKey;
            var native = RenderHandler.LoadFromAtlas(savedCustom ? "Items/items_tool" : item.spriteAtlasPath, savedCustom ? "postit" : item.spritePath);
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
            // 当前代理的 Graphics.DrawTextureImpl 直接抛出 Method unstripping failed，不能用于缩略图绘制。
            // 使用已核实的原生像素读取/上传入口，在 CPU 采样，整个流程不依赖 GL 或 RenderTexture。
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
            _texture = new Texture2D(width, height, TextureFormat.RGBA32, false) { name = SpriteKey, filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
            stage = "native texture upload";
            _texture.SetPixels32(resized);
            _texture.Apply(false, true);
            stage = "native sprite creation";
            _sprite = Sprite.Create(_texture, new Rect(0, 0, width, height), new Vector2(.5f, .5f), native != null ? native.pixelsPerUnit : 100f);
            if (_sprite == null) throw new InvalidOperationException("原生精灵创建返回空值。");
            _sprite.name = SpriteKey;
            _log.Msg($"[股市图像] 原生 PNG 解码与 CPU 像素采样完成，已注册 {width}×{height} 原版尺寸精灵。");
            return true;
        }
        catch (Exception ex)
        {
            Warn(stage, "独立 PNG 载入失败，保留原版便笺图像；本次运行不再重试分配纹理。", ex);
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
        if (!_originals.TryGetValue(item.Pointer, out var original)) return;
        try
        {
            var element = item.TryCast<GameItemElement>();
            if (element != null && element.IsDestroyed()) return;
            // 先恢复可序列化字段；即使渲染节点已销毁，存档仍保留原版图集。
            item.spriteAtlasPath = original.Atlas;
            item.spritePath = original.Sprite;
            item.Validate();
        }
        catch (Exception ex) { Warn("restore native atlas", "原版图集字段已尝试恢复，渲染刷新失败。", ex); }
    }
    private void Warn(string stage, string message, Exception? exception = null)
    {
        if (!_warnedStages.Add(stage)) return;
        _log.Warning("[股市图像] stage=" + stage + "：" + message + (exception == null ? "" : Environment.NewLine + exception));
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
        _originals.Clear(); _saving = false;
    }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        foreach (var original in _originals.Values) Restore(original.Item);
        _originals.Clear();
        try
        {
            var cache = RenderHandler.atlasCache;
            if (cache != null && cache.ContainsKey(AtlasKey) && cache[AtlasKey].ContainsKey(SpriteKey) && cache[AtlasKey][SpriteKey]?.Pointer == _sprite?.Pointer)
            { cache[AtlasKey].Remove(SpriteKey); if (cache[AtlasKey].Count == 0) cache.Remove(AtlasKey); }
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
    private sealed record OriginalVisual(GameItem Item, string Atlas, string Sprite);
}
