#nullable disable

namespace uk.osric.copilot.Migrations {
    using Microsoft.EntityFrameworkCore.Migrations;

    public partial class InitialSchema : Migration {
        protected override void Up(MigrationBuilder migrationBuilder) {
            migrationBuilder.CreateTable(
                name: "email_certificates",
                columns: table => new {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    email_address = table.Column<string>(type: "TEXT", nullable: false),
                    subject_dn = table.Column<string>(type: "TEXT", nullable: false),
                    fingerprint = table.Column<string>(type: "TEXT", nullable: false),
                    pfx_data = table.Column<byte[]>(type: "BLOB", nullable: false),
                    certificate_der = table.Column<byte[]>(type: "BLOB", nullable: false),
                    not_before = table.Column<string>(type: "TEXT", nullable: false),
                    not_after = table.Column<string>(type: "TEXT", nullable: false),
                    is_revoked = table.Column<bool>(type: "INTEGER", nullable: false),
                    created_at = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table => {
                    table.PrimaryKey("PK_email_certificates", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "sessions",
                columns: table => new {
                    id = table.Column<string>(type: "TEXT", nullable: false),
                    title = table.Column<string>(type: "TEXT", nullable: false),
                    created_at = table.Column<string>(type: "TEXT", nullable: false),
                    last_active_at = table.Column<string>(type: "TEXT", nullable: false),
                    working_directory = table.Column<string>(type: "TEXT", nullable: true),
                    email_address = table.Column<string>(type: "TEXT", nullable: true),
                    inbound_message_id = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table => {
                    table.PrimaryKey("PK_sessions", x => x.id);
                });

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
                name: "IX_email_certificates_email_address",
                table: "email_certificates",
                column: "email_address");

            migrationBuilder.CreateIndex(
                name: "IX_messages_session_id",
                table: "messages",
                column: "session_id");

            migrationBuilder.CreateTable(
                name: "imap_sync_state",
                columns: table => new {
                    id = table.Column<int>(type: "INTEGER", nullable: false),
                    uid_validity = table.Column<long>(type: "INTEGER", nullable: false),
                    highest_mod_seq = table.Column<long>(type: "INTEGER", nullable: false),
                    last_seen_uid = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table => {
                    table.PrimaryKey("PK_imap_sync_state", x => x.id);
                });
        }

        protected override void Down(MigrationBuilder migrationBuilder) {
            migrationBuilder.DropTable(name: "imap_sync_state");
            migrationBuilder.DropTable(name: "messages");
            migrationBuilder.DropTable(name: "sessions");
            migrationBuilder.DropTable(name: "email_certificates");
        }
    }
}
