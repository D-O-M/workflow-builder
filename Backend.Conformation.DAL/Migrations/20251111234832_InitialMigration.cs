using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Backend.Relational.DAL.Migrations
{
    /// <inheritdoc />
    public partial class InitialMigration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TaskSchemas",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Path = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Version = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaskSchemas", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ChannelSchemas",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    TaskSchemaId = table.Column<int>(type: "int", nullable: true),
                    TaskSchemaId1 = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChannelSchemas", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ChannelSchemas_TaskSchemas_TaskSchemaId",
                        column: x => x.TaskSchemaId,
                        principalTable: "TaskSchemas",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_ChannelSchemas_TaskSchemas_TaskSchemaId1",
                        column: x => x.TaskSchemaId1,
                        principalTable: "TaskSchemas",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "ChannelParameterSchemas",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Type = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    ChannelId = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChannelParameterSchemas", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ChannelParameterSchemas_ChannelSchemas_ChannelId",
                        column: x => x.ChannelId,
                        principalTable: "ChannelSchemas",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ChannelParameterSchemas_ChannelId",
                table: "ChannelParameterSchemas",
                column: "ChannelId");

            migrationBuilder.CreateIndex(
                name: "IX_ChannelSchemas_TaskSchemaId",
                table: "ChannelSchemas",
                column: "TaskSchemaId");

            migrationBuilder.CreateIndex(
                name: "IX_ChannelSchemas_TaskSchemaId1",
                table: "ChannelSchemas",
                column: "TaskSchemaId1");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ChannelParameterSchemas");

            migrationBuilder.DropTable(
                name: "ChannelSchemas");

            migrationBuilder.DropTable(
                name: "TaskSchemas");
        }
    }
}
