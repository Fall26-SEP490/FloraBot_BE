using System;
using System.Collections.Generic;

namespace FloraBot.Api.Data.Entities;

public partial class VSellerBalance
{
    public Guid? SellerId { get; set; }

    public decimal? PendingBalance { get; set; }

    public decimal? AvailableBalance { get; set; }

    public decimal? Debt { get; set; }
}
