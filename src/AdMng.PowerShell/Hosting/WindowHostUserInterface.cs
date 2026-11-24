using System.Collections.ObjectModel;
using System.Globalization;
using System.Management.Automation;
using System.Management.Automation.Host;
using System.Security;
using AdMng.Core.PowerShell;

namespace AdMng.PowerShell.Hosting;

/// <summary>
/// The window's side of the host (docs/dev/r1-powershell-window.md 4.3): everything the engine writes becomes an item of the
/// active run's <see cref="OutputSink"/>, and every question (<c>Read-Host</c>, a missing mandatory parameter, a
/// confirmation) becomes a prompt item the pipeline thread waits on. Without an active run (an event writing late) output is
/// dropped. Nothing goes to a file or a console.
/// </summary>
internal sealed class WindowHostUserInterface : PSHostUserInterface
{
    private readonly WindowHostRawUserInterface _raw;
    private OutputSink? _sink;

    public WindowHostUserInterface(WindowHostRawUserInterface raw)
    {
        _raw = raw;
        _raw.Cleared = () => Sink?.Post(PowerShellClearItem.Instance);
    }

    public override PSHostRawUserInterface RawUI => _raw;

    /// <summary>No ANSI sequences: the window renders colours itself from the items.</summary>
    public override bool SupportsVirtualTerminal => false;

    /// <summary>Attaches the sink of the run that starts (null when it ended).</summary>
    public void Attach(OutputSink? sink) => Volatile.Write(ref _sink, sink);

    public override void Write(string value) => Text(PowerShellStream.Output, value);

    public override void Write(ConsoleColor foregroundColor, ConsoleColor backgroundColor, string value) =>
        Text(PowerShellStream.Output, value, foregroundColor, backgroundColor);

    public override void WriteLine() => Text(PowerShellStream.Output, "\n");

    public override void WriteLine(string value) => Text(PowerShellStream.Output, value + "\n");

    public override void WriteLine(ConsoleColor foregroundColor, ConsoleColor backgroundColor, string value) =>
        Text(PowerShellStream.Output, value + "\n", foregroundColor, backgroundColor);

    public override void WriteErrorLine(string value) => Text(PowerShellStream.Error, value + "\n");

    public override void WriteWarningLine(string message) => Text(PowerShellStream.Warning, message + "\n");

    public override void WriteVerboseLine(string message) => Text(PowerShellStream.Verbose, message + "\n");

    public override void WriteDebugLine(string message) => Text(PowerShellStream.Debug, message + "\n");

    public override void WriteProgress(long sourceId, ProgressRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        Sink?.Post(new PowerShellProgressItem(
            record.ActivityId,
            record.Activity ?? string.Empty,
            record.StatusDescription ?? string.Empty,
            record.PercentComplete,
            record.RecordType == ProgressRecordType.Completed));
    }

    public override string ReadLine()
    {
        var answer = Ask(new PowerShellPromptItem(PowerShellPromptKind.Text, null, null, null));
        return answer as string ?? throw new PipelineStoppedException();
    }

    public override SecureString ReadLineAsSecureString()
    {
        var answer = Ask(new PowerShellPromptItem(PowerShellPromptKind.Secret, null, null, null));
        return answer as SecureString ?? throw new PipelineStoppedException();
    }

    public override Dictionary<string, PSObject> Prompt(string caption, string message, Collection<FieldDescription> descriptions)
    {
        ArgumentNullException.ThrowIfNull(descriptions);
        var results = new Dictionary<string, PSObject>();
        var first = true;
        foreach (var field in descriptions)
        {
            var isSecret = string.Equals(field.ParameterTypeFullName, typeof(SecureString).FullName, StringComparison.Ordinal);
            var label = string.IsNullOrEmpty(field.Label) ? field.Name : field.Label;
            var item = new PowerShellPromptItem(
                isSecret ? PowerShellPromptKind.Secret : PowerShellPromptKind.Text,
                first ? caption : null,
                first ? message : null,
                label);
            first = false;
            var answer = Ask(item);
            results[field.Name] = answer switch
            {
                SecureString secret => PSObject.AsPSObject(secret),
                string text => PSObject.AsPSObject(ConvertAnswer(text, field.ParameterTypeFullName)),
                _ => throw new PipelineStoppedException(),
            };
        }

        return results;
    }

    public override int PromptForChoice(string caption, string message, Collection<ChoiceDescription> choices, int defaultChoice)
    {
        ArgumentNullException.ThrowIfNull(choices);
        var items = choices.Select(c => new PowerShellChoice(c.Label, c.HelpMessage ?? string.Empty)).ToList();
        var answer = Ask(new PowerShellPromptItem(PowerShellPromptKind.Choice, caption, message, null, items, defaultChoice));
        return answer is int index && index >= 0 && index < items.Count ? index : throw new PipelineStoppedException();
    }

    /// <summary>R1 creates no <c>PSCredential</c> (CLAUDE.md rule 2): a credential prompt is refused.</summary>
    public override PSCredential PromptForCredential(string caption, string message, string userName, string targetName) =>
        throw new PSNotSupportedException("The AD Manager PowerShell window does not ask for credentials: it works with the credentials of the connection it belongs to.");

    public override PSCredential PromptForCredential(
        string caption,
        string message,
        string userName,
        string targetName,
        PSCredentialTypes allowedCredentialTypes,
        PSCredentialUIOptions options) =>
        PromptForCredential(caption, message, userName, targetName);

    private OutputSink? Sink => Volatile.Read(ref _sink);

    /// <summary>
    /// The engine's own formatting decorates table headers, list labels and error text with ANSI escape sequences (the
    /// <c>$PSStyle</c> of PowerShell 7.2 and later) and leaves it to the host to remove them; the window shows text and colours
    /// them itself from the items, so none may reach it (R1 part 2: found by reading the formatted output of the read commands).
    /// </summary>
    private void Text(PowerShellStream stream, string text, ConsoleColor? foreground = null, ConsoleColor? background = null) =>
        Sink?.Post(new PowerShellTextItem(stream, GetOutputString(text ?? string.Empty, supportsVirtualTerminal: false), foreground, background));

    /// <summary>Posts a prompt and waits for its answer on the pipeline thread; a stop or a missing run cancels it.</summary>
    private object? Ask(PowerShellPromptItem item)
    {
        var sink = Sink ?? throw new PSInvalidOperationException("A question can be asked only while a command runs in the window.");
        sink.Post(item);
        var answer = item.Wait(sink.StopToken);
        if (answer is null)
        {
            item.Cancel();
        }

        return answer;
    }

    private static object ConvertAnswer(string text, string? typeName)
    {
        if (string.IsNullOrEmpty(typeName) || Type.GetType(typeName, throwOnError: false) is not { } type || type == typeof(string) || type == typeof(object))
        {
            return text;
        }

        if (type.IsArray && type.GetElementType() is { } elementType)
        {
            var parts = text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            var array = Array.CreateInstance(elementType, parts.Length);
            for (var i = 0; i < parts.Length; i++)
            {
                array.SetValue(LanguagePrimitives.ConvertTo(parts[i], elementType, CultureInfo.InvariantCulture), i);
            }

            return array;
        }

        return LanguagePrimitives.ConvertTo(text, type, CultureInfo.InvariantCulture);
    }
}
