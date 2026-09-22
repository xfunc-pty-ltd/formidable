using FluentValidation;
using FluentValidation.Results;

namespace Formidable.Tests;

/// <summary>
/// Pins <see cref="IRuleLevelValidator{TModel}"/> on the FluentValidation adapter: enumeration
/// selects exactly what whole-profile execution runs, per-rule execution reproduces the
/// whole-profile verdict issue for issue under the profile it is handed, identities are stable
/// enough to key a store, and <see cref="RuleLevelResult.IsProfileScoped"/> flags exactly the
/// verdicts whose child scope the profile's name list filtered. Every equivalence assertion
/// compares against a real whole-profile run of the same validator instance — the selector
/// semantics are pinned by comparison, not re-derived from documentation.
/// </summary>
public class RuleLevelValidatorTests
{
    private sealed class RuleModel
    {
        public string Notes { get; set; } = string.Empty;
        public string EventName { get; set; } = string.Empty;
        public string Contact { get; set; } = string.Empty;
        public string Website { get; set; } = string.Empty;
        public string Phone { get; set; } = string.Empty;
        public string Reference { get; set; } = string.Empty;
        public List<RuleItem> Items { get; set; } = [];
        public List<RuleSlot> Slots { get; set; } = [];
        public int Start { get; set; }
        public int End { get; set; }
    }

    private sealed class RuleItem
    {
        public string Sku { get; set; } = string.Empty;
    }

    private sealed class RuleSlot
    {
        public string Name { get; set; } = string.Empty;
        public string Special { get; set; } = string.Empty;
    }

    /// <summary>
    /// The Include() target: one untagged rule and one "Engaged"-tagged rule, so an including
    /// validator's per-rule execution must filter the internals by the profile's name list
    /// exactly as a whole-profile run does (Draft runs the website rule alone; an
    /// Engaged-shaped profile runs both; a ruleset-only Engaged profile runs the phone rule
    /// alone).
    /// </summary>
    private sealed class IncludedRuleModelValidator : AbstractValidator<RuleModel>
    {
        public IncludedRuleModelValidator()
        {
            RuleFor(x => x.Website).Must(website => !website.Contains(' '))
                .WithMessage("Website has spaces").WithErrorCode("WEBSITE_BAD");
            RuleSet("Engaged", () =>
                RuleFor(x => x.Phone).NotEmpty()
                    .WithMessage("Phone is required").WithErrorCode("PHONE_REQUIRED"));
        }
    }

    /// <summary>
    /// A child validator with its OWN ruleset axis: the untagged name rule rides the default
    /// bucket, the "Special" rule only runs under a profile naming "Special". Attached via
    /// SetValidator, its rules are filtered by the consuming profile's name list — the shape
    /// whose per-rule verdict is profile-scoped.
    /// </summary>
    private sealed class RuleSlotValidator : AbstractValidator<RuleSlot>
    {
        public RuleSlotValidator()
        {
            RuleFor(s => s.Name).NotEmpty()
                .WithMessage("Slot name is required").WithErrorCode("SLOT_NAME_REQUIRED");
            RuleSet("Special", () =>
                RuleFor(s => s.Special).NotEmpty()
                    .WithMessage("Slot special is required").WithErrorCode("SLOT_SPECIAL"));
        }
    }

    /// <summary>
    /// One rule of every shape the seam must carry, each with its own error code so a failure
    /// attributes to its producing rule in the assertions. The draft bucket holds a format
    /// rule, a <c>RuleForEach…ChildRules</c> collection rule, a cross-field
    /// <c>Must((model, value) => …)</c>, and an <c>Include()</c>. The profile axis holds a
    /// Submit-only presence rule; a dual-membership rule declared once into both "Submit" and
    /// "Engaged" via the raw comma-named <c>RuleSet</c> call (the shape
    /// <c>Profile(name, …)</c> cannot express — it registers the literal string as one name;
    /// the empty <c>Profile("Engaged", …)</c> alongside keeps ruleset-name verification alive
    /// for profiles naming "Engaged"); a rule tagged into the literal "default" ruleset plus
    /// its own name (admitted by any default-including profile); and a "Submit"-scoped
    /// collection rule whose SetValidator child validator carries its own ruleset axis.
    /// </summary>
    private sealed class RuleFixtureValidator : ProfiledValidator<RuleModel>
    {
        protected override void ConfigureCommonRules()
        {
            RuleFor(x => x.Notes).MaximumLength(3)
                .WithMessage("Notes are too long").WithErrorCode("NOTES_LONG");
            RuleForEach(x => x.Items).ChildRules(item =>
                item.RuleFor(i => i.Sku).NotEmpty()
                    .WithMessage("Sku is required").WithErrorCode("SKU_REQUIRED"));
            RuleFor(x => x.End).Must((model, end) => end >= model.Start)
                .WithMessage("End precedes start").WithErrorCode("END_BEFORE_START");
            Include(new IncludedRuleModelValidator());
        }

        protected override void ConfigureProfiles()
        {
            Profile("Submit", () =>
                RuleFor(x => x.EventName).NotEmpty()
                    .WithMessage("Event name is required").WithErrorCode("EVENT_REQUIRED"));
            Profile("Engaged", () => { });
            RuleSet("Submit,Engaged", () =>
                RuleFor(x => x.Contact).NotEmpty()
                    .WithMessage("Contact is required").WithErrorCode("CONTACT_REQUIRED"));
            RuleSet("default,Extra", () =>
                RuleFor(x => x.Reference).Must(reference => reference != "bad-ref")
                    .WithMessage("Reference is bad").WithErrorCode("REFERENCE_BAD"));
            // "Submit" is already registered by the Profile call above; this raw RuleSet block
            // appends the collection rule to the same ruleset.
            RuleSet("Submit", () =>
                RuleForEach(x => x.Slots).SetValidator(new RuleSlotValidator()));
        }
    }

    /// <summary>
    /// Comma-membership in isolation: a dual-membership RuleForEach whose ChildRules children
    /// carry the propagated declaration-scope tags. A plain AbstractValidator so both profiles
    /// of the pair reach it without ruleset registration.
    /// </summary>
    private sealed class EngagedListValidator : AbstractValidator<RuleModel>
    {
        public EngagedListValidator() =>
            RuleSet("Submit,Engaged", () =>
                RuleForEach(x => x.Items).ChildRules(item =>
                    item.RuleFor(i => i.Sku).NotEmpty()
                        .WithMessage("Sku is required").WithErrorCode("SKU_REQUIRED")));
    }

    /// <summary>Untagged plus "Submit"-tagged rules on a plain AbstractValidator, for the wildcard shape.</summary>
    private sealed class PlainRuleValidator : AbstractValidator<RuleModel>
    {
        public PlainRuleValidator()
        {
            RuleFor(x => x.Notes).MaximumLength(3)
                .WithMessage("Notes are too long").WithErrorCode("NOTES_LONG");
            RuleSet("Submit", () =>
                RuleFor(x => x.EventName).NotEmpty()
                    .WithMessage("Event name is required").WithErrorCode("EVENT_REQUIRED"));
        }
    }

    /// <summary>An AbstractValidator whose class-level cascade stops on the first failing rule.</summary>
    private sealed class CascadeStopValidator : AbstractValidator<RuleModel>
    {
        public CascadeStopValidator()
        {
            ClassLevelCascadeMode = CascadeMode.Stop;
            RuleFor(x => x.Notes).NotEmpty();
        }
    }

    /// <summary>An IValidator implemented by hand — no rule enumeration surface at all.</summary>
    private sealed class HandRolledValidator : IValidator<RuleModel>
    {
        public ValidationResult Validate(RuleModel instance) => new();

        public Task<ValidationResult> ValidateAsync(RuleModel instance, CancellationToken cancellation = default) =>
            Task.FromResult(new ValidationResult());

        public ValidationResult Validate(IValidationContext context) => new();

        public Task<ValidationResult> ValidateAsync(IValidationContext context, CancellationToken cancellation = default) =>
            Task.FromResult(new ValidationResult());

        public IValidatorDescriptor CreateDescriptor() => throw new NotSupportedException();

        public bool CanValidateInstancesOfType(Type type) => type == typeof(RuleModel);
    }

    /// <summary>A model every fixture rule fails on — each selected rule produces at least one issue.</summary>
    private static RuleModel EveryRuleFails() => new()
    {
        Notes = "far too long",
        EventName = string.Empty,
        Contact = string.Empty,
        Website = "has spaces",
        Phone = string.Empty,
        Reference = "bad-ref",
        Items = [new RuleItem { Sku = "present" }, new RuleItem { Sku = string.Empty }],
        Slots = [new RuleSlot { Name = string.Empty, Special = string.Empty }],
        Start = 5,
        End = 3,
    };

    private static ValidationProfile ResolveProfile(string name) => name switch
    {
        "Draft" => ValidationProfile.Draft,
        "Submit" => ValidationProfile.Submit,
        // A narrowed live profile's shape: default rules plus the "Engaged" ruleset, reaching
        // the dual rule through its second membership.
        "Engaged" => ValidationProfile.Named("Engaged", includeDefaultRules: true, "Engaged"),
        // Ruleset-only selection: no default bucket at all.
        "EngagedOnly" => ValidationProfile.Named("EngagedOnly", includeDefaultRules: false, "Engaged"),
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    [Theory]
    [InlineData("Draft")]
    [InlineData("Submit")]
    [InlineData("Engaged")]
    [InlineData("EngagedOnly")]
    public async Task Per_rule_execution_reproduces_the_whole_profile_verdict(string profileName)
    {
        var adapter = new FluentValidationModelValidator<RuleModel>(new RuleFixtureValidator());
        var model = EveryRuleFails();
        var profile = ResolveProfile(profileName);

        var whole = await adapter.ValidateAsync(model, profile);

        var perRule = new List<ValidationIssue>();
        foreach (var rule in adapter.SelectRules(profile))
        {
            var result = await adapter.ValidateRuleAsync(model, profile, rule);
            perRule.AddRange(result.Report.Issues);
        }

        // Path, Message, Severity, Code and DisplayName all participate in ValidationIssue's
        // record equality, and comparing the lists pins the order as well: per-rule reports
        // concatenated in SelectRules order ARE the whole-profile report — Include() internals
        // and SetValidator child filtering included, because per-rule execution filters child
        // scope with the same name list the whole-profile selector carries.
        Assert.NotEmpty(perRule);
        Assert.Equal(whole.Issues, perRule);
    }

    [Theory]
    [InlineData("Draft", 5)]
    [InlineData("Submit", 8)]
    [InlineData("Engaged", 6)]
    [InlineData("EngagedOnly", 2)]
    public async Task Selection_matches_execution_for_every_profile_shape(string profileName, int expectedRuleCount)
    {
        var adapter = new FluentValidationModelValidator<RuleModel>(new RuleFixtureValidator());
        var model = EveryRuleFails();
        var profile = ResolveProfile(profileName);

        var selection = adapter.SelectRules(profile);

        // The count check that catches enumeration reading membership differently than
        // execution: every fixture rule fails under this model, so each selected rule must
        // produce issues, and the union of per-rule codes must be exactly the whole-profile
        // run's codes (the Include() rule legitimately carries the codes of every included
        // rule the profile admits).
        Assert.Equal(expectedRuleCount, selection.Count);
        var whole = await adapter.ValidateAsync(model, profile);
        var executedCodes = whole.Issues.Select(issue => issue.Code).Distinct().Order().ToList();

        var perRuleCodes = new HashSet<string?>();
        foreach (var rule in selection)
        {
            var result = await adapter.ValidateRuleAsync(model, profile, rule);
            Assert.NotEmpty(result.Report.Issues);
            foreach (var issue in result.Report.Issues)
            {
                perRuleCodes.Add(issue.Code);
            }
        }

        Assert.Equal(executedCodes, perRuleCodes.Order().ToList());
    }

    [Fact]
    public async Task Child_collection_rules_carry_indexed_paths()
    {
        var adapter = new FluentValidationModelValidator<RuleModel>(new RuleFixtureValidator());
        var model = new RuleModel
        {
            Notes = "ok",
            Items = [new RuleItem { Sku = "present" }, new RuleItem { Sku = string.Empty }],
        };

        // Under Draft only the collection rule can fail here (the presence rules live in
        // rulesets Draft never selects, and every other draft-reachable rule passes on this
        // model), so the whole-profile run's indexed issues are exactly what the collection
        // rule's own report must carry.
        var whole = await adapter.ValidateAsync(model, ValidationProfile.Draft);
        var wholeIndexedIssues = whole.Issues
            .Where(issue => issue.Path.StartsWith("Items[", StringComparison.Ordinal))
            .ToList();
        Assert.NotEmpty(wholeIndexedIssues);
        Assert.All(wholeIndexedIssues, issue => Assert.Equal("Items[1].Sku", issue.Path));

        // Identities are deliberately opaque, so the collection rule is found by what it
        // produces: the one per-rule report with any issues at all.
        var reports = new List<ValidationReport>();
        foreach (var rule in adapter.SelectRules(ValidationProfile.Draft))
        {
            reports.Add((await adapter.ValidateRuleAsync(model, ValidationProfile.Draft, rule)).Report);
        }

        var collectionReport = Assert.Single(reports, report => report.Issues.Count > 0);
        Assert.Equal(wholeIndexedIssues, collectionReport.Issues);
    }

    [Fact]
    public async Task A_wildcard_profile_selects_and_runs_every_rule()
    {
        // ProfiledValidator's ruleset-name verification rejects "*" (it matches no registered
        // ruleset) identically on the whole-profile path and SelectRules, so the wildcard
        // shape is exercised on a plain AbstractValidator, where FluentValidation's selector
        // admits every rule — the untagged one included, even without default rules.
        var adapter = new FluentValidationModelValidator<RuleModel>(new PlainRuleValidator());
        var model = EveryRuleFails();
        var wildcard = ValidationProfile.Named("Everything", includeDefaultRules: false, "*");

        var whole = await adapter.ValidateAsync(model, wildcard);
        var selection = adapter.SelectRules(wildcard);
        Assert.Equal(2, selection.Count);

        var perRule = new List<ValidationIssue>();
        foreach (var rule in selection)
        {
            perRule.AddRange((await adapter.ValidateRuleAsync(model, wildcard, rule)).Report.Issues);
        }

        Assert.Equal(whole.Issues, perRule);
    }

    [Fact]
    public void A_typod_ruleset_name_still_throws()
    {
        var adapter = new FluentValidationModelValidator<RuleModel>(new RuleFixtureValidator());
        var bogus = ValidationProfile.Named("Bogus", includeDefaultRules: true, "Bogus");

        var fromSelection = Assert.Throws<InvalidOperationException>(() => adapter.SelectRules(bogus));
        var fromWholeProfile = Assert.Throws<InvalidOperationException>(() => adapter.Validate(new RuleModel(), bogus));

        // The same loud failure as the whole-profile path: same exception type, same message —
        // skipping ruleset-name verification in SelectRules would silently select nothing.
        Assert.Equal(fromWholeProfile.Message, fromSelection.Message);
        Assert.Contains("Bogus", fromSelection.Message);
        Assert.Contains(nameof(RuleFixtureValidator), fromSelection.Message);
    }

    [Fact]
    public void Identities_are_reference_stable()
    {
        var adapter = new FluentValidationModelValidator<RuleModel>(new RuleFixtureValidator());

        var first = adapter.SelectRules(ValidationProfile.Submit);
        var second = adapter.SelectRules(ValidationProfile.Submit);

        // Two enumerations of one validator agree identity for identity — no fresh wrappers
        // with value inequality — and the identities are distinct from one another.
        Assert.Equal(first, second);
        Assert.Equal(first.Count, first.Distinct().Count());

        // Dictionary-key viability: identities from the second enumeration find entries stored
        // under the first's, through both GetHashCode and Equals.
        var verdicts = first.ToDictionary(rule => rule, _ => 0);
        Assert.All(second, rule => Assert.True(verdicts.ContainsKey(rule)));

        // Validator-instance scoping: identities from a second validator instance never match
        // the first's, even though both instances declare the same rules.
        var other = new FluentValidationModelValidator<RuleModel>(new RuleFixtureValidator());
        Assert.All(other.SelectRules(ValidationProfile.Submit), rule => Assert.DoesNotContain(rule, first));
    }

    [Fact]
    public async Task A_foreign_identity_throws()
    {
        var adapter = new FluentValidationModelValidator<RuleModel>(new RuleFixtureValidator());
        var other = new FluentValidationModelValidator<RuleModel>(new RuleFixtureValidator());
        var model = EveryRuleFails();
        var foreign = other.SelectRules(ValidationProfile.Submit)[0];

        // A foreign identity would otherwise select nothing and return an empty report
        // indistinguishable from "rule passed" — the silent under-validation the guard exists
        // to prevent. A default identity carries no rule at all and fails the same way.
        await Assert.ThrowsAsync<ArgumentException>(
            () => adapter.ValidateRuleAsync(model, ValidationProfile.Submit, foreign));
        await Assert.ThrowsAsync<ArgumentException>(
            () => adapter.ValidateRuleAsync(model, ValidationProfile.Submit, default));
    }

    [Fact]
    public async Task Capability_reflects_the_wrapped_validator_shape()
    {
        // The tester beside the doers: an AbstractValidator with the default class-level
        // cascade answers true; both false shapes throw from the doers when the signal is
        // ignored, never silently under-validate.
        var capable = new FluentValidationModelValidator<RuleModel>(new RuleFixtureValidator());
        Assert.True(capable.CanValidateByRule);

        var handRolled = new FluentValidationModelValidator<RuleModel>(new HandRolledValidator());
        Assert.False(handRolled.CanValidateByRule);
        var fromHandRolled = Assert.Throws<NotSupportedException>(() => handRolled.SelectRules(ValidationProfile.Draft));
        Assert.Contains(nameof(HandRolledValidator), fromHandRolled.Message);
        await Assert.ThrowsAsync<NotSupportedException>(
            () => handRolled.ValidateRuleAsync(new RuleModel(), ValidationProfile.Draft, default));

        // A class-level cascade stop lets a failing rule suppress later rules within one
        // whole-profile pass; separate per-rule executions cannot reproduce that, so the
        // capability is off rather than quietly divergent.
        var cascadeStop = new FluentValidationModelValidator<RuleModel>(new CascadeStopValidator());
        Assert.False(cascadeStop.CanValidateByRule);
        var fromCascadeStop = Assert.Throws<NotSupportedException>(() => cascadeStop.SelectRules(ValidationProfile.Draft));
        Assert.Contains("ClassLevelCascadeMode", fromCascadeStop.Message);
    }

    [Fact]
    public async Task Profile_scope_detection_flags_only_cross_scope_child_shapes()
    {
        var adapter = new FluentValidationModelValidator<RuleModel>(new RuleFixtureValidator());
        var model = EveryRuleFails();

        var scopedByCode = new Dictionary<string, bool>();
        foreach (var rule in adapter.SelectRules(ValidationProfile.Submit))
        {
            var result = await adapter.ValidateRuleAsync(model, ValidationProfile.Submit, rule);
            Assert.NotEmpty(result.Report.Issues);
            scopedByCode[result.Report.Issues[0].Code!] = result.IsProfileScoped;
        }

        // Plain rules, an untagged ChildRules collection, and dual-membership rules are
        // profile-independent: every profile that selects them necessarily admits their whole
        // child scope, so the verdict is reusable across profiles at the same model state.
        Assert.False(scopedByCode["NOTES_LONG"]);
        Assert.False(scopedByCode["SKU_REQUIRED"]);
        Assert.False(scopedByCode["END_BEFORE_START"]);
        Assert.False(scopedByCode["EVENT_REQUIRED"]);
        Assert.False(scopedByCode["CONTACT_REQUIRED"]);
        Assert.False(scopedByCode["REFERENCE_BAD"]);

        // Include() internals and a SetValidator child validator with its own ruleset axis are
        // filtered by the profile's name list, so those verdicts answer only for the profile
        // they ran under.
        Assert.True(scopedByCode["WEBSITE_BAD"]);
        Assert.True(scopedByCode["SLOT_NAME_REQUIRED"]);
    }

    [Fact]
    public async Task A_propagated_tag_child_rule_stays_profile_independent()
    {
        // Comma-membership at its hardest: ChildRules children carry the propagated
        // declaration-scope tags, so any profile selecting the rule admits them through those
        // same tags — the verdict is reusable across every profile that reaches the rule, which
        // is what the per-rule store trades on. On the default profiles the pair sharing a
        // verdict is a live pass and the refresh behind it, both selecting the submit profile;
        // a dual-membership rule is the case where the two profiles differ by name and the
        // verdict still travels.
        var adapter = new FluentValidationModelValidator<RuleModel>(new EngagedListValidator());
        var model = new RuleModel
        {
            Items = [new RuleItem { Sku = "present" }, new RuleItem { Sku = string.Empty }],
        };
        var engaged = ResolveProfile("Engaged");

        var submitIdentity = Assert.Single(adapter.SelectRules(ValidationProfile.Submit));
        var engagedIdentity = Assert.Single(adapter.SelectRules(engaged));
        Assert.Equal(submitIdentity, engagedIdentity);

        var underSubmit = await adapter.ValidateRuleAsync(model, ValidationProfile.Submit, submitIdentity);
        var underEngaged = await adapter.ValidateRuleAsync(model, engaged, engagedIdentity);

        // The children genuinely ran (indexed path present) and neither execution was scoped
        // to its profile — both produce the same verdict.
        Assert.Contains(underSubmit.Report.Issues, issue => issue.Path == "Items[1].Sku");
        Assert.False(underSubmit.IsProfileScoped);
        Assert.False(underEngaged.IsProfileScoped);
        Assert.Equal(underSubmit.Report.Issues, underEngaged.Report.Issues);
    }
}
