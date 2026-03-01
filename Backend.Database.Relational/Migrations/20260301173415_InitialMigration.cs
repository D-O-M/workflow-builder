using System;
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
                name: "BlueprintDefinitions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Key = table.Column<string>(type: "nvarchar(450)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BlueprintDefinitions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TaskDefinitions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(450)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaskDefinitions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TaskVersions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Description = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    TaskDefinitionId = table.Column<int>(type: "int", nullable: false),
                    Version = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    Path = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaskVersions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TaskVersions_TaskDefinitions_TaskDefinitionId",
                        column: x => x.TaskDefinitionId,
                        principalTable: "TaskDefinitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Channels",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    TaskVersionId = table.Column<int>(type: "int", nullable: false),
                    Discriminator = table.Column<string>(type: "nvarchar(21)", maxLength: 21, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Channels", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Channels_TaskVersions_TaskVersionId",
                        column: x => x.TaskVersionId,
                        principalTable: "TaskVersions",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "ChannelProperties",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Type = table.Column<int>(type: "int", nullable: false),
                    ChannelId = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChannelProperties", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ChannelProperties_Channels_ChannelId",
                        column: x => x.ChannelId,
                        principalTable: "Channels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "BlueprintVersions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    BlueprintDefinitionId = table.Column<int>(type: "int", nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    StartingTaskInstanceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BlueprintVersions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BlueprintVersions_BlueprintDefinitions_BlueprintDefinitionId",
                        column: x => x.BlueprintDefinitionId,
                        principalTable: "BlueprintDefinitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TaskInstances",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BlueprintVersionId = table.Column<int>(type: "int", nullable: false),
                    TaskVersionId = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaskInstances", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TaskInstances_BlueprintVersions_BlueprintVersionId",
                        column: x => x.BlueprintVersionId,
                        principalTable: "BlueprintVersions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_TaskInstances_TaskVersions_TaskVersionId",
                        column: x => x.TaskVersionId,
                        principalTable: "TaskVersions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TaskConnections",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceTaskInstanceId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DestinationTaskInstanceId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ChannelId = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaskConnections", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TaskConnections_Channels_ChannelId",
                        column: x => x.ChannelId,
                        principalTable: "Channels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TaskConnections_TaskInstances_DestinationTaskInstanceId",
                        column: x => x.DestinationTaskInstanceId,
                        principalTable: "TaskInstances",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_TaskConnections_TaskInstances_SourceTaskInstanceId",
                        column: x => x.SourceTaskInstanceId,
                        principalTable: "TaskInstances",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_BlueprintDefinitions_Key",
                table: "BlueprintDefinitions",
                column: "Key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BlueprintVersions_BlueprintDefinitionId_Version",
                table: "BlueprintVersions",
                columns: new[] { "BlueprintDefinitionId", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BlueprintVersions_StartingTaskInstanceId",
                table: "BlueprintVersions",
                column: "StartingTaskInstanceId");

            migrationBuilder.CreateIndex(
                name: "IX_ChannelProperties_ChannelId",
                table: "ChannelProperties",
                column: "ChannelId");

            migrationBuilder.CreateIndex(
                name: "UX_Channels_TaskVersionId_GlobalState",
                table: "Channels",
                column: "TaskVersionId",
                unique: true,
                filter: "[Discriminator] = 'GlobalStateDefinition'");

            migrationBuilder.CreateIndex(
                name: "IX_TaskConnections_ChannelId",
                table: "TaskConnections",
                column: "ChannelId");

            migrationBuilder.CreateIndex(
                name: "IX_TaskConnections_DestinationTaskInstanceId",
                table: "TaskConnections",
                column: "DestinationTaskInstanceId");

            migrationBuilder.CreateIndex(
                name: "IX_TaskConnections_SourceTaskInstanceId",
                table: "TaskConnections",
                column: "SourceTaskInstanceId");

            migrationBuilder.CreateIndex(
                name: "IX_TaskDefinitions_Name",
                table: "TaskDefinitions",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TaskInstances_BlueprintVersionId",
                table: "TaskInstances",
                column: "BlueprintVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_TaskInstances_TaskVersionId",
                table: "TaskInstances",
                column: "TaskVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_TaskVersions_TaskDefinitionId_Version",
                table: "TaskVersions",
                columns: new[] { "TaskDefinitionId", "Version" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_BlueprintVersions_TaskInstances_StartingTaskInstanceId",
                table: "BlueprintVersions",
                column: "StartingTaskInstanceId",
                principalTable: "TaskInstances",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_BlueprintVersions_BlueprintDefinitions_BlueprintDefinitionId",
                table: "BlueprintVersions");

            migrationBuilder.DropForeignKey(
                name: "FK_BlueprintVersions_TaskInstances_StartingTaskInstanceId",
                table: "BlueprintVersions");

            migrationBuilder.DropTable(
                name: "ChannelProperties");

            migrationBuilder.DropTable(
                name: "TaskConnections");

            migrationBuilder.DropTable(
                name: "Channels");

            migrationBuilder.DropTable(
                name: "BlueprintDefinitions");

            migrationBuilder.DropTable(
                name: "TaskInstances");

            migrationBuilder.DropTable(
                name: "BlueprintVersions");

            migrationBuilder.DropTable(
                name: "TaskVersions");

            migrationBuilder.DropTable(
                name: "TaskDefinitions");
        }
    }
}
