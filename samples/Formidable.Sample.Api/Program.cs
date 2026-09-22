using Formidable;
using Formidable.AspNetCore;
using Formidable.Sample.Shared;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddFormidable();
builder.Services.AddValidatorsFromSharedAssembly();
builder.Services.AddControllers();
builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
    policy.WithOrigins("http://localhost:5181").AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();
app.UseCors();

// Group-level validation: every endpoint in the group runs the submit profile.
var orders = app.MapGroup("/api/orders").Validate<RoundTripOrder>();
orders.MapPost("/", (RoundTripOrder order) => Results.Ok(new { accepted = true, lines = order.Lines.Count }));

// The workout page's coupon story needs a rejection only the server can make - the client
// validator stays silent about codes it cannot know - so the handler owns the code list.
//
// The default profile (ValidationProfile.Submit) is enough here: the attendee name rule is a
// member of both the "Submit" and "Engaged" rulesets at its declaration
// (EventRegistrationValidator.ConfigureAdditionalProfiles), so a plain Submit validation already
// enforces it server-side with no extra profile wiring on this route.
var registrations = app.MapGroup("/api/registrations").Validate<EventRegistration>();
registrations.MapPost("/", (EventRegistration registration) =>
{
    var coupon = registration.CouponCode;
    var recognised = string.Equals(coupon, "WELCOME10", StringComparison.OrdinalIgnoreCase)
        || string.Equals(coupon, "SPEAKER", StringComparison.OrdinalIgnoreCase);

    if (!string.IsNullOrEmpty(coupon) && !recognised)
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["CouponCode"] = ["Coupon code is not recognised"]
        });
    }

    return Results.Ok(new { accepted = true, attendees = registration.Attendees.Count });
});

app.MapControllers();

app.Run();
