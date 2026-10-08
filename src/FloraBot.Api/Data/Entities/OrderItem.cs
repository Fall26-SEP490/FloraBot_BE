using System;
using System.Collections.Generic;

namespace FloraBot.Api.Data.Entities;

public partial class OrderItem
{
    public Guid Id { get; set; }

    public Guid OrderId { get; set; }

    public string ItemType { get; set; } = null!;

    public Guid? BouquetId { get; set; }

    public Guid? AccessoryId { get; set; }

    public Guid? SlotId { get; set; }

    public string NameSnapshot { get; set; } = null!;

    public int Quantity { get; set; }

    public long UnitPrice { get; set; }

    public long LineTotal { get; set; }

    public string LineStatus { get; set; } = null!;

    public DateTime CreatedAt { get; set; }

    public virtual Order Order { get; set; } = null!;
}
