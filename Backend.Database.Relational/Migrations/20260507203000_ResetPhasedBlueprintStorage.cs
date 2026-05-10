using System;
using Backend.Database.Relational;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Backend.Database.Relational.Migrations
{
    [DbContext(typeof(BlueprintDbContext))]
    [Migration("20260507203000_ResetPhasedBlueprintStorage")]
    public partial class ResetPhasedBlueprintStorage : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            DropPhasedBlueprintTablesIfTheyExist(migrationBuilder);

            migrationBuilder.CreateTable(
                name: "PhasedBlueprintDefinitions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Key = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PhasedBlueprintDefinitions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PhasedBlueprintVersions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    PhasedBlueprintDefinitionId = table.Column<int>(type: "int", nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false),
                    Description = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PhasedBlueprintVersions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PhasedBlueprintVersions_PhasedBlueprintDefinitions_PhasedBlueprintDefinitionId",
                        column: x => x.PhasedBlueprintDefinitionId,
                        principalTable: "PhasedBlueprintDefinitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PhasedBlueprintPhases",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PhasedBlueprintVersionId = table.Column<int>(type: "int", nullable: false),
                    PhaseIndex = table.Column<int>(type: "int", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    BlueprintDefinitionId = table.Column<int>(type: "int", nullable: false),
                    BlueprintVersionId = table.Column<int>(type: "int", nullable: true),
                    VersionPolicy = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PhasedBlueprintPhases", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PhasedBlueprintPhases_BlueprintDefinitions_BlueprintDefinitionId",
                        column: x => x.BlueprintDefinitionId,
                        principalTable: "BlueprintDefinitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PhasedBlueprintPhases_BlueprintVersions_BlueprintVersionId",
                        column: x => x.BlueprintVersionId,
                        principalTable: "BlueprintVersions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PhasedBlueprintPhases_PhasedBlueprintVersions_PhasedBlueprintVersionId",
                        column: x => x.PhasedBlueprintVersionId,
                        principalTable: "PhasedBlueprintVersions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PhasedBlueprintTransitions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PhasedBlueprintVersionId = table.Column<int>(type: "int", nullable: false),
                    SourcePhaseId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DestinationPhaseId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PhasedBlueprintTransitions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PhasedBlueprintTransitions_PhasedBlueprintPhases_DestinationPhaseId",
                        column: x => x.DestinationPhaseId,
                        principalTable: "PhasedBlueprintPhases",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_PhasedBlueprintTransitions_PhasedBlueprintPhases_SourcePhaseId",
                        column: x => x.SourcePhaseId,
                        principalTable: "PhasedBlueprintPhases",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_PhasedBlueprintTransitions_PhasedBlueprintVersions_PhasedBlueprintVersionId",
                        column: x => x.PhasedBlueprintVersionId,
                        principalTable: "PhasedBlueprintVersions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PhasedBlueprintCorrelationMappings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    PhasedBlueprintTransitionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceResultProperty = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    TriggerProperty = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PhasedBlueprintCorrelationMappings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PhasedBlueprintCorrelationMappings_PhasedBlueprintTransitions_PhasedBlueprintTransitionId",
                        column: x => x.PhasedBlueprintTransitionId,
                        principalTable: "PhasedBlueprintTransitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PhasedBlueprintHandoffMappings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    PhasedBlueprintTransitionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceKind = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    SourceBlueprintNodeId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SourceChannelName = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    SourceProperty = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    BridgePosition = table.Column<double>(type: "float", nullable: false),
                    TargetBlueprintNodeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TargetProperty = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PhasedBlueprintHandoffMappings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PhasedBlueprintHandoffMappings_PhasedBlueprintTransitions_PhasedBlueprintTransitionId",
                        column: x => x.PhasedBlueprintTransitionId,
                        principalTable: "PhasedBlueprintTransitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PhasedBlueprintCorrelationMappings_PhasedBlueprintTransitionId",
                table: "PhasedBlueprintCorrelationMappings",
                column: "PhasedBlueprintTransitionId");

            migrationBuilder.CreateIndex(
                name: "IX_PhasedBlueprintDefinitions_Key",
                table: "PhasedBlueprintDefinitions",
                column: "Key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PhasedBlueprintHandoffMappings_PhasedBlueprintTransitionId",
                table: "PhasedBlueprintHandoffMappings",
                column: "PhasedBlueprintTransitionId");

            migrationBuilder.CreateIndex(
                name: "IX_PhasedBlueprintPhases_BlueprintDefinitionId",
                table: "PhasedBlueprintPhases",
                column: "BlueprintDefinitionId");

            migrationBuilder.CreateIndex(
                name: "IX_PhasedBlueprintPhases_BlueprintVersionId",
                table: "PhasedBlueprintPhases",
                column: "BlueprintVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_PhasedBlueprintPhases_PhasedBlueprintVersionId_PhaseIndex",
                table: "PhasedBlueprintPhases",
                columns: new[] { "PhasedBlueprintVersionId", "PhaseIndex" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PhasedBlueprintTransitions_DestinationPhaseId",
                table: "PhasedBlueprintTransitions",
                column: "DestinationPhaseId");

            migrationBuilder.CreateIndex(
                name: "IX_PhasedBlueprintTransitions_PhasedBlueprintVersionId_DestinationPhaseId",
                table: "PhasedBlueprintTransitions",
                columns: new[] { "PhasedBlueprintVersionId", "DestinationPhaseId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PhasedBlueprintTransitions_PhasedBlueprintVersionId_SourcePhaseId",
                table: "PhasedBlueprintTransitions",
                columns: new[] { "PhasedBlueprintVersionId", "SourcePhaseId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PhasedBlueprintTransitions_SourcePhaseId",
                table: "PhasedBlueprintTransitions",
                column: "SourcePhaseId");

            migrationBuilder.CreateIndex(
                name: "IX_PhasedBlueprintVersions_PhasedBlueprintDefinitionId_Version",
                table: "PhasedBlueprintVersions",
                columns: new[] { "PhasedBlueprintDefinitionId", "Version" },
                unique: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // This development-only reset is intentionally destructive and does not restore wiped phase data.
        }

        private static void DropPhasedBlueprintTablesIfTheyExist(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                IF OBJECT_ID(N'[PhasedBlueprintCorrelationMappings]', N'U') IS NOT NULL DROP TABLE [PhasedBlueprintCorrelationMappings];
                IF OBJECT_ID(N'[PhasedBlueprintHandoffMappings]', N'U') IS NOT NULL DROP TABLE [PhasedBlueprintHandoffMappings];
                IF OBJECT_ID(N'[PhasedBlueprintTransitions]', N'U') IS NOT NULL DROP TABLE [PhasedBlueprintTransitions];
                IF OBJECT_ID(N'[PhasedBlueprintPhases]', N'U') IS NOT NULL DROP TABLE [PhasedBlueprintPhases];
                IF OBJECT_ID(N'[PhasedBlueprintVersions]', N'U') IS NOT NULL DROP TABLE [PhasedBlueprintVersions];
                IF OBJECT_ID(N'[PhasedBlueprintDefinitions]', N'U') IS NOT NULL DROP TABLE [PhasedBlueprintDefinitions];
                """);
        }
    }
}
