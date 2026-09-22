using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq.Expressions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;

namespace Formidable.Blazor;

/// <summary>
/// Base class for validated input components, and the kit's extension point for controls it does
/// not ship: it wires field registration, the engine state subscription, touch/notify plumbing,
/// the computed CSS class, the deterministic element id, and the aria attributes, so a derived
/// control only has to render markup and make two calls:
/// <see cref="AddCommonAttributes(RenderTreeBuilder, int)"/> for the shared attributes in the order
/// the kit's guarantees depend on, and <see cref="AddValueBinding(RenderTreeBuilder, int)"/> for
/// its value-commit attribute(s) — the one call that honours <see cref="UpdateOn"/> for
/// every mode, present and future, instead of a derived control re-deciding which DOM event to
/// bind. The field the input edits comes from <see cref="For"/> or from the
/// <see cref="ValueExpression"/> that <c>@bind-Value</c> supplies, and is (re-)read whenever the
/// cascaded <see cref="FormidableFormContext"/> is a new instance — including the first render and
/// again after a host such as <c>FormidableForm</c>/<c>FormidableValidator</c> swaps its model and
/// rebuilds its engine and registry — so the registration and the engine subscription always
/// target the currently-active context.
/// </summary>
/// <remarks>
/// Two guarantees a derived control inherits and should not work around: a consumer-splatted
/// <c>class</c> is merged with the computed state class rather than replaced (see
/// <see cref="CssClass"/>), and a consumer-supplied <c>id</c> is ignored — the rendered id is
/// always <see cref="ElementId"/>, because message lists, <c>aria-describedby</c> and
/// <see cref="IFormidableFocusService"/> all address the field by it. Both follow from rendering
/// <see cref="AdditionalAttributes"/> first and the computed values after, which is what
/// <see cref="AddCommonAttributes(RenderTreeBuilder, int)"/> does — the order is that call's to
/// keep, not a sequence a derived control transcribes.
/// The same race catches event handlers, not just attributes:
/// <see cref="AddValueBinding(RenderTreeBuilder, int)"/>'s own <c>oninput</c>/<c>onchange</c> are
/// rendered after <see cref="AdditionalAttributes"/> as well, and win outright — a consumer
/// splatting either of those is competing with the value binding itself. Its <c>onblur</c> under
/// <see cref="InputUpdateMode.OnBlur"/> is the exception: rather than clobber a handler the
/// consumer wrote for a different purpose, it chains — the splatted handler runs first, and the
/// engine notification follows once it completes.
/// </remarks>
/// <typeparam name="TValue">The field's value type.</typeparam>
public abstract class FormidableInputBase<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TValue>
    : ComponentBase, IDisposable
{
    private const string BlurAttributeName = "onblur";

    private readonly FormContextBinding _binding = new();

    /// <summary>
    /// The cascaded form context, and a derived control's route to the engine, the
    /// <c>EditContext</c> and the field registry when the members below are not enough. Supplied by
    /// a <c>FormidableForm</c>/<c>FormidableValidator</c> ancestor: it is null until parameters are
    /// first set, and a control rendered outside such an ancestor throws from
    /// <see cref="OnParametersSet"/> with a message naming the missing ancestor.
    /// </summary>
    [CascadingParameter]
    protected FormidableFormContext? Context { get; private set; }

    /// <summary>
    /// Accessor for the field this input edits, e.g. <c>() => Model.Description</c> — the
    /// explicit spelling, and optional: <c>@bind-Value</c> names the same field through
    /// <see cref="ValueExpression"/>, so ordinary markup writes it once rather than twice. Supply
    /// this when the input has no <c>@bind-Value</c> at all, or to deliberately override which
    /// field the input registers, validates and renders messages for — an explicit <c>For</c>
    /// wins over <see cref="ValueExpression"/> whenever both are present, silently and by design.
    /// With neither, the input throws from <see cref="OnParametersSet"/>: it has no field to speak
    /// for.
    /// </summary>
    [Parameter]
    public Expression<Func<TValue>>? For { get; set; }

    /// <summary>The field's current value.</summary>
    [Parameter]
    public TValue? Value { get; set; }

    /// <summary>Raised when the input commits a new value.</summary>
    [Parameter]
    public EventCallback<TValue?> ValueChanged { get; set; }

    /// <summary>
    /// The accessor behind <see cref="Value"/>, supplied by the Razor compiler for every
    /// <c>@bind-Value</c> usage — the third of the framework's <c>Value</c>/<c>ValueChanged</c>/
    /// <c>ValueExpression</c> parameter triple, the same convention native <c>InputBase</c>
    /// follows. Consumers do not set this by hand: writing
    /// <c>@bind-Value="_order.Description"</c> is what fills it in, and that is how an input with
    /// no <see cref="For"/> still knows which field it edits. Ignored when <see cref="For"/> is
    /// also present.
    /// </summary>
    [Parameter]
    public Expression<Func<TValue>>? ValueExpression { get; set; }

    /// <summary>Keeps the field revealed after disposal — for virtualized containers.</summary>
    [Parameter]
    public bool KeepRegistered { get; set; }

    /// <summary>Which native DOM event commits the value. Defaults to <see cref="InputUpdateMode.OnChange"/>.</summary>
    [Parameter]
    public InputUpdateMode UpdateOn { get; set; } = InputUpdateMode.OnChange;

    /// <summary>Additional attributes splatted onto the rendered element.</summary>
    [Parameter(CaptureUnmatchedValues = true)]
    public IReadOnlyDictionary<string, object>? AdditionalAttributes { get; set; }

    /// <summary>The resolved field identifier for <see cref="For"/> or <see cref="ValueExpression"/>.</summary>
    protected FieldIdentifier Field { get; private set; }

    /// <summary>
    /// The deterministic element id for this field (see <see cref="FormidableFieldId"/>). Message
    /// lists, <c>aria-describedby</c> and <see cref="IFormidableFocusService"/> all address the
    /// field by this id, so a concrete input must render it as written and must not let a
    /// consumer-splatted <c>id</c> replace it.
    /// </summary>
    protected string ElementId { get; private set; } = string.Empty;

    /// <summary>
    /// The id of the element listing this field's messages — <see cref="ElementId"/> plus the
    /// suffix <see cref="FormidableFieldId.MessagesFor(FieldIdentifier)"/> owns, computed once
    /// alongside <see cref="ElementId"/> at registration rather than per render. This is what
    /// <see cref="AddCommonAttributes"/> renders as <c>aria-describedby</c>, and what a control
    /// rendering that attribute by hand should point at.
    /// </summary>
    protected string MessagesElementId { get; private set; } = string.Empty;

    /// <summary>
    /// The field's current state (touched, modified, validating, errors, warnings). Each read asks
    /// the engine again; <see cref="AddCommonAttributes"/> reads it once and answers both the class
    /// and the aria attributes from that one read.
    /// </summary>
    protected FieldState State => Context!.Engine.GetFieldState(Field);

    /// <summary>
    /// The CSS class string to render: any <c>class</c> the consumer splatted through
    /// <see cref="AdditionalAttributes"/> first, then the computed state class (see
    /// <see cref="FormidableCss"/>). Merging rather than replacing mirrors the framework's own
    /// <c>InputBase.CssClass</c>, and means a consumer writing <c>class="form-control"</c> keeps
    /// their styling without silently discarding the invalid/valid/pending state class. Render this
    /// <em>after</em> splatting <see cref="AdditionalAttributes"/> so it wins the duplicate-attribute
    /// race (Blazor applies last-write-wins) — or let <see cref="AddCommonAttributes"/> render both
    /// in that order for you.
    /// </summary>
    /// <remarks>
    /// Returns the merged class string: any consumer-splatted <c>class</c> attribute first,
    /// then the computed state class. Wrapper authors needing the pure state class can call
    /// <see cref="FormidableCss.Compute"/> directly.
    /// </remarks>
    protected string CssClass => ComputeCssClass(State);

    /// <summary>
    /// Resolves the field from <see cref="For"/> or <see cref="ValueExpression"/>, registers it,
    /// and binds the engine subscription to the currently-cascaded context. A derived control that
    /// overrides this must call <c>base.OnParametersSet()</c>, or it registers nothing and never
    /// re-renders on a validation state change.
    /// </summary>
    protected override void OnParametersSet() =>
        _binding.Update(
            Context,
            GetType(),
            register: context =>
            {
                Field = FieldIdentifier.Create(FieldAccessor.RequireBoundField(For, ValueExpression, GetType()));
                ElementId = FormidableFieldId.For(Field);
                MessagesElementId = FormidableFieldId.MessagesFor(ElementId);
                return context.Registry.Register(Field, KeepRegistered);
            },
            stateChanged: OnEngineStateChanged);

    /// <summary>
    /// Adds the attributes every validated input shares, in the order that makes the kit's
    /// guarantees hold: <see cref="AdditionalAttributes"/> first, then <see cref="ElementId"/> as
    /// <c>id</c>, then <see cref="CssClass"/>, then the aria pair — <c>aria-invalid="true"</c>
    /// while the field has error-severity issues, and <c>aria-describedby</c> pointing at
    /// <see cref="MessagesElementId"/> while it has issues of any severity. Because the computed
    /// values enter the render tree after the splat, they win the duplicate-attribute race (Blazor
    /// applies last-write-wins): a consumer's <c>class</c> merges with the state class, and a
    /// consumer's <c>id</c> is ignored in favour of the id messages, <c>aria-describedby</c> and
    /// <see cref="IFormidableFocusService"/> all address the field by. Call it once, immediately
    /// after opening the element: it consumes <paramref name="sequence"/> through
    /// <paramref name="sequence"/> + 3, so the control's own attributes take
    /// <paramref name="sequence"/> + 4 onwards.
    /// </summary>
    /// <remarks>
    /// The ordering is a guarantee, not a convention a derived control transcribes: writing these
    /// four frames by hand is what a control does when it needs them somewhere this call cannot put
    /// them, and it takes the ordering — and a second engine read per property — on itself. This
    /// call reads the field's state and issues once each, and both the class and the aria
    /// attributes answer from that one read, so an element renders one consistent view of the
    /// field.
    /// </remarks>
    /// <param name="builder">The render tree being built.</param>
    /// <param name="sequence">The first of the four sequence numbers this call consumes.</param>
    protected void AddCommonAttributes(RenderTreeBuilder builder, int sequence)
    {
        var state = State;
        var issues = Context!.Engine.GetIssues(Field);

        builder.AddMultipleAttributes(sequence, AdditionalAttributes!);
        builder.AddAttribute(sequence + 1, "id", ElementId);
        builder.AddAttribute(sequence + 2, "class", ComputeCssClass(state));

        // Both aria attributes share one sequence number: attribute frames diff by name rather than
        // by sequence, and sharing it keeps this call's budget at four numbers for a control
        // numbering its own attributes around it.
        if (state.HasErrors)
        {
            builder.AddAttribute(sequence + 3, "aria-invalid", "true");
        }

        if (issues.Count > 0)
        {
            builder.AddAttribute(sequence + 3, "aria-describedby", MessagesElementId);
        }
    }

    /// <summary>
    /// Assigns <see cref="Value"/>, invokes <see cref="ValueChanged"/>, marks the field touched,
    /// and notifies the EditContext so the engine's live validation pass runs —
    /// <see cref="CommitValueAsync"/> followed by <see cref="NotifyChanged"/>, in one call. Right
    /// for <see cref="InputUpdateMode.OnChange"/>/<see cref="InputUpdateMode.OnInput"/>, where a
    /// single DOM event both commits the value and should start validation; call the two halves
    /// separately instead when an event should do only one (see
    /// <see cref="InputUpdateMode.OnBlur"/>, which
    /// <see cref="AddValueBinding(RenderTreeBuilder, int)"/> implements that
    /// way). A concrete input rendering its own markup can still call this directly from a change
    /// handler for the two combined modes.
    /// </summary>
    protected async Task SetCurrentValueAsync(TValue? value)
    {
        await CommitValueAsync(value);
        NotifyChanged();
    }

    /// <summary>
    /// Assigns <see cref="Value"/> and invokes <see cref="ValueChanged"/> — the value-commit half
    /// of <see cref="SetCurrentValueAsync"/>, without marking the field touched or notifying the
    /// EditContext. Pairs with <see cref="NotifyChanged"/> under
    /// <see cref="InputUpdateMode.OnBlur"/>: the model updates on <c>change</c> even though the
    /// value may not have settled yet (a date input firing per date-segment, for instance), and no
    /// live pass starts until the paired <see cref="NotifyChanged"/> call says it should.
    /// </summary>
    protected Task CommitValueAsync(TValue? value)
    {
        Value = value;
        return ValueChanged.HasDelegate ? ValueChanged.InvokeAsync(value) : Task.CompletedTask;
    }

    /// <summary>
    /// Marks the field touched and notifies the EditContext so the engine's live validation pass
    /// runs — the notify half of <see cref="SetCurrentValueAsync"/>, without touching
    /// <see cref="Value"/>. Pairs with <see cref="CommitValueAsync"/> under
    /// <see cref="InputUpdateMode.OnBlur"/>: call this once the value committed earlier has had a
    /// chance to settle. Named to match <see cref="FormidableFieldContext.NotifyChanged"/>, which
    /// does the same two things for a foreign control with no base class to call it from.
    /// </summary>
    protected void NotifyChanged()
    {
        Context!.Engine.MarkTouched(Field);
        Context.EditContext.NotifyFieldChanged(Field);
    }

    /// <summary>
    /// Adds the attribute(s) that commit a value change, honouring <see cref="UpdateOn"/>: under
    /// <see cref="InputUpdateMode.OnChange"/> (default) or <see cref="InputUpdateMode.OnInput"/> a
    /// single event both commits the value and notifies the engine (see
    /// <see cref="SetCurrentValueAsync"/>); under <see cref="InputUpdateMode.OnBlur"/> the two
    /// split across two events instead — the value commits on <c>change</c> via
    /// <see cref="CommitValueAsync"/>, and the engine is notified separately on <c>blur</c> via
    /// <see cref="NotifyChanged"/>, once the value has had a chance to settle. Reads
    /// <see cref="Value"/> directly rather than taking it as a parameter, since the base already
    /// owns it. Call this last, immediately before <see cref="RenderTreeBuilder.CloseElement"/>:
    /// it consumes <paramref name="sequence"/> and, under <see cref="InputUpdateMode.OnBlur"/>
    /// only, <paramref name="sequence"/> + 1 as well, so nothing else in the render tree should
    /// reuse either number. Also marks <c>value</c> as the attribute the just-added commit handler
    /// updates (<see cref="RenderTreeBuilder.SetUpdatesAttributeName(string)"/>), mirroring native
    /// <c>InputText</c>/<c>InputSelect</c>: before the handler runs, the renderer patches the
    /// current render tree's <c>value</c> frame to match what the browser already holds, so the
    /// following diff emits no edit when nothing actually changed.
    /// </summary>
    /// <remarks>
    /// The <c>onblur</c> this adds under <see cref="InputUpdateMode.OnBlur"/> chains rather than
    /// clobbers: a consumer-splatted <c>@onblur</c> handler is invoked first and awaited, and the
    /// engine notification follows. A splatted value that is not a .NET handler at all — a raw
    /// attribute string, say — has nothing to invoke, so only the notification runs.
    /// </remarks>
    protected void AddValueBinding(RenderTreeBuilder builder, int sequence)
    {
        if (UpdateOn == InputUpdateMode.OnBlur)
        {
            builder.AddAttribute(sequence, "onchange", EventCallback.Factory.CreateBinder<TValue?>(this, v => CommitValueAsync(v), Value));
            builder.SetUpdatesAttributeName("value");
            builder.AddAttribute(
                sequence + 1,
                BlurAttributeName,
                EventCallback.Factory.Create<FocusEventArgs>(this, HandleBlurAsync));
            return;
        }

        builder.AddAttribute(
            sequence,
            UpdateOn == InputUpdateMode.OnInput ? "oninput" : "onchange",
            EventCallback.Factory.CreateBinder<TValue?>(this, v => SetCurrentValueAsync(v), Value));
        builder.SetUpdatesAttributeName("value");
    }

    /// <summary>
    /// The string-projected value binding, for a control whose DOM value is always a string while
    /// its field is not — a <c>&lt;select&gt;</c> being the kit's own case. Binds <c>onchange</c>
    /// with <paramref name="formattedValue"/> as the currently-rendered string and
    /// <paramref name="setValueAsync"/> as the parse-and-commit step, and marks <c>value</c> as
    /// the attribute the handler updates, exactly as the
    /// <see cref="AddValueBinding(RenderTreeBuilder, int)"/> overload does — one policy for both,
    /// so a change to how the kit binds values or patches the <c>value</c> frame reaches every
    /// input. It consumes <paramref name="sequence"/> only.
    /// </summary>
    /// <remarks>
    /// This overload is deliberately fixed to <c>change</c> and ignores <see cref="UpdateOn"/>:
    /// the controls it serves have no meaningful <c>input</c> event distinct from <c>change</c>,
    /// and nothing to defer to <c>blur</c>. A control that wants the mode honoured takes the other
    /// overload and formats its own value instead. A consumer-splatted <c>@onblur</c> therefore
    /// passes straight through here — the library binds no <c>onblur</c> of its own to chain with.
    /// </remarks>
    protected void AddValueBinding(
        RenderTreeBuilder builder,
        int sequence,
        string? formattedValue,
        Func<string?, Task> setValueAsync)
    {
        builder.AddAttribute(
            sequence,
            "onchange",
            EventCallback.Factory.CreateBinder<string?>(this, setValueAsync, formattedValue));
        builder.SetUpdatesAttributeName("value");
    }

    /// <summary>
    /// Runs a consumer-splatted <c>onblur</c> handler, then notifies the engine — the chain
    /// <see cref="InputUpdateMode.OnBlur"/> needs so that wanting the <c>blur</c> event for
    /// validation does not quietly take it away from the consumer.
    /// </summary>
    private async Task HandleBlurAsync(FocusEventArgs args)
    {
        await InvokeSplattedBlurAsync(args);
        NotifyChanged();
    }

    /// <summary>
    /// Invokes whatever a consumer splatted as <c>onblur</c>, in whichever shape it arrived:
    /// <c>@onblur</c> in Razor markup compiles to an <see cref="EventCallback{TValue}"/> of
    /// <see cref="FocusEventArgs"/>, while markup built by hand can pass a plain delegate.
    /// Anything else — a string meant as a literal HTML attribute, most plausibly — is not
    /// callable from here and is left alone.
    /// </summary>
    private Task InvokeSplattedBlurAsync(FocusEventArgs args)
    {
        if (AdditionalAttributes is null || !AdditionalAttributes.TryGetValue(BlurAttributeName, out var splatted))
        {
            return Task.CompletedTask;
        }

        return splatted switch
        {
            EventCallback<FocusEventArgs> callback => callback.InvokeAsync(args),
            EventCallback callback => callback.InvokeAsync(args),
            Func<FocusEventArgs, Task> handler => handler(args),
            Func<Task> handler => handler(),
            Action<FocusEventArgs> handler => RunSynchronously(() => handler(args)),
            Action handler => RunSynchronously(handler),
            _ => Task.CompletedTask,
        };

        static Task RunSynchronously(Action handler)
        {
            handler();
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// The merged class string for a state already read — the one implementation behind
    /// <see cref="CssClass"/> and <see cref="AddCommonAttributes"/>, so the class a control renders
    /// by hand and the class the shared call renders are the same string by construction.
    /// </summary>
    private string ComputeCssClass(FieldState state) =>
        CombineClassNames(AdditionalAttributes, FormidableCss.Compute(state, Context!.Engine.Options.CssClasses));

    /// <summary>
    /// Joins a consumer-splatted <c>class</c> value (first) with the computed state class (last),
    /// tolerating either being absent or empty. Behaviourally equivalent to the framework's
    /// internal splat/class merge, reimplemented here rather than taken as a dependency on an
    /// internal type.
    /// </summary>
    private static string CombineClassNames(IReadOnlyDictionary<string, object>? additionalAttributes, string computed)
    {
        if (additionalAttributes is null || !additionalAttributes.TryGetValue("class", out var splatted))
        {
            return computed;
        }

        var splattedClass = Convert.ToString(splatted, CultureInfo.InvariantCulture);
        if (string.IsNullOrEmpty(splattedClass))
        {
            return computed;
        }

        return computed.Length == 0 ? splattedClass : $"{splattedClass} {computed}";
    }

    private void OnEngineStateChanged() => _ = InvokeAsync(StateHasChanged);

    /// <summary>
    /// Runs <see cref="DisposeCore"/>, then releases the field registration and the engine
    /// subscription. Deliberately not virtual: the base's cleanup is not a derived control's to
    /// forget, so a removed field cannot stay revealed because someone missed a base call.
    /// </summary>
    public void Dispose()
    {
        DisposeCore();
        _binding.Dispose();
    }

    /// <summary>
    /// Releases resources a derived control owns — a JS module, a timer, a subscription. Called by
    /// <see cref="Dispose"/> before the base releases the field registration and engine
    /// subscription, and doing nothing by default. A derived control implementing
    /// <see cref="IAsyncDisposable"/> owns the whole disposal path instead, because a component
    /// implementing both interfaces has only its async overload called: such a control must invoke
    /// <see cref="Dispose"/> from its <c>DisposeAsync</c>, or the registration is never released.
    /// </summary>
    protected virtual void DisposeCore()
    {
    }
}
