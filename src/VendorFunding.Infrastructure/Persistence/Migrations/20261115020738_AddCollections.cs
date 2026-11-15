using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VendorFunding.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCollections : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "VendorId",
                table: "JournalEntries",
                type: "int",
                nullable: true);

            // Entries posted before this column existed are all deal accruals, so their vendor is the deal's vendor.
            migrationBuilder.Sql("""
                UPDATE je SET je.VendorId = d.VendorId
                FROM JournalEntries je
                JOIN Deals d ON d.Id = je.DealId
                WHERE je.VendorId IS NULL;
                """);

            migrationBuilder.CreateTable(
                name: "VendorPayments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    VendorId = table.Column<int>(type: "int", nullable: false),
                    Reference = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    ReceivedDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Memo = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    JournalEntryId = table.Column<int>(type: "int", nullable: false),
                    RecordedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VendorPayments", x => x.Id);
                    table.CheckConstraint("CK_VendorPayments_Amount", "[Amount] > 0");
                    table.ForeignKey(
                        name: "FK_VendorPayments_JournalEntries_JournalEntryId",
                        column: x => x.JournalEntryId,
                        principalTable: "JournalEntries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_VendorPayments_Vendors_VendorId",
                        column: x => x.VendorId,
                        principalTable: "Vendors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_JournalEntries_VendorId_PostingDate",
                table: "JournalEntries",
                columns: new[] { "VendorId", "PostingDate" });

            migrationBuilder.CreateIndex(
                name: "IX_VendorPayments_JournalEntryId",
                table: "VendorPayments",
                column: "JournalEntryId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_VendorPayments_VendorId_Reference",
                table: "VendorPayments",
                columns: new[] { "VendorId", "Reference" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_JournalEntries_Vendors_VendorId",
                table: "JournalEntries",
                column: "VendorId",
                principalTable: "Vendors",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_JournalEntries_Vendors_VendorId",
                table: "JournalEntries");

            migrationBuilder.DropTable(
                name: "VendorPayments");

            migrationBuilder.DropIndex(
                name: "IX_JournalEntries_VendorId_PostingDate",
                table: "JournalEntries");

            migrationBuilder.DropColumn(
                name: "VendorId",
                table: "JournalEntries");
        }
    }
}
