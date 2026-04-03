#nullable disable

namespace uk.osric.copilot.Migrations {
    using Microsoft.EntityFrameworkCore.Migrations;

    public partial class AddEmailCertificates : Migration {
        protected override void Up(MigrationBuilder migrationBuilder) {
            migrationBuilder.CreateTable(
                name: "email_certificates",
                columns: table => new {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    email_address = table.Column<string>(type: "TEXT", nullable: false),
                    subject_dn = table.Column<string>(type: "TEXT", nullable: false),
                    serial_number = table.Column<string>(type: "TEXT", nullable: false),
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

            migrationBuilder.CreateIndex(
                name: "IX_email_certificates_email_address",
                table: "email_certificates",
                column: "email_address");
        }

        protected override void Down(MigrationBuilder migrationBuilder) {
            migrationBuilder.DropTable(name: "email_certificates");
        }
    }
}
