using System.Net;
using System.Net.Http.Json;
using Lensee.Modules.Catalog.Data;
using Lensee.Modules.Inventory.Data;
using Lensee.Modules.Inventory.Services;
using Lensee.Modules.Notifications.Data;
using Lensee.Modules.Operations.Data;
using Lensee.SharedKernel.Abstractions;
using Lensee.SharedKernel.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Lensee.Tests;

public sealed class OperationsEndpointContractTests : IClassFixture<OperationsEndpointFactory>
{
    private readonly OperationsEndpointFactory _factory;

    public OperationsEndpointContractTests(OperationsEndpointFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task InventoryReceipt_RejectsNonMainDestination()
    {
        var seed = await _factory.SeedAsync();
        using var client = _factory.CreateClient();
        client.AuthorizeAs(LenseeRoles.Admin, LenseePermissions.OperationsRead, LenseePermissions.OperationsWrite);

        var response = await client.PostAsJsonAsync("/api/v1/operations", new
        {
            operationType = "InventoryReceipt",
            destinationLocationId = seed.OnlineLocationId,
            lines = new[] { new { skuId = seed.SkuId, packQuantity = 1 } }
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Admin_CanConfirmInventoryReceiptIntoMainWarehouse()
    {
        var seed = await _factory.SeedAsync();
        using var client = _factory.CreateClient();
        client.AuthorizeAs(LenseeRoles.Admin, LenseePermissions.OperationsRead, LenseePermissions.OperationsWrite, LenseePermissions.InventoryRead);

        var operation = await CreateOperationAsync(client, new
        {
            operationType = "InventoryReceipt",
            destinationLocationId = seed.MainLocationId,
            receipt = new { supplierName = "Supplier", invoiceNumber = "INV-1" },
            lines = new[] { new { skuId = seed.SkuId, packQuantity = 8, lotNumber = "MAIN-1", expiryDate = "2028-06-01" } }
        });

        var confirm = await client.PostAsync($"/api/v1/operations/{operation.Id}/confirm", null);
        var balances = await client.GetFromJsonAsync<PagedContract<StockBalanceContract>>($"/api/v1/inventory/stock-balances?locationId={seed.MainLocationId}");

        Assert.Equal(HttpStatusCode.NoContent, confirm.StatusCode);
        Assert.Contains(balances!.Items, balance => balance.SkuId == seed.SkuId && balance.AvailablePacks == 8);
    }

    [Fact]
    public async Task WarehouseTransfer_ConfirmReserveShipReceive_MovesPacksBetweenWarehouses()
    {
        var seed = await _factory.SeedAsync(withMainStock: true);
        using var client = _factory.CreateClient();
        client.AuthorizeAs(LenseeRoles.Admin, LenseePermissions.OperationsRead, LenseePermissions.OperationsWrite, LenseePermissions.InventoryRead);

        var operation = await CreateOperationAsync(client, new
        {
            operationType = "WarehouseTransfer",
            sourceLocationId = seed.MainLocationId,
            destinationLocationId = seed.OnlineLocationId,
            lines = new[] { new { skuId = seed.SkuId, packQuantity = 4 } }
        });

        var confirm = await client.PostAsync($"/api/v1/operations/{operation.Id}/confirm", null);
        var afterReserve = await client.GetFromJsonAsync<PagedContract<OperationStockBalanceContract>>($"/api/v1/inventory/stock-balances?locationId={seed.MainLocationId}");
        var ship = await client.PostAsync($"/api/v1/operations/{operation.Id}/ship", null);
        var receive = await client.PostAsync($"/api/v1/operations/{operation.Id}/receive", null);
        var main = await client.GetFromJsonAsync<PagedContract<OperationStockBalanceContract>>($"/api/v1/inventory/stock-balances?locationId={seed.MainLocationId}");
        var online = await client.GetFromJsonAsync<PagedContract<OperationStockBalanceContract>>($"/api/v1/inventory/stock-balances?locationId={seed.OnlineLocationId}");

        Assert.Equal(HttpStatusCode.NoContent, confirm.StatusCode);
        Assert.Contains(afterReserve!.Items, balance => balance.SkuId == seed.SkuId && balance.AvailablePacks == 6 && balance.ReservedInWarehousePacks == 4);
        Assert.Equal(HttpStatusCode.NoContent, ship.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, receive.StatusCode);
        Assert.Contains(main!.Items, balance => balance.SkuId == seed.SkuId && balance.AvailablePacks == 6 && balance.ReservedInWarehousePacks == 0);
        Assert.Contains(online!.Items, balance => balance.SkuId == seed.SkuId && balance.AvailablePacks == 4);
    }

    [Fact]
    public async Task WarehouseTransfer_SkipsBatchesThatExpireBeforeOpenedDurationWindow()
    {
        var seed = await _factory.SeedAsync();
        await _factory.ReceiveMainStockAsync(seed.MainLocationId, seed.SkuId, "SHORT", new DateOnly(2026, 9, 1), 4);
        await _factory.ReceiveMainStockAsync(seed.MainLocationId, seed.SkuId, "VALID", new DateOnly(2028, 6, 1), 5);
        using var client = _factory.CreateClient();
        client.AuthorizeAs(LenseeRoles.Admin, LenseePermissions.OperationsRead, LenseePermissions.OperationsWrite, LenseePermissions.InventoryRead);

        var operation = await CreateOperationAsync(client, new
        {
            operationType = "WarehouseTransfer",
            sourceLocationId = seed.MainLocationId,
            destinationLocationId = seed.OnlineLocationId,
            lines = new[] { new { skuId = seed.SkuId, packQuantity = 3 } }
        });

        await client.PostAsync($"/api/v1/operations/{operation.Id}/confirm", null);
        await client.PostAsync($"/api/v1/operations/{operation.Id}/receive", null);
        var batches = await client.GetFromJsonAsync<PagedContract<BatchContract>>($"/api/v1/inventory/batches?locationId={seed.MainLocationId}&includeEmpty=true");

        Assert.Contains(batches!.Items, batch => batch.LotNumber == "SHORT" && batch.PackQuantity == 4);
        Assert.Contains(batches.Items, batch => batch.LotNumber == "VALID" && batch.PackQuantity == 2);
    }

    [Fact]
    public async Task WarehouseTransfer_RejectsWhenOnlyShortDatedBatchesExist()
    {
        var seed = await _factory.SeedAsync();
        await _factory.ReceiveMainStockAsync(seed.MainLocationId, seed.SkuId, "SHORT", new DateOnly(2026, 9, 1), 4);
        using var client = _factory.CreateClient();
        client.AuthorizeAs(LenseeRoles.Admin, LenseePermissions.OperationsRead, LenseePermissions.OperationsWrite, LenseePermissions.InventoryRead);

        var operation = await CreateOperationAsync(client, new
        {
            operationType = "WarehouseTransfer",
            sourceLocationId = seed.MainLocationId,
            destinationLocationId = seed.OnlineLocationId,
            lines = new[] { new { skuId = seed.SkuId, packQuantity = 3 } }
        });

        var confirm = await client.PostAsync($"/api/v1/operations/{operation.Id}/confirm", null);

        Assert.Equal(HttpStatusCode.BadRequest, confirm.StatusCode);
    }

    [Fact]
    public async Task CancelReservedTransfer_ReleasesMainWarehouseReservation()
    {
        var seed = await _factory.SeedAsync(withMainStock: true);
        using var client = _factory.CreateClient();
        client.AuthorizeAs(LenseeRoles.Admin, LenseePermissions.OperationsRead, LenseePermissions.OperationsWrite, LenseePermissions.InventoryRead);

        var operation = await CreateOperationAsync(client, new
        {
            operationType = "WarehouseTransfer",
            sourceLocationId = seed.MainLocationId,
            destinationLocationId = seed.OnlineLocationId,
            lines = new[] { new { skuId = seed.SkuId, packQuantity = 3 } }
        });

        await client.PostAsync($"/api/v1/operations/{operation.Id}/confirm", null);
        var cancel = await client.PostAsync($"/api/v1/operations/{operation.Id}/cancel", null);
        var balances = await client.GetFromJsonAsync<PagedContract<OperationStockBalanceContract>>($"/api/v1/inventory/stock-balances?locationId={seed.MainLocationId}");

        Assert.Equal(HttpStatusCode.NoContent, cancel.StatusCode);
        Assert.Contains(balances!.Items, balance => balance.SkuId == seed.SkuId && balance.AvailablePacks == 10 && balance.ReservedInWarehousePacks == 0);
    }

    [Fact]
    public async Task ReplenishmentReserve_CreatesReservedTransferForTargetShortage()
    {
        var seed = await _factory.SeedAsync(withMainStock: true);
        await _factory.SetTargetBalanceAsync(seed.OnlineLocationId, seed.SkuId, available: 2, target: 7);
        using var client = _factory.CreateClient();
        client.AuthorizeAs(LenseeRoles.Admin, LenseePermissions.OperationsRead, LenseePermissions.OperationsWrite, LenseePermissions.InventoryRead);

        var reserve = await client.PostAsJsonAsync("/api/v1/operations/replenishment/reserve", new { });
        var response = await reserve.Content.ReadFromJsonAsync<ReplenishmentReserveContract>();
        var operations = await client.GetFromJsonAsync<PagedContract<OperationListContract>>("/api/v1/operations?pageSize=10");
        var main = await client.GetFromJsonAsync<PagedContract<OperationStockBalanceContract>>($"/api/v1/inventory/stock-balances?locationId={seed.MainLocationId}");
        var online = await client.GetFromJsonAsync<PagedContract<OperationStockBalanceContract>>($"/api/v1/inventory/stock-balances?locationId={seed.OnlineLocationId}");

        Assert.Equal(HttpStatusCode.OK, reserve.StatusCode);
        Assert.Equal(1, response!.CreatedOperations);
        Assert.Equal(0, response.UnfilledPacks);
        Assert.Contains(operations!.Items, operation => operation.OperationType == "WarehouseTransfer" && operation.Status == "Reserved");
        Assert.Contains(main!.Items, balance => balance.SkuId == seed.SkuId && balance.AvailablePacks == 5 && balance.ReservedInWarehousePacks == 5);
        Assert.Contains(online!.Items, balance => balance.SkuId == seed.SkuId && balance.AvailablePacks == 2);
    }

    [Fact]
    public async Task ReplenishmentRows_CountReservedIncomingAgainstTarget()
    {
        var seed = await _factory.SeedAsync(withMainStock: true);
        await _factory.SetTargetBalanceAsync(seed.OnlineLocationId, seed.SkuId, available: 2, target: 7);
        using var client = _factory.CreateClient();
        client.AuthorizeAs(LenseeRoles.Admin, LenseePermissions.OperationsRead, LenseePermissions.OperationsWrite, LenseePermissions.InventoryRead);

        var before = await client.GetFromJsonAsync<IReadOnlyList<ReplenishmentRowContract>>("/api/v1/operations/replenishment");
        await client.PostAsJsonAsync("/api/v1/operations/replenishment/reserve", new { });
        var after = await client.GetFromJsonAsync<IReadOnlyList<ReplenishmentRowContract>>("/api/v1/operations/replenishment");

        Assert.Contains(before!, row => row.DestinationLocationId == seed.OnlineLocationId && row.ShortagePacks == 5 && row.IncomingPacks == 0);
        Assert.Contains(after!, row => row.DestinationLocationId == seed.OnlineLocationId && row.ShortagePacks == 0 && row.IncomingPacks == 5);
    }

    [Fact]
    public async Task ReplenishmentReserve_DoesNotCreateCancelledOperationWhenOnlyShortDatedStockExists()
    {
        var seed = await _factory.SeedAsync();
        await _factory.ReceiveMainStockAsync(seed.MainLocationId, seed.SkuId, "SHORT", new DateOnly(2026, 9, 1), 5);
        await _factory.SetTargetBalanceAsync(seed.OnlineLocationId, seed.SkuId, available: 0, target: 4);
        using var client = _factory.CreateClient();
        client.AuthorizeAs(LenseeRoles.Admin, LenseePermissions.OperationsRead, LenseePermissions.OperationsWrite, LenseePermissions.InventoryRead);

        var reserve = await client.PostAsJsonAsync("/api/v1/operations/replenishment/reserve", new { });
        var response = await reserve.Content.ReadFromJsonAsync<ReplenishmentReserveContract>();
        var operations = await client.GetFromJsonAsync<PagedContract<OperationListContract>>("/api/v1/operations?pageSize=10");

        Assert.Equal(HttpStatusCode.OK, reserve.StatusCode);
        Assert.Equal(0, response!.CreatedOperations);
        Assert.Equal(4, response.UnfilledPacks);
        Assert.DoesNotContain(operations!.Items, operation => operation.Status == "Cancelled");
    }

    [Fact]
    public async Task DailyReplenishment_DoesNotDropMainBelowTarget_AndCreatesLowMainStockAlert()
    {
        var seed = await _factory.SeedAsync(withMainStock: true);
        await _factory.SetTargetBalanceAsync(seed.MainLocationId, seed.SkuId, available: 10, target: 8);
        await _factory.SetTargetBalanceAsync(seed.OnlineLocationId, seed.SkuId, available: 0, target: 5);
        using var client = _factory.CreateClient();
        client.AuthorizeAs(LenseeRoles.Admin, LenseePermissions.OperationsRead, LenseePermissions.OperationsWrite, LenseePermissions.InventoryRead);

        var reserve = await client.PostAsJsonAsync("/api/v1/operations/replenishment/daily-reset", new { });
        var response = await reserve.Content.ReadFromJsonAsync<ReplenishmentReserveContract>();
        var main = await client.GetFromJsonAsync<PagedContract<OperationStockBalanceContract>>($"/api/v1/inventory/stock-balances?locationId={seed.MainLocationId}");
        var alerts = await _factory.GetNotificationCountAsync("TargetReplenishmentLowMainStock");

        Assert.Equal(HttpStatusCode.OK, reserve.StatusCode);
        Assert.Equal(1, response!.CreatedOperations);
        Assert.Equal(3, response.UnfilledPacks);
        Assert.Contains(main!.Items, balance => balance.SkuId == seed.SkuId && balance.AvailablePacks == 8 && balance.ReservedInWarehousePacks == 2);
        Assert.Equal(2, alerts);
    }

    [Fact]
    public async Task InventoryTransferBlockedBatches_ShowsShortDatedMainStock()
    {
        var seed = await _factory.SeedAsync();
        await _factory.ReceiveMainStockAsync(seed.MainLocationId, seed.SkuId, "SHORT", new DateOnly(2026, 9, 1), 5);
        using var client = _factory.CreateClient();
        client.AuthorizeAs(LenseeRoles.Admin, LenseePermissions.InventoryRead);

        var rows = await client.GetFromJsonAsync<IReadOnlyList<TransferBlockedBatchContract>>("/api/v1/inventory/transfer-blocked-batches");

        Assert.Contains(rows!, row =>
            row.SkuId == seed.SkuId &&
            row.LotNumber == "SHORT" &&
            row.PackQuantity == 5 &&
            row.MinimumTransferExpiryDate == new DateOnly(2026, 12, 28));
    }

    [Fact]
    public async Task InventoryTransferBlockedBatches_ShowsAlreadyExpiredStock()
    {
        var seed = await _factory.SeedAsync();
        await _factory.ReceiveMainStockAsync(seed.MainLocationId, seed.SkuId, "EXPIRED", new DateOnly(2026, 1, 1), 2);
        using var client = _factory.CreateClient();
        client.AuthorizeAs(LenseeRoles.Admin, LenseePermissions.InventoryRead);

        var rows = await client.GetFromJsonAsync<IReadOnlyList<TransferBlockedBatchContract>>("/api/v1/inventory/transfer-blocked-batches");

        Assert.Contains(rows!, row =>
            row.SkuId == seed.SkuId &&
            row.LotNumber == "EXPIRED" &&
            row.PackQuantity == 2 &&
            row.Reason == "Expired");
    }

    [Fact]
    public async Task CLevelAndAccountant_CannotMutateOperations()
    {
        var seed = await _factory.SeedAsync();
        using var cLevel = _factory.CreateClient();
        cLevel.AuthorizeAs(LenseeRoles.CLevel, LenseePermissions.OperationsRead);
        using var accountant = _factory.CreateClient();
        accountant.AuthorizeAs(LenseeRoles.Accountant, LenseePermissions.OperationsRead);

        var cLevelResponse = await cLevel.PostAsJsonAsync("/api/v1/operations", new { operationType = "InventoryReceipt", destinationLocationId = seed.MainLocationId, lines = new[] { new { skuId = seed.SkuId, packQuantity = 1 } } });
        var accountantResponse = await accountant.PostAsJsonAsync("/api/v1/operations", new { operationType = "InventoryReceipt", destinationLocationId = seed.MainLocationId, lines = new[] { new { skuId = seed.SkuId, packQuantity = 1 } } });

        Assert.Equal(HttpStatusCode.Forbidden, cLevelResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, accountantResponse.StatusCode);
    }

    private static async Task<OperationDetailContract> CreateOperationAsync(HttpClient client, object request)
    {
        var response = await client.PostAsJsonAsync("/api/v1/operations", request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<OperationDetailContract>())!;
    }
}

public sealed class OperationsEndpointFactory : WebApplicationFactory<Program>
{
    private readonly string _databaseName = $"operations-contracts-{Guid.NewGuid()}";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration(configuration =>
        {
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = "Host=localhost;Database=lensee_operations_contract_tests;Username=test;Password=test",
                ["Jwt:Secret"] = "OperationsContractTestsNeedASecret123!",
                ["Jwt:Issuer"] = "Lensee",
                ["Jwt:Audience"] = "Lensee.App"
            });
        });

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<DbContextOptions<CatalogDbContext>>();
            services.RemoveAll<DbContextOptions<InventoryDbContext>>();
            services.RemoveAll<DbContextOptions<NotificationsDbContext>>();
            services.RemoveAll<DbContextOptions<OperationsDbContext>>();
            services.RemoveAll<IAuditLogWriter>();
            services.AddDbContext<CatalogDbContext>(options => options.UseInMemoryDatabase(_databaseName));
            services.AddDbContext<InventoryDbContext>(options => options.UseInMemoryDatabase(_databaseName));
            services.AddDbContext<NotificationsDbContext>(options => options.UseInMemoryDatabase(_databaseName));
            services.AddDbContext<OperationsDbContext>(options => options.UseInMemoryDatabase(_databaseName));
            services.AddSingleton<IAuditLogWriter, NoOpAuditLogWriter>();

            services.AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = TestAuthHandler.TestScheme;
                options.DefaultChallengeScheme = TestAuthHandler.TestScheme;
                options.DefaultForbidScheme = TestAuthHandler.TestScheme;
            }).AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.TestScheme, _ => { });
        });
    }

    public async Task<OperationsSeed> SeedAsync(bool withMainStock = false)
    {
        using var scope = Services.CreateScope();
        var catalog = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        var inventory = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
        var operations = scope.ServiceProvider.GetRequiredService<OperationsDbContext>();
        var notifications = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();
        var ledger = scope.ServiceProvider.GetRequiredService<StockLedgerService>();
        var mainLocationId = Guid.NewGuid();
        var onlineLocationId = Guid.NewGuid();
        var categoryId = Guid.NewGuid();
        var brandId = Guid.NewGuid();
        var productId = Guid.NewGuid();
        var skuId = Guid.NewGuid();

        operations.OperationVersions.RemoveRange(operations.OperationVersions);
        operations.OperationLines.RemoveRange(operations.OperationLines);
        operations.InventoryReceiptHeaders.RemoveRange(operations.InventoryReceiptHeaders);
        operations.OperationLogs.RemoveRange(operations.OperationLogs);
        notifications.NotificationLogs.RemoveRange(notifications.NotificationLogs);
        notifications.AlertConfigs.RemoveRange(notifications.AlertConfigs);
        inventory.StockTransactions.RemoveRange(inventory.StockTransactions);
        inventory.InventoryBatches.RemoveRange(inventory.InventoryBatches);
        inventory.StockBalances.RemoveRange(inventory.StockBalances);
        inventory.Locations.RemoveRange(inventory.Locations);
        catalog.Skus.RemoveRange(catalog.Skus);
        catalog.Products.RemoveRange(catalog.Products);
        catalog.Brands.RemoveRange(catalog.Brands);
        catalog.Categories.RemoveRange(catalog.Categories);
        await operations.SaveChangesAsync();
        await notifications.SaveChangesAsync();
        await inventory.SaveChangesAsync();
        await catalog.SaveChangesAsync();

        inventory.Locations.AddRange(
            new Location { Id = mainLocationId, Name = $"Roxy {mainLocationId:N}", LocationType = "MainWarehouse", IsActive = true },
            new Location { Id = onlineLocationId, Name = $"Online {onlineLocationId:N}", LocationType = "Online", IsActive = true });
        catalog.Categories.Add(new Category { Id = categoryId, Name = $"Lenses {categoryId:N}" });
        catalog.Brands.Add(new Brand { Id = brandId, Name = $"Lansee {brandId:N}" });
        catalog.Products.Add(new Product
        {
            Id = productId,
            CategoryId = categoryId,
            BrandId = brandId,
            Name = $"Monthly Lens {productId:N}",
            ProductType = "Lens",
            ExpiryType = "Batch",
            OpenedExpiryDuration = "6 months",
            PiecesPerPack = 2,
            SellMode = "Both",
            ClinicalParams = "{}",
            ExtendedAttributes = "{}",
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        });
        catalog.Skus.Add(new Sku { Id = skuId, ProductId = productId, SkuCode = $"LEN-{skuId:N}", ColorName = "Hazel", IsActive = true });

        await catalog.SaveChangesAsync();
        await inventory.SaveChangesAsync();

        if (withMainStock)
        {
            await ledger.ReceiveAsync(mainLocationId, skuId, 10, Guid.NewGuid(), "MAIN-A", new DateOnly(2028, 6, 1));
        }

        return new OperationsSeed(mainLocationId, onlineLocationId, skuId);
    }

    public async Task SetTargetBalanceAsync(Guid locationId, Guid skuId, int available, int target)
    {
        using var scope = Services.CreateScope();
        var inventory = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
        var balance = await inventory.StockBalances.FirstOrDefaultAsync(value => value.LocationId == locationId && value.SkuId == skuId);
        if (balance is null)
        {
            inventory.StockBalances.Add(new StockBalance
            {
                Id = Guid.NewGuid(),
                LocationId = locationId,
                SkuId = skuId,
                AvailableQty = available,
                TargetQty = target,
                LastUpdated = DateTime.UtcNow
            });
        }
        else
        {
            balance.AvailableQty = available;
            balance.TargetQty = target;
            balance.LastUpdated = DateTime.UtcNow;
        }

        await inventory.SaveChangesAsync();
    }

    public async Task ReceiveMainStockAsync(Guid locationId, Guid skuId, string lotNumber, DateOnly expiryDate, int quantity)
    {
        using var scope = Services.CreateScope();
        var ledger = scope.ServiceProvider.GetRequiredService<StockLedgerService>();
        await ledger.ReceiveAsync(locationId, skuId, quantity, Guid.NewGuid(), lotNumber, expiryDate);
    }

    public async Task<int> GetNotificationCountAsync(string alertType)
    {
        using var scope = Services.CreateScope();
        var notifications = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();
        return await notifications.NotificationLogs.CountAsync(notification => notification.AlertType == alertType);
    }
}

public sealed record OperationsSeed(Guid MainLocationId, Guid OnlineLocationId, Guid SkuId);

public sealed record OperationDetailContract(Guid Id, string OperationType, string Status);

public sealed record OperationStockBalanceContract(Guid LocationId, Guid SkuId, int AvailablePacks, int ReservedInWarehousePacks);

public sealed record BatchContract(string? LotNumber, int PackQuantity);

public sealed record OperationListContract(Guid Id, string OperationType, string Status);

public sealed record ReplenishmentRowContract(Guid DestinationLocationId, Guid SkuId, int AvailablePacks, int IncomingPacks, int TargetPacks, int ShortagePacks);

public sealed record ReplenishmentReserveContract(int CreatedOperations, int UnfilledPacks);

public sealed record TransferBlockedBatchContract(Guid SkuId, string? LotNumber, int PackQuantity, DateOnly? MinimumTransferExpiryDate, string Reason);
