namespace Formidable.Blazor;

/// <summary>What <see cref="StateChangedRelay"/> needs of the engine: a place to attach, and the raw raise of <see cref="IFormidableEngine.StateChanged"/> the relay makes from its render.</summary>
internal interface IStateChangedPublisher
{
    /// <summary>Routes the engine's publishes through <paramref name="relay"/> from now on.</summary>
    /// <param name="relay">The relay the root rendered for this engine.</param>
    void AttachRelay(StateChangedRelay relay);

    /// <summary>Stops routing through <paramref name="relay"/>, if it is the one attached.</summary>
    /// <param name="relay">The relay being disposed.</param>
    void DetachRelay(StateChangedRelay relay);

    /// <summary>Invokes the <see cref="IFormidableEngine.StateChanged"/> handlers with <paramref name="args"/>; nothing once the engine is disposed.</summary>
    /// <param name="args">The publish's arguments.</param>
    void RaiseStateChanged(FormidableStateChangedEventArgs args);
}
