using System;
using System.Collections.Generic;

namespace FloraBot.Api.Data.Entities;

public partial class Seller
{
    public Guid Id { get; set; }

    public string ShopName { get; set; } = null!;

    public string Phone { get; set; } = null!;

    public string? Address { get; set; }

    public string Status { get; set; } = null!;

    public Guid? PackageId { get; set; }

    public DateOnly? PackageExpiresAt { get; set; }

    public string? BrandTone { get; set; }

    public string? BankName { get; set; }

    public string? BankAccountNoEnc { get; set; }

    public string? BankHolder { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public virtual SubscriptionPackage? Package { get; set; }

    public virtual ICollection<Subscription> Subscriptions { get; set; } = new List<Subscription>();

    public virtual ICollection<User> Users { get; set; } = new List<User>();
}
