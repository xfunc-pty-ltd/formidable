using Formidable.AspNetCore;
using Formidable.Sample.Shared;
using Microsoft.AspNetCore.Mvc;

namespace Formidable.Sample.Api.Controllers;

// The ServerRoundTrip page's endpoint picker posts here as well as to the minimal-API group
// in Program.cs, so the same order can be sent through either hosting style and prove the
// validation filter's 400 contract is identical regardless of which one runs it.
[ApiController]
[Route("api/controller/orders")]
[Validate] // class-level: every action's validatable arguments run the Submit profile
public class OrdersController : ControllerBase
{
    [HttpPost]
    public IActionResult Create([FromBody] RoundTripOrder order) =>
        Ok(new { accepted = true, lines = order.Lines.Count });
}
