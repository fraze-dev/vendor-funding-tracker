using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VendorFunding.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SalesRecords",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    StoreNumber = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    Sku = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    SaleDate = table.Column<DateOnly>(type: "date", nullable: false),
                    UnitsSold = table.Column<int>(type: "int", nullable: false),
                    NetSales = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SalesRecords", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Vendors",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    VendorNumber = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Vendors", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Deals",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    VendorId = table.Column<int>(type: "int", nullable: false),
                    DealNumber = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Type = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    StartDate = table.Column<DateOnly>(type: "date", nullable: false),
                    EndDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Sku = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    RatePerUnit = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    LumpSumAmount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Deals", x => x.Id);
                    table.CheckConstraint("CK_Deals_EndDate", "[EndDate] >= [StartDate]");
                    table.ForeignKey(
                        name: "FK_Deals_Vendors_VendorId",
                        column: x => x.VendorId,
                        principalTable: "Vendors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Deals_DealNumber",
                table: "Deals",
                column: "DealNumber",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Deals_VendorId",
                table: "Deals",
                column: "VendorId");

            migrationBuilder.CreateIndex(
                name: "IX_SalesRecords_Sku_SaleDate",
                table: "SalesRecords",
                columns: new[] { "Sku", "SaleDate" })
                .Annotation("SqlServer:Include", new[] { "UnitsSold" });

            migrationBuilder.CreateIndex(
                name: "IX_Vendors_VendorNumber",
                table: "Vendors",
                column: "VendorNumber",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Deals");

            migrationBuilder.DropTable(
                name: "SalesRecords");

            migrationBuilder.DropTable(
                name: "Vendors");
        }
    }
}
