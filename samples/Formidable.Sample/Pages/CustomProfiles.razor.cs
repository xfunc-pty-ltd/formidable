using Formidable.Blazor;
using Formidable.Sample.Shared;

namespace Formidable.Sample.Pages;

public partial class CustomProfiles
{
    private ReviewedPost _post = new();
    private FormidableOptions _options = new() { SubmitProfile = ValidationProfile.Submit };
    private FormidableForm<ReviewedPost>? _form;
    private bool _adminReview;
    private string _profileName = "Standard submit";
    private string _status = string.Empty;

    // A fresh model instance is what makes the new Options take effect - FormidableForm
    // only re-reads Options when the Model reference changes, so a profile swap without a
    // model swap would silently keep validating under the old profile.
    private void SelectProfile(bool adminReview)
    {
        _adminReview = adminReview;
        _profileName = adminReview ? "Admin review" : "Standard submit";
        _post = new ReviewedPost();
        _options = new FormidableOptions
        {
            SubmitProfile = adminReview ? ReviewedPostValidator.AdminReview : ValidationProfile.Submit
        };
        _status = string.Empty;
    }

    private async Task Submit()
    {
        var outcome = await _form!.SubmitAsync();
        _status = $"Submitted under {_profileName} — {(outcome.CanProceed ? "accepted" : "blocked")}.";
    }
}
