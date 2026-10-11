using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kulvar.Api.Migrations
{
    /// <inheritdoc />
    public partial class BillingOperations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "billing_operations",
                schema: "app",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    merchant_reference = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    target_plan_version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    amount_minor = table.Column<long>(type: "bigint", nullable: false),
                    currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    epoch = table.Column<int>(type: "integer", nullable: false),
                    requested_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    simulated_outcome = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    next_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    failure_code = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    resolution_note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    resolved_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_billing_operations", x => x.id);
                    table.CheckConstraint("ck_billing_operations_status", "status IN ('Pending','Unknown','Succeeded','Declined','RefundPending','Refunded','Voided','ManuallyClosed','NeedsResolution')");
                    table.ForeignKey(
                        name: "fk_billing_operations_plan_version_target_plan_version_id",
                        column: x => x.target_plan_version_id,
                        principalSchema: "app",
                        principalTable: "plan_versions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "platform_audit_events",
                schema: "app",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    actor_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    action = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: true),
                    data = table.Column<string>(type: "jsonb", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_platform_audit_events", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "provider_events",
                schema: "app",
                columns: table => new
                {
                    provider_event_id = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    payload_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: true),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    next_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    processed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    note = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_provider_events", x => x.provider_event_id);
                    table.CheckConstraint("ck_provider_events_status", "status IN ('Received','Applied','Ignored','Stale','UnknownMapping','Conflict')");
                });

            migrationBuilder.CreateIndex(
                name: "ix_billing_operations_merchant_reference",
                schema: "app",
                table: "billing_operations",
                column: "merchant_reference",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_billing_operations_next_attempt_at",
                schema: "app",
                table: "billing_operations",
                column: "next_attempt_at",
                filter: "next_attempt_at IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_billing_operations_target_plan_version_id",
                schema: "app",
                table: "billing_operations",
                column: "target_plan_version_id");

            migrationBuilder.CreateIndex(
                name: "ix_billing_operations_tenant_id",
                schema: "app",
                table: "billing_operations",
                column: "tenant_id",
                unique: true,
                filter: "status IN ('Pending','Unknown')");

            migrationBuilder.CreateIndex(
                name: "ix_billing_operations_tenant_id_created_at",
                schema: "app",
                table: "billing_operations",
                columns: new[] { "tenant_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_platform_audit_events_occurred_at_id",
                schema: "app",
                table: "platform_audit_events",
                columns: new[] { "occurred_at", "id" },
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "ix_platform_audit_events_tenant_id",
                schema: "app",
                table: "platform_audit_events",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_provider_events_next_attempt_at",
                schema: "app",
                table: "provider_events",
                column: "next_attempt_at",
                filter: "next_attempt_at IS NOT NULL");

            migrationBuilder.Sql(Sql.M0005.Up);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(Sql.M0005.DownBefore);

            migrationBuilder.DropTable(
                name: "billing_operations",
                schema: "app");

            migrationBuilder.DropTable(
                name: "platform_audit_events",
                schema: "app");

            migrationBuilder.DropTable(
                name: "provider_events",
                schema: "app");

            migrationBuilder.Sql(Sql.M0005.DownAfter);
        }
    }
}
