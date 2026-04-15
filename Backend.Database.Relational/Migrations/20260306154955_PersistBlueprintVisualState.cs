using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Backend.Database.Relational.Migrations
{
    /// <inheritdoc />
    public partial class PersistBlueprintVisualState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "PositionX",
                table: "TaskInstances",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "PositionY",
                table: "TaskInstances",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Anchor",
                table: "TaskConnections",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Position",
                table: "TaskConnections",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "Side",
                table: "TaskConnections",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PositionX",
                table: "TaskInstances");

            migrationBuilder.DropColumn(
                name: "PositionY",
                table: "TaskInstances");

            migrationBuilder.DropColumn(
                name: "Anchor",
                table: "TaskConnections");

            migrationBuilder.DropColumn(
                name: "Position",
                table: "TaskConnections");

            migrationBuilder.DropColumn(
                name: "Side",
                table: "TaskConnections");
        }
    }
}
