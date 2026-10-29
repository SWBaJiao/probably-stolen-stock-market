using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace ProbablyStolenStockMarket.Game;

// 原生 Validate 会重新解析精灵；仅为已登记的股市终端和联系名片返回专用图像。
internal static class IconRenderPatches
{
    public static void Install(HarmonyLib.Harmony harmony, ModLogger log)
    {
        try
        {
            var method = AccessTools.Method(typeof(GameItemElement), "ResolveSpriteByName", new[] { typeof(string) })
                ?? throw new MissingMethodException(typeof(GameItemElement).FullName, "ResolveSpriteByName");
            harmony.Patch(method, prefix: new HarmonyMethod(typeof(IconRenderPatches), nameof(ResolvePrefix)));
            log.Msg("[股市图像] stage=render hook：已安装专用道具 ResolveSpriteByName 钩子；实际 Image 绑定另行核对。");
        }
        catch (Exception ex) { log.Warning("[股市图像] stage=render hook：渲染钩子不可用，将使用显式 Image 刷新。" + Environment.NewLine + ex); }
    }
    public static void Uninstall(HarmonyLib.Harmony? harmony)
    {
        if (harmony == null) return;
        var method = AccessTools.Method(typeof(GameItemElement), "ResolveSpriteByName", new[] { typeof(string) });
        if (method != null) harmony.Unpatch(method, HarmonyPatchType.Prefix, harmony.Id);
    }
    private static bool ResolvePrefix(GameItemElement __instance, string __0, ref Sprite __result)
    {
        if (!TerminalVisual.TryResolve(__instance, __0, out var sprite)
            && !BusinessCardVisual.TryResolve(__instance, __0, out sprite)) return true;
        __result = sprite!; return false;
    }
}
