using System;
using System.Collections.Generic;

namespace FloraBot.Api.Data.Entities;

public partial class GiftSurvey
{
    public Guid Id { get; set; }

    public string SessionId { get; set; } = null!;

    public Guid? CustomerId { get; set; }

    public Guid KioskId { get; set; }

    public string RecipientType { get; set; } = null!;

    public string? AgeGroup { get; set; }

    public string Occasion { get; set; } = null!;

    public string? MessageTone { get; set; }

    public long? BudgetMax { get; set; }

    public string Results { get; set; } = null!;

    public string? ModelVersion { get; set; }

    public string? Source { get; set; }

    public int? LatencyMs { get; set; }

    public Guid? SelectedProductId { get; set; }

    public Guid? PurchasedOrderId { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}
