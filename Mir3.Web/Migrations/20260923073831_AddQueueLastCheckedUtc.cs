using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mir3.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddQueueLastCheckedUtc : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "QueueLastCheckedUtc",
                table: "Registrations",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Registrations_Status_QueueLastCheckedUtc_UpdatedUtc_Id",
                table: "Registrations",
                columns: new[] { "Status", "QueueLastCheckedUtc", "UpdatedUtc", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Registrations_Status_QueueLastCheckedUtc_UpdatedUtc_Id",
                table: "Registrations");

            migrationBuilder.DropColumn(
                name: "QueueLastCheckedUtc",
                table: "Registrations");
        }
    }
}
