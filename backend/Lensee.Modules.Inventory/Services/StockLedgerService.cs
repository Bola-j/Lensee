using Lensee.Modules.Inventory.Data;
using Lensee.SharedKernel.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace Lensee.Modules.Inventory.Services;

public sealed class StockLedgerService
{
    private readonly InventoryDbContext _dbContext;
    private readonly IClock _clock;

    public StockLedgerService(InventoryDbContext dbContext, IClock clock)
    {
        _dbContext = dbContext;
        _clock = clock;
    }

    public async Task<InventoryBatch> ReceiveAsync(
        Guid locationId,
        Guid skuId,
        int quantity,
        Guid userId,
        string? lotNumber = null,
        DateOnly? expiryDate = null,
        string? notes = null,
        Guid? referenceOperationId = null,
        CancellationToken cancellationToken = default)
    {
        EnsurePositive(quantity, nameof(quantity));
        var now = _clock.EgyptNow;
        var batch = await FindBatchAsync(locationId, skuId, lotNumber, expiryDate, cancellationToken);
        if (batch is null)
        {
            batch = new InventoryBatch
            {
                Id = Guid.NewGuid(),
                LocationId = locationId,
                SkuId = skuId,
                LotNumber = NormalizeBlank(lotNumber),
                ExpiryDate = expiryDate,
                Quantity = 0,
                Notes = NormalizeBlank(notes),
                CreatedFrom = referenceOperationId,
                CreatedBy = userId,
                CreatedAt = now
            };
            _dbContext.InventoryBatches.Add(batch);
        }
        else if (!string.IsNullOrWhiteSpace(notes))
        {
            batch.Notes = notes.Trim();
        }

        batch.Quantity += quantity;
        var balance = await GetOrCreateBalanceAsync(locationId, skuId, cancellationToken);
        ApplyAvailableDelta(balance, quantity, now);
        AddTransaction(locationId, skuId, InventoryTransactionTypes.Receipt, quantity, userId, referenceOperationId, now);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return batch;
    }

    public async Task<IReadOnlyList<BatchAllocation>> IssueFefoAsync(
        Guid locationId,
        Guid skuId,
        int quantity,
        string transactionType,
        Guid userId,
        Guid? referenceOperationId = null,
        CancellationToken cancellationToken = default)
    {
        EnsurePositive(quantity, nameof(quantity));
        EnsureTransactionType(transactionType);
        if (transactionType is InventoryTransactionTypes.Receipt or InventoryTransactionTypes.ChangeOut or InventoryTransactionTypes.ReturnIn or InventoryTransactionTypes.SupplyIn)
        {
            throw new InvalidOperationException($"{transactionType} is not an issuing transaction type.");
        }

        var now = _clock.EgyptNow;
        var balance = await GetBalanceAsync(locationId, skuId, cancellationToken);
        if (balance is null || balance.AvailableQty < quantity)
        {
            throw new InvalidOperationException("Available stock is insufficient.");
        }

        var batches = await LoadFefoBatchesAsync(locationId, skuId, cancellationToken);
        var remaining = quantity;
        var allocations = new List<BatchAllocation>();
        foreach (var batch in batches)
        {
            if (remaining == 0)
            {
                break;
            }

            var allocated = Math.Min(batch.Quantity, remaining);
            batch.Quantity -= allocated;
            remaining -= allocated;
            allocations.Add(new BatchAllocation(batch.Id, allocated));
        }

        if (remaining > 0)
        {
            throw new InvalidOperationException("Batch stock is insufficient.");
        }

        ApplyAvailableDelta(balance, -quantity, now);
        AddTransaction(locationId, skuId, transactionType, -quantity, userId, referenceOperationId, now);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return allocations;
    }

    public async Task ReserveInWarehouseAsync(Guid locationId, Guid skuId, int quantity, Guid userId, Guid? referenceOperationId = null, CancellationToken cancellationToken = default)
    {
        EnsurePositive(quantity, nameof(quantity));
        var now = _clock.EgyptNow;
        var balance = await GetBalanceAsync(locationId, skuId, cancellationToken);
        if (balance is null || balance.AvailableQty < quantity)
        {
            throw new InvalidOperationException("Available stock is insufficient.");
        }

        ApplyAvailableDelta(balance, -quantity, now);
        balance.ReservedInWarehouseQty += quantity;
        AddTransaction(locationId, skuId, InventoryTransactionTypes.ReserveInWarehouse, -quantity, userId, referenceOperationId, now);
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task ReleaseInWarehouseAsync(Guid locationId, Guid skuId, int quantity, Guid userId, Guid? referenceOperationId = null, CancellationToken cancellationToken = default)
    {
        EnsurePositive(quantity, nameof(quantity));
        var now = _clock.EgyptNow;
        var balance = await GetBalanceAsync(locationId, skuId, cancellationToken);
        if (balance is null || balance.ReservedInWarehouseQty < quantity)
        {
            throw new InvalidOperationException("Reserved stock is insufficient.");
        }

        balance.ReservedInWarehouseQty -= quantity;
        ApplyAvailableDelta(balance, quantity, now);
        AddTransaction(locationId, skuId, InventoryTransactionTypes.ReserveReleaseInWarehouse, quantity, userId, referenceOperationId, now);
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task ReserveWithRepAsync(Guid locationId, Guid skuId, int quantity, Guid userId, Guid? referenceOperationId = null, CancellationToken cancellationToken = default)
    {
        EnsurePositive(quantity, nameof(quantity));
        var now = _clock.EgyptNow;
        var balance = await GetBalanceAsync(locationId, skuId, cancellationToken);
        if (balance is null || balance.AvailableQty < quantity)
        {
            throw new InvalidOperationException("Available stock is insufficient.");
        }

        ApplyAvailableDelta(balance, -quantity, now);
        balance.ReservedWithRepQty += quantity;
        AddTransaction(locationId, skuId, InventoryTransactionTypes.ReserveWithRep, -quantity, userId, referenceOperationId, now);
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task ReleaseWithRepAsync(Guid locationId, Guid skuId, int quantity, Guid userId, Guid? referenceOperationId = null, CancellationToken cancellationToken = default)
    {
        EnsurePositive(quantity, nameof(quantity));
        var now = _clock.EgyptNow;
        var balance = await GetBalanceAsync(locationId, skuId, cancellationToken);
        if (balance is null || balance.ReservedWithRepQty < quantity)
        {
            throw new InvalidOperationException("Reserved stock is insufficient.");
        }

        balance.ReservedWithRepQty -= quantity;
        ApplyAvailableDelta(balance, quantity, now);
        AddTransaction(locationId, skuId, InventoryTransactionTypes.ReserveReleaseWithRep, quantity, userId, referenceOperationId, now);
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task<InventoryBatch?> FindBatchAsync(Guid locationId, Guid skuId, string? lotNumber, DateOnly? expiryDate, CancellationToken cancellationToken) =>
        await _dbContext.InventoryBatches.FirstOrDefaultAsync(batch =>
            batch.LocationId == locationId &&
            batch.SkuId == skuId &&
            batch.LotNumber == NormalizeBlank(lotNumber) &&
            batch.ExpiryDate == expiryDate,
            cancellationToken);

    private async Task<List<InventoryBatch>> LoadFefoBatchesAsync(Guid locationId, Guid skuId, CancellationToken cancellationToken) =>
        await _dbContext.InventoryBatches
            .Where(batch => batch.LocationId == locationId && batch.SkuId == skuId && batch.Quantity > 0)
            .OrderBy(batch => batch.ExpiryDate == null)
            .ThenBy(batch => batch.ExpiryDate)
            .ThenBy(batch => batch.CreatedAt)
            .ToListAsync(cancellationToken);

    private async Task<StockBalance?> GetBalanceAsync(Guid locationId, Guid skuId, CancellationToken cancellationToken) =>
        await _dbContext.StockBalances.FirstOrDefaultAsync(balance => balance.LocationId == locationId && balance.SkuId == skuId, cancellationToken);

    private async Task<StockBalance> GetOrCreateBalanceAsync(Guid locationId, Guid skuId, CancellationToken cancellationToken)
    {
        var balance = await GetBalanceAsync(locationId, skuId, cancellationToken);
        if (balance is not null)
        {
            return balance;
        }

        balance = new StockBalance
        {
            Id = Guid.NewGuid(),
            LocationId = locationId,
            SkuId = skuId,
            AvailableQty = 0,
            ReservedInWarehouseQty = 0,
            ReservedWithRepQty = 0,
            RowVersion = 0,
            LastUpdated = _clock.EgyptNow
        };
        _dbContext.StockBalances.Add(balance);
        return balance;
    }

    private static void ApplyAvailableDelta(StockBalance balance, int delta, DateTime now)
    {
        var nextAvailable = balance.AvailableQty + delta;
        if (nextAvailable < 0)
        {
            throw new InvalidOperationException("Available stock cannot be negative.");
        }

        if (balance.ReservedInWarehouseQty < 0 || balance.ReservedWithRepQty < 0)
        {
            throw new InvalidOperationException("Reserved stock cannot be negative.");
        }

        balance.AvailableQty = nextAvailable;
        balance.RowVersion++;
        balance.LastUpdated = now;
    }

    private void AddTransaction(Guid locationId, Guid skuId, string transactionType, int quantityChange, Guid userId, Guid? referenceOperationId, DateTime now)
    {
        EnsureTransactionType(transactionType);
        _dbContext.StockTransactions.Add(new StockTransaction
        {
            Id = Guid.NewGuid(),
            LocationId = locationId,
            SkuId = skuId,
            TransactionType = transactionType,
            QuantityChange = quantityChange,
            ReferenceOperationId = referenceOperationId,
            UserId = userId,
            CreatedAt = now
        });
    }

    private static void EnsurePositive(int value, string name)
    {
        if (value <= 0)
        {
            throw new ArgumentOutOfRangeException(name, "Quantity must be greater than zero.");
        }
    }

    private static void EnsureTransactionType(string transactionType)
    {
        if (!InventoryTransactionTypes.IsValid(transactionType))
        {
            throw new InvalidOperationException($"Unsupported inventory transaction type: {transactionType}.");
        }
    }

    private static string? NormalizeBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed record BatchAllocation(Guid BatchId, int Quantity);
