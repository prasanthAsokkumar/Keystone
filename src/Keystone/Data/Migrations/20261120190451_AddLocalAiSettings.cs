using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Keystone.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddLocalAiSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AiEndpoint",
                table: "Settings",
                type: "TEXT",
                maxLength: 200,
                nullable: false,
                defaultValue: "http://localhost:11434");

            migrationBuilder.AddColumn<string>(
                name: "AiModel",
                table: "Settings",
                type: "TEXT",
                maxLength: 100,
                nullable: false,
                defaultValue: "qwen2.5vl:7b");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AiEndpoint",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "AiModel",
                table: "Settings");
        }
    }
}
