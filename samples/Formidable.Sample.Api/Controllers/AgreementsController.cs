using Formidable.AspNetCore;
using Formidable.Sample.Shared;
using Microsoft.AspNetCore.Mvc;

namespace Formidable.Sample.Api.Controllers;

[ApiController]
[Route("api/agreements")]
[Validate] // class-level: every action's validatable arguments run the Submit profile
public class AgreementsController : ControllerBase
{
    [HttpPost]
    public IActionResult Create([FromBody] RoundTripOrder order) =>
        Ok(new { accepted = true, lines = order.Lines.Count });
}
