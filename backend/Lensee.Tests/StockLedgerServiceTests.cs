using Lensee.Modules.Inventory.Data;
using Lensee.Modules.Inventory.Services;
using Lensee.SharedKernel.Abstractions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Lensee.Tests;

public sealed class StockLedgerServiceTests
{
    [Fact]
    public async Task ReceiveAsync_CreatesBatchBalanceAndReceiptTransaction_InPacks()
    {
        await using var dbContext = CreateContext();
        var service = new StockLedgerService(dbContext, new FixedClock());
        var locationId = Guid.NewGuid();
        var skuId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        var batch = await service.ReceiveAsync(locationId, skuId, 12, userId, "LOT-1", new DateOnly(2028, 6, 1));

        var balance = await dbContext.StockBalances.SingleAsync();
        var transaction = await dbContext.StockTransactions.SingleAsync();
        Assert.Equal(12, batch.Quantity);
        Assert.Equal(12, balance.AvailableQty);
        Assert.Equal(1, balance.RowVersion);
        Assert.Equal(InventoryTransactionTypes.Receipt, transaction.TransactionType);
        Assert.Equal(12, transaction.QuantityChange);
    }

    [Fact]
    public async Task IssueFefoAsync_AllocatesPacksEarliestExpiryFirst_AndNullExpiryLast()
    {
        await using var dbContext = CreateContext();
        var service = new StockLedgerService(dbContext, new FixedClock());
        var locationId = Guid.NewGuid();
        var skuId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var nullExpiry = await service.ReceiveAsync(locationId, skuId, 5, userId, "NULL", null);
        var late = await service.ReceiveAsync(locationId, skuId, 5, userId, "LATE", new DateOnly(2029, 1, 1));
        var early = await service.ReceiveAsync(locationId, skuId, 5, userId, "EARLY", new DateOnly(2028, 1, 1));

        var allocations = await service.IssueFefoAsync(locationId, skuId, 11, InventoryTransactionTypes.Sale, userId);

        Assert.Equal(new[] { early.Id, late.Id, nullExpiry.Id }, allocations.Select(value => value.BatchId));
        Assert.Equal(new[] { 5, 5, 1 }, allocations.Select(value => value.Quantity));
        Assert.Equal(4, (await dbContext.InventoryBatches.SingleAsync(batch => batch.Id == nullExpiry.Id)).Quantity);
        Assert.Equal(4, (await dbContext.StockTransactions.CountAsync()));
        Assert.Equal(4, (await dbContext.StockBalances.SingleAsync()).RowVersion);
    }

    [Fact]
    public async Task IssueFefoAsync_RejectsInsufficientBatchQuantity()
    {
        await using var dbContext = CreateContext();
        var service = new StockLedgerService(dbContext, new FixedClock());
        var locationId = Guid.NewGuid();
        var skuId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        await service.ReceiveAsync(locationId, skuId, 2, userId);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.IssueFefoAsync(locationId, skuId, 3, InventoryTransactionTypes.Sale, userId));
    }

    [Fact]
    public async Task ReserveAndRelease_DoNotAllowNegativeQuantities()
    {
        await using var dbContext = CreateContext();
        var service = new StockLedgerService(dbContext, new FixedClock());
        var locationId = Guid.NewGuid();
        var skuId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        await service.ReceiveAsync(locationId, skuId, 5, userId);
        await service.ReserveInWarehouseAsync(locationId, skuId, 3, userId);
        await service.ReleaseInWarehouseAsync(locationId, skuId, 2, userId);

        var balance = await dbContext.StockBalances.SingleAsync();
        Assert.Equal(4, balance.AvailableQty);
        Assert.Equal(1, balance.ReservedInWarehouseQty);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ReleaseInWarehouseAsync(locationId, skuId, 2, userId));
    }

    [Fact]
    public async Task IssueFefoAsync_RejectsInvalidTransactionType()
    {
        await using var dbContext = CreateContext();
        var service = new StockLedgerService(dbContext, new FixedClock());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.IssueFefoAsync(Guid.NewGuid(), Guid.NewGuid(), 1, "Manual", Guid.NewGuid()));
    }

    private static InventoryDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<InventoryDbContext>()
            .UseInMemoryDatabase($"ledger-{Guid.NewGuid()}")
            .Options;
        return new InventoryDbContext(options);
    }
}

internal sealed class FixedClock : IClock
{
    public DateTime UtcNow { get; } = new(2026, 6, 25, 10, 0, 0);

    public DateTime EgyptNow { get; } = new(2026, 6, 25, 13, 0, 0);
}
