namespace Formidable.Sample.E2E;

/// <summary>Marks an E2E test: runs only when FORMIDABLE_E2E=1, otherwise self-skips.</summary>
public sealed class E2EFactAttribute : FactAttribute
{
    public E2EFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("FORMIDABLE_E2E") != "1")
        {
            Skip = "Set FORMIDABLE_E2E=1 (and run playwright install chromium once) to run E2E tests.";
        }
    }
}
