using ContableAI.Domain.Entities;
using ContableAI.Domain.Enums;
using ContableAI.Infrastructure.Options;
using ContableAI.Infrastructure.Persistence;
using ContableAI.Infrastructure.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ContableAI.Tests.Infrastructure;

/// <summary>
/// Tests del ciclo de vida de refresh tokens (A-3): emisión hasheada, rotación,
/// revocación (logout) y detección de reuso de un token ya rotado.
/// </summary>
public class RefreshTokenServiceTests
{
    private static ContableAIDbContext NewDb(string name) =>
        new(new DbContextOptionsBuilder<ContableAIDbContext>().UseInMemoryDatabase(name).Options);

    private static RefreshTokenService NewService(ContableAIDbContext db) =>
        new(db, Options.Create(new JwtOptions { RefreshTokenDays = 7 }), NullLogger<RefreshTokenService>.Instance);

    private static async Task<User> SeedUserAsync(ContableAIDbContext db, bool active = true)
    {
        var user = new User
        {
            Email          = "user@estudio.com",
            DisplayName    = "Usuario",
            StudioTenantId = "studio-x",
            Role           = UserRole.StudioOwner,
            IsActive       = active,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    [Fact]
    public async Task Issue_PersistsHash_NotRawToken()
    {
        using var db = NewDb(nameof(Issue_PersistsHash_NotRawToken));
        var svc  = NewService(db);
        var user = await SeedUserAsync(db);

        var raw = await svc.IssueAsync(user.Id);

        var stored = db.RefreshTokens.Single();
        stored.TokenHash.Should().NotBe(raw, "el token en claro nunca se persiste");
        stored.TokenHash.Should().HaveLength(64, "SHA-256 en hex son 64 caracteres");
        stored.IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task Rotate_ValidToken_ReturnsUser_AndInvalidatesOldToken()
    {
        using var db = NewDb(nameof(Rotate_ValidToken_ReturnsUser_AndInvalidatesOldToken));
        var svc  = NewService(db);
        var user = await SeedUserAsync(db);

        var raw1 = await svc.IssueAsync(user.Id);
        var rotation = await svc.RotateAsync(raw1);

        rotation.Should().NotBeNull();
        rotation!.User.Id.Should().Be(user.Id);
        rotation.NewRawToken.Should().NotBe(raw1);

        // El token viejo quedó revocado y enlazado al nuevo.
        var oldToken = db.RefreshTokens.Single(t => t.TokenHash != Sha256Hex(rotation.NewRawToken));
        oldToken.RevokedAt.Should().NotBeNull();
        oldToken.ReplacedByTokenId.Should().NotBeNull();
    }

    [Fact]
    public async Task Rotate_UnknownToken_ReturnsNull()
    {
        using var db = NewDb(nameof(Rotate_UnknownToken_ReturnsNull));
        var svc = NewService(db);
        await SeedUserAsync(db);

        (await svc.RotateAsync("deadbeef")).Should().BeNull();
    }

    [Fact]
    public async Task Rotate_AfterRevoke_ReturnsNull()
    {
        using var db = NewDb(nameof(Rotate_AfterRevoke_ReturnsNull));
        var svc  = NewService(db);
        var user = await SeedUserAsync(db);

        var raw = await svc.IssueAsync(user.Id);
        await svc.RevokeAsync(raw);

        // Un token revocado por logout se trata como reuso → no rota.
        (await svc.RotateAsync(raw)).Should().BeNull();
    }

    [Fact]
    public async Task Reuse_OfRotatedToken_RevokesWholeChain()
    {
        using var db = NewDb(nameof(Reuse_OfRotatedToken_RevokesWholeChain));
        var svc  = NewService(db);
        var user = await SeedUserAsync(db);

        var raw1 = await svc.IssueAsync(user.Id);
        var rotation = await svc.RotateAsync(raw1);   // raw1 → raw2
        rotation.Should().NotBeNull();

        // Reusar raw1 (ya rotado) debe delatar robo y revocar TODA la cadena, incluido raw2.
        (await svc.RotateAsync(raw1)).Should().BeNull();
        db.RefreshTokens.Where(t => t.UserId == user.Id).All(t => t.RevokedAt != null).Should().BeTrue();
        (await svc.RotateAsync(rotation!.NewRawToken)).Should().BeNull("raw2 también quedó revocado");
    }

    [Fact]
    public async Task Rotate_InactiveUser_ReturnsNull()
    {
        using var db = NewDb(nameof(Rotate_InactiveUser_ReturnsNull));
        var svc  = NewService(db);
        var user = await SeedUserAsync(db, active: false);

        var raw = await svc.IssueAsync(user.Id);
        (await svc.RotateAsync(raw)).Should().BeNull();
    }

    private static string Sha256Hex(string raw) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(raw)));
}
