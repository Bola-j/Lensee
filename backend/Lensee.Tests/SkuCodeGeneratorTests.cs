using Lensee.Modules.Catalog.Data;
using Lensee.Modules.Catalog.Services;
using Xunit;

namespace Lensee.Tests;

public sealed class SkuCodeGeneratorTests
{
    [Fact]
    public void Generate_UsesBrandCategoryPowerAndColor_ForLens()
    {
        var generator = new SkuCodeGenerator();
        var product = new Product
        {
            ProductType = CatalogValidation.Lens,
            Brand = new Brand { Name = "Lansee" },
            Category = new Category { Name = "Plain Medical" }
        };

        var code = generator.Generate(product, new SkuCodeInput("-", 1.25m, "Clear", null));

        Assert.Equal("LAN-PM-M125-CLEAR", code);
    }

    [Fact]
    public void Generate_UsesBrandCategoryAndSize_ForSolution()
    {
        var generator = new SkuCodeGenerator();
        var product = new Product
        {
            ProductType = CatalogValidation.Solution,
            Brand = new Brand { Name = "OptiCare" },
            Category = new Category { Name = "Preservation / Conservative Solution" }
        };

        var code = generator.Generate(product, new SkuCodeInput(null, null, null, "120ml"));

        Assert.Equal("OPT-PCS-120ML", code);
    }
}
