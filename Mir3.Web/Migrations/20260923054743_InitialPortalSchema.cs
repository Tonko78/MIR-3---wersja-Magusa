using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mir3.Web.Migrations
{
    /// <inheritdoc />
    public partial class InitialPortalSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AdminUsers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Username = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    NormalizedUsername = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    PasswordHash = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    FailedAttempts = table.Column<int>(type: "INTEGER", nullable: false),
                    LockoutUntilUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AdminUsers", x => x.Id);
                    table.CheckConstraint("CK_AdminUsers_FailedAttempts_NonNegative", "\"FailedAttempts\" >= 0");
                    table.CheckConstraint("CK_AdminUsers_NormalizedUsername_MaxLength", "length(\"NormalizedUsername\") <= 64");
                    table.CheckConstraint("CK_AdminUsers_PasswordHash_MaxLength", "length(\"PasswordHash\") <= 512");
                    table.CheckConstraint("CK_AdminUsers_Username_MaxLength", "length(\"Username\") <= 64");
                });

            migrationBuilder.CreateTable(
                name: "AuditEntries",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Actor = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Action = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Target = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    DetailsJson = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditEntries", x => x.Id);
                    table.CheckConstraint("CK_AuditEntries_Action_MaxLength", "length(\"Action\") <= 128");
                    table.CheckConstraint("CK_AuditEntries_Actor_MaxLength", "length(\"Actor\") <= 128");
                    table.CheckConstraint("CK_AuditEntries_DetailsJson_MaxLength", "\"DetailsJson\" IS NULL OR length(\"DetailsJson\") <= 4096");
                    table.CheckConstraint("CK_AuditEntries_Target_MaxLength", "length(\"Target\") <= 256");
                });

            migrationBuilder.CreateTable(
                name: "PortalSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Key = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    AutoActivateAfterEmailVerification = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PortalSettings", x => x.Id);
                    table.CheckConstraint("CK_PortalSettings_Singleton_Id", "\"Id\" = 1");
                    table.CheckConstraint("CK_PortalSettings_Singleton_Key", "\"Key\" = 'portal'");
                });

            migrationBuilder.CreateTable(
                name: "Registrations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Email = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    NormalizedEmail = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    PasswordHash = table.Column<byte[]>(type: "BLOB", maxLength: 36, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    VerificationTokenHash = table.Column<byte[]>(type: "BLOB", maxLength: 32, nullable: false),
                    VerificationExpiresUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    VerificationUsedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    EmailVerifiedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    GameActivatedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    AdminDisabledUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    QueueRequestId = table.Column<Guid>(type: "TEXT", nullable: true),
                    QueueLastErrorCode = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    SourceIpHash = table.Column<byte[]>(type: "BLOB", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Registrations", x => x.Id);
                    table.CheckConstraint("CK_Registrations_Email_MaxLength", "length(\"Email\") <= 320");
                    table.CheckConstraint("CK_Registrations_NormalizedEmail_MaxLength", "length(\"NormalizedEmail\") <= 320");
                    table.CheckConstraint("CK_Registrations_PasswordHash_Length", "length(\"PasswordHash\") = 36");
                    table.CheckConstraint("CK_Registrations_QueueLastErrorCode_MaxLength", "\"QueueLastErrorCode\" IS NULL OR length(\"QueueLastErrorCode\") <= 64");
                    table.CheckConstraint("CK_Registrations_SourceIpHash_Length", "length(\"SourceIpHash\") = 32");
                    table.CheckConstraint("CK_Registrations_Status_Value", "\"Status\" IN ('PendingEmail', 'AwaitingAdmin', 'QueuePending', 'Active', 'Disabled', 'Failed')");
                    table.CheckConstraint("CK_Registrations_VerificationTokenHash_Length", "length(\"VerificationTokenHash\") = 32");
                });

            migrationBuilder.CreateIndex(
                name: "IX_AdminUsers_NormalizedUsername",
                table: "AdminUsers",
                column: "NormalizedUsername",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AuditEntries_CreatedUtc",
                table: "AuditEntries",
                column: "CreatedUtc");

            migrationBuilder.CreateIndex(
                name: "IX_AuditEntries_Target",
                table: "AuditEntries",
                column: "Target");

            migrationBuilder.CreateIndex(
                name: "IX_PortalSettings_Key",
                table: "PortalSettings",
                column: "Key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Registrations_NormalizedEmail",
                table: "Registrations",
                column: "NormalizedEmail",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Registrations_QueueRequestId",
                table: "Registrations",
                column: "QueueRequestId");

            migrationBuilder.CreateIndex(
                name: "IX_Registrations_Status",
                table: "Registrations",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_Registrations_VerificationTokenHash",
                table: "Registrations",
                column: "VerificationTokenHash");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AdminUsers");

            migrationBuilder.DropTable(
                name: "AuditEntries");

            migrationBuilder.DropTable(
                name: "PortalSettings");

            migrationBuilder.DropTable(
                name: "Registrations");
        }
    }
}
