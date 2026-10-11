using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kulvar.Api.Migrations
{
    /// <inheritdoc />
    public partial class ExportsLifecycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "export_jobs",
                schema: "app",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    requested_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    size_bytes = table.Column<long>(type: "bigint", nullable: true),
                    sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    download_token_hash = table.Column<byte[]>(type: "bytea", nullable: true),
                    download_token_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    lease_owner = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    lease_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    lease_token = table.Column<long>(type: "bigint", nullable: false),
                    failure_code = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_export_jobs", x => x.id);
                    table.CheckConstraint("ck_export_jobs_status", "status IN ('Queued','Running','Ready','Failed','Expired','Cancelled')");
                    table.ForeignKey(
                        name: "fk_export_jobs_memberships_tenant_id_requested_by_user_id",
                        columns: x => new { x.tenant_id, x.requested_by_user_id },
                        principalSchema: "app",
                        principalTable: "memberships",
                        principalColumns: new[] { "tenant_id", "user_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_export_jobs_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalSchema: "app",
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_export_jobs_created_at",
                schema: "app",
                table: "export_jobs",
                column: "created_at",
                filter: "status IN ('Queued','Running')");

            migrationBuilder.CreateIndex(
                name: "ix_export_jobs_expires_at",
                schema: "app",
                table: "export_jobs",
                column: "expires_at",
                filter: "status = 'Ready'");

            migrationBuilder.CreateIndex(
                name: "ix_export_jobs_tenant_id",
                schema: "app",
                table: "export_jobs",
                column: "tenant_id",
                unique: true,
                filter: "status IN ('Queued','Running')");

            migrationBuilder.CreateIndex(
                name: "ix_export_jobs_tenant_id_created_at",
                schema: "app",
                table: "export_jobs",
                columns: new[] { "tenant_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_export_jobs_tenant_id_requested_by_user_id",
                schema: "app",
                table: "export_jobs",
                columns: new[] { "tenant_id", "requested_by_user_id" });

            migrationBuilder.Sql(Sql.M0006.Up);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(Sql.M0006.DownBefore);

            migrationBuilder.DropTable(
                name: "export_jobs",
                schema: "app");

            migrationBuilder.Sql(Sql.M0006.DownAfter);
        }
    }
}
