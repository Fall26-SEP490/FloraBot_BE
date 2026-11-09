using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace FloraBot.Api.Tests;

public sealed class ProductStockQuantityTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private NpgsqlDataSource Data => factory.Services.GetRequiredService<NpgsqlDataSource>();

    private sealed record Fixture(
        Guid Seller, Guid Actor, Guid Staff, Guid Customer, Guid Product, Guid Kiosk,
        Guid FirstSlot, Guid SecondSlot, Guid PreorderSlot, Guid FirstTask, Guid SecondTask,
        Guid Request, Guid Order);

    private async Task<Fixture> Setup(int quantity)
    {
        var f = new Fixture(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await Sql(f, """
            INSERT INTO identity.sellers(id,shop_name,phone,status,package_id,package_expires_at)
            VALUES(@seller,'Product quantity shop','0901234567','ACTIVE',
              '20000000-0000-0000-0000-00000000000a',CURRENT_DATE+365);
            INSERT INTO identity.users(id,email,password_hash,full_name,role,seller_id,status)
            VALUES(@actor,@actor::text||'@example.invalid','!test','Seller','SELLER',@seller,'ACTIVE'),
              (@staff,@staff::text||'@example.invalid','!test','Staff','SELLER_STAFF',@seller,'ACTIVE'),
              (@customer,@customer::text||'@example.invalid','!test','Customer','CUSTOMER',NULL,'ACTIVE');
            INSERT INTO catalog.flower_products(id,seller_id,name,price,shelf_life_hours,status,quantity)
            VALUES(@product,@seller,'Quantity bouquet',350000,48,'ACTIVE',@quantity);
            INSERT INTO kiosk_ops.kiosks(id,code,name,address,region,hardware_id,mqtt_client_id,status)
            VALUES(@kiosk,@kiosk::text,'Quantity kiosk','Test address','HCM',@kiosk::text,@kiosk::text,'ONLINE');
            INSERT INTO kiosk_ops.slots(id,kiosk_id,slot_code,relay_channel,zone)
            VALUES(@firstSlot,@kiosk,'S01',0,'WALK_IN'),(@secondSlot,@kiosk,'S02',1,'WALK_IN'),
              (@preorderSlot,@kiosk,'S03',2,'PREORDER');
            """, ("quantity", quantity));
        await CurrentMainKioskFixture.AssignKioskWithLeaseAsync(Data, f.Kiosk, f.Seller);
        await Sql(f, """
            INSERT INTO kiosk_ops.staff_tasks(id,kind,assignee_id,assigned_by,kiosk_id,seller_id,instructions,status)
            VALUES(@firstTask,'DELIVERY',@staff,@actor,@kiosk,@seller,'Load the physical bouquets','IN_PROGRESS'),
              (@secondTask,'DELIVERY',@staff,@actor,@kiosk,@seller,'Load the physical bouquets','IN_PROGRESS');
            """);
        return f;
    }

    private static void AddParameters(NpgsqlCommand cmd, Fixture f, params (string Name, object Value)[] extra)
    {
        foreach (var (name, id) in new[]
        {
            ("seller", f.Seller), ("actor", f.Actor), ("staff", f.Staff), ("customer", f.Customer),
            ("product", f.Product), ("kiosk", f.Kiosk), ("firstSlot", f.FirstSlot),
            ("secondSlot", f.SecondSlot), ("preorderSlot", f.PreorderSlot),
            ("firstTask", f.FirstTask), ("secondTask", f.SecondTask), ("request", f.Request), ("order", f.Order)
        }) cmd.Parameters.AddWithValue(name, id);
        foreach (var (name, value) in extra) cmd.Parameters.AddWithValue(name, value);
    }

    private async Task<object?> Sql(Fixture f, string sql, params (string Name, object Value)[] extra)
    {
        await using var cmd = Data.CreateCommand(sql);
        AddParameters(cmd, f, extra);
        return await cmd.ExecuteScalarAsync();
    }

    [Fact]
    public async Task ZeroStockRejectsLoadingAndNegativeValuesAtDatabaseBoundary()
    {
        var f = await Setup(0);
        await Sql(f, "UPDATE catalog.flower_products SET quantity=DEFAULT WHERE id=@product");
        Assert.Equal(0, await Sql(f, "SELECT quantity FROM catalog.flower_products WHERE id=@product"));
        var invalid = await Assert.ThrowsAsync<PostgresException>(() => Sql(f,
            "UPDATE catalog.flower_products SET quantity=-1 WHERE id=@product"));
        Assert.Equal(PostgresErrorCodes.CheckViolation, invalid.SqlState);

        var exhausted = await Assert.ThrowsAsync<PostgresException>(() => Sql(f,
            "SELECT flow.stock_bouquet(flow.new_batch(),@product,@firstSlot,@actor,@product::text)"));
        Assert.Equal("40001", exhausted.SqlState);
        Assert.Equal("RENTED_EMPTY", await Sql(f, "SELECT status FROM kiosk_ops.slots WHERE id=@firstSlot"));
        Assert.Equal(0L, await Sql(f, "SELECT count(*) FROM kiosk_ops.bouquets WHERE product_id=@product"));
        Assert.Equal(0L, await Sql(f, "SELECT count(*) FROM kiosk_ops.inventory_logs WHERE slot_id=@firstSlot"));
        Assert.Equal(0L, await Sql(f, "SELECT count(*) FROM kiosk_ops.product_quantity_deductions WHERE product_id=@product"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InventoryBoundaryRechecksProductOwnershipAndActiveStatus(bool inactive)
    {
        var f = await Setup(1);
        var bouquet = Guid.NewGuid();
        await Sql(f, """
            INSERT INTO kiosk_ops.bouquets(id,product_id,seller_id,qr_code,price_snapshot,status,sellable_until,stocked_at)
            VALUES(@bouquet,@product,@bouquetSeller,@bouquet::text,350000,'STOCKED',
              public.app_now()+interval '48 hours',public.app_now());
            """, ("bouquet", bouquet), ("bouquetSeller", inactive ? f.Seller : Guid.NewGuid()));
        if (inactive) await Sql(f, "UPDATE catalog.flower_products SET status='INACTIVE' WHERE id=@product");
        var error = await Assert.ThrowsAsync<PostgresException>(() => Sql(f, """
            INSERT INTO kiosk_ops.inventory_logs(bouquet_id,slot_id,movement_type,batch_id,performed_by)
            VALUES(@bouquet,@firstSlot,'STOCK_IN',flow.new_batch(),@actor)
            """, ("bouquet", bouquet)));
        Assert.Equal("40001", error.SqlState);
        Assert.Equal(1, await Sql(f, "SELECT quantity FROM catalog.flower_products WHERE id=@product"));
        Assert.Equal(0L, await Sql(f, "SELECT count(*) FROM kiosk_ops.product_quantity_deductions WHERE bouquet_id=@bouquet", ("bouquet", bouquet)));
    }

    [Fact]
    public async Task StockDeductsOnceAndRemovalRequiresManualShopReconciliation()
    {
        var f = await Setup(1);
        var bouquet = Assert.IsType<Guid>(await Sql(f,
            "SELECT flow.stock_bouquet(flow.new_batch(),@product,@firstSlot,@actor,@product::text)"));
        Assert.Equal(0, await Sql(f, "SELECT quantity FROM catalog.flower_products WHERE id=@product"));
        await Assert.ThrowsAsync<PostgresException>(() => Sql(f,
            "SELECT flow.stock_bouquet(flow.new_batch(),@product,@firstSlot,@actor,@actor::text)"));

        // Even a duplicate STOCK_IN event for the same physical bouquet is safe
        // at zero stock; its first event remains the only deduction reference.
        await Sql(f, """
            INSERT INTO kiosk_ops.inventory_logs(bouquet_id,slot_id,movement_type,batch_id,performed_by)
            VALUES(@bouquet,@firstSlot,'STOCK_IN',flow.new_batch(),@actor);
            INSERT INTO kiosk_ops.inventory_logs(bouquet_id,slot_id,movement_type,performed_by)
            VALUES(@bouquet,@firstSlot,'EXPIRE_OUT',@actor),(@bouquet,@firstSlot,'DAMAGE',@actor);
            """, ("bouquet", bouquet));
        Assert.Equal(0, await Sql(f, "SELECT quantity FROM catalog.flower_products WHERE id=@product"));
        Assert.Equal(1L, await Sql(f, "SELECT count(*) FROM kiosk_ops.product_quantity_deductions WHERE product_id=@product"));
        Assert.Equal("RENTED_EMPTY", await Sql(f,
            "SELECT flow.seller_remove_slot_bouquet(@kiosk,@firstSlot,@actor,@bouquet,'Returned to the shop',true)",
            ("bouquet", bouquet)));
        Assert.Equal("RENTED_EMPTY", await Sql(f,
            "SELECT flow.seller_remove_slot_bouquet(@kiosk,@firstSlot,@actor,@bouquet,'Returned to the shop',true)",
            ("bouquet", bouquet)));
        Assert.Equal(0, await Sql(f, "SELECT quantity FROM catalog.flower_products WHERE id=@product"));
        Assert.Equal(1L, await Sql(f, "SELECT count(*) FROM kiosk_ops.inventory_logs WHERE bouquet_id=@bouquet AND movement_type='RETURN_SELLER'", ("bouquet", bouquet)));

        await Sql(f, "UPDATE catalog.flower_products SET quantity=1 WHERE id=@product");
        Assert.IsType<Guid>(await Sql(f,
            "SELECT flow.stock_bouquet(flow.new_batch(),@product,@secondSlot,@actor,@customer::text)"));
        Assert.Equal(0, await Sql(f, "SELECT quantity FROM catalog.flower_products WHERE id=@product"));
        Assert.Equal(2L, await Sql(f, "SELECT count(*) FROM kiosk_ops.product_quantity_deductions WHERE product_id=@product"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentStaffLoadsForDifferentSlotsCannotOversellOrDeadlockOnProductLockUpgrade(bool artificial)
    {
        var f = await Setup(1);
        if (artificial)
            await Sql(f, """
                UPDATE catalog.flower_products
                SET inventory_kind='ARTIFICIAL',length_cm=10,width_cm=10,height_cm=10
                WHERE id=@product
                """);
        var firstOperation = Guid.NewGuid();
        var secondOperation = Guid.NewGuid();
        var applicationPrefix = $"quantity-race-{Guid.NewGuid():N}";
        await using var blocker = await Data.OpenConnectionAsync();
        await using var blockTransaction = await blocker.BeginTransactionAsync();
        await using (var lockSlots = new NpgsqlCommand(
            "SELECT id FROM kiosk_ops.slots WHERE id IN (@firstSlot,@secondSlot) ORDER BY id FOR UPDATE", blocker, blockTransaction))
        {
            AddParameters(lockSlots, f);
            await lockSlots.ExecuteNonQueryAsync();
        }

        async Task<(Guid? Bouquet, PostgresException? Error)> Load(Guid operation, Guid task, Guid slot, string suffix)
        {
            await using var connection = await Data.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await using (var identify = new NpgsqlCommand("SELECT set_config('application_name',@name,true)", connection, transaction))
            {
                identify.Parameters.AddWithValue("name", applicationPrefix + suffix);
                await identify.ExecuteNonQueryAsync();
            }
            try
            {
                await using var cmd = new NpgsqlCommand(
                    "SELECT flow.staff_stock_bouquet(@operation,@task,@staff,@product,@slot,@qr)", connection, transaction);
                AddParameters(cmd, f, ("operation", operation), ("task", task), ("slot", slot), ("qr", operation.ToString()));
                var bouquet = (Guid)(await cmd.ExecuteScalarAsync())!;
                await transaction.CommitAsync();
                return (bouquet, null);
            }
            catch (PostgresException ex)
            {
                await transaction.RollbackAsync();
                return (null, ex);
            }
        }

        var first = Load(firstOperation, f.FirstTask, f.FirstSlot, "-first");
        var second = Load(secondOperation, f.SecondTask, f.SecondSlot, "-second");
        try
        {
            // Both flows have acquired their product read lock and are waiting
            // for different slots. Releasing both slots exposes lock upgrades.
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (true)
            {
                await using var waiting = Data.CreateCommand("""
                    SELECT count(*) FROM pg_stat_activity
                    WHERE application_name IN (@first,@second) AND wait_event_type='Lock'
                    """);
                waiting.Parameters.AddWithValue("first", applicationPrefix + "-first");
                waiting.Parameters.AddWithValue("second", applicationPrefix + "-second");
                if ((long)(await waiting.ExecuteScalarAsync())! == 2) break;
                Assert.True(DateTime.UtcNow < deadline, "Both staff stock flows must reach their slot locks.");
                await Task.Delay(25);
            }
        }
        finally
        {
            await blockTransaction.CommitAsync();
        }
        var results = await Task.WhenAll(first, second);
        var winner = Assert.Single(results, r => r.Bouquet.HasValue);
        var loser = Assert.Single(results, r => r.Error is not null);
        Assert.Equal("40001", loser.Error!.SqlState);
        Assert.Equal(0, await Sql(f, "SELECT quantity FROM catalog.flower_products WHERE id=@product"));
        Assert.Equal(1L, await Sql(f, "SELECT count(*) FROM kiosk_ops.bouquets WHERE product_id=@product"));
        Assert.Equal(1L, await Sql(f, "SELECT count(*) FROM kiosk_ops.product_quantity_deductions WHERE product_id=@product"));
        Assert.Equal(1L, await Sql(f, "SELECT count(*) FROM kiosk_ops.slots WHERE kiosk_id=@kiosk AND status='STOCKED'"));
        if (artificial)
            Assert.Equal(true, await Sql(f,
                "SELECT bool_and(sellable_until='infinity'::timestamptz) FROM kiosk_ops.bouquets WHERE product_id=@product"));

        var firstWon = results[0].Bouquet.HasValue;
        Assert.Equal(winner.Bouquet, await Sql(f,
            "SELECT flow.staff_stock_bouquet(@operation,@task,@staff,@product,@slot,@qr)",
            ("operation", firstWon ? firstOperation : secondOperation),
            ("task", firstWon ? f.FirstTask : f.SecondTask),
            ("slot", firstWon ? f.FirstSlot : f.SecondSlot),
            ("qr", (firstWon ? firstOperation : secondOperation).ToString())));
        Assert.Equal(0, await Sql(f, "SELECT quantity FROM catalog.flower_products WHERE id=@product"));
    }

    private async Task PreparePaidPreorder(Fixture f)
    {
        await Sql(f, """
            INSERT INTO ordering.orders(id,order_code,checkout_id,customer_id,seller_id,kiosk_id,status,subtotal,total_amount,tracking_token)
            VALUES(@order,@order::text,@order,@customer,@seller,@kiosk,'PAID',350000,350000,@order::text);
            INSERT INTO ordering.web_requests(id,customer_id,seller_id,product_id,kiosk_id,kind,instructions,pickup_at,pickup_before,state,order_id)
            VALUES(@request,@customer,@seller,@product,@kiosk,'CUSTOM','Quantity preorder',
              public.app_now()+interval '1 hour',public.app_now()+interval '2 hours','ORDERED',@order);
            INSERT INTO payment.payments(kind,purpose,checkout_id,gateway,idempotency_key,amount,status,paid_at)
            VALUES('CHARGE','ORDER_CHECKOUT',@order,'MANUAL',@order::text,350000,'SUCCEEDED',public.app_now());
            """);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PaidCustomFulfillmentDeductsOnceIncludingConcurrentRetries(bool staff)
    {
        var f = await Setup(1);
        await PreparePaidPreorder(f);
        var sql = staff
            ? "SELECT flow.staff_fulfill_preorder(@firstTask,@request,@staff,@preorderSlot,@request::text)"
            : "SELECT flow.web_fulfill(@request,@seller,@actor,@preorderSlot,@request::text)";
        var results = await Task.WhenAll(Sql(f, sql), Sql(f, sql));
        Assert.IsType<Guid>(results[0]);
        Assert.Equal(results[0], results[1]);
        Assert.Equal(0, await Sql(f, "SELECT quantity FROM catalog.flower_products WHERE id=@product"));
        Assert.Equal(1L, await Sql(f, "SELECT count(*) FROM kiosk_ops.product_quantity_deductions WHERE product_id=@product"));
        Assert.Equal(1L, await Sql(f, "SELECT count(*) FROM ordering.order_items WHERE order_id=@order"));
        Assert.Equal(1L, await Sql(f, "SELECT count(*) FROM kiosk_ops.inventory_logs WHERE slot_id=@preorderSlot AND movement_type='SALE_OUT'"));
        Assert.Equal("HELD", await Sql(f, "SELECT status FROM kiosk_ops.slots WHERE id=@preorderSlot"));
    }

    [Fact]
    public async Task LaterFulfillmentFailureRollsBackTheQuantityAndAllStockWrites()
    {
        var f = await Setup(1);
        await PreparePaidPreorder(f);
        await Sql(f, "UPDATE catalog.flower_products SET shelf_life_hours=1 WHERE id=@product");
        await Assert.ThrowsAsync<PostgresException>(() => Sql(f,
            "SELECT flow.web_fulfill(@request,@seller,@actor,@preorderSlot,@request::text)"));
        Assert.Equal(1, await Sql(f, "SELECT quantity FROM catalog.flower_products WHERE id=@product"));
        Assert.Equal(0L, await Sql(f, "SELECT count(*) FROM kiosk_ops.product_quantity_deductions WHERE product_id=@product"));
        Assert.Equal(0L, await Sql(f, "SELECT count(*) FROM kiosk_ops.bouquets WHERE product_id=@product"));
        Assert.Equal(0L, await Sql(f, "SELECT count(*) FROM ordering.order_items WHERE order_id=@order"));
        Assert.Equal("RENTED_EMPTY", await Sql(f, "SELECT status FROM kiosk_ops.slots WHERE id=@preorderSlot"));
    }
}
