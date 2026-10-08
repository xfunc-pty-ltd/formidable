# Sample walkthrough

The sample app is Formidable's runnable tour, one page per feature. The Playwright suite in
`tests/Formidable.Sample.E2E` drives it end to end in a real browser
(`FORMIDABLE_E2E=1 dotnet test`; see [Releasing](../docs/releasing.md)). That suite covers
*behaviour*: which messages appear, where focus lands, what the server sends back.

This checklist covers what a headless browser cannot judge: colour, contrast, spacing, focus
cues, and the chrome the browser paints for native controls.

Walk the whole list once, in either OS colour scheme. Every row that judges a colour or a look
says "in both colour schemes": switch the scheme and check that row again. Behaviour does not
change with the scheme, so a second full pass would mostly repeat itself. A row that names one
scheme (LIGHT or DARK) checks what that scheme alone changes.

Each sample page has a teaching panel beside or above its form, and the panel numbers its
*Try it* steps. A row that names a step walks that step as the page words it, then checks what
the row adds. Work each section top to bottom: a row often needs the state the row above it
left.

Most rows need only the running sample. A row that names code (a component, an option or a
method) is saying why the check holds; the page's *Show the code* holds that code.

A few rows need a tool and say so where they ask for it. The tools are a screen reader
(Narrator or NVDA), the OS's reduce-motion setting and the browser's devtools (the elements
panel, the network tab or the console). Firefox, PowerShell (`pwsh`) and the repository's
source each serve a row or two.

Start both servers from the repo root:

```bash
dotnet run --project samples/Formidable.Sample.Api
dotnet run --project samples/Formidable.Sample
```

Then open <http://localhost:5181>. Every page and Navigation come first; the sections from
Start here to Workout follow the sidebar's groups; Docs and Hosted demo come last.

## Every page

- [ ] **The teaching panel, in both colour schemes.** Each page's panel is a box named "About this
      page" (an `aside`, which a screen reader lists as a landmark). It reads *Try it*, then *The
      rules*, then *How it works* and *Show the code*. The last two start closed and open on a
      click. *Show the code* holds the page's real source files, the same as in the repository
- [ ] **Try it steps, in both colour schemes.** Each step is one action, then its result on an
      indented line opening "You see:". That label is semibold, in the accent colour (the sample's
      blue). Some steps add a third line, "Why:", whose label is violet, set apart from the blue
      and from the red, amber, purple-blue and green of the field states
- [ ] **A wide window, in both colour schemes** (1,152 px or wider at the default text size): two
      columns, the form on the left and the panel on the right, about three to two. The panel
      scrolls with the page and has no scrollbar of its own, at any window height. It never
      covers a form control
- [ ] **Show the code on a wide window, in both colour schemes.** Open it: the panel moves above
      the form at full width. Close it and the two columns come back
- [ ] **A narrow window, in both colour schemes** (narrower than 1,152 px): one column, with the
      whole panel first, its two closed sections included, then the form. Past the sidebar, Tab
      moves down the page in the order it is drawn. It goes through the panel's links and its two
      closed sections, then into the form
- [ ] **The panel's colours, in both colour schemes:** its border, its headings, the "You see:"
      and "Why:" labels and the code under *Show the code* all read well
- [ ] In both colour schemes, buttons sit in a spaced actions row, and nothing touches a message
- [ ] In both colour schemes, a line under an actions row has breathing room below the buttons.
      That holds for a valid submit's status line and for the plain lines on three pages: the
      note under Async rules' Submit, Normalize's raw values line and Localization's formatting
      line
- [ ] A blocked submit moves focus to the first error IN DOCUMENT ORDER, with no click needed.
      That is the field highest in the form, not the one whose rule the validator declares
      first (`FormidableForm.FocusFirstErrorOnInvalidSubmit`, on by default). Scroll & focus has
      a toggle that turns it off, and Dialog-first submit suppresses the move for the submit
      that opens its dialog
- [ ] Two pages have no blocked submit, so the rows just above and below skip them. On
      Field-state visualizer, *Submit* stays disabled while the form is invalid. On Server
      round-trip, *Send to server* is an ordinary button, and step 1's rejection moves focus to
      the first error instead
- [ ] A blocked submit's summary slides open over roughly 0.2 s as its issues appear, rather than
      snapping into place. With the OS's reduce-motion setting on, it appears instantly instead.
      Dialog-first submit is the exception: its summary rides in on the dialog, which fades
      over the same 0.2 s and appears instantly under reduce-motion
- [ ] In both colour schemes, hovering an entry in the error summary tints only the entry text, in
      a pill sized to hug it. It is never a full-width bar across the row
- [ ] Each field the form requires with no condition attached carries an asterisk beside its label
      (one per row in a list), legible in both colour schemes. So does a field required even while
      hidden (Full workout's Dietary notes). A field required only under a condition wears none
      (Progressive disclosure's Type and Special requirements). Fitting MudBlazor is the one page
      without marks, and a rule on that page says why

## Navigation

- [ ] Sidebar shows seven groups in order: Start here, Core concepts, Fields & collections,
      Async & server, Presentation, Model & data, Workout
- [ ] All twenty-three links route to a live page; the active link is highlighted, legibly in both
      colour schemes
- [ ] Group headings are legible (small caps, muted) in both colour schemes
- [ ] On a first load or a hard reload, the ring on the "Loading the Formidable sample" screen
      turns until the app appears. The network tab's throttling holds that screen up long enough
      to watch
- [ ] Inspect a group heading and the list under it (devtools or a screen reader). The heading
      carries an id and the list's `aria-labelledby` names it. So the group reads as one unit
      to assistive technology, not as an unrelated heading floating above a plain list

---

## Start here

### Quickstart

- [ ] Step 1: ONE message per field, "Name is required" under Name and "Email is required"
      under Email. Both are in the error summary too
- [ ] Step 2: the click on "Email is required" in the error summary moves focus into Email
- [ ] Step 3: Email's message becomes "A valid email is required" as you leave the field
- [ ] Step 4: the status line thanks you by the name you typed
- [ ] **Slide on submit:** reload, then submit the empty form and watch Name's message arrive.
      It slides open over roughly 0.2 s, and the Email field below it eases down with it.
      Nothing jumps straight into its new position. With the OS's reduce-motion setting on, it
      appears instantly instead
- [ ] **Slide on clear:** type a name and press Tab. Name's message eases CLOSED over roughly
      0.2 s, and the Email field eases back up with it. It is the same motion in reverse, not a
      message snapping out of existence. With reduce-motion on, it disappears instantly
      instead
- [ ] **A screen reader hears the first blocked submit (no browser test can check this).** Start
      a screen reader (Narrator: Win+Ctrl+Enter, or NVDA), load the page fresh and submit it
      empty. The reader speaks the summary's "Name is required" and "Email is required", with
      no focus move from you. What must NOT happen is silence from the summary until a second
      submit
- [ ] On that same first submit, also expect the focused field's name and message: the form's
      own focus move lands in the Name box. The summary's alert region has been on the page
      since first paint, which is why even its first insertion is announced

---

## Core concepts

### Draft vs Submit

- [ ] Step 1: each label's text is followed by a red asterisk (`Title *` and `Summary *`), in both
      colour schemes. Nothing on the page declares one: the mark comes from the validator's Submit
      profile (the rules a submit checks)
- [ ] *The rules* state Title's 60-character limit. Step 2 shows "Title is 60 characters max"
      as soon as you leave the field, with no submit
- [ ] Steps 3 and 4: *Save draft* is blocked by the format rule only (Title's 60-character
      limit). With the title shortened, the draft saves with Summary still empty
- [ ] Step 5: "Title is required to submit" lands with NO submit anywhere. Summary, failing its
      own required rule and never changed, stays silent
- [ ] Steps 6 and 7: Summary's border turns green. Cleared again, it shows no message, no entry in
      the error summary and no red border, just the neutral border, in both colour schemes.
      Summary's input sets `WaitForSubmit`, which holds its error back until the first *Submit*
- [ ] Step 8: Summary speaks for the first time, and the error summary now carries both fields
- [ ] Step 9: Summary's message and its entry in the error summary clear with no second submit
- [ ] Step 11: the status line confirms the submit
- [ ] After step 11, clear Summary and leave the field. "Summary is required to submit" appears
      at once, red border included, with no submit, because the wait ended at the first
      *Submit*. Type a summary back in before step 12
- [ ] Steps 12 and 13, with text in both boxes: *Reset* clears the error and returns the form to
      pristine, with no field counted as changed and no message. The typed values in both boxes
      STAY exactly as typed, over-long title included. `ResetAsync()` never writes model
      properties, and it is the SAME form, with no page reload
- [ ] After step 13, clear Summary and leave the field. It is silent again, with no red border,
      since *Reset* starts the wait over

### Custom profiles

- [ ] Step 1, with *Standard submit* selected: Title, Slug, Category, Read minutes and Publish
      date each carry an asterisk. Review note has none
- [ ] Steps 2 to 4: Category is a select (`FormidableInputSelect`, `UpdateOn="OnBlur"`). After
      the empty submit, picking a category leaves "Category is required" as it was. It clears
      only when you press Tab
- [ ] Step 5: Read minutes is a number input (`FormidableInputNumber`). Typing `e3` and
      pressing Tab clears the box, so text the model never accepted does not linger. The
      submit's "Read time is required" sits over a box that visibly agrees with it
- [ ] Before step 6, type `0` into Read minutes and click *Submit*. The range message shows: "Read
      time must be between 1 and 180 minutes". The native spinner chrome matches the theme in both
      colour schemes
- [ ] Step 6, Publish date: it is a date input (`FormidableInputDate`, `UpdateOn="OnBlur"`).
      Typing a date and pressing Tab commits it, with no stray validation flash mid-type. The
      calendar picker chrome is legible in both colour schemes
- [ ] Step 6, the submit: Title, Slug, Category, Read minutes and Publish date are filled, and
      Review note is empty. The submit goes through under *Standard submit*
- [ ] Steps 7 to 9: *Admin review* RESETS the form, and Review note gains an asterisk. Before
      submitting anything, type into Review note and leave it, then clear it and leave it
      again. "A review note is required for admin review" appears with no submit since the switch,
      put there by the checks as you edit (the checks that run when you change a field), which
      follow the picked profile
- [ ] Steps 10 and 11: *Standard submit* RESETS the form again. Review note's message, its entry
      and its asterisk go. Repeat the two edits and no message appears at any point. Review
      note turns green and stays green, since that profile selects no rule for the field
- [ ] Step 12: enter the other five fields again (each switch emptied them). The submit under
      *Admin review* is blocked, because the review note is required here
- [ ] Step 13: with Review note filled, the submit goes through under *Admin review*

### Progressive disclosure

Work top to bottom: the steps build on each other.

- [ ] Steps 1 to 3 work as written. Traveler name appears with no message, turns green when
      filled, and shows its required message when cleared, with no submit
- [ ] Step 4: the traveler error lands IMMEDIATELY in the "Suppressed-issue diagnostic" list
      below, not inline. A collapsed section cannot show a message
- [ ] After step 4, the accommodation question's own message is on screen. In both colour schemes,
      it sits tight under the radios (a grouping gap: the small space that ties a message to its
      control), because the radio labels are inline. Check it now: step 7's answer clears it, and
      only a reload brings it back
- [ ] Step 7: the question's message clears on the spot. The Type select arrives with no
      message at all, because nothing has changed it. Before step 8, click *Submit*: Type now
      shows "Choose an accommodation type"
- [ ] In both colour schemes, Type's message keeps a visibly wider gap under its select than the
      Yes/No message did. That field's label is a block (unlike the inline radio labels) with its
      own trailing margin. Both gaps are deliberate
- [ ] Clicking Type's entry in the error summary focuses the select
- [ ] Steps 8 and 9: picking Accessible clears Type's error on the spot, since choosing is a
      change. Special requirements arrives empty with NO message, though its rule applies and
      is failing: nothing has changed it. The submit then shows it, inline and in the error
      summary
- [ ] In both colour schemes, Special requirements sits with a proper gap below the Type select
- [ ] Step 10: the submit goes through, the status line confirms, and the diagnostic list stays
      empty
- [ ] **Gate entry lands.** After step 10, clear the traveler's name and leave the field. Then
      hide the section and submit. Step 10's submit cleared what earlier submits showed, so
      nothing on screen explains this one. The error summary shows the gate: a form-level entry,
      "…not currently displayed is invalid", for a blocked submit that can show none of its
      errors. Clicking it scrolls the FORM into view and focuses it
- [ ] **Gate entry, keyboard and mouse, in both colour schemes.** Reached from the KEYBOARD (Tab
      to the entry, Enter), the form takes a visible accent outline. Reached by MOUSE, it takes
      the scroll with no outline. A stray click on the page background paints nothing
- [ ] Show traveler details again: the field renders with no message, and the form-level entry
      stays. Submit, and the traveler error lands inline and in the error summary, and the
      form-level entry gives way to it. Hide the section once more and submit: the entry stays
      listed with the field gone, and the form-level entry comes back beside it, since no error
      shows under a field. A field whose error a submit has shown stays listed until the form
      passes or resets. Show the section: the traveler message is back at once, and the
      form-level entry leaves with no submit
- [ ] **The second form, in both colour schemes.** *Without a summary* sits in its own bordered
      card, with a clear gap above it and its heading inside. Its button is named *Request trip*,
      so it stands apart from the first form's *Submit*
- [ ] Steps 11 and 12 walk as written on the *Without a summary* form. The gate's explanation
      arrives through the form-level message list at the top of that form, with a gap between it
      and *Show trip details*. It gives way to the three inline messages once the trip details
      show
- [ ] **The gate comes back for hidden fields.** After step 12, click *Hide trip details*, then
      *Request trip*. The three messages left with their fields, and this form has no summary,
      so the gate's explanation is back in the form-level list. Click *Show trip details*: the
      three messages return and the list empties, with no submit
- [ ] Step 13: the completed form (answer No) confirms on its own status line
- [ ] In both colour schemes, that form-level list takes no visible space while it is empty:
      before the first submit, and again once the inline messages take over. The gate's
      explanation slides open into it on the blocked submit, the way a field's message does
- [ ] **Required marks, in both colour schemes.** Destination wears the asterisk, and so does the
      accommodation question. Its mark trails the legend text, which names the field, not the Yes
      or No label. Type and Special requirements, on screen by now, wear none: their presence
      rules are conditional. Show the traveler details once more and that label carries one too.
      The *Without a summary* form already shows the same three
- [ ] **Required marks, in devtools.** Both radios carry `aria-required="true"`, and every star
      carries `aria-hidden="true"`. So the accessibility pane computes the Destination input's
      name as `Destination`, with no star in it

### Severity levels

- [ ] Steps 1 and 2: `Great synth!` brings the amber warning as you leave the field, and six tags
      bring the purple-blue info. Neither needs a submit. Judge the amber and the purple-blue in
      both colour schemes
- [ ] Step 3: inside the advisories block, the warning is listed above the info, each in its own
      colour, in both colour schemes
- [ ] Step 4, in both colour schemes: the two summaries stack at the top of the form, errors and
      advisories listed separately. Each bands what it holds into a panel per severity. With Title
      filled, the advisory summary shows its Warnings panel above its Info panel. The errors
      summary shows nothing: no empty panel, no tint, and no stray gap. Its wrapper and live
      region stay for screen readers, drawing nothing
- [ ] Steps 4 to 6: with Title filled, the submit proceeds and the status line counts the
      advisories. With Title cleared, only the error blocks
- [ ] Steps 5 and 6, nothing listed twice: with the error and both advisories showing, read all
      three panels. Each message appears in exactly one of them, so a screen reader hears it
      once
- [ ] Each severity panel carries its own background tint and left border, not one shared
      red-tinted box. With all three showing, the Info panel (the Tags message) never picks up the
      error's colouring. Every link in the Errors panel is error-coloured. Check all of it in both
      colour schemes

---

## Fields & collections

### Nested collections

- [ ] After step 2, look at the rows in both colour schemes: input, message and actions row all
      breathe
- [ ] Steps 3, 4 and 6: errors stay glued to their rows through the move and the remove. The
      valid submit confirms
- [ ] Step 5, in both colour schemes: the empty member list's "Every team needs at least one
      member" reads as a heading for that list. It does NOT read as an error on the team's Name
      field above. It keeps a full field-gap (the larger space between one field and the next)
      above it and sits tight to the list
- [ ] **Both container entries land.** After step 6, remove every team and submit. The error
      summary's "Add at least one team" entry scrolls the teams container into view and focuses
      it. Then click *Add team* (a new team starts with no members) and submit. The "Every team
      needs at least one member" entry lands on THAT team's fieldset. Keyboard-reached, the
      container shows an accent outline in both colour schemes; mouse-reached, only a scroll

### Virtualize + KeepRegistered

- [ ] Step 1: the FIRST submit lists all 28 missing serials without scrolling
- [ ] Step 3: clicking the last entry scrolls the list, the row renders, and focus lands in it
- [ ] Steps 4 to 6: the fixed serial's entry leaves the error summary on that change alone, with
      no submit. The other 27 stay listed. With the list scrolled away from that row, step 6's
      submit keeps it gone, and all 27 others are still listed
- [ ] Scroll the whole list slowly, top to bottom, in both colour schemes. Rows sit flush against
      each other, with no blank gap or overlap, whether or not a row carries a message. Check it
      on a wide window too, where the form has the narrower left column

### Wrapping a foreign control

- [ ] Steps 1 and 2, in both colour schemes: the select is styled like the inputs. The empty
      submit lands its own focus in the select, so judge the red border only after step 2's Tab. A
      focused box wears the accent border whatever its state
- [ ] **Message spacing, in both colour schemes.** With "Colour is required" showing, the message
      sits tight under the select: a grouping gap, not a field gap. It has a full field-gap of
      room BELOW it, never flush against the *Submit* row. It reads as the select's verdict, not
      as a caption for what follows
- [ ] Step 6: the comparison under *Show the code* reads well

### Vanilla interop

- [ ] Steps 1 and 2: Colour's message reads "Colour is required". Both inputs take the identical
      invalid border, in both colour schemes, only once neither box has focus. The submit's own
      focus lands in Nickname, and a focused box wears the accent border
- [ ] Step 3: focus moves INTO the native `InputText`, which the page gives the field's id.
      Keyboard-reached, the input shows its normal focus ring in both colour schemes, and nothing
      else on the page moves
- [ ] **Nickname's aria attributes, in devtools.** Reload, then inspect the input before step 1:
      it carries neither `aria-invalid` nor `aria-describedby`. The `ValidationMessage` the
      second would name is not on the page until it has a message. Walk steps 1 to 4 again.
      After step 1 both have arrived: `aria-invalid="true"`, and an `aria-describedby` naming
      the message below. After step 4 both have gone. `aria-required="true"` stays throughout

### Attaching to your own EditForm

- [ ] Step 1: the second expense line's message reads "Description is required", and the error
      summary lists it. "Submitted by" carries no error yet
- [ ] Step 2: the message leaves the row AND the error summary, with no second submit. Nothing
      beyond the row's own *Remove* is clicked, and the entry goes after no more than a brief
      pause
- [ ] Step 3, then press Tab to leave the box the submit focused. In both colour schemes, the new
      line takes the same red border and message the seeded line had at step 1. A focused box
      wears the accent border whatever its state
- [ ] Before step 4, clear "Submitted by", press Tab and click the page background. The native
      "Submitted by" box and the new line's Formidable "Description" box wear the same invalid
      border, in both colour schemes. Type a name back into "Submitted by" and press Tab, then
      carry on at step 4
- [ ] Step 5: the message appears through the native `ValidationMessage` under the input, styled
      like a Formidable message in both colour schemes. The error summary gains an entry for it
      too, with no submit. Changing the field is what shows it

---

## Async & server

### Async rules

- [ ] Before step 1: the *Simulated delay* slider reads **600 ms** on load. Dragging it updates
      the millisecond label as you drag
- [ ] **Message spacing, in both colour schemes.** At step 1, once "That username is taken" lands,
      it sits tight under the Username box. It leaves a full field-gap before the *Display name*
      label, never flush against it. The "checking…" line while a check runs must not push the
      message off its field
- [ ] Steps 1 to 3, before any submit: typing lights only the edited field's indicator (its
      spinner and its "checking…" line)
- [ ] **The spinner turns, in both colour schemes.** At step 4, with the delay at 2000 ms, the
      spinner in the box turns for the whole two seconds. It turns with the OS's reduce-motion
      setting on, too
- [ ] After step 4, with the delay still at 2000 ms, type two more letters a second apart.
      "checking…" stays, no answer shows for the value you typed past, and only the final
      value gets an answer
- [ ] Step 5: "Username is required" appears with NO submit anywhere. You changed the field, and
      the checks as you edit run the rules a submit would
- [ ] Step 8, after step 6's submit: typing STILL lights only the edited field's indicator. The
      whole-form re-check after a submit runs quietly
- [ ] Step 9: one edit to Username after the submit, then a pause, runs exactly one "checking…"
      cycle, not two. The whole-form re-check finds the uniqueness check already answered and
      does not run it again
- [ ] After step 9's accepted submit, replace Username with `admin`. "That username is taken"
      appears once its check completes, with no second submit
- [ ] Wait more than ten seconds after the last check, then click *Submit* with both fields
      filled. Both fields show "checking…" while the submit's check runs. Inside ten seconds,
      this page's memo (its validator remembers each answer for ten seconds) answers at once
- [ ] Step 10: with *Debounce live checks* ticked, typing `formidable` quickly shows no
      "checking…" mid-keystroke. The indicator lights only once, after you pause typing
- [ ] Untick it again and type the same word quickly. The indicator lights on the very first
      keystroke, as by default: a check on every keystroke
- [ ] Drag the slider to 0 ms and type into Username: answers land effectively at once, and no
      spinner is left behind
- [ ] Set the slider back to 600 ms, for the next row and before leaving the page
- [ ] **A green border rides out a slow check.** Type a free username (`tim`) and let it turn
      green. Then click into Display name and type a letter. While that field's 600 ms check
      runs, Username's green border stays the whole time, untouched by this edit. Display name
      goes neutral, with "checking…" showing, until its own check lands. Then it turns green
      the same way

### Server round-trip

- [ ] Before step 1: the *Endpoint* picker shows *Minimal API* selected. The caption directly
      under the radios reads `POST /api/orders/ on the API (port 5180)`
- [ ] The caption is legible in both colour schemes: muted, not washed out, with the `<code>` chip
      readable against the page background
- [ ] *Show the code* lists FOUR files, and the fourth is **OrdersController.cs**. It is the real
      server file (`samples/Formidable.Sample.Api/Controllers` in the repository), not a
      paraphrase: the `[ApiController]`/`[HttpPost]` twin of the minimal-API mapping
- [ ] Step 1: the 400 lands inline, one message per field. The send also moves focus on its
      own, with no click needed, to Description, the first field with an error on the page
- [ ] Step 2: a click on an entry in the error summary focuses its field
- [ ] Step 3: every remaining error shows ONE message, with no duplicates
- [ ] Step 3: with `Q3-restock` in Description and the SKU line still empty, the 400 carries the
      server's hyphen advisory as well as the SKU error. The advisory shows on Description in
      warning styling. There is no separate advisory list anywhere on the page
- [ ] Steps 4 and 5: the send with the SKU filled is accepted, and the hyphen advisory stays,
      because the form's own copy of the rule still fails it. This accepted send carries no
      error, so it moves focus nowhere. Remove the hyphen and leave the field, and the advisory
      goes
- [ ] Step 6: the caption rewrites to `POST /api/controller/orders` on the spot. Click
      *Minimal API* and it returns, then click *MVC controller* again. The caption always names
      the URL the next send will use
- [ ] With *MVC controller* picked, empty Description and the SKU and send. The same messages land
      on the same fields as the first empty send's. In devtools' network tab, the two 400 bodies'
      `errors` are identical, and only the MVC body adds a `traceId`: Formidable's endpoint filter
      and action filter share one mapper
- [ ] After step 7, type only spaces into the new line's SKU and send. The pre-send
      `Normalize()` drops that line, and no error lands on the wrong line

---

## Presentation

### Fitting a UI library

- [ ] Before step 1, in both colour schemes: *Submit* is Bootstrap's own primary button
      (`btn btn-primary`), not the site theme's orange. It is the same Bootstrap blue in each
      scheme, and hovering it darkens the blue
- [ ] Step 1, in both colour schemes: Bootstrap's own red `is-invalid` borders appear, not the
      site theme's
- [ ] Step 2, in both colour schemes: Bootstrap's green `is-valid` state shows on the fixed field
- [ ] Step 4: the only Formidable-specific lines are the `Options` object and the components
      themselves
- [ ] Step 5, in DARK mode: the box around the form is dark too, in Bootstrap's own dark
      palette. No light island sits in a dark shell
- [ ] Step 5, in LIGHT mode: the box around the form is light, and the page keeps its white
      background. Bootstrap's stylesheet styles the whole document while you are on this page, as
      its *How it works* says. So the headings, code spans (pink) and links (Bootstrap's blue)
      differ from other pages in both colour schemes, and in light mode the body text takes
      Bootstrap's near-black. That is expected
- [ ] Step 5: with the page OPEN, the box follows the OS colour scheme at once, with no reload
- [ ] Navigate away to another page and back, then flip the scheme again. The box still
      follows each flip, so the page's scheme watcher came back with the page
- [ ] In dark mode the site shell around the box stays dark. Bootstrap's body rule does not flip
      the whole page light

### Fitting MudBlazor

Walk this section on the local build. The hosted demo shows a note at `/mudblazor` instead, which
the Hosted demo section walks.

- [ ] Before step 1, in both colour schemes: the text field, the select and the *Submit* button
      wear MudBlazor's own look. That is an underlined field with a floating label and a filled
      button, in MudBlazor's palette for that scheme
- [ ] Flip the OS colour scheme with the page OPEN: the controls and the button follow at once,
      with no reload
- [ ] Step 1, in both colour schemes: each control's underline and floating label turn MudBlazor's
      error colour, and Formidable's message sits under the underline. No box or ring is drawn
      round the control. The focused Band name keeps its label in the error colour rather than the
      accent
- [ ] After step 1, inspect Band name in devtools. `formidable-invalid` sits on the outer
      `mud-input-control` div. The `id`, `aria-invalid` and `aria-describedby` sit on the
      `<input>` inside it
- [ ] Step 2, in both colour schemes: focus lands in Room with MudBlazor's own focus underline,
      not the sample's rounded accent ring. The popover stays shut
- [ ] Steps 3 and 4: both underlines and labels turn green, in both colour schemes
- [ ] Step 4: Room's popover lists three studios right against the select: below it, or above it
      when the window has no room below. It sits on MudBlazor's own surface, legible in both
      colour schemes. Picking one closes it and shows the studio in the field
- [ ] In both colour schemes, while this page is open, the whole page (sidebar included) takes
      MudBlazor's fonts and spacing. At step 6 the other page shows the sample's own fonts again
- [ ] Steps 6 and 7, in both colour schemes: the other page showed nothing of MudBlazor's look.
      Back here, Room's popover still sits against the select
- [ ] A failed script load, in both colour schemes. Reload the app on another page, block
      `MudBlazor.min.js` in devtools (the Network panel's request blocking), then open this page.
      Where the form would be, a sentence says MudBlazor's script did not load, with a *Retry*
      button under it. Both are legible, and *Retry* reads as a button. Unblock the request and
      click *Retry*: the form appears as it does on a first visit

### CSS colours

Every colour row here runs in both colour schemes. Reload before checking the second scheme:
picking any colour sets the others to their light defaults until the page reloads.

- [ ] **The valid colour, in both colour schemes: do this FIRST, on a fresh load.** Type a title
      and press Tab. The border turns a green plainly distinct from the accent. Picking any colour
      writes every colour token (the CSS custom properties the pickers set), the rest at their
      LIGHT defaults, so the dark green is gone until reload. RELOAD when you are done: step 1
      needs empty boxes
- [ ] Steps 1 and 2, in both colour schemes: the border, the message AND the error summary's left
      stripe and entry text all recolour at once
- [ ] Steps 3 and 4, in both colour schemes: the green border follows the new valid colour
- [ ] Step 5, in both colour schemes: the focus ring and the border the box wears while focused
      both follow the new accent
- [ ] Steps 6 and 7: the amber warning arrives mid-keystroke, with no Tab asked for. The message
      and the border both follow the new warning colour, in both colour schemes
- [ ] RELOAD first, since this row needs both boxes untouched. Type seven comma-separated tags
      into Tags, leaving Title empty. Click *Submit* IMMEDIATELY, with no Tab between: the
      missing-Title error shows in the error summary on that first click. The "More than five
      tags…" info arrives WHILE you type, not when you leave the box. So nothing shifts the
      *Submit* button out from under the pointer at click time

### Scroll & focus

- [ ] Step 1, with the toggle above the form ticked (its default): the long error summary
      appears. Focus jumps straight to the first error, with no click needed
- [ ] Step 2: the page scrolls to the bottom row, centred as far as the page can scroll, and
      focuses it
- [ ] Steps 3 and 4: fix that field, submit, and click another far entry. It gives the same ride
      back
- [ ] No focus misses anywhere on this page, since every row is on the page all the time.
      Contrast Virtualize + KeepRegistered
- [ ] Step 5: the error summary still lists the errors, but focus stays put on *Submit*. Nothing
      moves until you click an entry yourself (step 6)

### Shaping the summary

- [ ] **With a screen reader running**, at steps 2 to 4: the bands (the error summary's lists, one
      per severity) are live regions, so what changed inside one is announced. Focus stays on the
      toggle you ticked. A band whose contents did not change says nothing; at step 4, listen to
      the warning band
- [ ] Steps 2 to 5, in both colour schemes: the toggles sit ABOVE the error summary. So the toggle
      you are working stays put while the list grows and shrinks below it
- [ ] Step 5, and again at step 9, in both colour schemes: with the cap on and *Reveal what the
      cap held back* unticked, the error band simply ends after three entries. There is no
      ellipsis, no faded row, and no gap where a line used to be. It should read as a list that
      ends, not as one that was cut off
- [ ] Steps 6 and 7, in both colour schemes: look at the hairline rule above the "3 not listed"
      line, closed and then open. It should read as the end of the list in both, with air on each
      side, and the revealed names clearly INSIDE what it closes off. It is Dialog-first submit's
      rule, under an expander and its list rather than a bare count, so look at both before it
      ships
- [ ] Step 7, in both colour schemes: the revealed names sit indented under the line, quieter than
      the entries above them. They read as a note about the list, not as more things to click
- [ ] **Hover, both kinds of row, in both colour schemes.** Hovering an entry pills the entry text
      and shows a pointer. Hovering the expander's label shows a pointer and NO pill. One is a
      route into the form and the other acts on the list, so the two should not read as the same
      kind of thing
- [ ] **Keyboard.** Tab after the last entry reaches the expander's label. Space or Enter opens
      and closes it, and the browser's own triangle turns with it
- [ ] **Colour check, in both colour schemes.** The overflow line, its label and the revealed
      names are all legible against the error band's tint. All of them stay visibly quieter than
      the entries

### Dialog-first submit

- [ ] Step 1, in both colour schemes: the dialog fades in over roughly 0.2 s, the page behind it
      dims, and the dialog itself takes focus. Reach *Submit* with the keyboard and press Enter:
      the dialog shows a focus ring as well. Press <kbd>Escape</kbd>, then click *Submit* with the
      mouse: it takes focus without one, by the `:focus-visible` rule the summary's own landings
      follow
- [ ] **With a screen reader running**, step 1's submit announces the dialog: the heading, the
      count line and the four names, nothing before the submit and nothing twice. At step 5,
      <kbd>Escape</kbd> hands focus back to *Submit* as the dialog closes, then the page moves
      it on: you hear "Submit, button", then "Invoice reference, edit". That order passes;
      note whether hearing the pair is worth it
- [ ] Step 2, in both colour schemes: the list reads as four field NAMES, not four sentences, with
      `2 more to fix` under them. Hovering a name pills the name. Hovering the overflow line does
      nothing: it is a count, not something to click, and it never takes a focus ring or a pointer
      cursor
- [ ] Step 2, in both colour schemes: `2 more to fix` sits under a hairline rule with air on both
      sides of it. Its text starts at exactly the same left edge as the names above it; sight down
      that edge, and the line should not sit in from them. The rule should read as the end of the
      list, not as a box around it
- [ ] Step 3: the dialog fades OUT, and only once it has gone does the caret appear in Invoice
      reference. Watch the box, not the dialog: nothing should flicker into focus early
- [ ] Step 4, with *Wait for the dialog to finish closing before moving focus* unticked: you
      can SEE the box take focus and then lose it as the dialog finishes closing. Focus ends on
      the *Submit* button (step 5 ticks the toggle again)
- [ ] Steps 5 and 6: <kbd>Escape</kbd> closes the dialog, and the caret ends in Invoice reference,
      the first thing to fix, not the button you pressed. *Close* ends in the same box. Neither
      names a field, so the page asks for the move; nothing in Formidable starts it. Watch the
      button in both colour schemes: it takes focus back before the page moves on, and should not
      flash a ring
- [ ] Focus is CONTAINED while the dialog is open, which is what its `aria-modal="true"` says.
      Tab from the open dialog reaches the names and then *Close*. Tab from *Close* comes back
      to the first name rather than reaching the form behind it. Shift+Tab from the first name
      goes to *Close* rather than out of the dialog. Shift+Tab straight after the dialog opens,
      before touching anything, stays inside as well
- [ ] Step 7, with *Focus the first error automatically on a blocked submit* ticked: the
      Invoice reference box behind the overlay already wears the focus border. A keystroke
      would land in a field you cannot see (step 8 unticks that toggle again)
- [ ] **Colour check, in both colour schemes.** The dialog, the error band inside it, the names
      and the overflow line are all legible over the dimmed page. The dialog's shadow reads as a
      raised surface rather than a smudge
- [ ] **With the OS's reduce-motion setting on:** the dialog appears and goes instantly.
      Clicking a name still lands in the field rather than anywhere else

---

## Model & data

### Field-state visualizer

- [ ] Step 1, on a fresh load: Submit is DISABLED, and the readout above it reads "Form valid:
      No". The empty model already fails the required-Username rule, before anything is typed
- [ ] **The disabled Submit, in both colour schemes.** It is grey with muted text, the pointer
      over it shows a not-allowed sign, and hovering it changes nothing. Once step 6 enables
      it, it turns orange
- [ ] Step 2: Touched flips to True while Modified stays False, exactly as the step says
- [ ] Step 2's Why line and *How it works* explain it: the page wires `field.MarkTouched()` to
      `onblur`, while Formidable's own inputs touch a field only when a change reaches the form.
      Read them and confirm they match what you saw
- [ ] Step 3: the same for Display name. Leaving the box touches it, with no value change needed
- [ ] Step 3: neither visit typed anything, but only Display name takes the green border. Username
      stays uncoloured: the form checks itself as it opens, through the `TrackFormValidity` check
      and a re-check it runs with or without that option, so it already knows the required rule
      fails. A field that would fail a submit is never green. Nothing has been changed yet, so
      those two opening checks alone decide it
- [ ] Step 4: Modified and Validating flip True, then Errors flips once the check lands
- [ ] Step 4: once that check lands, Submit stays disabled and Form valid stays "No". A taken
      username still fails
- [ ] **Message spacing, in both colour schemes.** Username's message sits tight under its box and
      clear of the *Display name* label, never flush against it
- [ ] Steps 5 and 6: once `ada`'s check clears, the readout flips to "Form valid: Yes". Submit
      enables itself with no click needed
- [ ] After step 7, clear Username and press Tab. The green gives way to the error border, and
      "Username is required" lands with it. You changed the field, and the checks as you edit
      run the rules a submit would. Type `ada` back in and press Tab before step 8
- [ ] Step 8: BOTH rows' Validating flip together, since a submit checks the whole form
- [ ] After step 8, replace Username with `admin`. Errors flips once that check completes,
      without waiting for another submit

### Loading a saved draft

- [ ] Step 1: three empty boxes, three asterisks, and no borders or messages anywhere
- [ ] Step 2: all three values arrive at once, and three different answers arrive with them.
      Title takes the green border. Contact email takes the red one and says "That is not a
      valid email address". Summary looks exactly as it did
- [ ] Step 3: Summary is still marked required AND still silent. The mark is what the rules
      demand. The silence is because nobody has reached the field, and a required field nobody
      has reached says nothing. Read the step's Why line and confirm it matches what you saw
- [ ] Step 4: the email message is the FORMAT rule's, not the required rule's. The box is not
      empty, so what is wrong with it is the address
- [ ] Step 5: the message goes and the border turns green, with no submit anywhere
- [ ] Step 6: Summary turns green too, the ordinary way
- [ ] Step 7: the status line confirms the submit
- [ ] Steps 8 and 9: *Start blank* empties the boxes, and all three green borders clear with them
      at once: Title's, Contact email's and Summary's. None lingers after its box empties.
      *Load saved draft* then replays the whole of step 2
- [ ] **Colour check, in both colour schemes.** After step 9, Title's green border and Contact
      email's error border are legible side by side

### Normalize

- [ ] Step 1: type `spaced   out  title` with two spaces before and after it, then click
      *Normalize now*. The raw value line below snaps clean AND the Title box itself loses its
      spaces. Box and model can never show different text
- [ ] Steps 2 and 3: padding Title with spaces past 40 characters brings "Title is 40
      characters max" as you type. *Normalize now* trims it under the limit, and the message
      clears AT ONCE, with no Tab or submit needed
- [ ] Steps 4 and 5: type exactly what step 4 gives (four spaces,
      `Meeting notes about the Q3 rollout`, four more: 42 raw, 34 trimmed), then click
      *Normalize + submit*. Trimming runs BEFORE validation, so the 40-character rule judges
      the cleaned value. The submit SUCCEEDS, and the status line confirms it
- [ ] Steps 6 and 7: an all-spaces Title plus *Normalize + submit* trims to empty. ONLY "Title
      is required" shows, with no length message stacked beside it, whatever the number of
      spaces
- [ ] All three buttons fire on the FIRST click every time. No layout shift mid-click swallows
      the press
- [ ] Body is a textarea; its chrome and focus ring match the other fields in both colour schemes
- [ ] Step 8: with *Normalize automatically on submit* ticked, four spaces,
      `Meeting notes about the Q3 rollout` and four more (42 raw, 34 trimmed) go through on a
      plain *Submit*. That is *Submit*, not *Normalize + submit*, with no manual step: the
      option trimmed it first
- [ ] Step 9: with the box unticked, the same raw text is BLOCKED by "Title is 40 characters
      max". The raw 42-character value is judged as typed, since nothing trimmed it

### Localization

- [ ] Watch the line under the form at steps 1, 3 and 7: it writes the long date and the
      grouped number in each culture's own format
- [ ] Step 2, in English: FluentValidation's own default answers for Full Name ("'Full Name'
      must not be empty."). The resx message answers for Age
- [ ] Steps 3 and 4: picking *Deutsch (Deutschland)* reloads the page. The empty submit then
      brings BOTH messages in German: FluentValidation's built-in default AND the resx
      `AgeRange` message
- [ ] At step 3's reload, and again at step 7's, the *Messages and formats* picker PRESELECTS
      the stored culture. It does not snap back to English
- [ ] Steps 5 and 6: `30` in Age and Tab clears the range message on that change alone. `12`
      and Tab brings it back, in the active language
- [ ] Step 7 completes the full cycle: Deutsch stored at step 3, a reload, English stored, a
      reload. The choice sticks each way, with no stuck culture
- [ ] In both colour schemes, the picker's chrome (background, text, dropdown arrow) matches the
      other controls on the page

---

## Workout

### Full workout

The API must be running. Walk the tour first, on a fresh load: it is the page's *Try it*, eleven
steps in eight groups. Then reload and walk the full set below it, top to bottom. Those rows
build on each other and check what the tour leaves to *How it works*.

The tour:

- [ ] *Try it* opens with the line about starting the API. Eight group headings follow, each over
      its steps, numbered 1 to 11. They read "Save draft: the draft rules", "Contact email: an
      async check", "Coupon code: the server's answer", "Catering: hidden fields", "Attendees: a
      warning", "Sessions: a long list", "Ticket tier: a plain select" and "Venue region: Blazor's
      own input". All read as small group titles in both colour schemes
- [ ] **Save draft (step 1):** the status line names Dietary notes alone, though four other
      required boxes are empty
- [ ] **Contact email (step 2), in both colour schemes:** the spinner sits inside the box and
      turns while "checking…" shows under it, then the message lands
- [ ] **Coupon code (steps 3 and 4):** `BOGUS` puts the server's message on Coupon code alone.
      `WELCOME10` clears it as the registration goes through
- [ ] **Catering (steps 5 and 6):** the blocked submit names the form, not a field, in the error
      summary. Ticking the box brings Dietary notes back empty and quiet until the submit shows
      its message
- [ ] **Attendees (step 7):** the warning appears below the list with the eleventh row, not
      before
- [ ] **Sessions (steps 8 and 9):** Session 150's message appears on the first Tab. The click on
      its entry scrolls the list back to the row and lands focus in its box
- [ ] **Ticket tier (step 10):** the message and the entry arrive as you pick, before any Tab
- [ ] **Venue region (step 11):** the native message appears on Tab with no submit, and the
      click lands focus back in the box
- [ ] Each step ends with a "More on" link on its own indented line, in both colour schemes. After
      step 11, follow each one and come back with the browser's Back button. Each opens the page
      it names inside the app: Draft vs Submit, Async rules, Server round-trip, Progressive
      disclosure, Severity levels, Virtualize + KeepRegistered, Wrapping a foreign control or
      Vanilla interop

The full set:

- [ ] Load the page: **Ticket tier** already reads *General admission* (not *Choose…*), and
      **Include catering** is already ticked. A first submit never complains about a decision
      the visitor was not asked to make
- [ ] **Required marks at load.** Contact email, Event name, Event date, Dietary notes, Ticket
      tier and Venue region wear the asterisk. Early-bird deadline, Description, Coupon code and
      Catering headcount do not. Every one of those labels carries the same required-mark
      component, so the rules alone decide the difference
- [ ] Submit without filling anything in: every presence rule whose field is empty answers at
      once. That is five fields: contact email, event name, event date, **dietary notes** and
      venue region. The error summary lists them all
- [ ] **Every summary entry lands.** Click each in turn. Entries naming a Formidable input focus
      that input. The **venue-region** entry focuses the NATIVE input (see the venue checks
      below). The **attendees** info entry focuses the Attendees fieldset, which owns no input
      of its own. Nothing in the error summary is a click that goes nowhere. The form-level gate
      entry is one more kind; the gate rows below raise it and check it
- [ ] **Keyboard vs mouse landing.** Tab to the attendees info entry in the error summary and
      press Enter. The Attendees fieldset takes the focus and shows an accent outline in both
      colour schemes; the form's entry comes with the gate rows. Click the SAME entry with the
      mouse: it scrolls and takes focus with NO outline. Then click stray page background or a
      fieldset's padding: nothing paints an outline
- [ ] `taken@example.com` in Contact email (the tour's step 2): each keystroke asks about a new
      address. "checking…" shows for about 300 ms, then "That email is already registered".
      Clear the box and type it again: the other messages come and go, but NO "checking…"
      appears. This page's memo keeps each address's answer for five minutes (Async rules' memo
      keeps its own for ten seconds), so only a retype after that brings "checking…" back
- [ ] **A click that Submit's own blur displaces still lands.** Put the cursor in a field whose
      message will appear ABOVE the button, and leave it invalid. Click *Submit registration*
      without pressing Tab first. The message appears and the button moves down under a still
      pointer, but the submit still happens: the error summary lists everything
- [ ] **The displaced-click guard's limits.** The guard (Formidable's re-delivery of a click
      the moving button lost) re-delivers one whose press and release drifted up to 6 px. A
      deliberate drag of more than that is left alone. Turning the guard off
      (`ClickRecovery = DisplacedClickRecovery.None`) takes a code change, so that half is
      optional
- [ ] **Dates commit on blur, not per keystroke.** In **Event date**, type the year segment
      SLOWLY (`2`, `0`, `2`, `6`): no message appears while you are mid-year. Tab out: only
      then does the field get a verdict, and a complete date passes cleanly
- [ ] **A pure tab-through shows nothing.** Tab into **Event date** and straight out again
      without typing. No message appears, the box's border does not change, and no "checking…"
      flashes. Leaving a box with no change behind it tells the form nothing, here as everywhere
- [ ] Now type a deliberately garbled year (e.g. `0019`) and tab out: it is REJECTED as not a
      real date (the 1900–2100 range), not silently accepted. Fix it and the message goes
- [ ] Set **Early-bird deadline** AFTER **Event date** and tab out: the cross-field rule objects.
      Move it back on or before the event date and it clears. The rule runs only once both
      dates are real dates
- [ ] The tour's step 3: replace Contact email with a fresh address. Fill **Event name**,
      **Event date**, **Dietary notes** and **Venue region** (five fields with Contact email),
      then submit with coupon `BOGUS`. The coupon's answer always arrives LAST by design, since
      the page posts only after a submit finds nothing to block it. So the server's 400 lands
      inline on Coupon code with the rest of the form already clean
- [ ] The tour's step 4: change to `WELCOME10` and resubmit. The new answer REPLACES the old one,
      with no stale coupon error, and the registration is accepted
- [ ] With everything else valid, clear Dietary notes and press Tab. "Dietary notes are required
      for catering" answers your edit as soon as its check does, with no submit
- [ ] Untick *Include catering*: the field leaves and takes its message with it, under the box
      and in the error summary alike
- [ ] Submit: the form blocks with "information that is not currently displayed is invalid"
      (the tour's step 5). The one rule that can block has nowhere to show, so nothing on screen
      explains it. The accepted submit above cleared what earlier submits had shown, so the
      error summary has no entry for the note either
- [ ] **The gate survives editing.** While the form stays blocked, type into Description and
      press Tab. The box turns green, the sign a check answered for it, yet the form-level entry
      stays in the error summary. No re-check retires the gate; an error reaching the screen
      does, and Description has no rule to put one there. A submit that can show the error, or
      one that passes, decides it again
- [ ] **The gate entry lands too.** Click that form-level entry: the FORM scrolls into view and
      takes focus. From the KEYBOARD it shows the accent outline in both colour schemes; by MOUSE,
      the scroll alone
- [ ] **Disclosure (the tour's step 6).** Re-tick *Include catering*: the note comes back empty
      and stays quiet. Submit, and its message appears inline and in the error summary, and the
      form-level line gives way to it
- [ ] **A shown error stays listed.** Untick once more: the field goes and takes its inline
      message with it, but the error summary keeps the entry. Submit again and it is still listed,
      with the form-level entry beside it, since no error shows under a field. Once a submit has
      shown a field's error, the form keeps listing it until a submit goes through or the form
      resets
- [ ] **Where that entry lands.** Click that entry: the field it names is off the page, so
      focus lands on **Include catering** instead. That is the checkbox whose state is the
      reason the field is gone. Re-tick and fill in a note before moving on
- [ ] `nope@` in Contact email, then *Save draft* (a draft save, as in the tour's step 1). The
      draft checks the draft rules only, and the malformed address is what it names, since the
      note is back in
- [ ] Now clear **Event name** and press Tab. "Event name is required" lands as soon as that
      edit's check answers, because you changed that field. Save the draft again, and the
      status line still never mentions it. A draft save runs the Draft profile, and that
      presence rule is not in it. Type Event name back in
- [ ] *Add attendee*: the row appears with the required mark on Name and none on Email; both
      carry the mark's component, and the rules decide. It stays silent though Name's rule already
      fails: a mark is not a message, and nothing has changed the row yet
- [ ] Type a name into that row and press Tab, then come back, clear it and press Tab again.
      "Attendee name is required" appears inline and in the error summary as soon as that
      edit's check answers, with NO submit. Clicking that entry focuses that row's Name
- [ ] Fill that Name, then add ten more named rows (the tour's step 7 adds blank ones). Past
      ten, the warning that opens "More than 10 attendees needs approval" appears below the
      list. It goes on to say the submission is not blocked
- [ ] With 11 rows the Attendees fieldset is tall. Click the warning's entry in the error
      summary: the page scrolls so the warning message itself lands in view, roughly centred in
      the window. It does not align the tall fieldset's top and leave the message off-screen
      below
- [ ] Contact email is still `nope@` and is the only thing left broken. Put a real address back,
      check catering is still ticked with a note in it and **Venue region** still filled, then
      submit. The warning does NOT block, and the status line confirms the registration was
      accepted
- [ ] *Remove* every row: the warning gives way to the info "You can add attendees now or after
      registering"
- [ ] Scroll the Sessions list to the bottom, set the last session's Seats to `900` and press
      Tab (the tour's step 8). The row objects on the **FIRST Tab**: "Seats must be a whole
      number between 0 and 500" appears as soon as that edit's check answers. No second edit is
      needed to shake it loose
- [ ] Repeat on another row to be sure it is not a one-off, then set THAT row back to `0`. The
      next two rows expect the last session's error alone: left failing, another row's error
      would come first in document order and take the submit's focus
- [ ] Scroll the list back to its top and submit. It is blocked, and the error summary carries
      the same seats message for a row nowhere on screen. **The submit's own focus reaches it
      too**: with no click at all, the list scrolls itself, Virtualize renders the row, and
      focus lands in its Seats box
- [ ] Scroll the list back to its top, then click that entry in the error summary (the tour's
      step 9). The list scrolls itself, Virtualize renders the row, and focus lands in its Seats
      box. Set it back to `0`: that edit's check takes both the inline message and the entry
      away
- [ ] Set **Ticket tier** to the blank *Choose…* (the tour's step 10). Choosing is a change, so
      the foreign select takes the same inline message and error-summary entry as any wrapped
      input, with NO submit. Tab out and the red border joins them: a focused box wears the
      accent border whatever its state, so the red one waits until you leave. Clicking that
      entry moves focus INTO the select
- [ ] Add a word to **Venue region**, which still holds what you typed earlier, and tab out. The
      check that change starts turns it green, with no submit needed first. So, in both colour
      schemes, the native input wears the same green border a Formidable input shows: Formidable's
      `FieldCssClassProvider` serves both kinds of input
- [ ] Clear **Venue region** and press Tab (the tour's step 11). The native `ValidationMessage`
      shows "Venue region is required" with NO submit, since Formidable writes each error into
      the form's `EditContext`. The error summary lists it too. Clicking THAT entry **lands in
      the native input**: the page gives it the field's id, which is all a click in the error
      summary looks for. No error in devtools' console, no lost scroll position
- [ ] While that error stands, inspect the native input: `aria-invalid="true"`, an
      `aria-describedby` naming the message below it, and `aria-required="true"` beside them.
      Fill the region and tab out: `aria-invalid` and `aria-describedby` both disappear, with no
      resubmit. Each is conditional, and the second names the message element, which is not on
      the page while there is no message. `aria-required` stays, because the rules demand the
      value whether the box is full or empty
- [ ] **Message-bearing fields separate from the next field, in both shapes and both colour
      schemes.** With several errors showing, compare two idioms. Event name, both dates,
      Description and Coupon code render a message INSIDE the field box; Contact email and Ticket
      tier render it as the field's SIBLING. In both, the message sits tight under its input, with
      a clear gap, never flush, before the NEXT label. Both shapes read alike
- [ ] Whole page, in both colour schemes: fieldset legends, the Sessions list's border and
      background, and the foreign select's chrome read correctly. So do the focus outlines on the
      Attendees fieldset and the form, and the summary's severity colours. Nothing is a light
      island in dark mode. Date inputs' calendar icons are legible in dark; give Firefox one
      glance too, since the Chromium-only E2E suite never sees its picker chrome

---

## Docs

### Recipes

A reading check, not a browser check: do it from the repo.

- [ ] `README.md`'s doc table carries the row *"I want to…" answered with code, then what
      explains it, plus a sample where one exists*, linking to `docs/recipes.md`. Follow the
      link and it resolves
- [ ] `docs/recipes.md` opens with an intro pointing to Troubleshooting, then twenty-one
      unnumbered `### I want…` headings. `docs/troubleshooting.md` opens with a scope
      statement, then a symptom table of thirty-six rows
- [ ] Spot-check the recipe titled **"I want every summary entry to land somewhere"** against
      what you just saw on /workout, /vanilla, /collections and /disclosure. The ids and the
      containers match the pages
- [ ] Spot-check the recipe titled **"I want a modal dialog to announce a blocked submit"**
      against what you just watched on /dialog-submit. The two rules the page's own toggles
      break are things you saw go wrong when you flipped them. The summary parameters it names
      are the ones that page sets
- [ ] Spot-check the recipe titled
      **"I want to validate while typing, on blur, or only at submit"**. Its table of `UpdateOn`
      against what the live channel selects matches what /async, /field-state and /profiles
      (Summary with `WaitForSubmit`) actually do. And `docs/troubleshooting.md`'s row
      *"A date input reports impossible years while it is being typed"* matches the workout's
      date behaviour you just walked
- [ ] Every recipe answers with code first, then links to what explains it in full, with a
      sample page where one demonstrates it. No recipe contradicts the pages it names

### Quickstart and testing

Also reading checks, done from the repo.

- [ ] `docs/quickstart.md` builds a form out of three files: one page holding the model, the
      validator and the markup together, one `_Imports.razor` line, and two `Program.cs`
      registrations. Its Recap says when to move the model and the validator out
- [ ] The README's *5-minute quickstart* teaches the same three files, in the same order, and
      links on to `docs/quickstart.md` and the sample's Quickstart page (the app's home page)
- [ ] `docs/testing.md`'s **Testing your forms** section comes before the suite walk. It covers
      three things in order: validating a model with no renderer, rendering the form under bUnit
      with doubles for the focus and DOM-sync services, and waiting for a verdict that lands a
      render later

---

## Hosted demo

Build the Pages artifact from the repo root (`pwsh build-pages.ps1`, which also starts a local
static file server), then walk the rows below against <http://localhost:8080/formidable/>. No
API needs to be running: the artifact answers its own requests.

- [ ] `/server` and `/workout` behave exactly as they do against the real API. Empty submits
      land the same inline messages, and `/workout`'s coupon check rejects `BOGUS` and accepts
      `WELCOME10`. Every other page is unchanged except `/mudblazor`, which shows a note (the
      last two rows)
- [ ] Both pages show the demo note under their intro. On `/server` it reads, verbatim, "This
      hosted demo has no server behind it: an in-browser handler answers with the same
      validators and the same response shapes the real API would send". `/workout`'s ends
      "…coupon rejection included"
- [ ] Both pages' *Try it* opens with the demo line above the steps ("No server to start on this
      hosted demo…"). The local line telling you to start the API is gone, not just hidden, and
      the steps still number from 1
- [ ] Every link from one sample page to another stays inside the demo. That covers the Full
      workout tour's "More on" links and the links in a page's intro or *How it works*, such as
      Draft vs Submit's link to Custom profiles. Each opens the page it names with the address
      still under `/formidable/`, never the site's root
- [ ] Open `/mudblazor` from the sidebar, then again by typing its address. Both times, the page
      under the "Fitting MudBlazor" heading is a note, not a form. It says Formidable supports
      MudBlazor and that the page runs when you run the sample locally, and gives the reason
      this demo leaves it out
- [ ] The note's source link opens `MudBlazorFitting.razor` on GitHub, and its README link opens
      the README's "Run the sample locally" section
