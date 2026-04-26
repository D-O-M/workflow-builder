using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Backend.Database.Relational.Migrations
{
    /// <inheritdoc />
    [Migration("20260425231000_RemoveLegacyGlobalStateStubMetadata")]
    public partial class RemoveLegacyGlobalStateStubMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DECLARE @LegacyStubChannels TABLE (Id int PRIMARY KEY);

                INSERT INTO @LegacyStubChannels (Id)
                SELECT c.Id
                FROM Channels AS c
                INNER JOIN ChannelProperties AS cp ON cp.ChannelId = c.Id
                WHERE c.Discriminator = 'GlobalStateDefinition'
                GROUP BY c.Id
                HAVING COUNT(*) = 1
                   AND SUM(CASE
                       WHEN cp.Name = 'Empty'
                        AND cp.Kind = 3
                        AND cp.IsRequired = 1
                       THEN 1 ELSE 0 END) = 1;

                DELETE FROM ChannelProperties
                WHERE ChannelId IN (SELECT Id FROM @LegacyStubChannels);

                DELETE FROM Channels
                WHERE Id IN (SELECT Id FROM @LegacyStubChannels);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Intentionally left blank: the legacy stub was synthetic metadata and cannot be
            // restored safely without reintroducing fake global-state requirements.
        }
    }
}
