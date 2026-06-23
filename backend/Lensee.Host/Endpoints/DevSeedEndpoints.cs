using Lensee.Host.Infrastructure;
using Lensee.Modules.Catalog.Data;
using Lensee.Modules.Identity.Data;
using Lensee.Modules.Inventory.Data;
using Lensee.SharedKernel.Abstractions;
using Lensee.SharedKernel.Data;
using Lensee.SharedKernel.Security;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Lensee.Host.Endpoints;

public static class DevSeedEndpoints
{
    private static readonly Guid RoxyLocationId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid RetailLocationId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid OnlineLocationId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    public static RouteGroupBuilder MapDevSeedEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/v1/dev").WithTags("Development");

        group.MapPost("/seed", SeedAsync)
            .AllowAnonymous()
            .WithName("SeedDevelopmentData");

        return group;
    }

    private static async Task<Results<Ok<SeedResponse>, NotFound>> SeedAsync(
        IWebHostEnvironment environment,
        IdentityDbContext identityDbContext,
        InventoryDbContext inventoryDbContext,
        CatalogDbContext catalogDbContext,
        SharedDbContext sharedDbContext,
        IPasswordHasher passwordHasher,
        IClock clock,
        CancellationToken cancellationToken)
    {
        if (!environment.IsDevelopment())
        {
            return TypedResults.NotFound();
        }

        await SeedLocationsAsync(inventoryDbContext, cancellationToken);
        await SeedSettingsAsync(sharedDbContext, cancellationToken);
        await SeedPermissionsAsync(identityDbContext, cancellationToken);
        await SeedUsersAsync(identityDbContext, passwordHasher, clock, cancellationToken);
        await SeedCategoriesAsync(catalogDbContext, clock, cancellationToken);

        return TypedResults.Ok(new SeedResponse(
        [
            new SeedCredential("admin", "Admin123!", LenseeRoles.Admin),
            new SeedCredential("clevel", "CLevel123!", LenseeRoles.CLevel),
            new SeedCredential("accountant", "Accountant123!", LenseeRoles.Accountant),
            new SeedCredential("roxy_clerk", "Clerk123!", LenseeRoles.WarehouseClerk),
            new SeedCredential("retail_clerk", "Clerk123!", LenseeRoles.WarehouseClerk),
            new SeedCredential("online_clerk", "Clerk123!", LenseeRoles.WarehouseClerk)
        ]));
    }

    private static async Task SeedLocationsAsync(
        InventoryDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var locations = new[]
        {
            new Location
            {
                Id = RoxyLocationId,
                Name = "Roxy (Main)",
                LocationType = "MainWarehouse",
                IsActive = true
            },
            new Location
            {
                Id = RetailLocationId,
                Name = "Mohamed Naguib (Retail)",
                LocationType = "SubWarehouse",
                IsActive = true
            },
            new Location
            {
                Id = OnlineLocationId,
                Name = "Online",
                LocationType = "Online",
                IsActive = true
            }
        };

        foreach (var location in locations)
        {
            var existing = await dbContext.Locations.FindAsync([location.Id], cancellationToken);

            if (existing is null)
            {
                dbContext.Locations.Add(location);
            }
            else
            {
                existing.Name = location.Name;
                existing.LocationType = location.LocationType;
                existing.IsActive = location.IsActive;
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static async Task SeedSettingsAsync(
        SharedDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var settings = new[]
        {
            new SystemSetting
            {
                Key = "low_stock_threshold_default",
                Value = "10",
                Description = "Default low stock alert threshold (pieces)"
            },
            new SystemSetting
            {
                Key = "reserve_unresolved_days",
                Value = "7",
                Description = "Days before an unresolved reserve triggers alert"
            },
            new SystemSetting
            {
                Key = "in_warehouse_expiry_months",
                Value = "3",
                Description = "Months before expiry to fire in-warehouse alert"
            },
            new SystemSetting
            {
                Key = "merchant_held_expiry_months",
                Value = "18",
                Description = "Months before expiry to fire merchant-held alert"
            },
            new SystemSetting
            {
                Key = "outstanding_balance_days",
                Value = "30",
                Description = "Days since last payment to fire balance notification flags"
            }
        };

        foreach (var setting in settings)
        {
            var existing = await dbContext.SystemSettings.FindAsync([setting.Key], cancellationToken);

            if (existing is null)
            {
                dbContext.SystemSettings.Add(setting);
            }
            else
            {
                existing.Value = setting.Value;
                existing.Description = setting.Description;
                existing.UpdatedAt = DateTime.Now;
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static async Task SeedPermissionsAsync(
        IdentityDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var rolePermissions = LenseeRoles.All
            .SelectMany(role => LenseePermissions.ForRole(role)
                .Select(permission => new { Role = role, Permission = permission }))
            .ToList();

        foreach (var rolePermission in rolePermissions)
        {
            var exists = await dbContext.RolesPermissions.AnyAsync(
                value =>
                    value.Role == rolePermission.Role &&
                    value.Permission == rolePermission.Permission,
                cancellationToken);

            if (!exists)
            {
                dbContext.RolesPermissions.Add(new RolesPermission
                {
                    Id = Guid.NewGuid(),
                    Role = rolePermission.Role,
                    Permission = rolePermission.Permission
                });
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static async Task SeedUsersAsync(
        IdentityDbContext dbContext,
        IPasswordHasher passwordHasher,
        IClock clock,
        CancellationToken cancellationToken)
    {
        var users = new[]
        {
            new UserSeed("admin", "Admin123!", "Lansee Admin", LenseeRoles.Admin, null),
            new UserSeed("clevel", "CLevel123!", "C-Level Executive", LenseeRoles.CLevel, null),
            new UserSeed("accountant", "Accountant123!", "Lansee Accountant", LenseeRoles.Accountant, null),
            new UserSeed("roxy_clerk", "Clerk123!", "Roxy Warehouse Clerk", LenseeRoles.WarehouseClerk, RoxyLocationId),
            new UserSeed("retail_clerk", "Clerk123!", "Retail Warehouse Clerk", LenseeRoles.WarehouseClerk, RetailLocationId),
            new UserSeed("online_clerk", "Clerk123!", "Online Warehouse Clerk", LenseeRoles.WarehouseClerk, OnlineLocationId)
        };

        foreach (var seed in users)
        {
            var existing = await dbContext.Users.SingleOrDefaultAsync(
                value => value.Username == seed.Username,
                cancellationToken);

            if (existing is null)
            {
                dbContext.Users.Add(new User
                {
                    Id = Guid.NewGuid(),
                    Username = seed.Username,
                    PasswordHash = passwordHasher.Hash(seed.Password),
                    FullName = seed.FullName,
                    Role = seed.Role,
                    LocationId = seed.LocationId,
                    IsActive = true,
                    CreatedAt = clock.EgyptNow
                });
            }
            else
            {
                // Important:
                // This replaces placeholder hashes like "<hash:Admin123!>"
                // with real PBKDF2 hashes generated by your PasswordHasher.
                existing.PasswordHash = passwordHasher.Hash(seed.Password);
                existing.FullName = seed.FullName;
                existing.Role = seed.Role;
                existing.LocationId = seed.LocationId;
                existing.IsActive = true;
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static async Task SeedCategoriesAsync(
        CatalogDbContext dbContext,
        IClock clock,
        CancellationToken cancellationToken)
    {
        var categories = new[]
        {
            new CategorySeed(Guid.Parse("10000000-0000-0000-0000-000000000001"), null, "Products"),
            new CategorySeed(Guid.Parse("10000000-0000-0000-0000-000000000002"), Guid.Parse("10000000-0000-0000-0000-000000000001"), "Lenses"),
            new CategorySeed(Guid.Parse("10000000-0000-0000-0000-000000000003"), Guid.Parse("10000000-0000-0000-0000-000000000002"), "Colored Lenses"),
            new CategorySeed(Guid.Parse("10000000-0000-0000-0000-000000000004"), Guid.Parse("10000000-0000-0000-0000-000000000002"), "Medical Lenses"),
            new CategorySeed(Guid.Parse("10000000-0000-0000-0000-000000000005"), Guid.Parse("10000000-0000-0000-0000-000000000004"), "Plain Medical"),
            new CategorySeed(Guid.Parse("10000000-0000-0000-0000-000000000006"), Guid.Parse("10000000-0000-0000-0000-000000000004"), "Colored Medical"),
            new CategorySeed(Guid.Parse("10000000-0000-0000-0000-000000000007"), Guid.Parse("10000000-0000-0000-0000-000000000001"), "Solutions"),
            new CategorySeed(Guid.Parse("10000000-0000-0000-0000-000000000008"), Guid.Parse("10000000-0000-0000-0000-000000000007"), "Preservation / Conservative Solution")
        };

        foreach (var seed in categories)
        {
            var existing = await dbContext.Categories.FindAsync([seed.Id], cancellationToken);

            if (existing is null)
            {
                dbContext.Categories.Add(new Category
                {
                    Id = seed.Id,
                    ParentId = seed.ParentId,
                    Name = seed.Name,
                    CreatedAt = clock.EgyptNow
                });
            }
            else
            {
                existing.ParentId = seed.ParentId;
                existing.Name = seed.Name;
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private sealed record UserSeed(
        string Username,
        string Password,
        string FullName,
        string Role,
        Guid? LocationId);

    private sealed record CategorySeed(
        Guid Id,
        Guid? ParentId,
        string Name);
}

public sealed record SeedResponse(IReadOnlyList<SeedCredential> Credentials);

public sealed record SeedCredential(string Username, string Password, string Role);