using System;
using System.Collections.Generic;

namespace FloraBot.Api.Data.Entities;

public partial class Dispute
{
    public Guid Id { get; set; }

    public string Kind { get; set; } = null!;

    public Guid? OrderId { get; set; }

    public Guid KioskId { get; set; }

    public Guid? SlotId { get; set; }

    public string Reason { get; set; } = null!;

    public Guid? ReportedBy { get; set; }

    public string Status { get; set; } = null!;

    public string? Decision { get; set; }

    public long? RefundAmount { get; set; }

    public Guid? DecidedBy { get; set; }

    public DateTime? ResolvedAt { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public virtual Order? Order { get; set; }
}
