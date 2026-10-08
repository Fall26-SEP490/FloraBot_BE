using System;
using System.Collections.Generic;

namespace FloraBot.Api.Data.Entities;

public partial class FlowerProduct
{
    public Guid Id { get; set; }

    public Guid SellerId { get; set; }

    public string Name { get; set; } = null!;

    public string? Description { get; set; }

    public long Price { get; set; }

    public List<string> Tags { get; set; } = null!;

    public int ShelfLifeHours { get; set; }

    public string Status { get; set; } = null!;

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}
