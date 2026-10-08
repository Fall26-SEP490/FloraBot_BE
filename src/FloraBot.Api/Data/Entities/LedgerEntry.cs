using System;
using System.Collections.Generic;

namespace FloraBot.Api.Data.Entities;

public partial class LedgerEntry
{
    public Guid Id { get; set; }

    public Guid JournalId { get; set; }

    public string Account { get; set; } = null!;

    public Guid? SellerId { get; set; }

    public long Amount { get; set; }

    public string RefType { get; set; } = null!;

    public Guid RefId { get; set; }

    public string? Memo { get; set; }

    public DateTime CreatedAt { get; set; }
}
