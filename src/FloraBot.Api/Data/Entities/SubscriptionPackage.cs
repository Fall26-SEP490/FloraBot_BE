using System;
using System.Collections.Generic;

namespace FloraBot.Api.Data.Entities;

public partial class SubscriptionPackage
{
    public Guid Id { get; set; }

    public string Name { get; set; } = null!;

    public long MonthlyFee { get; set; }

    public int MaxSlots { get; set; }

    public string Status { get; set; } = null!;

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public virtual ICollection<Seller> Sellers { get; set; } = new List<Seller>();

    public virtual ICollection<Subscription> Subscriptions { get; set; } = new List<Subscription>();
}
