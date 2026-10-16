namespace AdMng.Watchdog.Service.Hosting;

/// <summary>Ends the process with an exit code (tests replace it). A non-zero exit of the Windows service triggers the
/// SCM recovery actions (restart), which a clean stop never does.</summary>
public interface IProcessTerminator
{
    void Exit(int exitCode);
}
