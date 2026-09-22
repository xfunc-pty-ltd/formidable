using Formidable.AspNetCore;
using Formidable.Sample.Shared;
using Microsoft.AspNetCore.Mvc;

namespace Formidable.Sample.Api.Controllers;

// The ServerRoundTrip page's endpoint picker posts here as well as to the minimal-API group
// in Program.cs — two filters sharing one mapper, so the same order sent through either
// hosting style gets an identical errors member back; only the MVC envelope's extra traceId
// differs.
[ApiController]
[Route("api/controller/orders")]
[Validate] // class-level: every action's validatable arguments run the Submit profile
public class OrdersController : ControllerBase
{
    [HttpPost]
    public IActionResult Create([FromBody] RoundTripOrder order) =>
        Ok(new { accepted = true, lines = order.Lines.Count });
}
