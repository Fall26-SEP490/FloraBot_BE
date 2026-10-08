using System;
using System.Collections.Generic;

namespace FloraBot.Api.Data.Entities;

public partial class Payment
{
    public Guid Id { get; set; }

    public string Kind { get; set; } = null!;

    public string Purpose { get; set; } = null!;

    public Guid? CheckoutId { get; set; }

    public Guid? OrderId { get; set; }

    public Guid? SubscriptionId { get; set; }

    public Guid? ParentPaymentId { get; set; }

    public string Gateway { get; set; } = null!;

    public string? GatewayTxnId { get; set; }

    public string IdempotencyKey { get; set; } = null!;

    public long Amount { get; set; }

    public string Status { get; set; } = null!;

    public Guid? ApprovedBy { get; set; }

    public string? Reason { get; set; }

    public string? RawPayload { get; set; }

    public DateTime? PaidAt { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public virtual ICollection<Payment> InverseParentPayment { get; set; } = new List<Payment>();

    public virtual Payment? ParentPayment { get; set; }
}
