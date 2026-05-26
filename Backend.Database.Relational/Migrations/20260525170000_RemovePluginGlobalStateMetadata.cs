using Backend.Database.Relational;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Backend.Database.Relational.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(BlueprintDbContext))]
    [Migration("20260525170000_RemovePluginGlobalStateMetadata")]
    public partial class RemovePluginGlobalStateMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_Channels_TaskVersionId_GlobalState",
                table: "Channels");

            migrationBuilder.Sql(
                """
                DELETE FROM ChannelProperties
                WHERE ChannelId IN (
                    SELECT Id
                    FROM Channels
                    WHERE Discriminator = 'GlobalStateDefinition'
                );

                DELETE FROM Channels
                WHERE Discriminator = 'GlobalStateDefinition';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "UX_Channels_TaskVersionId_GlobalState",
                table: "Channels",
                column: "TaskVersionId",
                unique: true,
                filter: "[Discriminator] = 'GlobalStateDefinition'");
        }
    }
}
