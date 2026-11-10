#if NET11_0_OR_GREATER
using System.ComponentModel.DataAnnotations;
using System.Linq.Expressions;
using Bunit;
using FluentValidation;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;

namespace Formidable.Blazor.Tests;

/// <summary>
/// On .NET 11 a kit input, a renderless <see cref="FormidableField{TValue}"/> and a native
/// <c>InputText</c> on one field read the same state classes while Blazor's own async check of that
/// field runs, after it answers clean, and after it throws, with and without a Formidable error. The
/// check is an async DataAnnotations attribute behind a <c>DataAnnotationsValidator</c>, held on a
/// gate the test opens, so each state is read at a known moment.
/// </summary>
/// <remarks>
/// While the check runs, the classes are read in the work item of the edit that started it. As it
/// ends, they are read inside Blazor's own notification that it ended, by a handler subscribed after
/// every surface's, so the read sees what that notification re-rendered and nothing later. The test
/// waits for that notification itself, not for a render, and then reads the classes once more in a
/// later turn: the DOM settles on what the notification showed.
/// </remarks>
// Mutations that must break it: drop the engine's subscription to the EditContext's notification,
// and the kit input and the FormidableField keep the classes they had before the check started, or
// as it started; drop
// Blazor's pending read from the field state, or its faulted read, and the kit surfaces wear the
// valid class while the check runs, or after it threw, where the native input does not.
public class BlazorAsyncStateParityTests : BunitContext
{
    private const string Pending = "formidable-pending";
    private const string Valid = "formidable-valid";
    private const string Invalid = "formidable-invalid";

    public BlazorAsyncStateParityTests() => Services.AddFormidable();

    [Theory]
    [InlineData("fine", Pending, Valid)]
    [InlineData("boom", Pending, "")]
    [InlineData("a Formidable error", Invalid + " " + Pending, Invalid)]
    [InlineData("a Formidable error and a throw", Invalid + " " + Pending, Invalid)]
    public async Task Every_surface_reads_the_same_class_while_Blazor_checks_the_field_and_after(
        string edit, string whileChecking, string afterwards)
    {
        var model = new Profile();
        // Over Formidable's 40-character rule; the second also starts "boom", so Blazor's check throws.
        var value = edit switch
        {
            "a Formidable error" => new string('x', 41),
            "a Formidable error and a throw" => "boom" + new string('x', 37),
            _ => edit,
        };
        var cut = RenderProfile(model);
        var field = FieldIdentifier.Create(() => model.Nickname);
        var editContext = cut.FindComponent<FormidableForm<Profile>>().Instance.Engine!.EditContext;
        var settled = new TaskCompletionSource<Settled>(TaskCreationOptions.RunContinuationsAsynchronously);

        await cut.InvokeAsync(() =>
        {
            cut.Find("input[data-surface=native]").Change(value);

            Assert.True(editContext.IsValidationPending(field), "Blazor's check should be running");
            Assert.Equal(whileChecking, Classes(cut));
        });

        await cut.InvokeAsync(() => editContext.OnValidationStateChanged += (_, _) =>
        {
            if (!editContext.IsValidationPending(field))
            {
                settled.TrySetResult(new Settled(editContext.IsValidationFaulted(field), Classes(cut)));
            }
        });

        model.Gate.SetResult();
        var end = await settled.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(value.StartsWith("boom", StringComparison.Ordinal), end.Faulted);
        Assert.Equal(afterwards, end.Classes);
        await cut.InvokeAsync(() => Assert.Equal(afterwards, Classes(cut)));
    }

    // Blazor's check started with no field change at all, through the call an async validator of
    // Blazor's own makes, on a field an edit has already turned valid. No check of Formidable's
    // runs, so only the engine's answer to Blazor's own notification can redraw the kit input and
    // the FormidableField: as the check starts they lose the valid class with the native input,
    // and as it ends they get it back, or after a throw stay without it.
    [Theory]
    [InlineData(false, Valid)]
    [InlineData(true, "")]
    public async Task Every_surface_redraws_as_a_check_registered_on_the_EditContext_starts_and_ends(
        bool throws, string afterwards)
    {
        var model = new Profile();
        var cut = RenderProfile(model, alias: true);
        var field = FieldIdentifier.Create(() => model.Alias);
        var editContext = cut.FindComponent<FormidableForm<Profile>>().Instance.Engine!.EditContext;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var settled = new TaskCompletionSource<Settled>(TaskCreationOptions.RunContinuationsAsynchronously);

        await cut.InvokeAsync(() => cut.Find("input[data-surface=native]").Change("ok"));
        cut.WaitForState(() => Classes(cut) == Valid);

        await cut.InvokeAsync(() =>
        {
            editContext.RegisterAsyncFieldValidator(field, async cancellationToken =>
            {
                await gate.Task.WaitAsync(cancellationToken);
                if (throws)
                {
                    throw new InvalidOperationException("The check faulted.");
                }
            });

            Assert.True(editContext.IsValidationPending(field), "Blazor's check should be running");
            Assert.Equal(Pending, Classes(cut));
            editContext.OnValidationStateChanged += (_, _) =>
            {
                if (!editContext.IsValidationPending(field))
                {
                    settled.TrySetResult(new Settled(editContext.IsValidationFaulted(field), Classes(cut)));
                }
            };
        });

        gate.SetResult();
        var end = await settled.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(throws, end.Faulted);
        Assert.Equal(afterwards, end.Classes);
        await cut.InvokeAsync(() => Assert.Equal(afterwards, Classes(cut)));
    }

    // The three surfaces' classes, which must agree: the shared value when they do, the three
    // listed apart when they do not, so a failure names the surface that differs.
    private static string Classes(IRenderedComponent<IComponent> cut)
    {
        var native = ClassOf(cut.Find("input[data-surface=native]").GetAttribute("class"));
        var kit = ClassOf(cut.Find("input[data-surface=kit]").GetAttribute("class"));
        var renderless = ClassOf(cut.Find("span[data-surface=field]").GetAttribute("class"));
        return native == kit && kit == renderless
            ? native
            : $"native '{Short(native)}' kit '{Short(kit)}' field '{Short(renderless)}'";

        static string ClassOf(string? attribute) => attribute?.Trim() ?? string.Empty;

        static string Short(string classes) => classes.Replace("formidable-", string.Empty, StringComparison.Ordinal);
    }

    // The three surfaces over Nickname, or over Alias, which carries no attribute and no rule.
    private IRenderedComponent<IComponent> RenderProfile(Profile model, bool alias = false) =>
        Render(builder =>
        {
            Expression<Func<string?>> nickname = alias ? () => model.Alias : () => model.Nickname;
            var value = alias ? model.Alias : model.Nickname;
            var changed = EventCallback.Factory.Create<string?>(this, v =>
            {
                if (alias)
                {
                    model.Alias = v;
                }
                else
                {
                    model.Nickname = v;
                }
            });

            builder.OpenComponent<FormidableForm<Profile>>(0);
            builder.AddComponentParameter(1, "Model", model);
            builder.AddComponentParameter(2, "Validator",
                new FluentValidationModelValidator<Profile>(new ProfileValidator()));
            builder.AddComponentParameter(3, "ChildContent", (RenderFragment<FormidableFormContext>)(_ => inner =>
            {
                inner.OpenComponent<DataAnnotationsValidator>(0);
                inner.CloseComponent();

                inner.OpenComponent<InputText>(1);
                inner.AddComponentParameter(2, "data-surface", "native");
                inner.AddComponentParameter(3, "Value", value);
                inner.AddComponentParameter(4, "ValueChanged", changed);
                inner.AddComponentParameter(5, "ValueExpression", nickname);
                inner.CloseComponent();

                inner.OpenComponent<FormidableInputText>(6);
                inner.AddComponentParameter(7, "data-surface", "kit");
                inner.AddComponentParameter(8, "Value", value);
                inner.AddComponentParameter(9, "ValueChanged", changed);
                inner.AddComponentParameter(10, "ValueExpression", nickname);
                inner.CloseComponent();

                inner.OpenComponent<FormidableField<string?>>(11);
                inner.AddComponentParameter(12, "For", nickname);
                inner.AddComponentParameter(13, "ChildContent", (RenderFragment<FormidableFieldContext>)(context => span =>
                {
                    span.OpenElement(0, "span");
                    span.AddAttribute(1, "data-surface", "field");
                    span.AddAttribute(2, "class", context.CssClass);
                    span.CloseElement();
                }));
                inner.CloseComponent();
            }));
            builder.CloseComponent();
        });

    private sealed record Settled(bool Faulted, string Classes);

    public sealed class Profile
    {
        [GatedNickname]
        public string? Nickname { get; set; }

        public string? Alias { get; set; }

        /// <summary>Holds Blazor's check of <see cref="Nickname"/> until the test opens it.</summary>
        public TaskCompletionSource Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class ProfileValidator : AbstractValidator<Profile>
    {
        public ProfileValidator() =>
            RuleFor(x => x.Nickname).MaximumLength(40).WithMessage("Nickname is too long");
    }

    /// <summary>An async DataAnnotations attribute that waits for the model's gate, then passes, or throws for a value starting "boom".</summary>
    [AttributeUsage(AttributeTargets.Property)]
    private sealed class GatedNicknameAttribute : AsyncValidationAttribute
    {
        protected override ValidationResult? IsValid(object? value, ValidationContext validationContext) =>
            throw new NotSupportedException("The async path is the one under test.");

        protected override async Task<ValidationResult?> IsValidAsync(
            object? value, ValidationContext validationContext, CancellationToken cancellationToken)
        {
            await ((Profile)validationContext.ObjectInstance).Gate.Task.WaitAsync(cancellationToken);
            return value is string text && text.StartsWith("boom", StringComparison.Ordinal)
                ? throw new InvalidOperationException("The check faulted.")
                : ValidationResult.Success;
        }
    }
}
#endif
