using ContableAI.Application.Features.Auth.Commands;
using ContableAI.Domain.Entities;
using ContableAI.Infrastructure.Features.Auth;
using ContableAI.Infrastructure.Options;
using ContableAI.Infrastructure.Persistence;
using ContableAI.Infrastructure.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ContableAI.Tests.Infrastructure;

/// <summary>
/// Tests de B-1c (token de reset con CSPRNG + hash en BD) y B-1b (respuesta constante en
/// registro para prevenir enumeración de emails).
/// </summary>
public class PasswordResetSecurityTests
{
    private sealed class FakeEmailService : IEmailService
    {
        public string? LastResetUrl { get; private set; }
        public Task SendPasswordResetEmailAsync(string toEmail, string displayName, string resetUrl, CancellationToken ct = default)
        {
            LastResetUrl = resetUrl;
            return Task.CompletedTask;
        }
    }

    private static ContableAIDbContext CtxFor(string dbName) =>
        new(new DbContextOptionsBuilder<ContableAIDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options);

    private static readonly IPasswordHasher<User> Hasher = new PasswordHasher<User>();

    private static User SeedUser(ContableAIDbContext ctx, string email)
    {
        var user = new User
        {
            Email          = email,
            DisplayName    = "Usuaria",
            StudioTenantId = Guid.NewGuid().ToString(),
            IsActive       = true,
        };
        user.PasswordHash = Hasher.HashPassword(user, "Anterior123!");
        ctx.Users.Add(user);
        ctx.SaveChanges();
        return user;
    }

    // ── B-1c: generación ──────────────────────────────────────────────────────

    [Fact]
    public void Generate_Produces256BitHexTokens_Unique()
    {
        var tokens = Enumerable.Range(0, 100).Select(_ => PasswordResetTokens.Generate()).ToList();

        tokens.Should().OnlyContain(t => t.Length == 64, "32 bytes → 64 chars hex (256 bits ≥ 128 de OWASP)");
        tokens.Distinct().Should().HaveCount(100);
    }

    [Fact]
    public void Verify_RejectsLegacyPlaintextGuidToken()
    {
        // Token legado pre-B-1c: guid en claro almacenado tal cual. Nunca debe validar.
        var legacyStored = Guid.NewGuid().ToString("N");
        PasswordResetTokens.Verify(legacyStored, legacyStored).Should().BeFalse(
            "lo almacenado se interpreta como hash; un valor legado en claro no coincide con ningún hash");
    }

    // ── B-1c: flujo completo forgot → reset ───────────────────────────────────

    [Fact]
    public async Task ForgotPassword_StoresOnlyHash_AndEmailedTokenStillResets()
    {
        var db = nameof(ForgotPassword_StoresOnlyHash_AndEmailedTokenStillResets);
        var email = "b1c@estudio.com";
        var emailService = new FakeEmailService();

        using (var ctx = CtxFor(db))
        {
            SeedUser(ctx, email);
            var forgot = new ForgotPasswordHandler(ctx, emailService,
                Options.Create(new FrontendOptions()), NullLogger<ForgotPasswordHandler>.Instance);
            (await forgot.Handle(new ForgotPasswordCommand(email), default)).IsSuccess.Should().BeTrue();
        }

        // El token viaja en el link; extraerlo como lo haría el usuario.
        emailService.LastResetUrl.Should().NotBeNull();
        var rawToken = System.Web.HttpUtility.ParseQueryString(new Uri(emailService.LastResetUrl!).Query)["token"]!;
        rawToken.Should().HaveLength(64);

        using (var check = CtxFor(db))
        {
            var stored = (await check.Users.SingleAsync(u => u.Email == email)).PasswordResetToken;
            stored.Should().NotBeNull();
            stored.Should().NotBe(rawToken, "en la BD debe persistirse el hash, nunca el token en claro");
            stored.Should().Be(PasswordResetTokens.Hash(rawToken));
        }

        using (var ctx = CtxFor(db))
        {
            var reset = new ResetPasswordHandler(ctx, Hasher);
            var result = await reset.Handle(new ResetPasswordCommand(rawToken, email, "Nueva456!"), default);
            result.IsSuccess.Should().BeTrue();
        }

        using (var check = CtxFor(db))
        {
            var user = await check.Users.SingleAsync(u => u.Email == email);
            user.PasswordResetToken.Should().BeNull("el token es de un solo uso");
            Hasher.VerifyHashedPassword(user, user.PasswordHash, "Nueva456!")
                .Should().NotBe(PasswordVerificationResult.Failed);
        }
    }

    [Fact]
    public async Task ResetPassword_WithWrongToken_Fails()
    {
        var db = nameof(ResetPassword_WithWrongToken_Fails);
        var email = "b1c-wrong@estudio.com";

        using (var ctx = CtxFor(db))
        {
            var user = SeedUser(ctx, email);
            user.PasswordResetToken       = PasswordResetTokens.Hash(PasswordResetTokens.Generate());
            user.PasswordResetTokenExpiry = DateTime.UtcNow.AddHours(1);
            ctx.SaveChanges();
        }

        using var attempt = CtxFor(db);
        var result = await new ResetPasswordHandler(attempt, Hasher)
            .Handle(new ResetPasswordCommand(PasswordResetTokens.Generate(), email, "Hackeada789!"), default);

        result.IsSuccess.Should().BeFalse();
        result.StatusCode.Should().Be(400);
    }

    // ── B-1b: respuesta constante en registro de invitados ────────────────────

    [Fact]
    public async Task Register_WithExistingEmail_ReturnsSameGenericResponse_AndCreatesNothing()
    {
        var db = nameof(Register_WithExistingEmail_ReturnsSameGenericResponse_AndCreatesNothing);
        var email = "b1b@estudio.com";
        var env = new FakeWebHostEnvironment();

        Application.Common.Result<RegisterResponse> first, duplicate;
        using (var ctx = CtxFor(db))
            first = await new RegisterHandler(ctx, Hasher, env)
                .Handle(new RegisterCommand(email, "Password1!", "Uno", null), default);

        using (var ctx = CtxFor(db))
            duplicate = await new RegisterHandler(ctx, Hasher, env)
                .Handle(new RegisterCommand(email, "Password2!", "Dos", null), default);

        // Indistinguibles para el cliente: mismo status, mismo cuerpo.
        duplicate.IsSuccess.Should().BeTrue();
        duplicate.StatusCode.Should().Be(first.StatusCode);
        duplicate.Value!.Message.Should().Be(first.Value!.Message);
        duplicate.Value.PendingApproval.Should().Be(first.Value.PendingApproval);

        using var check = CtxFor(db);
        (await check.Users.CountAsync(u => u.Email == email)).Should().Be(1, "el duplicado no debe crear nada");
    }

    private sealed class FakeWebHostEnvironment : Microsoft.AspNetCore.Hosting.IWebHostEnvironment
    {
        public string WebRootPath { get; set; } = string.Empty;
        public Microsoft.Extensions.FileProviders.IFileProvider WebRootFileProvider { get; set; } = null!;
        public string ApplicationName { get; set; } = "Tests";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
        public string ContentRootPath { get; set; } = string.Empty;
        public string EnvironmentName { get; set; } = "Production";
    }
}
