namespace AdMng.Watchdog.Contracts.Events;

public enum DetectionSource
{
    IncrementalPoll = 1, Reconciliation = 2, DeletedObjects = 3, LockoutMonitor = 4, ExpiryScan = 5,
    BurstDetector = 6, SecurityLog = 7, Health = 8, Test = 9,
}
