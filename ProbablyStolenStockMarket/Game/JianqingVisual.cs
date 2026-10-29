using System;
using System.Collections.Generic;
using System.IO;
using ProbablyStolenStockMarket.Features;
using UnityEngine;

namespace ProbablyStolenStockMarket.Game;

// 新人物沿用原版角色精灵的尺寸和锚点，图像只注册到运行时字典。
internal abstract class BrokerCharacterVisual : IDisposable
{
    private readonly ModLogger _log;
    private readonly string _key, _file, _name;
    private readonly List<System.Collections.Generic.Dictionary<string, Sprite>> _registrations = new();
    private Texture2D? _texture;
    private Sprite? _sprite;
    private bool _disposed, _failed;
    private string _lastWarning = "";

    protected BrokerCharacterVisual(ModLogger log, string key, string file, string name)
    { _log = log; _key = key; _file = file; _name = name; }

    internal bool EnsureRegistered()
    {
        if (_disposed || _failed) return false;
        try
        {
            var dict = SpriteDict.Instance;
            var sprites = dict?.spriteDictionary;
            if (dict == null || sprites == null) return false;
            if (_sprite == null)
            {
                Sprite? reference = null;
                if (NativeGameAccess.GetBrokerPortraits(dict) != null)
                    foreach (var entry in NativeGameAccess.GetBrokerPortraits(dict))
                        if (entry?.sprite != null) { reference = entry.sprite; break; }
                if (reference == null) return false;
                Load(reference);
            }
            if (sprites.ContainsKey(_key) && sprites[_key] != _sprite!)
                throw new InvalidOperationException("人物精灵键已被其它资源占用，未覆盖原值。");
            if (!sprites.ContainsKey(_key))
            {
                sprites.Add(_key, _sprite!);
                if (!_registrations.Exists(d => d == sprites)) _registrations.Add(sprites);
                _log.Msg($"[股市人物图像] {_name} 已注册独立精灵 {_key}；实际人物显示待游戏验收。");
            }
            if (dict.GetSprite(_key) != _sprite!)
                throw new InvalidOperationException("原生人物精灵解析读回不一致。");
            _lastWarning = "";
            return true;
        }
        catch (Exception ex)
        {
            var message = ex.GetType().Name + ": " + ex.Message;
            if (_lastWarning != message) _log.Warning($"[股市人物图像] {_name} 暂用原版图像：{message}");
            _lastWarning = message;
            return false;
        }
    }

    private void Load(Sprite reference)
    {
        Texture2D? source = null;
        try
        {
            var path = ModPaths.AssetPath("Textures", "Characters", _file);
            var width = Math.Clamp((int)reference.rect.width, 16, 2048);
            var height = Math.Clamp((int)reference.rect.height, 32, 4096);
            source = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (!ImageConversion.LoadImage(source, ModPaths.ReadAssetBytes("Textures", "Characters", _file), false))
                throw new InvalidDataException("人物 PNG 解码失败。");
            if ((long)source.width * source.height > 16000000)
                throw new InvalidDataException("人物图像像素数超出限制。");
            var pixels = source.GetPixels32();
            var packed = new uint[pixels.Length];
            for (var i = 0; i < pixels.Length; i++)
                packed[i] = pixels[i].r | (uint)pixels[i].g << 8 | (uint)pixels[i].b << 16 | (uint)pixels[i].a << 24;
            // 最近邻保持像素边缘与透明度，不使用被当前游戏裁剪的 Graphics 路径。
            var sampled = NearestImageSampler.Fit(packed, source.width, source.height, width, height);
            var resized = new Color32[sampled.Pixels.Length];
            for (var i = 0; i < resized.Length; i++)
            {
                var p = sampled.Pixels[i];
                resized[i] = new Color32 { r = (byte)p, g = (byte)(p >> 8), b = (byte)(p >> 16), a = (byte)(p >> 24) };
            }
            _texture = new Texture2D(width, height, TextureFormat.RGBA32, false)
            { name = _key, filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
            _texture.SetPixels32(resized); _texture.Apply(false, false);
            var pivot = new Vector2(reference.pivot.x / reference.rect.width, reference.pivot.y / reference.rect.height);
            _sprite = Sprite.Create(_texture, new Rect(0, 0, width, height), pivot, reference.pixelsPerUnit);
            if (_sprite == null) throw new InvalidOperationException("创建人物精灵失败。");
            _sprite.name = _key;
            _log.Msg($"[股市人物图像] {_name} PNG {source.width}×{source.height} 已适配原生 {width}×{height}；未修改游戏资源。");
        }
        catch
        {
            _failed = true;
            if (_sprite != null) UnityEngine.Object.Destroy(_sprite);
            if (_texture != null) UnityEngine.Object.Destroy(_texture);
            _sprite = null; _texture = null;
            throw;
        }
        finally { if (source != null) UnityEngine.Object.Destroy(source); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var dict in _registrations)
            try { if (dict.ContainsKey(_key) && dict[_key] == _sprite) dict.Remove(_key); }
            catch (Exception ex) { _log.Warning("[股市人物图像] 解绑人物精灵失败：" + ex.Message); }
        _registrations.Clear();
        if (_sprite != null) UnityEngine.Object.Destroy(_sprite);
        if (_texture != null) UnityEngine.Object.Destroy(_texture);
        _sprite = null; _texture = null;
    }
}

internal sealed class JianqingVisual : BrokerCharacterVisual
{
    internal const string SpriteKey = "probably_stolen_stock.jianqing";
    internal JianqingVisual(ModLogger log) : base(log, SpriteKey, "jianqing.png", "剑清酱") { }
}

internal sealed class WenzhouVisual : BrokerCharacterVisual
{
    internal const string SpriteKey = "probably_stolen_stock.wenzhou";
    internal WenzhouVisual(ModLogger log) : base(log, SpriteKey, "wenzhou.png", "闻舟") { }
}
