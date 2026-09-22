# Hosting models

Where a form gets built, how often, and when the browser joins in depend on the app's template and
the page's render mode.

## Which template am I in?

| Template | Projects | Page file lives in | Page namespace |
|---|---|---|---|
| `dotnet new blazorwasm` — standalone WebAssembly | one | `Pages/` | `YourApp.Pages` |
| `dotnet new blazor` — Blazor Web App | one | `Components/Pages/` | `YourApp.Components.Pages` |
| `dotnet new blazor -int Auto` or `-int WebAssembly` | two: a server project and a `.Client` project | the `.Client` project | `YourApp.Client.Pages` |

Each project has a `Program.cs` of its own, and each one is a container a form resolves from. The
namespace column is the one that shows up in a registration: a model and validator nested in the
page class are named through the page's own namespace.

## Does the page need a render mode?

On a Blazor Web App, yes. Every page in a standalone WebAssembly app is interactive from the
moment it loads. A Blazor Web App server-renders its pages statically until one says otherwise.

So the page holding the form needs a render mode of its own, at the top of the file. Any of
`@rendermode InteractiveServer`, `@rendermode InteractiveWebAssembly` and
`@rendermode InteractiveAuto` answers that requirement. Leave the line off and `FormidableForm`
refuses to render, asking for a render mode in its own message. A form on such a page could be
filled in, but its submit would never reach the validation pipeline.

## The server builds the form too

A standalone WebAssembly app has one `Program.cs`, and it is the container every form resolves
from. A Web App created with `dotnet new blazor -int Auto` or `-int WebAssembly` has a server
project and a `.Client` project, each with a `Program.cs` of its own. Two routes take the form
through the server's container.

Prerendering builds it there before any runtime has started, and it is on unless a page turns it
off. A render mode that runs on the server's circuit builds it there too. `InteractiveServer` does
that on every visit, and `InteractiveAuto` does it on a first visit while the WebAssembly runtime
downloads.

So the server project needs `AddFormidableBlazor()` and the same validator registrations the client
project makes. Register on the client alone and the server has no validator to resolve. Building
the form then throws `No IModelValidator<Contact> is registered`, naming a call the client project
already makes.

One page shape escapes both routes, and only one:

```razor
@rendermode @(new InteractiveWebAssemblyRenderMode(prerender: false))
```

It has no prerender pass and no circuit to fall back to, so the browser builds the form alone. A
Web App created with `-int Server` has one project and one container, so the question never comes
up.

## The form is built twice per visit

Prerendering renders the page once on the server, and again when interactivity starts. A single
visit therefore constructs the engine twice and resolves the validator twice. With
`TrackFormValidity` on, the probe the engine runs at construction runs on the prerendered pass too.
A validator with a slow async rule pays for that on a render that is about to be replaced.

None of this is a fault to fix, but a team counting validator constructions should know why the
number is two. Writing the render mode the long way is what turns prerendering off, and the count
goes to one:

```razor
@rendermode @(new InteractiveServerRenderMode(prerender: false))
```

## What if a submit lands during prerendering?

Prerendering is on by default under all three interactive render modes, so the page is rendered on
the server and sent as HTML a moment before it becomes interactive. A submit that lands inside
that window posts natively. The server answers it with the platform's own 400: *"The POST request does not
specify which form is being submitted."*

The render-mode guard cannot help there, because interactivity genuinely is coming. The fix that
message proposes, a `FormName` on `EditForm`, is not a parameter `FormidableForm` carries. The
ordinary answer is a submit button that stays disabled until the page reports itself interactive.

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

Formidable ships nothing for this, deliberately, and the sample writes the few lines in the open.

**Read more:** [Culture at WebAssembly boot](component-kit.md#culture-at-webassembly-boot)
