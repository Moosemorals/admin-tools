#nullable disable

namespace uk.osric.copilot.Migrations {
    using Microsoft.EntityFrameworkCore.Migrations;

    /// <inheritdoc />
    public partial class AddMessages : Migration {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder) {
            migrationBuilder.CreateTable(
                name: "messages",
                columns: table => new {
                    id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    session_id = table.Column<string>(type: "TEXT", nullable: false),
                    event_type = table.Column<string>(type: "TEXT", nullable: false),
                    payload = table.Column<string>(type: "TEXT", nullable: false),
                    created_at = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table => {
                    table.PrimaryKey("PK_messages", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_messages_session_id",
                table: "messages",
                column: "session_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder) {
            migrationBuilder.DropTable(
                name: "messages");
        }
    }
}
