using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MuteReborn.Migrations
{
    public partial class RenameTable : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GuildConfigs");

            migrationBuilder.CreateTable(
                name: "MuteRebornGuildConfigs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    GuildId = table.Column<ulong>(type: "INTEGER", nullable: false),
                    EnableMuteReborn = table.Column<bool>(type: "INTEGER", nullable: false),
                    BuyMuteRebornTicketCost = table.Column<int>(type: "INTEGER", nullable: false),
                    EachTicketIncreaseMuteTime = table.Column<int>(type: "INTEGER", nullable: false),
                    EachTicketDecreaseMuteTime = table.Column<int>(type: "INTEGER", nullable: false),
                    MaxIncreaseMuteTime = table.Column<int>(type: "INTEGER", nullable: false),
                    DateAdded = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MuteRebornGuildConfigs", x => x.Id);
                });
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MuteRebornGuildConfigs");

            migrationBuilder.CreateTable(
                name: "GuildConfigs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    BuyMuteRebornTicketCost = table.Column<int>(type: "INTEGER", nullable: false),
                    DateAdded = table.Column<DateTime>(type: "TEXT", nullable: true),
                    EachTicketDecreaseMuteTime = table.Column<int>(type: "INTEGER", nullable: false),
                    EachTicketIncreaseMuteTime = table.Column<int>(type: "INTEGER", nullable: false),
                    EnableMuteReborn = table.Column<bool>(type: "INTEGER", nullable: false),
                    GuildId = table.Column<ulong>(type: "INTEGER", nullable: false),
                    MaxIncreaseMuteTime = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GuildConfigs", x => x.Id);
                });
        }
    }
}
