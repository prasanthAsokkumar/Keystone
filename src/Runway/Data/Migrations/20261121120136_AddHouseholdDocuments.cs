using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Runway.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddHouseholdDocuments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DocumentsFolder",
                table: "Settings",
                type: "TEXT",
                maxLength: 500,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "Documents",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Title = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Sender = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    PersonId = table.Column<int>(type: "INTEGER", nullable: true),
                    Date = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    Amount = table.Column<decimal>(type: "TEXT", nullable: true),
                    DueDate = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    PaidOn = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    Notes = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    RelativePath = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    AddedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    TransactionId = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Documents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Documents_People_PersonId",
                        column: x => x.PersonId,
                        principalTable: "People",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Documents_Transactions_TransactionId",
                        column: x => x.TransactionId,
                        principalTable: "Transactions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Documents_Date",
                table: "Documents",
                column: "Date");

            migrationBuilder.CreateIndex(
                name: "IX_Documents_PersonId",
                table: "Documents",
                column: "PersonId");

            migrationBuilder.CreateIndex(
                name: "IX_Documents_TransactionId",
                table: "Documents",
                column: "TransactionId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Documents");

            migrationBuilder.DropColumn(
                name: "DocumentsFolder",
                table: "Settings");
        }
    }
}
