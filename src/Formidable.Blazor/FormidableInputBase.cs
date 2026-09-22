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
/// Three guarantees a derived control inherits and should not work around: a consumer-splatted
/// <c>class</c> is merged with the computed state class rather than replaced (see
/// <see cref="CssClass"/>); a consumer-splatted <c>aria-describedby</c> is likewise merged —
/// while the field has issues, the computed messages id is appended after the splatted ids, so a
/// persistent hint keeps its association through the field's whole issue lifecycle; and a
/// consumer-supplied <c>id</c> is ignored — the rendered id is
/// always <see cref="ElementId"/>, because message lists, <c>aria-describedby</c> and
/// <see cref="IFormidableFocusService"/> all address the field by it. All three follow from rendering
/// <see cref="AdditionalAttributes"/> first and the computed values after, which is what
/// <see cref="AddCommonAttributes(RenderTreeBuilder, int)"/> does — the order is that call's to
/// keep, not a sequence a derived control transcribes.
/// The same race catches event handlers, not just attributes:
/// <see cref="AddValueBinding(RenderTreeBuilder, int)"/>'s own <c>oninput</c>/<c>onchange</c> are
/// rendered after <see cref="AdditionalAttributes"/> as well, and win outright — a consumer
/// splatting either of those is competing with the value binding itself. The <c>onblur</c> the
/// kit binds is the exception: rather than clobber a handler the consumer wrote for a different
/// purpose, it chains — the splatted handler runs first, and the kit's own work follows once it
/// completes: the DOM value sync for a control that opts in, then the delivery of any
/// notification a value commit has left pending. That delivery happens only under
/// <see cref="InputUpdateMode.OnBlur"/>, the one mode that binds blur on every input; a control
/// that syncs its DOM value on blur (<see cref="FormidableInputNumber{TValue}"/> and
/// <see cref="FormidableInputDate{TValue}"/>, via <see cref="IFormidableDomValueSync"/>) binds
/// it in every mode.
/// </remarks>
/// <typeparam name="TValue">The field's value type.</typeparam>
public abstract class FormidableInputBase<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TValue>
    : FormidableComponentBase
{
    private const string BlurAttributeName = "onblur";

    /// <summary>
    /// Whether a value commit has occurred since the engine was last notified — armed by
    /// <see cref="CommitValueAsync"/>, consumed by <see cref="NotifyChanged"/>, cleared on
    /// rebind. Under <see cref="InputUpdateMode.OnBlur"/> this is what makes blur a delivery
    /// rather than a trigger: <see cref="HandleBlurAsync"/> notifies only while one is pending.
    /// </summary>
    private bool _notificationPending;

    /// <summary>
    /// Accessor for the field this input edits, e.g. <c>() => Model.Description</c> — the
    /// explicit spelling, and optional: <c>@bind-Value</c> names the same field through
    /// <see cref="ValueExpression"/>, so ordinary markup writes it once rather than twice. Supply
    /// this when the input has no <c>@bind-Value</c> at all, or to deliberately override which
    /// field the input registers, validates and renders messages for — an explicit <c>For</c>
    /// wins over <see cref="ValueExpression"/> whenever both are present, silently and by design.
    /// With neither, the input throws from <see cref="FormidableComponentBase.OnParametersSet"/>:
    /// it has no field to speak for.
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

    /// <summary>Keeps the field registered after disposal — for virtualized containers.</summary>
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
    /// <see cref="AddCommonAttributes"/> renders as <c>aria-describedby</c> (appended after any
    /// consumer-splatted value), and what a control rendering that attribute by hand should
    /// point at.
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
    /// their styling without silently discarding the invalid/warning/info/valid/pending state
    /// class. Render this
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

    /// <inheritdoc />
    private protected sealed override FieldIdentifier ResolveField() =>
        FieldIdentifier.Create(FieldAccessor.RequireBoundField(For, ValueExpression, GetType()));

    /// <summary>
    /// Resolves the field from <see cref="For"/> or <see cref="ValueExpression"/>, computes the
    /// ids that address it, and registers it with the cascaded context's field registry. Called by
    /// <see cref="FormidableComponentBase.OnParametersSet"/> whenever the cascaded context is a new
    /// instance, so a derived control that overrides that method must call
    /// <c>base.OnParametersSet()</c>, or it registers nothing and never re-renders on a validation
    /// state change.
    /// Sealed: an input that resolved a different field here, or none, would have no id to render,
    /// nothing registered for disclosure, and no field to read state or issues for. What a derived
    /// control is meant to change is the markup, and the behaviour it drives through
    /// <see cref="FormidableComponentBase.Context"/> — not which field the control speaks for.
    /// </summary>
    /// <param name="context">The context now being bound.</param>
    /// <returns>The registration the base releases on the next rebind or on disposal.</returns>
    protected sealed override FieldRegistration? Register(FormidableFormContext context)
    {
        // A commit made against the outgoing context is not delivered to its successor.
        _notificationPending = false;
        Field = ResolveField();
        ElementId = FormidableFieldId.For(Field);
        MessagesElementId = FormidableFieldId.MessagesFor(ElementId);
        return context.Registry.Register(Field, KeepRegistered);
    }

    /// <summary>
    /// Adds the attributes every validated input shares, in the order that makes the kit's
    /// guarantees hold: <see cref="AdditionalAttributes"/> first, then <see cref="ElementId"/> as
    /// <c>id</c>, then <see cref="CssClass"/>, then the aria attributes —
    /// <c>aria-invalid="true"</c> while the field has error-severity issues,
    /// <c>aria-describedby</c> while it has issues of any severity — any consumer-splatted
    /// <c>aria-describedby</c> first, then <see cref="MessagesElementId"/> appended, the same
    /// merge the <c>class</c> gets — and <c>aria-required="true"</c> while
    /// <see cref="IFormValidationEngine.GetFieldRequirement"/> reports the submit profile
    /// demands a value for it. Because the computed
    /// values enter the render tree after the splat, they win the duplicate-attribute race (Blazor
    /// applies last-write-wins): a consumer's <c>class</c> and <c>aria-describedby</c> merge with
    /// the computed values, and a
    /// consumer's <c>id</c> is ignored in favour of the id messages, <c>aria-describedby</c> and
    /// <see cref="IFormidableFocusService"/> all address the field by. Call it once, immediately
    /// after opening the element: it consumes <paramref name="sequence"/> through
    /// <paramref name="sequence"/> + 3, so the control's own attributes take
    /// <paramref name="sequence"/> + 4 onwards.
    /// </summary>
    /// <remarks>
    /// The ordering is a guarantee, not a convention a derived control transcribes: writing these
    /// frames by hand is what a control does when it needs them somewhere this call cannot put
    /// them, and it takes the ordering — and a second engine read per property — on itself. This
    /// call reads the field's state and issues once each, and the class, <c>aria-invalid</c> and
    /// <c>aria-describedby</c> all answer from that one read, so an element renders one
    /// consistent view of the field. <c>aria-required</c> is asked separately, because what the
    /// rules demand of a field is not part of what the current values are doing — the engine
    /// answers it from the submit profile's declared rules, cached, so the extra ask is a
    /// dictionary lookup.
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

    /// <summary>
    /// The <c>aria-describedby</c> value <see cref="AddCommonAttributes"/> renders while the
    /// field has issues: any consumer-splatted <c>aria-describedby</c> first, then
    /// <see cref="MessagesElementId"/> — the same consumer-first, computed-appended merge
    /// <see cref="CssClass"/> applies to <c>class</c>. Splatted first because the splatted ids
    /// are the only ones present while the field is clean: appending the messages id when
    /// issues arrive adds to the end of the announced sequence, where prepending would
    /// reshuffle the consumer's hint at the exact moment an error joins it.
    /// </summary>
    private string ComputeAriaDescribedBy()
    {
        if (AdditionalAttributes is null || !AdditionalAttributes.TryGetValue("aria-describedby", out var splatted))
        {
            return MessagesElementId;
        }

        var splattedIds = Convert.ToString(splatted, CultureInfo.InvariantCulture);
        return string.IsNullOrEmpty(splattedIds) ? MessagesElementId : $"{splattedIds} {MessagesElementId}";
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
    /// handler for the two combined modes. The commit arms a pending notification and the
    /// immediate <see cref="NotifyChanged"/> consumes it, so the two-call sequence leaves nothing
    /// pending for a later blur to deliver.
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
    /// live pass starts until the paired <see cref="NotifyChanged"/> call says it should. Each
    /// call also arms the pending notification that mode's blur delivers — however many commits
    /// accumulate before the blur, <see cref="NotifyChanged"/> consumes them as one.
    /// </summary>
    protected Task CommitValueAsync(TValue? value)
    {
        _notificationPending = true;
        Value = value;
        return ValueChanged.HasDelegate ? ValueChanged.InvokeAsync(value) : Task.CompletedTask;
    }

    /// <summary>
    /// Notifies the EditContext that the field changed, which is what marks it touched and runs the
    /// engine's live validation pass — the notify half of <see cref="SetCurrentValueAsync"/>,
    /// without touching <see cref="Value"/>. Pairs with <see cref="CommitValueAsync"/> under
    /// <see cref="InputUpdateMode.OnBlur"/>: call this once the value committed earlier has had a
    /// chance to settle. Delivering the notification consumes any pending one a commit armed,
    /// whichever path calls it, so a following blur under <see cref="InputUpdateMode.OnBlur"/>
    /// delivers nothing of its own. Named to match
    /// <see cref="FormidableFieldContext.NotifyChanged"/>, which does the same for a foreign
    /// control with no base class to call it from.
    /// </summary>
    protected void NotifyChanged()
    {
        _notificationPending = false;
        Context!.EditContext.NotifyFieldChanged(Field);
    }

    /// <summary>
    /// Whether <see cref="AddValueBinding(RenderTreeBuilder, int)"/> binds <c>blur</c> in every
    /// <see cref="UpdateOn"/> mode so <see cref="SyncDomValueAsync"/> can run there. False by
    /// default: a control whose DOM always displays exactly what it reports has nothing to
    /// reconcile. The kit's number and date inputs opt in, because their native elements can keep
    /// displaying text they report as empty — which no render-tree diff can overwrite, since the
    /// rendered value and the reported value already agree. A derived control whose element has
    /// the same property makes the identical two-override opt-in: return
    /// <see langword="true"/> here, and put the write in <see cref="SyncDomValueAsync"/>. The
    /// base owns the blur binding this flag turns on, and the ordering guarantees that come with
    /// it — nothing else is the deriver's to wire.
    /// </summary>
    protected virtual bool SyncsDomValueOnBlur => false;

    /// <summary>
    /// Writes the field's authoritative value into the DOM element on <c>blur</c> — a no-op by
    /// default; a control opting in via <see cref="SyncsDomValueOnBlur"/> overrides this to pass
    /// its currently-formatted <see cref="Value"/> to <see cref="IFormidableDomValueSync"/>
    /// (injected into the derived class; addressed by <see cref="ElementId"/>). Runs
    /// on every blur, whether or not anything committed — the box must revert either way — and
    /// the base guarantees where in the blur chain it runs: after
    /// any consumer-splatted <c>onblur</c> and, under <see cref="InputUpdateMode.OnBlur"/>,
    /// before any engine notification the blur delivers, so the live pass renders against a box
    /// that already matches the model. The override is the write alone — binding <c>blur</c>,
    /// chaining the splatted handler, and delivering the pending notification all stay the
    /// base's (see <see cref="AddValueBinding(RenderTreeBuilder, int)"/>'s remarks for the
    /// chain).
    /// </summary>
    protected virtual ValueTask SyncDomValueAsync() => ValueTask.CompletedTask;

    /// <summary>
    /// Adds the attribute(s) that commit a value change, honouring <see cref="UpdateOn"/>: under
    /// <see cref="InputUpdateMode.OnChange"/> (default) or <see cref="InputUpdateMode.OnInput"/> a
    /// single event both commits the value and notifies the engine (see
    /// <see cref="SetCurrentValueAsync"/>); under <see cref="InputUpdateMode.OnBlur"/> the two
    /// split across two events instead — the value commits on <c>change</c> via
    /// <see cref="CommitValueAsync"/>, arming a pending notification that the next <c>blur</c>
    /// delivers via <see cref="NotifyChanged"/>, once the value has had a chance to settle; a
    /// blur with no commit pending delivers nothing. Reads
    /// <see cref="Value"/> directly rather than taking it as a parameter, since the base already
    /// owns it. Call this last, immediately before <see cref="RenderTreeBuilder.CloseElement"/>:
    /// it consumes <paramref name="sequence"/>, and <paramref name="sequence"/> + 1 whenever it
    /// also binds <c>blur</c> — under <see cref="InputUpdateMode.OnBlur"/>, or in any mode for a
    /// control that syncs its DOM value on blur — so nothing else in the render tree should
    /// reuse either number. Also marks <c>value</c> as the attribute the just-added commit handler
    /// updates (<see cref="RenderTreeBuilder.SetUpdatesAttributeName(string)"/>), mirroring native
    /// <c>InputText</c>/<c>InputSelect</c>: before the handler runs, the renderer patches the
    /// current render tree's <c>value</c> frame to match what the browser already holds, so the
    /// following diff emits no edit when nothing actually changed.
    /// </summary>
    /// <remarks>
    /// The <c>onblur</c> this adds chains rather than clobbers: a consumer-splatted
    /// <c>@onblur</c> handler is invoked first and awaited, and the kit's own blur work — the DOM
    /// value sync for a control that opts in, then under <see cref="InputUpdateMode.OnBlur"/> the
    /// delivery of a pending commit notification — follows. A splatted value that is not a .NET
    /// handler at all — a raw attribute string, say — has nothing to invoke, so only the kit's
    /// work runs. If the consumer handler throws, it propagates as an unhandled component
    /// exception and neither the sync nor the notification runs — the value itself was already
    /// committed on the earlier <c>change</c> event either way, and its notification stays
    /// pending for the next blur to deliver.
    /// </remarks>
    protected void AddValueBinding(RenderTreeBuilder builder, int sequence)
    {
        if (UpdateOn == InputUpdateMode.OnBlur)
        {
            builder.AddAttribute(sequence, "onchange", EventCallback.Factory.CreateBinder<TValue?>(this, v => CommitValueAsync(v), Value));
            builder.SetUpdatesAttributeName("value");
            AddBlurBinding(builder, sequence + 1);
            return;
        }

        builder.AddAttribute(
            sequence,
            UpdateOn == InputUpdateMode.OnInput ? "oninput" : "onchange",
            EventCallback.Factory.CreateBinder<TValue?>(this, v => SetCurrentValueAsync(v), Value));
        builder.SetUpdatesAttributeName("value");

        if (SyncsDomValueOnBlur)
        {
            AddBlurBinding(builder, sequence + 1);
        }
    }

    /// <summary>
    /// The string-projected value binding, for a control whose DOM value is always a string while
    /// its field is not — a <c>&lt;select&gt;</c> being the kit's own case. Honours
    /// <see cref="UpdateOn"/>, with one coercion: a <c>&lt;select&gt;</c> has no meaningful
    /// <c>input</c> event distinct from <c>change</c> the way a text box does, so
    /// <see cref="InputUpdateMode.OnInput"/> behaves exactly like
    /// <see cref="InputUpdateMode.OnChange"/> (the default) — both bind <c>onchange</c> and, once
    /// <paramref name="tryCommitAsync"/> reports a value was committed, notify the engine
    /// immediately. Under <see cref="InputUpdateMode.OnBlur"/> the same <c>change</c> event still
    /// commits the value, but the notification the commit arms defers to <c>blur</c> instead,
    /// riding the same <see cref="HandleBlurAsync"/> the typed overload uses — including the
    /// consumer-splatted-<c>onblur</c> chaining; a string that fails to parse commits nothing and
    /// arms nothing, so the following blur delivers nothing. Also marks <c>value</c> as the
    /// attribute the commit handler updates, exactly as
    /// <see cref="AddValueBinding(RenderTreeBuilder, int)"/> does. It consumes <paramref name="sequence"/>, and <paramref name="sequence"/> + 1 under
    /// <see cref="InputUpdateMode.OnBlur"/>.
    /// </summary>
    /// <param name="builder">The render tree being built.</param>
    /// <param name="sequence">The first sequence number this call consumes.</param>
    /// <param name="formattedValue">The field's current value, already formatted as a string.</param>
    /// <param name="tryCommitAsync">
    /// Parses the DOM-committed string and, on success, commits it (see
    /// <see cref="CommitValueAsync"/>) and returns <see langword="true"/>; returns
    /// <see langword="false"/> without committing when the string does not parse. Whether to
    /// notify the engine afterwards is this call's decision, not the delegate's — see the mode
    /// split above.
    /// </param>
    protected void AddValueBinding(
        RenderTreeBuilder builder,
        int sequence,
        string? formattedValue,
        Func<string?, Task<bool>> tryCommitAsync)
    {
        if (UpdateOn == InputUpdateMode.OnBlur)
        {
            builder.AddAttribute(
                sequence,
                "onchange",
                EventCallback.Factory.CreateBinder<string?>(this, v => tryCommitAsync(v), formattedValue));
            builder.SetUpdatesAttributeName("value");
            AddBlurBinding(builder, sequence + 1);
            return;
        }

        builder.AddAttribute(
            sequence,
            "onchange",
            EventCallback.Factory.CreateBinder<string?>(this, v => CommitAndNotifyAsync(tryCommitAsync, v), formattedValue));
        builder.SetUpdatesAttributeName("value");
    }

    private async Task CommitAndNotifyAsync(Func<string?, Task<bool>> tryCommitAsync, string? value)
    {
        if (await tryCommitAsync(value))
        {
            NotifyChanged();
        }
    }

    /// <summary>
    /// Parses a DOM-committed string into <typeparamref name="TValue"/> for the
    /// <see cref="AddValueBinding(RenderTreeBuilder, int, string, StringValueParser)"/> overload —
    /// <see langword="false"/> when <paramref name="value"/> cannot become a
    /// <typeparamref name="TValue"/>, in which case the caller leaves the field uncommitted (the
    /// silent-revert contract every kit input shares).
    /// </summary>
    /// <param name="value">The string the DOM committed, exactly as the browser sent it.</param>
    /// <param name="result">The parsed value when parsing succeeds; undefined otherwise.</param>
    protected delegate bool StringValueParser(string? value, out TValue? result);

    /// <summary>
    /// The string-projected value binding that also honours <see cref="UpdateOn"/> — for a
    /// control whose DOM value must round-trip through a culture-invariant string rather than
    /// the culture-sensitive conversion <see cref="AddValueBinding(RenderTreeBuilder, int)"/>
    /// performs. A native <c>&lt;input type="number"&gt;</c> or <c>&lt;input type="date"&gt;</c>
    /// always reports its <c>value</c> in a fixed, period-decimal or ISO <c>yyyy-MM-dd</c> form
    /// regardless of the browser's locale, but that overload's binder resolves
    /// <see cref="System.Globalization.CultureInfo.CurrentCulture"/> when none is supplied —
    /// under a comma-decimal culture it silently misreads <c>"12.5"</c> as <c>125</c> rather than
    /// failing loudly, and under a non-Gregorian calendar culture it can misread a year outright.
    /// This overload exists so a control can supply its own <see cref="StringValueParser"/> doing
    /// invariant, format-exact parsing (<see cref="FormidableInputNumber{TValue}"/> and
    /// <see cref="FormidableInputDate{TValue}"/> are the kit's two cases) while still getting
    /// <see cref="InputUpdateMode.OnInput"/> and the commit/notify split
    /// <see cref="InputUpdateMode.OnBlur"/> needs, exactly as
    /// <see cref="AddValueBinding(RenderTreeBuilder, int)"/> provides them (a string the parser
    /// rejects commits nothing, so it arms no blur-delivered notification) — including the
    /// consumer-splatted <c>onblur</c> chaining, since both overloads share the same
    /// <see cref="HandleBlurAsync"/>. It consumes <paramref name="sequence"/>, and
    /// <paramref name="sequence"/> + 1 whenever it also binds <c>blur</c>, exactly like the typed
    /// overload.
    /// </summary>
    /// <param name="builder">The render tree being built.</param>
    /// <param name="sequence">The first sequence number this call consumes.</param>
    /// <param name="formattedValue">The field's current value, already formatted as a string.</param>
    /// <param name="tryParseValue">Parses a DOM-committed string back into <typeparamref name="TValue"/>.</param>
    protected void AddValueBinding(
        RenderTreeBuilder builder,
        int sequence,
        string? formattedValue,
        StringValueParser tryParseValue)
    {
        if (UpdateOn == InputUpdateMode.OnBlur)
        {
            builder.AddAttribute(
                sequence,
                "onchange",
                EventCallback.Factory.CreateBinder<string?>(this, v => CommitParsedAsync(tryParseValue, v), formattedValue));
            builder.SetUpdatesAttributeName("value");
            AddBlurBinding(builder, sequence + 1);
            return;
        }

        builder.AddAttribute(
            sequence,
            UpdateOn == InputUpdateMode.OnInput ? "oninput" : "onchange",
            EventCallback.Factory.CreateBinder<string?>(this, v => SetCurrentParsedAsync(tryParseValue, v), formattedValue));
        builder.SetUpdatesAttributeName("value");

        if (SyncsDomValueOnBlur)
        {
            AddBlurBinding(builder, sequence + 1);
        }
    }

    /// <summary>
    /// Binds <see cref="HandleBlurAsync"/> as the element's <c>onblur</c> — after the splat, so it
    /// wins the duplicate-attribute race and chains any consumer handler itself.
    /// </summary>
    private void AddBlurBinding(RenderTreeBuilder builder, int sequence) =>
        builder.AddAttribute(
            sequence,
            BlurAttributeName,
            EventCallback.Factory.Create<FocusEventArgs>(this, HandleBlurAsync));

    private Task CommitParsedAsync(StringValueParser tryParseValue, string? value) =>
        tryParseValue(value, out var parsed) ? CommitValueAsync(parsed) : Task.CompletedTask;

    private Task SetCurrentParsedAsync(StringValueParser tryParseValue, string? value) =>
        tryParseValue(value, out var parsed) ? SetCurrentValueAsync(parsed) : Task.CompletedTask;

    /// <summary>
    /// Runs a consumer-splatted <c>onblur</c> handler, then the control's DOM value sync, then —
    /// under <see cref="InputUpdateMode.OnBlur"/>, and only while a value commit has left a
    /// notification pending — notifies the engine, consuming that notification. The chain exists
    /// so that the kit wanting the <c>blur</c> event does not quietly take it away from the
    /// consumer; the mode gate keeps the notification a blur-mode behaviour even for controls
    /// whose sync binds blur in every mode; the commit gate makes blur a delivery rather than a
    /// trigger, so a focus-then-leave with nothing committed notifies nothing, and however many
    /// commits precede a blur, it delivers exactly one notification.
    /// </summary>
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
        FormidableCss.CombineClassNames(AdditionalAttributes, FormidableCss.Compute(state, Context!.Engine.Options.CssClasses));
}
