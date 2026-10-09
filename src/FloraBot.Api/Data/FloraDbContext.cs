using System;
using System.Collections.Generic;
using FloraBot.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace FloraBot.Api.Data;

public partial class FloraDbContext : DbContext
{
    public FloraDbContext(DbContextOptions<FloraDbContext> options)
        : base(options)
    {
    }

    public virtual DbSet<Accessory> Accessories { get; set; }

    public virtual DbSet<Attachment> Attachments { get; set; }

    public virtual DbSet<AuditLog> AuditLogs { get; set; }

    public virtual DbSet<Bouquet> Bouquets { get; set; }

    public virtual DbSet<Dispute> Disputes { get; set; }

    public virtual DbSet<FlowerProduct> FlowerProducts { get; set; }

    public virtual DbSet<GiftSurvey> GiftSurveys { get; set; }

    public virtual DbSet<InventoryLog> InventoryLogs { get; set; }

    public virtual DbSet<Kiosk> Kiosks { get; set; }

    public virtual DbSet<LedgerEntry> LedgerEntries { get; set; }

    public virtual DbSet<Order> Orders { get; set; }

    public virtual DbSet<OrderItem> OrderItems { get; set; }

    public virtual DbSet<Payment> Payments { get; set; }

    public virtual DbSet<Seller> Sellers { get; set; }

    public virtual DbSet<Slot> Slots { get; set; }

    public virtual DbSet<SlotAssignment> SlotAssignments { get; set; }

    public virtual DbSet<Subscription> Subscriptions { get; set; }

    public virtual DbSet<SubscriptionPackage> SubscriptionPackages { get; set; }

    public virtual DbSet<SystemSetting> SystemSettings { get; set; }

    public virtual DbSet<UnlockToken> UnlockTokens { get; set; }

    public virtual DbSet<User> Users { get; set; }

    public virtual DbSet<VSellerBalance> VSellerBalances { get; set; }

    public virtual DbSet<WithdrawalRequest> WithdrawalRequests { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder
            .HasPostgresExtension("btree_gist")
            .HasPostgresExtension("pgcrypto");

        modelBuilder.Entity<Accessory>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("accessories_pkey");

            entity.ToTable("accessories", "kiosk_ops");

            entity.HasIndex(e => new { e.KioskId, e.SellerId, e.Name }, "accessories_kiosk_id_seller_id_name_key").IsUnique();

            entity.Property(e => e.Id)
                .HasDefaultValueSql("uuid_v7()")
                .HasColumnName("id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("app_now()")
                .HasColumnName("created_at");
            entity.Property(e => e.KioskId).HasColumnName("kiosk_id");
            entity.Property(e => e.Name).HasColumnName("name");
            entity.Property(e => e.Price).HasColumnName("price");
            entity.Property(e => e.SellerId).HasColumnName("seller_id");
            entity.Property(e => e.Status)
                .HasDefaultValueSql("'ACTIVE'::text")
                .HasColumnName("status");
            entity.Property(e => e.StockQuantity).HasColumnName("stock_quantity");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("app_now()")
                .HasColumnName("updated_at");

            entity.HasOne(d => d.Kiosk).WithMany(p => p.Accessories)
                .HasForeignKey(d => d.KioskId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("accessories_kiosk_id_fkey");
        });

        modelBuilder.Entity<Attachment>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("attachments_pkey");

            entity.ToTable("attachments", "notify");

            entity.HasIndex(e => new { e.OwnerType, e.OwnerId }, "attachments_owner_type_owner_id_idx");

            entity.Property(e => e.Id)
                .HasDefaultValueSql("uuid_v7()")
                .HasColumnName("id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("app_now()")
                .HasColumnName("created_at");
            entity.Property(e => e.FileUrl).HasColumnName("file_url");
            entity.Property(e => e.MimeType).HasColumnName("mime_type");
            entity.Property(e => e.OwnerId).HasColumnName("owner_id");
            entity.Property(e => e.OwnerService).HasColumnName("owner_service");
            entity.Property(e => e.OwnerType).HasColumnName("owner_type");
            entity.Property(e => e.Phase).HasColumnName("phase");
            entity.Property(e => e.Sha256).HasColumnName("sha256");
            entity.Property(e => e.SizeBytes).HasColumnName("size_bytes");
            entity.Property(e => e.UploadedBy).HasColumnName("uploaded_by");
        });

        modelBuilder.Entity<AuditLog>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("audit_logs_pkey");

            entity.ToTable("audit_logs", "notify");

            entity.HasIndex(e => new { e.EntityType, e.EntityId, e.Id }, "audit_logs_entity_type_entity_id_id_idx");

            entity.Property(e => e.Id)
                .UseIdentityAlwaysColumn()
                .HasColumnName("id");
            entity.Property(e => e.Action).HasColumnName("action");
            entity.Property(e => e.ActorId).HasColumnName("actor_id");
            entity.Property(e => e.ActorType).HasColumnName("actor_type");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("app_clock()")
                .HasColumnName("created_at");
            entity.Property(e => e.EntityId).HasColumnName("entity_id");
            entity.Property(e => e.EntityType).HasColumnName("entity_type");
            entity.Property(e => e.Payload)
                .HasColumnType("jsonb")
                .HasColumnName("payload");
        });

        modelBuilder.Entity<Bouquet>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("bouquets_pkey");

            entity.ToTable("bouquets", "kiosk_ops");

            entity.HasIndex(e => e.QrCode, "bouquets_qr_code_key").IsUnique();

            entity.HasIndex(e => e.SellableUntil, "bouquets_sellable_idx").HasFilter("(status = 'STOCKED'::text)");

            entity.Property(e => e.Id)
                .HasDefaultValueSql("uuid_v7()")
                .HasColumnName("id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("app_now()")
                .HasColumnName("created_at");
            entity.Property(e => e.PriceSnapshot).HasColumnName("price_snapshot");
            entity.Property(e => e.ProductId).HasColumnName("product_id");
            entity.Property(e => e.QrCode).HasColumnName("qr_code");
            entity.Property(e => e.SellableUntil).HasColumnName("sellable_until");
            entity.Property(e => e.SellerId).HasColumnName("seller_id");
            entity.Property(e => e.Status)
                .HasDefaultValueSql("'STOCKED'::text")
                .HasColumnName("status");
            entity.Property(e => e.StockedAt).HasColumnName("stocked_at");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("app_now()")
                .HasColumnName("updated_at");
        });

        modelBuilder.Entity<Dispute>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("disputes_pkey");

            entity.ToTable("disputes", "ordering");

            entity.HasIndex(e => new { e.KioskId, e.Status }, "disputes_kiosk_id_status_idx");

            entity.HasIndex(e => e.OrderId, "disputes_one_complaint_per_order")
                .IsUnique()
                .HasFilter("(kind = 'COMPLAINT'::text)");

            entity.Property(e => e.Id)
                .HasDefaultValueSql("uuid_v7()")
                .HasColumnName("id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("app_now()")
                .HasColumnName("created_at");
            entity.Property(e => e.DecidedBy).HasColumnName("decided_by");
            entity.Property(e => e.Decision).HasColumnName("decision");
            entity.Property(e => e.Kind).HasColumnName("kind");
            entity.Property(e => e.KioskId).HasColumnName("kiosk_id");
            entity.Property(e => e.OrderId).HasColumnName("order_id");
            entity.Property(e => e.Reason).HasColumnName("reason");
            entity.Property(e => e.RefundAmount).HasColumnName("refund_amount");
            entity.Property(e => e.ReportedBy).HasColumnName("reported_by");
            entity.Property(e => e.ResolvedAt).HasColumnName("resolved_at");
            entity.Property(e => e.SlotId).HasColumnName("slot_id");
            entity.Property(e => e.Status)
                .HasDefaultValueSql("'OPEN'::text")
                .HasColumnName("status");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("app_now()")
                .HasColumnName("updated_at");

            entity.HasOne(d => d.Order).WithOne(p => p.Dispute)
                .HasForeignKey<Dispute>(d => d.OrderId)
                .HasConstraintName("disputes_order_id_fkey");
        });

        modelBuilder.Entity<FlowerProduct>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("flower_products_pkey");

            entity.ToTable("flower_products", "catalog");

            entity.HasIndex(e => new { e.SellerId, e.Status }, "flower_products_seller_id_status_idx");

            entity.HasIndex(e => e.Tags, "flower_products_tags_idx").HasMethod("gin");

            entity.Property(e => e.Id)
                .HasDefaultValueSql("uuid_v7()")
                .HasColumnName("id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("app_now()")
                .HasColumnName("created_at");
            entity.Property(e => e.Description).HasColumnName("description");
            entity.Property(e => e.Name).HasColumnName("name");
            entity.Property(e => e.Price).HasColumnName("price");
            entity.Property(e => e.SellerId).HasColumnName("seller_id");
            entity.Property(e => e.ShelfLifeHours).HasColumnName("shelf_life_hours");
            entity.Property(e => e.InventoryKind)
                .HasDefaultValueSql("'LEGACY_FRESH'::text")
                .HasColumnName("inventory_kind");
            entity.Property(e => e.LengthCm)
                .HasPrecision(6, 2)
                .HasColumnName("length_cm");
            entity.Property(e => e.WidthCm)
                .HasPrecision(6, 2)
                .HasColumnName("width_cm");
            entity.Property(e => e.HeightCm)
                .HasPrecision(6, 2)
                .HasColumnName("height_cm");
            entity.Property(e => e.Status)
                .HasDefaultValueSql("'DRAFT'::text")
                .HasColumnName("status");
            entity.Property(e => e.Tags)
                .HasDefaultValueSql("'{}'::text[]")
                .HasColumnName("tags");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("app_now()")
                .HasColumnName("updated_at");
        });

        modelBuilder.Entity<GiftSurvey>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("gift_surveys_pkey");

            entity.ToTable("gift_surveys", "ai");

            entity.HasIndex(e => e.CustomerId, "gift_surveys_customer_id_idx").HasFilter("(customer_id IS NOT NULL)");

            entity.HasIndex(e => new { e.KioskId, e.CreatedAt }, "gift_surveys_kiosk_id_created_at_idx");

            entity.Property(e => e.Id)
                .HasDefaultValueSql("uuid_v7()")
                .HasColumnName("id");
            entity.Property(e => e.AgeGroup).HasColumnName("age_group");
            entity.Property(e => e.BudgetMax).HasColumnName("budget_max");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("app_now()")
                .HasColumnName("created_at");
            entity.Property(e => e.CustomerId).HasColumnName("customer_id");
            entity.Property(e => e.KioskId).HasColumnName("kiosk_id");
            entity.Property(e => e.LatencyMs).HasColumnName("latency_ms");
            entity.Property(e => e.MessageTone).HasColumnName("message_tone");
            entity.Property(e => e.ModelVersion).HasColumnName("model_version");
            entity.Property(e => e.Occasion).HasColumnName("occasion");
            entity.Property(e => e.PurchasedOrderId).HasColumnName("purchased_order_id");
            entity.Property(e => e.RecipientType).HasColumnName("recipient_type");
            entity.Property(e => e.Results)
                .HasDefaultValueSql("'[]'::jsonb")
                .HasColumnType("jsonb")
                .HasColumnName("results");
            entity.Property(e => e.SelectedProductId).HasColumnName("selected_product_id");
            entity.Property(e => e.SessionId).HasColumnName("session_id");
            entity.Property(e => e.Source).HasColumnName("source");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("app_now()")
                .HasColumnName("updated_at");
        });

        modelBuilder.Entity<InventoryLog>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("inventory_logs_pkey");

            entity.ToTable("inventory_logs", "kiosk_ops");

            entity.HasIndex(e => e.BatchId, "inventory_logs_batch_id_idx");

            entity.HasIndex(e => new { e.BouquetId, e.Seq }, "inventory_logs_bouquet_id_seq_idx");

            entity.HasIndex(e => e.Seq, "inventory_logs_seq_key").IsUnique();

            entity.Property(e => e.Id)
                .HasDefaultValueSql("uuid_v7()")
                .HasColumnName("id");
            entity.Property(e => e.BatchId).HasColumnName("batch_id");
            entity.Property(e => e.BouquetId).HasColumnName("bouquet_id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("app_clock()")
                .HasColumnName("created_at");
            entity.Property(e => e.MovementType).HasColumnName("movement_type");
            entity.Property(e => e.OrderId).HasColumnName("order_id");
            entity.Property(e => e.PerformedBy).HasColumnName("performed_by");
            entity.Property(e => e.Reason).HasColumnName("reason");
            entity.Property(e => e.Seq)
                .ValueGeneratedOnAdd()
                .UseIdentityAlwaysColumn()
                .HasColumnName("seq");
            entity.Property(e => e.SlotId).HasColumnName("slot_id");

            entity.HasOne(d => d.Bouquet).WithMany(p => p.InventoryLogs)
                .HasForeignKey(d => d.BouquetId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("inventory_logs_bouquet_id_fkey");

            entity.HasOne(d => d.Slot).WithMany(p => p.InventoryLogs)
                .HasForeignKey(d => d.SlotId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("inventory_logs_slot_id_fkey");
        });

        modelBuilder.Entity<Kiosk>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("kiosks_pkey");

            entity.ToTable("kiosks", "kiosk_ops");

            entity.HasIndex(e => e.Code, "kiosks_code_key").IsUnique();

            entity.HasIndex(e => e.HardwareId, "kiosks_hardware_id_key").IsUnique();

            entity.HasIndex(e => e.MqttClientId, "kiosks_mqtt_client_id_key").IsUnique();

            entity.Property(e => e.Id)
                .HasDefaultValueSql("uuid_v7()")
                .HasColumnName("id");
            entity.Property(e => e.Address).HasColumnName("address");
            entity.Property(e => e.ApiKeyHash).HasColumnName("api_key_hash");
            entity.Property(e => e.Code).HasColumnName("code");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("app_now()")
                .HasColumnName("created_at");
            entity.Property(e => e.HardwareId).HasColumnName("hardware_id");
            entity.Property(e => e.LastHeartbeatAt).HasColumnName("last_heartbeat_at");
            entity.Property(e => e.MqttClientId).HasColumnName("mqtt_client_id");
            entity.Property(e => e.Name).HasColumnName("name");
            entity.Property(e => e.Region).HasColumnName("region");
            entity.Property(e => e.Status)
                .HasDefaultValueSql("'OFFLINE'::text")
                .HasColumnName("status");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("app_now()")
                .HasColumnName("updated_at");
        });

        modelBuilder.Entity<LedgerEntry>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("ledger_entries_pkey");

            entity.ToTable("ledger_entries", "payment");

            entity.HasIndex(e => e.JournalId, "ledger_entries_journal_id_idx");

            entity.HasIndex(e => new { e.RefId, e.Account }, "ledger_entries_ref_id_account_idx");

            entity.HasIndex(e => new { e.SellerId, e.Account }, "ledger_entries_seller_id_account_idx");

            entity.HasIndex(e => new { e.RefType, e.RefId, e.Account, e.SellerId }, "ledger_once")
                .IsUnique()
                .AreNullsDistinct(false);

            entity.Property(e => e.Id)
                .HasDefaultValueSql("uuid_v7()")
                .HasColumnName("id");
            entity.Property(e => e.Account).HasColumnName("account");
            entity.Property(e => e.Amount).HasColumnName("amount");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("app_now()")
                .HasColumnName("created_at");
            entity.Property(e => e.JournalId).HasColumnName("journal_id");
            entity.Property(e => e.Memo).HasColumnName("memo");
            entity.Property(e => e.RefId).HasColumnName("ref_id");
            entity.Property(e => e.RefType).HasColumnName("ref_type");
            entity.Property(e => e.SellerId).HasColumnName("seller_id");
        });

        modelBuilder.Entity<Order>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("orders_pkey");

            entity.ToTable("orders", "ordering");

            entity.HasIndex(e => e.CheckoutId, "orders_checkout_id_idx");

            entity.HasIndex(e => new { e.CustomerId, e.CreatedAt }, "orders_customer_id_created_at_idx");

            entity.HasIndex(e => e.OrderCode, "orders_order_code_key").IsUnique();

            entity.HasIndex(e => new { e.SellerId, e.CreatedAt }, "orders_seller_id_created_at_idx");

            entity.HasIndex(e => e.TrackingToken, "orders_tracking_token_key").IsUnique();

            entity.Property(e => e.Id)
                .HasDefaultValueSql("uuid_v7()")
                .HasColumnName("id");
            entity.Property(e => e.CheckoutId).HasColumnName("checkout_id");
            entity.Property(e => e.CompletedAt).HasColumnName("completed_at");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("app_now()")
                .HasColumnName("created_at");
            entity.Property(e => e.CustomerId).HasColumnName("customer_id");
            entity.Property(e => e.DiscountAmount).HasColumnName("discount_amount");
            entity.Property(e => e.EcardContent).HasColumnName("ecard_content");
            entity.Property(e => e.KioskId).HasColumnName("kiosk_id");
            entity.Property(e => e.OrderCode).HasColumnName("order_code");
            entity.Property(e => e.PointsEarned).HasColumnName("points_earned");
            entity.Property(e => e.PointsRedeemed).HasColumnName("points_redeemed");
            entity.Property(e => e.RefundBankAccountEnc).HasColumnName("refund_bank_account_enc");
            entity.Property(e => e.RefundBankHolder).HasColumnName("refund_bank_holder");
            entity.Property(e => e.RefundBankName).HasColumnName("refund_bank_name");
            entity.Property(e => e.SellerId).HasColumnName("seller_id");
            entity.Property(e => e.Status)
                .HasDefaultValueSql("'AWAITING_PAYMENT'::text")
                .HasColumnName("status");
            entity.Property(e => e.Subtotal).HasColumnName("subtotal");
            entity.Property(e => e.TotalAmount).HasColumnName("total_amount");
            entity.Property(e => e.TrackingToken).HasColumnName("tracking_token");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("app_now()")
                .HasColumnName("updated_at");
        });

        modelBuilder.Entity<OrderItem>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("order_items_pkey");

            entity.ToTable("order_items", "ordering");

            entity.HasIndex(e => e.BouquetId, "order_items_bouquet_once")
                .IsUnique()
                .HasFilter("((bouquet_id IS NOT NULL) AND (line_status = 'ACTIVE'::text))");

            entity.Property(e => e.Id)
                .HasDefaultValueSql("uuid_v7()")
                .HasColumnName("id");
            entity.Property(e => e.AccessoryId).HasColumnName("accessory_id");
            entity.Property(e => e.BouquetId).HasColumnName("bouquet_id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("app_now()")
                .HasColumnName("created_at");
            entity.Property(e => e.ItemType).HasColumnName("item_type");
            entity.Property(e => e.LineStatus)
                .HasDefaultValueSql("'ACTIVE'::text")
                .HasColumnName("line_status");
            entity.Property(e => e.LineTotal).HasColumnName("line_total");
            entity.Property(e => e.NameSnapshot).HasColumnName("name_snapshot");
            entity.Property(e => e.OrderId).HasColumnName("order_id");
            entity.Property(e => e.Quantity).HasColumnName("quantity");
            entity.Property(e => e.SlotId).HasColumnName("slot_id");
            entity.Property(e => e.UnitPrice).HasColumnName("unit_price");

            entity.HasOne(d => d.Order).WithMany(p => p.OrderItems)
                .HasForeignKey(d => d.OrderId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("order_items_order_id_fkey");
        });

        modelBuilder.Entity<Payment>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("payments_pkey");

            entity.ToTable("payments", "payment");

            entity.HasIndex(e => new { e.Gateway, e.GatewayTxnId }, "payments_gateway_gateway_txn_id_key").IsUnique();

            entity.HasIndex(e => e.IdempotencyKey, "payments_idempotency_key_key").IsUnique();

            entity.HasIndex(e => e.CheckoutId, "payments_one_charge_per_checkout")
                .IsUnique()
                .HasFilter("(kind = 'CHARGE'::text)");

            entity.HasIndex(e => e.OrderId, "payments_order_id_idx");

            entity.HasIndex(e => e.ParentPaymentId, "payments_refund_parent_idx").HasFilter("(kind = 'REFUND'::text)");

            entity.Property(e => e.Id)
                .HasDefaultValueSql("uuid_v7()")
                .HasColumnName("id");
            entity.Property(e => e.Amount).HasColumnName("amount");
            entity.Property(e => e.ApprovedBy).HasColumnName("approved_by");
            entity.Property(e => e.CheckoutId).HasColumnName("checkout_id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("app_now()")
                .HasColumnName("created_at");
            entity.Property(e => e.Gateway).HasColumnName("gateway");
            entity.Property(e => e.GatewayTxnId).HasColumnName("gateway_txn_id");
            entity.Property(e => e.IdempotencyKey).HasColumnName("idempotency_key");
            entity.Property(e => e.Kind).HasColumnName("kind");
            entity.Property(e => e.OrderId).HasColumnName("order_id");
            entity.Property(e => e.PaidAt).HasColumnName("paid_at");
            entity.Property(e => e.ParentPaymentId).HasColumnName("parent_payment_id");
            entity.Property(e => e.Purpose).HasColumnName("purpose");
            entity.Property(e => e.RawPayload)
                .HasColumnType("jsonb")
                .HasColumnName("raw_payload");
            entity.Property(e => e.Reason).HasColumnName("reason");
            entity.Property(e => e.Status)
                .HasDefaultValueSql("'PENDING'::text")
                .HasColumnName("status");
            entity.Property(e => e.SubscriptionId).HasColumnName("subscription_id");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("app_now()")
                .HasColumnName("updated_at");

            entity.HasOne(d => d.ParentPayment).WithMany(p => p.InverseParentPayment)
                .HasForeignKey(d => d.ParentPaymentId)
                .HasConstraintName("payments_parent_payment_id_fkey");
        });

        modelBuilder.Entity<Seller>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("sellers_pkey");

            entity.ToTable("sellers", "identity");

            entity.Property(e => e.Id)
                .HasDefaultValueSql("uuid_v7()")
                .HasColumnName("id");
            entity.Property(e => e.Address).HasColumnName("address");
            entity.Property(e => e.BankAccountNoEnc).HasColumnName("bank_account_no_enc");
            entity.Property(e => e.BankHolder).HasColumnName("bank_holder");
            entity.Property(e => e.BankName).HasColumnName("bank_name");
            entity.Property(e => e.BrandTone).HasColumnName("brand_tone");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("app_now()")
                .HasColumnName("created_at");
            entity.Property(e => e.PackageExpiresAt).HasColumnName("package_expires_at");
            entity.Property(e => e.PackageId).HasColumnName("package_id");
            entity.Property(e => e.Phone).HasColumnName("phone");
            entity.Property(e => e.ShopName).HasColumnName("shop_name");
            entity.Property(e => e.Status)
                .HasDefaultValueSql("'PENDING'::text")
                .HasColumnName("status");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("app_now()")
                .HasColumnName("updated_at");

            entity.HasOne(d => d.Package).WithMany(p => p.Sellers)
                .HasForeignKey(d => d.PackageId)
                .HasConstraintName("sellers_package_id_fkey");
        });

        modelBuilder.Entity<Slot>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("slots_pkey");

            entity.ToTable("slots", "kiosk_ops");

            entity.HasIndex(e => e.BouquetId, "slots_bouquet_id_key").IsUnique();

            entity.HasIndex(e => e.HoldUntil, "slots_hold_idx").HasFilter("(status = 'HELD'::text)");

            entity.HasIndex(e => new { e.KioskId, e.RelayChannel }, "slots_kiosk_id_relay_channel_key").IsUnique();

            entity.HasIndex(e => new { e.KioskId, e.SlotCode }, "slots_kiosk_id_slot_code_key").IsUnique();

            entity.Property(e => e.Id)
                .HasDefaultValueSql("uuid_v7()")
                .HasColumnName("id");
            entity.Property(e => e.BouquetId).HasColumnName("bouquet_id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("app_now()")
                .HasColumnName("created_at");
            entity.Property(e => e.CurrentSellerId).HasColumnName("current_seller_id");
            entity.Property(e => e.HoldOrderId).HasColumnName("hold_order_id");
            entity.Property(e => e.HoldUntil).HasColumnName("hold_until");
            entity.Property(e => e.KioskId).HasColumnName("kiosk_id");
            entity.Property(e => e.RelayChannel).HasColumnName("relay_channel");
            entity.Property(e => e.RowVersion).HasColumnName("row_version");
            entity.Property(e => e.SlotCode).HasColumnName("slot_code");
            entity.Property(e => e.Status)
                .HasDefaultValueSql("'FREE'::text")
                .HasColumnName("status");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("app_now()")
                .HasColumnName("updated_at");

            entity.HasOne(d => d.Bouquet).WithOne(p => p.Slot)
                .HasForeignKey<Slot>(d => d.BouquetId)
                .HasConstraintName("slots_bouquet_id_fkey");

            entity.HasOne(d => d.Kiosk).WithMany(p => p.Slots)
                .HasForeignKey(d => d.KioskId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("slots_kiosk_id_fkey");
        });

        modelBuilder.Entity<SlotAssignment>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("slot_assignments_pkey");

            entity.ToTable("slot_assignments", "kiosk_ops");

            entity.HasIndex(e => e.SlotId, "slot_assignments_one_active")
                .IsUnique()
                .HasFilter("(status = 'ACTIVE'::text)");

            entity.HasIndex(e => e.SellerId, "slot_assignments_seller_id_idx").HasFilter("(status = 'ACTIVE'::text)");

            entity.Property(e => e.Id)
                .HasDefaultValueSql("uuid_v7()")
                .HasColumnName("id");
            entity.Property(e => e.AssignedAt)
                .HasDefaultValueSql("app_now()")
                .HasColumnName("assigned_at");
            entity.Property(e => e.AssignedBy).HasColumnName("assigned_by");
            entity.Property(e => e.ReleaseReason).HasColumnName("release_reason");
            entity.Property(e => e.ReleasedAt).HasColumnName("released_at");
            entity.Property(e => e.SellerId).HasColumnName("seller_id");
            entity.Property(e => e.SlotId).HasColumnName("slot_id");
            entity.Property(e => e.Status)
                .HasDefaultValueSql("'ACTIVE'::text")
                .HasColumnName("status");

            entity.HasOne(d => d.Slot).WithOne(p => p.SlotAssignment)
                .HasForeignKey<SlotAssignment>(d => d.SlotId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("slot_assignments_slot_id_fkey");
        });

        modelBuilder.Entity<Subscription>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("subscriptions_pkey");

            entity.ToTable("subscriptions", "identity");

            entity.Property(e => e.Id)
                .HasDefaultValueSql("uuid_v7()")
                .HasColumnName("id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("app_now()")
                .HasColumnName("created_at");
            entity.Property(e => e.PackageId).HasColumnName("package_id");
            entity.Property(e => e.PaymentId).HasColumnName("payment_id");
            entity.Property(e => e.PeriodFrom).HasColumnName("period_from");
            entity.Property(e => e.PeriodTo).HasColumnName("period_to");
            entity.Property(e => e.Price).HasColumnName("price");
            entity.Property(e => e.SellerId).HasColumnName("seller_id");
            entity.Property(e => e.Status)
                .HasDefaultValueSql("'PENDING_PAYMENT'::text")
                .HasColumnName("status");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("app_now()")
                .HasColumnName("updated_at");

            entity.HasOne(d => d.Package).WithMany(p => p.Subscriptions)
                .HasForeignKey(d => d.PackageId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("subscriptions_package_id_fkey");

            entity.HasOne(d => d.Seller).WithMany(p => p.Subscriptions)
                .HasForeignKey(d => d.SellerId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("subscriptions_seller_id_fkey");
        });

        modelBuilder.Entity<SubscriptionPackage>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("subscription_packages_pkey");

            entity.ToTable("subscription_packages", "identity");

            entity.HasIndex(e => e.Name, "subscription_packages_name_key").IsUnique();

            entity.Property(e => e.Id)
                .HasDefaultValueSql("uuid_v7()")
                .HasColumnName("id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("app_now()")
                .HasColumnName("created_at");
            entity.Property(e => e.MaxSlots).HasColumnName("max_slots");
            entity.Property(e => e.MonthlyFee).HasColumnName("monthly_fee");
            entity.Property(e => e.Name).HasColumnName("name");
            entity.Property(e => e.Status)
                .HasDefaultValueSql("'ACTIVE'::text")
                .HasColumnName("status");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("app_now()")
                .HasColumnName("updated_at");
        });

        modelBuilder.Entity<SystemSetting>(entity =>
        {
            entity.HasKey(e => e.Key).HasName("system_settings_pkey");

            entity.ToTable("system_settings", "kiosk_ops");

            entity.Property(e => e.Key).HasColumnName("key");
            entity.Property(e => e.Description).HasColumnName("description");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("app_now()")
                .HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");
            entity.Property(e => e.Value)
                .HasColumnType("jsonb")
                .HasColumnName("value");
        });

        modelBuilder.Entity<UnlockToken>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("unlock_tokens_pkey");

            entity.ToTable("unlock_tokens", "kiosk_ops");

            entity.HasIndex(e => e.ExpiresAt, "unlock_live_expiry_idx").HasFilter("(status = ANY (ARRAY['ISSUED'::text, 'SENT'::text, 'ACKED'::text]))");

            entity.HasIndex(e => e.SlotId, "unlock_one_live_per_slot")
                .IsUnique()
                .HasFilter("(status = ANY (ARRAY['ISSUED'::text, 'SENT'::text, 'ACKED'::text, 'OPENED'::text]))");

            entity.HasIndex(e => e.CmdId, "unlock_tokens_cmd_id_key").IsUnique();

            entity.HasIndex(e => e.OrderId, "unlock_tokens_order_id_idx");

            entity.HasIndex(e => e.TokenHash, "unlock_tokens_token_hash_key").IsUnique();

            entity.Property(e => e.Id)
                .HasDefaultValueSql("uuid_v7()")
                .HasColumnName("id");
            entity.Property(e => e.Attempts).HasColumnName("attempts");
            entity.Property(e => e.CmdId)
                .HasDefaultValueSql("uuid_v7()")
                .HasColumnName("cmd_id");
            entity.Property(e => e.DoorClosedAt).HasColumnName("door_closed_at");
            entity.Property(e => e.DoorOpenedAt).HasColumnName("door_opened_at");
            entity.Property(e => e.ExpiresAt).HasColumnName("expires_at");
            entity.Property(e => e.IssuedAt)
                .HasDefaultValueSql("app_now()")
                .HasColumnName("issued_at");
            entity.Property(e => e.IssuedToUserId).HasColumnName("issued_to_user_id");
            entity.Property(e => e.KioskId).HasColumnName("kiosk_id");
            entity.Property(e => e.OrderId).HasColumnName("order_id");
            entity.Property(e => e.Purpose).HasColumnName("purpose");
            entity.Property(e => e.SlotId).HasColumnName("slot_id");
            entity.Property(e => e.Status)
                .HasDefaultValueSql("'ISSUED'::text")
                .HasColumnName("status");
            entity.Property(e => e.TokenHash).HasColumnName("token_hash");

            entity.HasOne(d => d.Kiosk).WithMany(p => p.UnlockTokens)
                .HasForeignKey(d => d.KioskId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("unlock_tokens_kiosk_id_fkey");

            entity.HasOne(d => d.Slot).WithOne(p => p.UnlockToken)
                .HasForeignKey<UnlockToken>(d => d.SlotId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("unlock_tokens_slot_id_fkey");
        });

        modelBuilder.Entity<User>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("users_pkey");

            entity.ToTable("users", "identity");

            entity.HasIndex(e => e.Email, "users_email_key").IsUnique();

            entity.HasIndex(e => e.Phone, "users_phone_key").IsUnique();

            entity.Property(e => e.Id)
                .HasDefaultValueSql("uuid_v7()")
                .HasColumnName("id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("app_now()")
                .HasColumnName("created_at");
            entity.Property(e => e.Email).HasColumnName("email");
            entity.Property(e => e.FullName).HasColumnName("full_name");
            entity.Property(e => e.LoyaltyPoints).HasColumnName("loyalty_points");
            entity.Property(e => e.PasswordHash).HasColumnName("password_hash");
            entity.Property(e => e.Phone).HasColumnName("phone");
            entity.Property(e => e.Role).HasColumnName("role");
            entity.Property(e => e.SellerId).HasColumnName("seller_id");
            entity.Property(e => e.Status)
                .HasDefaultValueSql("'ACTIVE'::text")
                .HasColumnName("status");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("app_now()")
                .HasColumnName("updated_at");

            entity.HasOne(d => d.Seller).WithMany(p => p.Users)
                .HasForeignKey(d => d.SellerId)
                .HasConstraintName("users_seller_id_fkey");
        });

        modelBuilder.Entity<VSellerBalance>(entity =>
        {
            entity
                .HasNoKey()
                .ToView("v_seller_balance", "payment");

            entity.Property(e => e.AvailableBalance).HasColumnName("available_balance");
            entity.Property(e => e.Debt).HasColumnName("debt");
            entity.Property(e => e.PendingBalance).HasColumnName("pending_balance");
            entity.Property(e => e.SellerId).HasColumnName("seller_id");
        });

        modelBuilder.Entity<WithdrawalRequest>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("withdrawal_requests_pkey");

            entity.ToTable("withdrawal_requests", "payment");

            entity.HasIndex(e => e.SellerId, "withdrawal_one_pending")
                .IsUnique()
                .HasFilter("(status = ANY (ARRAY['PENDING'::text, 'APPROVED'::text]))");

            entity.Property(e => e.Id)
                .HasDefaultValueSql("uuid_v7()")
                .HasColumnName("id");
            entity.Property(e => e.Amount).HasColumnName("amount");
            entity.Property(e => e.ApprovedBy).HasColumnName("approved_by");
            entity.Property(e => e.BankAccountNoEnc).HasColumnName("bank_account_no_enc");
            entity.Property(e => e.BankHolder).HasColumnName("bank_holder");
            entity.Property(e => e.BankName).HasColumnName("bank_name");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("app_now()")
                .HasColumnName("created_at");
            entity.Property(e => e.PaidAt).HasColumnName("paid_at");
            entity.Property(e => e.RejectReason).HasColumnName("reject_reason");
            entity.Property(e => e.SellerId).HasColumnName("seller_id");
            entity.Property(e => e.Status)
                .HasDefaultValueSql("'PENDING'::text")
                .HasColumnName("status");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("app_now()")
                .HasColumnName("updated_at");
        });

        OnModelCreatingPartial(modelBuilder);
    }

    partial void OnModelCreatingPartial(ModelBuilder modelBuilder);
}
