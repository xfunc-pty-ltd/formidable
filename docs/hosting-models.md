# Hosting models

Formidable runs in full on a standalone WebAssembly app and on every interactive Blazor Web App
render mode. What differs is the settings each one needs, and what happens before the browser
takes over.

## Which hosting models are supported?

**Tier 1 — a standalone WebAssembly app.** Formidable's sample app is one, and the browser suite
drives it end to end. A manual checklist walks what a headless browser cannot judge.

**Tier 2 — a Blazor Web App page under `InteractiveServer`, `InteractiveWebAssembly` or
`InteractiveAuto`.** All three are verified working with exactly the settings below.

The browser suite reaches a Web App on a server circuit, with a prerender window and without
one. It does not reach `InteractiveWebAssembly` or `InteractiveAuto`, and neither does the
checklist. Both of those are driven by hand. An issue found on a Tier 2 hosting model is fixed
in a later version.

## Which template am I in?

| Template | Projects | Page file lives in | Page namespace |
|---|---|---|---|
| `dotnet new blazorwasm` — standalone WebAssembly | one | `Pages/` | `YourApp.Pages` |
| `dotnet new blazor` — Blazor Web App | one | `Components/Pages/` | `YourApp.Components.Pages` |
| `dotnet new blazor -int Auto` or `-int WebAssembly` | two: a server project and a `.Client` project | the `.Client` project | `YourApp.Client.Pages` |

Each project has a `Program.cs` of its own, and each one is a container a form resolves from. The
namespace column is the one that shows up in a registration: a model and validator nested in the
page class are named through the page's own namespace.

## Use exactly these settings

Every shape below needs `AddFormidableBlazor()` and an `IValidator<T>` for the form's model.
A render-mode line goes at the top of the page file. Which `Program.cs` takes the two
registrations changes with the shape.

| Page | Render-mode line | Register in | Prerender window |
|---|---|---|---|
| A standalone WebAssembly app | none: every page is interactive already | the app's one `Program.cs` | no |
| Blazor Web App, on the server's circuit | `@rendermode InteractiveServer` | every `Program.cs` the app has | yes |
| Blazor Web App, on the server's circuit, no prerendering | `@rendermode @(new InteractiveServerRenderMode(prerender: false))` | every `Program.cs` the app has | no |
| Blazor Web App, on the WebAssembly runtime | `@rendermode InteractiveWebAssembly` | every `Program.cs` the app has | yes |
| Blazor Web App, on the WebAssembly runtime, no prerendering | `@rendermode @(new InteractiveWebAssemblyRenderMode(prerender: false))` | the `.Client` project alone | no |
| Blazor Web App, on whichever runtime is ready | `@rendermode InteractiveAuto` | every `Program.cs` the app has | yes |
| Blazor Web App, on whichever runtime is ready, no prerendering | `@rendermode @(new InteractiveAutoRenderMode(prerender: false))` | every `Program.cs` the app has | no |

Registering in every project is never wrong, because a form resolves from whichever container
builds it. One shape needs the client project alone: `InteractiveWebAssembly` with prerendering
off has no prerender pass and no circuit behind it. `InteractiveAuto` with prerendering off
reads like the same escape and is not, because its first visit runs on the server's circuit.

The prerender window is the gap between the server's HTML and a page that is listening.
`FormidableForm` renders `inert` on its own `<form>` for that window, so a visitor cannot
operate it there. On a page whose whole job is a form, prefer a row with no window. What the
window costs there is a form on screen that nobody can use yet.

`InteractiveServer` runs the component on the server, and `InteractiveAuto` does the same on a
first visit. Every event the form answers there makes a network round trip. Formidable's engine
is a field of the form component, so on a circuit it sits in server memory for every visitor
holding the page open. A WebAssembly runtime runs the component in the browser, where neither
of those applies.

## Does the page need a render mode?

On a Blazor Web App, yes. Every page in a standalone WebAssembly app is interactive from the
moment it loads. A Blazor Web App server-renders its pages statically until one says otherwise.
The page holding the form takes any of `@rendermode InteractiveServer`,
`@rendermode InteractiveWebAssembly` or `@rendermode InteractiveAuto`.

A statically rendered page with no render mode gets a paragraph where the form would have been,
asking for a render mode in the component's own words. The rest of the page paints as it always
does. A form on such a page could be filled in, but its submit would never reach the validation
pipeline.

## The server builds the form too

Two routes take a form through a Blazor Web App's server container. Prerendering builds it there
before any runtime has started, and it is on unless a page turns it off. A render mode that runs
on the server's circuit builds it there too. `InteractiveServer` does that on every visit.
`InteractiveAuto` does it on a first visit while the WebAssembly runtime downloads.

So the server project needs `AddFormidableBlazor()` and the same validator registrations the
client project makes. Register on the client alone and the server has no validator to resolve.
Building the form then throws `No IModelValidator<Contact> is registered`, with the form's own
model type where `Contact` is. The message names a call the client project already makes.

One page shape escapes both routes, and only one: `InteractiveWebAssembly` with prerendering
off. A Web App created with `-int Server` has one project and one container, so the question
never comes up.

## How often is the form built?

Prerendering renders the page once on the server, and again when interactivity starts. A single
visit therefore constructs the engine twice and resolves the validator twice. Where the two
builds land follows the render mode: both on the server under `InteractiveServer`, and under
`InteractiveAuto` on a first visit; one on each side under `InteractiveWebAssembly`.

With `TrackFormValidity` on, the probe the engine runs at construction runs on the prerendered
pass too. A validator with a slow async rule pays for that on a render that is about to be
replaced.

None of this is a fault to fix, but a team counting validator constructions should know why the
number is two. Turning prerendering off takes it to one, built wherever the page's render mode
runs.

## What happens inside the prerender window?

Prerendering is on by default under all three interactive render modes, so the page arrives as
HTML a moment before it becomes interactive. The form is on screen in that window and nothing is
listening to it yet.

A live form there costs the visitor twice. A submit posts natively, and the server answers it with
the platform's own 400: *"The POST request does not specify which form is being submitted."*
Typing is accepted and then discarded, because the interactive render fills every input from the
model, and nothing typed in the window is in it.

`FormidableForm` puts the form out of reach for that window. It renders `inert` on its own
`<form>` until interactivity arrives, so a visitor cannot click the form, type into it, or reach
it with Tab. The render that brings interactivity carries none, so the form writes the attribute
for the window and nothing else.

`inert` greys nothing out. The library ships no styling, so a page that wants the window to look
as unavailable as it is writes its own rule:

```css
form[inert] { opacity: .6; }
```

Where the form is the whole point of the page, take the window away instead. A render mode with
prerendering off gives the form no prerender pass at all, at the cost of painting a moment later.

> [!NOTE]
> `inert` refuses a visitor, not a request. A post that reaches the server without coming from the
> rendered form still gets the 400. The fix that message proposes, a `FormName` on `EditForm`, is
> not a parameter `FormidableForm` carries: naming the form routes the post into the submit
> pipeline on the static pass, where the focus move cannot reach the browser, so a 500 naming
> the library replaces a 400 naming the fix.

## Nothing reaches the browser until interactivity does

`OnAfterRenderAsync` does not run on the prerendered pass. A JavaScript call issued earlier throws,
in the platform's own words: *"JavaScript interop calls cannot be issued at this time. This is
because the component is being statically rendered."*

The library's browser-side work waits accordingly. The displaced-click guard installs on the first
interactive render, and a summary lists issues in validator order until the first field-order
resolve lands. Interop of your own belongs in `OnAfterRenderAsync` for the same reason.

## Culture at WebAssembly boot

A WebAssembly app fixes its culture before `RunAsync()`, and downloads its satellite resource
assemblies for that one culture. An app offering a language choice has to apply the stored choice
there, rather than from a page afterwards. A Blazor Server host takes its culture from the request
and needs no boot-time step at all.

Formidable ships nothing for this, deliberately. The sample app that ships with the library
writes the few lines in the open.

**Read more:** [Culture at WebAssembly boot](component-kit.md#culture-at-webassembly-boot)
