using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Keystone.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSortingAndSections : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "SortOrder",
                table: "Tasks",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "ListSorts",
                table: "Settings",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "SortOrder",
                table: "SavingsGoals",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "SortOrder",
                table: "Insurance",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "Section",
                table: "Documents",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "SortOrder",
                table: "Debts",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "SortOrder",
                table: "Contracts",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SortOrder",
                table: "Tasks");

            migrationBuilder.DropColumn(
                name: "ListSorts",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "SortOrder",
                table: "SavingsGoals");

            migrationBuilder.DropColumn(
                name: "SortOrder",
                table: "Insurance");

            migrationBuilder.DropColumn(
                name: "Section",
                table: "Documents");

            migrationBuilder.DropColumn(
                name: "SortOrder",
                table: "Debts");

            migrationBuilder.DropColumn(
                name: "SortOrder",
                table: "Contracts");
        }
    }
}
