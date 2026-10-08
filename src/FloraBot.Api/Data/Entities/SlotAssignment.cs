using System;
using System.Collections.Generic;

namespace FloraBot.Api.Data.Entities;

public partial class SlotAssignment
{
    public Guid Id { get; set; }

    public Guid SlotId { get; set; }

    public Guid SellerId { get; set; }

    public Guid AssignedBy { get; set; }

    public DateTime AssignedAt { get; set; }

    public string Status { get; set; } = null!;

    public DateTime? ReleasedAt { get; set; }

    public string? ReleaseReason { get; set; }

    public virtual Slot Slot { get; set; } = null!;
}
