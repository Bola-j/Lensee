using System.Text.Json;
using Lensee.Modules.Catalog.Data;
using Lensee.Modules.Inventory.Data;
using Lensee.Modules.Inventory.Services;
using Lensee.Modules.Notifications.Data;
using Lensee.Modules.Operations.Data;
using Lensee.SharedKernel.Abstractions;
using Lensee.SharedKernel.Primitives;
using Lensee.SharedKernel.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Lensee.Host.Endpoints;

public static class OperationsEndpoints
{
    private const string InventoryReceipt = "InventoryReceipt";
    private const string WarehouseTransfer = "WarehouseTransfer";
    private const string Draft = "Draft";
    private const string Reserved = "Reserved";
    private const string Shipped = "Shipped";
    private const string Received = "Received";
    private const string Cancelled = "Cancelled";
    private const string MainWarehouse = "MainWarehouse";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static RouteGroupBuilder MapOperationsEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/v1/operations").WithTags("Operations");

        group.MapGet("/replenishment", GetReplenishmentAsync).RequireAuthorization("operations.read");
        group.MapPost("/replenishment/reserve", ReserveReplenishmentAsync).RequireAuthorization("operations.write");
        group.MapPost("/replenishment/daily-reset", ReserveReplenishmentAsync).RequireAuthorization("operations.write");
        group.MapGet("/", ListOperationsAsync).RequireAuthorization("operations.read");
        group.MapGet("/{id:guid}", GetOperationAsync).RequireAuthorization("operations.read");
        group.MapPost("/", CreateOperationAsync).RequireAuthorization("operations.write");
        group.MapPut("/{id:guid}", UpdateOperationAsync).RequireAuthorization("operations.write");
        group.MapPost("/{id:guid}/confirm", ConfirmOperationAsync).RequireAuthorization("operations.write");
        group.MapPost("/{id:guid}/ship", ShipOperationAsync).RequireAuthorization("operations.write");
        group.MapPost("/{id:guid}/receive", ReceiveOperationAsync).RequireAuthorization("operations.write");
        group.MapPost("/{id:guid}/cancel", CancelOperationAsync).RequireAuthorization("operations.write");

        return group;
    }

    private static async Task<IResult> GetReplenishmentAsync(
        InventoryDbContext inventoryDbContext,
        CatalogDbContext catalogDbContext,
        OperationsDbContext operationsDbContext,
        ICurrentUser currentUser,
        CancellationToken cancellationToken)
    {
        if (IsWarehouseClerk(currentUser) && currentUser.LocationId is null)
        {
            return Results.Forbid();
        }

        var rows = await BuildReplenishmentRowsAsync(inventoryDbContext, catalogDbContext, operationsDbContext, currentUser, null, null, cancellationToken);
        return Results.Ok(rows);
    }

    private static async Task<IResult> ReserveReplenishmentAsync(
        ReplenishmentReserveRequest request,
        OperationsDbContext operationsDbContext,
        InventoryDbContext inventoryDbContext,
        CatalogDbContext catalogDbContext,
        NotificationsDbContext notificationsDbContext,
        StockLedgerService ledgerService,
        ICurrentUser currentUser,
        IClock clock,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(currentUser.Role, LenseeRoles.Admin, StringComparison.OrdinalIgnoreCase))
        {
            return Results.Forbid();
        }

        var mainLocation = await inventoryDbContext.Locations
            .FirstOrDefaultAsync(location => location.IsActive && location.LocationType == MainWarehouse, cancellationToken);
        if (mainLocation is null)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["mainWarehouse"] = ["MainWarehouse location is required."] });
        }

        Guid? locationFilter = request.LocationId is { } locationId && locationId != Guid.Empty ? locationId : null;
        Guid? skuFilter = request.SkuId is { } skuId && skuId != Guid.Empty ? skuId : null;
        var rows = await BuildReplenishmentRowsAsync(inventoryDbContext, catalogDbContext, operationsDbContext, currentUser, locationFilter, skuFilter, cancellationToken);
        var shortages = rows
            .Where(row => row.ShortagePacks > 0)
            .GroupBy(row => row.DestinationLocationId)
            .ToList();
        if (shortages.Count == 0)
        {
            return Results.Ok(new ReplenishmentReserveResponse(0, 0, [], []));
        }

        var mainBalances = await inventoryDbContext.StockBalances
            .Where(balance => balance.LocationId == mainLocation.Id)
            .ToDictionaryAsync(balance => balance.SkuId, cancellationToken);
        var remainingMainAvailable = mainBalances.ToDictionary(
            pair => pair.Key,
            pair => Math.Max(pair.Value.AvailableQty - (pair.Value.TargetQty ?? 0), 0));
        var now = clock.EgyptNow;
        var userId = currentUser.UserId ?? Guid.Empty;
        var created = new List<ReplenishmentOperationResponse>();
        var alerts = new List<ReplenishmentAlertResponse>();
        var unfilled = 0;

        foreach (var destinationGroup in shortages)
        {
            var draftLines = new List<ReplenishmentLineDraft>();
            foreach (var shortage in destinationGroup)
            {
                remainingMainAvailable.TryGetValue(shortage.SkuId, out var mainAvailable);
                var quantity = Math.Min(shortage.ShortagePacks, Math.Max(mainAvailable, 0));
                if (quantity <= 0)
                {
                    unfilled += shortage.ShortagePacks;
                    alerts.Add(ToReplenishmentAlert(shortage, "MainWarehouse cannot supply this SKU without falling below its target stock."));
                    continue;
                }

                remainingMainAvailable[shortage.SkuId] = mainAvailable - quantity;
                if (quantity < shortage.ShortagePacks)
                {
                    unfilled += shortage.ShortagePacks - quantity;
                    alerts.Add(ToReplenishmentAlert(shortage, $"MainWarehouse can reserve only {quantity} of {shortage.ShortagePacks} needed pack(s) without falling below target."));
                }

                draftLines.Add(new ReplenishmentLineDraft(shortage.SkuId, shortage.SkuCode, shortage.ProductName, quantity));
            }

            if (draftLines.Count == 0)
            {
                continue;
            }

            var draftOperationLines = draftLines
                .Select(line => new OperationLine
                {
                    Id = Guid.NewGuid(),
                    OperationId = Guid.Empty,
                    SkuId = line.SkuId,
                    ProductNameSnapshot = line.ProductName ?? line.SkuId.ToString(),
                    SkuCodeSnapshot = line.SkuCode ?? line.SkuId.ToString(),
                    Section = "Standard",
                    Quantity = line.PackQuantity,
                    EntryMode = "Packs",
                    BonusQuantity = 0,
                    UnitPrice = 0,
                    LineTotal = 0,
                    LineNotes = "Target-stock replenishment"
                })
                .ToList();

            var minimumExpiryBySku = await LoadMinimumExpiryBySkuAsync(catalogDbContext, draftOperationLines, now, cancellationToken);
            var planFailed = false;
            foreach (var line in draftOperationLines)
            {
                minimumExpiryBySku.TryGetValue(line.SkuId, out var minimumExpiryDate);
                try
                {
                    await ledgerService.PlanReserveInWarehouseFefoAsync(
                        mainLocation.Id,
                        line.SkuId,
                        line.Quantity,
                        minimumExpiryDate,
                        cancellationToken);
                }
                catch (InvalidOperationException)
                {
                    planFailed = true;
                    unfilled += line.Quantity;
                    remainingMainAvailable[line.SkuId] = remainingMainAvailable.GetValueOrDefault(line.SkuId) + line.Quantity;
                    var shortage = destinationGroup.First(row => row.SkuId == line.SkuId);
                    alerts.Add(ToReplenishmentAlert(shortage, "MainWarehouse has stock, but eligible batch stock is insufficient after opened-expiry rules."));
                    break;
                }
            }

            if (planFailed)
            {
                continue;
            }

            var operation = new OperationLog
            {
                Id = Guid.NewGuid(),
                OperationNumber = $"OP-{now:yyyyMMddHHmmss}-{Random.Shared.Next(100, 999)}",
                OperationType = WarehouseTransfer,
                Status = Draft,
                SourceLocationId = mainLocation.Id,
                DestinationLocationId = destinationGroup.Key,
                Notes = "Target-stock replenishment",
                CreatedBy = userId,
                CreatedAt = now
            };
            foreach (var line in draftOperationLines)
            {
                line.OperationId = operation.Id;
                operation.OperationLines.Add(line);
            }

            var allocations = new List<TransferAllocationSnapshot>();
            try
            {
                await ExecuteInventoryOperationTransactionAsync(inventoryDbContext, operationsDbContext, async () =>
                {
                    operationsDbContext.OperationLogs.Add(operation);
                    foreach (var line in operation.OperationLines)
                    {
                        minimumExpiryBySku.TryGetValue(line.SkuId, out var minimumExpiryDate);
                        var lineAllocations = await ledgerService.ReserveInWarehouseFefoAsync(
                            mainLocation.Id,
                            line.SkuId,
                            line.Quantity,
                            userId,
                            operation.Id,
                            minimumExpiryDate,
                            cancellationToken);

                        allocations.Add(new TransferAllocationSnapshot(line.SkuId, lineAllocations.ToList()));
                    }

                    operation.Status = Reserved;
                    operation.ConfirmedAt = now;
                    operation.ConfirmedBy = userId;
                    await AddVersionAsync(operationsDbContext, operation, "Reserved replenishment", userId, CreateSnapshot(operation, allocations), now, cancellationToken);
                    await operationsDbContext.SaveChangesAsync(cancellationToken);
                }, cancellationToken);
            }
            catch (InvalidOperationException)
            {
                unfilled += operation.OperationLines.Sum(line => line.Quantity);
                continue;
            }

            created.Add(new ReplenishmentOperationResponse(operation.Id, operation.OperationNumber, operation.DestinationLocationId!.Value, draftLines.Sum(line => line.PackQuantity)));
        }

        if (alerts.Count > 0)
        {
            await WriteReplenishmentAlertsAsync(notificationsDbContext, alerts, now, cancellationToken);
        }

        return Results.Ok(new ReplenishmentReserveResponse(created.Count, unfilled, created, alerts));
    }

    private static async Task<IResult> ListOperationsAsync(
        int? page,
        int? pageSize,
        OperationsDbContext operationsDbContext,
        InventoryDbContext inventoryDbContext,
        ICurrentUser currentUser,
        CancellationToken cancellationToken)
    {
        var request = new PageRequest(page ?? 1, pageSize ?? 25);
        var query = operationsDbContext.OperationLogs
            .Include(operation => operation.OperationLines)
            .Where(operation => !operation.IsDeleted)
            .AsQueryable();

        if (IsWarehouseClerk(currentUser))
        {
            if (currentUser.LocationId is not { } locationId)
            {
                return Results.Forbid();
            }

            query = query.Where(operation => operation.SourceLocationId == locationId || operation.DestinationLocationId == locationId);
        }

        var total = await query.CountAsync(cancellationToken);
        var operations = await query
            .OrderByDescending(operation => operation.CreatedAt)
            .Skip(request.Skip)
            .Take(request.PageSize)
            .ToListAsync(cancellationToken);
        var locationLookup = await LoadLocationLookupAsync(inventoryDbContext, operations, cancellationToken);

        return Results.Ok(new PagedResult<OperationListResponse>(
            operations.Select(operation => ToListResponse(operation, locationLookup)).ToList(),
            request.Page,
            request.PageSize,
            total));
    }

    private static async Task<IResult> GetOperationAsync(
        Guid id,
        OperationsDbContext operationsDbContext,
        InventoryDbContext inventoryDbContext,
        ICurrentUser currentUser,
        CancellationToken cancellationToken)
    {
        var operation = await LoadOperationAsync(operationsDbContext, id, cancellationToken);
        if (operation is null)
        {
            return Results.NotFound();
        }

        if (!CanReadOperation(currentUser, operation))
        {
            return Results.Forbid();
        }

        var locationLookup = await LoadLocationLookupAsync(inventoryDbContext, [operation], cancellationToken);
        return Results.Ok(ToDetailResponse(operation, locationLookup));
    }

    private static async Task<IResult> CreateOperationAsync(
        OperationRequest request,
        OperationsDbContext operationsDbContext,
        CatalogDbContext catalogDbContext,
        InventoryDbContext inventoryDbContext,
        ICurrentUser currentUser,
        IClock clock,
        CancellationToken cancellationToken)
    {
        var validation = await ValidateDraftAsync(request, catalogDbContext, inventoryDbContext, currentUser, isCreate: true, cancellationToken);
        if (validation.Errors.Count > 0)
        {
            return Results.ValidationProblem(validation.Errors);
        }

        if (!CanCreateDraft(currentUser, request, validation.SourceLocation, validation.DestinationLocation))
        {
            return Results.Forbid();
        }

        var now = clock.EgyptNow;
        var operation = new OperationLog
        {
            Id = Guid.NewGuid(),
            OperationNumber = $"OP-{now:yyyyMMddHHmmss}-{Random.Shared.Next(100, 999)}",
            OperationType = NormalizeOperationType(request.OperationType),
            Status = Draft,
            SourceLocationId = request.SourceLocationId,
            DestinationLocationId = request.DestinationLocationId,
            Notes = request.Notes,
            CreatedBy = currentUser.UserId ?? Guid.Empty,
            CreatedAt = now
        };

        operationsDbContext.OperationLogs.Add(operation);
        AddLines(operation, validation.SkusById, request.Lines);
        if (operation.OperationType == InventoryReceipt)
        {
            operationsDbContext.InventoryReceiptHeaders.Add(new InventoryReceiptHeader
            {
                Id = Guid.NewGuid(),
                OperationId = operation.Id,
                SupplierName = request.Receipt?.SupplierName?.Trim() ?? "Supplier",
                InvoiceNumber = TrimToNull(request.Receipt?.InvoiceNumber),
                ReceiptDate = now
            });
        }

        await operationsDbContext.SaveChangesAsync(cancellationToken);
        await AddVersionAsync(operationsDbContext, operation, "Initial", currentUser.UserId ?? Guid.Empty, CreateSnapshot(operation), now, cancellationToken);
        await operationsDbContext.SaveChangesAsync(cancellationToken);
        var created = await LoadOperationAsync(operationsDbContext, operation.Id, cancellationToken);
        var locationLookup = await LoadLocationLookupAsync(inventoryDbContext, [created!], cancellationToken);

        return Results.Created($"/api/v1/operations/{operation.Id}", ToDetailResponse(created!, locationLookup));
    }

    private static async Task<IResult> UpdateOperationAsync(
        Guid id,
        OperationRequest request,
        OperationsDbContext operationsDbContext,
        CatalogDbContext catalogDbContext,
        InventoryDbContext inventoryDbContext,
        ICurrentUser currentUser,
        IClock clock,
        CancellationToken cancellationToken)
    {
        var operation = await LoadOperationAsync(operationsDbContext, id, cancellationToken);
        if (operation is null)
        {
            return Results.NotFound();
        }
        if (operation.Status != Draft)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { [nameof(operation.Status)] = ["Only draft operations can be edited."] });
        }

        var validation = await ValidateDraftAsync(request, catalogDbContext, inventoryDbContext, currentUser, isCreate: false, cancellationToken);
        if (validation.Errors.Count > 0)
        {
            return Results.ValidationProblem(validation.Errors);
        }
        if (!CanCreateDraft(currentUser, request, validation.SourceLocation, validation.DestinationLocation))
        {
            return Results.Forbid();
        }

        operation.OperationType = NormalizeOperationType(request.OperationType);
        operation.SourceLocationId = request.SourceLocationId;
        operation.DestinationLocationId = request.DestinationLocationId;
        operation.Notes = request.Notes;
        operationsDbContext.OperationLines.RemoveRange(operation.OperationLines);
        operation.OperationLines.Clear();
        AddLines(operation, validation.SkusById, request.Lines);
        await AddVersionAsync(operationsDbContext, operation, "Draft update", currentUser.UserId ?? Guid.Empty, CreateSnapshot(operation), clock.EgyptNow, cancellationToken);
        await operationsDbContext.SaveChangesAsync(cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> ConfirmOperationAsync(
        Guid id,
        OperationsDbContext operationsDbContext,
        InventoryDbContext inventoryDbContext,
        CatalogDbContext catalogDbContext,
        StockLedgerService ledgerService,
        ICurrentUser currentUser,
        IClock clock,
        IAuditLogWriter auditLogWriter,
        CancellationToken cancellationToken)
    {
        var operation = await LoadOperationAsync(operationsDbContext, id, cancellationToken);
        if (operation is null)
        {
            return Results.NotFound();
        }
        if (operation.Status != Draft)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { [nameof(operation.Status)] = ["Only draft operations can be confirmed."] });
        }
        if (!await CanMutateOperationAsync(currentUser, operation, inventoryDbContext, "confirm", cancellationToken))
        {
            return Results.Forbid();
        }

        var now = clock.EgyptNow;
        var userId = currentUser.UserId ?? Guid.Empty;
        if (operation.OperationType == InventoryReceipt)
        {
            await ExecuteInventoryOperationTransactionAsync(inventoryDbContext, operationsDbContext, async () =>
            {
                foreach (var line in operation.OperationLines)
                {
                    await ledgerService.ReceiveAsync(
                        operation.DestinationLocationId!.Value,
                        line.SkuId,
                        line.Quantity,
                        userId,
                        line.LotNumber,
                        line.ExpiryDate,
                        line.LineNotes,
                        operation.Id,
                        cancellationToken);
                }

                operation.Status = Received;
                operation.ConfirmedAt = now;
                operation.ConfirmedBy = userId;
                await AddVersionAsync(operationsDbContext, operation, "Received", userId, CreateSnapshot(operation), now, cancellationToken);
                await operationsDbContext.SaveChangesAsync(cancellationToken);
            }, cancellationToken);
        }
        else
        {
            var minimumExpiryBySku = await LoadMinimumExpiryBySkuAsync(catalogDbContext, operation.OperationLines, now, cancellationToken);
            var allocations = new List<TransferAllocationSnapshot>();
            foreach (var line in operation.OperationLines)
            {
                minimumExpiryBySku.TryGetValue(line.SkuId, out var minimumExpiryDate);
                try
                {
                    await ledgerService.PlanReserveInWarehouseFefoAsync(
                        operation.SourceLocationId!.Value,
                        line.SkuId,
                        line.Quantity,
                        minimumExpiryDate,
                        cancellationToken);
                }
                catch (InvalidOperationException exception)
                {
                    return Results.ValidationProblem(new Dictionary<string, string[]>
                    {
                        [line.SkuCodeSnapshot] = [exception.Message]
                    });
                }
            }

            await ExecuteInventoryOperationTransactionAsync(inventoryDbContext, operationsDbContext, async () =>
            {
                foreach (var line in operation.OperationLines)
                {
                    minimumExpiryBySku.TryGetValue(line.SkuId, out var minimumExpiryDate);
                    var lineAllocations = await ledgerService.ReserveInWarehouseFefoAsync(
                        operation.SourceLocationId!.Value,
                        line.SkuId,
                        line.Quantity,
                        userId,
                        operation.Id,
                        minimumExpiryDate,
                        cancellationToken);

                    allocations.Add(new TransferAllocationSnapshot(line.SkuId, lineAllocations.ToList()));
                }

                operation.Status = Reserved;
                operation.ConfirmedAt = now;
                operation.ConfirmedBy = userId;
                await AddVersionAsync(operationsDbContext, operation, "Reserved", userId, CreateSnapshot(operation, allocations), now, cancellationToken);
                await operationsDbContext.SaveChangesAsync(cancellationToken);
            }, cancellationToken);
        }

        await auditLogWriter.WriteAsync("Operation", operation.Id, "Confirm", new { operation.OperationType, operation.Status }, cancellationToken: cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> ShipOperationAsync(
        Guid id,
        OperationsDbContext operationsDbContext,
        InventoryDbContext inventoryDbContext,
        ICurrentUser currentUser,
        IClock clock,
        CancellationToken cancellationToken)
    {
        var operation = await LoadOperationAsync(operationsDbContext, id, cancellationToken);
        if (operation is null)
        {
            return Results.NotFound();
        }
        if (operation.OperationType != WarehouseTransfer || operation.Status != Reserved)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { [nameof(operation.Status)] = ["Only reserved warehouse transfers can be shipped."] });
        }
        if (!await CanMutateOperationAsync(currentUser, operation, inventoryDbContext, "ship", cancellationToken))
        {
            return Results.Forbid();
        }

        operation.Status = Shipped;
        await AddVersionAsync(operationsDbContext, operation, "Shipped", currentUser.UserId ?? Guid.Empty, CreateSnapshot(operation, ReadTransferAllocations(operation)), clock.EgyptNow, cancellationToken);
        await operationsDbContext.SaveChangesAsync(cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> ReceiveOperationAsync(
        Guid id,
        OperationsDbContext operationsDbContext,
        InventoryDbContext inventoryDbContext,
        StockLedgerService ledgerService,
        ICurrentUser currentUser,
        IClock clock,
        CancellationToken cancellationToken)
    {
        var operation = await LoadOperationAsync(operationsDbContext, id, cancellationToken);
        if (operation is null)
        {
            return Results.NotFound();
        }
        if (operation.OperationType != WarehouseTransfer || operation.Status is not (Reserved or Shipped))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { [nameof(operation.Status)] = ["Only reserved or shipped warehouse transfers can be received."] });
        }
        if (!await CanMutateOperationAsync(currentUser, operation, inventoryDbContext, "receive", cancellationToken))
        {
            return Results.Forbid();
        }

        var userId = currentUser.UserId ?? Guid.Empty;
        var allocations = ReadTransferAllocations(operation);
        if (operation.OperationLines.Any(line => allocations.All(value => value.SkuId != line.SkuId)))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { [nameof(operation.OperationLines)] = ["Transfer allocation snapshot is missing."] });
        }

        await ExecuteInventoryOperationTransactionAsync(inventoryDbContext, operationsDbContext, async () =>
        {
            foreach (var line in operation.OperationLines)
            {
                var lineAllocation = allocations.SingleOrDefault(value => value.SkuId == line.SkuId);
                if (lineAllocation is null)
                {
                    throw new InvalidOperationException("Transfer allocation snapshot is missing.");
                }

                await ledgerService.CommitReservedInWarehouseOutAsync(
                    operation.SourceLocationId!.Value,
                    line.SkuId,
                    lineAllocation.Allocations,
                    userId,
                    operation.Id,
                    cancellationToken);

                foreach (var allocation in lineAllocation.Allocations)
                {
                    await ledgerService.ReceiveSupplyAsync(
                        operation.DestinationLocationId!.Value,
                        line.SkuId,
                        allocation.Quantity,
                        userId,
                        allocation.LotNumber,
                        allocation.ExpiryDate,
                        line.LineNotes,
                        operation.Id,
                        cancellationToken);
                }
            }

            operation.Status = Received;
            await AddVersionAsync(operationsDbContext, operation, "Received", userId, CreateSnapshot(operation, allocations), clock.EgyptNow, cancellationToken);
            await operationsDbContext.SaveChangesAsync(cancellationToken);
        }, cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> CancelOperationAsync(
        Guid id,
        OperationsDbContext operationsDbContext,
        InventoryDbContext inventoryDbContext,
        StockLedgerService ledgerService,
        ICurrentUser currentUser,
        IClock clock,
        CancellationToken cancellationToken)
    {
        var operation = await LoadOperationAsync(operationsDbContext, id, cancellationToken);
        if (operation is null)
        {
            return Results.NotFound();
        }
        if (operation.Status == Received)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { [nameof(operation.Status)] = ["Received operations cannot be cancelled."] });
        }
        if (operation.Status == Cancelled)
        {
            return Results.NoContent();
        }
        if (!await CanMutateOperationAsync(currentUser, operation, inventoryDbContext, "cancel", cancellationToken))
        {
            return Results.Forbid();
        }

        var userId = currentUser.UserId ?? Guid.Empty;
        await ExecuteInventoryOperationTransactionAsync(inventoryDbContext, operationsDbContext, async () =>
        {
            if (operation.OperationType == WarehouseTransfer && operation.Status is Reserved or Shipped)
            {
                foreach (var line in operation.OperationLines)
                {
                    await ledgerService.ReleaseInWarehouseAsync(operation.SourceLocationId!.Value, line.SkuId, line.Quantity, userId, operation.Id, cancellationToken);
                }
            }

            operation.Status = Cancelled;
            await AddVersionAsync(operationsDbContext, operation, "Cancelled", userId, CreateSnapshot(operation, ReadTransferAllocations(operation)), clock.EgyptNow, cancellationToken);
            await operationsDbContext.SaveChangesAsync(cancellationToken);
        }, cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IReadOnlyList<ReplenishmentRowResponse>> BuildReplenishmentRowsAsync(
        InventoryDbContext inventoryDbContext,
        CatalogDbContext catalogDbContext,
        OperationsDbContext operationsDbContext,
        ICurrentUser currentUser,
        Guid? locationFilter,
        Guid? skuFilter,
        CancellationToken cancellationToken)
    {
        var locationsQuery = inventoryDbContext.Locations
            .Where(location => location.IsActive && location.LocationType != MainWarehouse);
        if (IsWarehouseClerk(currentUser))
        {
            var clerkLocationId = currentUser.LocationId!.Value;
            locationsQuery = locationsQuery.Where(location => location.Id == clerkLocationId);
        }
        if (locationFilter.HasValue)
        {
            locationsQuery = locationsQuery.Where(location => location.Id == locationFilter.Value);
        }

        var locations = await locationsQuery
            .OrderBy(location => location.Name)
            .ToListAsync(cancellationToken);
        var locationIds = locations.Select(location => location.Id).ToArray();
        if (locationIds.Length == 0)
        {
            return [];
        }

        var balancesQuery = inventoryDbContext.StockBalances
            .Where(balance => locationIds.Contains(balance.LocationId) && balance.TargetQty != null);
        if (skuFilter.HasValue)
        {
            balancesQuery = balancesQuery.Where(balance => balance.SkuId == skuFilter.Value);
        }

        var balances = await balancesQuery.ToListAsync(cancellationToken);
        var skuIds = balances.Select(balance => balance.SkuId).Distinct().ToArray();
        if (skuIds.Length == 0)
        {
            return [];
        }

        var skus = await catalogDbContext.Skus
            .Include(sku => sku.Product)
            .Where(sku => skuIds.Contains(sku.Id))
            .ToDictionaryAsync(
                sku => sku.Id,
                sku => new ReplenishmentSkuLookup(sku.SkuCode, sku.Product.Name, sku.Product.PiecesPerPack),
                cancellationToken);

        var incoming = await operationsDbContext.OperationLogs
            .Include(operation => operation.OperationLines)
            .Where(operation =>
                !operation.IsDeleted &&
                operation.OperationType == WarehouseTransfer &&
                (operation.Status == Reserved || operation.Status == Shipped) &&
                operation.DestinationLocationId.HasValue &&
                locationIds.Contains(operation.DestinationLocationId.Value))
            .SelectMany(
                operation => operation.OperationLines,
                (operation, line) => new { DestinationLocationId = operation.DestinationLocationId!.Value, line.SkuId, line.Quantity })
            .GroupBy(value => new { value.DestinationLocationId, value.SkuId })
            .Select(group => new { group.Key.DestinationLocationId, group.Key.SkuId, Quantity = group.Sum(value => value.Quantity) })
            .ToDictionaryAsync(value => (value.DestinationLocationId, value.SkuId), value => value.Quantity, cancellationToken);

        var mainLocationId = await inventoryDbContext.Locations
            .Where(location => location.IsActive && location.LocationType == MainWarehouse)
            .Select(location => (Guid?)location.Id)
            .FirstOrDefaultAsync(cancellationToken);
        var mainStock = mainLocationId.HasValue
            ? await inventoryDbContext.StockBalances
                .Where(balance => balance.LocationId == mainLocationId.Value && skuIds.Contains(balance.SkuId))
                .ToDictionaryAsync(balance => balance.SkuId, balance => balance.AvailableQty, cancellationToken)
            : [];

        return balances
            .Select(balance =>
            {
                var location = locations.First(location => location.Id == balance.LocationId);
                skus.TryGetValue(balance.SkuId, out var sku);
                incoming.TryGetValue((balance.LocationId, balance.SkuId), out var incomingPacks);
                mainStock.TryGetValue(balance.SkuId, out var mainAvailable);
                var target = balance.TargetQty ?? 0;
                var shortage = Math.Max(target - balance.AvailableQty - incomingPacks, 0);
                return new ReplenishmentRowResponse(
                    balance.LocationId,
                    location.Name,
                    location.LocationType,
                    balance.SkuId,
                    sku?.SkuCode,
                    sku?.ProductName,
                    sku?.PiecesPerPack,
                    balance.AvailableQty,
                    ToPieces(balance.AvailableQty, sku?.PiecesPerPack, location.LocationType),
                    incomingPacks,
                    ToPieces(incomingPacks, sku?.PiecesPerPack, location.LocationType),
                    target,
                    ToPieces(target, sku?.PiecesPerPack, location.LocationType),
                    shortage,
                    ToPieces(shortage, sku?.PiecesPerPack, location.LocationType),
                    mainAvailable);
            })
            .OrderByDescending(row => row.ShortagePacks)
            .ThenBy(row => row.DestinationLocationName)
            .ThenBy(row => row.SkuCode ?? row.SkuId.ToString())
            .ToList();
    }

    private static async Task<Dictionary<Guid, DateOnly>> LoadMinimumExpiryBySkuAsync(
        CatalogDbContext catalogDbContext,
        IEnumerable<OperationLine> lines,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var skuIds = lines.Select(line => line.SkuId).Distinct().ToArray();
        if (skuIds.Length == 0)
        {
            return [];
        }

        var openedDurations = await catalogDbContext.Skus
            .Include(sku => sku.Product)
            .Where(sku => skuIds.Contains(sku.Id))
            .Select(sku => new { sku.Id, sku.Product.OpenedExpiryDuration })
            .ToListAsync(cancellationToken);
        var today = DateOnly.FromDateTime(now);
        return openedDurations
            .Select(value => new { value.Id, MinimumExpiryDate = CalculateMinimumExpiryDate(today, value.OpenedExpiryDuration) })
            .Where(value => value.MinimumExpiryDate.HasValue)
            .ToDictionary(value => value.Id, value => value.MinimumExpiryDate!.Value);
    }

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

    private static async Task<OperationLog?> LoadOperationAsync(OperationsDbContext dbContext, Guid id, CancellationToken cancellationToken) =>
        await dbContext.OperationLogs
            .Include(operation => operation.OperationLines)
            .Include(operation => operation.InventoryReceiptHeader)
            .Include(operation => operation.OperationVersions)
            .FirstOrDefaultAsync(operation => operation.Id == id && !operation.IsDeleted, cancellationToken);

    private static async Task<DraftValidationResult> ValidateDraftAsync(
        OperationRequest request,
        CatalogDbContext catalogDbContext,
        InventoryDbContext inventoryDbContext,
        ICurrentUser currentUser,
        bool isCreate,
        CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        var operationType = NormalizeOperationType(request.OperationType);
        if (operationType is not (InventoryReceipt or WarehouseTransfer))
        {
            errors[nameof(request.OperationType)] = ["Operation type must be InventoryReceipt or WarehouseTransfer."];
        }
        if (request.Lines.Count == 0)
        {
            errors[nameof(request.Lines)] = ["At least one line is required."];
        }
        if (request.Lines.Any(line => line.PackQuantity <= 0))
        {
            errors[nameof(request.Lines)] = ["Line pack quantity must be greater than zero."];
        }
        if (request.Lines.Select(line => line.SkuId).Distinct().Count() != request.Lines.Count)
        {
            errors[nameof(request.Lines)] = ["Duplicate SKUs are not supported in this sprint."];
        }

        var source = request.SourceLocationId.HasValue
            ? await inventoryDbContext.Locations.FirstOrDefaultAsync(location => location.Id == request.SourceLocationId && location.IsActive, cancellationToken)
            : null;
        var destination = request.DestinationLocationId.HasValue
            ? await inventoryDbContext.Locations.FirstOrDefaultAsync(location => location.Id == request.DestinationLocationId && location.IsActive, cancellationToken)
            : null;

        if (operationType == InventoryReceipt)
        {
            if (destination is null || !IsMainWarehouse(destination))
            {
                errors[nameof(request.DestinationLocationId)] = ["Inventory receipt destination must be the main warehouse."];
            }
        }
        if (operationType == WarehouseTransfer)
        {
            if (source is null || !IsMainWarehouse(source))
            {
                errors[nameof(request.SourceLocationId)] = ["Warehouse transfer source must be the main warehouse."];
            }
            if (destination is null || IsMainWarehouse(destination))
            {
                errors[nameof(request.DestinationLocationId)] = ["Warehouse transfer destination must be a non-main warehouse."];
            }
        }

        var skuIds = request.Lines.Select(line => line.SkuId).Distinct().ToArray();
        var skus = await catalogDbContext.Skus
            .Include(sku => sku.Product)
            .Where(sku => skuIds.Contains(sku.Id) && sku.IsActive && sku.DeletedAt == null && sku.Product.IsActive && sku.Product.DeletedAt == null)
            .ToDictionaryAsync(sku => sku.Id, cancellationToken);
        if (skus.Count != skuIds.Length)
        {
            errors[nameof(request.Lines)] = ["All operation SKUs must exist and be active."];
        }

        return new DraftValidationResult(errors, source, destination, skus);
    }

    private static bool CanReadOperation(ICurrentUser currentUser, OperationLog operation) =>
        !IsWarehouseClerk(currentUser) ||
        (currentUser.LocationId.HasValue &&
            (operation.SourceLocationId == currentUser.LocationId || operation.DestinationLocationId == currentUser.LocationId));

    private static bool CanCreateDraft(ICurrentUser currentUser, OperationRequest request, Location? source, Location? destination)
    {
        if (string.Equals(currentUser.Role, LenseeRoles.Admin, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        if (!IsWarehouseClerk(currentUser) || currentUser.LocationId is not { } clerkLocationId)
        {
            return false;
        }

        var operationType = NormalizeOperationType(request.OperationType);
        if (operationType == InventoryReceipt)
        {
            return destination?.Id == clerkLocationId && destination is not null && IsMainWarehouse(destination);
        }

        return source?.Id == clerkLocationId && source is not null && IsMainWarehouse(source);
    }

    private static async Task<bool> CanMutateOperationAsync(ICurrentUser currentUser, OperationLog operation, InventoryDbContext dbContext, string action, CancellationToken cancellationToken)
    {
        if (string.Equals(currentUser.Role, LenseeRoles.Admin, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        if (!IsWarehouseClerk(currentUser) || currentUser.LocationId is not { } clerkLocationId)
        {
            return false;
        }

        var source = operation.SourceLocationId.HasValue
            ? await dbContext.Locations.FindAsync([operation.SourceLocationId.Value], cancellationToken)
            : null;
        if (operation.OperationType == InventoryReceipt)
        {
            return action is "confirm" or "cancel" && operation.DestinationLocationId == clerkLocationId && operation.DestinationLocationId.HasValue;
        }
        if (action is "confirm" or "ship" or "cancel")
        {
            return operation.SourceLocationId == clerkLocationId && source is not null && IsMainWarehouse(source);
        }
        if (action == "receive")
        {
            return operation.DestinationLocationId == clerkLocationId;
        }

        return false;
    }

    private static async Task<Dictionary<Guid, Location>> LoadLocationLookupAsync(InventoryDbContext dbContext, IReadOnlyCollection<OperationLog> operations, CancellationToken cancellationToken)
    {
        var ids = operations.SelectMany(operation => new[] { operation.SourceLocationId, operation.DestinationLocationId })
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .Distinct()
            .ToArray();
        if (ids.Length == 0)
        {
            return [];
        }

        return await dbContext.Locations.Where(location => ids.Contains(location.Id)).ToDictionaryAsync(location => location.Id, cancellationToken);
    }

    private static void AddLines(OperationLog operation, IReadOnlyDictionary<Guid, Sku> skusById, IReadOnlyList<OperationLineRequest> lines)
    {
        foreach (var line in lines)
        {
            var sku = skusById[line.SkuId];
            operation.OperationLines.Add(new OperationLine
            {
                Id = Guid.NewGuid(),
                OperationId = operation.Id,
                SkuId = line.SkuId,
                ProductNameSnapshot = sku.Product.Name,
                SkuCodeSnapshot = sku.SkuCode,
                Section = "Standard",
                Quantity = line.PackQuantity,
                EntryMode = "Packs",
                BonusQuantity = 0,
                UnitPrice = 0,
                LineTotal = 0,
                LotNumber = TrimToNull(line.LotNumber),
                ExpiryDate = line.ExpiryDate,
                LineNotes = TrimToNull(line.Notes)
            });
        }
    }

    private static async Task AddVersionAsync(OperationsDbContext dbContext, OperationLog operation, string reason, Guid userId, OperationSnapshot snapshot, DateTime now, CancellationToken cancellationToken)
    {
        var version = new OperationVersion
        {
            Id = Guid.NewGuid(),
            OperationId = operation.Id,
            VersionNumber = operation.OperationVersions.Count == 0
                ? 1
                : operation.OperationVersions.Max(value => value.VersionNumber) + 1,
            SnapshotData = JsonSerializer.Serialize(snapshot, JsonOptions),
            Reason = reason,
            EditedBy = userId,
            EditedAt = now
        };
        operation.OperationVersions.Add(version);
        dbContext.OperationVersions.Add(version);
        await dbContext.SaveChangesAsync(cancellationToken);
        operation.CurrentVersionId = version.Id;
    }

    private static OperationSnapshot CreateSnapshot(OperationLog operation, IReadOnlyList<TransferAllocationSnapshot>? allocations = null) =>
        new(
            operation.OperationType,
            operation.Status,
            operation.SourceLocationId,
            operation.DestinationLocationId,
            operation.OperationLines
                .Select(line => new OperationLineSnapshot(line.SkuId, line.SkuCodeSnapshot, line.ProductNameSnapshot, line.Quantity, line.LotNumber, line.ExpiryDate))
                .ToList(),
            allocations ?? []);

    private static IReadOnlyList<TransferAllocationSnapshot> ReadTransferAllocations(OperationLog operation)
    {
        var snapshot = operation.OperationVersions
            .OrderByDescending(version => version.VersionNumber)
            .Select(version =>
            {
                try
                {
                    return JsonSerializer.Deserialize<OperationSnapshot>(version.SnapshotData, JsonOptions);
                }
                catch
                {
                    return null;
                }
            })
            .FirstOrDefault(value => value?.TransferAllocations.Count > 0);

        return snapshot?.TransferAllocations ?? [];
    }

    private static OperationListResponse ToListResponse(OperationLog operation, IReadOnlyDictionary<Guid, Location> locationLookup) =>
        new(
            operation.Id,
            operation.OperationNumber,
            operation.OperationType,
            operation.Status,
            operation.SourceLocationId,
            GetLocationName(operation.SourceLocationId, locationLookup),
            operation.DestinationLocationId,
            GetLocationName(operation.DestinationLocationId, locationLookup),
            operation.CreatedAt,
            operation.ConfirmedAt);

    private static OperationDetailResponse ToDetailResponse(OperationLog operation, IReadOnlyDictionary<Guid, Location> locationLookup) =>
        new(
            operation.Id,
            operation.OperationNumber,
            operation.OperationType,
            operation.Status,
            operation.SourceLocationId,
            GetLocationName(operation.SourceLocationId, locationLookup),
            operation.DestinationLocationId,
            GetLocationName(operation.DestinationLocationId, locationLookup),
            operation.Notes,
            operation.CreatedAt,
            operation.ConfirmedAt,
            operation.OperationLines.Select(line => new OperationLineResponse(line.Id, line.SkuId, line.SkuCodeSnapshot, line.ProductNameSnapshot, line.Quantity, line.LotNumber, line.ExpiryDate, line.LineNotes)).ToList(),
            operation.OperationVersions.OrderBy(version => version.VersionNumber).Select(version => new OperationVersionResponse(version.Id, version.VersionNumber, version.Reason, version.EditedAt)).ToList());

    private static string? GetLocationName(Guid? locationId, IReadOnlyDictionary<Guid, Location> lookup) =>
        locationId.HasValue && lookup.TryGetValue(locationId.Value, out var location) ? location.Name : null;

    private static string NormalizeOperationType(string value) =>
        string.Equals(value, InventoryReceipt, StringComparison.OrdinalIgnoreCase)
            ? InventoryReceipt
            : string.Equals(value, WarehouseTransfer, StringComparison.OrdinalIgnoreCase) || string.Equals(value, "Supply", StringComparison.OrdinalIgnoreCase)
                ? WarehouseTransfer
                : value.Trim();

    private static bool IsMainWarehouse(Location location) =>
        string.Equals(location.LocationType, MainWarehouse, StringComparison.OrdinalIgnoreCase);

    private static bool IsWarehouseClerk(ICurrentUser currentUser) =>
        string.Equals(currentUser.Role, LenseeRoles.WarehouseClerk, StringComparison.OrdinalIgnoreCase);

    private static string? TrimToNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static int? ToPieces(int packs, int? piecesPerPack, string locationType) =>
        !string.Equals(locationType, MainWarehouse, StringComparison.OrdinalIgnoreCase) && piecesPerPack is > 0
            ? packs * piecesPerPack.Value
            : null;

    private static ReplenishmentAlertResponse ToReplenishmentAlert(ReplenishmentRowResponse shortage, string message) =>
        new(
            shortage.DestinationLocationId,
            shortage.DestinationLocationName,
            shortage.SkuId,
            shortage.SkuCode,
            shortage.ProductName,
            shortage.ShortagePacks,
            shortage.MainAvailablePacks,
            message);

    private static async Task WriteReplenishmentAlertsAsync(
        NotificationsDbContext notificationsDbContext,
        IReadOnlyCollection<ReplenishmentAlertResponse> alerts,
        DateTime now,
        CancellationToken cancellationToken)
    {
        foreach (var alert in alerts)
        {
            var message = $"{alert.DestinationLocationName}: {alert.SkuCode ?? alert.SkuId.ToString()} needs {alert.ShortagePacks} pack(s). {alert.Message}";
            foreach (var role in new[] { LenseeRoles.Admin, LenseeRoles.CLevel })
            {
                var alreadyExists = await notificationsDbContext.NotificationLogs.AnyAsync(notification =>
                    notification.AlertType == "TargetReplenishmentLowMainStock" &&
                    notification.TargetRole == role &&
                    notification.ReferenceId == alert.SkuId &&
                    notification.ReferenceType == "Sku" &&
                    notification.Message == message &&
                    notification.CreatedAt.Date == now.Date,
                    cancellationToken);
                if (alreadyExists)
                {
                    continue;
                }

                notificationsDbContext.NotificationLogs.Add(new NotificationLog
                {
                    Id = Guid.NewGuid(),
                    AlertType = "TargetReplenishmentLowMainStock",
                    Message = message,
                    ReferenceId = alert.SkuId,
                    ReferenceType = "Sku",
                    TargetRole = role,
                    Channel = "InApp",
                    IsRead = false,
                    CreatedAt = now
                });
            }
        }

        await notificationsDbContext.SaveChangesAsync(cancellationToken);
    }

    private static async Task ExecuteInventoryOperationTransactionAsync(
        InventoryDbContext inventoryDbContext,
        OperationsDbContext operationsDbContext,
        Func<Task> action,
        CancellationToken cancellationToken)
    {
        if (!inventoryDbContext.Database.IsRelational() || !operationsDbContext.Database.IsRelational())
        {
            await action();
            return;
        }

        await using var transaction = await inventoryDbContext.Database.BeginTransactionAsync(cancellationToken);
        await operationsDbContext.Database.UseTransactionAsync(transaction.GetDbTransaction(), cancellationToken);
        try
        {
            await action();
            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            await operationsDbContext.Database.UseTransactionAsync(null, cancellationToken);
        }
    }

    private sealed record DraftValidationResult(Dictionary<string, string[]> Errors, Location? SourceLocation, Location? DestinationLocation, Dictionary<Guid, Sku> SkusById);

    private sealed record ReplenishmentLineDraft(Guid SkuId, string? SkuCode, string? ProductName, int PackQuantity);

    private sealed record ReplenishmentSkuLookup(string SkuCode, string ProductName, int? PiecesPerPack);

    private sealed record OperationSnapshot(
        string OperationType,
        string Status,
        Guid? SourceLocationId,
        Guid? DestinationLocationId,
        IReadOnlyList<OperationLineSnapshot> Lines,
        IReadOnlyList<TransferAllocationSnapshot> TransferAllocations);

    private sealed record OperationLineSnapshot(Guid SkuId, string SkuCode, string ProductName, int PackQuantity, string? LotNumber, DateOnly? ExpiryDate);

    private sealed record TransferAllocationSnapshot(Guid SkuId, IReadOnlyList<BatchAllocation> Allocations);
}

public sealed record OperationRequest(
    string OperationType,
    Guid? SourceLocationId,
    Guid? DestinationLocationId,
    string? Notes,
    ReceiptRequest? Receipt,
    IReadOnlyList<OperationLineRequest> Lines);

public sealed record ReceiptRequest(string? SupplierName, string? InvoiceNumber);

public sealed record OperationLineRequest(Guid SkuId, int PackQuantity, string? LotNumber, DateOnly? ExpiryDate, string? Notes);

public sealed record OperationListResponse(
    Guid Id,
    string OperationNumber,
    string OperationType,
    string Status,
    Guid? SourceLocationId,
    string? SourceLocationName,
    Guid? DestinationLocationId,
    string? DestinationLocationName,
    DateTime CreatedAt,
    DateTime? ConfirmedAt);

public sealed record OperationDetailResponse(
    Guid Id,
    string OperationNumber,
    string OperationType,
    string Status,
    Guid? SourceLocationId,
    string? SourceLocationName,
    Guid? DestinationLocationId,
    string? DestinationLocationName,
    string? Notes,
    DateTime CreatedAt,
    DateTime? ConfirmedAt,
    IReadOnlyList<OperationLineResponse> Lines,
    IReadOnlyList<OperationVersionResponse> Versions);

public sealed record OperationLineResponse(Guid Id, Guid SkuId, string SkuCode, string ProductName, int PackQuantity, string? LotNumber, DateOnly? ExpiryDate, string? Notes);

public sealed record OperationVersionResponse(Guid Id, int VersionNumber, string Reason, DateTime EditedAt);

public sealed record ReplenishmentReserveRequest(Guid? LocationId, Guid? SkuId);

public sealed record ReplenishmentRowResponse(
    Guid DestinationLocationId,
    string DestinationLocationName,
    string DestinationLocationType,
    Guid SkuId,
    string? SkuCode,
    string? ProductName,
    int? PiecesPerPack,
    int AvailablePacks,
    int? AvailablePieces,
    int IncomingPacks,
    int? IncomingPieces,
    int TargetPacks,
    int? TargetPieces,
    int ShortagePacks,
    int? ShortagePieces,
    int MainAvailablePacks);

public sealed record ReplenishmentReserveResponse(
    int CreatedOperations,
    int UnfilledPacks,
    IReadOnlyList<ReplenishmentOperationResponse> Operations,
    IReadOnlyList<ReplenishmentAlertResponse> Alerts);

public sealed record ReplenishmentOperationResponse(Guid Id, string OperationNumber, Guid DestinationLocationId, int ReservedPacks);

public sealed record ReplenishmentAlertResponse(
    Guid DestinationLocationId,
    string DestinationLocationName,
    Guid SkuId,
    string? SkuCode,
    string? ProductName,
    int ShortagePacks,
    int MainAvailablePacks,
    string Message);
