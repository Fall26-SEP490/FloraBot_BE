using System;
using System.Collections.Generic;

namespace FloraBot.Api.Data.Entities;

public partial class AuditLog
{
    public long Id { get; set; }

    public string ActorType { get; set; } = null!;

    public Guid? ActorId { get; set; }

    public string Action { get; set; } = null!;

    public string EntityType { get; set; } = null!;

    public Guid? EntityId { get; set; }

    public string? Payload { get; set; }

    public DateTime CreatedAt { get; set; }
}
