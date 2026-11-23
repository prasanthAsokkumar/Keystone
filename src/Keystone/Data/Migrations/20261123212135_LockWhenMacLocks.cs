using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Keystone.Data.Migrations
{
    /// <inheritdoc />
    public partial class LockWhenMacLocks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The owner asked for the lock to follow the Mac's own lock, not every switch to another app.
            migrationBuilder.Sql("UPDATE Settings SET LockAfterMinutes = -1;");

            migrationBuilder.AlterColumn<int>(
                name: "LockAfterMinutes",
                table: "Settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: -1,
                oldClrType: typeof(int),
                oldType: "INTEGER",
                oldDefaultValue: 5);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<int>(
                name: "LockAfterMinutes",
                table: "Settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 5,
                oldClrType: typeof(int),
                oldType: "INTEGER",
                oldDefaultValue: -1);
        }
    }
}
