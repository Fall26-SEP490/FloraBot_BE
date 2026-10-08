using System;
using System.Collections.Generic;

namespace FloraBot.Api.Data.Entities;

public partial class Subscription
{
    public Guid Id { get; set; }

    public Guid SellerId { get; set; }

    public Guid PackageId { get; set; }

    public DateOnly PeriodFrom { get; set; }

    public DateOnly PeriodTo { get; set; }

    public long Price { get; set; }

    public string Status { get; set; } = null!;

    public Guid? PaymentId { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public virtual SubscriptionPackage Package { get; set; } = null!;

    public virtual Seller Seller { get; set; } = null!;
}
