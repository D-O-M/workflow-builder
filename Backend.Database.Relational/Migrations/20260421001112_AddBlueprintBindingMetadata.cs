using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Backend.Database.Relational.Migrations
{
    /// <inheritdoc />
    public partial class AddBlueprintBindingMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DesignTimeInputOverridesJson",
                table: "BlueprintNodes",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PropertyMappingsJson",
                table: "BlueprintConnections",
                type: "nvarchar(max)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DesignTimeInputOverridesJson",
                table: "BlueprintNodes");

            migrationBuilder.DropColumn(
                name: "PropertyMappingsJson",
                table: "BlueprintConnections");
        }
    }
}
