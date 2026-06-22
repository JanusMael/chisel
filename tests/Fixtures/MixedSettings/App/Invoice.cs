using System.Collections.Generic;
using Lib;

namespace App;

public sealed class Invoice
{
    public Money Total { get; set; } = new Money();

    public List<Money> LineItems { get; set; } = new List<Money>();
}
