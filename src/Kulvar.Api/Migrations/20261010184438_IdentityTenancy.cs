using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kulvar.Api.Migrations
{
    /// <inheritdoc />
    public partial class IdentityTenancy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "app");

            migrationBuilder.CreateTable(
                name: "audit_events",
                schema: "app",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    actor_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    action = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    target_type = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    target_id = table.Column<Guid>(type: "uuid", nullable: true),
                    ticket_id = table.Column<Guid>(type: "uuid", nullable: true),
                    data = table.Column<string>(type: "jsonb", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_audit_events", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "outbox_messages",
                schema: "app",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: true),
                    user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    available_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    lease_owner = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    lease_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    lease_token = table.Column<long>(type: "bigint", nullable: false),
                    last_error = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    processed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_outbox_messages", x => x.id);
                    table.CheckConstraint("ck_outbox_messages_status", "status IN ('Pending','Done','Dead')");
                });

            migrationBuilder.CreateTable(
                name: "sink_messages",
                schema: "app",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: true),
                    recipient_normalized_email = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: false),
                    kind = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    subject = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    body = table.Column<string>(type: "text", nullable: false),
                    action_path = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: true),
                    dedupe_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sink_messages", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "users",
                schema: "app",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    email = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: false),
                    normalized_email = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: false),
                    display_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    password_hash = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    email_verified_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    failed_login_count = table.Column<int>(type: "integer", nullable: false),
                    lockout_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_users", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "platform_operators",
                schema: "app",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_platform_operators", x => x.user_id);
                    table.ForeignKey(
                        name: "fk_platform_operators_user_user_id",
                        column: x => x.user_id,
                        principalSchema: "app",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "sessions",
                schema: "app",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sessions", x => x.id);
                    table.ForeignKey(
                        name: "fk_sessions_user_user_id",
                        column: x => x.user_id,
                        principalSchema: "app",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "tenants",
                schema: "app",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    suspended_reason = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    deletion_requested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    purge_after = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tenants", x => x.id);
                    table.CheckConstraint("ck_tenants_status", "status IN ('Active','Suspended','DeletionPending','Purging','Deleted')");
                    table.ForeignKey(
                        name: "fk_tenants_users_created_by_user_id",
                        column: x => x.created_by_user_id,
                        principalSchema: "app",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "user_tokens",
                schema: "app",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    purpose = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    token_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    consumed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_user_tokens", x => x.id);
                    table.ForeignKey(
                        name: "fk_user_tokens_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "app",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "memberships",
                schema: "app",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    removed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_memberships", x => new { x.tenant_id, x.user_id });
                    table.CheckConstraint("ck_memberships_role", "role IN ('Owner','Admin','Agent','Viewer')");
                    table.CheckConstraint("ck_memberships_status", "status IN ('Active','Removed')");
                    table.ForeignKey(
                        name: "fk_memberships_tenant_tenant_id",
                        column: x => x.tenant_id,
                        principalSchema: "app",
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_memberships_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "app",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_audit_events_tenant_id_occurred_at_id",
                schema: "app",
                table: "audit_events",
                columns: new[] { "tenant_id", "occurred_at", "id" },
                descending: new[] { false, true, true });

            migrationBuilder.CreateIndex(
                name: "ix_audit_events_tenant_id_ticket_id_occurred_at_id",
                schema: "app",
                table: "audit_events",
                columns: new[] { "tenant_id", "ticket_id", "occurred_at", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_memberships_user_id",
                schema: "app",
                table: "memberships",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_outbox_messages_available_at",
                schema: "app",
                table: "outbox_messages",
                column: "available_at",
                filter: "status = 'Pending'");

            migrationBuilder.CreateIndex(
                name: "ix_outbox_messages_tenant_id",
                schema: "app",
                table: "outbox_messages",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_sessions_user_id",
                schema: "app",
                table: "sessions",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_sink_messages_dedupe_key",
                schema: "app",
                table: "sink_messages",
                column: "dedupe_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_sink_messages_recipient_normalized_email_created_at",
                schema: "app",
                table: "sink_messages",
                columns: new[] { "recipient_normalized_email", "created_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_sink_messages_tenant_id",
                schema: "app",
                table: "sink_messages",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_tenants_created_by_user_id",
                schema: "app",
                table: "tenants",
                column: "created_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_tenants_purge_after",
                schema: "app",
                table: "tenants",
                column: "purge_after",
                filter: "status = 'DeletionPending'");

            migrationBuilder.CreateIndex(
                name: "ix_user_tokens_token_hash",
                schema: "app",
                table: "user_tokens",
                column: "token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_user_tokens_user_id",
                schema: "app",
                table: "user_tokens",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_users_normalized_email",
                schema: "app",
                table: "users",
                column: "normalized_email",
                unique: true);

            migrationBuilder.Sql(Sql.M0001.Up);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(Sql.M0001.DownBefore);

            migrationBuilder.DropTable(
                name: "audit_events",
                schema: "app");

            migrationBuilder.DropTable(
                name: "memberships",
                schema: "app");

            migrationBuilder.DropTable(
                name: "outbox_messages",
                schema: "app");

            migrationBuilder.DropTable(
                name: "platform_operators",
                schema: "app");

            migrationBuilder.DropTable(
                name: "sessions",
                schema: "app");

            migrationBuilder.DropTable(
                name: "sink_messages",
                schema: "app");

            migrationBuilder.DropTable(
                name: "user_tokens",
                schema: "app");

            migrationBuilder.DropTable(
                name: "tenants",
                schema: "app");

            migrationBuilder.DropTable(
                name: "users",
                schema: "app");

            migrationBuilder.Sql(Sql.M0001.DownAfter);
        }
    }
}
