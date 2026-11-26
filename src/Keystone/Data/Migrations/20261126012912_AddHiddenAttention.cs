using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Keystone.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddHiddenAttention : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "HiddenAttention",
                table: "Settings",
                type: "TEXT",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "HiddenAttention",
                table: "Settings");
        }
    }
}
