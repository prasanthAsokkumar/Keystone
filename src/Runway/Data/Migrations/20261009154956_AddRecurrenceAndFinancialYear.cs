using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Runway.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddRecurrenceAndFinancialYear : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateOnly>(
                name: "EndDate",
                table: "Transactions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Recurrence",
                table: "Transactions",
                type: "TEXT",
                maxLength: 16,
                nullable: false,
                defaultValue: "OneTime");

            migrationBuilder.AddColumn<int>(
                name: "FinancialYearStartMonth",
                table: "Settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EndDate",
                table: "Transactions");

            migrationBuilder.DropColumn(
                name: "Recurrence",
                table: "Transactions");

            migrationBuilder.DropColumn(
                name: "FinancialYearStartMonth",
                table: "Settings");
        }
    }
}
