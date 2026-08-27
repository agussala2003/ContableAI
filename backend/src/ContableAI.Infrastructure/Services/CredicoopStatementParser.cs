using System.Text.RegularExpressions;

using ContableAI.Domain.Constants;

namespace ContableAI.Infrastructure.Services;

/// <summary>
/// Parser de extractos Banco Credicoop. Sobre el motor tabular agrega: descarte de las tablas
/// anexas (las que no traen columna SALDO no son la principal), limpieza del footer institucional
/// que Credicoop imprime dentro de las descripciones, y extracción del número de operación inicial
/// como id externo.
/// </summary>
internal sealed class CredicoopStatementParser : TabularStatementParser
{
    public override string Bank => BankCodes.Credicoop;

    protected override bool IsAccessoryTableHeader(bool hasSaldoHeader) => !hasSaldoHeader;

    protected override (string desc, string? extId) ExtractExternalId(string desc) => ExtractCredicoopExternalId(desc);

    protected override (string desc, string? extId) MergeContinuationExternalId(string newDesc, string? currentExtId)
        => ExtractCredicoopExternalId(newDesc, currentExtId);

    // ── Lógica específica Credicoop (movida verbatim desde PdfBankParser) ───────

    private static string CleanCredicoopDescription(string desc)
    {
        if (string.IsNullOrWhiteSpace(desc)) return desc;

        var clean = Regex.Replace(desc, @"(?is)Banco\s+Credicoop\s+Cooperativo\s+Limitado.*?www\.bancocredicoop\.coop", " ");
        clean = Regex.Replace(clean, @"(?is)Ctro\.\s+de\s+Contacto\s+Telefonico:.*?www\.bancocredicoop\.coop", " ");
        clean = Regex.Replace(clean, @"\s+", " ").Trim(' ', '-', '−');
        return clean;
    }

    private static (string cleanDesc, string? extId) ExtractCredicoopExternalId(string rawDesc, string? currentExtId = null)
    {
        var clean = CleanCredicoopDescription(rawDesc);
        if (!string.IsNullOrWhiteSpace(currentExtId))
            return (clean, currentExtId);

        var match = Regex.Match(clean, @"^0*(\d{5,})\s+(.+)$", RegexOptions.CultureInvariant);
        if (!match.Success)
            return (clean, null);

        var extId = match.Groups[1].Value;
        var desc = match.Groups[2].Value.Trim();
        return (desc, extId);
    }
}
