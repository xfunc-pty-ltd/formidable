using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;

namespace Formidable.Blazor;

/// <summary>The base of the kit's validated inputs and its extension point: a derived control renders its element and calls <see cref="AddCommonAttributes"/> and one of the <see cref="AddValueBinding(RenderTreeBuilder, int)"/> overloads; the base wires the field, the state class, the id and the aria attributes.</summary>
/// <typeparam name="TValue">The field's value type.</typeparam>
public abstract class FormidableInputBase<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TValue>
    : FormidableComponentBase
{
    private const string BlurAttributeName = "onblur";

    /// <summary>Whether a commit has happened after the engine was last told; set by <see cref="CommitValueAsync"/>, cleared by <see cref="NotifyChanged"/> and on rebind, and what a blur under <see cref="InputUpdateMode.OnBlur"/> delivers.</summary>
    private bool _notificationPending;

    /// <summary>The field this input speaks for when <c>@bind-Value</c> does not name it, or when another field should; wins over <see cref="ValueExpression"/>. Defaults to <see langword="null"/>, which leaves <see cref="ValueExpression"/> to name the field.</summary>
    /// <remarks>
    /// With neither this nor <c>@bind-Value</c>, the input throws from
    /// <see cref="FormidableComponentBase.OnParametersSet"/>, naming itself and both spellings.
    /// </remarks>
    [Parameter]
    public Expression<Func<TValue>>? For { get; set; }

    /// <summary>The field's current value.</summary>
    [Parameter]
    public TValue? Value { get; set; }

    /// <summary>Raised when the input commits a new value.</summary>
    [Parameter]
    public EventCallback<TValue?> ValueChanged { get; set; }

    /// <summary>The accessor <c>@bind-Value</c> supplies, which names the field when <see cref="For"/> is absent; not one you set by hand.</summary>
    [Parameter]
    public Expression<Func<TValue>>? ValueExpression { get; set; }

    /// <summary>Whether the field stays registered after this input is disposed, for rows a <c>Virtualize</c> container disposes while they remain in the form. Defaults to <see langword="false"/>.</summary>
    [Parameter]
    public bool KeepRegistered { get; set; }

    /// <summary>Which native event commits the value, and whether the engine hears about it then or on the next blur. Defaults to <see cref="InputUpdateMode.OnChange"/>.</summary>
    [Parameter]
    public InputUpdateMode UpdateOn { get; set; } = InputUpdateMode.OnChange;

    /// <summary>Attributes splatted onto the element ahead of the computed values: <c>class</c> and <c>aria-describedby</c> merge (splatted first), <c>id</c> is dropped, the commit event <see cref="UpdateOn"/> binds loses to the binding, and <c>onblur</c> runs ahead of the kit's blur work.</summary>
    [Parameter(CaptureUnmatchedValues = true)]
    public IReadOnlyDictionary<string, object>? AdditionalAttributes { get; set; }

    /// <summary>The field resolved from <see cref="For"/> or <see cref="ValueExpression"/> as the input registered.</summary>
    protected FieldIdentifier Field { get; private set; }

    /// <summary>The field's element id, as <see cref="FormidableFieldId.For(FieldIdentifier)"/> derives it; render it as written, because message lists, <c>aria-describedby</c> and <see cref="IFormidableFocusService"/> address the field by it.</summary>
    protected string ElementId { get; private set; } = string.Empty;

    /// <summary>The id of the field's message list, as <see cref="FormidableFieldId.MessagesFor(FieldIdentifier)"/> derives it; what <see cref="AddCommonAttributes"/> renders as <c>aria-describedby</c> while the field has issues.</summary>
    protected string MessagesElementId { get; private set; } = string.Empty;

    /// <summary>The field's current <see cref="FieldState"/>, read from the engine on each access.</summary>
    protected FieldState State => Context!.Engine.GetFieldState(Field);

    /// <summary>The class to render: any splatted <c>class</c> first, then the state class <see cref="FormidableCss.Compute"/> builds.</summary>
    // Merging rather than replacing mirrors the framework's own InputBase.CssClass, so a consumer
    // writing class="form-control" keeps their styling and still gets the state class.
    protected string CssClass => ComputeCssClass(State);

    /// <summary>The field <see cref="For"/> names, else the one <see cref="ValueExpression"/> names.</summary>
    /// <returns>The identifier <see cref="FieldIdentifier.Create{TField}"/> builds from whichever accessor is present.</returns>
    /// <exception cref="InvalidOperationException">Neither <see cref="For"/> nor <see cref="ValueExpression"/> is set; the message names the input and both spellings.</exception>
    private protected sealed override FieldIdentifier ResolveField() =>
        FieldIdentifier.Create(FieldAccessor.RequireBoundField(For, ValueExpression, GetType()));

    /// <summary>Resolves the field, computes its two ids and registers it with <paramref name="context"/>'s registry; sealed so every input speaks for the field its accessor names.</summary>
    /// <param name="context">The context being bound.</param>
    /// <returns>The registration the base releases on the next rebind or on disposal.</returns>
    // Sealed: an input that resolved a different field here, or none, would have no id to
    // render, nothing registered for disclosure, and no field to read state or issues for. What
    // a derived control is meant to change is the markup, and the behaviour it drives through
    // Context, not which field the control speaks for.
    protected sealed override FieldRegistration? Register(FormidableFormContext context)
    {
        // A commit made against the outgoing context is not delivered to its successor.
        _notificationPending = false;
        Field = ResolveField();
        ElementId = FormidableFieldId.For(Field);
        MessagesElementId = FormidableFieldId.MessagesFor(ElementId);
        return context.Registry.Register(Field, KeepRegistered);
    }

    /// <summary>Adds the attributes every validated input shares, in this order: the splat, then <c>id</c>, <c>class</c> and the aria attributes <see cref="FormidableFieldContext.InputAttributes"/> bundles, using four sequence numbers from <paramref name="sequence"/>.</summary>
    /// <param name="builder">The render tree being built.</param>
    /// <param name="sequence">The first of the four sequence numbers this call consumes; call it once, immediately after opening the element, and start the control's own attributes at <paramref name="sequence"/> + 4.</param>
    // The ordering is a guarantee, not a convention a derived control transcribes: the splat
    // enters first and the computed values after, so under Blazor's last-write-wins a consumer's
    // class and aria-describedby merge with the computed values and a consumer's id is ignored.
    // Writing these frames by hand is what a control does when it needs them somewhere this call
    // cannot put them, and it takes the ordering, and a second engine read per property, on
    // itself.
    protected void AddCommonAttributes(RenderTreeBuilder builder, int sequence)
    {
        // State and issues are read once each, so the class, aria-invalid and aria-describedby
        // render one consistent view of the field. aria-required is asked separately because what
        // the rules demand is not part of what the current values are doing.
        var state = State;
        var issues = Context!.Engine.GetIssues(Field);

        builder.AddMultipleAttributes(sequence, AdditionalAttributes!);
        builder.AddAttribute(sequence + 1, "id", ElementId);
        builder.AddAttribute(sequence + 2, "class", ComputeCssClass(state));

        // The aria attributes share one sequence number: attribute frames diff by name rather than
        // by sequence, and sharing it keeps this call's budget at four numbers for a control
        // numbering its own attributes around it.
        if (state.HasErrors)
        {
            builder.AddAttribute(sequence + 3, "aria-invalid", "true");
        }

        if (issues.Count > 0)
        {
            builder.AddAttribute(sequence + 3, "aria-describedby", ComputeAriaDescribedBy());
        }

        if (Context.Engine.GetFieldRequirement(Field) == FieldRequirement.Required)
        {
            builder.AddAttribute(sequence + 3, "aria-required", "true");
        }
    }

    /// <summary>The <c>aria-describedby</c> value while the field has issues: any splatted ids first, then <see cref="MessagesElementId"/>.</summary>
    /// <returns>The space-joined ids, or <see cref="MessagesElementId"/> alone.</returns>
    // Through CombineSplatted, the same method ComputeCssClass calls for class, so the two merges
    // are identical by construction rather than by coincidence. Splatted first because the
    // splatted ids are the only ones present while the field is clean: appending the messages id
    // when issues arrive adds to the end of the announced sequence, where prepending would
    // reshuffle the consumer's hint at the exact moment an error joins it.
    private string ComputeAriaDescribedBy() =>
        FormidableCss.CombineSplatted(AdditionalAttributes, "aria-describedby", MessagesElementId);

    /// <summary>Commits <paramref name="value"/> and tells the engine in one call (<see cref="CommitValueAsync"/> then <see cref="NotifyChanged"/>), for a control driving a commit from a handler of its own; the same two steps <see cref="InputUpdateMode.OnChange"/> and <see cref="InputUpdateMode.OnInput"/> take.</summary>
    /// <param name="value">The value to commit.</param>
    protected async Task SetCurrentValueAsync(TValue? value)
    {
        await CommitValueAsync(value);
        NotifyChanged();
    }

    /// <summary>Assigns <see cref="Value"/> and raises <see cref="ValueChanged"/> without telling the engine; the next <see cref="NotifyChanged"/> delivers one notification for any number of commits.</summary>
    /// <param name="value">The value to commit.</param>
    protected Task CommitValueAsync(TValue? value)
    {
        _notificationPending = true;
        Value = value;
        return ValueChanged.HasDelegate ? ValueChanged.InvokeAsync(value) : Task.CompletedTask;
    }

    /// <summary>Tells the engine the field's value was committed: the field is marked touched, engaged and checked live from then on; any commit waiting for a blur is delivered by this call instead.</summary>
    // Named to match FormidableFieldContext.NotifyChanged, which does the same for a foreign
    // control with no base class to call it from.
    protected void NotifyChanged()
    {
        _notificationPending = false;
        Context!.EditContext.NotifyFieldChanged(Field);
    }

    /// <summary>Whether the input binds <c>blur</c> in every <see cref="UpdateOn"/> mode so <see cref="SyncDomValueAsync"/> can rewrite an element that displays text it reports as empty; the number and date inputs return <see langword="true"/>. Defaults to <see langword="false"/>.</summary>
    // No render-tree diff can overwrite such text, because the rendered value and the reported
    // value already agree. The base owns the blur binding this flag turns on and the ordering
    // that comes with it; the override is the write alone.
    protected virtual bool SyncsDomValueOnBlur => false;

    /// <summary>Rewrites the element's value from <see cref="Value"/> on every blur while <see cref="SyncsDomValueOnBlur"/> is <see langword="true"/>, after any splatted <c>onblur</c> and before the notification <see cref="InputUpdateMode.OnBlur"/> delivers; does nothing by default.</summary>
    // The kit's number and date inputs write through IFormidableDomValueSync, addressed by
    // ElementId; binding blur, chaining the splatted handler and delivering the pending
    // notification all stay the base's.
    protected virtual ValueTask SyncDomValueAsync() => ValueTask.CompletedTask;

    /// <summary>Adds the event that commits the value under <see cref="UpdateOn"/>, and <c>onblur</c> where the mode or <see cref="SyncsDomValueOnBlur"/> calls for it, using <paramref name="sequence"/> and, for the blur, <paramref name="sequence"/> + 1.</summary>
    /// <param name="builder">The render tree being built.</param>
    /// <param name="sequence">The first sequence number this call consumes.</param>
    /// <remarks>
    /// Call it last, immediately before closing the element. A splatted <c>onblur</c> runs first
    /// and is awaited; a throw from it skips the kit's own blur work, so a value already committed
    /// stays, and under <see cref="InputUpdateMode.OnBlur"/> its notification waits for the next blur.
    /// </remarks>
    protected void AddValueBinding(RenderTreeBuilder builder, int sequence) =>
        AddCommitBinding<TValue?>(builder, sequence, Value, CommitTypedAsync, inputEventAvailable: true);

    /// <summary>The string-projected value binding, for a control whose DOM value is a string while its field is not (a <c>&lt;select&gt;</c>); under it <see cref="InputUpdateMode.OnInput"/> behaves as <see cref="InputUpdateMode.OnChange"/>, a select having no <c>input</c> event distinct from <c>change</c>.</summary>
    /// <param name="builder">The render tree being built.</param>
    /// <param name="sequence">The first sequence number this call consumes, as for <see cref="AddValueBinding(RenderTreeBuilder, int)"/>.</param>
    /// <param name="formattedValue">The field's current value, already formatted as a string.</param>
    /// <param name="tryCommitAsync">Parses the string the DOM committed and commits it, returning <see langword="true"/>; <see langword="false"/> when the string does not parse, in which case nothing commits and no blur notification is armed.</param>
    protected void AddValueBinding(
        RenderTreeBuilder builder,
        int sequence,
        string? formattedValue,
        Func<string?, Task<bool>> tryCommitAsync) =>
        AddCommitBinding<string?>(builder, sequence, formattedValue, tryCommitAsync, inputEventAvailable: false);

    /// <summary>Parses the string the DOM committed into <typeparamref name="TValue"/>; <see langword="false"/> leaves the field as it was.</summary>
    /// <param name="value">The string the DOM committed, exactly as the browser sent it.</param>
    /// <param name="result">The parsed value when parsing succeeds; undefined otherwise.</param>
    /// <returns><see langword="true"/> when <paramref name="value"/> parsed.</returns>
    protected delegate bool StringValueParser(string? value, out TValue? result);

    /// <summary>The string-projected value binding with a parser of the control's own, for a native number or date input whose DOM value keeps one fixed form whatever the browser's culture.</summary>
    /// <param name="builder">The render tree being built.</param>
    /// <param name="sequence">The first sequence number this call consumes, as for <see cref="AddValueBinding(RenderTreeBuilder, int)"/>.</param>
    /// <param name="formattedValue">The field's current value, already formatted as a string.</param>
    /// <param name="tryParseValue">Parses the string the DOM committed; a string it rejects commits nothing and arms no blur notification.</param>
    // The typed overload's binder resolves CultureInfo.CurrentCulture, while a native number or
    // date input always reports its value period-decimal or as ISO yyyy-MM-dd: under a
    // comma-decimal culture that binder silently misreads "12.5" as 125 rather than failing
    // loudly, and under a non-Gregorian calendar culture it can misread a year outright. This
    // overload exists so a control can parse invariantly and format-exactly (the number and date
    // inputs are the kit's two cases) while still getting every UpdateOn mode.
    protected void AddValueBinding(
        RenderTreeBuilder builder,
        int sequence,
        string? formattedValue,
        StringValueParser tryParseValue) =>
        AddCommitBinding<string?>(
            builder,
            sequence,
            formattedValue,
            value => CommitParsedAsync(tryParseValue, value),
            inputEventAvailable: true);

    /// <summary>The one implementation behind the three <see cref="AddValueBinding(RenderTreeBuilder, int)"/> overloads: the commit event, the <c>value</c> marking, and the blur binding where the mode or the control calls for it.</summary>
    /// <typeparam name="TBound">The type the binder round-trips: the field's own type where the DOM value converts directly, <see cref="string"/> where the control projects it through one.</typeparam>
    /// <param name="builder">The render tree being built.</param>
    /// <param name="sequence">The first sequence number this call consumes.</param>
    /// <param name="current">The value already rendered, which the binder compares against.</param>
    /// <param name="commitAsync">Commits what the DOM sent and reports whether it committed anything; <see langword="false"/> for a string the control's own parsing rejects.</param>
    /// <param name="inputEventAvailable">Whether the element has an <c>input</c> event distinct from <c>change</c>; <see langword="false"/> binds <c>onchange</c> under <see cref="InputUpdateMode.OnInput"/> too.</param>
    // What UpdateOn decides at render time is decided here and nowhere else, so two overloads
    // cannot drift into answering a mode differently; what an overload brings is the commit step
    // and whether the element has an input event worth binding. The mode's one remaining
    // consequence lives at event time instead, in HandleBlurAsync's gate on delivering a pending
    // notification. TBound is annotated for trimming because the binder converts through
    // reflection, the same reason TValue is.
    private void AddCommitBinding<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TBound>(
        RenderTreeBuilder builder,
        int sequence,
        TBound current,
        Func<TBound, Task<bool>> commitAsync,
        bool inputEventAvailable)
    {
        // OnBlur is the one mode that splits commit from notify, so it is the one mode where the
        // commit's report earns nothing immediately and blur has a notification to deliver.
        var deferNotification = UpdateOn == InputUpdateMode.OnBlur;

        builder.AddAttribute(
            sequence,
            inputEventAvailable && UpdateOn == InputUpdateMode.OnInput ? "oninput" : "onchange",
            EventCallback.Factory.CreateBinder<TBound>(this, value => CommitThenNotifyAsync(value), current));

        // Marks the attribute just added, so it has to follow that AddAttribute and precede the
        // blur binding below. Mirroring native InputText and InputSelect: before the handler
        // runs, the renderer patches the current render tree's value frame to match what the
        // browser already holds, so the following diff emits no edit when nothing changed.
        builder.SetUpdatesAttributeName("value");

        if (deferNotification || SyncsDomValueOnBlur)
        {
            AddBlurBinding(builder, sequence + 1);
        }

        async Task CommitThenNotifyAsync(TBound value)
        {
            var committed = await commitAsync(value);
            if (committed && !deferNotification)
            {
                NotifyChanged();
            }
        }
    }

    /// <summary>Binds <see cref="HandleBlurAsync"/> as the element's <c>onblur</c>, after the splat, so it wins the duplicate-attribute race and chains a splatted handler itself.</summary>
    /// <param name="builder">The render tree being built.</param>
    /// <param name="sequence">The sequence number the blur binding takes.</param>
    private void AddBlurBinding(RenderTreeBuilder builder, int sequence) =>
        builder.AddAttribute(
            sequence,
            BlurAttributeName,
            EventCallback.Factory.Create<FocusEventArgs>(this, HandleBlurAsync));

    /// <summary>The typed overload's commit step: the binder already converted the string, so it always reports a commit.</summary>
    /// <param name="value">The converted value.</param>
    /// <returns>Always <see langword="true"/>.</returns>
    private async Task<bool> CommitTypedAsync(TValue? value)
    {
        await CommitValueAsync(value);
        return true;
    }

    /// <summary>The parser overload's commit step: a string <paramref name="tryParseValue"/> rejects commits nothing and reports <see langword="false"/>.</summary>
    /// <param name="tryParseValue">The control's parser.</param>
    /// <param name="value">The string the DOM committed.</param>
    /// <returns><see langword="true"/> when the string parsed and the value was committed.</returns>
    private async Task<bool> CommitParsedAsync(StringValueParser tryParseValue, string? value)
    {
        if (!tryParseValue(value, out var parsed))
        {
            return false;
        }

        await CommitValueAsync(parsed);
        return true;
    }

    /// <summary>Runs a splatted <c>onblur</c>, then the DOM value sync where the control opts in, then, under <see cref="InputUpdateMode.OnBlur"/> with a commit pending, one notification.</summary>
    /// <param name="args">The blur event's arguments, handed to the splatted handler.</param>
    // The chain exists so that the kit wanting the blur event does not quietly take it away from
    // the consumer; the mode gate keeps the notification a blur-mode behaviour even for controls
    // whose sync binds blur in every mode; the commit gate makes blur a delivery rather than a
    // trigger, so a focus-then-leave with nothing committed notifies nothing, and however many
    // commits precede a blur, it delivers exactly one notification.
    private async Task HandleBlurAsync(FocusEventArgs args)
    {
        await InvokeSplattedBlurAsync(args);

        if (SyncsDomValueOnBlur)
        {
            await SyncDomValueAsync();
        }

        if (UpdateOn == InputUpdateMode.OnBlur && _notificationPending)
        {
            NotifyChanged();
        }
    }

    /// <summary>Invokes whatever a consumer splatted as <c>onblur</c>, in any callable shape, and ignores a value that is not callable.</summary>
    /// <param name="args">The blur event's arguments.</param>
    // @onblur in Razor markup compiles to an EventCallback<FocusEventArgs>, while markup built by
    // hand can pass a plain delegate; a string meant as a literal HTML attribute is not callable
    // from here and is left alone.
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

    /// <summary>The merged class string for a state already read, the one implementation behind <see cref="CssClass"/> and <see cref="AddCommonAttributes"/>: any splatted <c>class</c>, then the state class <see cref="FormidableCss.Compute"/> builds.</summary>
    /// <param name="state">The field state the class is computed from.</param>
    /// <returns>Both class strings space-joined, or whichever one is present.</returns>
    private string ComputeCssClass(FieldState state) =>
        FormidableCss.CombineClassNames(AdditionalAttributes, FormidableCss.Compute(state, Context!.Engine.Options.CssClasses));
}
