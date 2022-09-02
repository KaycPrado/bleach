﻿using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Intersect.Server.Migrations.MySql.Game
{
    public partial class AddContentString : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ContentStrings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    Comment = table.Column<string>(type: "longtext", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ContentStrings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LocaleContentStrings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    Locale = table.Column<string>(type: "varchar(255)", nullable: false),
                    Value = table.Column<string>(type: "longtext", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LocaleContentStrings", x => new { x.Id, x.Locale });
                    table.ForeignKey(
                        name: "FK_LocaleContentStrings_ContentStrings_Id",
                        column: x => x.Id,
                        principalTable: "ContentStrings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LocaleContentStrings");

            migrationBuilder.DropTable(
                name: "ContentStrings");
        }
    }
}
