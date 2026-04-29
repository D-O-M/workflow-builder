using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Backend.Database.Relational.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(BlueprintDbContext))]
    [Migration("20260429120000_AddCollectionIteratorSupport")]
    public partial class AddCollectionIteratorSupport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_BlueprintNodes_TaskDefinitions_TaskDefinitionId",
                table: "BlueprintNodes");

            migrationBuilder.AlterColumn<int>(
                name: "TaskDefinitionId",
                table: "BlueprintNodes",
                type: "int",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "int");

            migrationBuilder.AddColumn<string>(
                name: "CollectionIteratorItemKind",
                table: "BlueprintNodes",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NodeKind",
                table: "BlueprintNodes",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "PluginTask");

            migrationBuilder.AddColumn<bool>(
                name: "IsCollection",
                table: "ChannelProperties",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddForeignKey(
                name: "FK_BlueprintNodes_TaskDefinitions_TaskDefinitionId",
                table: "BlueprintNodes",
                column: "TaskDefinitionId",
                principalTable: "TaskDefinitions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_BlueprintNodes_TaskDefinitions_TaskDefinitionId",
                table: "BlueprintNodes");

            migrationBuilder.DropColumn(
                name: "CollectionIteratorItemKind",
                table: "BlueprintNodes");

            migrationBuilder.DropColumn(
                name: "NodeKind",
                table: "BlueprintNodes");

            migrationBuilder.DropColumn(
                name: "IsCollection",
                table: "ChannelProperties");

            migrationBuilder.AlterColumn<int>(
                name: "TaskDefinitionId",
                table: "BlueprintNodes",
                type: "int",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "int",
                oldNullable: true);

            migrationBuilder.AddForeignKey(
                name: "FK_BlueprintNodes_TaskDefinitions_TaskDefinitionId",
                table: "BlueprintNodes",
                column: "TaskDefinitionId",
                principalTable: "TaskDefinitions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
