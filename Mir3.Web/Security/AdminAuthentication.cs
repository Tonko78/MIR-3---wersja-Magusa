using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Mir3.Web.Domain;

namespace Mir3.Web.Security;

public static class AdminAuthentication
{
    public const string Scheme = "AdminCookie";
    public const string Policy = "AdminOnly";
    public const string CookieName = ".mir3.admin";
}

public sealed class AdminPasswordHasher : IPasswordHasher<AdminUser>
{
    private readonly PasswordHasher<AdminUser> _inner = new();

    public string HashPassword(AdminUser user, string password) =>
        _inner.HashPassword(user, password);

    public PasswordVerificationResult VerifyHashedPassword(
        AdminUser user,
        string hashedPassword,
        string providedPassword) =>
        _inner.VerifyHashedPassword(user, hashedPassword, providedPassword);
}
