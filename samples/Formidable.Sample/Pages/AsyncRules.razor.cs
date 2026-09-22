using Formidable;
using Formidable.Blazor;
using Formidable.Sample.Shared;

namespace Formidable.Sample.Pages;

public partial class AsyncRules : IDisposable
{
    private readonly Handle _handle = new();
    private readonly FormidableOptions _options = new();

    // The DI-registered IValidator<Handle> behind SharedValidatorRegistration is the plain
    // HandleValidator that /field-state also uses; this page supplies MemoizedHandleValidator
    // explicitly instead, through the same Validator parameter every page could use, so its
    // uniqueness checks memoize while the field-state page's keep answering in full each time.
    private readonly IModelValidator<Handle> _validator = new FluentValidationModelValidator<Handle>(new MemoizedHandleValidator());

    private string _status = string.Empty;

    private static int DelayMs
    {
        get => HandleValidator.SimulatedDelayMs;
        set => HandleValidator.SimulatedDelayMs = value;
    }

    // Mutating the held instance's properties (not reassigning it) is what lets this take
    // effect without a Model swap — FormidableForm reads Options once by reference, but the
    // engine reads each property off that same instance live, on every field change.
    private bool LiveDebounceEnabled
    {
        get => _options.LiveDebounce is not null;
        set => _options.LiveDebounce = value ? TimeSpan.FromMilliseconds(400) : null;
    }

    private void HandleValid() => _status = "Submitted — username checks passed.";

    // SimulatedDelayMs is a static shared by every page that runs Handle's async rules (e.g.
    // field-state); restore the default on leaving so this page's slider doesn't strand
    // other pages' async timing at whatever value was last dragged here.
    public void Dispose() => HandleValidator.SimulatedDelayMs = 600;
}
