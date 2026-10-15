using System;
using System.Collections.Generic;
using System.IO;
using Il2Cpp;
using MelonLoader;
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
    private bool _warned;
    private bool _disposed;
    private bool _saving;

    public TerminalVisual(MelonLogger.Instance log) => _log = log;

    public void Apply(GameItem item)
    {
        if (_disposed || _saving) return;
        try
        {
            // 每个存档仅保留当前入口，补领后立即释放旧道具包装引用。
            if (!_originals.ContainsKey(item.Pointer)) _originals.Clear();
            bool savedCustom = item.spriteAtlasPath == AtlasKey;
            _originals.TryAdd(item.Pointer, new OriginalVisual(item, savedCustom ? "Items/items_tool" : item.spriteAtlasPath, savedCustom ? "postit" : item.spritePath));
            if (_sprite == null && !Load(item)) { Restore(item); return; }
            var cache = RenderHandler.atlasCache;
            if (cache == null) { Warn("原版图集缓存尚未初始化，将保留便笺图像。"); Restore(item); return; }
            if (!cache.ContainsKey(AtlasKey)) cache[AtlasKey] = new Il2CppSystem.Collections.Generic.Dictionary<string, Sprite>();
            if (cache[AtlasKey].ContainsKey(SpriteKey) && cache[AtlasKey][SpriteKey]?.Pointer != _sprite?.Pointer)
            { Warn("专用图集键已被其他资源占用，保留便笺图像。"); Restore(item); return; }
            cache[AtlasKey][SpriteKey] = _sprite!;
            if (item.spriteAtlasPath == AtlasKey && item.spritePath == SpriteKey) return;
            item.SetSprite(AtlasKey, SpriteKey);
        }
        catch (Exception ex) { Warn("专用图像暂不可用，保留原版便笺图像：" + ex.Message); Restore(item); }
    }

    private bool Load(GameItem item)
    {
        string path = Path.Combine(ModPaths.AssetsDirectory, "Textures", "Items", "stock_market_terminal.png");
        if (!File.Exists(path)) { Warn("找不到专用图像：" + path + "，保留原版便笺图像。"); return false; }
        bool savedCustom = item.spriteAtlasPath == AtlasKey;
        var native = RenderHandler.LoadFromAtlas(savedCustom ? "Items/items_tool" : item.spriteAtlasPath, savedCustom ? "postit" : item.spritePath);
        // 尺寸取原版便笺精灵，防止生成的大图改变背包占位与像素比例。
        int width = native != null ? Math.Clamp((int)native.rect.width, 8, 128) : 16;
        int height = native != null ? Math.Clamp((int)native.rect.height, 8, 128) : 16;
        Texture2D? source = null;
        RenderTexture? target = null;
        RenderTexture? previous = RenderTexture.active;
        try
        {
            source = new Texture2D(2, 2, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
            if (!ImageConversion.LoadImage(source, File.ReadAllBytes(path), true)) throw new InvalidDataException("PNG 解码失败。");
            target = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32);
            target.filterMode = FilterMode.Point;
            RenderTexture.active = target;
            GL.Clear(true, true, Color.clear);
            float scale = Math.Min((float)width / source.width, (float)height / source.height);
            float drawnWidth = Math.Max(1, (int)(source.width * scale));
            float drawnHeight = Math.Max(1, (int)(source.height * scale));
            GL.PushMatrix();
            try
            {
                GL.LoadPixelMatrix(0, width, height, 0);
                Graphics.DrawTexture(new Rect((width - drawnWidth) / 2, (height - drawnHeight) / 2, drawnWidth, drawnHeight), source);
            }
            finally { GL.PopMatrix(); }
            _texture = new Texture2D(width, height, TextureFormat.RGBA32, false) { name = SpriteKey, filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
            _texture.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);
            _texture.Apply(false, true);
            _sprite = Sprite.Create(_texture, new Rect(0, 0, width, height), new Vector2(.5f, .5f), native != null ? native.pixelsPerUnit : 100f);
            _sprite.name = SpriteKey;
            _warned = false;
            _log.Msg($"[股市图像] 已载入独立 PNG，并注册 {width}×{height} 原版尺寸精灵。");
            return true;
        }
        catch
        {
            if (_sprite != null) UnityEngine.Object.Destroy(_sprite);
            if (_texture != null) UnityEngine.Object.Destroy(_texture);
            _sprite = null; _texture = null; throw;
        }
        finally
        {
            RenderTexture.active = previous;
            if (target != null) RenderTexture.ReleaseTemporary(target);
            if (source != null) UnityEngine.Object.Destroy(source);
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
        catch { }
    }
    private void Warn(string message)
    {
        if (_warned) return;
        _warned = true; _log.Warning("[股市图像] " + message);
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
        catch { }
        if (_sprite != null) UnityEngine.Object.Destroy(_sprite);
        if (_texture != null) UnityEngine.Object.Destroy(_texture);
        _sprite = null; _texture = null;
    }
    private sealed record OriginalVisual(GameItem Item, string Atlas, string Sprite);
}
