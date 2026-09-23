namespace PureEngine.Core;

/// <summary>Contract for click input implemented by the Button itself.</summary>
public interface IUiButtonHandler
{
    void OnClick(UiClickContext context);
}

/// <summary>Transient information for a click invocation. Holds the runtime Scene and the Button's SceneObject.</summary>
public readonly record struct UiClickContext(Scene Scene, SceneObject ButtonObject);
