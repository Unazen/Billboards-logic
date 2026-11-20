using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Billboard.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Ownership : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "OwnerId",
                table: "Billboards",
                type: "uuid",
                nullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "Name",
                table: "BillboardGroups",
                type: "character varying(100)",
                maxLength: 100,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AddColumn<Guid>(
                name: "OwnerId",
                table: "BillboardGroups",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Billboards_OwnerId",
                table: "Billboards",
                column: "OwnerId");

            migrationBuilder.CreateIndex(
                name: "IX_BillboardGroups_OwnerId",
                table: "BillboardGroups",
                column: "OwnerId");

            migrationBuilder.AddForeignKey(
                name: "FK_BillboardGroups_AspNetUsers_OwnerId",
                table: "BillboardGroups",
                column: "OwnerId",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Billboards_AspNetUsers_OwnerId",
                table: "Billboards",
                column: "OwnerId",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_BillboardGroups_AspNetUsers_OwnerId",
                table: "BillboardGroups");

            migrationBuilder.DropForeignKey(
                name: "FK_Billboards_AspNetUsers_OwnerId",
                table: "Billboards");

            migrationBuilder.DropIndex(
                name: "IX_Billboards_OwnerId",
                table: "Billboards");

            migrationBuilder.DropIndex(
                name: "IX_BillboardGroups_OwnerId",
                table: "BillboardGroups");

            migrationBuilder.DropColumn(
                name: "OwnerId",
                table: "Billboards");

            migrationBuilder.DropColumn(
                name: "OwnerId",
                table: "BillboardGroups");

            migrationBuilder.AlterColumn<string>(
                name: "Name",
                table: "BillboardGroups",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(100)",
                oldMaxLength: 100);
        }
    }
}
