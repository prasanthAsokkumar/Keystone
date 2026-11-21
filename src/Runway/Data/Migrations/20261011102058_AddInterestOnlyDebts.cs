using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Runway.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddInterestOnlyDebts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsInterestOnly",
                table: "Debts",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsInterestOnly",
                table: "Debts");
        }
    }
}
