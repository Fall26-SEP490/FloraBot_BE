using System;
using System.Collections.Generic;

namespace FloraBot.Api.Data.Entities;

public partial class Order
{
    public Guid Id { get; set; }

    public string OrderCode { get; set; } = null!;

    public Guid CheckoutId { get; set; }

    public Guid? CustomerId { get; set; }

    public Guid SellerId { get; set; }

    public Guid KioskId { get; set; }

    public string Status { get; set; } = null!;

    public long Subtotal { get; set; }

    public long DiscountAmount { get; set; }

    public long TotalAmount { get; set; }

    public long PointsRedeemed { get; set; }

    public long PointsEarned { get; set; }

    public string TrackingToken { get; set; } = null!;

    public string? EcardContent { get; set; }

    public DateTime? CompletedAt { get; set; }

    public string? RefundBankName { get; set; }

    public string? RefundBankAccountEnc { get; set; }

    public string? RefundBankHolder { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public virtual Dispute? Dispute { get; set; }

    public virtual ICollection<OrderItem> OrderItems { get; set; } = new List<OrderItem>();
}
