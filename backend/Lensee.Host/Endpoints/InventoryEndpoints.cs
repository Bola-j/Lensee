using Lensee.Modules.Catalog.Data;
using Lensee.Modules.Inventory.Data;
using Lensee.Modules.Inventory.Services;
using Lensee.SharedKernel.Abstractions;
using Lensee.SharedKernel.Primitives;
using Lensee.SharedKernel.Security;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Lensee.Host.Endpoints;

public static class InventoryEndpoints
{
    public static RouteGroupBuilder MapInventoryEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/v1/inventory").WithTags("Inventory");

        group.MapGet("/locations", ListLocationsAsync).RequireAuthorization("inventory.read");
        group.MapGet("/stock-balances", ListStockBalancesAsync).RequireAuthorization("inventory.read");
        group.MapGet("/stock-balances/{locationId:guid}/{skuId:guid}", GetStockBalanceAsync).RequireAuthorization("inventory.read");
        group.MapPut("/stock-balances/{locationId:guid}/{skuId:guid}/target", SetTargetQuantityAsync).RequireAuthorization("inventory.write");
        group.MapGet("/batches", ListBatchesAsync).RequireAuthorization("inventory.read");
        group.MapGet("/transfer-blocked-batches", ListTransferBlockedBatchesAsync).RequireAuthorization("inventory.read");
        group.MapGet("/transactions", ListTransactionsAsync).RequireAuthorization("inventory.read");
        group.MapPost("/receipts", CreateReceiptAsync).RequireAuthorization("inventory.write");

        return group;
    }

    private static async Task<IResult> ListLocationsAsync(
        InventoryDbContext dbContext,
        ICurrentUser currentUser,
        CancellationToken cancellationToken)
    {
        var query = dbContext.Locations.AsQueryable();
        if (IsWarehouseClerk(currentUser))
        {
            if (currentUser.LocationId is not { } clerkLocationId)
            {
                return Results.Forbid();
            }

            query = query.Where(location => location.Id == clerkLocationId);
        }

        var locations = await query
            .OrderBy(location => location.Name)
            .Select(location => new LocationResponse(location.Id, location.Name, location.LocationType, location.IsActive))
            .ToListAsync(cancellationToken);

        return Results.Ok(locations);
    }

    private static async Task<IResult> ListStockBalancesAsync(
        Guid? locationId,
        Guid? skuId,
        bool? includeZeroStock,
        int? page,
        int? pageSize,
        InventoryDbContext inventoryDbContext,
        CatalogDbContext catalogDbContext,
        ICurrentUser currentUser,
        CancellationToken cancellationToken)
    {
        if (!TryResolveLocationScope(currentUser, locationId, out var scopedLocationId, out var forbidden))
        {
            return forbidden;
        }

        var request = new PageRequest(page ?? 1, pageSize ?? 25);
        var query = inventoryDbContext.StockBalances.Include(balance => balance.Location).AsQueryable();
        if (scopedLocationId.HasValue)
        {
            query = query.Where(balance => balance.LocationId == scopedLocationId.Value);
        }
        if (skuId.HasValue)
        {
            query = query.Where(balance => balance.SkuId == skuId.Value);
        }

        var rows = await query
            .OrderBy(balance => balance.Location.Name)
            .ThenBy(balance => balance.SkuId)
            .ToListAsync(cancellationToken);
        var skuLookup = await LoadSkuLookupAsync(catalogDbContext, rows.Select(row => row.SkuId), cancellationToken);
        var response = rows
            .Select(balance => ToResponse(balance, skuLookup))
            .ToList();

        if (includeZeroStock == true)
        {
            response.AddRange(await BuildZeroStockRowsAsync(
                inventoryDbContext,
                catalogDbContext,
                scopedLocationId,
                skuId,
                rows,
                cancellationToken));
        }

        var ordered = response
            .OrderBy(balance => balance.LocationName)
            .ThenBy(balance => balance.SkuCode ?? balance.SkuId.ToString())
            .ToList();
        var pageItems = ordered
            .Skip(request.Skip)
            .Take(request.PageSize)
            .ToList();

        return Results.Ok(new PagedResult<StockBalanceResponse>(pageItems, request.Page, request.PageSize, ordered.Count));
    }

    private static async Task<IResult> GetStockBalanceAsync(
        Guid locationId,
        Guid skuId,
        InventoryDbContext inventoryDbContext,
        CatalogDbContext catalogDbContext,
        ICurrentUser currentUser,
        CancellationToken cancellationToken)
    {
        if (!CanAccessLocation(currentUser, locationId))
        {
            return Results.Forbid();
        }

        var balance = await inventoryDbContext.StockBalances
            .Include(value => value.Location)
            .FirstOrDefaultAsync(value => value.LocationId == locationId && value.SkuId == skuId, cancellationToken);
        if (balance is null)
        {
            return Results.NotFound();
        }

        var skuLookup = await LoadSkuLookupAsync(catalogDbContext, [skuId], cancellationToken);
        return Results.Ok(ToResponse(balance, skuLookup));
    }

    private static async Task<IResult> SetTargetQuantityAsync(
        Guid locationId,
        Guid skuId,
        TargetQuantityRequest request,
        InventoryDbContext dbContext,
        IAuditLogWriter auditLogWriter,
        CancellationToken cancellationToken)
    {
        if (request.TargetPacks < 0)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                [nameof(request.TargetPacks)] = ["Target packs must be zero or greater."]
            });
        }

        var balance = await dbContext.StockBalances.FirstOrDefaultAsync(value => value.LocationId == locationId && value.SkuId == skuId, cancellationToken);
        if (balance is null)
        {
            return Results.NotFound();
        }

        balance.TargetQty = request.TargetPacks;
        await dbContext.SaveChangesAsync(cancellationToken);
        await auditLogWriter.WriteAsync("StockBalance", balance.Id, "SetTarget", new { balance.LocationId, balance.SkuId, balance.TargetQty }, cancellationToken: cancellationToken);

        return Results.NoContent();
    }

    private static async Task<IResult> ListBatchesAsync(
        Guid? locationId,
        Guid? skuId,
        bool? includeEmpty,
        int? page,
        int? pageSize,
        InventoryDbContext inventoryDbContext,
        CatalogDbContext catalogDbContext,
        ICurrentUser currentUser,
        CancellationToken cancellationToken)
    {
        if (!TryResolveLocationScope(currentUser, locationId, out var scopedLocationId, out var forbidden))
        {
            return forbidden;
        }

        var request = new PageRequest(page ?? 1, pageSize ?? 25);
        var query = inventoryDbContext.InventoryBatches.Include(batch => batch.Location).AsQueryable();
        if (scopedLocationId.HasValue)
        {
            query = query.Where(batch => batch.LocationId == scopedLocationId.Value);
        }
        if (skuId.HasValue)
        {
            query = query.Where(batch => batch.SkuId == skuId.Value);
        }
        if (includeEmpty != true)
        {
            query = query.Where(batch => batch.Quantity > 0);
        }

        var total = await query.CountAsync(cancellationToken);
        var rows = await query
            .OrderBy(batch => batch.ExpiryDate == null)
            .ThenBy(batch => batch.ExpiryDate)
            .ThenBy(batch => batch.Location.Name)
            .ThenBy(batch => batch.LotNumber)
            .Skip(request.Skip)
            .Take(request.PageSize)
            .ToListAsync(cancellationToken);
        var skuLookup = await LoadSkuLookupAsync(catalogDbContext, rows.Select(row => row.SkuId), cancellationToken);
        var response = rows.Select(batch => ToResponse(batch, skuLookup)).ToList();

        return Results.Ok(new PagedResult<InventoryBatchResponse>(response, request.Page, request.PageSize, total));
    }

    private static async Task<IResult> ListTransactionsAsync(
        Guid? locationId,
        Guid? skuId,
        int? page,
        int? pageSize,
        InventoryDbContext inventoryDbContext,
        CatalogDbContext catalogDbContext,
        ICurrentUser currentUser,
        CancellationToken cancellationToken)
    {
        if (!TryResolveLocationScope(currentUser, locationId, out var scopedLocationId, out var forbidden))
        {
            return forbidden;
        }

        var request = new PageRequest(page ?? 1, pageSize ?? 25);
        var query = inventoryDbContext.StockTransactions.Include(transaction => transaction.Location).AsQueryable();
        if (scopedLocationId.HasValue)
        {
            query = query.Where(transaction => transaction.LocationId == scopedLocationId.Value);
        }
        if (skuId.HasValue)
        {
            query = query.Where(transaction => transaction.SkuId == skuId.Value);
        }

        var total = await query.CountAsync(cancellationToken);
        var rows = await query
            .OrderByDescending(transaction => transaction.CreatedAt)
            .Skip(request.Skip)
            .Take(request.PageSize)
            .ToListAsync(cancellationToken);
        var skuLookup = await LoadSkuLookupAsync(catalogDbContext, rows.Select(row => row.SkuId), cancellationToken);
        var response = rows.Select(transaction => ToResponse(transaction, skuLookup)).ToList();

        return Results.Ok(new PagedResult<StockTransactionResponse>(response, request.Page, request.PageSize, total));
    }

    private static async Task<IResult> ListTransferBlockedBatchesAsync(
        Guid? locationId,
        Guid? skuId,
        InventoryDbContext inventoryDbContext,
        CatalogDbContext catalogDbContext,
        ICurrentUser currentUser,
        IClock clock,
        CancellationToken cancellationToken)
    {
        if (!TryResolveLocationScope(currentUser, locationId, out var scopedLocationId, out var forbidden))
        {
            return forbidden;
        }

        var query = inventoryDbContext.InventoryBatches
            .Include(batch => batch.Location)
            .Where(batch => batch.Quantity > 0 && batch.ExpiryDate != null)
            .AsQueryable();
        if (scopedLocationId.HasValue)
        {
            query = query.Where(batch => batch.LocationId == scopedLocationId.Value);
        }
        if (skuId.HasValue)
        {
            query = query.Where(batch => batch.SkuId == skuId.Value);
        }

        var batches = await query
            .OrderBy(batch => batch.ExpiryDate)
            .ThenBy(batch => batch.Location.Name)
            .ThenBy(batch => batch.LotNumber)
            .ToListAsync(cancellationToken);
        var skuIds = batches.Select(batch => batch.SkuId).Distinct().ToArray();
        var skuLookup = await catalogDbContext.Skus
            .Include(sku => sku.Product)
            .Where(sku => skuIds.Contains(sku.Id))
            .Select(sku => new
            {
                sku.Id,
                sku.SkuCode,
                ProductName = sku.Product.Name,
                sku.Product.PiecesPerPack,
                sku.Product.SellMode,
                sku.Product.OpenedExpiryDuration
            })
            .ToDictionaryAsync(sku => sku.Id, cancellationToken);

        var today = DateOnly.FromDateTime(clock.EgyptNow);
        var rows = new List<TransferBlockedBatchResponse>();
        foreach (var batch in batches)
        {
            if (!skuLookup.TryGetValue(batch.SkuId, out var sku))
            {
                continue;
            }

            var minimumExpiryDate = CalculateMinimumExpiryDate(today, sku.OpenedExpiryDuration);
            var isExpired = batch.ExpiryDate < today;
            var isTooShortForOpeningWindow = minimumExpiryDate.HasValue && batch.ExpiryDate < minimumExpiryDate.Value;
            if (!isExpired && !isTooShortForOpeningWindow)
            {
                continue;
            }

            var reason = isExpired
                ? "Expired"
                : "Shorter than valid-after-opening duration";
            rows.Add(new TransferBlockedBatchResponse(
                batch.Id,
                batch.LocationId,
                batch.Location.Name,
                batch.Location.LocationType,
                batch.SkuId,
                sku.SkuCode,
                sku.ProductName,
                batch.LotNumber,
                batch.ExpiryDate,
                batch.Quantity,
                ToPieces(batch.Quantity, new SkuLookup(sku.Id, sku.SkuCode, sku.ProductName, sku.PiecesPerPack, sku.SellMode), batch.Location.LocationType),
                sku.OpenedExpiryDuration,
                minimumExpiryDate,
                reason));
        }

        return Results.Ok(rows);
    }

    private static async Task<IResult> CreateReceiptAsync(
        InventoryReceiptRequest request,
        InventoryDbContext inventoryDbContext,
        CatalogDbContext catalogDbContext,
        StockLedgerService ledgerService,
        ICurrentUser currentUser,
        IAuditLogWriter auditLogWriter,
        CancellationToken cancellationToken)
    {
        var errors = ValidateReceipt(request);
        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        if (!await inventoryDbContext.Locations.AnyAsync(location => location.Id == request.LocationId && location.IsActive, cancellationToken))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                [nameof(request.LocationId)] = ["Location must exist and be active."]
            });
        }

        if (!await catalogDbContext.Skus.AnyAsync(sku => sku.Id == request.SkuId && sku.IsActive && sku.DeletedAt == null && sku.Product.IsActive && sku.Product.DeletedAt == null, cancellationToken))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                [nameof(request.SkuId)] = ["SKU must exist and be active."]
            });
        }

        var userId = currentUser.UserId ?? Guid.Empty;
        var batch = await ledgerService.ReceiveAsync(
            request.LocationId,
            request.SkuId,
            request.PackQuantity,
            userId,
            request.LotNumber,
            request.ExpiryDate,
            request.Notes,
            cancellationToken: cancellationToken);
        await auditLogWriter.WriteAsync("InventoryReceipt", batch.Id, "Create", new { request.LocationId, request.SkuId, request.PackQuantity, request.LotNumber, request.ExpiryDate }, request.PackQuantity, cancellationToken);

        return Results.Created($"/api/v1/inventory/batches/{batch.Id}", new InventoryReceiptResponse(batch.Id, batch.LocationId, batch.SkuId, batch.Quantity));
    }

    private static Dictionary<string, string[]> ValidateReceipt(InventoryReceiptRequest request)
    {
        var errors = new Dictionary<string, string[]>();
        if (request.LocationId == Guid.Empty)
        {
            errors[nameof(request.LocationId)] = ["Location is required."];
        }
        if (request.SkuId == Guid.Empty)
        {
            errors[nameof(request.SkuId)] = ["SKU is required."];
        }
        if (request.PackQuantity <= 0)
        {
            errors[nameof(request.PackQuantity)] = ["Pack quantity must be greater than zero."];
        }
        if (request.LotNumber?.Length > 100)
        {
            errors[nameof(request.LotNumber)] = ["Lot number must be 100 characters or fewer."];
        }

        return errors;
    }

    private static bool TryResolveLocationScope(ICurrentUser currentUser, Guid? requestedLocationId, out Guid? scopedLocationId, out IResult forbidden)
    {
        scopedLocationId = requestedLocationId;
        forbidden = Results.Forbid();
        if (!IsWarehouseClerk(currentUser))
        {
            return true;
        }

        if (currentUser.LocationId is not { } clerkLocationId)
        {
            return false;
        }

        if (requestedLocationId.HasValue && requestedLocationId.Value != clerkLocationId)
        {
            return false;
        }

        scopedLocationId = clerkLocationId;
        return true;
    }

    private static bool CanAccessLocation(ICurrentUser currentUser, Guid locationId) =>
        !IsWarehouseClerk(currentUser) || currentUser.LocationId == locationId;

    private static bool IsWarehouseClerk(ICurrentUser currentUser) =>
        string.Equals(currentUser.Role, LenseeRoles.WarehouseClerk, StringComparison.OrdinalIgnoreCase);

    private static async Task<Dictionary<Guid, SkuLookup>> LoadSkuLookupAsync(CatalogDbContext dbContext, IEnumerable<Guid> skuIds, CancellationToken cancellationToken)
    {
        var ids = skuIds.Distinct().ToArray();
        if (ids.Length == 0)
        {
            return [];
        }

        return await dbContext.Skus
            .Include(sku => sku.Product)
            .Where(sku => ids.Contains(sku.Id))
            .Select(sku => new SkuLookup(sku.Id, sku.SkuCode, sku.Product.Name, sku.Product.PiecesPerPack, sku.Product.SellMode))
            .ToDictionaryAsync(sku => sku.Id, cancellationToken);
    }

    private static async Task<IReadOnlyList<StockBalanceResponse>> BuildZeroStockRowsAsync(
        InventoryDbContext inventoryDbContext,
        CatalogDbContext catalogDbContext,
        Guid? locationId,
        Guid? skuId,
        IReadOnlyCollection<StockBalance> existingBalances,
        CancellationToken cancellationToken)
    {
        var existingKeys = existingBalances
            .Select(balance => (balance.LocationId, balance.SkuId))
            .ToHashSet();
        var locationsQuery = inventoryDbContext.Locations.Where(location => location.IsActive).AsQueryable();
        if (locationId.HasValue)
        {
            locationsQuery = locationsQuery.Where(location => location.Id == locationId.Value);
        }

        var locations = await locationsQuery.OrderBy(location => location.Name).ToListAsync(cancellationToken);
        var skuQuery = catalogDbContext.Skus
            .Include(sku => sku.Product)
            .Where(sku => sku.IsActive && sku.DeletedAt == null && sku.Product.IsActive && sku.Product.DeletedAt == null)
            .AsQueryable();
        if (skuId.HasValue)
        {
            skuQuery = skuQuery.Where(sku => sku.Id == skuId.Value);
        }

        var skus = await skuQuery
            .OrderBy(sku => sku.SkuCode)
            .Select(sku => new SkuLookup(sku.Id, sku.SkuCode, sku.Product.Name, sku.Product.PiecesPerPack, sku.Product.SellMode))
            .ToListAsync(cancellationToken);
        var rows = new List<StockBalanceResponse>();
        foreach (var location in locations)
        {
            foreach (var sku in skus)
            {
                if (existingKeys.Contains((location.Id, sku.Id)))
                {
                    continue;
                }

                rows.Add(ToZeroStockResponse(location, sku));
            }
        }

        return rows;
    }

    private static StockBalanceResponse ToZeroStockResponse(Location location, SkuLookup sku) =>
        new(
            location.Id,
            location.Name,
            location.LocationType,
            sku.Id,
            sku.SkuCode,
            sku.ProductName,
            sku.PiecesPerPack,
            sku.SellMode,
            0,
            ToPieces(0, sku, location.LocationType),
            0,
            ToPieces(0, sku, location.LocationType),
            0,
            ToPieces(0, sku, location.LocationType),
            null,
            null,
            0,
            null);

    private static StockBalanceResponse ToResponse(StockBalance balance, IReadOnlyDictionary<Guid, SkuLookup> skuLookup)
    {
        skuLookup.TryGetValue(balance.SkuId, out var sku);
        return new StockBalanceResponse(
            balance.LocationId,
            balance.Location.Name,
            balance.Location.LocationType,
            balance.SkuId,
            sku?.SkuCode,
            sku?.ProductName,
            sku?.PiecesPerPack,
            sku?.SellMode,
            balance.AvailableQty,
            ToPieces(balance.AvailableQty, sku, balance.Location.LocationType),
            balance.ReservedInWarehouseQty,
            ToPieces(balance.ReservedInWarehouseQty, sku, balance.Location.LocationType),
            balance.ReservedWithRepQty,
            ToPieces(balance.ReservedWithRepQty, sku, balance.Location.LocationType),
            balance.TargetQty,
            ToPieces(balance.TargetQty, sku, balance.Location.LocationType),
            balance.RowVersion,
            balance.LastUpdated);
    }

    private static InventoryBatchResponse ToResponse(InventoryBatch batch, IReadOnlyDictionary<Guid, SkuLookup> skuLookup)
    {
        skuLookup.TryGetValue(batch.SkuId, out var sku);
        return new InventoryBatchResponse(
            batch.Id,
            batch.LocationId,
            batch.Location.Name,
            batch.Location.LocationType,
            batch.SkuId,
            sku?.SkuCode,
            sku?.ProductName,
            batch.LotNumber,
            batch.ExpiryDate,
            batch.Quantity,
            ToPieces(batch.Quantity, sku, batch.Location.LocationType),
            batch.Notes,
            batch.CreatedAt);
    }

    private static StockTransactionResponse ToResponse(StockTransaction transaction, IReadOnlyDictionary<Guid, SkuLookup> skuLookup)
    {
        skuLookup.TryGetValue(transaction.SkuId, out var sku);
        return new StockTransactionResponse(
            transaction.Id,
            transaction.LocationId,
            transaction.Location.Name,
            transaction.Location.LocationType,
            transaction.SkuId,
            sku?.SkuCode,
            sku?.ProductName,
            transaction.TransactionType,
            transaction.QuantityChange,
            ToPieces(transaction.QuantityChange, sku, transaction.Location.LocationType),
            transaction.ReferenceOperationId,
            transaction.CreatedAt);
    }

    private static int? ToPieces(int? packs, SkuLookup? sku, string locationType) =>
        packs.HasValue && AllowsPieceDisplay(locationType) && sku?.PiecesPerPack is > 0 ? packs.Value * sku.PiecesPerPack.Value : null;

    private static bool AllowsPieceDisplay(string locationType) =>
        !string.Equals(locationType, "MainWarehouse", StringComparison.OrdinalIgnoreCase);

    private static DateOnly? CalculateMinimumExpiryDate(DateOnly today, string? openedExpiryDuration)
    {
        if (string.IsNullOrWhiteSpace(openedExpiryDuration))
        {
            return null;
        }

        var parts = openedExpiryDuration.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2 || !int.TryParse(parts[0], out var amount) || amount <= 0)
        {
            return null;
        }

        return parts[1].ToLowerInvariant() switch
        {
            "day" or "days" => today.AddDays(amount),
            "month" or "months" => today.AddMonths(amount),
            "year" or "years" => today.AddYears(amount),
            _ => null
        };
    }
}

public sealed record LocationResponse(Guid Id, string Name, string LocationType, bool IsActive);

public sealed record StockBalanceResponse(
    Guid LocationId,
    string LocationName,
    string LocationType,
    Guid SkuId,
    string? SkuCode,
    string? ProductName,
    int? PiecesPerPack,
    string? SellMode,
    int AvailablePacks,
    int? AvailablePieces,
    int ReservedInWarehousePacks,
    int? ReservedInWarehousePieces,
    int ReservedWithRepPacks,
    int? ReservedWithRepPieces,
    int? TargetPacks,
    int? TargetPieces,
    int RowVersion,
    DateTime? LastUpdated);

public sealed record InventoryBatchResponse(
    Guid Id,
    Guid LocationId,
    string LocationName,
    string LocationType,
    Guid SkuId,
    string? SkuCode,
    string? ProductName,
    string? LotNumber,
    DateOnly? ExpiryDate,
    int PackQuantity,
    int? PieceQuantity,
    string? Notes,
    DateTime CreatedAt);

public sealed record StockTransactionResponse(
    Guid Id,
    Guid LocationId,
    string LocationName,
    string LocationType,
    Guid SkuId,
    string? SkuCode,
    string? ProductName,
    string TransactionType,
    int PackChange,
    int? PieceChange,
    Guid? ReferenceOperationId,
    DateTime CreatedAt);

public sealed record TransferBlockedBatchResponse(
    Guid Id,
    Guid LocationId,
    string LocationName,
    string LocationType,
    Guid SkuId,
    string? SkuCode,
    string? ProductName,
    string? LotNumber,
    DateOnly? ExpiryDate,
    int PackQuantity,
    int? PieceQuantity,
    string? OpenedExpiryDuration,
    DateOnly? MinimumTransferExpiryDate,
    string Reason);

public sealed record TargetQuantityRequest(int? TargetPacks);

public sealed record InventoryReceiptRequest(Guid LocationId, Guid SkuId, int PackQuantity, string? LotNumber, DateOnly? ExpiryDate, string? Notes);

public sealed record InventoryReceiptResponse(Guid BatchId, Guid LocationId, Guid SkuId, int BatchPackQuantity);

internal sealed record SkuLookup(Guid Id, string SkuCode, string ProductName, int? PiecesPerPack, string? SellMode);
