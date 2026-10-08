using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mir3.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddPendingPasswordHash : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<byte[]>(
                name: "PendingPasswordHash",
                table: "Registrations",
                type: "BLOB",
                maxLength: 36,
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_Registrations_PendingPasswordHash_Length",
                table: "Registrations",
                sql: "\"PendingPasswordHash\" IS NULL OR length(\"PendingPasswordHash\") = 36");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Registrations_PendingPasswordHash_Length",
                table: "Registrations");

            migrationBuilder.DropColumn(
                name: "PendingPasswordHash",
                table: "Registrations");
        }
    }
}
