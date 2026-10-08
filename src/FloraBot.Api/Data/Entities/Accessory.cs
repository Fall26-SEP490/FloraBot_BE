using System;
using System.Collections.Generic;

namespace FloraBot.Api.Data.Entities;

public partial class Accessory
{
    public Guid Id { get; set; }

    public Guid SellerId { get; set; }

    public Guid KioskId { get; set; }

    public string Name { get; set; } = null!;

    public long Price { get; set; }

    public int StockQuantity { get; set; }

    public string Status { get; set; } = null!;

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public virtual Kiosk Kiosk { get; set; } = null!;
}
