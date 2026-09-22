using System.Linq.Expressions;
using Bunit;
using FluentValidation;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;

namespace Formidable.Blazor.Tests;

/// <summary>
/// Derives a control from <see cref="FormidableInputBase{TValue}"/> the way a consumer does —
/// from outside the library's assembly, with nothing but the public and protected surface — and
/// pins what the base hands it: field registration, the deterministic element id, the state class
/// merged with a splatted one, the aria pair, and value commit. The derive-your-own recipe in
/// docs/component-kit.md quotes <see cref="RatingInput"/> as its worked example, so these
/// assertions are that recipe's proof.
/// </summary>
public class FormidableInputBaseDerivationTests : BunitContext
{
    public FormidableInputBaseDerivationTests()
    {
        Services.AddFormidable();
        Services.AddSingleton<IValidator<Feedback>, FeedbackValidator>();
    }

    private IRenderedComponent<FormidableForm<Feedback>> RenderRating(
        Feedback feedback, params (string Name, object Value)[] attributes)
    {
        var cut = Render(builder =>
        {
            builder.OpenComponent<FormidableForm<Feedback>>(0);
            builder.AddComponentParameter(1, "Model", feedback);
            builder.AddComponentParameter(2, "ChildContent", (RenderFragment<FormidableFormContext>)(_ => inner =>
            {
                inner.OpenComponent<RatingInput>(0);
                inner.AddComponentParameter(1, "For", (Expression<Func<int>>)(() => feedback.Rating));
                inner.AddComponentParameter(2, "Value", feedback.Rating);
                inner.AddComponentParameter(3, "ValueChanged",
                    EventCallback.Factory.Create<int>(this, v => feedback.Rating = v));
                var sequence = 4;
                foreach (var (name, value) in attributes)
                {
                    inner.AddComponentParameter(sequence++, name, value);
                }

                inner.CloseComponent();
            }));
            builder.CloseComponent();
        });
        return cut.FindComponent<FormidableForm<Feedback>>();
    }

    [Fact]
    public void Derived_control_registers_its_field_for_disclosure()
    {
        var feedback = new Feedback();
        var form = RenderRating(feedback);

        Assert.True(form.Instance.Engine!.Registry.IsRegistered(
            new FieldIdentifier(feedback, nameof(Feedback.Rating))));
    }

    [Fact]
    public void Derived_control_renders_the_deterministic_element_id()
    {
        var feedback = new Feedback();
        var form = RenderRating(feedback);

        Assert.Equal(
            FormidableFieldId.For(new FieldIdentifier(feedback, nameof(Feedback.Rating))),
            form.Find("input").GetAttribute("id"));
    }

    [Fact]
    public void Derived_control_commits_its_value_and_takes_the_state_class()
    {
        var feedback = new Feedback();
        var form = RenderRating(feedback, ("class", "rating"));

        form.Find("input").Change("9"); // outside the draft rule's 1..5

        Assert.Equal(9, feedback.Rating);
        form.WaitForAssertion(() =>
        {
            var css = form.Find("input").GetAttribute("class");
            Assert.Contains("rating", css); // the consumer's own class survives
            Assert.Contains("formidable-invalid", css);
        });
    }

    [Fact]
    public void Derived_control_gets_the_aria_wiring()
    {
        var feedback = new Feedback();
        var form = RenderRating(feedback);

        form.Find("input").Change("9");

        form.WaitForAssertion(() =>
        {
            var input = form.Find("input");
            Assert.Equal("true", input.GetAttribute("aria-invalid"));
            Assert.Equal($"{input.GetAttribute("id")}-messages", input.GetAttribute("aria-describedby"));
        });
    }

    // The base's own cleanup must not depend on a derived author remembering a base call, so the
    // hook below never calls base: the registration has to be released anyway.
    [Fact]
    public void Base_releases_the_registration_even_though_the_hook_ignores_it()
    {
        var feedback = new Feedback();
        var field = new FieldIdentifier(feedback, nameof(Feedback.Rating));
        RenderFragment child = inner =>
        {
            inner.OpenComponent<HookOnlyInput>(0);
            inner.AddComponentParameter(1, "For", (Expression<Func<int>>)(() => feedback.Rating));
            inner.AddComponentParameter(2, "Value", feedback.Rating);
            inner.CloseComponent();
        };

        var cut = Render(builder =>
        {
            builder.OpenComponent<FormidableForm<Feedback>>(0);
            builder.AddComponentParameter(1, "Model", feedback);
            builder.AddComponentParameter(2, "ChildContent", (RenderFragment<FormidableFormContext>)(_ => inner =>
            {
                inner.OpenComponent<ToggleHost>(0);
                inner.AddComponentParameter(1, nameof(ToggleHost.Show), true);
                inner.AddComponentParameter(2, nameof(ToggleHost.ChildContent), child);
                inner.CloseComponent();
            }));
            builder.CloseComponent();
        });

        var form = cut.FindComponent<FormidableForm<Feedback>>();
        var input = cut.FindComponent<HookOnlyInput>().Instance;
        Assert.True(form.Instance.Engine!.Registry.IsRegistered(field));

        cut.FindComponent<ToggleHost>().Render(parameters =>
        {
            parameters.Add(p => p.Show, false);
            parameters.Add(p => p.ChildContent, child);
        });

        Assert.True(input.HookRan);
        Assert.False(form.Instance.Engine!.Registry.IsRegistered(field));
    }

    // A DisposeCore that throws must not leave the registration behind — the release runs in
    // Dispose's finally, so it survives whatever a derived control's own cleanup does.
    [Fact]
    public void A_throwing_DisposeCore_still_releases_the_registration()
    {
        var feedback = new Feedback();
        var field = new FieldIdentifier(feedback, nameof(Feedback.Rating));
        RenderFragment child = inner =>
        {
            inner.OpenComponent<ThrowingDisposeInput>(0);
            inner.AddComponentParameter(1, "For", (Expression<Func<int>>)(() => feedback.Rating));
            inner.AddComponentParameter(2, "Value", feedback.Rating);
            inner.CloseComponent();
        };

        var cut = Render(builder =>
        {
            builder.OpenComponent<FormidableForm<Feedback>>(0);
            builder.AddComponentParameter(1, "Model", feedback);
            builder.AddComponentParameter(2, "ChildContent", (RenderFragment<FormidableFormContext>)(_ => inner =>
            {
                inner.OpenComponent<ToggleHost>(0);
                inner.AddComponentParameter(1, nameof(ToggleHost.Show), true);
                inner.AddComponentParameter(2, nameof(ToggleHost.ChildContent), child);
                inner.CloseComponent();
            }));
            builder.CloseComponent();
        });

        var form = cut.FindComponent<FormidableForm<Feedback>>();
        Assert.True(form.Instance.Engine!.Registry.IsRegistered(field));

        // bUnit surfaces a disposal exception rather than swallowing it (it propagates from the
        // render call that triggers the unmount), so the exception itself is part of what this
        // pins: the registration still has to be released whether or not the caller catches it.
        var thrown = Assert.Throws<InvalidOperationException>(() =>
            cut.FindComponent<ToggleHost>().Render(parameters =>
            {
                parameters.Add(p => p.Show, false);
                parameters.Add(p => p.ChildContent, child);
            }));
        Assert.Equal("dispose blew up", thrown.Message);

        Assert.False(form.Instance.Engine!.Registry.IsRegistered(field));
    }

    // The DOM-sync seam is protected precisely so a control shaped like the kit's own number and
    // date inputs can exist outside the library: the deriver opts in through SyncsDomValueOnBlur
    // and does its write in SyncDomValueAsync. This pins the order the base's blur handler
    // guarantees — the consumer-splatted onblur first, then the sync, then (under OnBlur, with a
    // commit pending) the engine notification — because a sync running before the splatted
    // handler would overwrite the DOM state that handler was written to read, and a notification
    // delivered before the sync would start a live pass against a box that does not yet match
    // the model. The mutation that breaks it is reordering HandleBlurAsync.
    [Fact]
    public void A_syncing_deriver_sees_splat_then_sync_then_notification_on_blur()
    {
        var feedback = new Feedback();
        var log = new List<string>();
        var cut = Render(builder =>
        {
            builder.OpenComponent<FormidableForm<Feedback>>(0);
            builder.AddComponentParameter(1, "Model", feedback);
            builder.AddComponentParameter(2, "ChildContent", (RenderFragment<FormidableFormContext>)(_ => inner =>
            {
                inner.OpenComponent<SyncingInput>(0);
                inner.AddComponentParameter(1, "For", (Expression<Func<int>>)(() => feedback.Rating));
                inner.AddComponentParameter(2, "Value", feedback.Rating);
                inner.AddComponentParameter(3, "ValueChanged",
                    EventCallback.Factory.Create<int>(this, v => feedback.Rating = v));
                inner.AddComponentParameter(4, "UpdateOn", InputUpdateMode.OnBlur);
                inner.AddComponentParameter(5, nameof(SyncingInput.Log), log);
                inner.AddComponentParameter(6, "onblur",
                    EventCallback.Factory.Create<Microsoft.AspNetCore.Components.Web.FocusEventArgs>(
                        this, () => log.Add("splat")));
                inner.CloseComponent();
            }));
            builder.CloseComponent();
        });
        var form = cut.FindComponent<FormidableForm<Feedback>>();
        form.Instance.Engine!.EditContext.OnFieldChanged += (_, _) => log.Add("notify");

        cut.Find("input").Change("9"); // commits under OnBlur; arms the notification, delivers nothing
        Assert.DoesNotContain("notify", log);

        cut.Find("input").Blur();

        Assert.Equal(new[] { "splat", "sync", "notify" }, log);
    }

    /// <summary>
    /// A derived control opting into the DOM-sync seam from outside the library — the two
    /// overrides the docs' custom-control recipe shows, with the sync recording its turn instead
    /// of writing through <see cref="IFormidableDomValueSync"/>.
    /// </summary>
    private sealed class SyncingInput : FormidableInputBase<int>
    {
        [Parameter]
        public List<string>? Log { get; set; }

        protected override bool SyncsDomValueOnBlur => true;

        protected override ValueTask SyncDomValueAsync()
        {
            Log?.Add("sync");
            return ValueTask.CompletedTask;
        }

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenElement(0, "input");
            AddCommonAttributes(builder, 1);
            builder.AddAttribute(5, "value", Value);
            AddValueBinding(builder, 6);
            builder.CloseElement();
        }
    }

    /// <summary>Renders its child only while <see cref="Show"/> is true, so a test can unmount it.</summary>
    private sealed class ToggleHost : ComponentBase
    {
        [Parameter]
        public bool Show { get; set; }

        [Parameter]
        public RenderFragment? ChildContent { get; set; }

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            if (Show && ChildContent is not null)
            {
                builder.AddContent(0, ChildContent);
            }
        }
    }

    /// <summary>Markup-free derived control that overrides the disposal hook without calling base.</summary>
    private sealed class HookOnlyInput : FormidableInputBase<int>
    {
        public bool HookRan { get; private set; }

        protected override void DisposeCore() => HookRan = true;
    }

    /// <summary>Markup-free derived control whose disposal hook throws.</summary>
    private sealed class ThrowingDisposeInput : FormidableInputBase<int>
    {
        protected override void DisposeCore() => throw new InvalidOperationException("dispose blew up");
    }
}

// The derive-your-own recipe in docs/component-kit.md quotes this control verbatim; the two must
// stay identical, so a change here is a change there.
public sealed class RatingInput : FormidableInputBase<int>
{
    /// <inheritdoc />
    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.OpenElement(0, "input");
        AddCommonAttributes(builder, 1);
        builder.AddAttribute(5, "type", "range");
        builder.AddAttribute(6, "value", Value);
        AddValueBinding(builder, 7);
        builder.CloseElement();
    }
}

public sealed class Feedback
{
    public int Rating { get; set; }
}

public sealed class FeedbackValidator : DraftSubmitValidator<Feedback>
{
    protected override void ConfigureDraftRules() =>
        RuleFor(x => x.Rating).InclusiveBetween(1, 5).WithMessage("Rating must be between 1 and 5");

    protected override void ConfigureSubmitRules()
    {
    }
}
