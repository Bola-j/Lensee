using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore;

namespace Lensee.Modules.Inventory.Data;

public partial class InventoryDbContext : DbContext
{
    public InventoryDbContext(DbContextOptions<InventoryDbContext> options)
        : base(options)
    {
    }

    public virtual DbSet<InventoryBatch> InventoryBatches { get; set; }

    public virtual DbSet<Location> Locations { get; set; }

    public virtual DbSet<StockBalance> StockBalances { get; set; }

    public virtual DbSet<StockTransaction> StockTransactions { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasPostgresExtension("uuid-ossp");

        modelBuilder.Entity<InventoryBatch>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("inventory_batches_pkey");

            entity.ToTable("inventory_batches", "inventory");

            entity.HasIndex(e => e.ExpiryDate, "idx_inv_batches_expiry").HasFilter("(expiry_date IS NOT NULL)");

            entity.HasIndex(e => new { e.LocationId, e.SkuId }, "idx_inv_batches_location_sku");

            entity.Property(e => e.Id)
                .HasDefaultValueSql("uuid_generate_v4()")
                .HasColumnName("id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("CURRENT_TIMESTAMP")
                .HasColumnType("timestamp without time zone")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.CreatedFrom).HasColumnName("created_from");
            entity.Property(e => e.ExpiryDate).HasColumnName("expiry_date");
            entity.Property(e => e.LocationId).HasColumnName("location_id");
            entity.Property(e => e.LotNumber)
                .HasMaxLength(100)
                .HasColumnName("lot_number");
            entity.Property(e => e.Notes).HasColumnName("notes");
            entity.Property(e => e.Quantity)
                .HasDefaultValue(0)
                .HasColumnName("quantity");
            entity.Property(e => e.SkuId).HasColumnName("sku_id");

            entity.HasOne(d => d.Location).WithMany(p => p.InventoryBatches)
                .HasForeignKey(d => d.LocationId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("inventory_batches_location_id_fkey");
        });

        modelBuilder.Entity<Location>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("locations_pkey");

            entity.ToTable("locations", "inventory");

            entity.Property(e => e.Id)
                .HasDefaultValueSql("uuid_generate_v4()")
                .HasColumnName("id");
            entity.Property(e => e.IsActive)
                .HasDefaultValue(true)
                .HasColumnName("is_active");
            entity.Property(e => e.LocationType)
                .HasMaxLength(50)
                .HasColumnName("location_type");
            entity.Property(e => e.Name)
                .HasMaxLength(100)
                .HasColumnName("name");
        });

        modelBuilder.Entity<StockBalance>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("stock_balances_pkey");

            entity.ToTable("stock_balances", "inventory");

            entity.HasIndex(e => new { e.LocationId, e.AvailableQty }, "idx_stock_balances_available");

            entity.HasIndex(e => e.SkuId, "idx_stock_balances_sku");

            entity.HasIndex(e => new { e.LocationId, e.SkuId }, "uq_location_sku").IsUnique();

            entity.Property(e => e.Id)
                .HasDefaultValueSql("uuid_generate_v4()")
                .HasColumnName("id");
            entity.Property(e => e.AvailableQty)
                .HasDefaultValue(0)
                .HasColumnName("available_qty");
            entity.Property(e => e.LastUpdated)
                .HasDefaultValueSql("CURRENT_TIMESTAMP")
                .HasColumnType("timestamp without time zone")
                .HasColumnName("last_updated");
            entity.Property(e => e.LocationId).HasColumnName("location_id");
            entity.Property(e => e.ReservedInWarehouseQty)
                .HasDefaultValue(0)
                .HasColumnName("reserved_in_warehouse_qty");
            entity.Property(e => e.ReservedWithRepQty)
                .HasDefaultValue(0)
                .HasColumnName("reserved_with_rep_qty");
            entity.Property(e => e.RowVersion)
                .HasDefaultValue(0)
                .HasColumnName("row_version");
            entity.Property(e => e.SkuId).HasColumnName("sku_id");
            entity.Property(e => e.TargetQty).HasColumnName("target_qty");

            entity.HasOne(d => d.Location).WithMany(p => p.StockBalances)
                .HasForeignKey(d => d.LocationId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("stock_balances_location_id_fkey");
        });

        modelBuilder.Entity<StockTransaction>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("stock_transactions_pkey");

            entity.ToTable("stock_transactions", "inventory");

            entity.HasIndex(e => e.ReferenceOperationId, "idx_stock_txn_operation").HasFilter("(reference_operation_id IS NOT NULL)");

            entity.HasIndex(e => new { e.SkuId, e.LocationId, e.CreatedAt }, "idx_stock_txn_sku_location").IsDescending(false, false, true);

            entity.HasIndex(e => e.UserId, "idx_stock_txn_user");

            entity.Property(e => e.Id)
                .HasDefaultValueSql("uuid_generate_v4()")
                .HasColumnName("id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("CURRENT_TIMESTAMP")
                .HasColumnType("timestamp without time zone")
                .HasColumnName("created_at");
            entity.Property(e => e.LocationId).HasColumnName("location_id");
            entity.Property(e => e.QuantityChange).HasColumnName("quantity_change");
            entity.Property(e => e.ReferenceOperationId).HasColumnName("reference_operation_id");
            entity.Property(e => e.SkuId).HasColumnName("sku_id");
            entity.Property(e => e.TransactionType)
                .HasMaxLength(50)
                .HasColumnName("transaction_type");
            entity.Property(e => e.UserId).HasColumnName("user_id");

            entity.HasOne(d => d.Location).WithMany(p => p.StockTransactions)
                .HasForeignKey(d => d.LocationId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("stock_transactions_location_id_fkey");
        });

        OnModelCreatingPartial(modelBuilder);
    }

    partial void OnModelCreatingPartial(ModelBuilder modelBuilder);
}
