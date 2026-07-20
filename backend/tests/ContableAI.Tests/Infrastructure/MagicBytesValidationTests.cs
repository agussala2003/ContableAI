using ContableAI.Infrastructure.Features.Transactions;
using FluentAssertions;

namespace ContableAI.Tests.Infrastructure;

/// <summary>
/// Tests de la validación de firma binaria (magic bytes) del UploadBankStatementHandler (fix M-2).
/// Impiden que un binario arbitrario renombrado a .pdf/.xlsx supere la validación de extensión.
/// </summary>
public class MagicBytesValidationTests
{
    private static byte[] Bytes(params int[] values) => values.Select(v => (byte)v).ToArray();

    [Fact]
    public void Pdf_WithValidSignature_IsAccepted()
    {
        var pdf = "%PDF-1.7\n..."u8.ToArray();
        UploadBankStatementHandler.HasValidSignature(".pdf", pdf).Should().BeTrue();
    }

    [Fact]
    public void Pdf_WithForeignSignature_IsRejected()
    {
        // Un ZIP/EXE renombrado a .pdf no debe pasar.
        var notPdf = Bytes(0x50, 0x4B, 0x03, 0x04, 0x00, 0x00);
        UploadBankStatementHandler.HasValidSignature(".pdf", notPdf).Should().BeFalse();
    }

    [Fact]
    public void Xlsx_WithZipSignature_IsAccepted()
    {
        var xlsx = Bytes(0x50, 0x4B, 0x03, 0x04, 0x14, 0x00);
        UploadBankStatementHandler.HasValidSignature(".xlsx", xlsx).Should().BeTrue();
    }

    [Fact]
    public void Xlsx_WithoutZipSignature_IsRejected()
    {
        var fake = "%PDF-1.7"u8.ToArray();
        UploadBankStatementHandler.HasValidSignature(".xlsx", fake).Should().BeFalse();
    }

    [Fact]
    public void Xls_WithOle2Signature_IsAccepted()
    {
        var xls = Bytes(0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1);
        UploadBankStatementHandler.HasValidSignature(".xls", xls).Should().BeTrue();
    }

    [Fact]
    public void Csv_HasNoSignature_IsAlwaysAccepted()
    {
        var csv = "Fecha,Descripcion,Importe\n2026-01-01,Pago,100"u8.ToArray();
        UploadBankStatementHandler.HasValidSignature(".csv", csv).Should().BeTrue();
    }

    [Fact]
    public void UnknownExtension_IsRejected()
    {
        UploadBankStatementHandler.HasValidSignature(".exe", Bytes(0x4D, 0x5A)).Should().BeFalse();
    }

    [Fact]
    public void EmptyContent_ForBinaryType_IsRejected()
    {
        UploadBankStatementHandler.HasValidSignature(".pdf", Array.Empty<byte>()).Should().BeFalse();
    }
}
