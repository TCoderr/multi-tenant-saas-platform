using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kulvar.Api.Migrations
{
    /// <inheritdoc />
    public partial class BillingEntitlements : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "plans",
                schema: "app",
                columns: table => new
                {
                    code = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    name = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_plans", x => x.code);
                });

            migrationBuilder.CreateTable(
                name: "usage_counters",
                schema: "app",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    metric = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    period_start = table.Column<DateOnly>(type: "date", nullable: false),
                    used = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_usage_counters", x => new { x.tenant_id, x.metric, x.period_start });
                    table.CheckConstraint("ck_usage_counters_used", "used >= 0");
                });

            migrationBuilder.CreateTable(
                name: "usage_events",
                schema: "app",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    metric = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    source_id = table.Column<Guid>(type: "uuid", nullable: false),
                    period_start = table.Column<DateOnly>(type: "date", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_usage_events", x => new { x.tenant_id, x.metric, x.source_id });
                });

            migrationBuilder.CreateTable(
                name: "plan_versions",
                schema: "app",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    plan_code = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    price_minor = table.Column<long>(type: "bigint", nullable: false),
                    currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                    seat_limit = table.Column<int>(type: "integer", nullable: false),
                    monthly_ticket_limit = table.Column<int>(type: "integer", nullable: false),
                    export_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    trial_days = table.Column<int>(type: "integer", nullable: true),
                    is_current = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_plan_versions", x => x.id);
                    table.CheckConstraint("ck_plan_versions_currency", "currency = 'TRY'");
                    table.CheckConstraint("ck_plan_versions_limits", "price_minor >= 0 AND seat_limit > 0 AND monthly_ticket_limit >= 0");
                    table.ForeignKey(
                        name: "fk_plan_versions_plans_plan_code",
                        column: x => x.plan_code,
                        principalSchema: "app",
                        principalTable: "plans",
                        principalColumn: "code",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "subscriptions",
                schema: "app",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    plan_version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    epoch = table.Column<int>(type: "integer", nullable: false),
                    trial_ends_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    grace_ends_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    current_period_end = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    provider_subscription_id = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    provider_sequence = table.Column<long>(type: "bigint", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_subscriptions", x => x.tenant_id);
                    table.CheckConstraint("ck_subscriptions_status", "status IN ('Trialing','Active','PastDue','Suspended','Cancelled')");
                    table.ForeignKey(
                        name: "fk_subscriptions_plan_versions_plan_version_id",
                        column: x => x.plan_version_id,
                        principalSchema: "app",
                        principalTable: "plan_versions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_plan_versions_plan_code",
                schema: "app",
                table: "plan_versions",
                column: "plan_code",
                unique: true,
                filter: "is_current");

            migrationBuilder.CreateIndex(
                name: "ix_plan_versions_plan_code_version",
                schema: "app",
                table: "plan_versions",
                columns: new[] { "plan_code", "version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_subscriptions_grace_ends_at",
                schema: "app",
                table: "subscriptions",
                column: "grace_ends_at",
                filter: "status = 'PastDue'");

            migrationBuilder.CreateIndex(
                name: "ix_subscriptions_plan_version_id",
                schema: "app",
                table: "subscriptions",
                column: "plan_version_id");

            migrationBuilder.CreateIndex(
                name: "ix_subscriptions_provider_subscription_id",
                schema: "app",
                table: "subscriptions",
                column: "provider_subscription_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_subscriptions_trial_ends_at",
                schema: "app",
                table: "subscriptions",
                column: "trial_ends_at",
                filter: "status = 'Trialing'");

            migrationBuilder.CreateIndex(
                name: "ix_usage_events_tenant_id_metric_period_start",
                schema: "app",
                table: "usage_events",
                columns: new[] { "tenant_id", "metric", "period_start" });

            migrationBuilder.Sql(Sql.M0004.Up);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(Sql.M0004.DownBefore);

            migrationBuilder.DropTable(
                name: "subscriptions",
                schema: "app");

            migrationBuilder.DropTable(
                name: "usage_counters",
                schema: "app");

            migrationBuilder.DropTable(
                name: "usage_events",
                schema: "app");

            migrationBuilder.DropTable(
                name: "plan_versions",
                schema: "app");

            migrationBuilder.DropTable(
                name: "plans",
                schema: "app");

            migrationBuilder.Sql(Sql.M0004.DownAfter);
        }
    }
}
