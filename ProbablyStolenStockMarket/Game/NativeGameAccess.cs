using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace ProbablyStolenStockMarket.Game;

// 正式版保留为私有的游戏字段集中在此访问，避免依赖 IL2CPP 代理公开的内部成员。
internal static class NativeGameAccess
{
    private static readonly FieldInfo PhoneNumber = AccessTools.Field(typeof(PhoneUIManager), "currentNumber");
    private static readonly FieldInfo PhoneCalling = AccessTools.Field(typeof(PhoneUIManager), "isCalling");
    private static readonly FieldInfo PhoneTalking = AccessTools.Field(typeof(PhoneUIManager), "isTalking");
    private static readonly FieldInfo ConfirmationAccept = AccessTools.Field(typeof(GenericConfirmationUIManager), "onConfirm");
    private static readonly FieldInfo ConfirmationCancel = AccessTools.Field(typeof(GenericConfirmationUIManager), "onCancel");
    private static readonly FieldInfo Atlas = AccessTools.Field(typeof(RenderHandler), "atlasCache");
    private static readonly FieldInfo BrokerPortraits = AccessTools.Field(typeof(SpriteDict), "bmMaleEntries");
    private static readonly FieldInfo BrokerFemalePortraits = AccessTools.Field(typeof(SpriteDict), "bmFemaleEntries");
    private static readonly FieldInfo NegotiationFrame = AccessTools.Field(typeof(NegociationUIManager), "frame");
    private static readonly FieldInfo NegotiationSignature = AccessTools.Field(typeof(NegociationUIManager), "builtSignature");
    private static readonly FieldInfo NegotiationPrimary = AccessTools.Field(typeof(NegociationUIManager), "primaryButton");
    private static readonly FieldInfo ImageOverride = AccessTools.Field(typeof(Image), "m_OverrideSprite");
    private static readonly Stopwatch Clock = Stopwatch.StartNew();

    internal static long Milliseconds => Clock.ElapsedMilliseconds;
    internal static long GetPhoneNumber(PhoneUIManager phone) => (long)PhoneNumber.GetValue(phone);
    internal static void SetPhoneNumber(PhoneUIManager phone, long number) => PhoneNumber.SetValue(phone, number);
    internal static bool IsCalling(PhoneUIManager phone) => (bool)PhoneCalling.GetValue(phone);
    internal static bool IsTalking(PhoneUIManager phone) => (bool)PhoneTalking.GetValue(phone);
    internal static bool OwnsConfirmation(GenericConfirmationUIManager ui, Action? accept, Action? cancel)
        => ReferenceEquals(ConfirmationAccept.GetValue(ui), accept) && ReferenceEquals(ConfirmationCancel.GetValue(ui), cancel);
    internal static void ClearConfirmation(GenericConfirmationUIManager ui)
    {
        ConfirmationAccept.SetValue(ui, null);
        ConfirmationCancel.SetValue(ui, null);
    }
    internal static Dictionary<string, Dictionary<string, Sprite>> AtlasCache => (Dictionary<string, Dictionary<string, Sprite>>)Atlas.GetValue(null);
    internal static List<SpriteDict.SpriteEntry> GetBrokerPortraits(SpriteDict dict) => (List<SpriteDict.SpriteEntry>)BrokerPortraits.GetValue(dict);
    internal static NegotiationTabletFrame? GetNegotiationFrame(NegociationUIManager ui) => NegotiationFrame.GetValue(ui) as NegotiationTabletFrame;
    internal static void RebuildNegotiation(NegociationUIManager ui)
    {
        NegotiationSignature.SetValue(ui, null);
        ui.RefreshUI();
    }
    internal static void SetNegotiationPrimary(NegociationUIManager ui, TabletButton button) => NegotiationPrimary.SetValue(ui, button);
    internal static TabletButton? GetNegotiationPrimary(NegociationUIManager ui) => NegotiationPrimary.GetValue(ui) as TabletButton;
    internal static List<SpriteDict.SpriteEntry> GetBrokerFemalePortraits(SpriteDict dict) => (List<SpriteDict.SpriteEntry>)BrokerFemalePortraits.GetValue(dict);
    // overrideSprite 的公开 getter 会回退到基础精灵，保存真实字段才能在退出时完整恢复原状态。
    internal static Sprite? GetImageOverride(Image? image) => image == null ? null : ImageOverride.GetValue(image) as Sprite;
    internal static string HashPrefix(string text, int length)
    {
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(text));
        var hex = BitConverter.ToString(hash).Replace("-", "");
        return hex.Substring(0, length);
    }
}

// 道具和事件以同一个托管实例作为身份，不依赖地址，也不受类型自定义相等规则影响。
internal sealed class NativeReferenceComparer : IEqualityComparer<object>
{
    internal static readonly NativeReferenceComparer Instance = new();
    public new bool Equals(object? x, object? y) => ReferenceEquals(x, y);
    public int GetHashCode(object obj) => RuntimeHelpers.GetHashCode(obj);
}
