namespace PureEngine.Core.Components;

/// <summary>A plain component that receives click hit-testing within the UiElement area. It uses the Image on the same object for its appearance.</summary>
/// <remarks>
/// Only <see cref="Button.Interactable"/> is persisted. It holds no transient pressed, hover, or focus state.
/// Placed in the Components namespace to avoid clashes with Avalonia.Controls.Button. The typeId is core.button.
/// </remarks>
public sealed class Button : IUiButtonHandler
{
    [Inspector]
    public bool Interactable { get; set; } = true;

    /// <summary>Click handler registered on the runtime instance. It is not carried over by persistence or Clone.</summary>
    public event Action<UiClickContext>? Clicked;

    public void OnClick(UiClickContext context) => Clicked?.Invoke(context);
}
