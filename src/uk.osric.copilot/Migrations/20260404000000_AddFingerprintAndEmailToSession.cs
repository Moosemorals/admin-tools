#nullable disable

namespace uk.osric.copilot.Migrations {
    using Microsoft.EntityFrameworkCore.Migrations;

    public partial class AddFingerprintAndEmailToSession : Migration {
        protected override void Up(MigrationBuilder migrationBuilder) {
            migrationBuilder.AddColumn<string>(
                name: "email_address",
                table: "sessions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.RenameColumn(
                name: "serial_number",
                table: "email_certificates",
                newName: "fingerprint");
        }

        protected override void Down(MigrationBuilder migrationBuilder) {
            migrationBuilder.DropColumn(
                name: "email_address",
                table: "sessions");

            migrationBuilder.RenameColumn(
                name: "fingerprint",
                table: "email_certificates",
                newName: "serial_number");
        }
    }
}
