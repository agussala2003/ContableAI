using ContableAI.Infrastructure.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Net;
using System.Net.Mail;

namespace ContableAI.Infrastructure.Services;

public interface IEmailService
{
    Task SendPasswordResetEmailAsync(string toEmail, string displayName, string resetUrl, CancellationToken ct = default);
}

/// <summary>
/// Implementación SMTP estándar de .NET.
/// Se configura a través de appsettings: "Smtp:Host", "Smtp:Port", "Smtp:User", "Smtp:Password", "Smtp:FromAddress".
/// Para desarrollo, si Host está vacío, imprime el link en consola en lugar de enviar.
/// </summary>
public class SmtpEmailService : IEmailService
{
    private readonly SmtpOptions _smtpOptions;
    private readonly ILogger<SmtpEmailService> _log;

    public SmtpEmailService(IOptions<SmtpOptions> smtpOptions, ILogger<SmtpEmailService> log)
    {
        _smtpOptions = smtpOptions.Value;
        _log         = log;
    }

    public async Task SendPasswordResetEmailAsync(
        string toEmail, string displayName, string resetUrl, CancellationToken ct = default)
    {
        var host = _smtpOptions.Host;

        // En desarrollo sin SMTP configurado: loggear el link en consola
        if (string.IsNullOrWhiteSpace(host))
        {
            _log.LogWarning("[DEV] Enlace de reset de contraseña para {Email}: {Url}", toEmail, resetUrl);
            return;
        }

        var port     = int.TryParse(_smtpOptions.Port, out var p) ? p : 587;
        var user     = _smtpOptions.User;
        var pass     = _smtpOptions.Password;
        var from     = string.IsNullOrWhiteSpace(_smtpOptions.FromAddress) ? user : _smtpOptions.FromAddress;
        var fromName = string.IsNullOrWhiteSpace(_smtpOptions.FromName) ? "ContableAI" : _smtpOptions.FromName;

        var subject = "Restablecer tu contraseña — ContableAI";
        var body    = $"""
            <html><body style="font-family:sans-serif;max-width:600px;margin:0 auto">
              <h2>Restablecer contraseña</h2>
              <p>Hola <strong>{System.Net.WebUtility.HtmlEncode(displayName)}</strong>,</p>
              <p>Recibimos una solicitud para restablecer la contraseña de tu cuenta ContableAI.</p>
              <p>
                <a href="{resetUrl}" style="display:inline-block;padding:12px 24px;background:#4f46e5;color:#fff;text-decoration:none;border-radius:6px">
                  Restablecer contraseña
                </a>
              </p>
              <p style="color:#666;font-size:14px">
                Este enlace es válido por <strong>1 hora</strong>.<br>
                Si no solicitaste este cambio, podés ignorar este email.
              </p>
              <hr style="border:none;border-top:1px solid #eee">
              <p style="color:#999;font-size:12px">ContableAI · Sistema de gestión contable</p>
            </body></html>
            """;

        using var client = new SmtpClient(host, port)
        {
            EnableSsl   = true,
            Credentials = new NetworkCredential(user, pass),
        };

        var message = new MailMessage(new MailAddress(from, fromName), new MailAddress(toEmail, displayName))
        {
            Subject    = subject,
            Body       = body,
            IsBodyHtml = true,
        };

        await client.SendMailAsync(message, ct);
        _log.LogInformation("Email de reset enviado a {Email}", toEmail);
    }
}
