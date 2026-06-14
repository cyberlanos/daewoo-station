using Content.Server.Database;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Content.Server.Database.Migrations.Postgres
{
    /// <inheritdoc />
    [DbContext(typeof(PostgresServerDbContext))]
    [Migration("20260613120001_PirateLoadoutNameDesc")]
    public partial class PirateLoadoutNameDesc : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "custom_name",
                table: "profile_loadout",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "custom_description",
                table: "profile_loadout",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "custom_name",
                table: "profile_loadout");

            migrationBuilder.DropColumn(
                name: "custom_description",
                table: "profile_loadout");
        }
    }
}
