using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Keystone.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAccountsInsuranceRemindersLock : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AccountId",
                table: "Transactions",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExternalId",
                table: "Transactions",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ToAccountId",
                table: "Transactions",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LockAfterMinutes",
                table: "Settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 5);

            migrationBuilder.AddColumn<bool>(
                name: "LockEnabled",
                table: "Settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "ReminderHour",
                table: "Settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 8);

            migrationBuilder.AddColumn<bool>(
                name: "RemindersEnabled",
                table: "Settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.CreateTable(
                name: "BankAccounts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Iban = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    OpeningBalance = table.Column<decimal>(type: "TEXT", nullable: false),
                    OverdraftLimit = table.Column<decimal>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BankAccounts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Insurance",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Insurer = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 24, nullable: false),
                    PersonId = table.Column<int>(type: "INTEGER", nullable: true),
                    PolicyNumber = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Premium = table.Column<decimal>(type: "TEXT", nullable: false),
                    PremiumRecurrence = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    ContractEnd = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    RenewsAutomatically = table.Column<bool>(type: "INTEGER", nullable: false),
                    NoticeMonths = table.Column<int>(type: "INTEGER", nullable: false),
                    Notes = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    DocumentId = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Insurance", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Insurance_Documents_DocumentId",
                        column: x => x.DocumentId,
                        principalTable: "Documents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Insurance_People_PersonId",
                        column: x => x.PersonId,
                        principalTable: "People",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Transactions_AccountId",
                table: "Transactions",
                column: "AccountId");

            migrationBuilder.CreateIndex(
                name: "IX_Transactions_ExternalId",
                table: "Transactions",
                column: "ExternalId");

            migrationBuilder.CreateIndex(
                name: "IX_Transactions_ToAccountId",
                table: "Transactions",
                column: "ToAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_Insurance_DocumentId",
                table: "Insurance",
                column: "DocumentId");

            migrationBuilder.CreateIndex(
                name: "IX_Insurance_PersonId",
                table: "Insurance",
                column: "PersonId");

            migrationBuilder.AddForeignKey(
                name: "FK_Transactions_BankAccounts_AccountId",
                table: "Transactions",
                column: "AccountId",
                principalTable: "BankAccounts",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_Transactions_BankAccounts_ToAccountId",
                table: "Transactions",
                column: "ToAccountId",
                principalTable: "BankAccounts",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Transactions_BankAccounts_AccountId",
                table: "Transactions");

            migrationBuilder.DropForeignKey(
                name: "FK_Transactions_BankAccounts_ToAccountId",
                table: "Transactions");

            migrationBuilder.DropTable(
                name: "BankAccounts");

            migrationBuilder.DropTable(
                name: "Insurance");

            migrationBuilder.DropIndex(
                name: "IX_Transactions_AccountId",
                table: "Transactions");

            migrationBuilder.DropIndex(
                name: "IX_Transactions_ExternalId",
                table: "Transactions");

            migrationBuilder.DropIndex(
                name: "IX_Transactions_ToAccountId",
                table: "Transactions");

            migrationBuilder.DropColumn(
                name: "AccountId",
                table: "Transactions");

            migrationBuilder.DropColumn(
                name: "ExternalId",
                table: "Transactions");

            migrationBuilder.DropColumn(
                name: "ToAccountId",
                table: "Transactions");

            migrationBuilder.DropColumn(
                name: "LockAfterMinutes",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "LockEnabled",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "ReminderHour",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "RemindersEnabled",
                table: "Settings");
        }
    }
}
