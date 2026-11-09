using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BankFlow.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddScheduleCreationIdempotency : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "BeneficiaryId",
                table: "ScheduledPayments",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "CreationKey",
                table: "ScheduledPayments",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<long>(
                name: "FirstRunAt",
                table: "ScheduledPayments",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.Sql("UPDATE [ScheduledPayments] SET [CreationKey] = NEWID(), [FirstRunAt] = [NextRunAt];");

            migrationBuilder.CreateIndex(
                name: "IX_ScheduledPayments_CreatedByUserId_CreationKey",
                table: "ScheduledPayments",
                columns: new[] { "CreatedByUserId", "CreationKey" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ScheduledPayments_CreatedByUserId_CreationKey",
                table: "ScheduledPayments");

            migrationBuilder.DropColumn(
                name: "BeneficiaryId",
                table: "ScheduledPayments");

            migrationBuilder.DropColumn(
                name: "CreationKey",
                table: "ScheduledPayments");

            migrationBuilder.DropColumn(
                name: "FirstRunAt",
                table: "ScheduledPayments");
        }
    }
}
