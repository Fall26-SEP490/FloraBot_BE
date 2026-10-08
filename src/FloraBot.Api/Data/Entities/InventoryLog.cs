using System;
using System.Collections.Generic;

namespace FloraBot.Api.Data.Entities;

public partial class InventoryLog
{
    public Guid Id { get; set; }

    public long Seq { get; set; }

    public Guid BouquetId { get; set; }

    public Guid SlotId { get; set; }

    public string MovementType { get; set; } = null!;

    public Guid? BatchId { get; set; }

    public Guid? OrderId { get; set; }

    public Guid? PerformedBy { get; set; }

    public string? Reason { get; set; }

    public DateTime CreatedAt { get; set; }

    public virtual Bouquet Bouquet { get; set; } = null!;

    public virtual Slot Slot { get; set; } = null!;
}
