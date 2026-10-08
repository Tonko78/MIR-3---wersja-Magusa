using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Mir3.Web.Domain;

namespace Mir3.Web.Data;

public sealed class PortalDbContext(DbContextOptions<PortalDbContext> options) : DbContext(options)
{
    private static readonly ValueConverter<DateTime, DateTime> UtcDateTimeConverter = new(
        value => value.Kind == DateTimeKind.Utc
            ? value
            : value.Kind == DateTimeKind.Local
                ? value.ToUniversalTime()
                : DateTime.SpecifyKind(value, DateTimeKind.Utc),
        value => DateTime.SpecifyKind(value, DateTimeKind.Utc));

    public DbSet<Registration> Registrations => Set<Registration>();

    public DbSet<PortalSetting> PortalSettings => Set<PortalSetting>();

    public DbSet<AdminUser> AdminUsers => Set<AdminUser>();

    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ConfigureRegistration(modelBuilder.Entity<Registration>());
        ConfigurePortalSetting(modelBuilder.Entity<PortalSetting>());
        ConfigureAdminUser(modelBuilder.Entity<AdminUser>());
        ConfigureAuditEntry(modelBuilder.Entity<AuditEntry>());
    }

    private static void ConfigureRegistration(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<Registration> entity)
    {
        entity.ToTable("Registrations", table =>
        {
            table.HasCheckConstraint("CK_Registrations_Email_MaxLength", "length(\"Email\") <= 320");
            table.HasCheckConstraint("CK_Registrations_NormalizedEmail_MaxLength", "length(\"NormalizedEmail\") <= 320");
            table.HasCheckConstraint("CK_Registrations_PreferredLanguage_Value", "\"PreferredLanguage\" IN ('en', 'pl')");
            table.HasCheckConstraint("CK_Registrations_PasswordHash_Length", "length(\"PasswordHash\") = 36");
            table.HasCheckConstraint("CK_Registrations_PendingPasswordHash_Length", "\"PendingPasswordHash\" IS NULL OR length(\"PendingPasswordHash\") = 36");
            table.HasCheckConstraint("CK_Registrations_VerificationTokenHash_Length", "length(\"VerificationTokenHash\") = 32");
            table.HasCheckConstraint("CK_Registrations_SourceIpHash_Length", "length(\"SourceIpHash\") = 32");
            table.HasCheckConstraint(
                "CK_Registrations_EmailLastErrorCode_Value",
                "\"EmailLastErrorCode\" IS NULL OR \"EmailLastErrorCode\" IN ('smtp-connect', 'smtp-auth', 'smtp-send')");
            table.HasCheckConstraint(
                "CK_Registrations_EmailDeliveryAttempt_Ownership",
                "\"EmailDeliveryAttemptAcquiredUtc\" IS NULL OR \"EmailDeliveryAttemptId\" IS NOT NULL");
            table.HasCheckConstraint(
                "CK_Registrations_QueueLastErrorCode_MaxLength",
                "\"QueueLastErrorCode\" IS NULL OR length(\"QueueLastErrorCode\") <= 64");
            table.HasCheckConstraint(
                "CK_Registrations_QueuePublicationState_Value",
                "\"QueuePublicationState\" IS NULL OR \"QueuePublicationState\" IN ('Pending', 'Published', 'Uncertain')");
            table.HasCheckConstraint(
                "CK_Registrations_Status_Value",
                "\"Status\" IN ('PendingEmail', 'AwaitingAdmin', 'QueuePending', 'Active', 'Disabled', 'Failed')");
        });
        entity.HasKey(registration => registration.Id);
        entity.Property(registration => registration.Email).HasMaxLength(320).IsRequired();
        entity.Property(registration => registration.NormalizedEmail).HasMaxLength(320).IsRequired();
        entity.Property(registration => registration.PreferredLanguage).HasMaxLength(2).HasDefaultValue("en").IsRequired();
        entity.Property(registration => registration.PasswordHash).HasMaxLength(36).IsRequired();
        entity.Property(registration => registration.PendingPasswordHash).HasMaxLength(36);
        entity.Property(registration => registration.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
        entity.Property(registration => registration.VerificationTokenHash).HasMaxLength(32).IsRequired();
        entity.Property(registration => registration.VerificationExpiresUtc).HasConversion(UtcDateTimeConverter);
        entity.Property(registration => registration.VerificationUsedUtc).HasConversion(UtcDateTimeConverter);
        entity.Property(registration => registration.EmailVerifiedUtc).HasConversion(UtcDateTimeConverter);
        entity.Property(registration => registration.EmailLastErrorCode).HasMaxLength(32);
        entity.Property(registration => registration.EmailDeliveryAttemptAcquiredUtc).HasConversion(UtcDateTimeConverter);
        entity.Property(registration => registration.GameActivatedUtc).HasConversion(UtcDateTimeConverter);
        entity.Property(registration => registration.AdminDisabledUtc).HasConversion(UtcDateTimeConverter);
        entity.Property(registration => registration.QueueCommandType).HasConversion<string>().HasMaxLength(32);
        entity.Property(registration => registration.QueuePublicationState).HasConversion<string>().HasMaxLength(32);
        entity.Property(registration => registration.QueueLastErrorCode).HasMaxLength(64);
        entity.Property(registration => registration.QueueLastCheckedUtc).HasConversion(UtcDateTimeConverter);
        entity.Property(registration => registration.CreatedUtc).HasConversion(UtcDateTimeConverter);
        entity.Property(registration => registration.UpdatedUtc).HasConversion(UtcDateTimeConverter);
        entity.Property(registration => registration.SourceIpHash).HasMaxLength(32).IsRequired();
        entity.HasIndex(registration => registration.NormalizedEmail).IsUnique();
        entity.HasIndex(registration => registration.Status);
        entity.HasIndex(registration => registration.VerificationTokenHash);
        entity.HasIndex(registration => new
        {
            registration.Status,
            registration.EmailDeliveryAttemptAcquiredUtc
        });
        entity.HasIndex(registration => registration.QueueRequestId);
        entity.HasIndex(registration => new
        {
            registration.Status,
            registration.QueuePublicationState
        });
        entity.HasIndex(registration => new
        {
            registration.Status,
            registration.QueueLastCheckedUtc,
            registration.UpdatedUtc,
            registration.Id
        });
    }

    private static void ConfigurePortalSetting(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<PortalSetting> entity)
    {
        entity.ToTable("PortalSettings", table =>
        {
            table.HasCheckConstraint("CK_PortalSettings_Singleton_Id", "\"Id\" = 1");
            table.HasCheckConstraint("CK_PortalSettings_Singleton_Key", "\"Key\" = 'portal'");
        });
        entity.HasKey(setting => setting.Id);
        entity.Property(setting => setting.Key).HasMaxLength(64).IsRequired();
        entity.Property(setting => setting.AutoActivateAfterEmailVerification).HasDefaultValue(false);
        entity.Property(setting => setting.UpdatedUtc).HasConversion(UtcDateTimeConverter);
        entity.HasIndex(setting => setting.Key).IsUnique();
    }

    private static void ConfigureAdminUser(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<AdminUser> entity)
    {
        entity.ToTable("AdminUsers", table =>
        {
            table.HasCheckConstraint("CK_AdminUsers_FailedAttempts_NonNegative", "\"FailedAttempts\" >= 0");
            table.HasCheckConstraint("CK_AdminUsers_Username_MaxLength", "length(\"Username\") <= 64");
            table.HasCheckConstraint("CK_AdminUsers_NormalizedUsername_MaxLength", "length(\"NormalizedUsername\") <= 64");
            table.HasCheckConstraint("CK_AdminUsers_PasswordHash_MaxLength", "length(\"PasswordHash\") <= 512");
        });
        entity.HasKey(admin => admin.Id);
        entity.Property(admin => admin.Username).HasMaxLength(64).IsRequired();
        entity.Property(admin => admin.NormalizedUsername).HasMaxLength(64).IsRequired();
        entity.Property(admin => admin.PasswordHash).HasMaxLength(512).IsRequired();
        entity.Property(admin => admin.LockoutUntilUtc).HasConversion(UtcDateTimeConverter);
        entity.Property(admin => admin.CreatedUtc).HasConversion(UtcDateTimeConverter);
        entity.Property(admin => admin.UpdatedUtc).HasConversion(UtcDateTimeConverter);
        entity.HasIndex(admin => admin.NormalizedUsername).IsUnique();
    }

    private static void ConfigureAuditEntry(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<AuditEntry> entity)
    {
        entity.ToTable("AuditEntries", table =>
        {
            table.HasCheckConstraint("CK_AuditEntries_Actor_MaxLength", "length(\"Actor\") <= 128");
            table.HasCheckConstraint("CK_AuditEntries_Action_MaxLength", "length(\"Action\") <= 128");
            table.HasCheckConstraint("CK_AuditEntries_Target_MaxLength", "length(\"Target\") <= 256");
            table.HasCheckConstraint(
                "CK_AuditEntries_DetailsJson_MaxLength",
                "\"DetailsJson\" IS NULL OR length(\"DetailsJson\") <= 4096");
        });
        entity.HasKey(entry => entry.Id);
        entity.Property(entry => entry.Actor).HasMaxLength(128).IsRequired();
        entity.Property(entry => entry.Action).HasMaxLength(128).IsRequired();
        entity.Property(entry => entry.Target).HasMaxLength(256).IsRequired();
        entity.Property(entry => entry.DetailsJson).HasMaxLength(4096);
        entity.Property(entry => entry.CreatedUtc).HasConversion(UtcDateTimeConverter);
        entity.HasIndex(entry => entry.CreatedUtc);
        entity.HasIndex(entry => entry.Target);
    }
}
