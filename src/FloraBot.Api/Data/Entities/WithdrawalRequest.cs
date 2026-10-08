using System;
using System.Collections.Generic;

namespace FloraBot.Api.Data.Entities;

public partial class WithdrawalRequest
{
    public Guid Id { get; set; }

    public Guid SellerId { get; set; }

    public long Amount { get; set; }

    public string Status { get; set; } = null!;

    public string BankName { get; set; } = null!;

    public string BankAccountNoEnc { get; set; } = null!;

    public string BankHolder { get; set; } = null!;

    public Guid? ApprovedBy { get; set; }

    public string? RejectReason { get; set; }

    public DateTime? PaidAt { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}
