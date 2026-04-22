using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Backend.Database.Relational.Migrations
{
    /// <inheritdoc />
    public partial class AddStructuredChannelPropertyMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "Type",
                table: "ChannelProperties",
                newName: "Kind");

            migrationBuilder.AddColumn<string>(
                name: "EnumValuesJson",
                table: "ChannelProperties",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsRequired",
                table: "ChannelProperties",
                type: "bit",
                nullable: false,
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EnumValuesJson",
                table: "ChannelProperties");

            migrationBuilder.DropColumn(
                name: "IsRequired",
                table: "ChannelProperties");

            migrationBuilder.RenameColumn(
                name: "Kind",
                table: "ChannelProperties",
                newName: "Type");
        }
    }
}
