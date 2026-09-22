using FluentValidation;
using Formidable.Blazor.Tests.Fixtures;
using Formidable.Introspection;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.Time.Testing;

namespace Formidable.Blazor.Tests;

/// <summary>
/// The draft-load API: what a form says about values it was handed rather than typed. Three
/// outcomes, decided by whether the field HOLDS a value and by what a whole-model
/// submit-profile answer then says about it — a field holding a value the rules pass is
/// confirmed, a field holding one they fail discloses it, and a field holding nothing stays
/// silent — and the fields it decides for are ENGAGED, not merely touched, because engagement
/// is what makes the live channel speak.
/// </summary>
/// <remarks>
/// The class pins read the same two surfaces the shipped seams read —
/// <c>EditContext.FieldCssClass</c> for a native input, <c>FormidableCss.Compute</c> over
/// <c>GetFieldState</c> for a kit input — and assert them equal, because this is one decision
/// both seams share rather than two that happen to agree.
/// </remarks>
public class FormidableEngineDraftLoadTests
{
    private const string SavedTitle = "Quarterly plan";
    private const string SavedBadEmail = "ada.lovelace";

    private static FormidableEngine<LoadedDraft> Engine(
        LoadedDraft draft,
        EditContext editContext,
        IModelValidator<LoadedDraft>? validator = null) =>
        new(
            draft,
            editContext,
            validator ?? new FluentValidationModelValidator<LoadedDraft>(new LoadedDraftValidator()),
            new ReflectionModelIntrospector(),
            new FormidableOptions(),
            new FakeTimeProvider());

    private static string KitClass<TModel>(FormidableEngine<TModel> engine, FieldIdentifier field)
        where TModel : class =>
        FormidableCss.Compute(engine.GetFieldState(field), engine.Options.CssClasses);

    /// <summary>
    /// Asserts both shipped seams agree on <paramref name="field"/>'s class, and returns what
    /// they agree on.
    /// </summary>
    private static string BothSeams<TModel>(
        FormidableEngine<TModel> engine, EditContext editContext, FieldIdentifier field)
        where TModel : class
    {
        var native = editContext.FieldCssClass(field);
        Assert.Equal(native, KitClass(engine, field));
        return native;
    }

    /// <summary>A draft saved with one good value, one field never filled in, one wrong value.</summary>
    private static LoadedDraft SavedDraft() =>
        new() { Title = SavedTitle, Summary = string.Empty, ContactEmail = SavedBadEmail };

    // The three-way outcome whole, on both shipped seams. Mutation this breaks: mark the fields
    // touched instead of engaging them. Touched alone still paints Title green - the vouch reads
    // coverage, not engagement - so the two ends of the row survive it and only the middle claim
    // fails: the live view is the filed verdicts read THROUGH the engaged set, so a
    // merely-touched ContactEmail files no verdict anyone can read and goes SILENT among the
    // green instead of red.
    [Fact]
    public async Task A_loaded_draft_confirms_the_good_value_discloses_the_wrong_one_and_stays_quiet_about_the_unfilled_one()
    {
        var draft = SavedDraft();
        var editContext = new EditContext(draft);
        using var engine = Engine(draft, editContext);

        var title = new FieldIdentifier(draft, nameof(LoadedDraft.Title));
        var summary = new FieldIdentifier(draft, nameof(LoadedDraft.Summary));
        var email = new FieldIdentifier(draft, nameof(LoadedDraft.ContactEmail));

        await engine.DiscloseLoadedValuesAsync();

        Assert.Equal("formidable-valid", BothSeams(engine, editContext, title));
        Assert.Equal(string.Empty, BothSeams(engine, editContext, summary));
        Assert.Equal("formidable-invalid", BothSeams(engine, editContext, email));

        // Red with its message, not a bare border: the point of engaging is that the reason
        // shows.
        Assert.Equal(
            ["That is not a valid email address"],
            engine.GetIssues(email).Select(i => i.Message));
        Assert.Empty(engine.GetIssues(summary));
        Assert.Empty(engine.GetIssues(title));

        // A load is not a submit, and nothing about it may make the form look submitted.
        Assert.False(engine.HasSubmitted);
    }

    // Mutation this breaks: classify from the rules the field carries rather than from the
    // failing code - ContactEmail HAS a presence rule, so a descriptor-driven reading calls its
    // failure an empty field and leaves it silent, which is the case a reader most needs to see.
    [Fact]
    public async Task A_presence_ruled_field_failing_a_different_rule_discloses_that_rule()
    {
        var draft = SavedDraft();
        var editContext = new EditContext(draft);
        using var engine = Engine(draft, editContext);
        var email = new FieldIdentifier(draft, nameof(LoadedDraft.ContactEmail));

        await engine.DiscloseLoadedValuesAsync();

        Assert.Equal("formidable-invalid", BothSeams(engine, editContext, email));
        var messages = engine.GetIssues(email).Select(i => i.Message).ToList();
        Assert.Contains("That is not a valid email address", messages);
        Assert.DoesNotContain("A contact email is required", messages);
    }

    // An advisory does not make an unfilled field speak. Summary is blank AND carries an
    // unconditional info advisory, so the field has something to say and nobody to say it about.
    // Mutation this breaks: adopt a field because it has issues rather than because it holds a
    // value - the presence error rides in on the same verdict and paints an unfilled field RED.
    [Fact]
    public async Task An_advisory_on_an_unfilled_field_does_not_paint_it_red()
    {
        var draft = SavedDraft();
        var editContext = new EditContext(draft);
        using var engine = Engine(draft, editContext);
        var summary = new FieldIdentifier(draft, nameof(LoadedDraft.Summary));

        await engine.DiscloseLoadedValuesAsync();

        Assert.Equal(string.Empty, BothSeams(engine, editContext, summary));
        Assert.Empty(engine.GetIssues(summary));
    }

    // Mutation this breaks: running any part of the feature without being asked. A form
    // that never calls it validates nothing, paints nothing and says nothing - which is what
    // "additive" has to mean for a library that has already shipped this behaviour.
    [Fact]
    public void A_form_that_never_calls_it_is_left_exactly_as_it_was()
    {
        var draft = SavedDraft();
        var editContext = new EditContext(draft);
        var counting = new CountingValidator<LoadedDraft>(
            new FluentValidationModelValidator<LoadedDraft>(new LoadedDraftValidator()));
        using var engine = Engine(draft, editContext, counting);

        var title = new FieldIdentifier(draft, nameof(LoadedDraft.Title));
        var summary = new FieldIdentifier(draft, nameof(LoadedDraft.Summary));
        var email = new FieldIdentifier(draft, nameof(LoadedDraft.ContactEmail));

        Assert.Equal(0, counting.CallCount);
        Assert.Equal(string.Empty, BothSeams(engine, editContext, title));
        Assert.Equal(string.Empty, BothSeams(engine, editContext, summary));
        Assert.Equal(string.Empty, BothSeams(engine, editContext, email));
        Assert.Empty(engine.GetVisibleIssues());
        Assert.False(engine.HasSubmitted);
    }

    // A row nobody filled in is as silent as a top-level field nobody filled in - the rule that
    // decides it is declared inside a ChildRules block and its failure carries an indexed path,
    // and neither of those changes the answer, because the answer is the row's own value.
    // Mutation this breaks: adopt a field because a rule declares it rather than because it
    // holds a value, and every unfilled row of every collection turns red the instant a draft
    // loads.
    [Fact]
    public async Task A_blank_collection_row_stays_silent()
    {
        var order = new EngineOrder
        {
            Description = "ok",
            Customer = new EngineCustomer(),
            Items = { new EngineItem { Sku = string.Empty } },
        };
        var editContext = new EditContext(order);
        using var engine = new FormidableEngine<EngineOrder>(
            order,
            editContext,
            new FluentValidationModelValidator<EngineOrder>(new EngineOrderValidator()),
            new ReflectionModelIntrospector(),
            new FormidableOptions(),
            new FakeTimeProvider());

        var sku = new FieldIdentifier(order.Items[0], nameof(EngineItem.Sku));
        var description = new FieldIdentifier(order, nameof(EngineOrder.Description));

        await engine.DiscloseLoadedValuesAsync();

        Assert.Equal(string.Empty, BothSeams(engine, editContext, sku));
        Assert.Empty(engine.GetIssues(sku));

        // The fields that DO hold a value are decided normally in the same call, so the silence
        // above is about the row being blank rather than about the call having done nothing.
        Assert.Equal("formidable-valid", BothSeams(engine, editContext, description));
    }

    private static FormidableEngine<LoadedRoster> RosterEngine(LoadedRoster roster, EditContext editContext) =>
        new(
            roster,
            editContext,
            new FluentValidationModelValidator<LoadedRoster>(new LoadedRosterValidator()),
            new ReflectionModelIntrospector(),
            new FormidableOptions(),
            new FakeTimeProvider());

    /// <summary>A saved roster with one good row, one wrong row, and one blank row.</summary>
    private static LoadedRoster SavedRoster() =>
        new()
        {
            Owner = "Ada",
            Rows =
            {
                new LoadedRow { Code = "R-1" },
                new LoadedRow { Code = "nope" },
                new LoadedRow { Code = string.Empty },
            },
        };

    // Outcome 1 of three: a field nobody has filled in stays silent, whatever KIND of rule its
    // emptiness fails. Presence written as a predicate is the shape that makes this a decision
    // rather than an accident - the rule is indistinguishable from a range check by inspection,
    // and reading the VALUE settles it without asking what the rule was. Mutation this breaks:
    // adopt a field the rules reach without asking whether it holds a value, which is what
    // leaves the classification to the failing rule's kind.
    [Fact]
    public async Task An_empty_field_whose_presence_is_a_predicate_stays_silent()
    {
        var draft = new LoadedDraft();
        var editContext = new EditContext(draft);
        using var engine = Engine(
            draft,
            editContext,
            new FluentValidationModelValidator<LoadedDraft>(new PredicatePresenceDraftValidator()));
        var title = new FieldIdentifier(draft, nameof(LoadedDraft.Title));

        await engine.DiscloseLoadedValuesAsync();

        Assert.Equal(string.Empty, BothSeams(engine, editContext, title));
        Assert.Empty(engine.GetIssues(title));
    }

    // Outcome 2 of three: a collection row the save filled in correctly is confirmed. Its path
    // carries an index, and the rule that judges it is declared inside a child validator, so the
    // only enumeration that can reach it is the validator's own declared shape walked through
    // that child and expanded against the rows the model actually holds. Mutation this breaks:
    // enumerate the green set from paths the validator declares for its OWN members only, and
    // every row goes back to saying nothing.
    [Fact]
    public async Task A_saved_collection_row_is_confirmed()
    {
        var roster = SavedRoster();
        var editContext = new EditContext(roster);
        using var engine = RosterEngine(roster, editContext);
        var good = new FieldIdentifier(roster.Rows[0], nameof(LoadedRow.Code));

        await engine.DiscloseLoadedValuesAsync();

        Assert.Equal("formidable-valid", BothSeams(engine, editContext, good));
        Assert.Empty(engine.GetIssues(good));
    }

    // Outcome 3 of three, and the one a bad saved value most needs: a row holding a value that
    // is present and wrong says so, rather than hiding among the confirmed rows. The blank row
    // beside it stays silent in the same call, so the pin discriminates "reads the row's value"
    // from "speaks about every row".
    //
    // Mutations this breaks, one per half. The blank row's half falls to dropping the emptiness
    // gate on its own - the blank row turns red, while the row above holding a good value goes
    // on painting green, because an adopted field still paints only what its own issues earn.
    // The wrong row's half needs BOTH of the routes that reach it removed together: its own
    // failure puts it in the universe whether or not any template is expanded, and its declared
    // template expands to it whether or not the failures are read, so removing either one alone
    // leaves it disclosed. That is why the confirmed row above, which has only the second route,
    // falls to the template mutation this one survives.
    [Fact]
    public async Task A_collection_row_holding_a_wrong_value_discloses_it()
    {
        var roster = SavedRoster();
        var editContext = new EditContext(roster);
        using var engine = RosterEngine(roster, editContext);
        var wrong = new FieldIdentifier(roster.Rows[1], nameof(LoadedRow.Code));
        var blank = new FieldIdentifier(roster.Rows[2], nameof(LoadedRow.Code));

        await engine.DiscloseLoadedValuesAsync();

        Assert.Equal("formidable-invalid", BothSeams(engine, editContext, wrong));
        Assert.Equal(["Row codes start with R-"], engine.GetIssues(wrong).Select(i => i.Message));

        Assert.Equal(string.Empty, BothSeams(engine, editContext, blank));
        Assert.Empty(engine.GetIssues(blank));
    }

    // Whatever mix of rules an empty field fails, it stays silent - a presence rule and a
    // predicate together under the default Continue cascade (both fail), the same pair under
    // rule-level Stop (only the presence rule fails), and a plain presence rule on its own.
    // Mutation this breaks: read the failing rules instead of the value. The first row then goes
    // red, because a predicate is indistinguishable from a range check by inspection.
    [Theory]
    [InlineData(nameof(PresenceAlongsidePredicateValidator))]
    [InlineData(nameof(StoppingPresenceValidator))]
    [InlineData(nameof(LoadedDraftValidator))]
    public async Task An_empty_field_is_silent_whatever_mix_of_rules_it_fails(string shape)
    {
        IValidator<LoadedDraft> validator = shape switch
        {
            nameof(PresenceAlongsidePredicateValidator) => new PresenceAlongsidePredicateValidator(),
            nameof(StoppingPresenceValidator) => new StoppingPresenceValidator(),
            _ => new LoadedDraftValidator(),
        };

        var draft = new LoadedDraft();
        var editContext = new EditContext(draft);
        using var engine = Engine(
            draft, editContext, new FluentValidationModelValidator<LoadedDraft>(validator));
        var title = new FieldIdentifier(draft, nameof(LoadedDraft.Title));

        await engine.DiscloseLoadedValuesAsync();

        Assert.Equal(string.Empty, BothSeams(engine, editContext, title));
    }

    // The other side of the same reading, and the control that keeps the silence above from
    // being silence about everything: a field HOLDING a value that fails discloses it, however
    // little its failing code says about what kind of rule produced it - here a presence rule
    // and a length rule share one WithErrorCode, so the code identifies neither. Mutation this
    // breaks: gate disclosure on being able to name the failing rule's kind.
    [Fact]
    public async Task A_field_holding_a_value_discloses_whatever_rule_it_fails()
    {
        var tooLong = new LoadedDraft { Title = "far too long" };
        var tooLongContext = new EditContext(tooLong);
        using var tooLongEngine = Engine(
            tooLong,
            tooLongContext,
            new FluentValidationModelValidator<LoadedDraft>(new AmbiguousCodeDraftValidator()));

        await tooLongEngine.DiscloseLoadedValuesAsync();

        Assert.Equal(
            "formidable-invalid",
            BothSeams(tooLongEngine, tooLongContext, new FieldIdentifier(tooLong, nameof(LoadedDraft.Title))));

        // The same validator over a field nobody filled in: the codes are exactly as unhelpful,
        // and the value settles it anyway.
        var unfilled = new LoadedDraft();
        var unfilledContext = new EditContext(unfilled);
        using var unfilledEngine = Engine(
            unfilled,
            unfilledContext,
            new FluentValidationModelValidator<LoadedDraft>(new AmbiguousCodeDraftValidator()));

        await unfilledEngine.DiscloseLoadedValuesAsync();

        Assert.Equal(
            string.Empty,
            BothSeams(unfilledEngine, unfilledContext, new FieldIdentifier(unfilled, nameof(LoadedDraft.Title))));
    }

    // Emptiness is read against the type the member DECLARES, which is what FluentValidation's
    // own NotEmpty() does - so a nullable holding its underlying type's default is an answer
    // while a non-nullable holding its own default is not. The two silent fields here are
    // exactly the two the validator itself fails; the two confirmed ones are exactly the two it
    // passes. Mutation this breaks: read emptiness off the boxed value instead, and a saved 0
    // and a saved false stop counting as answers.
    [Fact]
    public async Task Emptiness_is_read_against_the_declared_type()
    {
        var draft = new TypedDraft { Quantity = 0, Adjustment = 0, Accepted = false, Subscribed = false };
        var editContext = new EditContext(draft);
        using var engine = new FormidableEngine<TypedDraft>(
            draft,
            editContext,
            new FluentValidationModelValidator<TypedDraft>(new TypedDraftValidator()),
            new ReflectionModelIntrospector(),
            new FormidableOptions(),
            new FakeTimeProvider());

        await engine.DiscloseLoadedValuesAsync();

        Assert.Equal(
            string.Empty,
            BothSeams(engine, editContext, new FieldIdentifier(draft, nameof(TypedDraft.Quantity))));
        Assert.Equal(
            string.Empty,
            BothSeams(engine, editContext, new FieldIdentifier(draft, nameof(TypedDraft.Accepted))));
        Assert.Equal(
            "formidable-valid",
            BothSeams(engine, editContext, new FieldIdentifier(draft, nameof(TypedDraft.Adjustment))));
        Assert.Equal(
            "formidable-valid",
            BothSeams(engine, editContext, new FieldIdentifier(draft, nameof(TypedDraft.Subscribed))));
    }

    // The two halves of the classification need different things, and this pins the split: a
    // validator that cannot be inspected still discloses a value the rules fail, because
    // deciding that needs only the model - while it vouches for nothing, because the list of
    // fields a form speaks about has no source but the validator. Mutation this breaks: gate the
    // whole classification on the inspection capability, and a bad saved value goes silent on
    // every hand-rolled validator.
    [Fact]
    public async Task A_validator_that_cannot_be_inspected_discloses_a_wrong_value_and_vouches_for_nothing()
    {
        var draft = SavedDraft();
        var editContext = new EditContext(draft);
        using var engine = Engine(
            draft,
            editContext,
            new CapabilityHidingModelValidator<LoadedDraft>(
                new FluentValidationModelValidator<LoadedDraft>(new LoadedDraftValidator())));

        var title = new FieldIdentifier(draft, nameof(LoadedDraft.Title));
        var summary = new FieldIdentifier(draft, nameof(LoadedDraft.Summary));
        var email = new FieldIdentifier(draft, nameof(LoadedDraft.ContactEmail));

        await engine.DiscloseLoadedValuesAsync();

        Assert.Equal(string.Empty, BothSeams(engine, editContext, title));
        Assert.Equal(string.Empty, BothSeams(engine, editContext, summary));
        Assert.Equal("formidable-invalid", BothSeams(engine, editContext, email));
        Assert.Equal(
            ["That is not a valid email address"],
            engine.GetIssues(email).Select(i => i.Message));
    }

    // The premise, pinned: the values arrive with no field-changed notification anywhere, so
    // whatever the verdict store already holds describes a model that is no longer there. A
    // first render arms a reconciling refresh, and on a real page that refresh has usually
    // landed before the visitor clicks anything - so the store is populated, at an edit stamp
    // nothing has moved. Mutation this breaks: leave the edit stamp alone. Every selected rule
    // then reads as fresh, the pass executes nothing, and the whole report describes the empty
    // model the refresh answered for - three presence failures, nothing adopted, and a form
    // that says nothing at all about the values it is now showing.
    [Fact]
    public async Task A_load_answers_for_the_values_now_in_the_model_not_for_stored_verdicts()
    {
        var draft = new LoadedDraft();
        var editContext = new EditContext(draft);
        var time = new FakeTimeProvider();
        using var engine = new FormidableEngine<LoadedDraft>(
            draft,
            editContext,
            new FluentValidationModelValidator<LoadedDraft>(new LoadedDraftValidator()),
            new ReflectionModelIntrospector(),
            new FormidableOptions(),
            time);

        // The reconciling refresh a first render arms, answering for the empty model and filing
        // a verdict for every rule the submit profile selects.
        engine.OnRenderedFieldsChanged();
        time.Advance(TimeSpan.FromMilliseconds(301));
        await Task.Yield();

        // The draft arrives. Writing model properties notifies nothing.
        draft.Title = SavedTitle;
        draft.ContactEmail = SavedBadEmail;

        await engine.DiscloseLoadedValuesAsync();

        Assert.Equal(
            "formidable-valid",
            BothSeams(engine, editContext, new FieldIdentifier(draft, nameof(LoadedDraft.Title))));
        Assert.Equal(
            ["That is not a valid email address"],
            engine.GetIssues(new FieldIdentifier(draft, nameof(LoadedDraft.ContactEmail)))
                .Select(i => i.Message));
    }

    // A load answers for every field the engine is watching, not only the ones it adopts. It can
    // adopt nothing at all - a save that leaves every readable field merely unfilled does exactly
    // that - while replacing a value the visitor had already engaged, and the field's filed
    // verdict has to move with the model. Mutation this breaks: run the live pass only when
    // something was adopted. Title then keeps the empty verdict its own edit filed, and a
    // required field the load emptied says nothing at all.
    [Fact]
    public async Task A_load_re_answers_a_field_the_visitor_had_already_engaged()
    {
        var draft = new LoadedDraft
        {
            Title = SavedTitle,
            Summary = "there",
            ContactEmail = "ada@example.com",
        };
        var editContext = new EditContext(draft);
        using var engine = Engine(draft, editContext);
        var title = new FieldIdentifier(draft, nameof(LoadedDraft.Title));

        // The visitor engages Title with a value nothing objects to.
        editContext.NotifyFieldChanged(title);
        await Task.Yield();
        Assert.Empty(engine.GetIssues(title));

        // The save that arrives leaves every field blank, so every readable failure is a presence
        // failure and the load adopts nothing.
        draft.Title = string.Empty;
        draft.Summary = string.Empty;
        draft.ContactEmail = string.Empty;

        await engine.DiscloseLoadedValuesAsync();

        Assert.Equal(["Title is required"], engine.GetIssues(title).Select(i => i.Message));
        Assert.Equal("formidable-invalid", BothSeams(engine, editContext, title));
    }

    // Engagement is what the API grants, and engagement outlives the call: a later edit ELSEWHERE
    // re-answers the loaded field, so a value that goes wrong because of another field's change
    // discloses without a submit. Mutation this breaks: engaging for the duration of the load
    // pass alone - one edit to another field and the loaded field falls silent again.
    [Fact]
    public async Task A_loaded_field_keeps_answering_after_the_load()
    {
        var draft = new LoadedDraft { Title = SavedTitle, Summary = "there", ContactEmail = "ada@example.com" };
        var editContext = new EditContext(draft);
        using var engine = Engine(draft, editContext);
        var title = new FieldIdentifier(draft, nameof(LoadedDraft.Title));
        var summary = new FieldIdentifier(draft, nameof(LoadedDraft.Summary));

        await engine.DiscloseLoadedValuesAsync();
        Assert.Equal("formidable-valid", BothSeams(engine, editContext, title));

        // Someone empties the loaded title through the model and notifies for the OTHER field.
        draft.Title = string.Empty;
        editContext.NotifyFieldChanged(summary);
        await Task.Yield();

        Assert.Equal("formidable-invalid", BothSeams(engine, editContext, title));
        Assert.Equal(["Title is required"], engine.GetIssues(title).Select(i => i.Message));
    }

    /// <summary>
    /// A render dispatch that records how deeply dispatches nest, standing in for the engine's
    /// own <c>renderDispatch</c>. Every delegate the engine hands it completes inside the
    /// <c>await</c> below, so the depth is read the way it was written and never sampled across
    /// two dispatches that overlap.
    /// </summary>
    private sealed class NestingDispatch
    {
        private int _depth;

        public int MaxDepth { get; private set; }

        public async Task Invoke(Func<Task> work)
        {
            _depth++;
            MaxDepth = Math.Max(MaxDepth, _depth);
            try
            {
                await work();
            }
            finally
            {
                _depth--;
            }
        }
    }

    // The live pass a load starts is started THROUGH the render dispatch rather than on whatever
    // thread the load pass completed on. Its prologue reads the engaged set and calls BeginPass,
    // which moves the pass bookkeeping every other pass start takes on the dispatcher alone - and
    // which a field change arriving on the dispatcher is otherwise free to race on a host that
    // has one. Nesting is what makes that observable without a second thread: the load pass runs
    // outside any dispatch, so its own dispatches sit at depth one, and a live pass started the
    // same way could only reach depth one too. Depth two says the live pass asked for its
    // dispatches from inside one. Mutation this breaks: await the live pass directly on the
    // completing thread instead of marshalling it.
    [Fact]
    public async Task The_live_pass_a_load_starts_is_marshalled_through_the_render_dispatch()
    {
        var draft = SavedDraft();
        var editContext = new EditContext(draft);
        var dispatch = new NestingDispatch();
        using var engine = new FormidableEngine<LoadedDraft>(
            draft,
            editContext,
            new FluentValidationModelValidator<LoadedDraft>(new LoadedDraftValidator()),
            new ReflectionModelIntrospector(),
            new FormidableOptions(),
            new FakeTimeProvider(),
            renderDispatch: dispatch.Invoke);

        await engine.DiscloseLoadedValuesAsync();

        // The load engaged a field and the live pass really did run, without which the depth
        // below would be measuring a pass that never started.
        Assert.Equal(
            "formidable-invalid",
            BothSeams(engine, editContext, new FieldIdentifier(draft, nameof(LoadedDraft.ContactEmail))));
        Assert.Equal(2, dispatch.MaxDepth);
    }
}
