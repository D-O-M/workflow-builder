using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Backend.Database.Relational.Migrations
{
    /// <inheritdoc />
    public partial class RemoveBlueprintVersionName : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Name",
                table: "BlueprintVersions");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Name",
                table: "BlueprintVersions",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");
        }
    }
}
