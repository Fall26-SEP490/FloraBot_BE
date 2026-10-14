using System;

namespace FloraBot.Api.Data.Entities;

public partial class UserProfile
{
    public Guid UserId { get; set; }

    public string? Address { get; set; }

    public string? ContactEmail { get; set; }

    public DateTime? ContactEmailVerifiedAt { get; set; }

    public string? BankName { get; set; }

    public string? AccountHolder { get; set; }

    public string? AccountEncrypted { get; set; }

    public string? AccountLast4 { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public virtual User User { get; set; } = null!;
}
