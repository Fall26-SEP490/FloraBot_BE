using System;
using System.Collections.Generic;

namespace FloraBot.Api.Data.Entities;

public partial class UnlockToken
{
    public Guid Id { get; set; }

    public Guid KioskId { get; set; }

    public Guid SlotId { get; set; }

    public string Purpose { get; set; } = null!;

    public Guid? OrderId { get; set; }

    public Guid? IssuedToUserId { get; set; }

    public string TokenHash { get; set; } = null!;

    public Guid CmdId { get; set; }

    public string Status { get; set; } = null!;

    public int Attempts { get; set; }

    public DateTime IssuedAt { get; set; }

    public DateTime ExpiresAt { get; set; }

    public DateTime? DoorOpenedAt { get; set; }

    public DateTime? DoorClosedAt { get; set; }

    public virtual Kiosk Kiosk { get; set; } = null!;

    public virtual Slot Slot { get; set; } = null!;
}
