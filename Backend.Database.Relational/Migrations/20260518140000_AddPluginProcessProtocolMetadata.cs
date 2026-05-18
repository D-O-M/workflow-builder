using Backend.Database.Relational;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Backend.Database.Relational.Migrations
{
    [DbContext(typeof(BlueprintDbContext))]
    [Migration("20260518140000_AddPluginProcessProtocolMetadata")]
    public partial class AddPluginProcessProtocolMetadata : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AbiProcessProtocolVersion",
                table: "TaskVersions",
                type: "int",
                nullable: false,
                defaultValue: 0);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AbiProcessProtocolVersion",
                table: "TaskVersions");
        }
    }
}
