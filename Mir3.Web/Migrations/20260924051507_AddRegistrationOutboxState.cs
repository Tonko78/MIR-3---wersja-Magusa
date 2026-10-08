using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mir3.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddRegistrationOutboxState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "InitialEmailDeliveryPending",
                table: "Registrations",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "QueuePublicationState",
                table: "Registrations",
                type: "TEXT",
                maxLength: 32,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Registrations_Status_QueuePublicationState",
                table: "Registrations",
                columns: new[] { "Status", "QueuePublicationState" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_Registrations_QueuePublicationState_Value",
                table: "Registrations",
                sql: "\"QueuePublicationState\" IS NULL OR \"QueuePublicationState\" IN ('Pending', 'Published', 'Uncertain')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Registrations_Status_QueuePublicationState",
                table: "Registrations");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Registrations_QueuePublicationState_Value",
                table: "Registrations");

            migrationBuilder.DropColumn(
                name: "InitialEmailDeliveryPending",
                table: "Registrations");

            migrationBuilder.DropColumn(
                name: "QueuePublicationState",
                table: "Registrations");
        }
    }
}
