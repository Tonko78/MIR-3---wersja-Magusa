using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mir3.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddEmailDeliveryOwnership : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "EmailDeliveryAttemptAcquiredUtc",
                table: "Registrations",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "EmailDeliveryAttemptId",
                table: "Registrations",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Registrations_Status_EmailDeliveryAttemptAcquiredUtc",
                table: "Registrations",
                columns: new[] { "Status", "EmailDeliveryAttemptAcquiredUtc" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_Registrations_EmailDeliveryAttempt_Ownership",
                table: "Registrations",
                sql: "\"EmailDeliveryAttemptAcquiredUtc\" IS NULL OR \"EmailDeliveryAttemptId\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Registrations_Status_EmailDeliveryAttemptAcquiredUtc",
                table: "Registrations");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Registrations_EmailDeliveryAttempt_Ownership",
                table: "Registrations");

            migrationBuilder.DropColumn(
                name: "EmailDeliveryAttemptAcquiredUtc",
                table: "Registrations");

            migrationBuilder.DropColumn(
                name: "EmailDeliveryAttemptId",
                table: "Registrations");
        }
    }
}
