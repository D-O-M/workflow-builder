using Backend.Database.Relational;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Backend.Database.Relational.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(BlueprintDbContext))]
    [Migration("20260526120000_RemoveLegacyPhaseHandoffGlobalStateRows")]
    public partial class RemoveLegacyPhaseHandoffGlobalStateRows : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DELETE FROM PhasedBlueprintHandoffMappings
                WHERE SourceKind = 'GlobalState' OR SourceKind = '1';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
