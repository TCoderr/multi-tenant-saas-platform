using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kulvar.Api.Migrations
{
    /// <inheritdoc />
    public partial class CustomersSupport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "customers",
                schema: "app",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    company_title = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    email = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: true),
                    normalized_email = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: true),
                    phone = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    search_text = table.Column<string>(type: "text", nullable: false, computedColumnSql: "lower(name || ' ' || coalesce(company_title, '') || ' ' || coalesce(email, ''))", stored: true),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_customers", x => x.id);
                    table.UniqueConstraint("ak_customers_tenant_id_id", x => new { x.tenant_id, x.id });
                });

            migrationBuilder.CreateTable(
                name: "idempotency_keys",
                schema: "app",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    operation = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    request_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    response_status = table.Column<int>(type: "integer", nullable: false),
                    response_body = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_idempotency_keys", x => new { x.tenant_id, x.user_id, x.operation, x.key });
                });

            migrationBuilder.CreateTable(
                name: "tenant_counters",
                schema: "app",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    next_ticket_number = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tenant_counters", x => x.tenant_id);
                    table.ForeignKey(
                        name: "fk_tenant_counters_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalSchema: "app",
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "tickets",
                schema: "app",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<int>(type: "integer", nullable: false),
                    customer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "character varying(10000)", maxLength: 10000, nullable: false),
                    priority = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    assignee_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    resolved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    closed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    reopen_count = table.Column<int>(type: "integer", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tickets", x => x.id);
                    table.UniqueConstraint("ak_ticket_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.CheckConstraint("ck_tickets_priority", "priority IN ('Low','Normal','High','Urgent')");
                    table.CheckConstraint("ck_tickets_status", "status IN ('Open','InProgress','Resolved','Closed')");
                    table.ForeignKey(
                        name: "fk_tickets_customers_tenant_id_customer_id",
                        columns: x => new { x.tenant_id, x.customer_id },
                        principalSchema: "app",
                        principalTable: "customers",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_tickets_memberships_tenant_id_assignee_user_id",
                        columns: x => new { x.tenant_id, x.assignee_user_id },
                        principalSchema: "app",
                        principalTable: "memberships",
                        principalColumns: new[] { "tenant_id", "user_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_tickets_memberships_tenant_id_created_by_user_id",
                        columns: x => new { x.tenant_id, x.created_by_user_id },
                        principalSchema: "app",
                        principalTable: "memberships",
                        principalColumns: new[] { "tenant_id", "user_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ticket_comments",
                schema: "app",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    ticket_id = table.Column<Guid>(type: "uuid", nullable: false),
                    author_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    body = table.Column<string>(type: "character varying(5000)", maxLength: 5000, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ticket_comments", x => x.id);
                    table.ForeignKey(
                        name: "fk_ticket_comments_memberships_tenant_id_author_user_id",
                        columns: x => new { x.tenant_id, x.author_user_id },
                        principalSchema: "app",
                        principalTable: "memberships",
                        principalColumns: new[] { "tenant_id", "user_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_ticket_comments_ticket_tenant_id_ticket_id",
                        columns: x => new { x.tenant_id, x.ticket_id },
                        principalSchema: "app",
                        principalTable: "tickets",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_customers_tenant_id_created_at_id",
                schema: "app",
                table: "customers",
                columns: new[] { "tenant_id", "created_at", "id" },
                descending: new[] { false, true, true });

            migrationBuilder.CreateIndex(
                name: "ix_customers_tenant_id_name_id",
                schema: "app",
                table: "customers",
                columns: new[] { "tenant_id", "name", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_customers_tenant_id_normalized_email",
                schema: "app",
                table: "customers",
                columns: new[] { "tenant_id", "normalized_email" },
                unique: true,
                filter: "normalized_email IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_idempotency_keys_expires_at",
                schema: "app",
                table: "idempotency_keys",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "ix_ticket_comments_tenant_id_author_user_id",
                schema: "app",
                table: "ticket_comments",
                columns: new[] { "tenant_id", "author_user_id" });

            migrationBuilder.CreateIndex(
                name: "ix_ticket_comments_tenant_id_ticket_id_created_at_id",
                schema: "app",
                table: "ticket_comments",
                columns: new[] { "tenant_id", "ticket_id", "created_at", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_tickets_tenant_id_assignee_user_id_status",
                schema: "app",
                table: "tickets",
                columns: new[] { "tenant_id", "assignee_user_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_tickets_tenant_id_created_at_id",
                schema: "app",
                table: "tickets",
                columns: new[] { "tenant_id", "created_at", "id" },
                descending: new[] { false, true, true });

            migrationBuilder.CreateIndex(
                name: "ix_tickets_tenant_id_created_by_user_id",
                schema: "app",
                table: "tickets",
                columns: new[] { "tenant_id", "created_by_user_id" });

            migrationBuilder.CreateIndex(
                name: "ix_tickets_tenant_id_customer_id",
                schema: "app",
                table: "tickets",
                columns: new[] { "tenant_id", "customer_id" });

            migrationBuilder.CreateIndex(
                name: "ix_tickets_tenant_id_number",
                schema: "app",
                table: "tickets",
                columns: new[] { "tenant_id", "number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_tickets_tenant_id_status_created_at_id",
                schema: "app",
                table: "tickets",
                columns: new[] { "tenant_id", "status", "created_at", "id" },
                descending: new[] { false, false, true, true });

            migrationBuilder.CreateIndex(
                name: "ix_tickets_tenant_id_updated_at_id",
                schema: "app",
                table: "tickets",
                columns: new[] { "tenant_id", "updated_at", "id" },
                descending: new[] { false, true, true });

            migrationBuilder.Sql(Sql.M0002.Up);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(Sql.M0002.DownBefore);

            migrationBuilder.DropTable(
                name: "idempotency_keys",
                schema: "app");

            migrationBuilder.DropTable(
                name: "tenant_counters",
                schema: "app");

            migrationBuilder.DropTable(
                name: "ticket_comments",
                schema: "app");

            migrationBuilder.DropTable(
                name: "tickets",
                schema: "app");

            migrationBuilder.DropTable(
                name: "customers",
                schema: "app");

            migrationBuilder.Sql(Sql.M0002.DownAfter);
        }
    }
}
