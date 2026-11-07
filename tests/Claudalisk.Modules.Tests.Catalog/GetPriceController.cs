using Microsoft.AspNetCore.Mvc;

namespace Claudalisk.Modules.Tests.Catalog;

[ApiController]
[Route("api/catalog/prices")]
public sealed class GetPriceController : ControllerBase
{
    [HttpGet("{sku}")]
    public IActionResult Get(string sku) => Ok(sku);
}
