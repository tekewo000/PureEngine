namespace PureEngine.Editor;

public partial class MainWindow
{
    /// <summary>Rebuilds the preview language list from the edit-scene asset snapshot. Keeps the selection when still available.</summary>
    internal void RefreshPreviewLanguages()
    {
        if (ViewModel.IsDisposed) return;
        ViewModel.Localization.Refresh(Documents.Current.Current.DataAssets);
    }
}
