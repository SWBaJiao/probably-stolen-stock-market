using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using System.Text.RegularExpressions;

namespace ProbablyStolenStockMarket.Localization;

public enum ModLanguage { Chinese, Japanese, English, Korean }

// 存档保留原始文案和稳定标识，仅在原生控件的显示边界转换语言。
public static class Localizer
{
    private sealed class Translation
    {
        public string en { get; set; } = "";
        public string ja { get; set; } = "";
        public string ko { get; set; } = "";
        public string Get(ModLanguage language) => language switch
        {
            ModLanguage.Japanese => ja,
            ModLanguage.Korean => ko,
            _ => en
        };
    }

    private static readonly Lazy<Dictionary<string, Translation>> Catalog = new(LoadCatalog);
    private static readonly Lazy<Regex> Matcher = new(() => new Regex(
        string.Join("|", Catalog.Value.Keys.OrderByDescending(k => k.Length).ThenBy(k => k, StringComparer.Ordinal).Select(Regex.Escape)),
        RegexOptions.Compiled | RegexOptions.CultureInvariant));
    private static readonly Dictionary<string, string> Cache = new(StringComparer.Ordinal);
    private static ModLanguage _language = ModLanguage.English;
    public static int Revision { get; private set; }
    public static ModLanguage Language
    {
        get => _language;
        set
        {
            if (!Enum.IsDefined(typeof(ModLanguage), value)) value = ModLanguage.English;
            if (_language == value) return;
            _language = value;
            Cache.Clear();
            Revision++;
        }
    }

    public static ModLanguage ResolveLanguage(string? code)
    {
        var normalized = (code ?? "").Trim().Replace('_', '-').ToLowerInvariant();
        var primary = normalized.Split('-')[0];
        return primary switch
        {
            "zh" => ModLanguage.Chinese,
            "ja" => ModLanguage.Japanese,
            "ko" => ModLanguage.Korean,
            _ => ModLanguage.English
        };
    }

    public static string Text(string source)
    {
        if (string.IsNullOrEmpty(source) || Language == ModLanguage.Chinese || !source.Any(IsChinese)) return source;
        if (Cache.TryGetValue(source, out var cached)) return cached;
        // 一次匹配后不再扫描译文，避免日文中的汉字再次被当作中文替换。
        var translated = Matcher.Value.Replace(source, match => Catalog.Value[match.Value].Get(Language));
        if (Language == ModLanguage.English)
        {
            // 中文日期把序号和单位分开，英文日期只保留一次 Day。
            translated = Regex.Replace(translated, @"\bDay\s*(\d+(?:\s*[～~–-]\s*\d+)?)\s*day\b", "Day $1");
            translated = Regex.Replace(translated, @"\b(\d+) day\b", m => m.Groups[1].Value + (m.Groups[1].Value == "1" ? " day" : " days"));
        }
        if (Cache.Count >= 4096) Cache.Clear();
        Cache[source] = translated;
        return translated;
    }

    private static bool IsChinese(char value) => value >= '\u3400' && value <= '\u9fff';

    private static Dictionary<string, Translation> LoadCatalog()
    {
        var result = new Dictionary<string, Translation>(StringComparer.Ordinal);
        var assembly = typeof(Localizer).Assembly;
        foreach (var name in new[] { "market", "game", "ui", "loans" })
        {
            using var stream = assembly.GetManifestResourceStream("ProbablyStolenStockMarket.Localization." + name + ".json")
                ?? throw new InvalidOperationException("缺少内置股市翻译资源：" + name);
            using var reader = new StreamReader(stream);
            var items = JsonConvert.DeserializeObject<Dictionary<string, Translation>>(reader.ReadToEnd())
                ?? throw new InvalidOperationException("股市翻译资源为空：" + name);
            foreach (var item in items)
            {
                if (string.IsNullOrWhiteSpace(item.Key) || item.Value == null
                    || string.IsNullOrWhiteSpace(item.Value.en) || string.IsNullOrWhiteSpace(item.Value.ja)
                    || string.IsNullOrWhiteSpace(item.Value.ko))
                    throw new InvalidOperationException("股市翻译条目不完整：" + name + "/" + item.Key);
                // 同一原文跨模块出现时采用同一份译文。
                if (!result.ContainsKey(item.Key)) result.Add(item.Key, item.Value);
            }
        }
        return result;
    }
}
