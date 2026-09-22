using Formidable.Blazor;
using Formidable.Sample.Shared;
using Microsoft.AspNetCore.Components.Forms;

namespace Formidable.Sample.Pages;

public partial class VanillaInterop : IDisposable
{
    private readonly GadgetOrder _order = new();
    private string _status = string.Empty;

    private FormidableForm<GadgetOrder>? _form;
    private IFormidableEngine? _subscribedEngine;

    private FieldIdentifier NicknameField => new(_order, nameof(GadgetOrder.Nickname));

    // The id the focus service looks for. A Formidable input renders it from the field context;
    // the native input opposite has to be handed the same value to be reachable from the summary.
    private string NicknameId => FormidableFieldId.For(NicknameField);

    // The id of the element listing this field's messages, handed to the native ValidationMessage
    // so a wrapped input's contract holds for a hand-rolled one.
    private string NicknameMessagesId => FormidableFieldId.MessagesFor(NicknameField);

    // Naming that id only describes something while an element carries it, and a native
    // ValidationMessage renders one <div> per message and nothing whatever when the field is
    // clean. So the attribute stands only while there is a message to point at, and the question
    // to ask is the one that component answers for itself: its messages come from the EditContext.
    // A wrapped input is never asked it, because FormidableFieldMessage leaves its list on the
    // page, empty.
    private string? NicknameAriaDescribedBy =>
        _form?.Engine?.EditContext.GetValidationMessages(NicknameField).Any() == true
            ? NicknameMessagesId
            : null;

    // Same story for aria-invalid: the field context a wrapped input reads it from does not exist
    // here, so the page reads the state off the engine. Null renders no attribute at all. A native
    // InputText answers that attribute for itself from the field's messages, and it answers with
    // the same "true" or nothing this read does, so the ordering cannot put a third thing on the
    // input.
    private string? NicknameAriaInvalid =>
        _form?.Engine?.GetFieldState(NicknameField).HasErrors == true ? "true" : null;

    private void HandleValid() => _status = "Submitted — native and Formidable inputs agreed.";

    // The engine notifies the components bound to it, not the page, so without this subscription
    // the aria-invalid above would lag a keystroke behind the message beside it. The kit's own
    // components subscribe for the same reason; this page's model never swaps, so the first
    // engine it sees is the only one.
    protected override void OnAfterRender(bool firstRender)
    {
        if (_subscribedEngine is null && _form?.Engine is { } engine)
        {
            _subscribedEngine = engine;
            engine.StateChanged += OnEngineStateChanged;
        }
    }

    private void OnEngineStateChanged(object? sender, FormidableStateChangedEventArgs e) =>
        _ = InvokeAsync(StateHasChanged);

    public void Dispose()
    {
        if (_subscribedEngine is not null)
        {
            _subscribedEngine.StateChanged -= OnEngineStateChanged;
        }
    }
}
