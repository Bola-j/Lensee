using System;
using System.Collections.Generic;

namespace Lensee.Modules.Catalog.Data;

public partial class Brand
{
    public Guid Id { get; set; }

    public string Name { get; set; } = null!;

    public decimal? PowerMin { get; set; }

    public decimal? PowerMax { get; set; }

    public decimal? PowerStep { get; set; }

    public DateTime CreatedAt { get; set; }

    public virtual ICollection<Product> Products { get; set; } = new List<Product>();
}
