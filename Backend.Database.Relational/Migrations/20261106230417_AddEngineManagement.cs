using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Backend.Database.Relational.Migrations
{
    /// <inheritdoc />
    public partial class AddEngineManagement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "EngineFleetPolicies",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    Document = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EngineFleetPolicies", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "EngineManagementChanges",
                columns: table => new
                {
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    Actor = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Action = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Target = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    ChangedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EngineManagementChanges", x => x.Revision);
                });

            migrationBuilder.CreateTable(
                name: "EnginePolicyObservations",
                columns: table => new
                {
                    EngineId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    AppliedRevision = table.Column<long>(type: "bigint", nullable: true),
                    LastSeenAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    FailureCode = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    ServingVersionIdsJson = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EnginePolicyObservations", x => x.EngineId);
                });

            migrationBuilder.InsertData(
                table: "EngineFleetPolicies",
                columns: new[] { "Id", "Document", "Revision" },
                values: new object[] { 1, "{}", 0L });

            migrationBuilder.CreateIndex(
                name: "IX_EnginePolicyObservations_LastSeenAt",
                table: "EnginePolicyObservations",
                column: "LastSeenAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EngineFleetPolicies");

            migrationBuilder.DropTable(
                name: "EngineManagementChanges");

            migrationBuilder.DropTable(
                name: "EnginePolicyObservations");
        }
    }
}
