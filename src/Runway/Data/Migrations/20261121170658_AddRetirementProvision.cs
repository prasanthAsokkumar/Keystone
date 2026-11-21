using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Runway.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddRetirementProvision : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PensionAccounts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Pillar = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    PersonId = table.Column<int>(type: "INTEGER", nullable: true),
                    Notes = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PensionAccounts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PensionAccounts_People_PersonId",
                        column: x => x.PersonId,
                        principalTable: "People",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "PensionStatements",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    AccountId = table.Column<int>(type: "INTEGER", nullable: false),
                    Date = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    Balance = table.Column<decimal>(type: "TEXT", nullable: false),
                    PaidIn = table.Column<decimal>(type: "TEXT", nullable: true),
                    ProjectedCapital = table.Column<decimal>(type: "TEXT", nullable: true),
                    ProjectedPension = table.Column<decimal>(type: "TEXT", nullable: true),
                    BuyInPotential = table.Column<decimal>(type: "TEXT", nullable: true),
                    DocumentId = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PensionStatements", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PensionStatements_Documents_DocumentId",
                        column: x => x.DocumentId,
                        principalTable: "Documents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_PensionStatements_PensionAccounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "PensionAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PensionAccounts_PersonId",
                table: "PensionAccounts",
                column: "PersonId");

            migrationBuilder.CreateIndex(
                name: "IX_PensionStatements_AccountId",
                table: "PensionStatements",
                column: "AccountId");

            migrationBuilder.CreateIndex(
                name: "IX_PensionStatements_Date",
                table: "PensionStatements",
                column: "Date");

            migrationBuilder.CreateIndex(
                name: "IX_PensionStatements_DocumentId",
                table: "PensionStatements",
                column: "DocumentId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PensionStatements");

            migrationBuilder.DropTable(
                name: "PensionAccounts");
        }
    }
}
