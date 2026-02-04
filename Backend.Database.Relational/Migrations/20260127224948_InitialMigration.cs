using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Backend.Database.Relational.Migrations
{
    /// <inheritdoc />
    public partial class InitialMigration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WorkflowBlueprints",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    StartingTaskId = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkflowBlueprints", x => x.Id);
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
                });

            migrationBuilder.CreateTable(
                name: "ChannelSchemas",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    TaskSchemaId = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChannelSchemas", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TaskSchemas",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Path = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Version = table.Column<string>(type: "nvarchar(11)", maxLength: 11, nullable: false),
                    InputChannelSpecificationId = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaskSchemas", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TaskSchemas_ChannelSchemas_InputChannelSpecificationId",
                        column: x => x.InputChannelSpecificationId,
                        principalTable: "ChannelSchemas",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "WorkflowTasks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SchemaId = table.Column<int>(type: "int", nullable: false),
                    BlueprintId = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkflowTasks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkflowTasks_TaskSchemas_SchemaId",
                        column: x => x.SchemaId,
                        principalTable: "TaskSchemas",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WorkflowTasks_WorkflowBlueprints_BlueprintId",
                        column: x => x.BlueprintId,
                        principalTable: "WorkflowBlueprints",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WorkflowTaskChannels",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SchemaId = table.Column<int>(type: "int", nullable: false),
                    SourceNodeId = table.Column<int>(type: "int", nullable: true),
                    DestinationNodeId = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkflowTaskChannels", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkflowTaskChannels_ChannelSchemas_SchemaId",
                        column: x => x.SchemaId,
                        principalTable: "ChannelSchemas",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WorkflowTaskChannels_WorkflowTasks_DestinationNodeId",
                        column: x => x.DestinationNodeId,
                        principalTable: "WorkflowTasks",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_WorkflowTaskChannels_WorkflowTasks_SourceNodeId",
                        column: x => x.SourceNodeId,
                        principalTable: "WorkflowTasks",
                        principalColumn: "Id");
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
                name: "IX_TaskSchemas_InputChannelSpecificationId",
                table: "TaskSchemas",
                column: "InputChannelSpecificationId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowTaskChannels_DestinationNodeId",
                table: "WorkflowTaskChannels",
                column: "DestinationNodeId",
                unique: true,
                filter: "[DestinationNodeId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowTaskChannels_SchemaId",
                table: "WorkflowTaskChannels",
                column: "SchemaId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowTaskChannels_SourceNodeId",
                table: "WorkflowTaskChannels",
                column: "SourceNodeId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowTasks_BlueprintId",
                table: "WorkflowTasks",
                column: "BlueprintId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowTasks_SchemaId",
                table: "WorkflowTasks",
                column: "SchemaId");

            migrationBuilder.AddForeignKey(
                name: "FK_ChannelParameterSchemas_ChannelSchemas_ChannelId",
                table: "ChannelParameterSchemas",
                column: "ChannelId",
                principalTable: "ChannelSchemas",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_ChannelSchemas_TaskSchemas_TaskSchemaId",
                table: "ChannelSchemas",
                column: "TaskSchemaId",
                principalTable: "TaskSchemas",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_TaskSchemas_ChannelSchemas_InputChannelSpecificationId",
                table: "TaskSchemas");

            migrationBuilder.DropTable(
                name: "ChannelParameterSchemas");

            migrationBuilder.DropTable(
                name: "WorkflowTaskChannels");

            migrationBuilder.DropTable(
                name: "WorkflowTasks");

            migrationBuilder.DropTable(
                name: "WorkflowBlueprints");

            migrationBuilder.DropTable(
                name: "ChannelSchemas");

            migrationBuilder.DropTable(
                name: "TaskSchemas");
        }
    }
}
