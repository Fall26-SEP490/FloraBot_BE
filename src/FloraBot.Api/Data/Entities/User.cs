using System;
using System.Collections.Generic;

namespace FloraBot.Api.Data.Entities;

public partial class User
{
    public Guid Id { get; set; }

    public string? Email { get; set; }

    public string? Phone { get; set; }

    public string? PasswordHash { get; set; }

    public string FullName { get; set; } = null!;

    public string Role { get; set; } = null!;

    public Guid? SellerId { get; set; }

    public string Status { get; set; } = null!;

    public long LoyaltyPoints { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public virtual Seller? Seller { get; set; }
}
