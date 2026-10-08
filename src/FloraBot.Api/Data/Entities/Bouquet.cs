using System;
using System.Collections.Generic;

namespace FloraBot.Api.Data.Entities;

public partial class Bouquet
{
    public Guid Id { get; set; }

    public Guid ProductId { get; set; }

    public Guid SellerId { get; set; }

    public string QrCode { get; set; } = null!;

    public long PriceSnapshot { get; set; }

    public string Status { get; set; } = null!;

    public DateTime SellableUntil { get; set; }

    public DateTime StockedAt { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public virtual ICollection<InventoryLog> InventoryLogs { get; set; } = new List<InventoryLog>();

    public virtual Slot? Slot { get; set; }
}
