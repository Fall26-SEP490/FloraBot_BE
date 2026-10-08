using System;
using System.Collections.Generic;

namespace FloraBot.Api.Data.Entities;

public partial class Kiosk
{
    public Guid Id { get; set; }

    public string Code { get; set; } = null!;

    public string Name { get; set; } = null!;

    public string Address { get; set; } = null!;

    public string Region { get; set; } = null!;

    public string HardwareId { get; set; } = null!;

    public string MqttClientId { get; set; } = null!;

    public string Status { get; set; } = null!;

    public DateTime? LastHeartbeatAt { get; set; }

    public string? ApiKeyHash { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public virtual ICollection<Accessory> Accessories { get; set; } = new List<Accessory>();

    public virtual ICollection<Slot> Slots { get; set; } = new List<Slot>();

    public virtual ICollection<UnlockToken> UnlockTokens { get; set; } = new List<UnlockToken>();
}
