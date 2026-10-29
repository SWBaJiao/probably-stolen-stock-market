using UnityEngine;

namespace ProbablyStolenStockMarket;

// 保留适配层的日志调用方式，由原生游戏控制台接收日志。
public sealed class ModLogger
{
    public void Msg(string message) => Debug.Log("[" + ModInfo.Name + "] " + message);
    public void Warning(string message) => Debug.LogWarning("[" + ModInfo.Name + "] " + message);
    public void Error(string message) => Debug.LogError("[" + ModInfo.Name + "] " + message);
}
