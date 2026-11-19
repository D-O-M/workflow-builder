using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Backend.Database.Relational.Migrations;

[DbContext(typeof(BlueprintDbContext))]
[Migration("20261106090000_AddConditionalBranch")]
public sealed class AddConditionalBranch : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<Guid>("PortableTaskKey", "BlueprintNodes", type: "uniqueidentifier", nullable: true);
        migrationBuilder.AddColumn<string>("ConditionalRuleJson", "BlueprintNodes", type: "nvarchar(max)", nullable: true);
        migrationBuilder.AddColumn<string>("ConditionalFieldsJson", "BlueprintNodes", type: "nvarchar(max)", nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn("PortableTaskKey", "BlueprintNodes");
        migrationBuilder.DropColumn("ConditionalRuleJson", "BlueprintNodes");
        migrationBuilder.DropColumn("ConditionalFieldsJson", "BlueprintNodes");
    }
}
