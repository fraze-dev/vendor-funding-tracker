using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VendorFunding.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSalesImports : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ImportId",
                table: "SalesRecords",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "SalesImports",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FileName = table.Column<string>(type: "nvarchar(260)", maxLength: 260, nullable: false),
                    BlobName = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    ContentHash = table.Column<string>(type: "nchar(64)", fixedLength: true, maxLength: 64, nullable: true),
                    Status = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    ReceivedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    TotalRows = table.Column<int>(type: "int", nullable: false),
                    RejectedRows = table.Column<int>(type: "int", nullable: false),
                    LoadedRows = table.Column<int>(type: "int", nullable: false),
                    TotalBatches = table.Column<int>(type: "int", nullable: false),
                    RejectsBlobName = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    Error = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SalesImports", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SalesImportBatches",
                columns: table => new
                {
                    ImportId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BatchNumber = table.Column<int>(type: "int", nullable: false),
                    RowCount = table.Column<int>(type: "int", nullable: false),
                    LoadedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SalesImportBatches", x => new { x.ImportId, x.BatchNumber });
                    table.ForeignKey(
                        name: "FK_SalesImportBatches_SalesImports_ImportId",
                        column: x => x.ImportId,
                        principalTable: "SalesImports",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SalesRecords_ImportId",
                table: "SalesRecords",
                column: "ImportId");

            migrationBuilder.CreateIndex(
                name: "IX_SalesImports_BlobName",
                table: "SalesImports",
                column: "BlobName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SalesImports_ContentHash",
                table: "SalesImports",
                column: "ContentHash");

            migrationBuilder.CreateIndex(
                name: "IX_SalesImports_ReceivedAt",
                table: "SalesImports",
                column: "ReceivedAt");

            migrationBuilder.AddForeignKey(
                name: "FK_SalesRecords_SalesImports_ImportId",
                table: "SalesRecords",
                column: "ImportId",
                principalTable: "SalesImports",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_SalesRecords_SalesImports_ImportId",
                table: "SalesRecords");

            migrationBuilder.DropTable(
                name: "SalesImportBatches");

            migrationBuilder.DropTable(
                name: "SalesImports");

            migrationBuilder.DropIndex(
                name: "IX_SalesRecords_ImportId",
                table: "SalesRecords");

            migrationBuilder.DropColumn(
                name: "ImportId",
                table: "SalesRecords");
        }
    }
}
