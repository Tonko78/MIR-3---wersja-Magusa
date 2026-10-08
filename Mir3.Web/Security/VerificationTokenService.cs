using System.Security.Cryptography;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Mir3.Web.Data;
using Mir3.Web.Domain;

namespace Mir3.Web.Security;

public sealed class VerificationTokenService(TimeProvider? timeProvider = null)
{
    public const int TokenByteCount = 32;
    public static readonly TimeSpan TokenLifetime = TimeSpan.FromHours(24);

    private static readonly byte[] EmptyTokenHash = new byte[TokenByteCount];
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public string Issue(Registration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);

        var rawToken = RandomNumberGenerator.GetBytes(TokenByteCount);
        try
        {
            var token = WebEncoders.Base64UrlEncode(rawToken);
            registration.VerificationTokenHash = SHA256.HashData(rawToken);
            registration.VerificationExpiresUtc = UtcNow.Add(TokenLifetime);
            registration.VerificationUsedUtc = null;
            return token;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(rawToken);
        }
    }

    public async Task<bool> ConsumeAsync(
        PortalDbContext database,
        string? token,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(database);

        if (!TryDecodeToken(token, out var rawToken))
        {
            return false;
        }

        byte[] tokenHash;
        try
        {
            tokenHash = SHA256.HashData(rawToken);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(rawToken);
        }

        try
        {
            var now = UtcNow;
            var candidate = await database.Registrations
                .AsNoTracking()
                .Where(registration =>
                    registration.VerificationTokenHash == tokenHash &&
                    registration.VerificationUsedUtc == null &&
                    registration.VerificationExpiresUtc > now)
                .Select(registration => new
                {
                    registration.Id,
                    registration.VerificationTokenHash
                })
                .SingleOrDefaultAsync(cancellationToken);

            var storedHash = candidate?.VerificationTokenHash ?? EmptyTokenHash;
            if (!CryptographicOperations.FixedTimeEquals(storedHash, tokenHash) || candidate is null)
            {
                return false;
            }

            var updated = await database.Registrations
                .Where(registration =>
                    registration.Id == candidate.Id &&
                    registration.VerificationTokenHash == tokenHash &&
                    registration.VerificationUsedUtc == null &&
                    registration.VerificationExpiresUtc > now)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(registration => registration.VerificationUsedUtc, now)
                        .SetProperty(registration => registration.UpdatedUtc, now),
                    cancellationToken);

            return updated == 1;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(tokenHash);
        }
    }

    private DateTime UtcNow => _timeProvider.GetUtcNow().UtcDateTime;

    private static bool TryDecodeToken(string? token, out byte[] rawToken)
    {
        rawToken = [];
        if (string.IsNullOrEmpty(token) || token.Length != 43)
        {
            return false;
        }

        try
        {
            rawToken = WebEncoders.Base64UrlDecode(token);
            if (rawToken.Length == TokenByteCount)
            {
                return true;
            }

            CryptographicOperations.ZeroMemory(rawToken);
            rawToken = [];
            return false;
        }
        catch (FormatException)
        {
            rawToken = [];
            return false;
        }
    }
}
