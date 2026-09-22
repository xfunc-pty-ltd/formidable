# Sample walkthrough

The sample app is Formidable's runnable tour, one page per feature, and the Playwright suite in
`tests/Formidable.Sample.E2E` drives it end to end in a real browser (`FORMIDABLE_E2E=1 dotnet
test` — see [Releasing](../docs/releasing.md)). That suite covers *behaviour*: which
messages appear, where focus lands, what the server sends back.

This checklist covers what a headless browser cannot judge — colour, contrast, spacing, focus
cues, and the chrome the browser paints for native controls. Walk it in **both light and dark OS
colour schemes**: several checks exist only because a theme flips something.

Start both servers from the repo root:

```bash
dotnet run --project samples/Formidable.Sample.Api
dotnet run --project samples/Formidable.Sample
```

Then open <http://localhost:5181>. Sections follow the sidebar's grouping.

## Every page

- [ ] Teaching panel present: the rules, numbered *Try it* steps, and a working *Show the code*
      accordion containing the page's real source
- [ ] Buttons sit in a spaced actions row; nothing touches a message
- [ ] Valid submit produces a status line with breathing room below the buttons
- [ ] A blocked submit focuses the first error IN DOCUMENT ORDER on the page — the field that
      sits highest visually, not the field whose rule was declared first in the validator — with
      no click needed (default `FormidableForm.FocusFirstErrorOnInvalidSubmit`; Scroll & focus is
      the one page that lets you turn it off)
- [ ] A blocked submit's summary slides open over roughly 0.2 s as its issues appear, rather than
      snapping into place; with the OS's reduce-motion setting on, it appears instantly instead
- [ ] Hovering a summary row tints only the message text, a pill sized to hug it — never a
      full-width bar across the row

## Navigation

- [ ] Sidebar shows seven groups in order: Start here, Core concepts, Fields &
      collections, Async & server, Presentation, Model & data, Workout
- [ ] All nineteen links route to a live page; the active link is highlighted
- [ ] Group headings are legible (small caps, muted) in BOTH light and dark mode
- [ ] Inspect a group heading and the list under it (devtools or a screen reader): the heading
      carries an id and the list's `aria-labelledby` names it, so the group reads as one unit
      to assistive technology, not as an unrelated heading floating above a plain list

---

## Start here

### Quickstart

- [ ] Empty submit: ONE message per field ("Name is required" / "Email is required")
- [ ] `not-an-email` + blur: message becomes "A valid email is required"
- [ ] Summary click focuses; valid submit thanks by name
- [ ] **Slide on submit:** submit the empty form and watch Name's message arrive — it slides
      open over roughly 0.2 s and the Email field below it eases down with it, rather than
      jumping straight into its new position; with the OS's reduce-motion setting on, it appears
      instantly instead
- [ ] **Slide on clear:** type a name and tab away — Name's message eases CLOSED over roughly
      0.2 s and the Email field eases back up with it, the same motion in reverse rather than the
      message snapping out of existence; with reduce-motion on, it disappears instantly instead

---

## Core concepts

### Draft vs Submit

- [ ] Each label's text is followed by a red asterisk — `Title *` and `Summary *` — in light and
      dark alike, and nothing on the page declares one: the mark is read from the validator's
      submit bucket
- [ ] Panel states the 60-char Title rule; 61 chars shows it live
- [ ] Save draft: blocked by format only. Shorten the title and save again: it saves with
      Summary still empty
- [ ] Clear Title and leave the field: "Title is required to submit" lands with NO submit
      anywhere — while Summary, failing its own required rule and never touched, stays silent
- [ ] Submit: Summary speaks for the first time; the error summary now carries both
- [ ] Valid submit: status line confirms
- [ ] Type into both fields, make Title 61 characters and leave the field so the format error
      is showing, then click *Reset*: the error clears and the form returns to pristine — but
      the typed values in both boxes STAY exactly as typed, over-long title included;
      `ResetAsync()` never writes model properties, and it is the SAME form, no page reload

### Custom profiles

- [ ] With *Standard submit* selected: Title + Slug + Category + Read minutes + Publish date
      filled, Review note empty — submit goes through
- [ ] Switch to *Admin review*: the form RESETS. BEFORE submitting anything, type into Review
      note and Tab out, then come back, clear it and Tab out again — "A review note is required
      for admin review" appears on a form that has never been submitted, so only the live channel
      can have put it there, following the profile the picker installed
- [ ] Switch back to *Standard submit* — the form RESETS again — and repeat those two edits on
      Review note: nothing appears at all, since that profile selects no rule for the field.
      Switch to *Admin review* once more before the next row
- [ ] Re-enter all five of the others (the reset emptied Title, Slug, Category, Read minutes and
      Publish date) and submit — blocked, the review note is required here
- [ ] Fill Review note and submit again: goes through under admin review
- [ ] Category is a select (`FormidableInputSelect`, `UpdateOn="OnBlur"`): submit with it empty,
      then pick a category — the "required" message stays exactly as it was until you tab away,
      then clears
- [ ] Read minutes is a number input (`FormidableInputNumber`): typing `0` and submitting shows
      the range message; the native spinner chrome matches the theme in both light and dark
- [ ] Typing `e3` into Read minutes and tabbing away clears the box — text the model never
      accepted does not linger, and a submit's "read time is required" message sits over a box
      that visibly agrees with it
- [ ] Publish date is a date input (`FormidableInputDate`, `UpdateOn="OnBlur"`): typing a date and
      tabbing away commits it with no stray validation flash mid-type; calendar picker chrome is
      legible in dark mode

### Progressive disclosure

- [ ] *Try it* step 1 works as written (Show traveler details first; live required message on clear)
- [ ] Hide the traveler details and submit: the traveler error lands in the diagnostic below
      IMMEDIATELY instead of appearing inline — a collapsed section cannot show a message
- [ ] The accommodation question is still unanswered, so its own message is on screen: it sits
      tight under the radios, a grouping gap, because the radio labels are inline. Check it
      here — answering the question clears it, and only a reload brings it back
- [ ] Answer Yes: that error clears on the spot, and the Type select arrives carrying no message
      at all, because nothing has engaged it. Submit again and Type discloses "Choose an
      accommodation type"
- [ ] The Type message keeps a visibly wider gap under its select than the Yes/No message did,
      because that field's label is a block (unlike the inline radio labels) with its own
      trailing margin — both gaps are deliberate
- [ ] Summary click on the accommodation-type error focuses the select
- [ ] Pick Accessible: the Type error clears on the spot, choosing being a committed change, and
      **Special requirements** renders empty and carries NO message, though its rule applies to
      it and is failing — nothing has engaged it. Submit and it discloses, inline and in the
      summary
- [ ] All-clear submit: fill Destination, fill Special requirements, then show the traveler
      details and fill the name, and submit — it goes through, the status line confirms, and the
      suppressed-issue diagnostic empties
- [ ] **Gate entry lands:** with the traveler details still showing, clear the name and leave the
      field, then hide the section again and submit — the submit that just went through cleared
      what earlier submits had revealed, so nothing on screen accounts for this one and the
      summary's form-level "not currently displayed" entry appears, and it has somewhere to go:
      clicking it scrolls the FORM into view and focuses it. Reached from the KEYBOARD (tab to
      the entry, Enter) the form takes a visible accent outline; reached by MOUSE it takes the
      scroll with no outline; a stray click on the page background paints nothing
- [ ] Show traveler details again — the field renders with no message and the form-level entry
      stays — then submit: the traveler error lands inline and in the summary and the
      form-level entry gives way to it. Hide the section once more and submit: the entry stays
      listed with the field gone — disclosed once, watched until the form passes or resets
- [ ] Special requirements sits with a proper gap below the Type select

### Severity levels

- [ ] `Great synth!` + blur: amber warning live; 6 tags + blur: purple-blue info live
- [ ] Submit with Title: proceeds, status counts advisories; without: only the error blocks
- [ ] Inside the advisories block the warning is listed above the info, each link in its own
      colour; every link in the errors block is error-coloured
- [ ] The two summaries stack at the top of the form, errors and advisories listed separately,
      and each bands what it holds into a panel per severity: with Title filled and the
      warning/info showing, what stands there is the advisory summary's Warnings panel above
      its Info panel, and the error summary is absent entirely — not an empty box
- [ ] Nothing is listed twice: clear Title and submit so an error and both advisories show at
      once, then read all three panels — each message appears in exactly one of them, so a
      screen reader hears it once
- [ ] Each severity band carries its own background tint and border-left (not one shared
      red-tinted box): with all three severities showing at once, the info band in the advisories
      block (the Tags message) never picks up the error's colouring, in both light and dark

---

## Fields & collections

### Nested collections

- [ ] Row rhythm: input / message / actions row all breathe
- [ ] Errors glued to rows through move/remove; valid submit confirms
- [ ] **Both container entries land:** remove every team and submit — the summary's "Add at
      least one team" entry scrolls the teams container into view and focuses it. Then add a
      team, remove its last member and submit — the "Every team needs at least one member"
      entry lands on THAT team's fieldset. Keyboard-reached: accent outline on the container;
      mouse-reached: scroll only, no outline
- [ ] With a team's member list empty, its "needs at least one member" message reads as a
      heading for the (empty) member list below it, NOT as an error on the team's Name field
      above — it keeps a full field-gap above it and sits tight to the list

### Virtualize + KeepRegistered

- [ ] FIRST submit lists all 28 missing serials without scrolling
- [ ] Far entry click: panel scrolls, row renders, focus lands
- [ ] Fix a serial and tab out: its entry leaves the summary on that commit alone, no submit
      needed; the other 27 stay listed. Scroll far away and submit: it stays gone, and all 27
      of the others are still listed
- [ ] Scroll the whole panel slowly, top to bottom: rows sit flush against each other with no
      blank gap or overlap, whether or not the row carries a message

### Wrapping a foreign control

- [ ] Select styled like the inputs; the empty submit lands its own focus in the select, so tab
      out before judging the red border — a focused box wears the accent border whatever its
      verdict; the code panel's comparison reads well
- [ ] **Message spacing:** with "Colour is required" showing, the message sits tight under
      the select (a grouping gap, not a field gap) and has a full field-gap of room BELOW it —
      it is never flush against the Submit row. Confirm the message reads as the select's
      verdict, not as a caption for what follows

### Vanilla interop

- [ ] Colour message reads "Colour is required"; submit empty, then click the page background so
      neither box holds focus — only then do both inputs take the identical invalid border, since
      the submit's own focus lands in Nickname and a focused box wears the accent border
- [ ] **Nickname entry lands:** submit empty and click the summary's Nickname entry — focus
      moves INTO the native `InputText` (the page renders it the field's id). Keyboard-reached,
      the input shows its normal focus ring; nothing else on the page moves
- [ ] While the Nickname error stands, inspect the input: `aria-invalid="true"` and an
      `aria-describedby` naming the `ValidationMessage` below it. Fill it and blur: both go

### Attaching to your own EditForm

- [ ] Submit as seeded: the second expense line's message reads "Description is required" and
      the summary lists it; "Submitted by" carries no error yet
- [ ] Remove that line: the message leaves the row AND the summary — no second submit, no click
      beyond the row's own Remove button, and no more than a brief pause before the summary
      entry goes
- [ ] Add a line and submit it blank, then tab out of the box the submit focused: the new row
      takes the identical red border and message treatment as the seeded one, indistinguishable
      from an original row (a focused box wears the accent border whatever its verdict)
- [ ] Clear "Submitted by" and tab out: its message appears through the native `ValidationMessage`
      beside the input, styled identically to a Formidable message — and the summary above gains
      an entry for it too, with no Submit press of its own — committing the change engages the
      field, and that is what discloses it
- [ ] Both inputs — the native "Submitted by" box and a Formidable "Description" box — take the
      same invalid-state border in both light and dark mode

---

## Async & server

### Async rules

- [ ] Before any submit: typing lights only the edited field's indicator
- [ ] After a submit: typing STILL lights only the edited field's indicator (the refresh
      reconciles quietly)
- [ ] Submitting lights both indicators while the submit pass runs
- [ ] After an accepted submit, type a taken value again: its verdict appears when the check
      completes, without waiting for another submit
- [ ] Edit Username once after a submit and pause: exactly one "checking…" cycle runs, not
      two — the refresh that follows the live pass finds the uniqueness check already
      answered and does not run it again
- [ ] Type a username, then clear it: "Username is required" appears with NO submit anywhere —
      the field is engaged, and the live channel runs the rules a submit would
- [ ] Delay slider reads **600 ms** on load; dragging it updates the millisecond label live
- [ ] At 2000 ms: the pending state lingers long enough to type again and watch supersession
      cancel the stale check mid-flight — only the final value gets a verdict
- [ ] At 0 ms: verdicts land effectively instantly and no spinner is left behind
- [ ] Restore the slider to 600 ms before leaving the page
- [ ] Tick *Debounce live checks* and type `formidable` quickly: no "checking…" flash appears
      mid-keystroke — the indicator lights only once, after you pause typing
- [ ] Untick it again and type the same word quickly: the indicator flashes on the very first
      keystroke, back to the default (immediate, no batching)
- [ ] **Message spacing:** type `admin` into Username and let the verdict land — its message
      sits tight under the Username box and leaves a full field-gap before the *Display name*
      label. It must never sit flush against that label, and the "checking…" line while a pass
      runs must not push the message off its field

### Server round-trip

- [ ] Endpoint picker shows *Minimal API* selected by default, and the caption directly under
      the radios reads `POST /api/orders/ on the API (port 5180)`
- [ ] Click *MVC controller*: the caption rewrites to `POST /api/controller/orders` on the spot;
      click back and it returns — the caption always names the URL the next send will use
- [ ] Caption is legible in BOTH light and dark mode (muted, not washed out; the `<code>` chip
      readable against the page background)
- [ ] *Show the code* lists FOUR files and the fourth is **OrdersController.cs** — the real
      server file, not a paraphrase (it shows the `[ApiController]`/`[HttpPost]` twin of the
      minimal-API mapping)
- [ ] Empty send: the 400 lands inline, one message per field; summary click focuses
- [ ] That same empty send also moves focus on its own, no click needed — it lands on
      Description, the first field with an error on the page
- [ ] Fix one field, send again: every remaining error shows ONE message (no duplicates)
- [ ] Description with a hyphen (e.g. `Q3-restock`) while a SKU line is still empty, then send:
      the 400 carries the server's hyphen advisory as well as the SKU error, and the advisory
      shows on Description in warning styling — no separate advisory list anywhere on the page.
      Fill the SKU and send again: accepted — and the hyphen advisory stays, because the client's
      own rule still fails it; this accepted send carries no error, so it moves focus nowhere.
      Remove the hyphen too, click away from the field, and it goes
- [ ] A whitespace-only SKU line: dropped by the pre-send Normalize; no misattributed errors
- [ ] Switch to *MVC controller* and repeat the empty send: identical messages land on the
      identical fields — the hosting style makes no difference to the 400 shape

---

## Presentation

### Fitting a UI library

- [ ] DARK mode: the bordered demo box is dark too — Bootstrap's own dark palette, no light
      island sitting in a dark shell
- [ ] LIGHT mode: the demo box is light; the rest of the shell stays as it was
- [ ] Flip the OS colour scheme with the page OPEN: the demo box follows immediately, no reload
- [ ] Navigate away to another page and back, then flip again: still follows (the watcher is
      re-registered, not doubled or lost)
- [ ] In dark mode the site shell around the box stays dark — Bootstrap's body rule does not
      flip the whole page light
- [ ] Submit empty: Bootstrap's own red `is-invalid` borders appear (not the site theme's)
- [ ] Fix a field and leave it: Bootstrap's green `is-valid` state shows
- [ ] *Show the code*: the only Formidable-specific lines are the `Options` object and the
      components themselves

### CSS colours

- [ ] **The success colour, both schemes — do this FIRST, on a fresh load.** Type a title and
      tab out: the border confirms in a green plainly distinct from the accent. Check it in BOTH
      light and dark mode. This row goes first because picking any colour latches the wrapper's
      inline style, and that style carries all five LIGHT defaults, so the dark green is gone
      until the next load. RELOAD when you are done here: the next row needs the boxes empty
      again, and nothing on this one clears the title you just typed
- [ ] Submit empty, then pick a new error colour: the border, the message AND the summary's
      left rule and entry text all recolour at once
- [ ] Type a title and tab out, then pick a new valid colour: the confirmation border follows it
- [ ] Pick a new accent, then click into Description: the focus ring and the border the box
      wears while focused both follow
- [ ] Type `!` in Description: the amber warning arrives mid-keystroke, with no blur asked for.
      Recolour warning: message and border both follow
- [ ] RELOAD first: the rows above leave Title filled and its error already revealed, and this
      one needs both boxes untouched. Then type seven comma-separated tags into Tags, leaving
      Title empty, and click Submit IMMEDIATELY with no intervening Tab: the missing-Title
      error discloses in the summary on that first click. The "more than five tags" info commits
      and renders WHILE you type, not at blur, so nothing shifts the Submit button out from
      under the pointer at click time

### Scroll & focus

- [ ] Submit: a long summary appears; click the LAST entry — the page scrolls to the bottom
      row, centred, and focuses it
- [ ] Fix that field, submit, click another far entry: same ride back
- [ ] No focus miss anywhere on this page (every row is in the DOM — contrast Virtualize)
- [ ] With the toggle above the form ticked (its default): submit and focus jumps straight to
      the first error with no click needed
- [ ] Untick the toggle and submit again: the summary appears but focus stays put — nothing
      moves until you click an entry yourself

---

## Model & data

### Field-state visualizer

- [ ] Tab INTO Username and straight out again without typing: Touched flips to True while
      Modified stays False — exactly what the page's first *Try it* step claims
- [ ] Same for Display name: blur alone touches it, no value commit needed
- [ ] Neither blur typed anything, but only Display name takes the "confirmed" valid border:
      Username stays unstyled — the probe's answer already carries its required-rule failure,
      and a field the engine knows would fail submit is never styled valid. Nothing has been
      edited yet, so this is the probe's work alone, with no live pass behind it. Type `ada`
      into Username and tab out: its border confirms once the check lands; clear it again and tab
      out once more — the confirmation gives way to the error border, and "Username is required"
      lands with it — the field is engaged now, and the live channel runs the rules a submit
      would
- [ ] The panel's Rules bullet explains why (the page wires `field.MarkTouched()` to `onblur`;
      the built-in inputs touch on value commit) — read it and confirm it matches what you saw
- [ ] Type `admin`: Modified and Validating flip True, then Errors flips once the check lands
- [ ] After a submit, type `admin` again: Errors flips once that check completes, without
      waiting for another submit
- [ ] Submit: BOTH rows' Validating flip together — submit is a form-wide pass
- [ ] **Message spacing:** Username's message sits tight under its box and clear of the
      *Display name* label — never flush against it
- [ ] Load the page fresh: Submit is DISABLED and the readout above it reads "Form valid: No" —
      the empty model already fails the required-Username rule, before anything is typed
- [ ] Type `admin`: Submit stays disabled once the check lands (Form valid stays "No") — a
      taken username still fails
- [ ] Clear Username and type `ada` instead: once the check clears, the readout flips to
      "Form valid: Yes" and Submit enables itself with no click needed

### Normalize

- [ ] `  spaced   out  title  ` + *Normalize now*: the raw value line below snaps clean AND
      the INPUT BOX itself loses its spaces — box and model can never show different text
- [ ] Pad Title with spaces past 40 characters: "Title is 40 characters max" appears live;
      *Normalize now* trims under the limit and the message clears AT ONCE — no tab-through or
      submit needed
- [ ] *Try it* step 3 verbatim — `    Meeting notes about the Q3 rollout    ` (42 raw, 34
      trimmed) + *Normalize + submit*: trimming runs BEFORE validation, the 40-char rule judges
      the cleaned value, and the submit SUCCEEDS (status line confirms)
- [ ] All-spaces Title + *Normalize + submit*: trims to empty and ONLY "Title is required"
      shows — no length message stacked alongside it, whatever the number of spaces
- [ ] All three buttons fire on the FIRST click every time — no mid-click layout shift
      swallowing the press
- [ ] Body is a textarea; chrome and focus ring match the other fields, light + dark
- [ ] Tick *Normalize automatically on submit*, type `    Meeting notes about the Q3 rollout    `
      (42 raw, 34 trimmed) and click plain *Submit* (not *Normalize + submit*): it succeeds with
      no manual step — the option trimmed it first
- [ ] Untick the box, replace Title with the same raw text, and click *Submit* again: BLOCKED
      — "Title is 40 characters max" — the raw 42-character value is judged as typed, since
      nothing trimmed it

### Localization

- [ ] Empty submit in English: FluentValidation's own default answers for Full Name
      ("'Full Name' must not be empty."), the resx message answers for Age
- [ ] Switch to *Deutsch* — the page reloads — then submit empty: BOTH messages arrive in
      German (FluentValidation's built-in default AND the resx `AgeRange` message)
- [ ] `30` in Age and Tab: the range message clears on that commit alone. Replace it with `12`
      and Tab again: it returns, in the active language
- [ ] The line under the form reformats the long date and the grouped number per culture
- [ ] After the reload the culture select PRESELECTS the stored culture (it does not snap
      back to English)
- [ ] Full cycle: store Deutsch → reload → switch back to English → reload — the choice
      sticks each way, no stuck culture
- [ ] Dark mode: the culture select's chrome (background, text, dropdown arrow) matches the
      other controls on the page

---

## Workout

### Full workout

One check per *Try it* step of the page (the API must be running). Work top to bottom: the
steps build on each other.

- [ ] Load the page: **Ticket tier** already reads *General admission* (not *Choose…*), and
      **Include catering** is already ticked — a first submit never complains about a decision
      the visitor was not asked to make
- [ ] Submit without filling anything in (step 2): every presence rule whose field is empty
      answers at once — contact email, event name, event date, **dietary notes** and venue
      region, five fields — and the summary lists them all
- [ ] **Every summary entry lands.** Click each in turn: entries naming a Formidable input
      focus that input; the **venue-region** entry focuses the NATIVE input (see the venue
      checks below); and the **attendee** advisory focuses the Attendees fieldset, which owns
      no input of its own. Nothing in the summary is a click that goes nowhere. (The form-level
      gate entry is one more kind — step 7 raises it, and it is checked there)
- [ ] **Keyboard vs mouse landing.** Tab to a summary entry and press Enter: the container that
      takes the focus (Attendees fieldset, the form) shows an accent outline. Click the SAME
      entry with the mouse: it scrolls and takes focus with NO outline — the cue is the scroll.
      Then click stray page background or a fieldset's padding: nothing paints an outline
- [ ] `taken@example.com` in Contact email: "checking…" sits on that field for ~300 ms, then
      "That email is already registered" lands
- [ ] **Dates commit on blur, not per keystroke.** In **Event date**, type the year segment
      SLOWLY (`2`, `0`, `2`, `6`): no message appears while you are mid-year. Tab out: only
      then does the field get a verdict, and a complete date passes cleanly
- [ ] **A pure tab-through shows nothing.** Tab into **Event date** and straight out again
      without typing: no message, no state class on the box, no pending flash — a blur with no
      committed change behind it never notifies the engine, in this mode like every other
- [ ] Now type a deliberately garbled year (e.g. `0019`) and tab out: it is REJECTED as not a
      real date (the 1900–2100 range), not silently accepted. Fix it and the message goes
- [ ] Set **Early-bird deadline** AFTER **Event date** and tab out: the cross-field rule
      objects; move it back on or before the event date and it clears — the rule runs only once
      both dates parse
- [ ] Step 4: replace Contact email with a fresh address, fill **Event name**, **Event date**,
      **Dietary notes** and **Venue region** (five fields with Contact email), then submit with
      coupon `BOGUS`. The coupon verdict always arrives LAST by design — the POST happens only
      after a valid client submit — so the server's 400 lands inline on Coupon code with the
      rest of the form already clean. Change to `WELCOME10` and resubmit: the new verdict
      REPLACES the old one (no stale coupon error) and the registration is accepted
- [ ] Step 5: with everything else valid, clear Dietary notes and tab out — "Dietary notes are
      required for catering" answers your edit as soon as that edit's live pass does, a beat,
      since the pass waits on the 300 ms availability check
- [ ] Step 6: untick *Include catering* — the field leaves and takes the message with it,
      inline and summary alike
- [ ] Step 7: submit — the form blocks with "information that is not currently displayed is
      invalid": the one rule that can block has nowhere to show, and the submit that went
      through at step 4 cleared what earlier submits had revealed, so nothing on screen
      explains it
- [ ] **The gate survives editing (step 8).** While the form stays blocked, type into
      Description and pause: the edited field's pending marker comes and goes as the live pass
      and the background refresh run — and the form-level entry still stands on the far side. No
      refresh can retire the gate; an error reaching the screen does, and Description has no
      rule of its own to put one there. A submit that can show the error, or one that passes,
      re-decides it
- [ ] **The gate entry lands too.** Click that form-level entry: the FORM scrolls into view and
      takes focus. From the KEYBOARD it shows the accent outline; by MOUSE, the scroll alone
- [ ] **Disclosure (steps 9-10).** Re-tick *Include catering* — the note renders empty and stays
      quiet — and submit: its message appears inline and in the summary and the form-level line
      gives way to it
- [ ] **Union (steps 11-13).** Untick once more: the field goes and takes its inline message
      with it, but the summary keeps the entry. Submit again and it is still listed — once a
      submit has shown a field's error, its entry stands until the answer comes clean, and the
      field stays watched until the form passes or resets. Re-tick and fill in a note before
      moving on
- [ ] `nope@` in Contact email + *Save draft*: the draft answers about the always-on bucket
      only, and the malformed address is what it names — the note is back in. Now clear
      **Event name** and Tab: "Event name is required" lands as soon as that edit's live pass
      does — a beat, since the pass waits on the 300 ms availability check — because you
      engaged that field. Save the draft again and the status line still never mentions it, since a
      draft save runs the Draft profile and that presence rule is not in it. Type Event name
      back in
- [ ] *Add attendee*: the row stays silent even though Name's rule is already failing — nothing
      has engaged it yet. Type a name and Tab, then come back, clear it and Tab again:
      "Attendee name is required" appears as soon as that edit's live pass does — a beat, since
      the pass waits on the 300 ms availability check — inline and in the summary, with NO
      submit; clicking that entry focuses that row's Name
- [ ] Fill that Name, then add ten more named rows: past ten the warning "More than 10
      attendees needs approval — submission is not blocked" appears below the list
- [ ] With 11 rows the Attendees fieldset is tall: click the summary's attendee warning entry —
      the page scrolls so the warning message itself lands in view near the top of the
      viewport, not centred with the message off-screen above or below it
- [ ] Contact email is still `nope@` and is the only thing left broken: put a real address back,
      check catering is still ticked with a note in it and **Venue region** still filled, then
      submit — the warning does NOT block, and the status line confirms the registration was
      accepted
- [ ] *Remove* every row: the warning gives way to the info "You can add attendees now or
      after registering"
- [ ] Scroll the session panel to the bottom, set the last session's Seats to `900` and Tab:
      the row objects on the **FIRST tab-out** — "Seats must be a whole number between 0 and
      500" appears as soon as that edit's live pass does — a beat, since the pass waits on the
      300 ms availability check — with no second edit needed to shake it loose. Repeat on another
      row to be sure it is not a one-off, then set THAT row back to `0` too — leaving it dirty
      would let its error outrank Ticket tier's in document order and steal the focus at step 21
- [ ] Scroll back to the top and submit: blocked, the summary carries the same seats message
      for a row nowhere on screen — **and the submit's own focus reaches it too**: with no click
      at all, the panel scrolls itself, Virtualize renders the row, and focus lands in its Seats
      box
- [ ] Scroll the panel back to the top, then click that summary entry: the panel scrolls
      itself, Virtualize renders the row, focus lands in its Seats box. Set it back to `0` —
      the post-submit refresh takes both the inline message and the summary entry away
- [ ] Set **Ticket tier** to the blank *Choose…*: choosing is a committed change, so after the
      usual availability-check beat the foreign select takes the same inline message and summary
      entry as any wrapped input, with NO submit. Tab out and the red border joins them — a
      focused box wears the accent border whatever its verdict, so the severity one waits for
      the blur. Clicking that summary entry moves focus INTO the select
- [ ] Add a word to **Venue region**, which still holds what you typed earlier, and tab out: the
      blur's own live pass is what confirms
      it — no submit needed first — so the native input wears the same "confirmed" border a
      Formidable input shows, a beat later, once that pass's 300 ms availability check lands.
      The css class provider serves both kinds of input
- [ ] Clear **Venue region** and Tab (step 23): the native `ValidationMessage` shows "Venue
      region is required" with NO submit — the live channel's verdict reaches the EditContext's
      own message store — the summary lists it on the same terms, and clicking THAT entry
      **lands in the native input** — the page renders it the field's id, which is the whole of
      what the focus service looks for. No console error, no lost scroll position
- [ ] While that error stands, inspect the native input: `aria-invalid="true"` plus an
      `aria-describedby` naming the message below it. Fill the region and blur: `aria-invalid`
      disappears (it is conditional, and it stays current without a resubmit)
- [ ] **Message-bearing fields separate from the next field — both shapes.** With several
      errors showing at once, check the two idioms side by side: a message rendered INSIDE its
      field box (Event name, both dates, Description, Coupon code) and one rendered as the
      field's SIBLING (Contact email, Ticket tier). In both, the message sits tight under its
      own input and leaves a clear gap before the NEXT label — no message is ever flush against
      the label below it, and the two shapes read alike down the form
- [ ] Whole page in BOTH light and dark mode: fieldset legends, the scrolling session panel's
      border/background, the foreign select's chrome, the focus outlines on the Attendees
      fieldset and the form, and the summary's severity colours all read correctly; nothing is
      a light island in dark mode; date inputs' calendar icons legible in dark (Firefox renders
      picker chrome differently from Chromium — worth one glance; the Chromium-only E2E suite
      never sees it)

---

## Docs

### Recipes

A reading check, not a browser check — do it from the repo.

- [ ] `README.md`'s doc table carries the row *"I want to…" answered with code, plus a
      symptom-to-fix troubleshooting table* linking to `docs/recipes.md`; follow the link and it
      resolves
- [ ] `docs/recipes.md` opens with fourteen unnumbered `### I want to…` headings, then a
      troubleshooting table of twelve rows
- [ ] Spot-check the recipe titled **"I want every summary entry to land somewhere"** against
      what you just saw on /workout, /vanilla, /collections and /disclosure — the ids, the
      containers and the outline story match the pages
- [ ] Spot-check the recipe titled **"I want to validate while typing, on blur, or only at
      submit"** — its table of `UpdateOn` against what the live channel selects matches what
      /async and /field-state actually do, and the troubleshooting row *"A date input reports
      impossible years while it is being typed"* matches the workout's date behaviour you just
      walked
- [ ] Every recipe answers with code first, then links to the doc that explains it in full and
      the sample page that demonstrates it, and no recipe contradicts the page it names

### Quickstart and testing

Also reading checks, done from the repo.

- [ ] `docs/quickstart.md` builds a form out of three files — one page holding the model, the
      validator and the markup together, one `_Imports.razor` line, two `Program.cs`
      registrations — and its closing section says how to split that page up as the form grows
- [ ] The README's *5-minute quickstart* teaches the same three files, in the same order, and
      links on to `docs/quickstart.md` and the sample's Quickstart page (the app's home page)
- [ ] `docs/testing.md`'s **Testing your forms** section comes before the suite walk and covers
      three things in order: validating a model with no renderer, rendering the form under bUnit
      with doubles for the focus and DOM-sync services, and waiting for a verdict that lands a
      render later

---

## Hosted demo

Build the Pages artifact from the repo root (`pwsh build-pages.ps1`) — this also starts a local
static file server — then browse the two pages there. No API needs to be running — the artifact
answers its own requests.

- [ ] `/server` and `/workout` behave exactly as they do against the real API: empty submits
      land the same inline messages, `/workout`'s coupon check rejects `BOGUS` and accepts
      `WELCOME10`, and every other page is unchanged
- [ ] Both pages show the demo note under their heading ("This hosted demo has no server behind
      it — an in-browser handler answers with the same validators and the same response shapes
      the real API would send" — verbatim on `/server`; `/workout`'s ends "…coupon rejection
      included")
- [ ] Both pages' *Try it* list opens with the demo variant of its first step ("No server to
      start on this hosted demo…") — the "Start the API first" step is gone, not just hidden
