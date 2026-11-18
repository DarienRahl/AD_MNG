using AdMng.Core.Pki;

namespace AdMng.Presentation.Pki;

/// <summary>
/// One finding of <i>Przegląd bezpieczeństwa AD CS</i> (docs/dev/p-certificate-services.md 2.5, W6): the list columns
/// (severity, ESC, affected object, principals, title and condition) and the details (the condition with its values, the
/// attack path, what to do, the affected object, the principals with their account counts, the notes, the related CAs and
/// templates, the Certipy / Locksmith / MDI ids) and where <i>Otwórz szablon / urząd / konto</i> lead. Texts in the UI
/// language; directory values in memory only (rule 1) – <see cref="ToString"/> prints the code, severity, area and GUID.
/// </summary>
public sealed class PkiFindingRowViewModel
{
    public required PkiFinding Finding { get; init; }

    /// <summary>The catalogue entry of the code (ESC number, tool ids, rule group).</summary>
    public required PkiFindingDefinition Definition { get; init; }

    public PkiFindingSeverity Severity => Finding.Severity;

    public PkiFindingArea Area => Finding.Area;

    public string Code => Finding.Code;

    /// <summary>Certipy's ESC number ("ESC1").</summary>
    public string Esc => Definition.Esc;

    /// <summary>"Krytyczna".</summary>
    public required string SeverityText { get; init; }

    /// <summary>"Szablon certyfikatu".</summary>
    public required string AreaText { get; init; }

    /// <summary>The affected object: the template's or CA's name, the PKI object, the account (or the number of accounts).</summary>
    public required string ObjectText { get; init; }

    /// <summary>"Szablon certyfikatu: Użytkownik-VPN-Podatny" – the rows of one object are grouped under it.</summary>
    public required string GroupText { get; init; }

    /// <summary>The finding's title.</summary>
    public required string Title { get; init; }

    /// <summary>The condition with the finding's values (names instead of SIDs).</summary>
    public required string Message { get; init; }

    /// <summary>The attack path (why it matters).</summary>
    public required string Explanation { get; init; }

    /// <summary>What to do (P1 repairs nothing: the operator does it in the console).</summary>
    public required string Recommendation { get; init; }

    /// <summary>"Certipy: ESC1 · Locksmith: ESC1 · MDI: –".</summary>
    public required string IdsText { get; init; }

    /// <summary>The principals of the list column ("Użytkownicy domeny (152)"), "–" when none.</summary>
    public required string PrincipalsText { get; init; }

    /// <summary>One line per principal: name, class, account count and the right it holds.</summary>
    public required IReadOnlyList<string> PrincipalLines { get; init; }

    /// <summary>The notes (<i>szablon nie jest wystawiany</i>, <i>zakłada prawo Rejestruj urzędu</i> …).</summary>
    public required IReadOnlyList<string> NoteLines { get; init; }

    /// <summary>The related CAs, target templates and OID objects (one line each).</summary>
    public required IReadOnlyList<string> RelatedLines { get; init; }

    public bool HasPrincipals => PrincipalLines.Count > 0;

    public bool HasNotes => NoteLines.Count > 0;

    public bool HasRelated => RelatedLines.Count > 0;

    /// <summary>The template <i>Otwórz szablon</i> selects in <i>Szablony certyfikatów</i> (its cn), null when none.</summary>
    public string? TemplateCn { get; init; }

    /// <summary>The enterprise CA <i>Otwórz urząd</i> selects in the overview (its DN), null when none.</summary>
    public string? CaDn { get; init; }

    /// <summary>The account, CA computer or PKI object whose property sheet <i>Otwórz konto / komputer / obiekt</i> opens.</summary>
    public string? OpenDn { get; init; }

    /// <summary>"Otwórz konto", "Otwórz komputer" or "Otwórz obiekt".</summary>
    public required string OpenObjectText { get; init; }

    public bool CanOpenTemplate => TemplateCn is not null;

    public bool CanOpenCa => CaDn is not null;

    public bool CanOpenObject => OpenDn is not null;

    /// <summary>The finding's identity (code, object, arguments): the selection is kept across a new check by it.</summary>
    public required string Key { get; init; }

    /// <summary>The texts the search looks in (title, condition, object, principals, ESC and tool ids, area, severity, code).</summary>
    public required string SearchText { get; init; }

    /// <summary>The details as plain text (<i>Kopiuj</i>), with the headings in the UI language.</summary>
    public string DetailsText(PkiFindingHeadings headings)
    {
        ArgumentNullException.ThrowIfNull(headings);
        var lines = new List<string>
        {
            $"{SeverityText} · {Esc} · {AreaText} · {Title}",
            IdsText,
            string.Empty,
            headings.Object,
            ObjectText,
            string.Empty,
            headings.Condition,
            Message,
            string.Empty,
            headings.AttackPath,
            Explanation,
            string.Empty,
            headings.What,
            Recommendation,
        };
        Section(headings.Principals, PrincipalLines);
        Section(headings.Notes, NoteLines);
        Section(headings.Related, RelatedLines);
        return string.Join(Environment.NewLine, lines);

        void Section(string heading, IReadOnlyList<string> items)
        {
            if (items.Count > 0)
            {
                lines.Add(string.Empty);
                lines.Add(heading);
                lines.AddRange(items);
            }
        }
    }

    /// <summary>For diagnostics: the code, severity, area and GUID only (no directory values).</summary>
    public override string ToString() => Finding.ToString();
}

/// <summary>The headings of a finding's details in the UI language (<i>Kopiuj</i>, the HTML report).</summary>
public sealed class PkiFindingHeadings
{
    public required string Condition { get; init; }

    public required string AttackPath { get; init; }

    public required string What { get; init; }

    public required string Object { get; init; }

    public required string Principals { get; init; }

    public required string Notes { get; init; }

    public required string Related { get; init; }

    /// <summary>Localized headings only.</summary>
    public override string ToString() => nameof(PkiFindingHeadings);
}
