using System;
using System.Collections.Generic;

namespace FloraBot.Api.Data.Entities;

public partial class Slot
{
    public Guid Id { get; set; }

    public Guid KioskId { get; set; }

    public string SlotCode { get; set; } = null!;

    public string Status { get; set; } = null!;

    public Guid? CurrentSellerId { get; set; }

    public Guid? BouquetId { get; set; }

    public DateTime? HoldUntil { get; set; }

    public Guid? HoldOrderId { get; set; }

    public int RowVersion { get; set; }

    public short RelayChannel { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public virtual Bouquet? Bouquet { get; set; }

    public virtual ICollection<InventoryLog> InventoryLogs { get; set; } = new List<InventoryLog>();

    public virtual Kiosk Kiosk { get; set; } = null!;

    public virtual SlotAssignment? SlotAssignment { get; set; }

    public virtual UnlockToken? UnlockToken { get; set; }
}
