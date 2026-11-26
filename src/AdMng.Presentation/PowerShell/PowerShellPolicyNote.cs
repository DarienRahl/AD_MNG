using AdMng.Presentation.Feedback;
using AdMng.Presentation.Localization;

namespace AdMng.Presentation.PowerShell;

/// <summary>The sentence the window shows about what the policies record, and its value-free copy for the log.</summary>
/// <param name="Message">The text on screen; it names the workstation's directory for transcripts, never a command or a result.</param>
/// <param name="Log">The key and a bit mask of what is on (1 transcription, 2 script block logging, 4 module logging) - never a path.</param>
public sealed record PowerShellPolicyNote(string Message, PageStatusLog Log);

/// <summary>
/// The lasting note of the window (track R, decision D7): what the policies of this workstation record, where, and that
/// AD Manager does not get around them. Plain sentences, in the wording of the status line (docs/dev/v-interface-renewal.md 4.5).
/// </summary>
public static class PowerShellPolicyNotes
{
    /// <summary>Transcription.</summary>
    public const long TranscriptionBit = 1;

    /// <summary>Script block logging.</summary>
    public const long ScriptBlockLoggingBit = 2;

    /// <summary>Module logging.</summary>
    public const long ModuleLoggingBit = 4;

    /// <summary>
    /// The note for <paramref name="policies"/>: one sentence for every policy that records, then the sentence that the
    /// program changes none of them. Null when nothing records - the execution policy and a lockdown are no recording, they
    /// are shown where they matter (the language mode switch).
    /// </summary>
    public static PowerShellPolicyNote? For(PowerShellPolicies policies, ILocalizer localizer)
    {
        ArgumentNullException.ThrowIfNull(policies);
        ArgumentNullException.ThrowIfNull(localizer);
        if (!policies.IsRecording)
        {
            return null;
        }

        var sentences = new List<string>();
        long mask = 0;
        if (policies.Transcription.Enabled)
        {
            mask |= TranscriptionBit;
            var transcription = policies.Transcription;
            var origin = OriginText(localizer, transcription.Origin);
            sentences.Add(transcription.OutputDirectory is { } directory
                ? localizer.Format(L.PowerShellPolicy.Transcription, origin, directory)
                : localizer.Format(L.PowerShellPolicy.TranscriptionDefault, origin));
        }

        if (policies.ScriptBlockLogging.Enabled)
        {
            mask |= ScriptBlockLoggingBit;
            sentences.Add(localizer.Format(L.PowerShellPolicy.ScriptBlockLogging, OriginText(localizer, policies.ScriptBlockLogging.Origin)));
        }

        if (policies.ModuleLogging.Enabled)
        {
            mask |= ModuleLoggingBit;
            var modules = policies.ModuleLogging.ModuleNames.Count == 0
                ? localizer[L.PowerShellPolicy.NoModules]
                : string.Join(", ", policies.ModuleLogging.ModuleNames);
            sentences.Add(localizer.Format(L.PowerShellPolicy.ModuleLogging, OriginText(localizer, policies.ModuleLogging.Origin), modules));
        }

        sentences.Add(localizer[L.PowerShellPolicy.Closing]);
        return new PowerShellPolicyNote(string.Join(' ', sentences), new PageStatusLog(L.PowerShellPolicy.Note, mask));
    }

    /// <summary>"Computer policy", "user policy of Windows PowerShell"… - which hive and which source a setting came from.</summary>
    public static string OriginText(ILocalizer localizer, PowerShellPolicyOrigin? origin)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        if (origin is not { } found)
        {
            return localizer[L.PowerShellPolicy.OriginUnknown];
        }

        var key = (found.Store, found.Scope) switch
        {
            (PowerShellPolicyStore.PowerShellCore, PowerShellPolicyScope.Computer) => L.PowerShellPolicy.OriginCoreComputer,
            (PowerShellPolicyStore.PowerShellCore, _) => L.PowerShellPolicy.OriginCoreUser,
            (PowerShellPolicyStore.WindowsPowerShell, PowerShellPolicyScope.Computer) => L.PowerShellPolicy.OriginWindowsComputer,
            (PowerShellPolicyStore.WindowsPowerShell, _) => L.PowerShellPolicy.OriginWindowsUser,
            (_, PowerShellPolicyScope.Computer) => L.PowerShellPolicy.OriginFileComputer,
            _ => L.PowerShellPolicy.OriginFileUser,
        };
        return localizer[key];
    }
}
