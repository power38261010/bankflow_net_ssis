using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BankFlow.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddReadPageIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Transactions_AccountId",
                table: "Transactions");

            migrationBuilder.CreateIndex(
                name: "IX_Transactions_AccountId_CreatedAt_Id",
                table: "Transactions",
                columns: new[] { "AccountId", "CreatedAt", "Id" })
                .Annotation("SqlServer:Include", new[] { "Amount", "Type", "Description", "TransferId" });

            migrationBuilder.CreateIndex(
                name: "IX_Accounts_CreatedAt_Id",
                table: "Accounts",
                columns: new[] { "CreatedAt", "Id" })
                .Annotation("SqlServer:Include", new[] { "CustomerName", "AccountNumber", "Balance", "OwnerUserId" });

            migrationBuilder.CreateIndex(
                name: "IX_Accounts_OwnerUserId_CreatedAt_Id",
                table: "Accounts",
                columns: new[] { "OwnerUserId", "CreatedAt", "Id" })
                .Annotation("SqlServer:Include", new[] { "CustomerName", "AccountNumber", "Balance" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Transactions_AccountId_CreatedAt_Id",
                table: "Transactions");

            migrationBuilder.DropIndex(
                name: "IX_Accounts_CreatedAt_Id",
                table: "Accounts");

            migrationBuilder.DropIndex(
                name: "IX_Accounts_OwnerUserId_CreatedAt_Id",
                table: "Accounts");

            migrationBuilder.CreateIndex(
                name: "IX_Transactions_AccountId",
                table: "Transactions",
                column: "AccountId");
        }
    }
}
