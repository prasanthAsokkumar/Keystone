using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Keystone.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddHouseholdProfiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AhvNumber",
                table: "People",
                type: "TEXT",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Allergies",
                table: "People",
                type: "TEXT",
                maxLength: 1000,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateOnly>(
                name: "BirthDate",
                table: "People",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BloodType",
                table: "People",
                type: "TEXT",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Conditions",
                table: "People",
                type: "TEXT",
                maxLength: 1000,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "DoctorName",
                table: "People",
                type: "TEXT",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "DoctorPhone",
                table: "People",
                type: "TEXT",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Email",
                table: "People",
                type: "TEXT",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "EmergencyName",
                table: "People",
                type: "TEXT",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "EmergencyPhone",
                table: "People",
                type: "TEXT",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "EmergencyRelation",
                table: "People",
                type: "TEXT",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Notes",
                table: "People",
                type: "TEXT",
                maxLength: 2000,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Phone",
                table: "People",
                type: "TEXT",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "Employments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PersonId = table.Column<int>(type: "INTEGER", nullable: true),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    Employer = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Role = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Workload = table.Column<int>(type: "INTEGER", nullable: true),
                    StartedOn = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    EndedOn = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    Address = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Phone = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    ManagerName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    ManagerPhone = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    ManagerEmail = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    HrName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    HrPhone = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    HrEmail = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    ReportBy = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    ReportBefore = table.Column<TimeOnly>(type: "TEXT", nullable: true),
                    CertificateFromDay = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 3),
                    SickPayInsurer = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    AccidentInsurer = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Notes = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Employments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Employments_People_PersonId",
                        column: x => x.PersonId,
                        principalTable: "People",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Medicines",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PersonId = table.Column<int>(type: "INTEGER", nullable: true),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Strength = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Dose = table.Column<decimal>(type: "TEXT", nullable: false),
                    Unit = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    Morning = table.Column<bool>(type: "INTEGER", nullable: false),
                    Noon = table.Column<bool>(type: "INTEGER", nullable: false),
                    Evening = table.Column<bool>(type: "INTEGER", nullable: false),
                    Night = table.Column<bool>(type: "INTEGER", nullable: false),
                    AsNeeded = table.Column<bool>(type: "INTEGER", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    PrescribedBy = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    StartedOn = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    EndsOn = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    Stock = table.Column<decimal>(type: "TEXT", nullable: true),
                    CountedOn = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    PackSize = table.Column<int>(type: "INTEGER", nullable: true),
                    PrescriptionUntil = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    Notes = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Medicines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Medicines_People_PersonId",
                        column: x => x.PersonId,
                        principalTable: "People",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SickLeaves",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PersonId = table.Column<int>(type: "INTEGER", nullable: true),
                    EmploymentId = table.Column<int>(type: "INTEGER", nullable: true),
                    From = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    To = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    IsAccident = table.Column<bool>(type: "INTEGER", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Informed = table.Column<bool>(type: "INTEGER", nullable: false),
                    CertificateSent = table.Column<bool>(type: "INTEGER", nullable: false),
                    Notes = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SickLeaves", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SickLeaves_Employments_EmploymentId",
                        column: x => x.EmploymentId,
                        principalTable: "Employments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_SickLeaves_People_PersonId",
                        column: x => x.PersonId,
                        principalTable: "People",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Employments_PersonId",
                table: "Employments",
                column: "PersonId");

            migrationBuilder.CreateIndex(
                name: "IX_Medicines_PersonId",
                table: "Medicines",
                column: "PersonId");

            migrationBuilder.CreateIndex(
                name: "IX_SickLeaves_EmploymentId",
                table: "SickLeaves",
                column: "EmploymentId");

            migrationBuilder.CreateIndex(
                name: "IX_SickLeaves_PersonId",
                table: "SickLeaves",
                column: "PersonId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Medicines");

            migrationBuilder.DropTable(
                name: "SickLeaves");

            migrationBuilder.DropTable(
                name: "Employments");

            migrationBuilder.DropColumn(
                name: "AhvNumber",
                table: "People");

            migrationBuilder.DropColumn(
                name: "Allergies",
                table: "People");

            migrationBuilder.DropColumn(
                name: "BirthDate",
                table: "People");

            migrationBuilder.DropColumn(
                name: "BloodType",
                table: "People");

            migrationBuilder.DropColumn(
                name: "Conditions",
                table: "People");

            migrationBuilder.DropColumn(
                name: "DoctorName",
                table: "People");

            migrationBuilder.DropColumn(
                name: "DoctorPhone",
                table: "People");

            migrationBuilder.DropColumn(
                name: "Email",
                table: "People");

            migrationBuilder.DropColumn(
                name: "EmergencyName",
                table: "People");

            migrationBuilder.DropColumn(
                name: "EmergencyPhone",
                table: "People");

            migrationBuilder.DropColumn(
                name: "EmergencyRelation",
                table: "People");

            migrationBuilder.DropColumn(
                name: "Notes",
                table: "People");

            migrationBuilder.DropColumn(
                name: "Phone",
                table: "People");
        }
    }
}
