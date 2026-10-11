using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kulvar.Api.Migrations
{
    /// <inheritdoc />
    public partial class InvitesOwnership : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "invites",
                schema: "app",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    email = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: false),
                    normalized_email = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: false),
                    role = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    token_hash = table.Column<byte[]>(type: "bytea", nullable: true),
                    sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    invited_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    accepted_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    accepted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_invites", x => x.id);
                    table.UniqueConstraint("ak_invites_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.CheckConstraint("ck_invites_role", "role IN ('Admin','Agent','Viewer')");
                    table.CheckConstraint("ck_invites_status", "status IN ('Pending','Accepted','Revoked')");
                    table.ForeignKey(
                        name: "fk_invites_membership_tenant_id_invited_by_user_id",
                        columns: x => new { x.tenant_id, x.invited_by_user_id },
                        principalSchema: "app",
                        principalTable: "memberships",
                        principalColumns: new[] { "tenant_id", "user_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_invites_tenant_tenant_id",
                        column: x => x.tenant_id,
                        principalSchema: "app",
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ownership_offers",
                schema: "app",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    from_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    to_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    demote_self = table.Column<bool>(type: "boolean", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    resolved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ownership_offers", x => x.id);
                    table.CheckConstraint("ck_ownership_offers_status", "status IN ('Pending','Accepted','Cancelled','Declined')");
                    table.ForeignKey(
                        name: "fk_ownership_offers_memberships_tenant_id_from_user_id",
                        columns: x => new { x.tenant_id, x.from_user_id },
                        principalSchema: "app",
                        principalTable: "memberships",
                        principalColumns: new[] { "tenant_id", "user_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_ownership_offers_memberships_tenant_id_to_user_id",
                        columns: x => new { x.tenant_id, x.to_user_id },
                        principalSchema: "app",
                        principalTable: "memberships",
                        principalColumns: new[] { "tenant_id", "user_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_ownership_offers_tenant_tenant_id",
                        column: x => x.tenant_id,
                        principalSchema: "app",
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_invites_tenant_id_invited_by_user_id",
                schema: "app",
                table: "invites",
                columns: new[] { "tenant_id", "invited_by_user_id" });

            migrationBuilder.CreateIndex(
                name: "ix_invites_tenant_id_normalized_email",
                schema: "app",
                table: "invites",
                columns: new[] { "tenant_id", "normalized_email" },
                unique: true,
                filter: "status = 'Pending'");

            migrationBuilder.CreateIndex(
                name: "ix_invites_token_hash",
                schema: "app",
                table: "invites",
                column: "token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_ownership_offers_tenant_id",
                schema: "app",
                table: "ownership_offers",
                column: "tenant_id",
                unique: true,
                filter: "status = 'Pending'");

            migrationBuilder.CreateIndex(
                name: "ix_ownership_offers_tenant_id_from_user_id",
                schema: "app",
                table: "ownership_offers",
                columns: new[] { "tenant_id", "from_user_id" });

            migrationBuilder.CreateIndex(
                name: "ix_ownership_offers_tenant_id_to_user_id",
                schema: "app",
                table: "ownership_offers",
                columns: new[] { "tenant_id", "to_user_id" });

            migrationBuilder.Sql(Sql.M0003.Up);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(Sql.M0003.DownBefore);

            migrationBuilder.DropTable(
                name: "invites",
                schema: "app");

            migrationBuilder.DropTable(
                name: "ownership_offers",
                schema: "app");

            migrationBuilder.Sql(Sql.M0003.DownAfter);
        }
    }
}
