using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mir3.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddRegistrationPreferredLanguage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PreferredLanguage",
                table: "Registrations",
                type: "TEXT",
                maxLength: 2,
                nullable: false,
                defaultValue: "en");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Registrations_PreferredLanguage_Value",
                table: "Registrations",
                sql: "\"PreferredLanguage\" IN ('en', 'pl')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Registrations_PreferredLanguage_Value",
                table: "Registrations");

            migrationBuilder.DropColumn(
                name: "PreferredLanguage",
                table: "Registrations");
        }
    }
}
