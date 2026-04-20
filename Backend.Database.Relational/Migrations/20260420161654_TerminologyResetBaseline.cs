using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Backend.Database.Relational.Migrations
{
    /// <inheritdoc />
    public partial class TerminologyResetBaseline : Migration
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
                    Key = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(max)", nullable: false)
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
                    Path = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    State = table.Column<string>(type: "nvarchar(max)", nullable: false)
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
                    Discriminator = table.Column<string>(type: "nvarchar(34)", maxLength: 34, nullable: false)
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
                name: "BlueprintConnections",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceBlueprintNodeId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DestinationBlueprintNodeId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Anchor = table.Column<int>(type: "int", nullable: false),
                    Side = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Position = table.Column<int>(type: "int", nullable: false),
                    ChannelName = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BlueprintConnections", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "BlueprintNodes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BlueprintVersionId = table.Column<int>(type: "int", nullable: false),
                    TaskDefinitionId = table.Column<int>(type: "int", nullable: false),
                    AllowedMajor = table.Column<int>(type: "int", nullable: false),
                    AllowedMinor = table.Column<int>(type: "int", nullable: false),
                    PositionX = table.Column<int>(type: "int", nullable: false),
                    PositionY = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BlueprintNodes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BlueprintNodes_TaskDefinitions_TaskDefinitionId",
                        column: x => x.TaskDefinitionId,
                        principalTable: "TaskDefinitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
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
                    StartingBlueprintNodeId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
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
                    table.ForeignKey(
                        name: "FK_BlueprintVersions_BlueprintNodes_StartingBlueprintNodeId",
                        column: x => x.StartingBlueprintNodeId,
                        principalTable: "BlueprintNodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BlueprintConnections_DestinationBlueprintNodeId",
                table: "BlueprintConnections",
                column: "DestinationBlueprintNodeId");

            migrationBuilder.CreateIndex(
                name: "IX_BlueprintConnections_SourceBlueprintNodeId",
                table: "BlueprintConnections",
                column: "SourceBlueprintNodeId");

            migrationBuilder.CreateIndex(
                name: "IX_BlueprintDefinitions_Key",
                table: "BlueprintDefinitions",
                column: "Key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BlueprintNodes_BlueprintVersionId",
                table: "BlueprintNodes",
                column: "BlueprintVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_BlueprintNodes_TaskDefinitionId",
                table: "BlueprintNodes",
                column: "TaskDefinitionId");

            migrationBuilder.CreateIndex(
                name: "IX_BlueprintVersions_BlueprintDefinitionId_Version",
                table: "BlueprintVersions",
                columns: new[] { "BlueprintDefinitionId", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BlueprintVersions_StartingBlueprintNodeId",
                table: "BlueprintVersions",
                column: "StartingBlueprintNodeId");

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
                name: "IX_TaskDefinitions_Name",
                table: "TaskDefinitions",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TaskVersions_TaskDefinitionId_Version",
                table: "TaskVersions",
                columns: new[] { "TaskDefinitionId", "Version" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_BlueprintConnections_BlueprintNodes_DestinationBlueprintNodeId",
                table: "BlueprintConnections",
                column: "DestinationBlueprintNodeId",
                principalTable: "BlueprintNodes",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_BlueprintConnections_BlueprintNodes_SourceBlueprintNodeId",
                table: "BlueprintConnections",
                column: "SourceBlueprintNodeId",
                principalTable: "BlueprintNodes",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_BlueprintNodes_BlueprintVersions_BlueprintVersionId",
                table: "BlueprintNodes",
                column: "BlueprintVersionId",
                principalTable: "BlueprintVersions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_BlueprintVersions_BlueprintNodes_StartingBlueprintNodeId",
                table: "BlueprintVersions");

            migrationBuilder.DropTable(
                name: "BlueprintConnections");

            migrationBuilder.DropTable(
                name: "ChannelProperties");

            migrationBuilder.DropTable(
                name: "Channels");

            migrationBuilder.DropTable(
                name: "TaskVersions");

            migrationBuilder.DropTable(
                name: "BlueprintNodes");

            migrationBuilder.DropTable(
                name: "BlueprintVersions");

            migrationBuilder.DropTable(
                name: "TaskDefinitions");

            migrationBuilder.DropTable(
                name: "BlueprintDefinitions");
        }
    }
}
