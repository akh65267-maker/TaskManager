using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrderService.Migrations
{
    /// <inheritdoc />
    public partial class AddOrderSagaCreatedAtUtc : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // now(), not the generated DateTimeOffset.MinValue default. Any saga
            // already in flight when this is applied would otherwise be backdated
            // to 0001-01-01, read as long expired, and be cancelled by the very
            // first sweep - releasing stock and cancelling orders that were
            // progressing perfectly normally. Stamping existing rows with the
            // deployment time gives each of them a full timeout window instead.
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CreatedAtUtc",
                table: "OrderSagaStates",
                type: "timestamp with time zone",
                nullable: false,
                defaultValueSql: "now()");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CreatedAtUtc",
                table: "OrderSagaStates");
        }
    }
}
