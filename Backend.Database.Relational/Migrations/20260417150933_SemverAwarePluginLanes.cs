using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Backend.Database.Relational.Migrations
{
    /// <inheritdoc />
    public partial class SemverAwarePluginLanes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_TaskConnections_Channels_ChannelId",
                table: "TaskConnections");

            migrationBuilder.DropForeignKey(
                name: "FK_TaskInstances_TaskVersions_TaskVersionId",
                table: "TaskInstances");

            migrationBuilder.DropIndex(
                name: "IX_TaskConnections_ChannelId",
                table: "TaskConnections");

            migrationBuilder.DropIndex(
                name: "IX_TaskInstances_TaskVersionId",
                table: "TaskInstances");

            migrationBuilder.AddColumn<string>(
                name: "State",
                table: "TaskVersions",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "Registered");

            migrationBuilder.AddColumn<int>(
                name: "TaskDefinitionId",
                table: "TaskInstances",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "AllowedMajor",
                table: "TaskInstances",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "AllowedMinor",
                table: "TaskInstances",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "ChannelName",
                table: "TaskConnections",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.Sql(
                """
                UPDATE ti
                SET
                    ti.TaskDefinitionId = tv.TaskDefinitionId,
                    ti.AllowedMajor = COALESCE(TRY_CONVERT(int, PARSENAME(CONVERT(nvarchar(128), tv.[Version]), 3)), 0),
                    ti.AllowedMinor = COALESCE(TRY_CONVERT(int, PARSENAME(CONVERT(nvarchar(128), tv.[Version]), 2)), 0)
                FROM TaskInstances ti
                INNER JOIN TaskVersions tv ON tv.Id = ti.TaskVersionId;

                UPDATE tc
                SET tc.ChannelName = c.Name
                FROM TaskConnections tc
                INNER JOIN Channels c ON c.Id = tc.ChannelId;

                UPDATE TaskVersions
                SET State = 'Superseded';

                WITH RankedVersions AS
                (
                    SELECT
                        tv.Id,
                        ROW_NUMBER() OVER
                        (
                            PARTITION BY
                                tv.TaskDefinitionId,
                                COALESCE(TRY_CONVERT(int, PARSENAME(CONVERT(nvarchar(128), tv.[Version]), 3)), 0),
                                COALESCE(TRY_CONVERT(int, PARSENAME(CONVERT(nvarchar(128), tv.[Version]), 2)), 0)
                            ORDER BY
                                COALESCE(TRY_CONVERT(int, PARSENAME(CONVERT(nvarchar(128), tv.[Version]), 1)), 0) DESC,
                                tv.Id DESC
                        ) AS LaneRank
                    FROM TaskVersions tv
                )
                UPDATE tv
                SET tv.State = 'Active'
                FROM TaskVersions tv
                INNER JOIN RankedVersions rv ON rv.Id = tv.Id
                WHERE rv.LaneRank = 1;
                """);

            migrationBuilder.AlterColumn<int>(
                name: "TaskDefinitionId",
                table: "TaskInstances",
                type: "int",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "int",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "ChannelName",
                table: "TaskConnections",
                type: "nvarchar(max)",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_TaskInstances_TaskDefinitionId",
                table: "TaskInstances",
                column: "TaskDefinitionId");

            migrationBuilder.AddForeignKey(
                name: "FK_TaskInstances_TaskDefinitions_TaskDefinitionId",
                table: "TaskInstances",
                column: "TaskDefinitionId",
                principalTable: "TaskDefinitions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.DropColumn(
                name: "TaskVersionId",
                table: "TaskInstances");

            migrationBuilder.DropColumn(
                name: "ChannelId",
                table: "TaskConnections");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_TaskInstances_TaskDefinitions_TaskDefinitionId",
                table: "TaskInstances");

            migrationBuilder.DropIndex(
                name: "IX_TaskInstances_TaskDefinitionId",
                table: "TaskInstances");

            migrationBuilder.AddColumn<int>(
                name: "TaskVersionId",
                table: "TaskInstances",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ChannelId",
                table: "TaskConnections",
                type: "int",
                nullable: true);

            migrationBuilder.Sql(
                """
                UPDATE ti
                SET ti.TaskVersionId = resolved.Id
                FROM TaskInstances ti
                OUTER APPLY
                (
                    SELECT TOP (1) tv.Id
                    FROM TaskVersions tv
                    WHERE
                        tv.TaskDefinitionId = ti.TaskDefinitionId
                        AND COALESCE(TRY_CONVERT(int, PARSENAME(CONVERT(nvarchar(128), tv.[Version]), 3)), 0) = ti.AllowedMajor
                        AND COALESCE(TRY_CONVERT(int, PARSENAME(CONVERT(nvarchar(128), tv.[Version]), 2)), 0) = ti.AllowedMinor
                    ORDER BY
                        CASE tv.State
                            WHEN 'Active' THEN 0
                            WHEN 'Draining' THEN 1
                            WHEN 'Registered' THEN 2
                            ELSE 3
                        END,
                        COALESCE(TRY_CONVERT(int, PARSENAME(CONVERT(nvarchar(128), tv.[Version]), 1)), 0) DESC,
                        tv.Id DESC
                ) resolved;

                UPDATE tc
                SET tc.ChannelId = channelMatch.Id
                FROM TaskConnections tc
                INNER JOIN TaskInstances sourceTask ON sourceTask.Id = tc.SourceTaskInstanceId
                OUTER APPLY
                (
                    SELECT TOP (1) tv.Id
                    FROM TaskVersions tv
                    WHERE
                        tv.TaskDefinitionId = sourceTask.TaskDefinitionId
                        AND COALESCE(TRY_CONVERT(int, PARSENAME(CONVERT(nvarchar(128), tv.[Version]), 3)), 0) = sourceTask.AllowedMajor
                        AND COALESCE(TRY_CONVERT(int, PARSENAME(CONVERT(nvarchar(128), tv.[Version]), 2)), 0) = sourceTask.AllowedMinor
                    ORDER BY
                        CASE tv.State
                            WHEN 'Active' THEN 0
                            WHEN 'Draining' THEN 1
                            WHEN 'Registered' THEN 2
                            ELSE 3
                        END,
                        COALESCE(TRY_CONVERT(int, PARSENAME(CONVERT(nvarchar(128), tv.[Version]), 1)), 0) DESC,
                        tv.Id DESC
                ) resolvedVersion
                OUTER APPLY
                (
                    SELECT TOP (1) c.Id
                    FROM Channels c
                    WHERE c.TaskVersionId = resolvedVersion.Id AND c.Name = tc.ChannelName
                    ORDER BY c.Id
                ) channelMatch;
                """);

            migrationBuilder.AlterColumn<int>(
                name: "TaskVersionId",
                table: "TaskInstances",
                type: "int",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "int",
                oldNullable: true);

            migrationBuilder.AlterColumn<int>(
                name: "ChannelId",
                table: "TaskConnections",
                type: "int",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "int",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_TaskConnections_ChannelId",
                table: "TaskConnections",
                column: "ChannelId");

            migrationBuilder.CreateIndex(
                name: "IX_TaskInstances_TaskVersionId",
                table: "TaskInstances",
                column: "TaskVersionId");

            migrationBuilder.AddForeignKey(
                name: "FK_TaskConnections_Channels_ChannelId",
                table: "TaskConnections",
                column: "ChannelId",
                principalTable: "Channels",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_TaskInstances_TaskVersions_TaskVersionId",
                table: "TaskInstances",
                column: "TaskVersionId",
                principalTable: "TaskVersions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.DropColumn(
                name: "State",
                table: "TaskVersions");

            migrationBuilder.DropColumn(
                name: "AllowedMajor",
                table: "TaskInstances");

            migrationBuilder.DropColumn(
                name: "AllowedMinor",
                table: "TaskInstances");

            migrationBuilder.DropColumn(
                name: "TaskDefinitionId",
                table: "TaskInstances");

            migrationBuilder.DropColumn(
                name: "ChannelName",
                table: "TaskConnections");
        }
    }
}
