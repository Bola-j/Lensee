using Lensee.Modules.Operations.Data;
using Microsoft.EntityFrameworkCore;

namespace Lensee.Host.Infrastructure;

public static class DatabaseCompatibility
{
    public static async Task EnsureDevelopmentSchemaAsync(IServiceProvider services, IWebHostEnvironment environment)
    {
        if (!environment.IsDevelopment())
        {
            return;
        }

        using var scope = services.CreateScope();
        var logger = scope.ServiceProvider
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger("DatabaseCompatibility");
        var operationsDbContext = scope.ServiceProvider.GetRequiredService<OperationsDbContext>();

        try
        {
            await operationsDbContext.Database.ExecuteSqlRawAsync("""
                alter table if exists operations.operation_logs
                    drop constraint if exists chk_op_type;

                alter table if exists operations.operation_logs
                    add constraint chk_op_type
                    check (operation_type in (
                        'InventoryReceipt',
                        'WarehouseTransfer',
                        'WholesaleSale',
                        'RetailSale',
                        'Reserve',
                        'Supply',
                        'WriteOff',
                        'Change',
                        'Return'
                    ));

                alter table if exists operations.operation_logs
                    drop constraint if exists chk_op_status;

                alter table if exists operations.operation_logs
                    add constraint chk_op_status
                    check (status in (
                        'Draft',
                        'Confirmed',
                        'Reserved',
                        'Shipped',
                        'Received',
                        'Cancelled'
                    ));
                """);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not update development operation constraints.");
        }
    }
}
