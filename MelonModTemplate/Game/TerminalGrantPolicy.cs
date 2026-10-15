namespace MelonModTemplate.Game;

internal enum TerminalGrantAction { WaitForInventory, UseExisting, KeepGranted, Create }

// 发放决策不依赖 IL2CPP 查询缓存；本次会话确认过发放后，查询为空也不能再次造物。
internal sealed class TerminalGrantPolicy
{
    public bool AttemptInProgress { get; private set; }
    public bool GrantedThisSession { get; private set; }

    public TerminalGrantAction Decide(bool inventoryReady, bool existingFound, bool persistedGrant, bool explicitRecovery)
    {
        if (AttemptInProgress || !inventoryReady) return TerminalGrantAction.WaitForInventory;
        if (existingFound) { GrantedThisSession = true; return TerminalGrantAction.UseExisting; }
        if (GrantedThisSession || persistedGrant && !explicitRecovery) return TerminalGrantAction.KeepGranted;
        return TerminalGrantAction.Create;
    }
    public bool BeginAttempt()
    {
        if (AttemptInProgress || GrantedThisSession) return false;
        AttemptInProgress = true; return true;
    }
    public void FinishAttempt(bool placementConfirmed, bool outcomeUncertain = false)
    {
        if (placementConfirmed || outcomeUncertain) GrantedThisSession = true;
        AttemptInProgress = false;
    }
}
