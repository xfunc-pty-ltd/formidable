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

app.MapControllers();

app.Run();
