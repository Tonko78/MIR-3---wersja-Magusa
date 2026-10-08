using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mir3.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddEmailDeliveryState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "EmailLastErrorCode",
                table: "Registrations",
                type: "TEXT",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_Registrations_EmailLastErrorCode_Value",
                table: "Registrations",
                sql: "\"EmailLastErrorCode\" IS NULL OR \"EmailLastErrorCode\" IN ('smtp-connect', 'smtp-auth', 'smtp-send')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Registrations_EmailLastErrorCode_Value",
                table: "Registrations");

            migrationBuilder.DropColumn(
                name: "EmailLastErrorCode",
                table: "Registrations");
        }
    }
}
