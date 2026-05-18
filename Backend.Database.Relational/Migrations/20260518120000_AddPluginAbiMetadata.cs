using Backend.Database.Relational;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Backend.Database.Relational.Migrations
{
    [DbContext(typeof(BlueprintDbContext))]
    [Migration("20260518120000_AddPluginAbiMetadata")]
    public partial class AddPluginAbiMetadata : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AbiHydrationProtocolVersion",
                table: "TaskVersions",
                type: "int",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<string>(
                name: "AbiHarnessVersion",
                table: "TaskVersions",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "1.0.0");

            migrationBuilder.AddColumn<int>(
                name: "AbiInvocationEnvelopeSchemaVersion",
                table: "TaskVersions",
                type: "int",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "AbiResultSchemaVersion",
                table: "TaskVersions",
                type: "int",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<string>(
                name: "AbiSupportedPropertyKindsJson",
                table: "TaskVersions",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "[\"String\",\"Integer\",\"Decimal\",\"Boolean\",\"Guid\",\"Date\",\"DateTime\",\"Enum\"]");

            migrationBuilder.AddColumn<string>(
                name: "AbiSupportedStateStoreModesJson",
                table: "TaskVersions",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "[\"Cosmos\",\"File\"]");

            migrationBuilder.AddColumn<int>(
                name: "AbiVersionMajor",
                table: "TaskVersions",
                type: "int",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "AbiVersionMinor",
                table: "TaskVersions",
                type: "int",
                nullable: false,
                defaultValue: 0);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AbiHydrationProtocolVersion",
                table: "TaskVersions");

            migrationBuilder.DropColumn(
                name: "AbiHarnessVersion",
                table: "TaskVersions");

            migrationBuilder.DropColumn(
                name: "AbiInvocationEnvelopeSchemaVersion",
                table: "TaskVersions");

            migrationBuilder.DropColumn(
                name: "AbiResultSchemaVersion",
                table: "TaskVersions");

            migrationBuilder.DropColumn(
                name: "AbiSupportedPropertyKindsJson",
                table: "TaskVersions");

            migrationBuilder.DropColumn(
                name: "AbiSupportedStateStoreModesJson",
                table: "TaskVersions");

            migrationBuilder.DropColumn(
                name: "AbiVersionMajor",
                table: "TaskVersions");

            migrationBuilder.DropColumn(
                name: "AbiVersionMinor",
                table: "TaskVersions");
        }
    }
}
