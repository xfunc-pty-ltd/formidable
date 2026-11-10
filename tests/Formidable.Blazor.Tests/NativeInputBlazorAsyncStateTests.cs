#if NET11_0_OR_GREATER
using System.ComponentModel.DataAnnotations;
using FluentValidation;
using Formidable.Introspection;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Formidable.Blazor.Tests;

/// <summary>
/// A native input beside a Formidable root wears the pending class while Blazor's own async
/// DataAnnotations check runs on its field, never the valid class, and never the valid class
/// after that check faults. The check's timing is a gate the test opens, so each state is read in
/// the notification that produced it, and the EditContext carries both Formidable's engine and the
/// DataAnnotations validation exactly as a root with a <c>DataAnnotationsValidator</c> beside it does.
/// </summary>
public class NativeInputBlazorAsyncStateTests
{
    [Fact]
    public async Task A_running_check_wears_pending_and_not_valid_then_valid_once_it_answers_clean()
    {
        using var rig = new Rig("fine");

        rig.Edit();

        // Every notification that carried Blazor's pending state read the pending class alone.
        Assert.NotEmpty(rig.WhilePending);
        Assert.All(rig.WhilePending, s => Assert.Equal("formidable-pending", s.Class));
        Assert.Equal("formidable-pending", rig.Current);

        await rig.OpenGateAndWaitAsync();

        Assert.Equal("formidable-valid", rig.Samples[^1].Class);
        Assert.Equal("formidable-valid", rig.Current);
    }

    [Fact]
    public async Task A_check_that_throws_leaves_the_field_neither_valid_nor_pending()
    {
        using var rig = new Rig("boom");

        rig.Edit();
        await rig.OpenGateAndWaitAsync();

        Assert.True(rig.Samples[^1].Faulted);
        Assert.Equal(string.Empty, rig.Samples[^1].Class);
        Assert.Equal(string.Empty, rig.Current);
    }

    [Fact]
    public async Task A_formidable_error_while_the_check_runs_reads_invalid_and_pending()
    {
        using var rig = new Rig(new string('x', 41));

        rig.Edit();

        Assert.NotEmpty(rig.WhilePending);
        Assert.All(rig.WhilePending, s => Assert.Equal("formidable-invalid formidable-pending", s.Class));
        Assert.Equal("formidable-invalid formidable-pending", rig.Current);

        await rig.OpenGateAndWaitAsync();

        Assert.Equal("formidable-invalid", rig.Current);
    }

    private sealed class Rig : IDisposable
    {
        private readonly ServiceProvider _services = new ServiceCollection().BuildServiceProvider();
        private readonly FormidableEngine<AsyncNicknameModel> _engine;
        private readonly TaskCompletionSource _settled = new();
        private bool _sawPending;

        public Rig(string nickname)
        {
            Model = new AsyncNicknameModel { Nickname = nickname };
            Context = new EditContext(Model);
            _engine = new FormidableEngine<AsyncNicknameModel>(
                Model, Context,
                new FluentValidationModelValidator<AsyncNicknameModel>(new AsyncNicknameModelValidator()),
                new ReflectionModelIntrospector(), new FormidableOptions(), new FakeTimeProvider());
            Context.EnableDataAnnotationsValidation(_services);
            Field = new FieldIdentifier(Model, nameof(AsyncNicknameModel.Nickname));
            Context.OnValidationStateChanged += (_, _) =>
            {
                var pending = Context.IsValidationPending(Field);
                Samples.Add(new Sample(pending, Context.IsValidationFaulted(Field), Context.FieldCssClass(Field)));
                _sawPending |= pending;
                if (_sawPending && !pending)
                {
                    _settled.TrySetResult();
                }
            };
        }

        public AsyncNicknameModel Model { get; }

        public EditContext Context { get; }

        public FieldIdentifier Field { get; }

        /// <summary>What each validation-state notification read, in the notification itself.</summary>
        public List<Sample> Samples { get; } = [];

        public IEnumerable<Sample> WhilePending => Samples.Where(s => s.Pending);

        public string Current => Context.FieldCssClass(Field);

        public void Edit() => Context.NotifyFieldChanged(Field);

        public async Task OpenGateAndWaitAsync()
        {
            Model.Gate.SetResult();
            await _settled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }

        public void Dispose()
        {
            _engine.Dispose();
            _services.Dispose();
        }
    }

    private sealed record Sample(bool Pending, bool Faulted, string Class);

    private sealed class AsyncNicknameModel
    {
        [GatedNickname]
        public string? Nickname { get; set; }

        public TaskCompletionSource Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class AsyncNicknameModelValidator : AbstractValidator<AsyncNicknameModel>
    {
        public AsyncNicknameModelValidator() =>
            RuleFor(x => x.Nickname).MaximumLength(40).WithMessage("Nickname is too long");
    }

    [AttributeUsage(AttributeTargets.Property)]
    private sealed class GatedNicknameAttribute : AsyncValidationAttribute
    {
        protected override ValidationResult? IsValid(object? value, ValidationContext validationContext) =>
            throw new NotSupportedException("The async path is the one under test.");

        protected override async Task<ValidationResult?> IsValidAsync(
            object? value, ValidationContext validationContext, CancellationToken cancellationToken)
        {
            await ((AsyncNicknameModel)validationContext.ObjectInstance).Gate.Task.WaitAsync(cancellationToken);
            return value switch
            {
                "boom" => throw new InvalidOperationException("The check faulted."),
                _ => ValidationResult.Success
            };
        }
    }
}
#endif
