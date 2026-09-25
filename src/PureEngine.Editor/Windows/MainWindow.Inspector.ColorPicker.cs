using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Color = Avalonia.Media.Color;

namespace PureEngine.Editor;

public partial class MainWindow
{
    /// <summary>
    /// Turns a color preview swatch into the single entry point for editing.
    /// Spectrum, Palette, and Sliders (with hex and alpha) open on click; exact entry lives in the picker.
    /// </summary>
    private void AttachColorPicker(Border preview, Panel editorRoot, Func<object?> getter, Action<PureEngine.Core.Color> setColor, string automationName)
    {
        preview.Cursor = new Cursor(StandardCursorType.Hand);
        ToolTip.SetTip(preview, "Current color — Select to edit (Spectrum, Palette, Sliders)");
        var gate = new ColorPickerGate();
        var view = new ColorView
        {
            IsAlphaEnabled = true,
            IsAlphaVisible = true,
            IsColorSpectrumVisible = true,
            IsColorPaletteVisible = true,
            IsColorComponentsVisible = true,
            IsHexInputVisible = true,
            MinWidth = 300,
        };
        view.SetValue(AutomationProperties.NameProperty, $"{automationName}.Picker");
        view.PropertyChanged += (_, args) =>
        {
            if (args.Property != ColorView.ColorProperty || gate.Syncing || IsPlaying)
                return;
            setColor(ToEngineColor(view.Color));
            RefreshColorBoxes(editorRoot, getter());
        };
        var flyout = new Flyout { Content = view, Placement = PlacementMode.Bottom };
        FlyoutBase.SetAttachedFlyout(preview, flyout);
        preview.PointerPressed += (_, e) =>
        {
            if (IsPlaying || !e.GetCurrentPoint(preview).Properties.IsLeftButtonPressed)
                return;
            if (getter() is not PureEngine.Core.Color current)
                return;
            // Sync the view without writing back: the programmatic set echoes through PropertyChanged.
            gate.Syncing = true;
            try
            {
                view.Color = ToAvaloniaColor(current);
            }
            finally
            {
                gate.Syncing = false;
            }
            flyout.ShowAt(preview);
            e.Handled = true;
        };
    }

    /// <summary>Reentrancy gate for programmatic picker sync. Stored in the view Tag.</summary>
    private sealed class ColorPickerGate
    {
        public bool Syncing;
    }

    private static Color ToAvaloniaColor(PureEngine.Core.Color color) =>
        Color.FromArgb(ToPreviewByte(color.A), ToPreviewByte(color.R), ToPreviewByte(color.G), ToPreviewByte(color.B));

    private static PureEngine.Core.Color ToEngineColor(Color color) =>
        new(color.R / 255f, color.G / 255f, color.B / 255f, color.A / 255f);
}
