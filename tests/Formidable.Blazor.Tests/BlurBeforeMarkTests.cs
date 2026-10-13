using System.Linq.Expressions;
using Bunit;
using FluentValidation;
using Formidable.Blazor.Tests.Fixtures;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Formidable.Blazor.Tests;

/// <summary>
/// The visitor commits a value, leaves the box by pressing Submit, and the page marks its request
/// in <c>OnValidSubmit</c>. On Blazor Server the blur reaches the form before the press, but the
/// number and date inputs' blur work includes a script round trip that writes the box's value
/// back. The form hears of the change as the blur arrives, without waiting for that write, so the
/// mark comes after the change and the server's reply about the posted values applies. A page
/// handler the input awaits before it tells the form (an <c>@bind-Value:after</c> lookup) is
/// different: a press made while it runs marks before the change counts, and that reply is
/// dropped, as the docs say.
/// </summary>
/// <remarks>
/// The script write is held through <see cref="IFormidableDomValueSync"/>, as the browser round
/// trip is on Blazor Server, and the lookup through a gate the page's handler awaits.
/// </remarks>
// Mutation that must break it: await the sync before delivering the blur's notification, and the
// date and number rows drop the reply.
public class BlurBeforeMarkTests : BunitContext
{
    private readonly FakeTimeProvider _clock = new();
    private readonly HeldDomValueSync _sync = new();
    private readonly TaskCompletionSource _lookup = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _http = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private FormidableForm<Booking>? _form;
    private ValidationIssue? _serverIssue;
    private bool _markTaken;
    private bool? _applied;

    public BlurBeforeMarkTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton<IFormidableFocusService>(new RecordingFocusService());
        Services.AddSingleton<IFormidableDomValueSync>(_sync);
        Services.AddFormidableBlazor();
        Services.AddSingleton<TimeProvider>(_clock);
        Services.AddSingleton<IValidator<Booking>>(new BookingRules());
    }

    [Theory]
    [InlineData("date", true)]
    [InlineData("number", true)]
    [InlineData("text-after", false)]
    public async Task A_change_committed_before_the_press_counts_before_the_mark(string input, bool applies)
    {
        var model = new Booking();
        var (path, typed) = input switch
        {
            "date" => ("Day", "2026-03-14"),
            "number" => ("Seats", "4"),
            _ => ("Code", "B2"),
        };
        _serverIssue = new ValidationIssue(path, $"{path} {typed} is taken");
        var cut = RenderForm(model, input);
        _form = cut.Instance;
        await SettleAsync(cut);

        // The visitor commits a value. Date and number commit on change and wait for the blur to
        // tell the form; the text box's handler sets the model, then awaits its lookup.
        Task? change = null;
        if (input == "text-after")
        {
            await cut.InvokeAsync(() => { change = cut.Find("input").ChangeAsync(new ChangeEventArgs { Value = typed }); });
            await cut.InvokeAsync(() => { });
            Assert.False(change!.IsCompleted);
        }
        else
        {
            await cut.InvokeAsync(() => cut.Find("input").Change(typed));
        }

        // The press blurs the box first. The blur's script write stays out.
        Task? blur = null;
        if (input != "text-after")
        {
            await cut.InvokeAsync(() => { blur = cut.Find("input").BlurAsync(new FocusEventArgs()); });
            await cut.InvokeAsync(() => { });
            Assert.Single(_sync.Pending);
        }

        // The press's submit: synchronous rules, then OnValidSubmit marks and posts.
        Task? submit = null;
        await cut.InvokeAsync(() => { submit = cut.Find("form").SubmitAsync(); });
        for (var i = 0; i < 10 && !_markTaken; i++)
        {
            await cut.InvokeAsync(() => { });
        }

        Assert.True(_markTaken);

        // The held work finishes after the mark, then the server's reply arrives.
        if (input == "text-after")
        {
            await cut.InvokeAsync(() => _lookup.SetResult());
            await change!.WaitAsync(TimeSpan.FromSeconds(5));
        }
        else
        {
            await cut.InvokeAsync(() => _sync.Pending[0].SetResult());
            await blur!.WaitAsync(TimeSpan.FromSeconds(5));
        }

        await cut.InvokeAsync(() => _http.SetResult());
        await submit!.WaitAsync(TimeSpan.FromSeconds(5));
        await cut.InvokeAsync(() => { });

        Assert.Equal(applies, _applied);
        var field = new FieldIdentifier(model, path);
        Assert.Equal(applies, _form.Engine!.GetIssues(field).Any(issue => issue.Message == _serverIssue.Message));

        await Services.DisposeAsync();
    }

    // The blur tells the form while its script write is still out, and only then waits for the
    // write: the write starts first, and the blur's handler ends once it is done.
    [Theory]
    [InlineData("date")]
    [InlineData("number")]
    public async Task A_blur_tells_the_form_before_its_script_write_returns(string input)
    {
        var model = new Booking();
        var cut = RenderForm(model, input);
        await SettleAsync(cut);
        var changed = 0;
        cut.Instance.Engine!.EditContext.OnFieldChanged += (_, _) => changed++;

        await cut.InvokeAsync(() => cut.Find("input").Change(input == "date" ? "2026-03-14" : "4"));
        Assert.Equal(0, changed);

        Task? blur = null;
        await cut.InvokeAsync(() => { blur = cut.Find("input").BlurAsync(new FocusEventArgs()); });
        await cut.InvokeAsync(() => { });

        Assert.Single(_sync.Pending);
        Assert.Equal(1, changed);
        Assert.False(blur!.IsCompleted);

        await cut.InvokeAsync(() => _sync.Pending[0].SetResult());
        await blur.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, changed);

        await Services.DisposeAsync();
    }

    private async Task OnValidSubmitAsync(SubmitOutcome outcome)
    {
        var sentAt = _form!.MarkServerRequest();
        _markTaken = true;
        await _http.Task;
        _applied = _form.ApplyServerIssues([_serverIssue!], sentAt);
    }

    private async Task SettleAsync(IRenderedComponent<FormidableForm<Booking>> cut)
    {
        await cut.InvokeAsync(() => { });
        await cut.InvokeAsync(() => _clock.Advance(new FormidableOptions().RefreshDebounce + TimeSpan.FromMilliseconds(1)));
        cut.WaitForState(() => !cut.Instance.Engine!.IsValidating);
        await cut.InvokeAsync(() => { });
    }

    private IRenderedComponent<FormidableForm<Booking>> RenderForm(Booking model, string input)
    {
        var cut = Render(builder =>
        {
            builder.OpenComponent<FormidableForm<Booking>>(0);
            builder.AddComponentParameter(1, "Model", model);
            builder.AddComponentParameter(2, "OnValidSubmit", EventCallback.Factory.Create<SubmitOutcome>(this, OnValidSubmitAsync));
            builder.AddComponentParameter(3, "ChildContent", (RenderFragment<FormidableFormContext>)(_ => inner =>
            {
                switch (input)
                {
                    case "date":
                        inner.OpenComponent<FormidableInputDate<DateOnly?>>(0);
                        inner.AddComponentParameter(1, "For", (Expression<Func<DateOnly?>>)(() => model.Day));
                        inner.AddComponentParameter(2, "Value", model.Day);
                        inner.AddComponentParameter(3, "ValueChanged", EventCallback.Factory.Create<DateOnly?>(this, v => model.Day = v));
                        inner.AddComponentParameter(4, "UpdateOn", InputUpdateMode.OnBlur);
                        inner.CloseComponent();
                        break;
                    case "number":
                        inner.OpenComponent<FormidableInputNumber<int?>>(0);
                        inner.AddComponentParameter(1, "For", (Expression<Func<int?>>)(() => model.Seats));
                        inner.AddComponentParameter(2, "Value", model.Seats);
                        inner.AddComponentParameter(3, "ValueChanged", EventCallback.Factory.Create<int?>(this, v => model.Seats = v));
                        inner.AddComponentParameter(4, "UpdateOn", InputUpdateMode.OnBlur);
                        inner.CloseComponent();
                        break;
                    default:
                        // The shape @bind-Value:after compiles to: set the model, then await.
                        inner.OpenComponent<FormidableInputText>(0);
                        inner.AddComponentParameter(1, "For", (Expression<Func<string?>>)(() => model.Code));
                        inner.AddComponentParameter(2, "Value", model.Code);
                        inner.AddComponentParameter(3, "ValueChanged", EventCallback.Factory.Create<string?>(this, async v =>
                        {
                            model.Code = v;
                            await _lookup.Task;
                        }));
                        inner.CloseComponent();
                        break;
                }

                inner.AddMarkupContent(10, "<button type=\"submit\">Send</button>");
            }));
            builder.CloseComponent();
        });
        return cut.FindComponent<FormidableForm<Booking>>();
    }

    public sealed class Booking
    {
        public DateOnly? Day { get; set; }

        public int? Seats { get; set; }

        public string? Code { get; set; }
    }

    private sealed class BookingRules : AbstractValidator<Booking>
    {
        public BookingRules() => RuleFor(x => x.Code).MaximumLength(50);
    }

    /// <summary>The script write the number and date inputs start on blur, held until the test answers it.</summary>
    private sealed class HeldDomValueSync : IFormidableDomValueSync
    {
        public List<TaskCompletionSource> Pending { get; } = [];

        public ValueTask SyncValueAsync(string elementId, string? value)
        {
            var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Pending.Add(held);
            return new ValueTask(held.Task);
        }
    }
}
