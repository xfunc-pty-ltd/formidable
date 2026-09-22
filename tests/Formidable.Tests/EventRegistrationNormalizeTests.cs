using Formidable.Sample.Shared;

namespace Formidable.Tests;

/// <summary>Pins the one Normalize() leg nothing else observes: the coupon upper-cases (the
/// browser only ever sees the trimmed effects, and no other test reads the model after
/// normalization).</summary>
public class EventRegistrationNormalizeTests
{
    [Fact]
    public void Normalize_upper_cases_and_trims_the_coupon()
    {
        var registration = new EventRegistration { CouponCode = "  save20  " };

        registration.Normalize();

        Assert.Equal("SAVE20", registration.CouponCode);
    }
}
