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

    // FormidableInputSelect ignores a splatted id (like every FormidableInputBase descendant). A
    // <label> wrapping a <select> has text content that includes every <option>'s own text, not
    // just the label's, which defeats an exact-match label lookup in test tooling (not an
    // accessibility defect) — so unlike the text fields above, Category needs an explicit for=,
    // addressed by the same deterministic id the component renders itself. Computed, not cached:
    // _post is a new instance after every profile switch.
    private string CategoryId => FormidableFieldId.For(_post, p => p.Category);

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
