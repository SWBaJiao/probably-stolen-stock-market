using UnityEngine;

namespace MelonModTemplate.UI;

// 颜色统一使用不透明底色，避免游戏场景和原版贴图降低文字对比。
internal static class StockUiTheme
{
    internal static readonly Color Background = Rgb(16, 24, 38);
    internal static readonly Color TitleBackground = Rgb(36, 60, 86);
    internal static readonly Color Surface = Rgb(30, 43, 61);
    internal static readonly Color Button = Rgb(44, 64, 88);
    internal static readonly Color Selected = Rgb(23, 50, 72);
    internal static readonly Color Hover = Rgb(47, 66, 88);
    internal static readonly Color SelectedHover = Rgb(27, 61, 83);
    internal static readonly Color Pressed = Rgb(22, 48, 69);
    internal static readonly Color Disabled = Rgb(38, 51, 68);
    internal static readonly Color Text = Rgb(241, 245, 249);
    internal static readonly Color Muted = Rgb(193, 207, 222);
    internal static readonly Color Accent = Rgb(140, 228, 255);
    internal static readonly Color Rising = Rgb(255, 154, 148);
    internal static readonly Color Falling = Rgb(122, 227, 172);
    internal static readonly Color Neutral = Rgb(185, 197, 212);
    internal static readonly Color Grid = Rgb(66, 84, 107);
    internal static readonly Color ChartBackground = Rgb(17, 28, 43);
    internal static readonly Color TooltipBackground = Rgb(36, 54, 76);
    internal static readonly Color Warning = Rgb(255, 209, 128);
    internal static readonly Color Event = Rgb(215, 174, 255);
    private static Color Rgb(byte r, byte g, byte b) => new(r / 255f, g / 255f, b / 255f, 1f);
}
